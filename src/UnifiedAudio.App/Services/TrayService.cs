using System.Runtime.InteropServices;
using UnifiedAudio.Helpers;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;

namespace UnifiedAudio.Services;

public sealed class TrayService : IDisposable
{
    private const uint IconId = 1;
    private const uint OpenCommand = 1000;
    private const uint ExitCommand = 1001;
    private const uint ToggleMuteCommand = 1002;
    private const uint ProfileCommandBase = 2000;

    private readonly AppLog _log;
    private NativeMethods.WndProc? _wndProc;
    private nint _hwnd;
    private nint _icon;
    private bool _added;
    private bool _disposed;
    private IReadOnlyList<AudioProfile> _profiles = [];
    private string? _activeProfileId;
    private bool _muted;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? ProfileRequested;
    public event EventHandler? ToggleMuteRequested;

    public TrayService(AppLog log)
    {
        _log = log;
        CreateWindow();
        AddIcon();
    }

    public void Update(IEnumerable<AudioProfile> profiles, string? activeProfileId, bool? muted = null)
    {
        _profiles = profiles.ToList();
        _activeProfileId = activeProfileId;
        if (muted.HasValue) _muted = muted.Value;
        ModifyIcon();
    }

    private void CreateWindow()
    {
        _wndProc = WndProc;
        var className = "UnifiedAudioTrayWindow";
        var wndClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className
        };
        NativeMethods.RegisterClassEx(ref wndClass);
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
            className,
            "UnifiedAudioTray",
            NativeMethods.WsPopup,
            0, 0, 0, 0,
            nint.Zero, nint.Zero, wndClass.hInstance, nint.Zero);
        if (_hwnd == nint.Zero)
        {
            _log.Error("Failed to create the tray message window.");
        }
    }

    private void AddIcon()
    {
        _icon = LoadIcon();
        var data = CreateData();
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
        {
            _log.Error("Failed to add the system tray icon.");
            return;
        }

        _added = true;
    }

    private void ModifyIcon()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref data);
    }

    private NativeMethods.NotifyIconData CreateData()
    {
        var tip = string.IsNullOrWhiteSpace(_activeProfileId)
            ? Loc.Get("AppName")
            : $"{Loc.Get("AppName")} - {(_profiles.FirstOrDefault(p => p.Id == _activeProfileId)?.Name ?? Loc.Get("CustomProfile"))} - {LiteralCatalog.Get(_muted ? "Muteado" : "Activo")}";

        return new NativeMethods.NotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip,
            uCallbackMessage = NativeMethods.WmTrayIcon,
            hIcon = _icon,
            szTip = tip.Length > 127 ? tip[..127] : tip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private nint LoadIcon()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, "Assets", "AppIcon.ico");
            if (File.Exists(path))
            {
                var handle = NativeMethods.LoadImage(nint.Zero, path, NativeMethods.ImageIcon, 16, 16, NativeMethods.LrLoadFromFile);
                if (handle != nint.Zero)
                {
                    return handle;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load the tray icon from disk.", ex);
        }

        return NativeMethods.LoadIcon(nint.Zero, NativeMethods.IdiApplication);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WmTrayIcon)
        {
            var mouseMsg = NativeMethods.LowWord(lParam).ToInt32();
            if (mouseMsg is NativeMethods.WmRButtonUp or NativeMethods.WmContextMenu)
            {
                ShowMenu();
            }
            else if (mouseMsg == NativeMethods.WmLButtonUp)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }

            return nint.Zero;
        }

        if (msg == NativeMethods.WmDestroy)
        {
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MfString | NativeMethods.MfGrayed, 1, Loc.Get("AppName"));
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, string.Empty);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, ToggleMuteCommand,
                LiteralCatalog.Get(_muted ? "Desmutear micrófono" : "Mutear micrófono"));
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, string.Empty);

            var index = 0;
            foreach (var profile in _profiles)
            {
                var flags = NativeMethods.MfString;
                if (profile.Id == _activeProfileId)
                {
                    flags |= NativeMethods.MfChecked;
                }

                NativeMethods.AppendMenu(menu, (uint)flags, ProfileCommandBase + (uint)index, profile.Name);
                index++;
            }

            if (_profiles.Count > 0)
            {
                NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, string.Empty);
            }

            NativeMethods.AppendMenu(menu, NativeMethods.MfString, OpenCommand, Loc.Get("TrayOpen"));
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, ExitCommand, Loc.Get("TrayExit"));

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_hwnd);
            var selected = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmBottomAlign | NativeMethods.TpmReturnCmd,
                point.X,
                point.Y,
                _hwnd,
                nint.Zero);

            if (selected == OpenCommand)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (selected == ExitCommand)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (selected == ToggleMuteCommand)
            {
                ToggleMuteRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (selected >= ProfileCommandBase)
            {
                var profileIndex = (int)(selected - ProfileCommandBase);
                if (profileIndex >= 0 && profileIndex < _profiles.Count)
                {
                    ProfileRequested?.Invoke(this, _profiles[profileIndex].Id);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to show the tray menu.", ex);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
        }

        if (_icon != nint.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = nint.Zero;
        }

        if (_hwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }
}
