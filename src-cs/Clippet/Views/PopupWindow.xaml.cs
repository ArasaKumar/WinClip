using System.Runtime.InteropServices;
using System.Text;
using Clippet.Models;
using Clippet.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Clippet.Views;

public sealed partial class PopupWindow : Window
{
    private readonly Storage _storage;
    private readonly SettingsDto _settings;
    private readonly ThemeService _theme;
    private readonly ClipboardMonitor _monitor = new();
    private readonly ClipboardWriter _writer = new();
    private readonly ClipboardGate _gate = new();

    private readonly List<ClipItem> _history;
    private ulong _nextId;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private WindowSubclass _subclass = null!;
    private TrayService _tray = null!;
    private readonly Dictionary<ulong, BitmapImage> _thumbCache = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchTimer;

    private IntPtr _prevFg;
    private string _query = "";

    private const int HotkeyId = 1;

    // Smallest size (in DIPs) that still leaves room for the title bar, search box,
    // at least a row of history, and the full footer. Below this the footer clips.
    private const int MinPopupW = 320;
    private const int MinPopupH = 280;

    public PopupWindow(Storage storage, SettingsDto settings, ThemeService theme)
    {
        _storage = storage;
        _settings = settings;
        _theme = theme;

        var loaded = _storage.LoadHistory();
        _history = loaded.Items;
        _nextId = loaded.NextId;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        ConfigureWindow();

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(90);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => RebuildRows();

        // Ctrl+P toggles pin from anywhere in the window. Hide the auto-generated accelerator
        // tooltip ("Ctrl+P") so it doesn't float over the search box.
        var pinAccel = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.P };
        pinAccel.Invoked += (_, e) => { e.Handled = true; TogglePinSelected(); };
        RootGrid.KeyboardAccelerators.Add(pinAccel);
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        Activated += OnActivated;

        InstallNativeHooks();
        ApplyTheme();
        RebuildRows();
        UpdateTrayTooltip();

        _appWindow.Hide();
    }

    /// <summary>Effective scale (1.0 == 96 DPI) of a specific monitor, falling back to the
    /// window's own DPI. Used so the popup is sized for the screen it will appear on.</summary>
    private double MonitorScale(IntPtr monitor)
    {
        if (Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        uint wdpi = Native.GetDpiForWindow(_hwnd);
        return wdpi > 0 ? wdpi / 96.0 : 1.0;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = true;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(true, false);

            // Stop the user from dragging the window so small the footer falls off.
            double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
            p.PreferredMinimumWidth = (int)Math.Round(MinPopupW * scale);
            p.PreferredMinimumHeight = (int)Math.Round(MinPopupH * scale);
        }
        _appWindow.IsShownInSwitchers = false;

        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
    }

    private void InstallNativeHooks()
    {
        _subclass = new WindowSubclass(_hwnd, HandleMessage);
        Native.RegisterHotKey(_hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_V);
        Native.AddClipboardFormatListener(_hwnd);

        IntPtr hIcon = LoadAppIcon();
        _tray = new TrayService(_hwnd, hIcon, "Clippet");
    }

    private static IntPtr LoadAppIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "clippet.ico");
        if (File.Exists(icoPath))
        {
            IntPtr h = Native.LoadImageW(IntPtr.Zero, icoPath, Native.IMAGE_ICON, 0, 0,
                Native.LR_LOADFROMFILE | Native.LR_DEFAULTSIZE);
            if (h != IntPtr.Zero) return h;
        }
        return Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION);
    }

    // ------------------- native message routing -------------------
    private bool HandleMessage(uint msg, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case Native.WM_HOTKEY:
                ShowAtCursor();
                return true;
            case Native.WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                return true;
            case Native.WM_TRAYCALLBACK:
                uint ev = (uint)(lParam & 0xFFFF);
                if (ev == Native.WM_LBUTTONUP)
                    TogglePopup();
                else if (ev is Native.WM_RBUTTONUP or Native.WM_CONTEXTMENU)
                    HandleTrayCommand(_tray.ShowContextMenu());
                return true;
        }
        return false;
    }

    private void HandleTrayCommand(TrayService.MenuCommand cmd)
    {
        switch (cmd)
        {
            case TrayService.MenuCommand.Open: ShowAtCursor(); break;
            case TrayService.MenuCommand.Clear: _ = ConfirmClearAsync(); break;
            case TrayService.MenuCommand.Settings: _ = ShowSettingsAsync(); break;
            case TrayService.MenuCommand.Exit: QuitApp(); break;
        }
    }

    // ------------------- capture -------------------
    private async void OnClipboardUpdate()
    {
        if (_gate.ConsumeIfArmed()) return;
        var raw = _monitor.ReadClipboard(_hwnd);
        if (raw == null) return;

        PreparedCapture? prepared;
        try { prepared = await Task.Run(() => _monitor.PrepareAsync(raw)); }
        catch { return; }
        if (prepared == null) return;

        AddPrepared(prepared);
    }

    private void AddPrepared(PreparedCapture prepared)
    {
        if (_history.Count > 0)
        {
            var last = _history[0];
            if (last.Kind == prepared.Kind && last.ContentHash == prepared.ContentHash)
                return; // consecutive duplicate
        }

        ulong id = _nextId++;
        var item = new ClipItem
        {
            Id = id,
            Kind = prepared.Kind,
            Raw = prepared.Raw,
            Preview = prepared.Preview,
            Timestamp = Util.NowUnix(),
            Lang = prepared.Lang,
            ContentHash = prepared.ContentHash,
        };
        if (prepared.Kind == ItemType.Image)
        {
            item.MediaFile = _storage.MediaFileName(id);
            item.ThumbFile = _storage.ThumbFileName(id);
            item.MediaW = prepared.W;
            item.MediaH = prepared.H;
            _ = _storage.SaveImageAsync(id, prepared.Png!, prepared.Thumb!);
        }

        _history.Insert(0, item);
        Prune();
        _storage.RequestSave(_history);
        RebuildRows();
        UpdateTrayTooltip();
    }

    private void Prune()
    {
        while (_history.Count > Storage.HistoryCap)
        {
            int idx = -1;
            for (int i = _history.Count - 1; i >= 0; i--)
                if (!_history[i].Pinned) { idx = i; break; }
            if (idx < 0) break; // all pinned
            var removed = _history[idx];
            _history.RemoveAt(idx);
            if (removed.IsImage) { _storage.DeleteMediaFor(removed); _thumbCache.Remove(removed.Id); }
        }
    }

    // ------------------- list / rows -------------------
    private void RebuildRows()
    {
        var rows = SearchEngine.UpdateFilter(_query, _history);
        var pal = _theme.Palette;
        var list = new List<RowVM>(rows.Count);
        foreach (var r in rows)
        {
            var it = _history[r.HistIndex];
            list.Add(new RowVM
            {
                Item = it,
                HistIndex = r.HistIndex,
                Indices = r.Indices,
                TagBrush = B(pal.TagColor(it.Kind)),
                TextBrush = B(pal.Text),
                SubtextBrush = B(pal.Subtext),
                TimeText = Util.RelativeTime(it.Timestamp),
                PinBrush = B(it.Pinned ? pal.Accent : pal.PinDim),
            });
        }
        HistoryList.ItemsSource = list;
        if (list.Count > 0) HistoryList.SelectedIndex = 0;
    }

    private void HistoryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer?.ContentTemplateRoot is not FrameworkElement root) return;
        if (args.Item is not RowVM vm) return;

        if (root.FindName("PreviewText") is TextBlock tb)
            ApplyHighlight(tb, vm);

        if (vm.IsImage && vm.Thumb == null)
            _ = LoadThumbAsync(vm);
    }

    private static void ApplyHighlight(TextBlock tb, RowVM vm)
    {
        if (vm.Indices.Count == 0) { tb.Text = vm.Preview; return; }

        tb.Inlines.Clear();
        var set = new HashSet<int>(vm.Indices);
        var sb = new StringBuilder();
        bool curBold = false;
        void Flush()
        {
            if (sb.Length == 0) return;
            tb.Inlines.Add(new Run { Text = sb.ToString(), FontWeight = curBold ? FontWeights.Bold : FontWeights.Normal });
            sb.Clear();
        }
        for (int i = 0; i < vm.Preview.Length; i++)
        {
            bool b = set.Contains(i);
            if (b != curBold) { Flush(); curBold = b; }
            sb.Append(vm.Preview[i]);
        }
        Flush();
    }

    private async Task LoadThumbAsync(RowVM vm)
    {
        if (_thumbCache.TryGetValue(vm.Item.Id, out var cached)) { vm.Thumb = cached; return; }
        try
        {
            string p = _storage.ThumbPath(vm.Item.Id);
            if (!File.Exists(p)) return;
            var file = await StorageFile.GetFileFromPathAsync(p);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            _thumbCache[vm.Item.Id] = bmp;
            vm.Thumb = bmp;
        }
        catch { /* missing/corrupt thumbnail: leave placeholder */ }
    }

    // ------------------- interactions -------------------
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down: MoveSelection(1); e.Handled = true; break;
            case VirtualKey.Up: MoveSelection(-1); e.Handled = true; break;
            case VirtualKey.Enter: _ = PasteSelectedAsync(); e.Handled = true; break;
            case VirtualKey.Escape: HideWindow(); e.Handled = true; break;
            case VirtualKey.Tab: HistoryList.Focus(FocusState.Programmatic); e.Handled = true; break;
        }
    }

    private void HistoryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter: _ = PasteSelectedAsync(); e.Handled = true; break;
            case VirtualKey.Escape: HideWindow(); e.Handled = true; break;
            case VirtualKey.Tab: SearchBox.Focus(FocusState.Programmatic); e.Handled = true; break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (HistoryList.ItemsSource is not List<RowVM> list || list.Count == 0) return;
        int i = HistoryList.SelectedIndex;
        i = i < 0 ? 0 : Math.Clamp(i + delta, 0, list.Count - 1);
        HistoryList.SelectedIndex = i;
        HistoryList.ScrollIntoView(list[i]);
    }

    private RowVM? SelectedRow()
    {
        if (HistoryList.SelectedItem is RowVM vm) return vm;
        if (HistoryList.ItemsSource is List<RowVM> { Count: > 0 } list) return list[0];
        return null;
    }

    private void HistoryList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => _ = PasteSelectedAsync();

    private void HistoryList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not RowVM vm) return;
        HistoryList.SelectedItem = vm;

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItem("Paste", () => _ = PasteSelectedAsync()));
        flyout.Items.Add(MenuItem(vm.Item.Pinned ? "Unpin" : "Pin", () => TogglePin(vm.Item)));
        flyout.Items.Add(MenuItem("Copy to clipboard", () => _ = CopyToClipboardAsync(vm.Item)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MenuItem("Delete this item", () => DeleteItem(vm.Item)));
        flyout.ShowAt(HistoryList, e.GetPosition(HistoryList));
        e.Handled = true;
    }

    private static MenuFlyoutItem MenuItem(string text, Action action)
    {
        var mi = new MenuFlyoutItem { Text = text };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RowVM vm) TogglePin(vm.Item);
    }

    private void TogglePinSelected()
    {
        if (SelectedRow() is { } row) TogglePin(row.Item);
    }

    private void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        _storage.RequestSave(_history);
        RebuildRows();
        UpdateTrayTooltip();
    }

    private void DeleteItem(ClipItem item)
    {
        _history.Remove(item);
        if (item.IsImage) _storage.DeleteMediaFor(item);
        _thumbCache.Remove(item.Id);
        _storage.RequestSave(_history);
        RebuildRows();
        UpdateTrayTooltip();
    }

    private async Task PasteSelectedAsync()
    {
        if (SelectedRow() is not { } row) return;
        var item = _history[row.HistIndex];
        await _writer.PublishAsync(item, _gate, _hwnd, _storage);
        HideWindow();
        ClipboardWriter.PasteInto(_prevFg);
    }

    private async Task CopyToClipboardAsync(ClipItem item)
    {
        await _writer.PublishAsync(item, _gate, _hwnd, _storage);
    }

    // ------------------- show / hide -------------------
    private void ShowAtCursor()
    {
        _prevFg = Native.GetForegroundWindow();
        Native.GetCursorPos(out var pt);
        IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);

        // Scale to the monitor the popup will appear on (the cursor's), not the monitor the
        // window currently sits on — otherwise it's mis-sized on mixed-DPI multi-monitor setups.
        double scale = MonitorScale(mon);
        // Clamp up to the minimum so a previously-persisted tiny size can't clip the footer.
        int w = (int)Math.Round(Math.Max(_settings.PopupW ?? 400, MinPopupW) * scale);
        int h = (int)Math.Round(Math.Max(_settings.PopupH ?? 500, MinPopupH) * scale);

        // Never exceed the monitor work area, or the footer would fall off-screen.
        int workW = mi.rcWork.Right - mi.rcWork.Left;
        int workH = mi.rcWork.Bottom - mi.rcWork.Top;
        w = Math.Min(w, workW);
        h = Math.Min(h, workH);

        int x = pt.X, y = pt.Y;
        if (x + w > mi.rcWork.Right) x = mi.rcWork.Right - w;
        if (x < mi.rcWork.Left) x = mi.rcWork.Left;
        if (y + h > mi.rcWork.Bottom) y = mi.rcWork.Bottom - h;
        if (y < mi.rcWork.Top) y = mi.rcWork.Top;

        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        _appWindow.Show();
        Native.SetForegroundWindow(_hwnd);

        SearchBox.Text = "";
        _query = "";
        RebuildRows();
        SearchBox.Focus(FocusState.Programmatic);
        if (HistoryList.ItemsSource is List<RowVM> { Count: > 0 }) HistoryList.SelectedIndex = 0;

        if (!_settings.AutostartPrompted) _ = MaybePromptAutostartAsync();
    }

    /// <summary>Public entry used by a second launch (redirected via the single-instance signal).</summary>
    public void ShowFromHotkey() => ShowAtCursor();

    private void HideWindow()
    {
        PersistSize();
        _appWindow.Hide();
    }

    private void TogglePopup()
    {
        if (_appWindow.IsVisible) HideWindow();
        else ShowAtCursor();
    }

    private void PersistSize()
    {
        try
        {
            double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
            int w = Math.Max((int)Math.Round(_appWindow.Size.Width / scale), MinPopupW);
            int h = Math.Max((int)Math.Round(_appWindow.Size.Height / scale), MinPopupH);
            if (w > 0 && h > 0 && (w != (_settings.PopupW ?? 400) || h != (_settings.PopupH ?? 500)))
            {
                _settings.PopupW = w;
                _settings.PopupH = h;
                _storage.SaveSettings(_settings);
            }
        }
        catch { /* ignore */ }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _hwnd) _prevFg = fg;
            // Deliberately do NOT auto-hide on focus loss.
        }
    }

    // ------------------- footer / dialogs -------------------
    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideWindow();

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _theme.Toggle();
        ApplyTheme();
        RebuildRows();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _ = ConfirmClearAsync();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _ = ShowSettingsAsync();
    private void AboutButton_Click(object sender, RoutedEventArgs e) => _ = ShowAboutAsync();
    private void QuitButton_Click(object sender, RoutedEventArgs e) => QuitApp();

    private async Task ConfirmClearAsync()
    {
        EnsureVisibleForDialog();
        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Clear history?",
            Content = "Clear unpinned clipboard history? Pinned items will be kept. This cannot be undone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            ClearHistory();
    }

    private void ClearHistory()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            var it = _history[i];
            if (it.Pinned) continue;
            if (it.IsImage) _storage.DeleteMediaFor(it);
            _thumbCache.Remove(it.Id);
            _history.RemoveAt(i);
        }
        _storage.RequestSave(_history);
        RebuildRows();
        UpdateTrayTooltip();
    }

    private async Task ShowAboutAsync()
    {
        EnsureVisibleForDialog();
        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Clippet",
            Content = "Native Windows 11 clipboard manager.\n\nBuilt with C# 14, .NET 10 and WinUI 3 (Windows App SDK).\n\nGlobal hotkey: Ctrl+Shift+V",
            CloseButtonText = "OK",
        };
        await dlg.ShowAsync();
    }

    private async Task ShowSettingsAsync()
    {
        EnsureVisibleForDialog();

        var autostart = new ToggleSwitch { Header = "Start with Windows", IsOn = _storage.IsAutostartEnabled() };
        var theme = new ComboBox { Header = "Theme", SelectedIndex = _settings.ThemeOverride is null ? 0 : (_settings.ThemeOverride.Value ? 1 : 2) };
        theme.Items.Add("Follow system");
        theme.Items.Add("Light");
        theme.Items.Add("Dark");

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(autostart);
        panel.Children.Add(theme);

        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _storage.SetAutostart(autostart.IsOn);
        _settings.AutostartEnabled = autostart.IsOn;
        _settings.ThemeOverride = theme.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        _storage.SaveSettings(_settings);

        // Re-resolve theme (follow-system vs forced) and re-apply.
        bool light = _settings.ThemeOverride ?? ThemeService.ReadSystemLight();
        if (light != _theme.IsLight) _theme.Toggle();
        ApplyTheme();
        RebuildRows();
    }

    private async Task MaybePromptAutostartAsync()
    {
        _settings.AutostartPrompted = true;
        _storage.SaveSettings(_settings);

        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Start with Windows?",
            Content = "Start Clippet automatically with Windows?",
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            _storage.SetAutostart(true);
            _settings.AutostartEnabled = true;
            _storage.SaveSettings(_settings);
        }
    }

    private void EnsureVisibleForDialog()
    {
        if (!_appWindow.IsVisible) ShowAtCursor();
    }

    // ------------------- theme / tray -------------------
    private void ApplyTheme()
    {
        _theme.Apply(RootGrid, _hwnd);
        var pal = _theme.Palette;

        RootGrid.Background = B(WithAlpha(pal.Bg, 0xF2));
        SearchPanel.Background = B(pal.SearchBg);
        TitleText.Foreground = B(pal.Text);
        SearchBox.Foreground = B(pal.Text);
        SearchIcon.Foreground = B(pal.Subtext);

        ThemeIcon.Foreground = B(pal.Accent);
        ClearIcon.Foreground = B(pal.TagRich);
        SettingsIcon.Foreground = B(pal.Subtext);
        AboutIcon.Foreground = B(pal.TagCode);
        QuitIcon.Foreground = B(pal.TagFile);

        ThemeIcon.Glyph = _theme.IsLight ? "" : ""; // moon when light, sun when dark
        ThemeTip.Content = _theme.IsLight ? "Switch to Dark Mode" : "Switch to Light Mode";
    }

    private void UpdateTrayTooltip()
    {
        int pinned = _history.Count(i => i.Pinned);
        _tray.SetTooltip(pinned > 0 ? $"Clippet — {pinned} pinned" : "Clippet");
    }

    private void QuitApp()
    {
        try { _storage.FlushNow(_history); } catch { }
        try { _tray.Dispose(); } catch { }
        try { _subclass.Dispose(); } catch { }
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        Native.RemoveClipboardFormatListener(_hwnd);
        Application.Current.Exit();
    }

    private static SolidColorBrush B(Color c) => new(c);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
