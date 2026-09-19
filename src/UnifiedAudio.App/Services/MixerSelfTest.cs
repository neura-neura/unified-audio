using UnifiedAudio.Interop;

namespace UnifiedAudio.Services;

internal static class MixerSelfTest
{
    public static async Task<bool> RunAsync()
    {
        var log = new AppLog();
        await using var engine = new EngineClient(log);
        var tone = Path.Combine(Path.GetTempPath(), "UnifiedAudio-test-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var sourceId = MmDeviceNative.GetDefaultId(EDataFlow.eRender, ERole.eMultimedia, log);
            await engine.ConnectAsync();
            await engine.ConfigurePipelineAsync(null, null, null, true);
            var devices = await engine.GetDevicesAsync();
            var source = devices.OutputEndpoints.Single(d => d.Id == sourceId);
            if (source.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test requires a physical default playback endpoint.");
            var sessions = await engine.GetAudioSessionsAsync(source.Name, source.Id);
            var rules = sessions.Sessions.Select(s => new EngineProcessMixRule(s.Key, s.DisplayName, 0.75f, false)).ToArray();
            if (rules.Length == 0) rules = [new EngineProcessMixRule(Environment.ProcessPath!, "Self test", 0.75f, false)];
            var config = new EngineMixerConfiguration(EngineMixerMode.Both, source.Name, source.Id,
                0, 1, false, 0.55f, -36, 20, 200, 280, false, false, rules);
            var snapshot = await engine.ConfigurePipelineAsync(null, config, null, true);
            if (snapshot.MixerMode != 2 || !snapshot.SystemCaptureRunning)
                throw new InvalidOperationException("Both did not start: " + snapshot.FeedbackLoopDescription);
            Console.WriteLine($"Both accepted {rules.Length} typed application rules; source={source.Name}");
            WriteTone(tone);
            if (!NativeMethods.PlaySound(tone, 0, NativeMethods.SndFilename | NativeMethods.SndAsync | NativeMethods.SndNodefault | 8))
                throw new InvalidOperationException("Test tone playback failed.");
            float systemPeak = 0, outputPeak = 0;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100);
                snapshot = await engine.GetSnapshotAsync();
                if (!snapshot.SystemCaptureRunning || snapshot.MixerMode != 2)
                    throw new InvalidOperationException("Both stopped: " + snapshot.FeedbackLoopDescription);
                systemPeak = Math.Max(systemPeak, snapshot.SystemPeak);
                outputPeak = Math.Max(outputPeak, snapshot.OutputPeak);
            }
            Console.WriteLine($"Both signal: system={systemPeak}; final={outputPeak}; voice muted={snapshot.MuteSettled}");
            if (systemPeak <= 0.00001f || outputPeak <= 0.00001f || !snapshot.MuteSettled)
                throw new InvalidOperationException("System audio did not reach final output while voice was muted.");
            // The disabled filter's rules must also survive the typed protocol.
            if (snapshot.ProcessRules.Count != rules.Length || snapshot.ProcessRules.Any(r => r.Gain != 0.75f))
                throw new InvalidOperationException("Application rule round trip failed.");
            foreach (var mode in new[] { EngineMixerMode.Voice, EngineMixerMode.System, EngineMixerMode.Both })
            {
                await engine.ConfigurePipelineAsync(null, config with { Mode = mode }, null, true);
                await Task.Delay(700);
                snapshot = await engine.GetSnapshotAsync();
                if (snapshot.MixerMode != (int)mode) throw new InvalidOperationException($"Mode {mode} rejected");
                if (mode == EngineMixerMode.Voice && snapshot.OutputPeak > 0.00001f)
                    throw new InvalidOperationException("PC audio leaked into Voice mode");
                if (mode != EngineMixerMode.Voice && snapshot.OutputPeak <= 0.00001f)
                    throw new InvalidOperationException($"PC audio missing from {mode}");
                Console.WriteLine($"Mode {mode}: final={snapshot.OutputPeak}, voice muted={snapshot.MuteSettled} PASS");
            }
            var plugins = await engine.GetPluginsAsync();
            foreach (var plugin in plugins.Chain.Where(p => p.HasEditor))
            {
                await engine.OpenPluginEditorAsync(plugin.Index);
                await Task.Delay(300);
                var window = FindWindow(null, plugin.Name);
                if (window == 0 || !IsWindowVisible(window))
                    throw new InvalidOperationException($"Editor is not visible: {plugin.Name}");
                PostMessage(window, 0x0010, 0, 0);
                await Task.Delay(100);
                await engine.OpenPluginEditorAsync(plugin.Index);
                await Task.Delay(100);
                if (!IsWindowVisible(window)) throw new InvalidOperationException($"Editor did not reopen: {plugin.Name}");
                PostMessage(window, 0x0010, 0, 0);
                Console.WriteLine($"Editor visible / close / reopen: {plugin.Name} PASS");
            }
            Console.WriteLine("Mixer client-to-host integration PASS");
            return true;
        }
        catch (Exception ex) { Console.WriteLine(ex); return false; }
        finally
        {
            NativeMethods.PlaySound(null!, 0, 0);
            try { File.Delete(tone); } catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private static void WriteTone(string path)
    {
        const int rate = 48000, frames = 48000;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2);
        writer.Write(rate); writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 4);
        for (int i = 0; i < frames; i++)
        {
            var value = (short)(300 * Math.Sin(2 * Math.PI * 440 * i / rate));
            writer.Write(value); writer.Write(value);
        }
    }
}
