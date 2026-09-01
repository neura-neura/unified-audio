using System.Runtime.InteropServices;
using System.Text;
using UnifiedAudio.Models;
using Windows.System;

namespace UnifiedAudio.Helpers;

public static class HotkeyFormatter
{
    public static string ToDisplay(HotkeyBinding? binding)
    {
        if (binding is null || !binding.Enabled || !binding.HasKey)
        {
            return Loc.Get("NoShortcut");
        }

        var parts = new List<string>();
        if (binding.Wildcard) parts.Add("*");
        if (binding.Control) parts.Add(SidedModifier("Ctrl", binding.ControlSide));
        if (binding.Alt) parts.Add(SidedModifier("Alt", binding.AltSide));
        if (binding.Shift) parts.Add(SidedModifier("Shift", binding.ShiftSide));
        if (binding.Windows) parts.Add("Win");
        parts.Add(binding.MouseButton > 0
            ? MouseButtonName(binding.MouseButton)
            : binding.ScanCode == 0x51 && !binding.ExtendedKey
                ? "NumpadPgDn"
                : VirtualKeyName((VirtualKey)binding.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string MouseButtonName(int button) => button switch
    {
        1 => LiteralCatalog.Get("Mouse izquierdo"),
        2 => LiteralCatalog.Get("Mouse derecho"),
        3 => LiteralCatalog.Get("Mouse central"),
        4 => LiteralCatalog.Get("Mouse X1"),
        5 => LiteralCatalog.Get("Mouse X2"),
        _ => LiteralCatalog.Get("Mouse")
    };

    private static string SidedModifier(string name, HotkeyModifierSide side) => side switch
    {
        HotkeyModifierSide.Left => "L" + name,
        HotkeyModifierSide.Right => "R" + name,
        _ => name
    };

    public static bool TryCreate(VirtualKey key, bool control, bool alt, bool shift, bool windows, out HotkeyBinding binding, out string? errorKey)
    {
        binding = new HotkeyBinding();
        errorKey = null;
        if (IsModifier(key) || key == VirtualKey.None)
        {
            errorKey = "ShortcutInvalid";
            return false;
        }

        if (!control && !alt && !windows)
        {
            errorKey = "ShortcutInvalid";
            return false;
        }

        binding = new HotkeyBinding
        {
            Enabled = true,
            Control = control,
            Alt = alt,
            Shift = shift,
            Windows = windows,
            VirtualKey = (int)key
        };
        return true;
    }

    public static bool Conflicts(HotkeyBinding candidate, IEnumerable<AudioProfile> profiles, string? ignoreProfileId)
    {
        if (!candidate.Enabled || !candidate.HasKey)
        {
            return false;
        }

        return profiles.Any(profile =>
            profile.Id != ignoreProfileId &&
            profile.Hotkey.Enabled &&
            profile.Hotkey.HasKey &&
            profile.Hotkey.Control == candidate.Control &&
            profile.Hotkey.Alt == candidate.Alt &&
            profile.Hotkey.Shift == candidate.Shift &&
            profile.Hotkey.Windows == candidate.Windows &&
            profile.Hotkey.VirtualKey == candidate.VirtualKey);
    }

    public static VirtualKey ResolvePressedKey(VirtualKey key, VirtualKey originalKey)
    {
        var down = GetDownNonModifierKeys();
        if (down.Count == 1)
        {
            return down[0];
        }

        if (down.Count > 1)
        {
            if (down.Contains(originalKey))
            {
                return originalKey;
            }

            if (down.Contains(key))
            {
                return key;
            }

            var oem = down.FirstOrDefault(IsOemOrPunctuation);
            if (oem != VirtualKey.None)
            {
                return oem;
            }

            return down[0];
        }

        if (!IsModifier(originalKey) && originalKey != VirtualKey.None)
        {
            return originalKey;
        }

        return key;
    }

    private static string VirtualKeyName(VirtualKey key)
    {
        if (TryKnownName(key, out var known))
        {
            return known;
        }

        if (TryLayoutCharacter(key, out var character))
        {
            return character;
        }

        if (TryScanCodeName(key, out var scanName))
        {
            return scanName;
        }

        var raw = key.ToString();
        return raw.All(char.IsDigit) ? $"Key {raw}" : raw;
    }

    private static bool TryKnownName(VirtualKey key, out string name)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            name = ((char)('A' + (key - VirtualKey.A))).ToString();
            return true;
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            name = "F" + (key - VirtualKey.F1 + 1);
            return true;
        }

        name = key switch
        {
            VirtualKey.Number0 => "0",
            VirtualKey.Number1 => "1",
            VirtualKey.Number2 => "2",
            VirtualKey.Number3 => "3",
            VirtualKey.Number4 => "4",
            VirtualKey.Number5 => "5",
            VirtualKey.Number6 => "6",
            VirtualKey.Number7 => "7",
            VirtualKey.Number8 => "8",
            VirtualKey.Number9 => "9",
            VirtualKey.NumberPad0 => "Num 0",
            VirtualKey.NumberPad1 => "Num 1",
            VirtualKey.NumberPad2 => "Num 2",
            VirtualKey.NumberPad3 => "Num 3",
            VirtualKey.NumberPad4 => "Num 4",
            VirtualKey.NumberPad5 => "Num 5",
            VirtualKey.NumberPad6 => "Num 6",
            VirtualKey.NumberPad7 => "Num 7",
            VirtualKey.NumberPad8 => "Num 8",
            VirtualKey.NumberPad9 => "Num 9",
            VirtualKey.Multiply => "Num *",
            VirtualKey.Add => "Num +",
            VirtualKey.Subtract => "Num -",
            VirtualKey.Decimal => "Num .",
            VirtualKey.Divide => "Num /",
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Escape => "Esc",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "Page Up",
            VirtualKey.PageDown => "Page Down",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.CapitalLock => "Caps Lock",
            VirtualKey.NumberKeyLock => "Num Lock",
            VirtualKey.Scroll => "Scroll Lock",
            VirtualKey.Print => "Print Screen",
            VirtualKey.Snapshot => "Print Screen",
            VirtualKey.Pause => "Pause",
            VirtualKey.Application => "Menu",
            _ => string.Empty
        };
        return name.Length > 0;
    }

    private static bool TryLayoutCharacter(VirtualKey key, out string name)
    {
        name = string.Empty;
        var vk = (uint)key;
        if (vk is 0 or > 255)
        {
            return false;
        }

        var mapped = MapVirtualKey(vk, MapVkToChar);
        var mappedChar = (char)(mapped & 0xFFFF);
        if (mappedChar != 0 && !char.IsControl(mappedChar) && !char.IsWhiteSpace(mappedChar))
        {
            name = FormatKeyChar(mappedChar);
            return true;
        }

        var scan = MapVirtualKey(vk, MapVkToVsc);
        if (scan == 0)
        {
            return false;
        }

        var keyboardState = new byte[256];
        var buffer = new StringBuilder(8);
        var result = ToUnicode(vk, scan, keyboardState, buffer, buffer.Capacity, 0);
        if (result < 0)
        {
            var flush = new StringBuilder(8);
            ToUnicode(vk, scan, keyboardState, flush, flush.Capacity, 0);
        }

        if (result == 0)
        {
            return false;
        }

        var text = buffer.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var ch = text[0];
        if (char.IsControl(ch) || char.IsWhiteSpace(ch))
        {
            return false;
        }

        name = FormatKeyChar(ch);
        return true;
    }

    private static bool TryScanCodeName(VirtualKey key, out string name)
    {
        name = string.Empty;
        var scan = MapVirtualKey((uint)key, MapVkToVsc);
        if (scan == 0)
        {
            return false;
        }

        var lParam = (int)(scan << 16);
        var buffer = new StringBuilder(64);
        var length = GetKeyNameText(lParam, buffer, buffer.Capacity);
        if (length <= 0)
        {
            return false;
        }

        var text = buffer.ToString().Trim();
        if (string.IsNullOrEmpty(text) || text.All(char.IsDigit))
        {
            return false;
        }

        name = text;
        return true;
    }

    private static string FormatKeyChar(char ch) =>
        char.IsLetter(ch) ? ch.ToString().ToUpperInvariant() : ch.ToString();

    private static List<VirtualKey> GetDownNonModifierKeys()
    {
        var keys = new List<VirtualKey>();
        for (var code = 8; code <= 254; code++)
        {
            var key = (VirtualKey)code;
            if (IsModifier(key) || !IsKeyDown(code))
            {
                continue;
            }

            keys.Add(key);
        }

        return keys;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static bool IsModifier(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsOemOrPunctuation(VirtualKey key)
    {
        var code = (int)key;
        return code is (>= 186 and <= 192) or (>= 219 and <= 223);
    }

    private const uint MapVkToVsc = 0;
    private const uint MapVkToChar = 2;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

public static class ProfileIcons
{
    public static string Glyph(ProfileIconKind icon) => icon switch
    {
        ProfileIconKind.Desktop => "\uE7F8",
        ProfileIconKind.Sofa => "\uE10F",
        ProfileIconKind.Tv => "\uE7F4",
        ProfileIconKind.Speaker => "\uE767",
        ProfileIconKind.Headphones => "\uE7F6",
        ProfileIconKind.Vr => "\uE7FC",
        _ => "\uE767"
    };

    public static string DisplayName(ProfileIconKind icon) => icon switch
    {
        ProfileIconKind.Desktop => Loc.Get("IconDesktop"),
        ProfileIconKind.Sofa => Loc.Get("IconSofa"),
        ProfileIconKind.Tv => Loc.Get("IconTv"),
        ProfileIconKind.Speaker => Loc.Get("IconSpeaker"),
        ProfileIconKind.Headphones => Loc.Get("IconHeadphones"),
        ProfileIconKind.Vr => Loc.Get("IconVr"),
        _ => Loc.Get("IconSpeaker")
    };
}
