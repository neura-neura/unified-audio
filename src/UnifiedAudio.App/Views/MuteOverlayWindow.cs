using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using UnifiedAudio.Core.Audio;
using UnifiedAudio.Helpers;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;
using Windows.UI;

namespace UnifiedAudio.Views;

// Every visible pixel is a premultiplied-alpha circle. There is no WinUI
// backing surface, window frame, clipped white matte, or second ellipse.
public sealed class MuteOverlayWindow : IDisposable
{
    private readonly string _className = "UnifiedAudio.Overlay." + Guid.NewGuid().ToString("N");
    private readonly NativeMethods.WndProc _windowProc;
    private readonly nint _instance = NativeMethods.GetModuleHandle(null);
    private nint _hwnd;
    private AppSettings? _settings;
    private int _size = 64;
    private bool _visible, _dragging, _resizing, _muted, _speaking, _running;
    private NativeMethods.Point _startCursor;
    private NativeMethods.Rect _startRect;
    private Color _color = Colors.DimGray;
    private (int Size, Color Color, int Opacity)? _painted;
    public Action<string, double, double>? PlacementChanged { get; set; }
    internal nint Handle => _hwnd;
    private bool IsLocked => _settings?.OverlayLocked ?? true;

    public MuteOverlayWindow()
    {
        _windowProc = WindowProc;
        var wc = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance = _instance, lpszClassName = _className
        };
        if (NativeMethods.RegisterClassEx(ref wc) == 0) throw new Win32Exception();
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExLayered | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
            _className, "UnifiedAudio microphone status", unchecked((int)0x80000000),
            0, 0, _size, _size, 0, 0, _instance, 0);
        if (_hwnd == 0) { UnregisterClass(_className, _instance); throw new Win32Exception(); }
        int noRounding = 1, noBorder = -2;
        DwmSetWindowAttribute(_hwnd, 33, ref noRounding, sizeof(int));
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));
        Paint();
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_hwnd == 0) return;
        _settings = settings;
        _size = (int)Math.Round(64 * Math.Clamp(settings.OverlayScalePercent, 50, 300) / 100.0);
        var styles = NativeMethods.WsExLayered | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        if (IsLocked) styles |= NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle, new nint(styles));
        MoveToConfiguredPosition();
        UpdateState(_muted, _speaking, _running);
    }

    public void UpdateState(bool muted, bool speaking, bool engineAvailable = true)
    {
        if (_hwnd == 0) return;
        _muted = muted; _speaking = speaking; _running = engineAvailable;
        _color = !engineAvailable ? UiColor.Parse(_settings?.OverlayIdleColor, Colors.DimGray)
            : muted ? UiColor.Parse(_settings?.OverlayMutedColor, Colors.DarkRed)
            : speaking ? UiColor.Parse(_settings?.OverlaySpeakingColor, Colors.ForestGreen)
            : UiColor.Parse(_settings?.OverlayIdleColor, Colors.DimGray);
        var label = !engineAvailable ? Loc.Get("EngineUnavailable") : muted ? LiteralCatalog.Get("Muteado")
            : speaking ? LiteralCatalog.Get("Hablando") : LiteralCatalog.Get("Micrófono activo");
        SetWindowText(_hwnd, string.Format(LiteralCatalog.Get("Micrófono: {0}"), label));
        Paint();
    }

    public void SetVisible(bool visible)
    {
        if (_hwnd == 0) return;
        if (visible)
        {
            if (!IsOnScreen()) MoveToConfiguredPosition();
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }
        else if (_visible) NativeMethods.ShowWindow(_hwnd, 0);
        _visible = visible;
    }

    private NativeMethods.MonitorDescriptor CurrentMonitor()
    {
        var monitors = NativeMethods.EnumerateMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("No display is available.");
        NativeMethods.GetWindowRect(_hwnd, out var r);
        return monitors.FirstOrDefault(m => r.Left + _size / 2 >= m.Monitor.Left
            && r.Left + _size / 2 < m.Monitor.Right && r.Top + _size / 2 >= m.Monitor.Top
            && r.Top + _size / 2 < m.Monitor.Bottom, monitors.FirstOrDefault(m => m.IsPrimary, monitors[0]));
    }

    private void MoveToConfiguredPosition()
    {
        var monitor = NativeMethods.EnumerateMonitors().FirstOrDefault(
            m => string.Equals(m.Id, _settings?.OverlayMonitorId, StringComparison.OrdinalIgnoreCase), CurrentMonitor());
        var x = _settings?.OverlayRelativeX ?? 0.95;
        var y = _settings?.OverlayRelativeY ?? 0.05;
        if (!string.IsNullOrWhiteSpace(_settings?.OverlayMonitorId)
            && _settings.OverlayMonitorPlacements.TryGetValue(monitor.Id, out var saved))
        { x = saved.RelativeX; y = saved.RelativeY; }
        Move(monitor.Work.Left + (int)Math.Round(Math.Max(0, monitor.Work.Right - monitor.Work.Left - _size) * Math.Clamp(x, 0, 1)),
             monitor.Work.Top + (int)Math.Round(Math.Max(0, monitor.Work.Bottom - monitor.Work.Top - _size) * Math.Clamp(y, 0, 1)));
        Paint();
    }

    private bool IsOnScreen()
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return false;
        return NativeMethods.EnumerateMonitors().Any(m => r.Right > m.Work.Left && r.Left < m.Work.Right
            && r.Bottom > m.Work.Top && r.Top < m.Work.Bottom);
    }
    private void Move(int x, int y) => NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost,
        x, y, _size, _size, NativeMethods.SwpNoActivate);

    private void SavePlacement()
    {
        if (_settings is null || !NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        var m = CurrentMonitor();
        var x = Math.Clamp((r.Left - m.Work.Left) / (double)Math.Max(1, m.Work.Right - m.Work.Left - _size), 0, 1);
        var y = Math.Clamp((r.Top - m.Work.Top) / (double)Math.Max(1, m.Work.Bottom - m.Work.Top - _size), 0, 1);
        _settings.OverlayScalePercent = (int)Math.Round(_size / 64.0 * 100);
        PlacementChanged?.Invoke(m.Id, x, y);
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmNcHitTest)
            return new nint(IsLocked ? NativeMethods.HtTransparent : NativeMethods.HtClient);
        if (message == NativeMethods.WmMouseActivate) return new nint(NativeMethods.MaNoActivate);
        if (message == NativeMethods.WmLButtonDown && !IsLocked && NativeMethods.GetCursorPos(out _startCursor))
        {
            NativeMethods.GetWindowRect(hwnd, out _startRect);
            var x = _startCursor.X - _startRect.Left - _size / 2.0;
            var y = _startCursor.Y - _startRect.Top - _size / 2.0;
            _resizing = Math.Sqrt(x * x + y * y) >= _size * 0.375 - 5;
            _dragging = !_resizing;
            NativeMethods.SetCapture(hwnd);
            return 0;
        }
        if (message == NativeMethods.WmMouseMove && (_dragging || _resizing) && NativeMethods.GetCursorPos(out var cursor))
        {
            var dx = cursor.X - _startCursor.X;
            var dy = cursor.Y - _startCursor.Y;
            if (_dragging) Move(_startRect.Left + dx, _startRect.Top + dy);
            else
            {
                _size = Math.Clamp(_startRect.Right - _startRect.Left + (Math.Abs(dx) >= Math.Abs(dy) ? dx : dy), 32, 192);
                Move(_startRect.Left, _startRect.Top);
                Paint();
            }
            return 0;
        }
        if (message == NativeMethods.WmLButtonUp && (_dragging || _resizing))
        {
            _dragging = _resizing = false;
            NativeMethods.ReleaseCapture();
            SavePlacement();
            return 0;
        }
        if (message == 0x0215) { _dragging = _resizing = false; }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Paint()
    {
        var opacity = Math.Clamp(_settings?.OverlayOpacityPercent ?? 100, 10, 100);
        var paint = (_size, _color, opacity);
        if (_painted == paint || _hwnd == 0) return;
        var pixels = OverlayCircle.Render(_size, _color.R, _color.G, _color.B, _color.A, opacity);
        var dc = CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception();
        var info = new BitmapInfo { Size = 40, Width = _size, Height = -_size, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
        if (bitmap == 0) { DeleteDC(dc); throw new Win32Exception(); }
        var previous = SelectObject(dc, bitmap);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            NativeMethods.GetWindowRect(_hwnd, out var r);
            var destination = new NativeMethods.Point { X = r.Left, Y = r.Top };
            var source = new NativeMethods.Point();
            var size = new NativeMethods.Point { X = _size, Y = _size };
            var blend = new Blend { SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(_hwnd, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
                throw new Win32Exception();
            _painted = paint;
        }
        finally { SelectObject(dc, previous); NativeMethods.DeleteObject(bitmap); DeleteDC(dc); }
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = 0;
        UnregisterClass(_className, _instance);
        GC.KeepAlive(_windowProc);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint hwnd, string text);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint dstDc, ref NativeMethods.Point dst, ref NativeMethods.Point size, nint srcDc, ref NativeMethods.Point src, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
