using UnifiedAudio.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Microsoft.UI;
using UnifiedAudio.Models;
using UnifiedAudio.Helpers;

namespace UnifiedAudio.Views;

public sealed class MuteOsdWindow : Window, IDisposable
{
    private readonly Border _surface;
    private readonly FontIcon _icon;
    private readonly TextBlock _text;
    private readonly DispatcherQueueTimer _timer;

    public MuteOsdWindow()
    {
        Title = "UnifiedAudio OSD";
        _icon = new FontIcon { FontSize = 24, Foreground = new SolidColorBrush(Colors.White) };
        _text = new TextBlock
        {
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(_icon);
        content.Children.Add(_text);
        _surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 12, 18, 12),
            Child = content
        };
        Content = _surface;
        AutomationProperties.SetName(_surface, LiteralCatalog.Get("Estado del micrófono"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        AppWindow.Resize(new SizeInt32(360, 72));
        ApplyNoActivateStyle();

        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => AppWindow.Hide();
    }

    public void ShowState(bool muted, AppSettings settings)
    {
        _settingsForPlacement = settings;
        _icon.Glyph = muted ? "\uE74F" : "\uE767";
        var configuredText = muted ? settings.MutedOsdText : settings.UnmutedOsdText;
        _text.Text = configuredText is "Micrófono muteado" or "Microphone muted" or "麦克风已静音"
            ? LiteralCatalog.Get("Micrófono muteado")
            : configuredText is "Micrófono activo" or "Microphone active" or "麦克风活动"
                ? LiteralCatalog.Get("Micrófono activo")
                : configuredText;
        _surface.Background = new SolidColorBrush(muted
            ? UiColor.Parse(settings.MutedOsdColor, Colors.DarkRed)
            : UiColor.Parse(settings.UnmutedOsdColor, Colors.DodgerBlue));
        AutomationProperties.SetName(_surface, _text.Text);
        MoveToPosition(settings.OsdPosition);
        AppWindow.Show(false);
        AppWindow.MoveInZOrderAtTop();
        EnsureTopmost();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.MuteOsdDurationMilliseconds, 250, 10000));
        _timer.Start();
    }

    private void MoveToBottomCenter()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + work.Height - size.Height - 24));
    }

    private void MoveToPosition(MuteOsdPosition position)
    {
        var settings = _settingsForPlacement;
        var selected = settings is not null ? FindMonitor(settings.OsdMonitorId) : null;
        var work = selected ?? GetForegroundWorkArea();
        var size = AppWindow.Size;
        const int margin = 24;
        var left = position is MuteOsdPosition.TopLeft or MuteOsdPosition.BottomLeft;
        var right = position is MuteOsdPosition.TopRight or MuteOsdPosition.BottomRight;
        var top = position is MuteOsdPosition.TopLeft or MuteOsdPosition.TopCenter or MuteOsdPosition.TopRight;
        var x = left ? work.X + margin : right ? work.X + work.Width - size.Width - margin
                                             : work.X + (work.Width - size.Width) / 2;
        var y = top ? work.Y + margin : work.Y + work.Height - size.Height - margin;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - size.Width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));
        AppWindow.Move(new PointInt32(x, y));
    }

    private AppSettings? _settingsForPlacement;

    private MonitorWorkArea GetForegroundWorkArea()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var area = foreground != nint.Zero
            ? DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Primary)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        return new MonitorWorkArea(work.X, work.Y, work.Width, work.Height);
    }

    private static MonitorWorkArea? FindMonitor(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var monitor = NativeMethods.EnumerateMonitors()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(monitor.Id)
            ? null
            : new MonitorWorkArea(
                monitor.Work.Left,
                monitor.Work.Top,
                monitor.Work.Right - monitor.Work.Left,
                monitor.Work.Bottom - monitor.Work.Top);
    }

    private sealed record MonitorWorkArea(int X, int Y, int Width, int Height);

    private void EnsureTopmost()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero) return;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate
                | NativeMethods.SwpShowWindow);
    }

    private void ApplyNoActivateStyle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new nint(style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate
                | NativeMethods.SwpFrameChanged);
    }

    public void Dispose()
    {
        _timer.Stop();
        AppWindow.Hide();
        Close();
    }
}
