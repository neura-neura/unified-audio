using UnifiedAudio.Helpers;
using UnifiedAudio.Models;

namespace UnifiedAudio.Services;

public sealed class ProfileActivationService
{
    private readonly AudioDeviceService _audio;
    private readonly AppLog _log;
    private readonly EngineClient _engine;
    private readonly ExternalEndpointMuteService _externalMute;
    private readonly AppSettings _settings;

    public ProfileActivationService(
        AudioDeviceService audio,
        EngineClient engine,
        ExternalEndpointMuteService externalMute,
        AppLog log)
        : this(audio, engine, externalMute, new AppSettings(), log)
    {
    }

    public ProfileActivationService(
        AudioDeviceService audio,
        EngineClient engine,
        ExternalEndpointMuteService externalMute,
        AppSettings settings,
        AppLog log)
    {
        _audio = audio;
        _engine = engine;
        _externalMute = externalMute;
        _settings = settings;
        _log = log;
    }

    public async Task<ProfileActivationResult> ActivateAsync(AudioProfile profile, CancellationToken cancellationToken = default)
    {
        EngineSnapshot? priorEngine = null;
        EngineProfileState? priorPluginState = null;
        var priorExternal = _externalMute.CaptureTransactionSnapshot();
        var priorDefaults = CaptureDefaults(profile);
        if (profile.ConfigureInternalPipeline || profile.ConfigurePluginChain || profile.ConfigureMixer)
        {
            var input = profile.ConfigureInternalPipeline
                ? ResolveInternalInputDevice(profile)
                : null;
            var output = profile.ConfigureInternalPipeline
                ? _audio.FindDevice(AudioFlow.Playback, profile.FinalVirtualRender.Id)
                : null;
            if (profile.ConfigureInternalPipeline
                && (input?.Availability != DeviceAvailability.Available || output?.Availability != DeviceAvailability.Available))
            {
                var message = input?.Availability != DeviceAvailability.Available
                    ? $"Internal input unavailable: {InternalInputDisplayName(profile)}"
                    : $"Final virtual render unavailable: {profile.FinalVirtualRender.Name}";
                return FailedBeforeApply(profile, message);
            }

            try
            {
                if (!_engine.IsConnected) await _engine.ConnectAsync(cancellationToken).ConfigureAwait(false);
                priorEngine = await _engine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (profile.ConfigurePluginChain)
                    priorPluginState = await _engine.CaptureProfileStateAsync(cancellationToken).ConfigureAwait(false);
                var deviceConfiguration = profile.ConfigureInternalPipeline
                    ? new EngineDeviceConfiguration(
                        input!.Name,
                        output!.Name,
                        profile.EngineBufferSize,
                        input.Id,
                        output.Id)
                    : null;
                EngineMixerConfiguration? mixerConfiguration = null;
                if (profile.ConfigureMixer)
                {
                    var systemEndpoint = profile.RoutingMode == ProfileRoutingMode.Voice
                        ? null
                        : ResolveSystemCaptureDevice(profile);
                    var systemDevice = systemEndpoint?.Name ?? string.Empty;
                    var systemDeviceId = systemEndpoint?.Id ?? string.Empty;
                    if (profile.RoutingMode != ProfileRoutingMode.Voice
                        && (string.IsNullOrWhiteSpace(systemDevice) || string.IsNullOrWhiteSpace(systemDeviceId)))
                        throw new InvalidOperationException($"System capture endpoint unavailable: {profile.SystemCapture.Name}");
                    mixerConfiguration = new EngineMixerConfiguration(
                        (EngineMixerMode)profile.RoutingMode,
                        systemDevice,
                        systemDeviceId,
                        profile.VoiceGain,
                        profile.SystemGain,
                        profile.DuckingEnabled,
                        profile.DuckAmount,
                        profile.DuckThresholdDb,
                        profile.DuckAttackMs,
                        profile.DuckHoldMs,
                        profile.DuckReleaseMs,
                        profile.ProcessFilterEnabled,
                        profile.ProcessFilterExclusionMode,
                        profile.ApplicationMixRules.Select(rule =>
                            new EngineProcessMixRule(rule.Key, rule.DisplayName, rule.Gain, rule.Excluded)).ToArray());
                }
                bool? muted = profile.ConfigureInternalPipeline
                              && profile.InternalMute != InternalMuteState.Preserve
                    ? profile.InternalMute == InternalMuteState.Muted
                    : null;
                var plugins = profile.ConfigurePluginChain
                    ? profile.PluginChain.Select(plugin => new EnginePluginState(
                        plugin.Id,
                        plugin.Bypassed,
                        plugin.StateBase64)).ToArray()
                    : null;
                await _engine.ConfigurePipelineAsync(
                    deviceConfiguration,
                    mixerConfiguration,
                    plugins,
                    muted,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Internal pipeline activation failed for '{profile.Name}'.", ex);
                await RollbackEngineAsync(priorEngine, priorPluginState).ConfigureAwait(false);
                return FailedBeforeApply(profile, ex.Message);
            }
        }

        if (profile.ConfigureExternalEndpoints && priorEngine is null)
        {
            try
            {
                if (!_engine.IsConnected) await _engine.ConnectAsync(cancellationToken).ConfigureAwait(false);
                priorEngine = await _engine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Could not read internal mute before external endpoint activation for '{profile.Name}'.", ex);
                return FailedBeforeApply(profile, ex.Message);
            }
        }

        var desiredExternalMute = profile.InternalMute switch
        {
            InternalMuteState.Muted => true,
            InternalMuteState.Unmuted => false,
            _ => priorEngine?.MutedRequested ?? priorExternal.DesiredMuted
        };
        var externalResult = _externalMute.ApplyProfile(profile, desiredExternalMute);
        if (!externalResult.Succeeded)
        {
            _log.Warn($"External endpoint activation failed for '{profile.Name}': "
                      + string.Join(" | ", externalResult.Errors));
            var externalRollbackOk = _externalMute.Restore(priorExternal).Succeeded;
            if (profile.ConfigureInternalPipeline || profile.ConfigurePluginChain || profile.ConfigureMixer)
                externalRollbackOk &= await RollbackEngineAsync(priorEngine, priorPluginState).ConfigureAwait(false);
            return new ProfileActivationResult
            {
                Profile = profile,
                Outcome = externalRollbackOk ? ActivationOutcomeKind.Failed : ActivationOutcomeKind.Partial,
                Summary = "No se pudo aplicar el control de endpoints externos: "
                          + string.Join(" | ", externalResult.Errors),
                RolledBack = externalRollbackOk,
                InternalPipelineError = externalRollbackOk ? null : "Rollback parcial"
            };
        }

        var defaultsResult = ActivateDefaults(profile);
        if (defaultsResult.Outcome == ActivationOutcomeKind.Success)
        {
            return new ProfileActivationResult
            {
                Profile = defaultsResult.Profile,
                Outcome = defaultsResult.Outcome,
                Output = defaultsResult.Output,
                Input = defaultsResult.Input,
                Summary = defaultsResult.Summary,
                InternalPipelineApplied = profile.ConfigureInternalPipeline
                                          || profile.ConfigurePluginChain
                                          || profile.ConfigureMixer
                                          || profile.ConfigureExternalEndpoints
            };
        }

        var rollbackOk = RollbackDefaults(priorDefaults);
        rollbackOk &= _externalMute.Restore(priorExternal).Succeeded;
        if (profile.ConfigureInternalPipeline || profile.ConfigurePluginChain || profile.ConfigureMixer)
        {
            rollbackOk &= await RollbackEngineAsync(priorEngine, priorPluginState).ConfigureAwait(false);
        }
        return new ProfileActivationResult
        {
            Profile = defaultsResult.Profile,
            Outcome = rollbackOk ? ActivationOutcomeKind.Failed : ActivationOutcomeKind.Partial,
            Output = defaultsResult.Output,
            Input = defaultsResult.Input,
            Summary = defaultsResult.Summary + Environment.NewLine
                + (rollbackOk ? "Los cambios parciales se revirtieron." : "El rollback fue parcial; revisa Diagnóstico."),
            InternalPipelineApplied = false,
            RolledBack = rollbackOk,
            InternalPipelineError = rollbackOk ? null : "Rollback parcial"
        };
    }

    public AudioDeviceInfo? ResolveInternalInputDevice(AudioProfile profile) =>
        profile.InputMode switch
        {
            ProfileInputMode.FollowGlobal => ResolveGlobalPhysicalInput(),
            ProfileInputMode.ProfileOverride => ResolvePhysicalCapture(profile.InternalInput),
            _ => ResolveDefaultAwareDevice(AudioFlow.Recording, profile.InternalInput, profile.UseDefaultInternalInput)
        };

    public AudioDeviceInfo? ResolveSystemCaptureDevice(AudioProfile profile) =>
        ResolveDefaultAwareDevice(AudioFlow.Playback, profile.SystemCapture, profile.UseDefaultSystemCapture);

    private AudioDeviceInfo? ResolveDefaultAwareDevice(
        AudioFlow flow,
        SavedDeviceReference fallback,
        bool followDefault)
    {
        var saved = _audio.FindDevice(flow, fallback.Id);
        if (!followDefault) return saved;
        var defaults = _audio.GetCurrentDefaults();
        var defaultId = flow == AudioFlow.Playback ? defaults.OutputId : defaults.InputId;
        var current = _audio.FindDevice(flow, defaultId);
        if (current?.Availability == DeviceAvailability.Available && !LooksLikeVirtualCable(current.Name))
            return current;
        if (saved?.Availability == DeviceAvailability.Available && !LooksLikeVirtualCable(saved.Name))
            return saved;
        return null;
    }

    private AudioDeviceInfo? ResolveGlobalPhysicalInput()
    {
        if (_settings.GlobalInputMode == GlobalInputMode.Fixed)
            return ResolvePhysicalCapture(_settings.GlobalPhysicalInput);

        if (_settings.GlobalInputMode != GlobalInputMode.FollowWindowsDefault)
            return null;

        var defaults = _audio.GetCurrentDefaults();
        var current = _audio.FindDevice(AudioFlow.Recording, defaults.InputId);
        if (IsPhysicalCapture(current))
            return current;

        return ResolvePhysicalCapture(_settings.GlobalPhysicalInput);
    }

    private AudioDeviceInfo? ResolvePhysicalCapture(SavedDeviceReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Id))
            return null;

        var device = _audio.FindDevice(AudioFlow.Recording, reference.Id);
        return IsPhysicalCapture(device) ? device : null;
    }

    private bool IsPhysicalCapture(AudioDeviceInfo? device) =>
        device is not null
        && _audio.GetPhysicalCaptureDevices().Any(candidate => SameId(candidate.Id, device.Id));

    private string InternalInputDisplayName(AudioProfile profile) => profile.InputMode switch
    {
        ProfileInputMode.FollowGlobal => string.IsNullOrWhiteSpace(_settings.GlobalPhysicalInput.Name)
            ? Loc.Get("GlobalPhysicalInput")
            : _settings.GlobalPhysicalInput.Name,
        _ => profile.InternalInput.Name
    };

    private static bool LooksLikeVirtualCable(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private ProfileActivationResult ActivateDefaults(AudioProfile profile)
    {
        var output = ActivateFlow(profile, AudioFlow.Playback);
        var configuresWindowsInput = ShouldConfigureWindowsInputDefaults(profile);
        var input = configuresWindowsInput
            ? ActivateFlow(profile, AudioFlow.Recording)
            : UnchangedInputResult(profile);

        var outcome = configuresWindowsInput
            ? (output.Succeeded, input.Succeeded) switch
            {
                (true, true) => ActivationOutcomeKind.Success,
                (false, false) => ActivationOutcomeKind.Failed,
                _ => ActivationOutcomeKind.Partial
            }
            : output.Succeeded ? ActivationOutcomeKind.Success : ActivationOutcomeKind.Failed;

        var result = new ProfileActivationResult
        {
            Profile = profile,
            Outcome = outcome,
            Output = output,
            Input = input,
            Summary = Describe(AudioFlow.Playback, output) + Environment.NewLine
                + (configuresWindowsInput
                    ? Describe(AudioFlow.Recording, input)
                    : Loc.Get("InputUnchanged"))
        };

        if (outcome == ActivationOutcomeKind.Success)
        {
            _log.Info($"Activated profile '{profile.Name}'.");
        }
        else if (outcome == ActivationOutcomeKind.Partial)
        {
            _log.Warn($"Partially activated profile '{profile.Name}'. Output={output.Succeeded} Input={input.Succeeded}");
        }
        else
        {
            _log.Error($"Failed to activate profile '{profile.Name}'.");
        }

        return result;
    }

    private ProfileActivationResult FailedBeforeApply(AudioProfile profile, string message) => new()
    {
        Profile = profile,
        Outcome = ActivationOutcomeKind.Failed,
        Summary = message,
        InternalPipelineError = message
    };

    private IReadOnlyList<RoleAssignment> CaptureDefaults(AudioProfile profile)
    {
        var result = new List<RoleAssignment>();
        // FollowGlobal never mutates Windows Recording defaults, so its
        // rollback transaction must not write those roles either.
        var flows = ShouldConfigureWindowsInputDefaults(profile)
            ? new[] { AudioFlow.Playback, AudioFlow.Recording }
            : new[] { AudioFlow.Playback };
        foreach (var flow in flows)
        {
            var defaults = _audio.GetDefaultIds(flow);
            Add(AudioRole.Console, defaults.ConsoleId);
            Add(AudioRole.Multimedia, defaults.MultimediaId);
            Add(AudioRole.Communications, defaults.CommunicationsId);

            void Add(AudioRole role, string? id)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                var device = _audio.FindDevice(flow, id);
                result.Add(new RoleAssignment(flow, role, new SavedDeviceReference
                {
                    Id = id,
                    Name = device?.Name ?? id
                }));
            }
        }
        return result;
    }

    private bool RollbackDefaults(IReadOnlyList<RoleAssignment> assignments)
    {
        var succeeded = true;
        foreach (var assignment in assignments)
        {
            var result = _audio.SetDefaultDevice(assignment.Device, assignment.Flow, assignment.Role);
            succeeded &= result.Succeeded;
        }
        _log.Info(succeeded ? "Windows defaults rollback succeeded." : "Windows defaults rollback was partial.");
        return succeeded;
    }

    private async Task<bool> RollbackEngineAsync(
        EngineSnapshot? snapshot,
        EngineProfileState? pluginState = null)
    {
        if (snapshot is null) return true;
        try
        {
            var devices = snapshot.Running
                          && !string.IsNullOrWhiteSpace(snapshot.InputDeviceName)
                          && !string.IsNullOrWhiteSpace(snapshot.OutputDeviceName)
                ? new EngineDeviceConfiguration(
                    snapshot.InputDeviceName,
                    snapshot.OutputDeviceName,
                    snapshot.BufferSize,
                    snapshot.InputDeviceId,
                    snapshot.OutputDeviceId)
                : null;
            var mixer = new EngineMixerConfiguration(
                (EngineMixerMode)Math.Clamp(snapshot.MixerMode, 0, 2),
                snapshot.SystemCaptureDeviceName,
                snapshot.SystemCaptureDeviceId,
                snapshot.VoiceGain,
                snapshot.SystemGain,
                snapshot.DuckingEnabled,
                snapshot.DuckAmount,
                snapshot.DuckThresholdDb,
                snapshot.DuckAttackMs,
                snapshot.DuckHoldMs,
                snapshot.DuckReleaseMs,
                snapshot.ProcessFilterEnabled,
                snapshot.ProcessFilterExclusionMode,
                snapshot.ProcessRules);
            await _engine.ConfigurePipelineAsync(
                devices,
                mixer,
                pluginState?.Plugins,
                snapshot.MutedRequested).ConfigureAwait(false);
            if (!snapshot.Running) await _engine.StopAsync().ConfigureAwait(false);
            _log.Info("Internal engine rollback succeeded.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Internal engine rollback failed.", ex);
            return false;
        }
    }

    public bool MatchesCurrentDefaults(AudioProfile profile)
    {
        if (!profile.UseAdvancedRoles)
        {
            var defaults = _audio.GetCurrentDefaults();
            return SameId(profile.Output.Id, defaults.OutputId)
                   && (!ShouldConfigureWindowsInputDefaults(profile) || SameId(profile.Input.Id, defaults.InputId));
        }

        return MatchesAdvanced(profile, AudioFlow.Playback)
               && (!ShouldConfigureWindowsInputDefaults(profile) || MatchesAdvanced(profile, AudioFlow.Recording));
    }

    private static bool ShouldConfigureWindowsInputDefaults(AudioProfile profile) =>
        profile.InputMode != ProfileInputMode.FollowGlobal
        && profile.ConfigureWindowsInputDefaults;

    private DeviceSwitchResult ActivateFlow(AudioProfile profile, AudioFlow flow)
    {
        if (!profile.UseAdvancedRoles)
        {
            return _audio.SetDefaultDevice(PrimaryDevice(profile, flow), flow);
        }

        var assignments = GetAdvancedAssignments(profile, flow).ToList();
        DeviceSwitchResult? lastSuccess = null;
        DeviceSwitchResult? lastFailure = null;
        foreach (var assignment in assignments)
        {
            var result = _audio.SetDefaultDevice(assignment.Device, assignment.Flow, assignment.Role);
            if (result.Succeeded)
            {
                lastSuccess = result;
            }
            else
            {
                lastFailure = result;
            }
        }

        if (lastFailure is null)
        {
            return lastSuccess ?? new DeviceSwitchResult
            {
                Requested = PrimaryDevice(profile, flow),
                Succeeded = false,
                ErrorMessage = Loc.Get("MissingAdvancedDevices")
            };
        }

        return lastFailure;
    }

    private static DeviceSwitchResult UnchangedInputResult(AudioProfile profile) => new()
    {
        Requested = new SavedDeviceReference
        {
            Id = profile.Input.Id,
            Name = string.IsNullOrWhiteSpace(profile.Input.Name) ? "global" : profile.Input.Name
        },
        Succeeded = true
    };

    private bool MatchesAdvanced(AudioProfile profile, AudioFlow flow)
    {
        var defaults = _audio.GetDefaultIds(flow);
        foreach (var assignment in GetAdvancedAssignments(profile, flow))
        {
            var current = assignment.Role switch
            {
                AudioRole.Console => defaults.ConsoleId,
                AudioRole.Communications => defaults.CommunicationsId,
                _ => defaults.MultimediaId
            };
            if (!SameId(assignment.Device.Id, current))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<RoleAssignment> GetAdvancedAssignments(AudioProfile profile, AudioFlow flow)
    {
        yield return new RoleAssignment(flow, AudioRole.Console, RoleDevice(profile, flow, AudioRole.Console));
        yield return new RoleAssignment(flow, AudioRole.Multimedia, RoleDevice(profile, flow, AudioRole.Multimedia));
        yield return new RoleAssignment(flow, AudioRole.Communications, RoleDevice(profile, flow, AudioRole.Communications));
    }

    private static SavedDeviceReference PrimaryDevice(AudioProfile profile, AudioFlow flow) =>
        flow == AudioFlow.Playback ? profile.Output : profile.Input;

    private static SavedDeviceReference RoleDevice(AudioProfile profile, AudioFlow flow, AudioRole role)
    {
        var specific = (flow, role) switch
        {
            (AudioFlow.Playback, AudioRole.Console) => profile.OutputConsole,
            (AudioFlow.Playback, AudioRole.Multimedia) => profile.OutputMultimedia,
            (AudioFlow.Playback, AudioRole.Communications) => profile.OutputCommunications,
            (AudioFlow.Recording, AudioRole.Console) => profile.InputConsole,
            (AudioFlow.Recording, AudioRole.Multimedia) => profile.InputMultimedia,
            (AudioFlow.Recording, AudioRole.Communications) => profile.InputCommunications,
            _ => null
        };

        return HasId(specific) ? specific! : new SavedDeviceReference();
    }

    private static string Describe(AudioFlow flow, DeviceSwitchResult result)
    {
        var key = flow == AudioFlow.Playback ? "OutputLine" : "InputLine";
        return result.Succeeded
            ? Loc.Format(key, result.Requested.Name)
            : Loc.Format(key, Loc.Format("DeviceUnavailable", DisplayName(result.Requested)));
    }

    private static string DisplayName(SavedDeviceReference reference) =>
        string.IsNullOrWhiteSpace(reference.Name) ? reference.Id : reference.Name;

    private static bool HasId(SavedDeviceReference? reference) =>
        !string.IsNullOrWhiteSpace(reference?.Id);

    private static bool SameId(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
