using System.Text;
using System.Security.Cryptography;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;

namespace UnifiedAudio.Services;

internal static class AudioSelfTest
{
    public static bool Run()
    {
        NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
        NativeMethods.CoInitializeEx(nint.Zero, NativeMethods.CoInitApartmentThreaded);

        var log = new AppLog();
        var output = new StringBuilder();
        void Write(string line)
        {
            output.AppendLine(line);
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }
        }

        try
        {
            using var audio = new AudioDeviceService(log);
            var outputs = audio.GetDevices(AudioFlow.Playback).Where(d => d.Availability == DeviceAvailability.Available).ToList();
            var inputs = audio.GetDevices(AudioFlow.Recording).Where(d => d.Availability == DeviceAvailability.Available).ToList();
            Write($"Playback devices: {outputs.Count}");
            foreach (var device in outputs)
            {
                Write($"  OUT {device.Name} [{device.Id}]");
            }

            Write($"Capture devices: {inputs.Count}");
            foreach (var device in inputs)
            {
                Write($"  IN  {device.Name} [{device.Id}]");
            }

            // Read-only Windows Listen probe. This deliberately does not
            // toggle Listen, defaults, volume, or any endpoint state.
            var listenProbe = MmDeviceNative.ProbeListenEndpoints(log);
            Write($"Windows Listen probe: available={listenProbe.Available}; captureEndpoints={listenProbe.EndpointCount}; enabled={listenProbe.EnabledCount}; unknown={listenProbe.UnknownCount}");
            foreach (var endpoint in listenProbe.Endpoints.Where(item => item.Enabled || item.Unknown))
            {
                var target = endpoint.UsesDefault ? "default" : endpoint.TargetId;
                Write($"  LISTEN {endpoint.Name} [{endpoint.Id}] enabled={endpoint.Enabled}; targetKnown={endpoint.TargetKnown}; target={target}; unknown={endpoint.Unknown}");
            }

            var before = audio.GetCurrentDefaults();
            Write($"Current defaults: out={before.OutputId} in={before.InputId}");
            if (outputs.Count == 0 || inputs.Count == 0)
            {
                Write("Need at least one playback device and one capture device.");
                Persist(output, false);
                return false;
            }

            var originalOutput = outputs.FirstOrDefault(d => string.Equals(d.Id, before.OutputId, StringComparison.OrdinalIgnoreCase)) ?? outputs[0];
            var originalInput = inputs.FirstOrDefault(d => string.Equals(d.Id, before.InputId, StringComparison.OrdinalIgnoreCase)) ?? inputs[0];
            var targetOutput = outputs.FirstOrDefault(d => !string.Equals(d.Id, originalOutput.Id, StringComparison.OrdinalIgnoreCase)) ?? originalOutput;
            var targetInput = inputs.FirstOrDefault(d => !string.Equals(d.Id, originalInput.Id, StringComparison.OrdinalIgnoreCase)) ?? originalInput;

            var fallbackInput = inputs.FirstOrDefault(device => !LooksLikeVirtualCable(device.Name));
            var fallbackSystem = outputs.FirstOrDefault(device => !LooksLikeVirtualCable(device.Name));
            var defaultFollowOk = false;
            if (fallbackInput is not null && fallbackSystem is not null)
            {
                var resolverEngine = new EngineClient(log);
                try
                {
                    var resolver = new ProfileActivationService(
                        audio,
                        resolverEngine,
                        new ExternalEndpointMuteService(audio, log),
                        log);
                    var resolverProfile = new AudioProfile
                    {
                        InputMode = ProfileInputMode.LegacyPerProfile,
                        UseDefaultInternalInput = true,
                        InternalInput = new SavedDeviceReference { Id = fallbackInput.Id, Name = fallbackInput.Name },
                        UseDefaultSystemCapture = true,
                        SystemCapture = new SavedDeviceReference { Id = fallbackSystem.Id, Name = fallbackSystem.Name }
                    };
                    var defaultInput = inputs.FirstOrDefault(device => device.Id == before.InputId);
                    var defaultSystem = outputs.FirstOrDefault(device => device.Id == before.OutputId);
                    var expectedInput = defaultInput is not null && !LooksLikeVirtualCable(defaultInput.Name)
                        ? defaultInput : fallbackInput;
                    var expectedSystem = defaultSystem is not null && !LooksLikeVirtualCable(defaultSystem.Name)
                        ? defaultSystem : fallbackSystem;
                    defaultFollowOk = resolver.ResolveInternalInputDevice(resolverProfile)?.Id == expectedInput.Id
                                      && resolver.ResolveSystemCaptureDevice(resolverProfile)?.Id == expectedSystem.Id;
                    Write($"Default-follow physical fallback: {defaultFollowOk}; input={expectedInput.Name}; system={expectedSystem.Name}");
                }
                finally
                {
                    resolverEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            var originalEndpointState = MmDeviceNative.GetEndpointVolumeState(originalInput.Id, log);
            var switched = false;
            var restoredOk = false;
            var externalMuteChanged = false;
            var externalMuteRestored = false;
            var hashParserOk = UpdateService.ParseExpectedSha256(
                                   new string('A', 64) + " *UnifiedAudioSetup-0.1.0-x64.exe",
                                   "UnifiedAudioSetup-0.1.0-x64.exe") == new string('A', 64)
                               && UpdateService.ParseExpectedSha256(
                                   new string('B', 64) + " *different.exe",
                                   "UnifiedAudioSetup-0.1.0-x64.exe") is null;
            Write($"Update SHA-256 manifest parser: {hashParserOk}");
            var importer = new LegacyImportService(audio, log);
            var importPreviewOk = true;
            var importSources = importer.Discover(new AppState());
            foreach (var source in importSources)
            {
                var beforeBytes = File.ReadAllBytes(source.Path);
                var beforeHash = SHA256.HashData(beforeBytes);
                var beforeTime = File.GetLastWriteTimeUtc(source.Path);
                var preview = importer.Preview(source);
                var afterHash = SHA256.HashData(File.ReadAllBytes(source.Path));
                var unchanged = CryptographicOperations.FixedTimeEquals(beforeHash, afterHash)
                                && beforeTime == File.GetLastWriteTimeUtc(source.Path)
                                && !string.IsNullOrWhiteSpace(preview.Summary);
                importPreviewOk &= unchanged;
                Write($"Legacy preview {source.Kind}: unchanged={unchanged}; profiles={preview.ProfileCount}; warnings={preview.Warnings.Count}");
            }
            importPreviewOk &= importSources.Count > 0;
            try
            {
                var switchOut = audio.SetDefaultDevice(new SavedDeviceReference { Id = targetOutput.Id, Name = targetOutput.Name }, AudioFlow.Playback);
                var switchIn = audio.SetDefaultDevice(new SavedDeviceReference { Id = targetInput.Id, Name = targetInput.Name }, AudioFlow.Recording);
                var after = audio.GetCurrentDefaults();
                Write($"Switch output {targetOutput.Name}: {switchOut.Succeeded}");
                Write($"Switch input {targetInput.Name}: {switchIn.Succeeded}");
                Write($"Defaults after switch: out={after.OutputId} in={after.InputId}");
                switched = switchOut.Succeeded && switchIn.Succeeded &&
                           string.Equals(after.OutputId, targetOutput.Id, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(after.InputId, targetInput.Id, StringComparison.OrdinalIgnoreCase);

                if (originalEndpointState.HasValue)
                {
                    var testMute = !originalEndpointState.Value.Muted;
                    MmDeviceNative.SetEndpointVolumeState(originalInput.Id, testMute, null, log);
                    var changed = MmDeviceNative.GetEndpointVolumeState(originalInput.Id, log);
                    externalMuteChanged = changed?.Muted == testMute;
                    Write($"External endpoint mute toggle {originalInput.Name}: {externalMuteChanged}");
                }
                else
                {
                    Write($"External endpoint state unavailable for {originalInput.Name}.");
                }
            }
            finally
            {
                var restoreOut = audio.SetDefaultDevice(new SavedDeviceReference { Id = originalOutput.Id, Name = originalOutput.Name }, AudioFlow.Playback);
                var restoreIn = audio.SetDefaultDevice(new SavedDeviceReference { Id = originalInput.Id, Name = originalInput.Name }, AudioFlow.Recording);
                var restored = audio.GetCurrentDefaults();
                Write($"Restore output {originalOutput.Name}: {restoreOut.Succeeded}");
                Write($"Restore input {originalInput.Name}: {restoreIn.Succeeded}");
                Write($"Defaults after restore: out={restored.OutputId} in={restored.InputId}");
                restoredOk = restoreOut.Succeeded && restoreIn.Succeeded &&
                             string.Equals(restored.OutputId, originalOutput.Id, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(restored.InputId, originalInput.Id, StringComparison.OrdinalIgnoreCase);

                if (originalEndpointState.HasValue)
                {
                    MmDeviceNative.SetEndpointVolumeState(
                        originalInput.Id,
                        originalEndpointState.Value.Muted,
                        originalEndpointState.Value.VolumeScalar,
                        log);
                    var endpointRestored = MmDeviceNative.GetEndpointVolumeState(originalInput.Id, log);
                    externalMuteRestored = endpointRestored.HasValue
                                           && endpointRestored.Value.Muted == originalEndpointState.Value.Muted
                                           && Math.Abs(endpointRestored.Value.VolumeScalar - originalEndpointState.Value.VolumeScalar) < 0.005f;
                    Write($"Restore external endpoint mute/volume {originalInput.Name}: {externalMuteRestored}");
                }
            }

            var pass = switched && restoredOk && externalMuteChanged && externalMuteRestored
                       && hashParserOk && importPreviewOk && defaultFollowOk;
            Write(pass ? "SELFTEST PASS" : "SELFTEST FAIL");
            Persist(output, pass);
            return pass;
        }
        catch (Exception ex)
        {
            Write(ex.ToString());
            Persist(output, false);
            return false;
        }
    }

    private static bool LooksLikeVirtualCable(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private static void Persist(StringBuilder output, bool pass)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "self-test");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "result.txt"), output + (pass ? "PASS" : "FAIL") + Environment.NewLine);
        }
        catch
        {
        }
    }
}
