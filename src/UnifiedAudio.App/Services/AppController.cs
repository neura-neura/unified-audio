using UnifiedAudio.Helpers;
using UnifiedAudio.Models;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnifiedAudio.Interop;
using UnifiedAudio.Core.Hotkeys;
using UnifiedAudio.Core.Persistence;

namespace UnifiedAudio.Services;

public sealed class AppController : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsStore _store;
    private readonly ProfileActivationService _activation;
    private bool _disposed;
    private readonly DispatcherQueueTimer _automationTimer;
    private bool _automationBusy;
    private string? _linkedProfileActive;
    private string? _profileBeforeLinked;
    private bool _afkMuted;
    private readonly SemaphoreSlim _muteHotkeyGate = new(1, 1);
    private CancellationTokenSource? _muteReleaseCancellation;
    private long _muteKeyDownAt;
    private bool _hybridPreviousMute;
    private readonly IReadOnlyList<RoleAssignment> _startupDefaults;
    private CancellationTokenSource? _hardwareRecoveryCancellation;
    private bool _hardwareRecoveryBusy;
    private CancellationTokenSource? _engineRecoveryCancellation;
    private bool _engineRecoveryBusy;
    private bool _factoryResetInProgress;

    public AppController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Log = new AppLog();
        _store = new SettingsStore(Log);
        State = _store.Load();
        if (!State.Profiles.Any(profile => profile.Id == State.Settings.DefaultProfileId))
            State.Settings.DefaultProfileId = State.Profiles.FirstOrDefault()?.Id;
        Loc.Language = State.Settings.Language;
        Audio = new AudioDeviceService(Log);
        _startupDefaults = CaptureCurrentRoleDefaults();
        ExternalMute = new ExternalEndpointMuteService(Audio, Log);
        Engine = new EngineClient(Log);
        Feedback = new MuteFeedbackService(dispatcher, Engine, State.Settings, Log);
        Feedback.SettingsChanged += (_, _) => Persist();
        Actions = new MuteActionService(Log);
        Importer = new LegacyImportService(Audio, Log);
        _activation = new ProfileActivationService(Audio, Engine, ExternalMute, State.Settings, Log);
        Notifications = new NotificationService(Log);
        Hotkeys = new HotkeyService(Log);
        Hotkeys.MuteHotkeyPassthrough = State.Settings.MuteHotkeyPassthrough;
        if (!Hotkeys.TrySetMainMuteBinding(State.Settings.MuteHotkey, out var mainMuteHotkeyError))
            Log.Warn($"Main mute hotkey was not registered: {mainMuteHotkeyError}");
        if (!Hotkeys.TryRegisterMuteBindings(State.Settings.MuteOnlyHotkey, State.Settings.UnmuteOnlyHotkey, out var muteHotkeyError))
            Log.Warn($"Separate mute/unmute hotkeys were not registered: {muteHotkeyError}");
        if (!Hotkeys.TryRegisterOverlayBindings(State.Settings.OverlayToggleHotkey, State.Settings.OverlayLockHotkey, out var overlayHotkeyError))
            Log.Warn($"Overlay hotkeys were not registered: {overlayHotkeyError}");
        Tray = new TrayService(Log);
        Startup = new StartupService(Log);

        Audio.DevicesChanged += Audio_DevicesChanged;
        Engine.UnexpectedExit += Engine_UnexpectedExit;
        Hotkeys.HotkeyPressed += (_, profileId) => RunOnUi(() => _ = ActivateProfileAsync(profileId));
        Hotkeys.MuteKeyDown += (_, _) => RunOnUi(() => _ = HandleMuteKeyDownAsync());
        Hotkeys.MuteKeyUp += (_, _) => RunOnUi(() => _ = HandleMuteKeyUpAsync());
        Hotkeys.MuteRequested += (_, _) => RunOnUi(() => _ = SetInternalMuteAsync(true, "Mute hotkey"));
        Hotkeys.UnmuteRequested += (_, _) => RunOnUi(() => _ = SetInternalMuteAsync(false, "Unmute hotkey"));
        Hotkeys.OverlayToggleRequested += (_, _) => RunOnUi(Feedback.ToggleOverlayVisibility);
        Hotkeys.OverlayLockToggleRequested += (_, _) => RunOnUi(Feedback.ToggleOverlayLock);
        Tray.OpenRequested += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        Tray.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        Tray.ProfileRequested += (_, profileId) => RunOnUi(() => _ = ActivateProfileAsync(profileId));
        Tray.ToggleMuteRequested += (_, _) => RunOnUi(() => _ = ToggleInternalMuteAsync("Tray"));

        _automationTimer = dispatcher.CreateTimer();
        _automationTimer.Interval = TimeSpan.FromSeconds(1);
        _automationTimer.Tick += AutomationTimer_Tick;
    }

    public AppLog Log { get; }
    public AppState State { get; }
    public AudioDeviceService Audio { get; }
    public NotificationService Notifications { get; }
    public HotkeyService Hotkeys { get; }
    public TrayService Tray { get; }
    public StartupService Startup { get; }
    public EngineClient Engine { get; }
    public ExternalEndpointMuteService ExternalMute { get; }
    public MuteFeedbackService Feedback { get; }
    public MuteActionService Actions { get; }
    public LegacyImportService Importer { get; }

    public event EventHandler? StateChanged;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<EngineSnapshot>? MuteStateChanged;

    public IReadOnlyList<AudioProfile> Profiles => State.Profiles;
    public AppSettings Settings => State.Settings;

    public async Task InitializeAsync()
    {
        Log.Info("Application startup.");
        Log.Verbose = Settings.WriteDetailedLogs;
        Notifications.Initialize();
        await Startup.ApplyAsync(Settings);
        await Startup.WaitForReadyAsync(Settings);
        try
        {
            await Engine.ConnectAsync();
            var active = State.Profiles.FirstOrDefault(profile => profile.Id == Settings.LastActivatedProfileId);
            if (active is not null)
            {
                var snapshot = await Engine.GetSnapshotAsync();
                var externalResult = ExternalMute.ApplyProfile(active, snapshot.MutedRequested);
                if (!externalResult.Succeeded)
                    Log.Warn("External endpoint startup policy was partial: " + string.Join(" | ", externalResult.Errors));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Engine unavailable at startup: {ex.Message}");
        }
        RefreshFromHardware();
        try
        {
            var legacySources = Importer.Discover(State);
            Log.Info($"Legacy import discovery found {legacySources.Count} source(s): "
                + string.Join(", ", legacySources.Select(source => source.Kind)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Legacy import discovery failed: {ex.Message}");
        }
        Hotkeys.RegisterProfiles(State.Profiles);
        Tray.Update(State.Profiles, DetectActiveProfileId());
        _automationTimer.Start();
    }

    public string DetectActiveProfileId()
    {
        var last = State.Profiles.FirstOrDefault(profile => profile.Id == Settings.LastActivatedProfileId);
        if (last is not null && _activation.MatchesCurrentDefaults(last))
        {
            return last.Id;
        }
        var match = State.Profiles.FirstOrDefault(_activation.MatchesCurrentDefaults);
        return match?.Id ?? string.Empty;
    }

    public string CurrentProfileName()
    {
        var id = DetectActiveProfileId();
        return string.IsNullOrEmpty(id)
            ? Loc.Get("CustomProfile")
            : State.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? Loc.Get("CustomProfile");
    }

    public async Task<ProfileActivationResult> ActivateProfileAsync(string profileId)
    {
        var profile = State.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            Log.Warn($"Activation requested for unknown profile '{profileId}'.");
            return new ProfileActivationResult
            {
                Profile = new AudioProfile { Name = Loc.Get("CustomProfile") },
                Outcome = ActivationOutcomeKind.Failed,
                Summary = Loc.Get("FailedTitle")
            };
        }

        var result = await _activation.ActivateAsync(profile);
        if (result.Outcome != ActivationOutcomeKind.Failed)
        {
            Settings.LastActivatedProfileId = profile.Id;
            Persist();
        }

        RefreshFromHardware();
        if (Settings.ShowNotifications)
        {
            Notifications.ShowActivation(result);
        }

        return result;
    }

    public void AddOrUpdateProfile(AudioProfile profile)
    {
        var existing = State.Profiles.FindIndex(p => p.Id == profile.Id);
        if (existing >= 0)
        {
            State.Profiles[existing] = profile;
        }
        else
        {
            State.Profiles.Add(profile);
            Settings.DefaultProfileId ??= profile.Id;
        }

        PersistAndBroadcast();
    }

    public void CommitGlobalPhysicalInput(SavedDeviceReference reference, bool followWindowsDefault)
    {
        Settings.GlobalPhysicalInput = new SavedDeviceReference
        {
            Id = reference.Id,
            Name = reference.Name
        };
        Settings.GlobalInputMode = followWindowsDefault
            ? GlobalInputMode.FollowWindowsDefault
            : GlobalInputMode.Fixed;
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteProfile(string profileId)
    {
        State.Profiles.RemoveAll(p => p.Id == profileId);
        if (Settings.LastActivatedProfileId == profileId)
        {
            Settings.LastActivatedProfileId = null;
        }
        if (Settings.DefaultProfileId == profileId)
            Settings.DefaultProfileId = State.Profiles.FirstOrDefault()?.Id;

        PersistAndBroadcast();
    }

    public void Persist()
    {
        if (_factoryResetInProgress) return;
        try
        {
            _store.Save(State);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save configuration.", ex);
        }
    }

    public void PersistAndBroadcast()
    {
        Persist();
        Hotkeys.RegisterProfiles(State.Profiles);
        Tray.Update(State.Profiles, DetectActiveProfileId());
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshFromHardware()
    {
        Tray.Update(State.Profiles, DetectActiveProfileId());
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Audio_DevicesChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        RefreshFromHardware();
        ScheduleHardwareRecovery();
    });

    private void ScheduleHardwareRecovery()
    {
        _hardwareRecoveryCancellation?.Cancel();
        _hardwareRecoveryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _hardwareRecoveryCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750, cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                    _dispatcher.TryEnqueue(() => _ = RecoverActiveProfileAfterHardwareChangeAsync());
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void Engine_UnexpectedExit(object? sender, EngineUnexpectedExit e) => RunOnUi(() =>
    {
        Log.Warn($"Scheduling engine recovery. Exit={e.ExitCode}; lastCommand={e.LastCommand}; recent={e.RecentExitCount}; circuitOpen={e.CircuitOpen}.");
        _engineRecoveryCancellation?.Cancel();
        _engineRecoveryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _engineRecoveryCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                var delay = e.RetryAfter < TimeSpan.FromMilliseconds(250)
                    ? TimeSpan.FromMilliseconds(250)
                    : e.RetryAfter;
                await Task.Delay(delay, cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                    _dispatcher.TryEnqueue(() => _ = RecoverEngineAfterCrashAsync());
            }
            catch (OperationCanceledException)
            {
            }
        });
    });

    private async Task RecoverEngineAfterCrashAsync()
    {
        if (_engineRecoveryBusy || _disposed) return;
        _engineRecoveryBusy = true;
        try
        {
            var profile = State.Profiles.FirstOrDefault(candidate => candidate.Id == Settings.LastActivatedProfileId);
            if (profile is null)
            {
                await Engine.ConnectAsync();
                Log.Info("Audio engine restarted after an unexpected exit; no active profile required reapplication.");
                return;
            }
            var result = await _activation.ActivateAsync(profile);
            Log.Info(result.Outcome == ActivationOutcomeKind.Failed
                ? $"Engine restarted but profile '{profile.Name}' could not be reapplied: {result.Summary}"
                : $"Engine restarted and profile '{profile.Name}' was reapplied.");
            RefreshFromHardware();
        }
        catch (Exception ex)
        {
            Log.Error("Automatic engine recovery failed.", ex);
        }
        finally
        {
            _engineRecoveryBusy = false;
        }
    }

    public async Task<FactoryResetResult> FactoryResetAsync()
    {
        if (_factoryResetInProgress)
            throw new InvalidOperationException("A factory reset is already in progress.");

        _factoryResetInProgress = true;
        _automationTimer.Stop();
        _hardwareRecoveryCancellation?.Cancel();
        _engineRecoveryCancellation?.Cancel();
        Engine.UnexpectedExit -= Engine_UnexpectedExit;
        try
        {
            await Engine.DisposeAsync();
            var localDirectory = Path.GetDirectoryName(_store.FilePath)
                ?? throw new InvalidOperationException("The settings file has no parent directory.");
            var roamingDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnifiedAudio");
            var reset = new FactoryResetBackup(
                localDirectory,
                roamingDirectory,
                Path.Combine(localDirectory, "factory-reset-backups"));
            var preserveStartup = Settings.StartWithWindows;
            var freshState = new AppState();
            freshState.Settings.StartWithWindows = preserveStartup;
            var result = reset.Execute(preserveStartup, () => _store.Save(freshState));
            Log.Info($"Factory reset completed. Recoverable backup: {result.BackupDirectory}");
            return result;
        }
        catch
        {
            _factoryResetInProgress = false;
            throw;
        }
    }

    private async Task RecoverActiveProfileAfterHardwareChangeAsync()
    {
        if (_hardwareRecoveryBusy || _disposed) return;
        var profile = State.Profiles.FirstOrDefault(candidate =>
            candidate.Id == Settings.LastActivatedProfileId && candidate.ConfigureInternalPipeline);
        if (profile is null) return;
        var input = _activation.ResolveInternalInputDevice(profile);
        var output = Audio.FindDevice(AudioFlow.Playback, profile.FinalVirtualRender.Id);
        var systemCapture = profile.ConfigureMixer && profile.RoutingMode != ProfileRoutingMode.Voice
            ? _activation.ResolveSystemCaptureDevice(profile)
            : null;
        if (input?.Availability != DeviceAvailability.Available
            || output?.Availability != DeviceAvailability.Available
            || profile.ConfigureMixer && profile.RoutingMode != ProfileRoutingMode.Voice
            && systemCapture?.Availability != DeviceAvailability.Available)
        {
            Log.Warn($"Active profile '{profile.Name}' is waiting for its internal input or final output to reconnect.");
            return;
        }

        _hardwareRecoveryBusy = true;
        try
        {
            if (!Engine.IsConnected) await Engine.ConnectAsync();
            var snapshot = await Engine.GetSnapshotAsync();
            if (snapshot.Running
                && string.Equals(snapshot.InputDeviceId, input.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.OutputDeviceId, output.Id, StringComparison.OrdinalIgnoreCase)
                && (systemCapture is null
                    || string.Equals(snapshot.SystemCaptureDeviceId, systemCapture.Id, StringComparison.OrdinalIgnoreCase))) return;
            var result = await _activation.ActivateAsync(profile);
            Log.Info(result.Outcome == ActivationOutcomeKind.Failed
                ? $"Hardware recovery failed for profile '{profile.Name}': {result.Summary}"
                : $"Hardware recovery reapplied profile '{profile.Name}' after endpoint change.");
            RefreshFromHardware();
        }
        catch (Exception ex)
        {
            Log.Warn($"Hardware recovery failed for profile '{profile.Name}': {ex.Message}");
        }
        finally
        {
            _hardwareRecoveryBusy = false;
        }
    }

    public void ApplyTheme(Microsoft.UI.Xaml.Application app)
    {
        _ = app;
    }

    public Microsoft.UI.Xaml.ElementTheme ResolveElementTheme() => Settings.Theme switch
    {
        AppTheme.Light => Microsoft.UI.Xaml.ElementTheme.Light,
        AppTheme.Dark => Microsoft.UI.Xaml.ElementTheme.Dark,
        _ => Microsoft.UI.Xaml.ElementTheme.Default
    };

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    public async Task<LegacyImportResult> ImportLegacyAsync(LegacyImportSource source)
    {
        // Force a current settings file so SettingsStore.Save creates a complete
        // pre-import backup on the following write.
        Persist();
        if (source.Kind == LegacyImportKind.MicVst)
        {
            if (!Engine.IsConnected) await Engine.ConnectAsync();
            var engineResult = await Engine.ImportMicVstAsync(source.Path);
            var recorded = Importer.Apply(source, State);
            PersistAndBroadcast();
            return recorded with
            {
                Summary = $"MicVST: {engineResult.ImportedPluginCount} plugins; entrada '{engineResult.InputDeviceName}'. La salida final se conservó como '{engineResult.KeptOutputDeviceName}'."
            };
        }

        var result = Importer.Apply(source, State);
        PersistAndBroadcast();
        return result;
    }

    private async Task HandleMuteKeyDownAsync()
    {
        _muteReleaseCancellation?.Cancel();
        _muteReleaseCancellation?.Dispose();
        _muteReleaseCancellation = null;
        _muteKeyDownAt = Environment.TickCount64;
        await _muteHotkeyGate.WaitAsync();
        try
        {
            var mode = (MuteKeyMode)Settings.MuteHotkeyMode;
            var command = MuteHotkeyStateMachine.OnKeyDown(mode);
            if (mode == MuteKeyMode.Hybrid)
            {
                if (!Engine.IsConnected) await Engine.ConnectAsync();
                _hybridPreviousMute = (await Engine.GetSnapshotAsync()).MutedRequested;
            }
            switch (command)
            {
                case MuteKeyCommand.Unmute:
                    await SetInternalMuteAsync(false, "PTT down");
                    break;
                case MuteKeyCommand.Mute:
                    await SetInternalMuteAsync(true, "Push-to-mute down");
                    break;
                default:
                    await ToggleInternalMuteAsync(mode == MuteKeyMode.Hybrid ? "Hybrid down" : "NumpadPgDn");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("NumpadPgDn could not toggle internal mute.", ex);
        }
        finally
        {
            _muteHotkeyGate.Release();
        }
    }

    private async Task HandleMuteKeyUpAsync()
    {
        var duration = Environment.TickCount64 - _muteKeyDownAt;
        var mode = (MuteKeyMode)Settings.MuteHotkeyMode;
        var command = MuteHotkeyStateMachine.OnKeyUp(mode, duration, Settings.HybridHoldMilliseconds);
        if (command == MuteKeyCommand.None)
            return;

        _muteReleaseCancellation?.Cancel();
        _muteReleaseCancellation?.Dispose();
        _muteReleaseCancellation = new CancellationTokenSource();
        var token = _muteReleaseCancellation.Token;
        try
        {
            await Task.Delay(Math.Clamp(Settings.MuteHotkeyReleaseDelayMilliseconds, 0, 5000), token);
            await _muteHotkeyGate.WaitAsync(token);
            try
            {
                if (command == MuteKeyCommand.Mute)
                    await SetInternalMuteAsync(true, "PTT up");
                else if (command == MuteKeyCommand.Unmute)
                    await SetInternalMuteAsync(false, "Push-to-mute up");
                else
                    await SetInternalMuteAsync(_hybridPreviousMute, "Hybrid long release");
            }
            finally
            {
                _muteHotkeyGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Mute hotkey release action failed.", ex);
        }
    }

    public async Task<EngineSnapshot> SetInternalMuteAsync(bool muted, string origin = "UI")
    {
        if (!Engine.IsConnected) await Engine.ConnectAsync();
        var snapshot = await Engine.SetMuteAsync(muted);
        snapshot = await WaitForMuteSettledAsync(snapshot, muted);
        var externalResult = ExternalMute.SetDesiredMute(snapshot.MutedRequested);
        if (!externalResult.Succeeded)
            Log.Warn("External mute was partial: " + string.Join(" | ", externalResult.Errors));
        Feedback.NotifyMuteChanged(snapshot, origin);
        Tray.Update(State.Profiles, DetectActiveProfileId(), snapshot.MutedRequested);
        MuteStateChanged?.Invoke(this, snapshot);
        RunMuteActions(snapshot, origin);
        return snapshot;
    }

    public async Task<EngineSnapshot> ToggleInternalMuteAsync(string origin = "UI")
    {
        if (!Engine.IsConnected) await Engine.ConnectAsync();
        var snapshot = await Engine.ToggleMuteAsync();
        snapshot = await WaitForMuteSettledAsync(snapshot, snapshot.MutedRequested);
        var externalResult = ExternalMute.SetDesiredMute(snapshot.MutedRequested);
        if (!externalResult.Succeeded)
            Log.Warn("External mute was partial: " + string.Join(" | ", externalResult.Errors));
        Feedback.NotifyMuteChanged(snapshot, origin);
        Tray.Update(State.Profiles, DetectActiveProfileId(), snapshot.MutedRequested);
        MuteStateChanged?.Invoke(this, snapshot);
        RunMuteActions(snapshot, origin);
        return snapshot;
    }

    private async Task<EngineSnapshot> WaitForMuteSettledAsync(EngineSnapshot requested, bool desiredMuted)
    {
        // The engine command response is intentionally immediate: it reports
        // the new target while the render callback performs the 64-sample
        // de-click ramp. Feedback must not claim a settled mute until the
        // callback has published that fact.
        if (!desiredMuted || !requested.Running || !requested.MutedRequested || requested.MuteSettled)
            return requested;

        var current = requested;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(10).ConfigureAwait(true);
            try
            {
                current = await Engine.GetSnapshotAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Mute settle verification failed: {ex.Message}");
                return current;
            }

            if (!current.MutedRequested || current.MuteSettled)
                return current;
        }

        Log.Warn($"Mute target was requested but did not settle within 500 ms. requested={current.MutedRequested}; settled={current.MuteSettled}.");
        return current;
    }

    private void RunMuteActions(EngineSnapshot snapshot, string origin)
    {
        var activeId = DetectActiveProfileId();
        var profile = State.Profiles.FirstOrDefault(candidate => candidate.Id == activeId);
        if (profile is not null)
        {
            _ = Actions.ExecuteAsync(profile, snapshot, origin);
        }
    }

    private async void AutomationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_automationBusy || _disposed) return;
        _automationBusy = true;
        try
        {
            ExternalMute.PollAndEnforce();
            var foreground = ForegroundExecutableName();
            var match = State.Profiles.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(profile.LinkedApplication)
                && (profile.LinkedApplicationForegroundOnly
                    ? ExecutableMatches(profile.LinkedApplication, foreground)
                    : IsProcessRunning(profile.LinkedApplication)));

            if (match is not null && _linkedProfileActive != match.Id)
            {
                _profileBeforeLinked ??= DetectActiveProfileId();
                var activation = await ActivateProfileAsync(match.Id);
                if (activation.Outcome != ActivationOutcomeKind.Failed)
                    _linkedProfileActive = match.Id;
            }
            else if (match is null && _linkedProfileActive is not null)
            {
                var leavingProfile = State.Profiles.FirstOrDefault(profile => profile.Id == _linkedProfileActive);
                var restore = !string.IsNullOrWhiteSpace(_profileBeforeLinked)
                    ? _profileBeforeLinked
                    : Settings.DefaultProfileId;
                _linkedProfileActive = null;
                _profileBeforeLinked = null;
                if (leavingProfile is not null) RequestAutoExit(leavingProfile);
                if (!string.IsNullOrWhiteSpace(restore) && State.Profiles.Any(profile => profile.Id == restore))
                    await ActivateProfileAsync(restore);
            }

            var active = State.Profiles.FirstOrDefault(profile => profile.Id == DetectActiveProfileId());
            if (active?.AfkTimeoutMilliseconds > 0)
            {
                var info = new NativeMethods.LastInputInfo { cbSize = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
                if (NativeMethods.GetLastInputInfo(ref info))
                {
                    var idleMilliseconds = unchecked((uint)NativeMethods.GetTickCount64() - info.dwTime);
                    if (idleMilliseconds >= active.AfkTimeoutMilliseconds && !_afkMuted)
                    {
                        _afkMuted = true;
                        await SetInternalMuteAsync(true, "AFK");
                    }
                    else if (idleMilliseconds < active.AfkTimeoutMilliseconds)
                    {
                        _afkMuted = false;
                    }
                }
            }
            else
            {
                _afkMuted = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Automation tick failed: {ex.Message}");
        }
        finally
        {
            _automationBusy = false;
        }
    }

    private static string ForegroundExecutableName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero) return string.Empty;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0) return string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsProcessRunning(string executable)
    {
        var processName = Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    private static bool ExecutableMatches(string configured, string actual) =>
        string.Equals(Path.GetFileName(configured), Path.GetFileName(actual), StringComparison.OrdinalIgnoreCase);

    private void RequestAutoExit(AudioProfile profile)
    {
        foreach (var executable in profile.AutoExitApplications.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processName = Path.GetFileNameWithoutExtension(executable);
            if (string.IsNullOrWhiteSpace(processName)) continue;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var requested = process.CloseMainWindow();
                        Log.Info(requested
                            ? $"Requested normal exit for '{process.ProcessName}' after leaving linked profile '{profile.Name}'."
                            : $"Auto-exit skipped '{process.ProcessName}': it has no closable main window.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not request auto-exit for '{processName}': {ex.Message}");
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _automationTimer.Stop();
        _automationTimer.Tick -= AutomationTimer_Tick;
        Audio.DevicesChanged -= Audio_DevicesChanged;
        Engine.UnexpectedExit -= Engine_UnexpectedExit;
        _hardwareRecoveryCancellation?.Cancel();
        _hardwareRecoveryCancellation?.Dispose();
        _hardwareRecoveryCancellation = null;
        _engineRecoveryCancellation?.Cancel();
        _engineRecoveryCancellation?.Dispose();
        _engineRecoveryCancellation = null;
        _muteReleaseCancellation?.Cancel();
        _muteReleaseCancellation?.Dispose();
        _muteHotkeyGate.Dispose();
        if (Settings.RestoreWindowsDefaultsOnExit)
        {
            var restored = true;
            foreach (var assignment in _startupDefaults)
                restored &= Audio.SetDefaultDevice(assignment.Device, assignment.Flow, assignment.Role).Succeeded;
            Log.Info(restored
                ? "Restored startup Windows audio defaults on exit."
                : "Windows audio default restoration on exit was partial.");
        }
        Log.Info("Application shutdown.");
        Audio.Dispose();
        Hotkeys.Dispose();
        Tray.Dispose();
        Notifications.Shutdown();
        Feedback.Dispose();
        Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private IReadOnlyList<RoleAssignment> CaptureCurrentRoleDefaults()
    {
        var assignments = new List<RoleAssignment>();
        foreach (var flow in new[] { AudioFlow.Playback, AudioFlow.Recording })
        {
            var ids = Audio.GetDefaultIds(flow);
            foreach (var pair in new[]
                     {
                         (AudioRole.Console, ids.ConsoleId),
                         (AudioRole.Multimedia, ids.MultimediaId),
                         (AudioRole.Communications, ids.CommunicationsId)
                     })
            {
                if (string.IsNullOrWhiteSpace(pair.Item2)) continue;
                var device = Audio.FindDevice(flow, pair.Item2);
                assignments.Add(new RoleAssignment(
                    flow,
                    pair.Item1,
                    new SavedDeviceReference { Id = pair.Item2, Name = device?.Name ?? pair.Item2 }));
            }
        }
        return assignments;
    }
}
