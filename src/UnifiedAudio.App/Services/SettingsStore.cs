using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedAudio.Core.Persistence;
using UnifiedAudio.Models;

namespace UnifiedAudio.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly AppLog _log;
    private readonly string _directory;
    private readonly string _filePath;
    private readonly string _backupPath;

    public SettingsStore(AppLog log)
    {
        _log = log;
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnifiedAudio");
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "settings.json");
        _backupPath = Path.Combine(_directory, "settings.bak.json");
    }

    public string FilePath => _filePath;

    public AppState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _log.Info("No settings file found. Creating a new configuration.");
                var created = new AppState();
                Save(created);
                return created;
            }

            var json = File.ReadAllText(_filePath);
            var state = DeserializeState(json);
            Normalize(state);
            return state;
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load settings.json. Trying backup.", ex);
            try
            {
                if (File.Exists(_backupPath))
                {
                    var json = File.ReadAllText(_backupPath);
                    var state = DeserializeState(json);
                    Normalize(state);
                    _log.Warn("Recovered settings from backup.");
                    return state;
                }
            }
            catch (Exception backupEx)
            {
                _log.Error("Failed to load settings backup.", backupEx);
            }

            var fallback = new AppState();
            try
            {
                Save(fallback);
            }
            catch (Exception saveEx)
            {
                _log.Error("Failed to write a replacement settings file.", saveEx);
            }

            return fallback;
        }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(state, Options);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(_filePath))
        {
            File.Copy(_filePath, _backupPath, overwrite: true);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static AppState DeserializeState(string json)
    {
        var state = JsonSerializer.Deserialize<AppState>(json, Options)
                    ?? throw new InvalidDataException("Settings file deserialized to null.");
        MigrateLegacyGlobalInputMode(json, state);
        return state;
    }

    private static void MigrateLegacyGlobalInputMode(string json, AppState state)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return;

        var settingsElement = FindProperty(document.RootElement, "Settings");
        if (settingsElement is null || settingsElement.Value.ValueKind != JsonValueKind.Object)
            return;

        // When both names are present, the canonical v8 property wins. This
        // keeps a partially-written migration from being overwritten by the
        // compatibility field.
        if (FindProperty(settingsElement.Value, "GlobalInputMode") is not null)
            return;

        var legacyElement = FindProperty(settingsElement.Value, "GlobalPhysicalInputMode");
        if (legacyElement is not null && TryParseGlobalInputMode(legacyElement.Value, out var mode))
        {
            state.Settings ??= new AppSettings();
            state.Settings.GlobalInputMode = mode;
        }
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static bool TryParseGlobalInputMode(JsonElement value, out GlobalInputMode mode)
    {
        if (value.ValueKind == JsonValueKind.String
            && Enum.TryParse(value.GetString(), ignoreCase: true, out mode)
            && Enum.IsDefined(mode))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(GlobalInputMode), numeric))
        {
            mode = (GlobalInputMode)numeric;
            return true;
        }

        mode = GlobalInputMode.Unconfigured;
        return false;
    }

    private static void Normalize(AppState state)
    {
        var migrateProfileInputPolicy = state.Version < 8;
        state.Profiles ??= [];
        state.Settings ??= new AppSettings();
        if (string.IsNullOrWhiteSpace(state.Settings.MuteSoundPath))
            state.Settings.MuteSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "mute.wav");
        if (string.IsNullOrWhiteSpace(state.Settings.UnmuteSoundPath))
            state.Settings.UnmuteSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "unmute.wav");
        if (string.IsNullOrWhiteSpace(state.Settings.PttOnSoundPath))
            state.Settings.PttOnSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "ptt_on.wav");
        if (string.IsNullOrWhiteSpace(state.Settings.PttOffSoundPath))
            state.Settings.PttOffSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "ptt_off.wav");
        state.Settings.GlobalPhysicalInput ??= new SavedDeviceReference();
        state.Settings.MuteOnlyHotkey ??= new HotkeyBinding();
        state.Settings.MuteHotkey ??= new HotkeyBinding
        {
            Enabled = true,
            Control = false,
            Alt = false,
            VirtualKey = 0x22,
            ScanCode = 0x51,
            ExtendedKey = false
        };
        if (state.Version < 6
            && state.Settings.MuteHotkey.VirtualKey == 0x22
            && state.Settings.MuteHotkey.ScanCode == 0x51)
        {
            state.Settings.MuteHotkey.Control = false;
            state.Settings.MuteHotkey.Alt = false;
        }
        state.Settings.UnmuteOnlyHotkey ??= new HotkeyBinding();
        state.Settings.OverlayToggleHotkey ??= new HotkeyBinding
            { Enabled = true, Control = true, Alt = true, VirtualKey = 0x78 };
        state.Settings.OverlayLockHotkey ??= new HotkeyBinding
            { Enabled = true, Control = true, Alt = true, VirtualKey = 0x79 };
        state.Settings.ImportedSourceFingerprints ??= [];
        state.Settings.OverlayMonitorPlacements = state.Settings.OverlayMonitorPlacements is null
            ? new Dictionary<string, OverlayMonitorPlacement>(StringComparer.OrdinalIgnoreCase)
            : CaseInsensitiveDictionaryNormalizer.Normalize(
                state.Settings.OverlayMonitorPlacements,
                value => value is not null,
                value => new OverlayMonitorPlacement
                {
                    RelativeX = double.IsFinite(value.RelativeX)
                        ? Math.Clamp(value.RelativeX, 0.0, 1.0)
                        : 0.98,
                    RelativeY = double.IsFinite(value.RelativeY)
                        ? Math.Clamp(value.RelativeY, 0.0, 1.0)
                        : 0.02
                });
        state.Settings.MuteSoundVolumePercent = Math.Clamp(state.Settings.MuteSoundVolumePercent, 0, 100);
        state.Settings.UnmuteSoundVolumePercent = Math.Clamp(state.Settings.UnmuteSoundVolumePercent, 0, 100);
        state.Settings.PttOnSoundVolumePercent = Math.Clamp(state.Settings.PttOnSoundVolumePercent, 0, 100);
        state.Settings.PttOffSoundVolumePercent = Math.Clamp(state.Settings.PttOffSoundVolumePercent, 0, 100);
        state.Settings.OverlayScalePercent = Math.Clamp(state.Settings.OverlayScalePercent, 50, 300);
        state.Settings.OverlayOpacityPercent = Math.Clamp(state.Settings.OverlayOpacityPercent, 10, 100);
        state.Settings.OverlayRelativeX = double.IsFinite(state.Settings.OverlayRelativeX)
            ? Math.Clamp(state.Settings.OverlayRelativeX, 0.0, 1.0)
            : 0.98;
        state.Settings.OverlayRelativeY = double.IsFinite(state.Settings.OverlayRelativeY)
            ? Math.Clamp(state.Settings.OverlayRelativeY, 0.0, 1.0)
            : 0.02;
        foreach (var profile in state.Profiles)
        {
            profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id;
            profile.Name ??= string.Empty;
            profile.Output ??= new SavedDeviceReference();
            profile.Input ??= new SavedDeviceReference();
            profile.Hotkey ??= new HotkeyBinding();
            profile.InternalInput ??= new SavedDeviceReference();
            profile.FinalVirtualRender ??= new SavedDeviceReference();
            profile.SystemCapture ??= new SavedDeviceReference();
            profile.MuteActions ??= [];
            profile.ApplicationMixRules ??= [];
            profile.PluginChain ??= [];
            profile.ExternalMuteDevices ??= [];
            profile.AutoExitApplications ??= [];
            if (migrateProfileInputPolicy)
            {
                // Versions through 7 had no global-input mode. Preserve their
                // Windows capture assignment and internal input exactly.
                profile.InputMode = ProfileInputMode.LegacyPerProfile;
                profile.ConfigureWindowsInputDefaults = true;
            }

            // FollowGlobal owns the engine capture input and deliberately does
            // not change Windows' Recording defaults, even if an older UI or
            // hand-edited JSON left this flag enabled.
            if (profile.InputMode == ProfileInputMode.FollowGlobal)
                profile.ConfigureWindowsInputDefaults = false;
        }
        state.Version = Math.Max(state.Version, 8);
    }
}
