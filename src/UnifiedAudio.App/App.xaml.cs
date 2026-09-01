using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace UnifiedAudio;

public partial class App : Application
{
    private MainWindow? _window;
    private AppController? _controller;
    private bool _exitRequested;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static App Instance => (App)Current;
    public AppController Controller => _controller ?? throw new InvalidOperationException("The application is still starting.");
    public MainWindow? MainAppWindow => _window;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _controller = new AppController(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        await _controller.InitializeAsync();
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;

        _window = new MainWindow(_controller);
        _window.Closed += OnWindowClosed;
        _controller.OpenRequested += (_, _) => ShowMainWindow();
        _controller.ExitRequested += (_, _) => ExitApplication();

        var startHidden = !_controller.Startup.ShouldShowInForeground()
            && (_controller.Startup.WasStartedByWindows() || _controller.Settings.LaunchMinimized);
        if (startHidden && _controller.Settings.KeepRunningInBackground)
        {
            _window.PrepareHiddenStart();
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.RestoreFromTray();
        _window.Activate();
    }

    public void ExitApplication(bool persist = true)
    {
        _exitRequested = true;
        try
        {
            if (persist) _controller?.Persist();
            _controller?.Dispose();
        }
        catch
        {
        }

        _window?.ForceClose();
        Exit();
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        _window?.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_exitRequested || _controller is null || !_controller.Settings.KeepRunningInBackground)
        {
            if (!_exitRequested)
            {
                ExitApplication();
            }

            return;
        }

        args.Handled = true;
        _window?.HideToTray();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            _controller?.Log.Error("Unhandled UI exception.", e.Exception);
        }
        catch
        {
        }

        e.Handled = true;
    }
}
