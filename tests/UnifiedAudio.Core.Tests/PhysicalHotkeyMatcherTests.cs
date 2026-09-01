using UnifiedAudio.Core.Hotkeys;

namespace UnifiedAudio.Core.Tests;

public sealed class PhysicalHotkeyMatcherTests
{
    [Fact]
    public void NumpadPageDown_IsDistinguishedFromExtendedNavigationKey()
    {
        Assert.True(PhysicalHotkeyMatcher.IsNumpadPageDown(0x22, 0x51, extended: false));
        Assert.False(PhysicalHotkeyMatcher.IsNumpadPageDown(0x22, 0x51, extended: true));
    }

    [Fact]
    public void NumpadThreeWithNumLock_IsNotPageDown()
    {
        Assert.False(PhysicalHotkeyMatcher.IsNumpadPageDown(0x63, 0x51, extended: false));
    }
}
