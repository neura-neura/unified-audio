using System.Text.Json.Serialization;

namespace UnifiedAudio.Core.Models;

public enum EndpointFlow
{
    Render,
    Capture
}

public enum EndpointAvailability
{
    Available,
    Disconnected,
    Disabled,
    NotPresent,
    Unknown
}

public enum WindowsAudioRole
{
    Console,
    Multimedia,
    Communications
}

public enum RoutingMode
{
    Voice,
    System,
    Both
}

public enum MuteStartupState
{
    Preserve,
    Muted,
    Unmuted
}

public enum HotkeyAction
{
    ToggleMute,
    Mute,
    Unmute,
    PushToTalk,
    PushToMute,
    ToggleOverlay,
    ToggleOverlayLock,
    ActivateProfile
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
    Left = 16,
    Right = 32
}

public sealed record EndpointReference
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public EndpointFlow Flow { get; init; }

    [JsonIgnore]
    public bool IsAssigned => !string.IsNullOrWhiteSpace(Id);
}

public sealed record RoleEndpointAssignment
{
    public WindowsAudioRole Role { get; init; }
    public EndpointReference Endpoint { get; init; } = new();
}

public sealed record PluginInstanceState
{
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
    public string ClassId { get; init; } = string.Empty;
    public string BundlePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public bool Bypassed { get; init; }
    public byte[] State { get; init; } = [];
}

public sealed record HotkeyBinding
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; init; }
    public HotkeyAction Action { get; init; }
    public HotkeyModifiers Modifiers { get; init; }
    public int VirtualKey { get; init; }
    public bool Passthrough { get; init; }
    public bool Wildcard { get; init; }
    public bool NeutralModifiers { get; init; } = true;
    public bool Hybrid { get; init; }
    public int HybridHoldMilliseconds { get; init; } = 200;
    public int ReleaseDelayMilliseconds { get; init; } = 100;
}

public sealed record DuckingSettings
{
    public bool Enabled { get; init; }
    public float Amount { get; init; } = 0.55f;
    public float VoiceThresholdDb { get; init; } = -36f;
    public int AttackMilliseconds { get; init; } = 20;
    public int HoldMilliseconds { get; init; } = 200;
    public int ReleaseMilliseconds { get; init; } = 280;
}

public sealed record ApplicationMixRule
{
    public string ApplicationId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Excluded { get; init; }
    public float Gain { get; init; } = 1f;
}

public sealed record OverlaySettings
{
    public bool Enabled { get; init; }
    public bool Locked { get; init; } = true;
    public int Size { get; init; } = 48;
    public string VisibilityMode { get; init; } = "Always";
    public bool ShowVoiceActivity { get; init; } = true;
    public float ActivityThresholdDb { get; init; } = -36f;
    public IReadOnlyList<MonitorPlacement> Placements { get; init; } = [];
}

public sealed record MonitorPlacement
{
    public string MonitorId { get; init; } = string.Empty;
    public double RelativeX { get; init; } = 0.96;
    public double RelativeY { get; init; } = 0.05;
}

public sealed record OsdSettings
{
    public bool Enabled { get; init; } = true;
    public bool ExcludeFullscreen { get; init; }
    public int DurationMilliseconds { get; init; } = 1000;
    public string MutedText { get; init; } = "Microphone muted";
    public string UnmutedText { get; init; } = "Microphone on";
    public string Position { get; init; } = "BottomCenter";
}

public sealed record AutomationAction
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool Trusted { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record UnifiedProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = "Audio";
    public IReadOnlyList<RoleEndpointAssignment> WindowsRenderDefaults { get; init; } = [];
    public IReadOnlyList<RoleEndpointAssignment> WindowsCaptureDefaults { get; init; } = [];
    public EndpointReference InternalInput { get; init; } = new() { Flow = EndpointFlow.Capture };
    public EndpointReference SystemCapture { get; init; } = new() { Flow = EndpointFlow.Render };
    public EndpointReference FinalVirtualRender { get; init; } = new() { Flow = EndpointFlow.Render };
    public IReadOnlyList<PluginInstanceState> Plugins { get; init; } = [];
    public MuteStartupState MuteState { get; init; } = MuteStartupState.Preserve;
    public RoutingMode RoutingMode { get; init; } = RoutingMode.Voice;
    public float VoiceGain { get; init; } = 1f;
    public float SystemGain { get; init; } = 1f;
    public DuckingSettings Ducking { get; init; } = new();
    public IReadOnlyList<ApplicationMixRule> ApplicationRules { get; init; } = [];
    public IReadOnlyList<HotkeyBinding> Hotkeys { get; init; } = [];
    public OverlaySettings Overlay { get; init; } = new();
    public OsdSettings Osd { get; init; } = new();
    public IReadOnlyList<AutomationAction> Actions { get; init; } = [];
    public string? LinkedApplication { get; init; }
    public bool LinkedApplicationForegroundOnly { get; init; } = true;
    public IReadOnlyList<string> AutoExitApplications { get; init; } = [];
    public int AfkTimeoutMilliseconds { get; init; }
}

public sealed record UnifiedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<UnifiedProfile> Profiles { get; init; } = [];
    public string? ActiveProfileId { get; init; }
    public string? DefaultProfileId { get; init; }
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool KeepRunningInBackground { get; init; } = true;
    public bool UpdateCheckEnabled { get; init; }
    public string Theme { get; init; } = "System";
    public string Language { get; init; } = "es-ES";
}
