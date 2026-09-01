using UnifiedAudio.Core.Hotkeys;

namespace UnifiedAudio.Core.Tests;

public sealed class MuteHotkeyStateMachineTests
{
    [Theory]
    [InlineData(MuteKeyMode.Toggle, MuteKeyCommand.Toggle, MuteKeyCommand.None)]
    [InlineData(MuteKeyMode.PushToTalk, MuteKeyCommand.Unmute, MuteKeyCommand.Mute)]
    [InlineData(MuteKeyMode.PushToMute, MuteKeyCommand.Mute, MuteKeyCommand.Unmute)]
    public void ModesMapPressAndRelease(MuteKeyMode mode, MuteKeyCommand down, MuteKeyCommand up)
    {
        Assert.Equal(down, MuteHotkeyStateMachine.OnKeyDown(mode));
        Assert.Equal(up, MuteHotkeyStateMachine.OnKeyUp(mode, 300, 200));
    }

    [Fact]
    public void HybridShortPressKeepsToggleAndLongPressRestoresPrevious()
    {
        Assert.Equal(MuteKeyCommand.Toggle, MuteHotkeyStateMachine.OnKeyDown(MuteKeyMode.Hybrid));
        Assert.Equal(MuteKeyCommand.None, MuteHotkeyStateMachine.OnKeyUp(MuteKeyMode.Hybrid, 199, 200));
        Assert.Equal(MuteKeyCommand.RestorePrevious, MuteHotkeyStateMachine.OnKeyUp(MuteKeyMode.Hybrid, 200, 200));
    }
}
