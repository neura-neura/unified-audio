namespace UnifiedAudio.Core;

/// <summary>
/// Centralizes names that will change when the provisional UnifiedAudio branding is replaced.
/// Domain models and protocol names intentionally do not depend on the display name.
/// </summary>
public static class ProductIdentity
{
    public const string InternalName = "UnifiedAudio";
    public const string DisplayName = "UnifiedAudio";
    public const string AppUserModelId = "UnifiedAudio.Desktop";
    public const string SingleInstanceKey = "UnifiedAudio.SingleInstance";
    public const string EnginePipePrefix = "UnifiedAudio.Engine.v1";
    public const string SettingsDirectoryName = "UnifiedAudio";
    public const string SettingsFileName = "settings.json";
}
