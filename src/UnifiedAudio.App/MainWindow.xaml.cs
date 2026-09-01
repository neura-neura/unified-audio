using UnifiedAudio.Helpers;
using UnifiedAudio.Interop;
using UnifiedAudio.Services;
using UnifiedAudio.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace UnifiedAudio;

public sealed partial class MainWindow : Window
{
    private const int CurrentWindowLayoutVersion = 1;
    private const int InitialWindowWidth = 1120;
    private const int InitialWindowHeight = 760;
    private const int MinimumWindowWidth = 720;
    private const int MinimumWindowHeight = 560;

    private readonly AppController _controller;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        Title = Loc.Get("AppName");
        AppTitleBar.Title = Loc.Get("AppName");
        FlowNavItem.Content = Loc.Get("Flow");
        InputPluginsNavItem.Content = Loc.Get("InputAndPlugins");
        MuteFeedbackNavItem.Content = Loc.Get("MuteAndFeedback");
        MixerNavItem.Content = Loc.Get("Mixer");
        ProfilesNavItem.Content = Loc.Get("Profiles");
        AutomationNavItem.Content = Loc.Get("AutomationAndIntegrations");
        DiagnosticsNavItem.Content = Loc.Get("Diagnostics");
        SettingsNavItem.Content = Loc.Get("Settings");

        ApplyTheme();
        RestoreBounds();
        ContentFrame.Navigate(typeof(FlowPage));
        _controller.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTheme);
        _ = UpdateEngineInfoAsync();
    }

    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = _controller.ResolveElementTheme();
        }
    }

    public void NavigateToProfiles() => Navigate(ProfilesNavItem, typeof(ProfilesPage));
    public void NavigateToSettings() => Navigate(SettingsNavItem, typeof(SettingsPage));

    public void NavigateToEditor(string? profileId)
    {
        ContentFrame.Navigate(typeof(EditProfilePage), profileId);
    }

    public void NavigateToTag(string tag)
    {
        var item = RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
        if (item is not null)
        {
            RootNavigation.SelectedItem = item;
        }
    }

    public void PrepareHiddenStart() => AppWindow.Hide();

    public void HideToTray()
    {
        SaveBounds();
        AppWindow.Hide();
    }

    public void RestoreFromTray()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
        Activate();
        BringNativeWindowToForeground();
    }

    private async Task UpdateEngineInfoAsync()
    {
        try
        {
            if (!_controller.Engine.IsConnected)
            {
                await _controller.Engine.ConnectAsync();
            }
            EngineInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            EngineInfoBar.Title = Loc.Get("EngineUnavailable");
            EngineInfoBar.Message = ex.Message;
            EngineInfoBar.Severity = InfoBarSeverity.Error;
            EngineInfoBar.IsOpen = true;
        }
    }

    private void BringNativeWindowToForeground()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero)
        {
            return;
        }

        NativeMethods.AllowSetForegroundWindow(-1);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    public void ForceClose()
    {
        SaveBounds();
        Close();
    }

    private void RestoreBounds()
    {
        var settings = _controller.Settings;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var hasSavedPosition = settings.WindowLeft is not null && settings.WindowTop is not null;
        var isLegacyLayout = settings.WindowLayoutVersion < CurrentWindowLayoutVersion;
        var availableWidth = Math.Max(1, workArea.Width);
        var availableHeight = Math.Max(1, workArea.Height);
        var minimumWidth = Math.Min(MinimumWindowWidth, availableWidth);
        var minimumHeight = Math.Min(MinimumWindowHeight, availableHeight);

        var desiredWidth = hasSavedPosition && double.IsFinite(settings.WindowWidth)
            ? (int)Math.Round(settings.WindowWidth)
            : InitialWindowWidth;
        var desiredHeight = hasSavedPosition && double.IsFinite(settings.WindowHeight)
            ? (int)Math.Round(settings.WindowHeight)
            : InitialWindowHeight;
        if (isLegacyLayout)
        {
            desiredWidth = Math.Max(desiredWidth, InitialWindowWidth);
            desiredHeight = Math.Max(desiredHeight, InitialWindowHeight);
        }

        var width = Math.Clamp(desiredWidth, minimumWidth, availableWidth);
        var height = Math.Clamp(desiredHeight, minimumHeight, availableHeight);
        var defaultLeft = workArea.X + Math.Max(0, (availableWidth - width) / 2);
        var defaultTop = workArea.Y + Math.Max(0, (availableHeight - height) / 2);
        var left = hasSavedPosition && double.IsFinite(settings.WindowLeft!.Value)
            ? SafeRoundToInt32(settings.WindowLeft.Value, defaultLeft)
            : defaultLeft;
        var top = hasSavedPosition && double.IsFinite(settings.WindowTop!.Value)
            ? SafeRoundToInt32(settings.WindowTop.Value, defaultTop)
            : defaultTop;
        left = Math.Clamp(left, workArea.X, workArea.X + Math.Max(0, availableWidth - width));
        top = Math.Clamp(top, workArea.Y, workArea.Y + Math.Max(0, availableHeight - height));

        AppWindow.MoveAndResize(new RectInt32(left, top, width, height));

        if (isLegacyLayout)
        {
            settings.WindowLayoutVersion = CurrentWindowLayoutVersion;
            settings.WindowLeft = left;
            settings.WindowTop = top;
            settings.WindowWidth = width;
            settings.WindowHeight = height;
            _controller.Persist();
        }
    }

    private static int SafeRoundToInt32(double value, int fallback)
    {
        if (!double.IsFinite(value))
        {
            return fallback;
        }

        return value <= int.MinValue
            ? int.MinValue
            : value >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Round(value);
    }

    private void SaveBounds()
    {
        var area = AppWindow.Position;
        var size = AppWindow.Size;
        _controller.Settings.WindowLeft = area.X;
        _controller.Settings.WindowTop = area.Y;
        _controller.Settings.WindowWidth = size.Width;
        _controller.Settings.WindowHeight = size.Height;
        _controller.Persist();
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "inputPlugins" => typeof(InputPluginsPage),
            "muteFeedback" => typeof(MuteFeedbackPage),
            "mixer" => typeof(MixerPage),
            "profiles" => typeof(ProfilesPage),
            "automation" => typeof(AutomationPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(FlowPage)
        };
        Navigate(item, pageType);
    }

    private void Navigate(NavigationViewItem item, Type pageType)
    {
        if (!ReferenceEquals(RootNavigation.SelectedItem, item))
        {
            RootNavigation.SelectedItem = item;
        }
        if (ContentFrame.Content?.GetType() != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
