using System.Threading;
using Clippet.Services;
using Clippet.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Clippet;

public partial class App : Application
{
    private const string MutexName = "Clippet_SingleInstance_Mutex";
    private const string ShowEventName = "Clippet_Show_Event";

    private static Mutex? _mutex;
    private PopupWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is running — signal it to surface, then exit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                    ev.Set();
            }
            catch { /* ignore */ }
            Environment.Exit(0);
            return;
        }

        var storage = new Storage();
        var settings = storage.LoadSettings();
        var theme = new ThemeService(settings, storage);

        _window = new PopupWindow(storage, settings, theme);

        StartShowListener(_window.DispatcherQueue);
    }

    private void StartShowListener(DispatcherQueue dispatcher)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                dispatcher.TryEnqueue(() => _window?.ShowFromHotkey());
            }
        })
        { IsBackground = true, Name = "Clippet-ShowListener" };
        thread.Start();
    }
}
