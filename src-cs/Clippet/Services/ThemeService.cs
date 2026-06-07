using Clippet.Models;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace Clippet.Services;

/// <summary>Resolves and applies the effective light/dark theme (override or follow-system),
/// owns the active <see cref="Palette"/>, and toggles/persists the override.</summary>
public sealed class ThemeService
{
    private readonly SettingsDto _settings;
    private readonly Storage _storage;

    public bool IsLight { get; private set; }
    public Palette Palette => Palette.For(IsLight);

    public ThemeService(SettingsDto settings, Storage storage)
    {
        _settings = settings;
        _storage = storage;
        IsLight = Resolve();
    }

    private bool Resolve() => _settings.ThemeOverride ?? ReadSystemLight();

    public static bool ReadSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* default below */ }
        return false; // default dark
    }

    /// <summary>Flip light↔dark, persist as an explicit override.</summary>
    public bool Toggle()
    {
        IsLight = !IsLight;
        _settings.ThemeOverride = IsLight;
        _storage.SaveSettings(_settings);
        return IsLight;
    }

    /// <summary>Apply theme to a window: element theme on the root + DWM dark mode and rounded corners.</summary>
    public void Apply(FrameworkElement root, IntPtr hwnd)
    {
        root.RequestedTheme = IsLight ? ElementTheme.Light : ElementTheme.Dark;

        int dark = IsLight ? 0 : 1;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int corner = (int)Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }
}
