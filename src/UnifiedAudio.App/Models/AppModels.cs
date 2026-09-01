using System.Text.Json.Serialization;

namespace UnifiedAudio.Models;

public enum AudioFlow
{
    Playback,
    Recording
}

public enum AudioRole
{
    Console,
    Multimedia,
    Communications
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum AppLanguage
{
    System,
    English,
    Spanish,
    SimplifiedChinese
}

public enum ProfileIconKind
{
    Desktop,
    Sofa,
    Tv,
    Speaker,
    Headphones,
    Vr
}

public enum DeviceAvailability
{
    Available,
    Disconnected,
    Disabled,
    Unknown
}

public enum InternalMuteState
{
    Preserve,
    Muted,
    Unmuted
}

public enum ProfileRoutingMode
{
    Voice,
    System,
    Both
}

public enum GlobalInputMode
{
    Unconfigured,
    Fixed,
    FollowWindowsDefault
}

public enum ProfileInputMode
{
    FollowGlobal,
    ProfileOverride,
    LegacyPerProfile
}

public enum MuteActionType
{
    Program,
    PowerShell,
    Voicemeeter
}

public enum GlobalMuteHotkeyMode
{
    Toggle,
    PushToTalk,
    PushToMute,
    Hybrid
}

public enum OverlayVisibilityMode
{
    Always,
    MutedOnly,
    UnmutedOnly
}

public enum MuteOsdPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public enum StartupGateMode
{
    None,
    Delay,
    WaitForProcess
}

public enum HotkeyModifierSide
{
    Neutral,
    Left,
    Right
}

public sealed class OverlayMonitorPlacement
{
    public double RelativeX { get; set; } = 0.98;
    public double RelativeY { get; set; } = 0.02;
}

public sealed class MuteActionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public MuteActionType Type { get; set; }
    public bool Enabled { get; set; }
    public bool Trusted { get; set; }
    public bool RunWhenMuted { get; set; } = true;
    public bool RunWhenUnmuted { get; set; } = true;
    public string FileName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string PowerShellScript { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
    public string VoicemeeterParameter { get; set; } = "Strip[0].Mute";
    public float VoicemeeterMutedValue { get; set; } = 1.0f;
    public float VoicemeeterUnmutedValue { get; set; }

    public MuteActionDefinition Clone() => (MuteActionDefinition)MemberwiseClone();
}

public sealed class ProfileApplicationMixRule
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public float Gain { get; set; } = 1.0f;
    public bool Excluded { get; set; }

    public ProfileApplicationMixRule Clone() => (ProfileApplicationMixRule)MemberwiseClone();
}

public sealed class ProfilePluginPresetEntry
{
    public string Id { get; set; } = string.Empty;
    public bool Bypassed { get; set; }
    public string StateBase64 { get; set; } = string.Empty;

    public ProfilePluginPresetEntry Clone() => (ProfilePluginPresetEntry)MemberwiseClone();
}

public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AudioFlow Flow { get; init; }
    public DeviceAvailability Availability { get; init; } = DeviceAvailability.Available;
    public bool IsDefaultConsole { get; init; }
    public bool IsDefaultMultimedia { get; init; }
    public bool IsDefaultCommunications { get; init; }
}

public sealed class SavedDeviceReference
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class HotkeyBinding
{
    public bool Enabled { get; set; }
    public bool Control { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Windows { get; set; }
    public int VirtualKey { get; set; }
    public int MouseButton { get; set; }
    public int ScanCode { get; set; }
    public bool ExtendedKey { get; set; }
    public bool Wildcard { get; set; }
    public HotkeyModifierSide ControlSide { get; set; }
    public HotkeyModifierSide AltSide { get; set; }
    public HotkeyModifierSide ShiftSide { get; set; }

    [JsonIgnore]
    public bool HasKey => VirtualKey > 0 || MouseButton > 0;

    public HotkeyBinding Clone() => new()
    {
        Enabled = Enabled,
        Control = Control,
        Alt = Alt,
        Shift = Shift,
        Windows = Windows,
        VirtualKey = VirtualKey,
        MouseButton = MouseButton,
        ScanCode = ScanCode,
        ExtendedKey = ExtendedKey,
        Wildcard = Wildcard,
        ControlSide = ControlSide,
        AltSide = AltSide,
        ShiftSide = ShiftSide
    };
}

public sealed class AudioProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ProfileIconKind Icon { get; set; } = ProfileIconKind.Speaker;
    public SavedDeviceReference Output { get; set; } = new();
    public SavedDeviceReference Input { get; set; } = new();
    public ProfileInputMode InputMode { get; set; } = ProfileInputMode.FollowGlobal;
    public bool ConfigureWindowsInputDefaults { get; set; }
    public HotkeyBinding Hotkey { get; set; } = new();
    public bool UseAdvancedRoles { get; set; }
    public SavedDeviceReference? OutputConsole { get; set; }
    public SavedDeviceReference? OutputMultimedia { get; set; }
    public SavedDeviceReference? OutputCommunications { get; set; }
    public SavedDeviceReference? InputConsole { get; set; }
    public SavedDeviceReference? InputMultimedia { get; set; }
    public SavedDeviceReference? InputCommunications { get; set; }
    public bool ConfigureInternalPipeline { get; set; }
    public bool UseDefaultInternalInput { get; set; }
    public SavedDeviceReference InternalInput { get; set; } = new();
    public SavedDeviceReference FinalVirtualRender { get; set; } = new();
    public int EngineBufferSize { get; set; }
    public InternalMuteState InternalMute { get; set; } = InternalMuteState.Preserve;
    public bool ConfigureExternalEndpoints { get; set; }
    public bool ExternalMuteAllRecordingDevices { get; set; }
    public List<SavedDeviceReference> ExternalMuteDevices { get; set; } = [];
    public bool ForceExternalMuteState { get; set; }
    public bool ExternalVolumeLockEnabled { get; set; }
    public float ExternalVolumeScalar { get; set; } = 1.0f;
    public bool ConfigurePluginChain { get; set; }
    public List<ProfilePluginPresetEntry> PluginChain { get; set; } = [];
    public bool ConfigureMixer { get; set; }
    public ProfileRoutingMode RoutingMode { get; set; } = ProfileRoutingMode.Voice;
    public bool UseDefaultSystemCapture { get; set; }
    public SavedDeviceReference SystemCapture { get; set; } = new();
    public float VoiceGain { get; set; } = 1.0f;
    public float SystemGain { get; set; } = 1.0f;
    public bool DuckingEnabled { get; set; }
    public float DuckAmount { get; set; } = 0.55f;
    public float DuckThresholdDb { get; set; } = -36.0f;
    public int DuckAttackMs { get; set; } = 20;
    public int DuckHoldMs { get; set; } = 200;
    public int DuckReleaseMs { get; set; } = 280;
    public bool ProcessFilterEnabled { get; set; }
    public bool ProcessFilterExclusionMode { get; set; }
    public List<ProfileApplicationMixRule> ApplicationMixRules { get; set; } = [];
    public List<MuteActionDefinition> MuteActions { get; set; } = [];
    public string LinkedApplication { get; set; } = string.Empty;
    public bool LinkedApplicationForegroundOnly { get; set; } = true;
    public List<string> AutoExitApplications { get; set; } = [];
    public int AfkTimeoutMilliseconds { get; set; }

    public AudioProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        Output = new SavedDeviceReference { Id = Output.Id, Name = Output.Name },
        Input = new SavedDeviceReference { Id = Input.Id, Name = Input.Name },
        InputMode = InputMode,
        ConfigureWindowsInputDefaults = ConfigureWindowsInputDefaults,
        Hotkey = Hotkey.Clone(),
        UseAdvancedRoles = UseAdvancedRoles,
        OutputConsole = CloneRef(OutputConsole),
        OutputMultimedia = CloneRef(OutputMultimedia),
        OutputCommunications = CloneRef(OutputCommunications),
        InputConsole = CloneRef(InputConsole),
        InputMultimedia = CloneRef(InputMultimedia),
        InputCommunications = CloneRef(InputCommunications),
        ConfigureInternalPipeline = ConfigureInternalPipeline,
        UseDefaultInternalInput = UseDefaultInternalInput,
        InternalInput = new SavedDeviceReference { Id = InternalInput.Id, Name = InternalInput.Name },
        FinalVirtualRender = new SavedDeviceReference { Id = FinalVirtualRender.Id, Name = FinalVirtualRender.Name },
        EngineBufferSize = EngineBufferSize,
        InternalMute = InternalMute,
        ConfigureExternalEndpoints = ConfigureExternalEndpoints,
        ExternalMuteAllRecordingDevices = ExternalMuteAllRecordingDevices,
        ExternalMuteDevices = ExternalMuteDevices.Select(reference =>
            new SavedDeviceReference { Id = reference.Id, Name = reference.Name }).ToList(),
        ForceExternalMuteState = ForceExternalMuteState,
        ExternalVolumeLockEnabled = ExternalVolumeLockEnabled,
        ExternalVolumeScalar = ExternalVolumeScalar,
        ConfigurePluginChain = ConfigurePluginChain,
        PluginChain = PluginChain.Select(plugin => plugin.Clone()).ToList(),
        ConfigureMixer = ConfigureMixer,
        RoutingMode = RoutingMode,
        UseDefaultSystemCapture = UseDefaultSystemCapture,
        SystemCapture = new SavedDeviceReference { Id = SystemCapture.Id, Name = SystemCapture.Name },
        VoiceGain = VoiceGain,
        SystemGain = SystemGain,
        DuckingEnabled = DuckingEnabled,
        DuckAmount = DuckAmount,
        DuckThresholdDb = DuckThresholdDb,
        DuckAttackMs = DuckAttackMs,
        DuckHoldMs = DuckHoldMs,
        DuckReleaseMs = DuckReleaseMs,
        ProcessFilterEnabled = ProcessFilterEnabled,
        ProcessFilterExclusionMode = ProcessFilterExclusionMode,
        ApplicationMixRules = ApplicationMixRules.Select(rule => rule.Clone()).ToList(),
        MuteActions = MuteActions.Select(action => action.Clone()).ToList(),
        LinkedApplication = LinkedApplication,
        LinkedApplicationForegroundOnly = LinkedApplicationForegroundOnly,
        AutoExitApplications = AutoExitApplications.ToList(),
        AfkTimeoutMilliseconds = AfkTimeoutMilliseconds
    };

    private static SavedDeviceReference? CloneRef(SavedDeviceReference? source) =>
        source is null ? null : new SavedDeviceReference { Id = source.Id, Name = source.Name };
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public bool StartWithWindows { get; set; }
    public bool KeepRunningInBackground { get; set; } = true;
    public bool RestoreWindowsDefaultsOnExit { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool LaunchMinimized { get; set; }
    public GlobalInputMode GlobalInputMode { get; set; } = GlobalInputMode.Unconfigured;
    public SavedDeviceReference GlobalPhysicalInput { get; set; } = new();
    public StartupGateMode StartupGate { get; set; }
    public int StartupDelaySeconds { get; set; }
    public string StartupWaitProcess { get; set; } = string.Empty;
    public int StartupWaitTimeoutSeconds { get; set; } = 180;
    public bool WriteDetailedLogs { get; set; }
    public bool PlayMuteFeedbackSounds { get; set; } = true;
    public string MuteSoundPath { get; set; } = string.Empty;
    public string UnmuteSoundPath { get; set; } = string.Empty;
    public string PttOnSoundPath { get; set; } = string.Empty;
    public string PttOffSoundPath { get; set; } = string.Empty;
    public int MuteSoundVolumePercent { get; set; } = 100;
    public int UnmuteSoundVolumePercent { get; set; } = 100;
    public int PttOnSoundVolumePercent { get; set; } = 100;
    public int PttOffSoundVolumePercent { get; set; } = 100;
    public string FeedbackOutputDeviceId { get; set; } = string.Empty;
    public string FeedbackOutputDeviceName { get; set; } = string.Empty;
    public bool ShowMuteOsd { get; set; } = true;
    public int MuteOsdDurationMilliseconds { get; set; } = 1000;
    public bool ShowMuteOverlay { get; set; }
    public float OverlayActivityThresholdDb { get; set; } = -36.0f;
    public bool OverlayLocked { get; set; } = true;
    public int OverlayScalePercent { get; set; } = 100;
    public int OverlayOpacityPercent { get; set; } = 100;
    public double OverlayRelativeX { get; set; } = 0.98;
    public double OverlayRelativeY { get; set; } = 0.02;
    public string OverlayMonitorId { get; set; } = string.Empty;
    public Dictionary<string, OverlayMonitorPlacement> OverlayMonitorPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public OverlayVisibilityMode OverlayVisibility { get; set; } = OverlayVisibilityMode.Always;
    public bool ExcludeFullscreenOsd { get; set; }
    public string OsdMonitorId { get; set; } = string.Empty;
    public MuteOsdPosition OsdPosition { get; set; } = MuteOsdPosition.BottomCenter;
    public string MutedOsdText { get; set; } = "Micrófono muteado";
    public string UnmutedOsdText { get; set; } = "Micrófono activo";
    public string MutedOsdColor { get; set; } = "#FF8B0000";
    public string UnmutedOsdColor { get; set; } = "#FF1E90FF";
    public string OverlayMutedColor { get; set; } = "#FF8B0000";
    public string OverlayIdleColor { get; set; } = "#FF696969";
    public string OverlaySpeakingColor { get; set; } = "#FF228B22";
    public List<string> ImportedSourceFingerprints { get; set; } = [];
    public GlobalMuteHotkeyMode MuteHotkeyMode { get; set; } = GlobalMuteHotkeyMode.Toggle;
    public int MuteHotkeyReleaseDelayMilliseconds { get; set; } = 100;
    public int HybridHoldMilliseconds { get; set; } = 200;
    public bool MuteHotkeyPassthrough { get; set; } = true;
    public HotkeyBinding MuteHotkey { get; set; } = new()
    {
        Enabled = true,
        Control = false,
        Alt = false,
        VirtualKey = 0x22,
        ScanCode = 0x51,
        ExtendedKey = false
    };
    public HotkeyBinding MuteOnlyHotkey { get; set; } = new();
    public HotkeyBinding UnmuteOnlyHotkey { get; set; } = new();
    public HotkeyBinding OverlayToggleHotkey { get; set; } = new()
    {
        Enabled = true, Control = true, Alt = true, VirtualKey = 0x78
    };
    public HotkeyBinding OverlayLockHotkey { get; set; } = new()
    {
        Enabled = true, Control = true, Alt = true, VirtualKey = 0x79
    };
    public string? LastActivatedProfileId { get; set; }
    public string? DefaultProfileId { get; set; }
    public int WindowLayoutVersion { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 920;
    public double WindowHeight { get; set; } = 720;
}

public sealed class AppState
{
    public int Version { get; set; } = 8;
    public List<AudioProfile> Profiles { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}

public enum ActivationOutcomeKind
{
    Success,
    Partial,
    Failed
}

public sealed class DeviceSwitchResult
{
    public required SavedDeviceReference Requested { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ProfileActivationResult
{
    public required AudioProfile Profile { get; init; }
    public ActivationOutcomeKind Outcome { get; init; }
    public DeviceSwitchResult Output { get; init; } = new() { Requested = new SavedDeviceReference(), Succeeded = false };
    public DeviceSwitchResult Input { get; init; } = new() { Requested = new SavedDeviceReference(), Succeeded = false };
    public string Summary { get; init; } = string.Empty;
    public bool InternalPipelineApplied { get; init; }
    public bool RolledBack { get; init; }
    public string? InternalPipelineError { get; init; }
}

public readonly record struct RoleAssignment(AudioFlow Flow, AudioRole Role, SavedDeviceReference Device);
