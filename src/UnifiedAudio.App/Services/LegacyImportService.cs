using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedAudio.Models;
using UnifiedAudio.Helpers;

namespace UnifiedAudio.Services;

public enum LegacyImportKind { AudioProfiles, MicMix, AhkMicMute, MicVst }

public sealed record LegacyImportSource(
    LegacyImportKind Kind,
    string Path,
    string Label,
    string Fingerprint,
    bool AlreadyImported)
{
    public string DisplayName => $"{Label} — {Path}{(AlreadyImported ? " (ya importado)" : string.Empty)}";
}

public sealed record LegacyImportPreview(
    LegacyImportSource Source,
    int ProfileCount,
    string Summary,
    IReadOnlyList<string> Warnings);

public sealed record LegacyImportResult(int ProfilesAdded, string Summary, IReadOnlyList<string> Warnings);

public sealed class LegacyImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AudioDeviceService _audio;
    private readonly AppLog _log;

    public LegacyImportService(AudioDeviceService audio, AppLog log)
    {
        _audio = audio;
        _log = log;
    }

    public IReadOnlyList<LegacyImportSource> Discover(AppState state)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new (LegacyImportKind Kind, string Path, string Label)[]
        {
            (LegacyImportKind.AudioProfiles, Path.Combine(local, "AudioProfiles", "settings.json"), "Audio Profiles"),
            (LegacyImportKind.MicMix, Path.Combine(appData, "com.neura.micmix", "settings.json"), "Mic Mix"),
            (LegacyImportKind.MicVst, Path.Combine(appData, "MicVST", "config.xml"), "MicVST"),
            (LegacyImportKind.AhkMicMute, Path.Combine(home, "scoop", "apps", "micmute", "current", "config.json"), "AHK_MicMute")
        };

        var sources = new List<LegacyImportSource>();
        foreach (var candidate in paths)
        {
            var path = ResolveCurrentLinkFallback(candidate.Path);
            if (!File.Exists(path)) continue;
            var fingerprint = Fingerprint(path);
            sources.Add(new LegacyImportSource(candidate.Kind, path, candidate.Label, fingerprint,
                state.Settings.ImportedSourceFingerprints.Contains(fingerprint, StringComparer.OrdinalIgnoreCase)));
        }
        return sources;
    }

    public LegacyImportPreview Preview(LegacyImportSource source)
    {
        var warnings = new List<string>();
        var count = source.Kind switch
        {
            LegacyImportKind.AudioProfiles => CountArray(source.Path, "Profiles"),
            LegacyImportKind.AhkMicMute => CountArray(source.Path, "Profiles"),
            LegacyImportKind.MicMix => 1,
            LegacyImportKind.MicVst => 0,
            _ => 0
        };
        if (source.AlreadyImported) warnings.Add("Esta versión exacta del archivo ya se importó.");
        if (source.Kind == LegacyImportKind.MicMix)
            warnings.Add("Las exclusiones de apps se migrarán al modo 'todas salvo las desmarcadas'.");
        if (source.Kind == LegacyImportKind.AhkMicMute)
            warnings.Add("Acciones y scripts importados permanecen deshabilitados hasta revisión explícita.");
        if (source.Kind == LegacyImportKind.MicVst)
            warnings.Add("Se importa entrada, carpetas y cadena VST; la salida virtual anterior no reemplaza el endpoint final actual.");
        var summary = source.Kind == LegacyImportKind.MicVst
            ? "Configuración global del motor y cadena VST3."
            : $"{count} perfil(es) listos para agregarse sin sobrescribir nombres existentes.";
        return new LegacyImportPreview(source, count, summary, warnings);
    }

    public LegacyImportResult Apply(LegacyImportSource source, AppState state)
    {
        if (source.AlreadyImported
            || state.Settings.ImportedSourceFingerprints.Contains(source.Fingerprint, StringComparer.OrdinalIgnoreCase))
            return new LegacyImportResult(0, "Esta versión exacta ya fue importada.", ["No se hicieron cambios."]);

        var result = source.Kind switch
        {
            LegacyImportKind.AudioProfiles => ImportAudioProfiles(source.Path, state),
            LegacyImportKind.MicMix => ImportMicMix(source.Path, state),
            LegacyImportKind.AhkMicMute => ImportAhk(source.Path, state),
            _ => new LegacyImportResult(0, "MicVST se aplica mediante el motor.", [])
        };
        state.Settings.ImportedSourceFingerprints.Add(source.Fingerprint);
        _log.Info($"Imported {source.Kind} from '{source.Path}'. Profiles={result.ProfilesAdded}.");
        return result;
    }

    private LegacyImportResult ImportAudioProfiles(string path, AppState state)
    {
        var imported = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Audio Profiles settings are empty.");
        var warnings = new List<string>();
        var added = 0;
        foreach (var profile in imported.Profiles ?? [])
        {
            profile.Id = Guid.NewGuid().ToString("N");
            profile.Name = UniqueName(profile.Name, state.Profiles);
            profile.InputMode = ProfileInputMode.LegacyPerProfile;
            profile.ConfigureWindowsInputDefaults = true;
            profile.Hotkey ??= new HotkeyBinding();
            if (HotkeyFormatter.Conflicts(profile.Hotkey, state.Profiles, null))
            {
                profile.Hotkey.Enabled = false;
                warnings.Add($"Hotkey deshabilitada por conflicto: {profile.Name}.");
            }
            state.Profiles.Add(profile);
            added++;
        }
        return new LegacyImportResult(added, $"Se importaron {added} perfiles de Audio Profiles.", warnings);
    }

    private LegacyImportResult ImportMicMix(string path, AppState state)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var inputId = Text(root, "microphoneDeviceId");
        var systemId = Text(root, "systemDeviceId");
        var finalId = Text(root, "virtualOutputDeviceId");
        var mode = Text(root, "mode").ToLowerInvariant() switch
        {
            "system" => ProfileRoutingMode.System,
            "both" => ProfileRoutingMode.Both,
            _ => ProfileRoutingMode.Voice
        };
        var defaults = _audio.GetCurrentDefaults();
        var profile = new AudioProfile
        {
            Name = UniqueName("Mic Mix importado", state.Profiles),
            Output = Reference(AudioFlow.Playback, defaults.OutputId),
            Input = Reference(AudioFlow.Recording, defaults.InputId),
            InputMode = ProfileInputMode.LegacyPerProfile,
            ConfigureWindowsInputDefaults = true,
            ConfigureInternalPipeline = true,
            InternalInput = Reference(AudioFlow.Recording, inputId),
            FinalVirtualRender = Reference(AudioFlow.Playback, finalId),
            ConfigureMixer = true,
            RoutingMode = mode,
            SystemCapture = Reference(AudioFlow.Playback, systemId),
            VoiceGain = Float(root, "voiceVolume", 1.0f),
            SystemGain = Float(root, "systemVolume", 1.0f),
            DuckingEnabled = Bool(root, "duckSystemWhileSpeaking"),
            DuckAmount = Float(root, "duckAmount", 0.55f),
            ProcessFilterEnabled = Bool(root, "appFilterEnabled"),
            ProcessFilterExclusionMode = true
        };
        if (root.TryGetProperty("excludedAppIds", out var excluded) && excluded.ValueKind == JsonValueKind.Array)
            foreach (var item in excluded.EnumerateArray())
            {
                var key = (item.GetString() ?? string.Empty);
                if (key.StartsWith("app:", StringComparison.OrdinalIgnoreCase)) key = key[4..];
                if (string.IsNullOrWhiteSpace(key)) continue;
                profile.ApplicationMixRules.Add(new ProfileApplicationMixRule
                {
                    Key = key,
                    DisplayName = Path.GetFileNameWithoutExtension(key),
                    Gain = 1.0f,
                    Excluded = true
                });
            }
        if (root.TryGetProperty("appVolumes", out var volumes) && volumes.ValueKind == JsonValueKind.Object)
            foreach (var property in volumes.EnumerateObject())
            {
                var key = property.Name.StartsWith("app:", StringComparison.OrdinalIgnoreCase)
                    ? property.Name[4..] : property.Name;
                var existing = profile.ApplicationMixRules.FirstOrDefault(rule =>
                    string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    profile.ApplicationMixRules.Add(new ProfileApplicationMixRule
                    {
                        Key = key, DisplayName = Path.GetFileNameWithoutExtension(key), Gain = property.Value.GetSingle()
                    });
                else existing.Gain = property.Value.GetSingle();
            }
        state.Profiles.Add(profile);
        state.Settings.StartWithWindows |= Bool(root, "startWithWindows");
        state.Settings.LaunchMinimized |= Bool(root, "startMinimized");
        return new LegacyImportResult(1, "Se importó la mezcla, endpoints, ducking y filtros de Mic Mix.", []);
    }

    private LegacyImportResult ImportAhk(string path, AppState state)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var defaults = _audio.GetCurrentDefaults();
        var warnings = new List<string>();
        var added = 0;
        if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            foreach (var item in profiles.EnumerateArray())
            {
                var name = Text(item, "ProfileName");
                var microphoneName = Text(item, "Microphone");
                var microphone = _audio.GetDevices(AudioFlow.Recording).FirstOrDefault(device =>
                    string.Equals(device.Name, microphoneName, StringComparison.OrdinalIgnoreCase));
                var profile = new AudioProfile
                {
                    Name = UniqueName(string.IsNullOrWhiteSpace(name) ? "MicMute importado" : name, state.Profiles),
                    Output = Reference(AudioFlow.Playback, defaults.OutputId),
                    Input = microphone is null
                        ? new SavedDeviceReference { Name = microphoneName }
                        : new SavedDeviceReference { Id = microphone.Id, Name = microphone.Name },
                    InputMode = ProfileInputMode.LegacyPerProfile,
                    ConfigureWindowsInputDefaults = true,
                    LinkedApplication = Text(item, "LinkedApp"),
                    AfkTimeoutMilliseconds = Int(item, "afkTimeout", 0),
                    ConfigureInternalPipeline = microphone is not null,
                    InternalInput = microphone is null
                        ? new SavedDeviceReference { Name = microphoneName }
                        : new SavedDeviceReference { Id = microphone.Id, Name = microphone.Name }
                };
                state.Profiles.Add(profile);
                added++;
            }
        state.Settings.ShowMuteOsd = root.TryGetProperty("SwitchProfileOSD", out var switchOsd) && switchOsd.GetInt32() != 0;
        warnings.Add("Las expresiones AHK de hotkey se conservaron como advertencia: revisa y configura las combinaciones nativas.");
        return new LegacyImportResult(added, $"Se importaron {added} perfiles de AHK_MicMute.", warnings);
    }

    private SavedDeviceReference Reference(AudioFlow flow, string? id)
    {
        var device = _audio.FindDevice(flow, id);
        return device is null
            ? new SavedDeviceReference { Id = id ?? string.Empty, Name = id ?? string.Empty }
            : new SavedDeviceReference { Id = device.Id, Name = device.Name };
    }

    private static string UniqueName(string name, IEnumerable<AudioProfile> existing)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Perfil importado" : name.Trim();
        var candidate = name;
        var suffix = 2;
        var names = existing.Select(profile => profile.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        while (names.Contains(candidate)) candidate = $"{name} (importado {suffix++})";
        return candidate;
    }

    private static int CountArray(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.GetArrayLength() : 0;
    }

    private static string Fingerprint(string path)
    {
        var content = File.ReadAllBytes(path);
        var prefix = Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant() + "\0");
        var data = new byte[prefix.Length + content.Length];
        prefix.CopyTo(data, 0);
        content.CopyTo(data, prefix.Length);
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private static string ResolveCurrentLinkFallback(string path)
    {
        if (File.Exists(path)) return path;
        if (!path.Contains($"{Path.DirectorySeparatorChar}current{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return path;
        var appDirectory = Directory.GetParent(Directory.GetParent(path)!.FullName)!.FullName;
        var file = Path.GetFileName(path);
        return Directory.Exists(appDirectory)
            ? Directory.GetDirectories(appDirectory)
                .Where(directory => !directory.EndsWith("current", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
                .Select(directory => Path.Combine(directory, file))
                .FirstOrDefault(File.Exists) ?? path
            : path;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static float Float(JsonElement element, string name, float fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetSingle(out var result) ? result : fallback;
    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
}
