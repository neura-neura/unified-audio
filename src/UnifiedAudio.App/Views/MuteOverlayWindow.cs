using UnifiedAudio.Interop;
using UnifiedAudio.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Microsoft.UI;
using UnifiedAudio.Helpers;
using System.Runtime.InteropServices;

namespace UnifiedAudio.Views;

public sealed class MuteOverlayWindow : Window, IDisposable
{
    private const int BaseWindowDiameter = 64;
    private const int BaseCircleDiameter = 48;
    private const int CircleInset = 8;

    private readonly Grid _surface;
    private readonly Viewbox _circleViewport;
    private readonly Ellipse _circle;
    private AppSettings? _settings;
    private bool _applyingPlacement;
    private bool _disposed;
    private bool _subclassInstalled;
    private nint _previousWndProc;
    private NativeMethods.WndProc? _wndProcDelegate;
    private bool _isResizing;
    private int _resizeHitTest;
    private NativeMethods.Point _resizeStartCursor;
    private PointInt32 _resizeStartPosition;
    private SizeInt32 _resizeStartSize;
    private int _lastHitTest;
    private int _lastHitScreenX;
    private int _lastHitScreenY;

    public Action<string, double, double>? PlacementChanged { get; set; }

    public MuteOverlayWindow()
    {
        // The overlay is deliberately borderless in both modes. Unlocked
        // movement/resizing is provided by WM_NCHITTEST below, so the native
        // caption can never introduce a titlebar or a rectangular frame.
        Title = string.Empty;
        ExtendsContentIntoTitleBar = false;

        _circle = new Ellipse
        {
            Width = BaseCircleDiameter,
            Height = BaseCircleDiameter,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Fill
        };
        _circleViewport = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(CircleInset),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = _circle
        };
        _surface = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _surface.Children.Add(_circleViewport);
        Content = _surface;
        _surface.PointerPressed += Surface_PointerPressed;
        _surface.PointerMoved += Surface_PointerMoved;
        _surface.PointerReleased += Surface_PointerReleased;
        _surface.PointerCanceled += Surface_PointerCanceled;

        // There is intentionally no visible label or glyph. Keep the state
        // available to UI Automation whenever the overlay is unlocked.
        AutomationProperties.SetAutomationId(_surface, "MuteOverlayState");
        AutomationProperties.SetName(_surface, LiteralCatalog.Get("Estado del micrófono"));
        AutomationProperties.SetAutomationId(_circle, "MuteOverlayCircle");

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        AppWindow.Resize(new SizeInt32(BaseWindowDiameter, BaseWindowDiameter));
        UpdateCircleLayout();

        // The subclass is installed once and kept alive by the field. Returning
        // HTTRANSPARENT from the top-level WndProc is what makes locked input
        // pass through to a window owned by another thread/process; the
        // WS_EX_TRANSPARENT bit alone only affects paint ordering.
        InstallWndProcSubclass();
        Closed += OverlayWindow_Closed;

        // Give an immediately enabled overlay a meaningful accessible state
        // while the first engine snapshot is still in flight.
        UpdateState(muted: false, speaking: false, engineAvailable: true);
        ApplyLockedStyle();
        MoveToTopRight();
        AppWindow.Changed += AppWindow_Changed;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        var scale = Math.Clamp(settings.OverlayScalePercent, 50, 300) / 100.0;
        var diameter = (int)Math.Round(BaseWindowDiameter * scale);
        AppWindow.Resize(new SizeInt32(diameter, diameter));
        ApplyCircleOpacity();
        UpdateCircleLayout();
        ApplyLockedStyle();
        MoveToConfiguredPosition();
    }

    public void UpdateState(bool muted, bool speaking, bool engineAvailable = true)
    {
        var stateName = !engineAvailable
            ? Loc.Get("EngineUnavailable")
            : muted
                ? LiteralCatalog.Get("Muteado")
                : speaking
                    ? LiteralCatalog.Get("Hablando")
                    : LiteralCatalog.Get("Micrófono activo");
        var color = !engineAvailable
            ? UiColor.Parse(_settings?.OverlayIdleColor, Colors.DimGray)
            : muted
                ? UiColor.Parse(_settings?.OverlayMutedColor, Colors.DarkRed)
                : speaking
                    ? UiColor.Parse(_settings?.OverlaySpeakingColor, Colors.ForestGreen)
                    : UiColor.Parse(_settings?.OverlayIdleColor, Colors.DimGray);

        _circle.Fill = new SolidColorBrush(color);
        ApplyCircleOpacity();
        var accessibleName = string.Format(LiteralCatalog.Get("Micrófono: {0}"), stateName);
        AutomationProperties.SetName(_surface, accessibleName);
        AutomationProperties.SetName(_circle, accessibleName);
    }

    public void SetVisible(bool visible)
    {
        if (_disposed) return;
        if (visible)
        {
            // SetVisible is called from the 250 ms refresh loop. Re-applying the
            // saved position on every tick would undo an unlocked drag and made
            // an off-screen window impossible to recover reliably.
            ApplyLockedStyle();
            if (!IsOnScreen())
                MoveToConfiguredPosition();
            AppWindow.Show(false);
            AppWindow.MoveInZOrderAtTop();
            EnsureTopmost(show: true);
        }
        else
        {
            AppWindow.Hide();
        }
    }

    private void MoveToTopRight()
    {
        var monitor = GetCurrentMonitor();
        var work = monitor;
        var size = AppWindow.Size;
        var x = Math.Max(work.X, work.X + work.Width - size.Width - 20);
        var y = Math.Max(work.Y, work.Y + 20);
        MoveWindow(x, y);
    }

    private void MoveToConfiguredPosition()
    {
        if (_settings is null) { MoveToTopRight(); return; }
        var monitor = FindMonitor(_settings.OverlayMonitorId) ?? GetCurrentMonitor();
        var work = monitor;
        var placement = !string.IsNullOrWhiteSpace(monitor.Id)
            && _settings.OverlayMonitorPlacements.TryGetValue(monitor.Id, out var saved)
                ? saved
                : new OverlayMonitorPlacement
                {
                    RelativeX = _settings.OverlayRelativeX,
                    RelativeY = _settings.OverlayRelativeY
                };
        var size = AppWindow.Size;
        var availableX = Math.Max(0, work.Width - size.Width);
        var availableY = Math.Max(0, work.Height - size.Height);
        var x = work.X + (int)Math.Round(availableX * Math.Clamp(placement.RelativeX, 0.0, 1.0));
        var y = work.Y + (int)Math.Round(availableY * Math.Clamp(placement.RelativeY, 0.0, 1.0));
        MoveWindow(x, y);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange == true)
        {
            UpdateCircleLayout();
            ApplyCircleRegion(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        if (_applyingPlacement || _settings?.OverlayLocked != false
            || (args.DidPositionChange != true && args.DidSizeChange != true)) return;
        var monitor = GetCurrentMonitor();
        var work = monitor;
        var size = AppWindow.Size;
        var availableX = Math.Max(1, work.Width - size.Width);
        var availableY = Math.Max(1, work.Height - size.Height);
        var x = Math.Clamp((AppWindow.Position.X - work.X) / (double)availableX, 0.0, 1.0);
        var y = Math.Clamp((AppWindow.Position.Y - work.Y) / (double)availableY, 0.0, 1.0);
        if (args.DidSizeChange == true)
            _settings.OverlayScalePercent = Math.Clamp((int)Math.Round(AppWindow.Size.Width / (double)BaseWindowDiameter * 100.0), 50, 300);
        PlacementChanged?.Invoke(monitor.Id, x, y);
    }

    private MonitorWorkArea GetCurrentMonitor()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        return ReadMonitor(handle) ?? new MonitorWorkArea(string.Empty, 0, 0, 1920, 1080);
    }

    private static MonitorWorkArea? FindMonitor(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        MonitorWorkArea? match = null;
        NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var candidate = ReadMonitor(monitor);
            if (candidate is not null && string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
                match = candidate;
            return match is null;
        };
        NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback);
        return match;
    }

    private static MonitorWorkArea? ReadMonitor(nint handle)
    {
        if (handle == nint.Zero) return null;
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            Device = string.Empty
        };
        if (!NativeMethods.GetMonitorInfo(handle, ref info)) return null;
        return new MonitorWorkArea(
            info.Device,
            info.Work.Left,
            info.Work.Top,
            info.Work.Right - info.Work.Left,
            info.Work.Bottom - info.Work.Top);
    }

    private sealed record MonitorWorkArea(string Id, int X, int Y, int Width, int Height);

    private void MoveWindow(int x, int y)
    {
        _applyingPlacement = true;
        try
        {
            AppWindow.Move(new PointInt32(x, y));
        }
        finally
        {
            _applyingPlacement = false;
        }
    }

    private bool IsOnScreen()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        return NativeMethods.EnumerateMonitors().Any(monitor =>
            rect.Right > monitor.Monitor.Left
            && rect.Left < monitor.Monitor.Right
            && rect.Bottom > monitor.Monitor.Top
            && rect.Top < monitor.Monitor.Bottom);
    }

    private void EnsureTopmost(bool show)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero) return;
        var flags = NativeMethods.SwpNoMove
            | NativeMethods.SwpNoSize
            | NativeMethods.SwpNoActivate
            | (show ? NativeMethods.SwpShowWindow : 0u);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
    }

    private void ApplyLockedStyle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero) return;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var locked = _settings?.OverlayLocked ?? true;
        // WS_EX_LAYERED keeps the root surface genuinely transparent outside
        // the ellipse. WS_EX_TRANSPARENT remains useful for paint ordering,
        // while WM_NCHITTEST below provides the actual input pass-through.
        var baseStyle = style
            | NativeMethods.WsExToolWindow
            | NativeMethods.WsExLayered;
        var newStyle = locked
            ? baseStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent
            : baseStyle & ~NativeMethods.WsExNoActivate & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new nint(newStyle));
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate
                | NativeMethods.SwpFrameChanged);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Never expose a caption, border, or resize frame: these would
            // render a rectangular window around the state circle. Native
            // hit-testing below still makes the borderless window draggable
            // and resizable while unlocked.
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = !locked;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        var windowStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        windowStyle = locked
            ? windowStyle & ~NativeMethods.WsThickFrame
            : windowStyle | NativeMethods.WsThickFrame;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlStyle, new nint(windowStyle));
        UpdateCircleLayout();
        // Keep the region in both modes. Clearing it when unlocked exposed a
        // rectangular WinUI surface (and any compositor backdrop) outside the
        // circle. SetWindowRgn clips that surface at the native level.
        ApplyCircleRegion(hwnd);
    }

    private void UpdateCircleLayout()
    {
        var size = AppWindow.Size;
        var scale = Math.Min(size.Width, size.Height) / (double)BaseWindowDiameter;
        _circleViewport.Margin = new Thickness(CircleInset * Math.Max(0.01, scale));
    }

    private void ApplyCircleOpacity()
    {
        _circle.Opacity = Math.Clamp(_settings?.OverlayOpacityPercent ?? 100, 10, 100) / 100.0;
    }

    private static void ApplyCircleRegion(nint hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var client)) return;
        var width = Math.Max(1, client.Right - client.Left);
        var height = Math.Max(1, client.Bottom - client.Top);
        var scale = Math.Min(width, height) / (double)BaseWindowDiameter;
        var inset = Math.Max(0, (int)Math.Round(CircleInset * scale));
        var diameter = Math.Max(1, Math.Min(width, height) - 2 * inset);
        var left = (width - diameter) / 2;
        var top = (height - diameter) / 2;
        var region = NativeMethods.CreateEllipticRgn(left, top, left + diameter, top + diameter);
        if (region == nint.Zero) return;
        if (NativeMethods.SetWindowRgn(hwnd, region, true) == 0)
            NativeMethods.DeleteObject(region);
    }

    private bool IsLocked => _settings?.OverlayLocked ?? true;

    private static int UnpackScreenCoordinate(nint packed, bool y)
    {
        var value = packed.ToInt64();
        return unchecked((short)((value >> (y ? 16 : 0)) & 0xFFFF));
    }

    private nint GetUnlockedHitTest(nint hwnd, int screenX, int screenY)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
            return new nint(NativeMethods.HtClient);

        var x = screenX - windowRect.Left;
        var y = screenY - windowRect.Top;
        if (!NativeMethods.GetClientRect(hwnd, out var client))
            return new nint(NativeMethods.HtClient);

        var width = Math.Max(1, client.Right - client.Left);
        var height = Math.Max(1, client.Bottom - client.Top);
        var scale = Math.Min(width, height) / (double)BaseWindowDiameter;
        var inset = Math.Max(0, (int)Math.Round(CircleInset * scale));
        var grip = Math.Clamp((int)Math.Round(8 * scale), 6, 12);
        var left = inset;
        var top = inset;
        var right = width - inset - 1;
        var bottom = height - inset - 1;
        var nearLeft = x <= left + grip;
        var nearRight = x >= right - grip;
        var nearTop = y <= top + grip;
        var nearBottom = y >= bottom - grip;

        if (nearTop && nearLeft) return new nint(NativeMethods.HtTopLeft);
        if (nearTop && nearRight) return new nint(NativeMethods.HtTopRight);
        if (nearBottom && nearLeft) return new nint(NativeMethods.HtBottomLeft);
        if (nearBottom && nearRight) return new nint(NativeMethods.HtBottomRight);
        if (nearLeft) return new nint(NativeMethods.HtLeft);
        if (nearRight) return new nint(NativeMethods.HtRight);
        if (nearTop) return new nint(NativeMethods.HtTop);
        if (nearBottom) return new nint(NativeMethods.HtBottom);
        return new nint(NativeMethods.HtCaption);
    }

    private void InstallWndProcSubclass()
    {
        if (_subclassInstalled) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero) return;

        _wndProcDelegate = WindowProc;
        var previous = NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        if (previous == nint.Zero)
        {
            _wndProcDelegate = null;
            return;
        }

        _previousWndProc = previous;
        _subclassInstalled = true;
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (IsLocked && message == NativeMethods.WmNcHitTest)
            return new nint(NativeMethods.HtTransparent);
        if (IsLocked && message == NativeMethods.WmMouseActivate)
            return new nint(NativeMethods.MaNoActivate);
        if (!IsLocked && message == NativeMethods.WmNcHitTest)
        {
            var hitTest = GetUnlockedHitTest(
                hwnd,
                UnpackScreenCoordinate(lParam, y: false),
                UnpackScreenCoordinate(lParam, y: true));
            _lastHitTest = hitTest.ToInt32();
            _lastHitScreenX = UnpackScreenCoordinate(lParam, y: false);
            _lastHitScreenY = UnpackScreenCoordinate(lParam, y: true);
            // Let WinUI deliver client PointerPressed on the invisible rim so
            // the manual resize path can capture it. The interior remains a
            // native caption hit-test for drag; returning HTLEFT/HTRIGHT here
            // makes the borderless WinUI frame consume the message as a move.
            return IsResizeHitTest(_lastHitTest) ? new nint(NativeMethods.HtClient) : hitTest;
        }
        if (!IsLocked && message == NativeMethods.WmNcLButtonDown
            && NativeMethods.GetCursorPos(out var ncCursor)
            && IsResizeHitTest(GetUnlockedHitTest(hwnd, ncCursor.X, ncCursor.Y).ToInt32()))
        {
            var resizeHitTest = GetUnlockedHitTest(hwnd, ncCursor.X, ncCursor.Y).ToInt32();
            BeginResize(resizeHitTest, ncCursor.X, ncCursor.Y, hwnd);
            return nint.Zero;
        }
        if (!IsLocked && message == NativeMethods.WmLButtonDown
            && NativeMethods.GetCursorPos(out var clientCursor))
        {
            var clientHitTest = GetUnlockedHitTest(hwnd, clientCursor.X, clientCursor.Y).ToInt32();
            if (IsResizeHitTest(clientHitTest))
            {
                BeginResize(clientHitTest, clientCursor.X, clientCursor.Y, hwnd);
                return nint.Zero;
            }
        }
        if (_isResizing && message == NativeMethods.WmMouseMove)
        {
            if (NativeMethods.GetCursorPos(out var cursor))
                ResizeFromCursor(cursor);
            return nint.Zero;
        }
        if (_isResizing && message == NativeMethods.WmLButtonUp)
        {
            EndResize();
            return nint.Zero;
        }
        var result = _previousWndProc != nint.Zero
            ? NativeMethods.CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam)
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        if (message == NativeMethods.WmSize)
            ApplyCircleRegion(hwnd);
        if (message == NativeMethods.WmNcDestroy)
        {
            _subclassInstalled = false;
            _previousWndProc = nint.Zero;
            _wndProcDelegate = null;
        }
        return result;
    }

    private void RemoveWndProcSubclass()
    {
        if (!_subclassInstalled) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != nint.Zero && _previousWndProc != nint.Zero)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlpWndProc, _previousWndProc);
        }

        _subclassInstalled = false;
        _previousWndProc = nint.Zero;
        // The delegate must stay rooted until the native proc has been
        // restored; only then is it safe to release the managed reference.
        _wndProcDelegate = null;
    }

    private void OverlayWindow_Closed(object sender, WindowEventArgs args) => RemoveWndProcSubclass();

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (IsLocked) return;
        var point = args.GetCurrentPoint(_surface);
        if (!point.Properties.IsLeftButtonPressed) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero) return;

        if (!NativeMethods.GetCursorPos(out var cursor))
            return;
        var hitTest = GetUnlockedHitTest(hwnd, cursor.X, cursor.Y).ToInt32();
        if (hitTest == NativeMethods.HtClient)
        {
            return;
        }

        if (IsResizeHitTest(hitTest))
        {
            BeginResize(hitTest, cursor.X, cursor.Y, hwnd);
            args.Handled = true;
            return;
        }

        // WinUI may capture the pointer before the borderless HWND enters the
        // native caption move path. Release that capture and ask the window
        // manager to perform the ordinary caption drag; no custom move loop
        // runs in our subclass.
        args.Handled = true;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(
            hwnd,
            NativeMethods.WmNcLButtonDown,
            new nint(hitTest),
            nint.Zero);
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_isResizing || IsLocked) return;
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        ResizeFromCursor(cursor);
        args.Handled = true;
    }

    private void BeginResize(int hitTest, int cursorX, int cursorY, nint hwnd)
    {
        if (_isResizing) return;
        _isResizing = true;
        _resizeHitTest = hitTest;
        _resizeStartCursor = new NativeMethods.Point { X = cursorX, Y = cursorY };
        _resizeStartPosition = AppWindow.Position;
        _resizeStartSize = AppWindow.Size;
        NativeMethods.SetCapture(hwnd);
    }

    private void ResizeFromCursor(NativeMethods.Point cursor)
    {
        if (!_isResizing || IsLocked) return;

        var deltaX = cursor.X - _resizeStartCursor.X;
        var deltaY = cursor.Y - _resizeStartCursor.Y;
        var minSize = (int)Math.Round(BaseWindowDiameter * 0.5);
        var maxSize = BaseWindowDiameter * 3;
        var startSize = Math.Max(1, Math.Min(_resizeStartSize.Width, _resizeStartSize.Height));
        var sizeDelta = GetSquareResizeDelta(_resizeHitTest, deltaX, deltaY);
        var size = Math.Clamp(startSize + sizeDelta, minSize, maxSize);
        var x = _resizeStartPosition.X;
        var y = _resizeStartPosition.Y;
        if (_resizeHitTest is NativeMethods.HtLeft or NativeMethods.HtTopLeft or NativeMethods.HtBottomLeft)
            x = _resizeStartPosition.X + _resizeStartSize.Width - size;
        if (_resizeHitTest is NativeMethods.HtTop or NativeMethods.HtTopLeft or NativeMethods.HtTopRight)
            y = _resizeStartPosition.Y + _resizeStartSize.Height - size;

        AppWindow.MoveAndResize(new RectInt32(x, y, size, size));
    }

    private static int GetSquareResizeDelta(int hitTest, int deltaX, int deltaY)
    {
        var horizontalDominant = Math.Abs(deltaX) >= Math.Abs(deltaY);
        return hitTest switch
        {
            NativeMethods.HtLeft => -deltaX,
            NativeMethods.HtRight => deltaX,
            NativeMethods.HtTop => -deltaY,
            NativeMethods.HtBottom => deltaY,
            NativeMethods.HtTopLeft => horizontalDominant ? -deltaX : -deltaY,
            NativeMethods.HtTopRight => horizontalDominant ? deltaX : -deltaY,
            NativeMethods.HtBottomLeft => horizontalDominant ? -deltaX : deltaY,
            NativeMethods.HtBottomRight => horizontalDominant ? deltaX : deltaY,
            _ => 0
        };
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_isResizing) return;
        EndResize();
        args.Handled = true;
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (!_isResizing) return;
        EndResize();
        args.Handled = true;
    }

    private void EndResize()
    {
        if (!_isResizing) return;
        _isResizing = false;
        NativeMethods.ReleaseCapture();
    }

    private static bool IsResizeHitTest(int hitTest) => hitTest is
        NativeMethods.HtLeft or NativeMethods.HtRight
        or NativeMethods.HtTop or NativeMethods.HtTopLeft or NativeMethods.HtTopRight
        or NativeMethods.HtBottom or NativeMethods.HtBottomLeft or NativeMethods.HtBottomRight;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.PointerPressed -= Surface_PointerPressed;
        _surface.PointerMoved -= Surface_PointerMoved;
        _surface.PointerReleased -= Surface_PointerReleased;
        _surface.PointerCanceled -= Surface_PointerCanceled;
        AppWindow.Changed -= AppWindow_Changed;
        Closed -= OverlayWindow_Closed;
        RemoveWndProcSubclass();
        AppWindow.Hide();
        Close();
    }
}
