using System.Runtime.InteropServices;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;
using UnifiedAudio.Views;

namespace UnifiedAudio.Services;

internal static class OverlaySelfTest
{
    // Opt-in integration test: uses temporary HWNDs only, never saved settings
    // or audio devices. Checks pixels composited by Windows, not just a bitmap.
    public static bool Run()
    {
        NativeMethods.WndProc proc = NativeMethods.DefWindowProc;
        var className = "UnifiedAudio.OverlayTest." + Guid.NewGuid().ToString("N");
        var wc = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = NativeMethods.GetModuleHandle(null), lpszClassName = className,
            hbrBackground = GetStockObject(4)
        };
        NativeMethods.RegisterClassEx(ref wc);
        var background = NativeMethods.CreateWindowEx(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
            className, "Overlay test background", unchecked((int)0x80000000),
            0, 0, 300, 300, 0, 0, NativeMethods.GetModuleHandle(null), 0);
        try
        {
            if (background == 0) throw new InvalidOperationException("Background window failed.");
            using var overlay = new MuteOverlayWindow();
            var settings = new AppSettings
            {
                OverlayScalePercent = 100, OverlayRelativeX = 0.35, OverlayRelativeY = 0.35,
                OverlayMutedColor = "#FF0000", OverlaySpeakingColor = "#00FF00", OverlayIdleColor = "#808080",
                OverlayLocked = true, OverlayOpacityPercent = 100
            };
            overlay.ApplySettings(settings);
            NativeMethods.GetWindowRect(overlay.Handle, out var before);
            settings.OverlayRelativeX = 0.65;
            settings.OverlayRelativeY = 0.65;
            overlay.ApplySettings(settings);
            NativeMethods.GetWindowRect(overlay.Handle, out var after);
            Check(before.Left != after.Left && before.Top != after.Top, "Relative X/Y moves the HWND");
            Check((NativeMethods.GetWindowLongPtr(overlay.Handle, NativeMethods.GwlStyle).ToInt64() & 0x00c40000) == 0,
                "No caption or resize frame");
            Check(NativeMethods.SendMessage(overlay.Handle, NativeMethods.WmNcHitTest, 0, 0).ToInt32() == NativeMethods.HtTransparent,
                "Locked hit-test passes through");
            settings.OverlayLocked = false;
            overlay.ApplySettings(settings);
            Check(NativeMethods.SendMessage(overlay.Handle, NativeMethods.WmNcHitTest, 0, 0).ToInt32() == NativeMethods.HtClient,
                "Unlocked hit-test accepts drag input");

            foreach (var white in new[] { false, true })
            {
                SetClassLongPtr(background, -10, GetStockObject(white ? 0 : 4));
                foreach (var opacity in new[] { 10, 50, 100 })
                {
                    settings.OverlayOpacityPercent = opacity;
                    overlay.ApplySettings(settings);
                    overlay.UpdateState(true, false);
                    NativeMethods.GetWindowRect(overlay.Handle, out var r);
                    NativeMethods.SetWindowPos(background, NativeMethods.HwndTopmost, r.Left - 50, r.Top - 50, 164, 164,
                        NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
                    InvalidateRect(background, 0, true);
                    UpdateWindow(background);
                    overlay.SetVisible(true);
                    Thread.Sleep(150);
                    DwmFlush();
                    var dc = GetDC(0);
                    try
                    {
                        var center = GetPixel(dc, r.Left + 32, r.Top + 32);
                        Console.WriteLine($"Pixel={center:X8}; position={r.Left},{r.Top}; opacity={opacity}; white={white}");
                        var alpha = Math.Round(255 * opacity / 100.0);
                        var unfilled = white ? 255 : 0;
                        var red = white ? 255 : alpha;
                        var other = white ? 255 - alpha : 0;
                        Check(Near(center & 255, red) && Near((center >> 8) & 255, other)
                            && Near((center >> 16) & 255, other), $"Composited alpha {opacity}% on {(white ? "white" : "black")}");
                        foreach (var point in new[] { (2, 2), (32, 5), (5, 32), (59, 32), (32, 59) })
                        {
                            var pixel = GetPixel(dc, r.Left + point.Item1, r.Top + point.Item2);
                            Check(Near(pixel & 255, unfilled) && Near((pixel >> 8) & 255, unfilled)
                                && Near((pixel >> 16) & 255, unfilled), "Outside circle stays transparent");
                        }
                    }
                    finally { ReleaseDC(0, dc); }
                }
            }
            settings.OverlayOpacityPercent = 100;
            overlay.ApplySettings(settings);
            overlay.UpdateState(false, true);
            Thread.Sleep(150);
            DwmFlush();
            var screen = GetDC(0);
            NativeMethods.GetWindowRect(overlay.Handle, out var greenRect);
            var green = GetPixel(screen, greenRect.Left + 32, greenRect.Top + 32);
            ReleaseDC(0, screen);
            Check((green & 0xffffff) == 0x00ff00, "Speaking renders green");
            settings.OverlayScalePercent = 200;
            overlay.ApplySettings(settings);
            NativeMethods.GetWindowRect(overlay.Handle, out var scaled);
            Check(scaled.Right - scaled.Left == 128 && scaled.Bottom - scaled.Top == 128, "Scale remains square");
            Console.WriteLine("Overlay compositor self-test PASS");
            return true;
        }
        catch (Exception ex) { Console.WriteLine(ex); return false; }
        finally
        {
            if (background != 0) NativeMethods.DestroyWindow(background);
            UnregisterClass(className, NativeMethods.GetModuleHandle(null));
            GC.KeepAlive(proc);
        }
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 8;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS: " + label);
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int obj);
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")] private static extern nint SetClassLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
}
