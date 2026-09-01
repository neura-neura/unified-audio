namespace UnifiedAudio.Core.Hotkeys;

public static class PhysicalHotkeyMatcher
{
    public const uint VirtualKeyPageDown = 0x22;
    public const uint NumpadPageDownScanCode = 0x51;

    public static bool IsNumpadPageDown(uint virtualKey, uint scanCode, bool extended) =>
        virtualKey == VirtualKeyPageDown
        && scanCode == NumpadPageDownScanCode
        && !extended;
}
