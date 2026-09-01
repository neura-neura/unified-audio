namespace UnifiedAudio.Core.Hotkeys;

public enum MuteKeyMode
{
    Toggle,
    PushToTalk,
    PushToMute,
    Hybrid
}

public enum MuteKeyCommand
{
    None,
    Toggle,
    Mute,
    Unmute,
    RestorePrevious
}

public static class MuteHotkeyStateMachine
{
    public static MuteKeyCommand OnKeyDown(MuteKeyMode mode) => mode switch
    {
        MuteKeyMode.PushToTalk => MuteKeyCommand.Unmute,
        MuteKeyMode.PushToMute => MuteKeyCommand.Mute,
        _ => MuteKeyCommand.Toggle
    };

    public static MuteKeyCommand OnKeyUp(MuteKeyMode mode, long heldMilliseconds, int hybridThresholdMilliseconds) =>
        mode switch
        {
            MuteKeyMode.PushToTalk => MuteKeyCommand.Mute,
            MuteKeyMode.PushToMute => MuteKeyCommand.Unmute,
            MuteKeyMode.Hybrid when heldMilliseconds >= hybridThresholdMilliseconds => MuteKeyCommand.RestorePrevious,
            _ => MuteKeyCommand.None
        };
}
