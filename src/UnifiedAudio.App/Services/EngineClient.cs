using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using UnifiedAudio.Core.Engine;

namespace UnifiedAudio.Services;

public sealed record EngineSnapshot(
    bool Running,
    bool MutedRequested,
    bool MuteSettled,
    bool Scanning,
    float InputRms,
    float InputPeak,
    float VoiceRms,
    float VoicePeak,
    float OutputRms,
    float OutputPeak,
    IReadOnlyList<float> Spectrum,
    float SystemRms,
    float SystemPeak,
    string InputDeviceName,
    string OutputDeviceName,
    string InputDeviceId,
    string OutputDeviceId,
    double CaptureSampleRate,
    double GraphSampleRate,
    int BufferSize,
    long QueuedCaptureFrames,
    long Underruns,
    long Overruns,
    int MixerMode,
    float VoiceGain,
    float SystemGain,
    float DuckingGain,
    bool DuckingEnabled,
    float DuckAmount,
    float DuckThresholdDb,
    int DuckAttackMs,
    int DuckHoldMs,
    int DuckReleaseMs,
    bool SystemCaptureRunning,
    string SystemCaptureDeviceName,
    string SystemCaptureDeviceId,
    bool ProcessFilterEnabled,
    bool ProcessFilterExclusionMode,
    int ActiveProcessCaptureCount,
    int ConfiguredProcessRuleCount,
    IReadOnlyList<EngineProcessMixRule> ProcessRules,
    int PluginCount,
    int SkippedPluginCount,
    bool FeedbackLoopDetected = false,
    bool FeedbackLoopGuarded = false,
    bool ListenRouteActive = false,
    string FeedbackLoopDescription = "",
    bool FeedbackProbeAvailable = false,
    bool FeedbackProbeUnknown = true,
    bool FinalCaptureConsumerProbeAvailable = false,
    bool FinalCaptureConsumerDetected = false,
    string FinalCaptureEndpointId = "",
    string FinalCaptureEndpointName = "",
    string FinalCaptureConsumerSummary = "");

public sealed record EngineEndpointDescriptor(string Id, string Name);
public sealed record EngineDevices(
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<EngineEndpointDescriptor> InputEndpoints,
    IReadOnlyList<EngineEndpointDescriptor> OutputEndpoints);
public enum EngineMixerMode { Voice, System, Both }
public enum EngineProcessFilterMode { IncludeSelected, ExcludeSelected }
public sealed record EngineAudioSession(
    string Key,
    long ProcessId,
    string DisplayName,
    string ProcessName,
    string ExecutablePath,
    float Peak,
    float SessionVolume,
    bool SessionMuted,
    bool Active,
    bool Included,
    bool Excluded,
    float ConfiguredGain);
public sealed record EngineAudioSessions(IReadOnlyList<EngineAudioSession> Sessions);
public sealed record EngineProcessMixRule(string Key, string DisplayName, float Gain, bool Excluded = false);
public sealed record EngineDeviceConfiguration(
    string InputName,
    string OutputName,
    int BufferSize,
    string InputId,
    string OutputId);
public sealed record EngineMixerConfiguration(
    EngineMixerMode Mode,
    string SystemDeviceName,
    string SystemDeviceId,
    float VoiceGain,
    float SystemGain,
    bool DuckingEnabled,
    float DuckAmount,
    float VoiceThresholdDb,
    int AttackMs,
    int HoldMs,
    int ReleaseMs,
    bool ProcessFilterEnabled,
    bool ProcessFilterExclusionMode,
    IReadOnlyCollection<EngineProcessMixRule> Rules);
public sealed record EnginePluginDescriptor(string Id, string Name, string Manufacturer, string Category)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Manufacturer) ? Name : $"{Manufacturer} — {Name}";
}
public sealed record EnginePluginEntry(
    int Index,
    string Id,
    string Name,
    string Manufacturer,
    bool Bypassed,
    bool HasEditor,
    int LatencySamples)
{
    public string DisplayName => $"{Index + 1}. {Name}{(Bypassed ? " (bypass)" : string.Empty)} — {LatencySamples} samples";
}
public sealed record EnginePluginInventory(
    IReadOnlyList<EnginePluginDescriptor> Catalog,
    IReadOnlyList<EnginePluginEntry> Chain);
public sealed record EnginePluginFolders(IReadOnlyList<string> Folders);
public sealed record EnginePluginState(string Id, bool Bypassed, string StateBase64);
public sealed record EngineProfileState(IReadOnlyList<EnginePluginState> Plugins);
public sealed record MicVstImportResult(
    EngineSnapshot Snapshot,
    int ImportedPluginCount,
    string InputDeviceName,
    string KeptOutputDeviceName);
public sealed record EngineUnexpectedExit(
    int ExitCode,
    string LastCommand,
    int RecentExitCount,
    TimeSpan RetryAfter,
    bool CircuitOpen);

public sealed class EngineClient : IAsyncDisposable
{
    private readonly AppLog _log;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private Process? _process;
    private long _sequence;
    private bool _disposed;
    private bool _shuttingDown;
    private string _lastCommand = string.Empty;
    private readonly EngineCrashCircuit _crashCircuit = new();

    public EngineClient(AppLog log)
    {
        _log = log;
    }

    public bool IsConnected => _pipe?.IsConnected == true;
    public string? ConnectionError { get; private set; }
    public event EventHandler<EngineUnexpectedExit>? UnexpectedExit;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            var retryAfter = _crashCircuit.RecoveryDelay(DateTimeOffset.UtcNow);
            if (retryAfter > TimeSpan.Zero)
            {
                ConnectionError = $"Engine recovery is waiting {Math.Ceiling(retryAfter.TotalSeconds)} seconds after repeated exits.";
                throw new InvalidOperationException(ConnectionError);
            }

            var executable = Path.Combine(AppContext.BaseDirectory, "UnifiedAudio Engine Host.exe");
            if (!File.Exists(executable))
            {
                ConnectionError = $"Engine executable is missing: {executable}";
                throw new FileNotFoundException(ConnectionError, executable);
            }

            if (_process is not null)
            {
                _process.Exited -= Process_Exited;
                _process.Dispose();
                _process = null;
            }
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (_process is null) throw new InvalidOperationException("The audio engine process could not be started.");
            _process.EnableRaisingEvents = true;
            _process.Exited += Process_Exited;

            _pipe = new NamedPipeClientStream(
                ".",
                "UnifiedAudio.Engine.v1",
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await _pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                var hello = await SendAsync("hello", new { }, timeout.Token).ConfigureAwait(false);
                if (hello.Payload.GetProperty("contractVersion").GetInt32() != EngineContract.MajorVersion)
                {
                    throw new InvalidDataException("Engine contract negotiation failed.");
                }
                ConnectionError = null;
                _log.Info($"Connected to engine {hello.Payload.GetProperty("engineVersion").GetString()}.");
            }
            catch (Exception ex)
            {
                ConnectionError = ex.Message;
                _log.Error("Could not connect to the audio engine.", ex);
                await ResetPipeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<EngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("engine.snapshot", new { }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineDevices> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineDevices>(await SendAsync("devices.list", new { }, cancellationToken).ConfigureAwait(false));

    public Task<EngineAudioSessions> GetAudioSessionsAsync(
        string deviceName,
        CancellationToken cancellationToken = default) =>
        GetAudioSessionsAsync(deviceName, string.Empty, cancellationToken);

    public async Task<EngineAudioSessions> GetAudioSessionsAsync(
        string deviceName,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineAudioSessions>(await SendAsync(
            "audio.sessions.list", new { deviceName, deviceId }, cancellationToken).ConfigureAwait(false));

    public async Task<EnginePluginInventory> GetPluginsAsync(CancellationToken cancellationToken = default) =>
        DeserializePayload<EnginePluginInventory>(await SendAsync("plugins.list", new { }, cancellationToken).ConfigureAwait(false));

    public async Task<EnginePluginFolders> GetPluginFoldersAsync(CancellationToken cancellationToken = default) =>
        DeserializePayload<EnginePluginFolders>(await SendAsync("plugin.folders.list", new { }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> StartAsync(
        string inputName,
        string outputName,
        int bufferSize,
        string inputId,
        string outputId,
        CancellationToken cancellationToken = default,
        bool preserveMixer = false) =>
        await ConfigurePipelineAsync(
            new EngineDeviceConfiguration(inputName, outputName, bufferSize, inputId, outputId),
            null,
            null,
            null,
            cancellationToken,
            resetMixer: !preserveMixer).ConfigureAwait(false);

    public async Task<EngineSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("engine.stop", new { }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> SetMuteAsync(bool muted, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("mute.set", new { muted }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> ToggleMuteAsync(CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("mute.toggle", new { }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> ConfigurePipelineAsync(
        EngineDeviceConfiguration? devices,
        EngineMixerConfiguration? mixer,
        IReadOnlyCollection<EnginePluginState>? plugins,
        bool? muted,
        CancellationToken cancellationToken = default,
        bool resetMixer = false) =>
        DeserializePayload<EngineSnapshot>(await SendAsync(
            "pipeline.configure",
            new
            {
                configureEngine = devices is not null,
                resetMixer,
                inputName = devices?.InputName ?? string.Empty,
                outputName = devices?.OutputName ?? string.Empty,
                bufferSize = devices?.BufferSize ?? 0,
                inputId = devices?.InputId ?? string.Empty,
                outputId = devices?.OutputId ?? string.Empty,
                configureMixer = mixer is not null,
                mode = mixer?.Mode.ToString() ?? EngineMixerMode.Voice.ToString(),
                systemDeviceName = mixer?.SystemDeviceName ?? string.Empty,
                systemDeviceId = mixer?.SystemDeviceId ?? string.Empty,
                voiceGain = mixer?.VoiceGain ?? 1.0f,
                systemGain = mixer?.SystemGain ?? 1.0f,
                duckingEnabled = mixer?.DuckingEnabled ?? false,
                duckAmount = mixer?.DuckAmount ?? 0.55f,
                voiceThresholdDb = mixer?.VoiceThresholdDb ?? -36.0f,
                attackMs = mixer?.AttackMs ?? 20,
                holdMs = mixer?.HoldMs ?? 200,
                releaseMs = mixer?.ReleaseMs ?? 280,
                processFilterEnabled = mixer?.ProcessFilterEnabled ?? false,
                processFilterExclusionMode = mixer?.ProcessFilterExclusionMode ?? false,
                rules = mixer?.Rules ?? Array.Empty<EngineProcessMixRule>(),
                configurePlugins = plugins is not null,
                plugins = plugins ?? Array.Empty<EnginePluginState>(),
                applyMute = muted.HasValue,
                muted = muted.GetValueOrDefault()
            },
            cancellationToken).ConfigureAwait(false));

    public async Task<EngineProfileState> CaptureProfileStateAsync(
        CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineProfileState>(await SendAsync(
            "profile.engine.capture", new { }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> ConfigureMixerAsync(
        EngineMixerMode mode,
        string systemDeviceName,
        string systemDeviceId,
        float voiceGain,
        float systemGain,
        bool duckingEnabled,
        float duckAmount,
        float voiceThresholdDb,
        int attackMs,
        int holdMs,
        int releaseMs,
        CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync(
            "mixer.configure",
            new
            {
                mode = mode.ToString(),
                systemDeviceName,
                systemDeviceId,
                voiceGain,
                systemGain,
                duckingEnabled,
                duckAmount,
                voiceThresholdDb,
                attackMs,
                holdMs,
                releaseMs
            },
            cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> ConfigureProcessFilterAsync(
        bool enabled,
        bool exclusionMode,
        string systemDeviceName,
        string systemDeviceId,
        IReadOnlyCollection<EngineProcessMixRule> rules,
        CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync(
            "mixer.processFilter.configure",
            new { enabled, exclusionMode, systemDeviceName, systemDeviceId, rules },
            cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> ScanAsync(string command, CancellationToken cancellationToken = default)
    {
        if (command is not ("scan.start" or "scan.rescan" or "scan.retry" or "scan.skip"))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        return DeserializePayload<EngineSnapshot>(await SendAsync(command, new { }, cancellationToken).ConfigureAwait(false));
    }

    public async Task<EngineSnapshot> AddPluginAsync(string path, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.add", new { path }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> RemovePluginAsync(int index, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.remove", new { index }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> MovePluginAsync(int from, int to, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.move", new { from, to }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> SetPluginBypassAsync(int index, bool bypassed, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.bypass", new { index, bypassed }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> OpenPluginEditorAsync(int index, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.openEditor", new { index }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> AddPluginFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.folder.add", new { folder }, cancellationToken).ConfigureAwait(false));

    public async Task<EngineSnapshot> RemovePluginFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        DeserializePayload<EngineSnapshot>(await SendAsync("plugin.folder.remove", new { folder }, cancellationToken).ConfigureAwait(false));

    public async Task<MicVstImportResult> ImportMicVstAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        DeserializePayload<MicVstImportResult>(await SendAsync(
            "legacy.micvst.import", new { path }, cancellationToken).ConfigureAwait(false));

    private async Task<EngineMessage> SendAsync(string name, object payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe?.IsConnected != true)
        {
            throw new InvalidOperationException(ConnectionError ?? "The audio engine is not connected.");
        }

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastCommand = name;
            var request = new EngineMessage
            {
                Sequence = Interlocked.Increment(ref _sequence),
                Kind = EngineMessageKind.Command,
                Name = name,
                Payload = JsonSerializer.SerializeToElement(payload)
            };
            var frame = EngineFrameCodec.Encode(request);
            await _pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var header = new byte[sizeof(int)];
            await _pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (bodyLength <= 0 || bodyLength > EngineContract.MaximumFrameBytes)
            {
                throw new InvalidDataException("Engine response length is invalid.");
            }

            var frameResponse = new byte[sizeof(int) + bodyLength];
            header.CopyTo(frameResponse, 0);
            await _pipe.ReadExactlyAsync(frameResponse.AsMemory(sizeof(int), bodyLength), cancellationToken)
                .ConfigureAwait(false);
            var response = EngineFrameCodec.Decode(frameResponse);
            if (!string.Equals(response.MessageId, request.MessageId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Engine response correlation ID does not match the command.");
            }
            if (response.Error is not null)
            {
                throw new InvalidOperationException($"{response.Error.Code}: {response.Error.Message}");
            }
            return response;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static T DeserializePayload<T>(EngineMessage message) where T : class =>
        message.Payload.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException($"Engine response has no {typeof(T).Name} payload.");

    private async Task ResetPipeAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }
    }

    private async void Process_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;
        var exitCode = -1;
        try { exitCode = process.ExitCode; } catch { }
        process.Exited -= Process_Exited;
        if (!ReferenceEquals(_process, process))
        {
            process.Dispose();
            return;
        }
        _process = null;
        process.Dispose();
        await ResetPipeAsync().ConfigureAwait(false);
        if (_disposed || _shuttingDown) return;
        ConnectionError = $"Audio engine exited unexpectedly with code {exitCode}.";
        _log.Error($"{ConnectionError} LastCommand={_lastCommand}.");
        var decision = _crashCircuit.RecordExit(DateTimeOffset.UtcNow);
        UnexpectedExit?.Invoke(this, new EngineUnexpectedExit(
            exitCode,
            _lastCommand,
            decision.RecentExitCount,
            decision.RetryAfter,
            decision.CircuitOpen));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _shuttingDown = true;
            if (IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync("host.shutdown", new { }, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Engine shutdown command failed: {ex.Message}");
        }
        finally
        {
            _disposed = true;
            await ResetPipeAsync().ConfigureAwait(false);
            if (_process is not null)
            {
                _process.Exited -= Process_Exited;
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process.Dispose();
                _process = null;
            }
            _commandGate.Dispose();
            _connectGate.Dispose();
        }
    }
}
