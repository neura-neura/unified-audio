using UnifiedAudio.Core.Models;

namespace UnifiedAudio.Core.Hotkeys;

public sealed record HotkeyConflict(string FirstBindingId, string SecondBindingId, string Gesture);

public static class HotkeyConflictAnalyzer
{
    public static IReadOnlyList<HotkeyConflict> FindInternalConflicts(IEnumerable<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var enabled = bindings.Where(binding => binding.Enabled && binding.VirtualKey > 0).ToArray();
        var conflicts = new List<HotkeyConflict>();
        for (var first = 0; first < enabled.Length; first++)
        {
            for (var second = first + 1; second < enabled.Length; second++)
            {
                if (Equivalent(enabled[first], enabled[second]))
                {
                    conflicts.Add(new HotkeyConflict(
                        enabled[first].Id,
                        enabled[second].Id,
                        Format(enabled[first])));
                }
            }
        }

        return conflicts;
    }

    public static bool Equivalent(HotkeyBinding left, HotkeyBinding right)
    {
        var leftModifiers = NormalizeModifiers(left);
        var rightModifiers = NormalizeModifiers(right);
        return left.VirtualKey == right.VirtualKey && leftModifiers == rightModifiers;
    }

    public static string Format(HotkeyBinding binding)
    {
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(VirtualKeyName(binding.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static HotkeyModifiers NormalizeModifiers(HotkeyBinding binding)
    {
        var modifiers = binding.Modifiers;
        if (binding.NeutralModifiers)
        {
            modifiers &= ~(HotkeyModifiers.Left | HotkeyModifiers.Right);
        }

        return modifiers;
    }

    private static string VirtualKeyName(int key) => key switch
    {
        0x22 => "NumpadPgDn",
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        _ => $"VK 0x{key:X2}"
    };
}
