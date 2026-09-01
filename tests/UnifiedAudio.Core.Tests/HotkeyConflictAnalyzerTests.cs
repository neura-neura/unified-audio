using UnifiedAudio.Core.Hotkeys;
using UnifiedAudio.Core.Models;

namespace UnifiedAudio.Core.Tests;

public sealed class HotkeyConflictAnalyzerTests
{
    [Fact]
    public void NumpadPageDownFormatsAndConflicts()
    {
        var first = new HotkeyBinding { Id = "a", Enabled = true, VirtualKey = 0x22, Action = HotkeyAction.ToggleMute };
        var second = new HotkeyBinding { Id = "b", Enabled = true, VirtualKey = 0x22, Action = HotkeyAction.PushToTalk };

        var conflict = Assert.Single(HotkeyConflictAnalyzer.FindInternalConflicts([first, second]));

        Assert.Equal("NumpadPgDn", conflict.Gesture);
    }

    [Fact]
    public void NeutralModifiersIgnoreLeftRightFlags()
    {
        var left = new HotkeyBinding
        {
            Enabled = true,
            VirtualKey = 0x41,
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Left,
            NeutralModifiers = true
        };
        var right = left with { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Right };

        Assert.True(HotkeyConflictAnalyzer.Equivalent(left, right));
    }
}
