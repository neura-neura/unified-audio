using System.Runtime.InteropServices;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;
using UnifiedAudio.Core.Hotkeys;
using AppHotkeyBinding = UnifiedAudio.Models.HotkeyBinding;

namespace UnifiedAudio.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly AppLog _log;
    private readonly Dictionary<int, string> _hotkeyIds = [];
    private IReadOnlyList<AudioProfile> _profiles = [];
    private nint _hwnd;
    private NativeMethods.WndProc? _wndProc;
    private nint _classAtom;
    private nint _keyboardHook;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private nint _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private bool _mainMuteHeld;
    private AppHotkeyBinding _mainMuteBinding = new()
    {
        Enabled = true,
        Control = false,
        Alt = false,
        VirtualKey = 0x22,
        ScanCode = 0x51,
        ExtendedKey = false
    };
    private AppHotkeyBinding _muteOnlyBinding = new();
    private AppHotkeyBinding _unmuteOnlyBinding = new();
    private AppHotkeyBinding _overlayToggleBinding = new();
    private AppHotkeyBinding _overlayLockBinding = new();
    private TaskCompletionSource<AppHotkeyBinding>? _mouseCapture;
    private const int OverlayToggleHotkeyId = 0x7FFA;
    private const int OverlayLockHotkeyId = 0x7FFB;
    private const int MuteOnlyHotkeyId = 0x7FFC;
    private const int UnmuteOnlyHotkeyId = 0x7FFD;
    private bool _disposed;

    public event EventHandler<string>? HotkeyPressed;
    public event EventHandler? MuteTogglePressed;
    public event EventHandler? MuteKeyDown;
    public event EventHandler? MuteKeyUp;
    public event EventHandler? MuteRequested;
    public event EventHandler? UnmuteRequested;
    public event EventHandler? OverlayToggleRequested;
    public event EventHandler? OverlayLockToggleRequested;
    public bool MuteHotkeyPassthrough { get; set; } = true;

    public HotkeyService(AppLog log)
    {
        _log = log;
        CreateMessageWindow();
        InstallKeyboardHook();
        InstallMouseHook();
    }

    public void RegisterProfiles(IEnumerable<AudioProfile> profiles)
    {
        _profiles = profiles.ToList();
        UnregisterAll();
        foreach (var profile in profiles)
        {
            if (!profile.Hotkey.Enabled || !profile.Hotkey.HasKey)
            {
                continue;
            }
            if (profile.Hotkey.MouseButton > 0)
            {
                _log.Warn($"Mouse profile hotkey for '{profile.Name}' is not supported; use the main mute binding.");
                continue;
            }
            if (SameCombination(profile.Hotkey, _mainMuteBinding))
            {
                _log.Warn($"Profile hotkey for '{profile.Name}' conflicts with the main mute hotkey.");
                continue;
            }

            var id = Math.Abs(profile.Id.GetHashCode()) % 0xBFFF + 1;
            while (_hotkeyIds.ContainsKey(id))
            {
                id++;
            }

            var modifiers = NativeMethods.ModNoRepeat;
            if (profile.Hotkey.Alt) modifiers |= NativeMethods.ModAlt;
            if (profile.Hotkey.Control) modifiers |= NativeMethods.ModControl;
            if (profile.Hotkey.Shift) modifiers |= NativeMethods.ModShift;
            if (profile.Hotkey.Windows) modifiers |= NativeMethods.ModWin;

            if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers, (uint)profile.Hotkey.VirtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                _log.Error($"Failed to register hotkey for '{profile.Name}'. Win32={error}");
                continue;
            }

            _hotkeyIds[id] = profile.Id;
        }
    }

    public bool TryRegister(AppHotkeyBinding binding, out string? errorKey)
    {
        errorKey = null;
        if (!binding.Enabled || !binding.HasKey)
        {
            return true;
        }

        if (LooksLikeReserved(binding))
        {
            errorKey = "ShortcutInvalid";
            return false;
        }

        var modifiers = NativeMethods.ModNoRepeat;
        if (binding.Alt) modifiers |= NativeMethods.ModAlt;
        if (binding.Control) modifiers |= NativeMethods.ModControl;
        if (binding.Shift) modifiers |= NativeMethods.ModShift;
        if (binding.Windows) modifiers |= NativeMethods.ModWin;

        UnregisterAll();
        const int probeId = 0x7FFE;
        try
        {
            if (!NativeMethods.RegisterHotKey(_hwnd, probeId, modifiers, (uint)binding.VirtualKey))
            {
                errorKey = "ShortcutInUse";
                return false;
            }

            NativeMethods.UnregisterHotKey(_hwnd, probeId);
            return true;
        }
        finally
        {
            RegisterProfiles(_profiles);
        }
    }

    public static bool LooksLikeReserved(AppHotkeyBinding binding)
    {
        if (!binding.HasKey)
        {
            return false;
        }

        var key = binding.VirtualKey;
        if (key is >= 0x70 and <= 0x7B && !binding.Control && !binding.Alt && !binding.Shift)
        {
            return true;
        }

        return key is 0x5B or 0x5C or 0x5D;
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotkeyIds.Keys)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _hotkeyIds.Clear();
    }

    private void CreateMessageWindow()
    {
        _wndProc = WndProc;
        var className = "UnifiedAudioHotkeyWindow";
        var wndClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className,
            style = NativeMethods.CsHRedraw | NativeMethods.CsVRedraw
        };
        _classAtom = NativeMethods.RegisterClassEx(ref wndClass);
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow,
            className,
            "UnifiedAudioHotkeys",
            NativeMethods.WsPopup,
            0, 0, 0, 0,
            nint.Zero, nint.Zero, wndClass.hInstance, nint.Zero);
        if (_hwnd == nint.Zero)
        {
            _log.Error("Failed to create the hotkey message window.");
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == MuteOnlyHotkeyId)
            {
                MuteRequested?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            }
            if (id == UnmuteOnlyHotkeyId)
            {
                UnmuteRequested?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            }
            if (id == OverlayToggleHotkeyId)
            {
                OverlayToggleRequested?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            }
            if (id == OverlayLockHotkeyId)
            {
                OverlayLockToggleRequested?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            }
            if (_hotkeyIds.TryGetValue(id, out var profileId))
            {
                HotkeyPressed?.Invoke(this, profileId);
            }
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public bool TrySetMainMuteBinding(AppHotkeyBinding binding, out string? errorKey)
    {
        errorKey = null;
        if (binding.Enabled && (!binding.HasKey || binding.VirtualKey > 0 && IsModifierKey(binding.VirtualKey)))
        {
            errorKey = "ShortcutInvalid";
            return false;
        }
        if (SameCombination(binding, _muteOnlyBinding)
            || SameCombination(binding, _unmuteOnlyBinding)
            || SameCombination(binding, _overlayToggleBinding)
            || SameCombination(binding, _overlayLockBinding)
            || _profiles.Any(profile => SameCombination(binding, profile.Hotkey)))
        {
            errorKey = "ShortcutInUse";
            return false;
        }
        _mainMuteBinding = binding.Clone();
        _mainMuteHeld = false;
        return true;
    }

    public async Task<AppHotkeyBinding> CaptureNextMainMouseButtonAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<AppHotkeyBinding>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _mouseCapture, completion, null) is not null)
            throw new InvalidOperationException("A mouse hotkey capture is already active.");
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task;
        }
        finally
        {
            Interlocked.CompareExchange(ref _mouseCapture, null, completion);
        }
    }

    public bool TryRegisterMuteBindings(
        AppHotkeyBinding mute,
        AppHotkeyBinding unmute,
        out string? errorKey)
    {
        errorKey = null;
        if (LooksLikeReserved(mute) || LooksLikeReserved(unmute))
        {
            errorKey = "ShortcutInvalid";
            return false;
        }
        if (SameCombination(mute, unmute)
            || SameCombination(mute, _mainMuteBinding)
            || SameCombination(unmute, _mainMuteBinding)
            || SameCombination(mute, _overlayToggleBinding)
            || SameCombination(mute, _overlayLockBinding)
            || SameCombination(unmute, _overlayToggleBinding)
            || SameCombination(unmute, _overlayLockBinding))
        {
            errorKey = "ShortcutInUse";
            return false;
        }

        var previousMute = _muteOnlyBinding.Clone();
        var previousUnmute = _unmuteOnlyBinding.Clone();
        UnregisterMuteBindings();
        if (!RegisterStateBinding(MuteOnlyHotkeyId, mute)
            || !RegisterStateBinding(UnmuteOnlyHotkeyId, unmute))
        {
            UnregisterMuteBindings();
            RegisterStateBinding(MuteOnlyHotkeyId, previousMute);
            RegisterStateBinding(UnmuteOnlyHotkeyId, previousUnmute);
            _muteOnlyBinding = previousMute;
            _unmuteOnlyBinding = previousUnmute;
            errorKey = "ShortcutInUse";
            return false;
        }

        _muteOnlyBinding = mute.Clone();
        _unmuteOnlyBinding = unmute.Clone();
        return true;
    }

    public bool TryRegisterOverlayBindings(
        AppHotkeyBinding toggle,
        AppHotkeyBinding lockToggle,
        out string? errorKey)
    {
        errorKey = null;
        if (LooksLikeReserved(toggle) || LooksLikeReserved(lockToggle))
        {
            errorKey = "ShortcutInvalid";
            return false;
        }
        if (SameCombination(toggle, lockToggle)
            || SameCombination(toggle, _mainMuteBinding)
            || SameCombination(lockToggle, _mainMuteBinding)
            || SameCombination(toggle, _muteOnlyBinding)
            || SameCombination(toggle, _unmuteOnlyBinding)
            || SameCombination(lockToggle, _muteOnlyBinding)
            || SameCombination(lockToggle, _unmuteOnlyBinding))
        {
            errorKey = "ShortcutInUse";
            return false;
        }

        var previousToggle = _overlayToggleBinding.Clone();
        var previousLock = _overlayLockBinding.Clone();
        UnregisterOverlayBindings();
        if (!RegisterStateBinding(OverlayToggleHotkeyId, toggle)
            || !RegisterStateBinding(OverlayLockHotkeyId, lockToggle))
        {
            UnregisterOverlayBindings();
            RegisterStateBinding(OverlayToggleHotkeyId, previousToggle);
            RegisterStateBinding(OverlayLockHotkeyId, previousLock);
            _overlayToggleBinding = previousToggle;
            _overlayLockBinding = previousLock;
            errorKey = "ShortcutInUse";
            return false;
        }

        _overlayToggleBinding = toggle.Clone();
        _overlayLockBinding = lockToggle.Clone();
        return true;
    }

    private bool RegisterStateBinding(int id, AppHotkeyBinding binding)
    {
        if (!binding.Enabled || !binding.HasKey) return true;
        var modifiers = NativeMethods.ModNoRepeat;
        if (binding.Alt) modifiers |= NativeMethods.ModAlt;
        if (binding.Control) modifiers |= NativeMethods.ModControl;
        if (binding.Shift) modifiers |= NativeMethods.ModShift;
        if (binding.Windows) modifiers |= NativeMethods.ModWin;
        return NativeMethods.RegisterHotKey(_hwnd, id, modifiers, (uint)binding.VirtualKey);
    }

    private void UnregisterMuteBindings()
    {
        NativeMethods.UnregisterHotKey(_hwnd, MuteOnlyHotkeyId);
        NativeMethods.UnregisterHotKey(_hwnd, UnmuteOnlyHotkeyId);
    }

    private void UnregisterOverlayBindings()
    {
        NativeMethods.UnregisterHotKey(_hwnd, OverlayToggleHotkeyId);
        NativeMethods.UnregisterHotKey(_hwnd, OverlayLockHotkeyId);
    }

    private static bool SameCombination(AppHotkeyBinding left, AppHotkeyBinding right) =>
        left.Enabled && right.Enabled && left.HasKey && right.HasKey
        && left.Control == right.Control && left.Alt == right.Alt
        && left.Shift == right.Shift && left.Windows == right.Windows
        && (left.MouseButton > 0 || right.MouseButton > 0
            ? left.MouseButton > 0 && left.MouseButton == right.MouseButton
            : left.VirtualKey == right.VirtualKey);

    private static bool IsModifierKey(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private void InstallKeyboardHook()
    {
        _keyboardProc = KeyboardHookProc;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardProc,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_keyboardHook == nint.Zero)
        {
            _log.Error($"Failed to install the NumpadPgDn hook. Win32={Marshal.GetLastWin32Error()}");
        }
    }

    private void InstallMouseHook()
    {
        _mouseProc = MouseHookProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseProc,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_mouseHook == nint.Zero)
            _log.Error($"Failed to install the mouse hotkey hook. Win32={Marshal.GetLastWin32Error()}");
    }

    private nint KeyboardHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHook>(lParam);
            var message = wParam.ToInt32();
            var release = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            if (MatchesMainMuteBinding(data) || (release && _mainMuteHeld && MatchesMainMuteKey(data)))
            {
                if (message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
                {
                    RaiseMainMuteDown();
                }
                else if (message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
                {
                    RaiseMainMuteUp();
                }
                if (!MuteHotkeyPassthrough)
                {
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint MouseHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MouseLowLevelHook>(lParam);
            var message = wParam.ToInt32();
            var (button, pressed) = MouseButtonFromMessage(message, data.MouseData);
            if (button > 0 && pressed && Interlocked.Exchange(ref _mouseCapture, null) is { } capture)
            {
                capture.TrySetResult(CreateMouseBinding(button));
                return 1;
            }
            if (!_mainMuteBinding.Enabled || _mainMuteBinding.MouseButton <= 0)
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            var released = button > 0 && !pressed;
            var modifiersMatch = ModifierStateMatches(_mainMuteBinding);
            if (button == _mainMuteBinding.MouseButton
                && (modifiersMatch || released && _mainMuteHeld))
            {
                if (pressed) RaiseMainMuteDown(); else RaiseMainMuteUp();
                if (!MuteHotkeyPassthrough) return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private AppHotkeyBinding CreateMouseBinding(int button) => new()
    {
        Enabled = true,
        Control = IsEitherDown(0xA2, 0xA3),
        Alt = IsEitherDown(0xA4, 0xA5),
        Shift = IsEitherDown(0xA0, 0xA1),
        Windows = IsEitherDown(0x5B, 0x5C),
        MouseButton = button,
        Wildcard = _mainMuteBinding.Wildcard,
        ControlSide = _mainMuteBinding.ControlSide,
        AltSide = _mainMuteBinding.AltSide,
        ShiftSide = _mainMuteBinding.ShiftSide
    };

    private static bool IsEitherDown(int leftKey, int rightKey) =>
        (NativeMethods.GetAsyncKeyState(leftKey) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(rightKey) & 0x8000) != 0;

    private static (int Button, bool Pressed) MouseButtonFromMessage(int message, uint mouseData) => message switch
    {
        NativeMethods.WmLButtonDown => (1, true),
        NativeMethods.WmLButtonUp => (1, false),
        NativeMethods.WmRButtonDown => (2, true),
        NativeMethods.WmRButtonUp => (2, false),
        NativeMethods.WmMButtonDown => (3, true),
        NativeMethods.WmMButtonUp => (3, false),
        NativeMethods.WmXButtonDown => (((mouseData >> 16) & 0xFFFF) == 1 ? 4 : 5, true),
        NativeMethods.WmXButtonUp => (((mouseData >> 16) & 0xFFFF) == 1 ? 4 : 5, false),
        _ => (0, false)
    };

    private void RaiseMainMuteDown()
    {
        if (_mainMuteHeld) return;
        _mainMuteHeld = true;
        MuteTogglePressed?.Invoke(this, EventArgs.Empty);
        MuteKeyDown?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseMainMuteUp()
    {
        if (!_mainMuteHeld) return;
        _mainMuteHeld = false;
        MuteKeyUp?.Invoke(this, EventArgs.Empty);
    }

    private bool MatchesMainMuteBinding(NativeMethods.KeyboardLowLevelHook data)
    {
        var binding = _mainMuteBinding;
        if (!MatchesMainMuteKey(data)) return false;

        return ModifierStateMatches(binding);
    }

    private static bool ModifierStateMatches(AppHotkeyBinding binding)
    {
        return ModifierMatches(binding.Control, binding.ControlSide, 0xA2, 0xA3, binding.Wildcard)
            && ModifierMatches(binding.Alt, binding.AltSide, 0xA4, 0xA5, binding.Wildcard)
            && ModifierMatches(binding.Shift, binding.ShiftSide, 0xA0, 0xA1, binding.Wildcard)
            && ModifierMatches(binding.Windows, HotkeyModifierSide.Neutral, 0x5B, 0x5C, binding.Wildcard);
    }

    private bool MatchesMainMuteKey(NativeMethods.KeyboardLowLevelHook data)
    {
        var binding = _mainMuteBinding;
        if (!binding.Enabled || binding.MouseButton > 0 || !binding.HasKey || data.VirtualKey != (uint)binding.VirtualKey) return false;
        return binding.ScanCode <= 0
            || data.ScanCode == (uint)binding.ScanCode
            && ((data.Flags & NativeMethods.LlkhfExtended) != 0) == binding.ExtendedKey;
    }

    private static bool ModifierMatches(bool required, HotkeyModifierSide side, int leftKey, int rightKey, bool wildcard)
    {
        var left = (NativeMethods.GetAsyncKeyState(leftKey) & 0x8000) != 0;
        var right = (NativeMethods.GetAsyncKeyState(rightKey) & 0x8000) != 0;
        if (!required) return wildcard || (!left && !right);
        return side switch
        {
            HotkeyModifierSide.Left => left,
            HotkeyModifierSide.Right => right,
            _ => left || right
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _mouseCapture, null)?.TrySetCanceled();
        UnregisterAll();
        UnregisterMuteBindings();
        UnregisterOverlayBindings();
        if (_keyboardHook != nint.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }
        if (_mouseHook != nint.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
        if (_hwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }
}
