using UnifiedAudio.Interop;
using UnifiedAudio.Models;

namespace UnifiedAudio.Services;

public sealed record ExternalEndpointStatus(
    string Id,
    string Name,
    bool Available,
    bool? Muted,
    float? VolumeScalar);

public sealed record ExternalEndpointPolicy(
    bool Enabled,
    bool AllRecordingDevices,
    IReadOnlyList<SavedDeviceReference> Devices,
    bool ForceMuteState,
    bool LockVolume,
    float TargetVolumeScalar);

public sealed record ExternalEndpointTransactionSnapshot(
    ExternalEndpointPolicy Policy,
    bool DesiredMuted,
    IReadOnlyList<ExternalEndpointStatus> EndpointStates);

public sealed record ExternalEndpointOperationResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    IReadOnlyList<ExternalEndpointStatus> Endpoints);

public sealed class ExternalEndpointMuteService
{
    private readonly AudioDeviceService _audio;
    private readonly AppLog _log;
    private ExternalEndpointPolicy _policy = DisabledPolicy();
    private bool _desiredMuted;
    private string _lastStateSignature = string.Empty;

    public ExternalEndpointMuteService(AudioDeviceService audio, AppLog log)
    {
        _audio = audio;
        _log = log;
    }

    public event Action<IReadOnlyList<ExternalEndpointStatus>>? StateChanged;

    public ExternalEndpointTransactionSnapshot CaptureTransactionSnapshot() =>
        new(ClonePolicy(_policy), _desiredMuted, GetStatuses());

    public ExternalEndpointOperationResult ApplyProfile(AudioProfile profile, bool desiredMuted)
    {
        _policy = new ExternalEndpointPolicy(
            profile.ConfigureExternalEndpoints,
            profile.ExternalMuteAllRecordingDevices,
            profile.ExternalMuteDevices.Select(CloneReference).ToArray(),
            profile.ForceExternalMuteState,
            profile.ExternalVolumeLockEnabled,
            Math.Clamp(profile.ExternalVolumeScalar, 0.0f, 1.0f));
        _desiredMuted = desiredMuted;
        if (!_policy.Enabled)
        {
            PublishIfChanged([]);
            return new ExternalEndpointOperationResult(true, [], []);
        }
        return ApplyDesiredState(requireEveryConfiguredEndpoint: true);
    }

    public ExternalEndpointOperationResult SetDesiredMute(bool muted)
    {
        _desiredMuted = muted;
        return !_policy.Enabled
            ? new ExternalEndpointOperationResult(true, [], [])
            : ApplyDesiredState(requireEveryConfiguredEndpoint: false);
    }

    public void PollAndEnforce()
    {
        if (!_policy.Enabled) return;
        var statuses = GetStatuses();
        foreach (var endpoint in statuses.Where(endpoint => endpoint.Available))
        {
            var muteMismatch = _policy.ForceMuteState && endpoint.Muted != _desiredMuted;
            var volumeMismatch = _policy.LockVolume
                                 && endpoint.VolumeScalar.HasValue
                                 && Math.Abs(endpoint.VolumeScalar.Value - _policy.TargetVolumeScalar) > 0.005f;
            if (!muteMismatch && !volumeMismatch) continue;
            try
            {
                MmDeviceNative.SetEndpointVolumeState(
                    endpoint.Id,
                    muteMismatch ? _desiredMuted : null,
                    volumeMismatch ? _policy.TargetVolumeScalar : null,
                    _log);
                _log.Info($"Reapplied external endpoint policy to '{endpoint.Name}'.");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not enforce external endpoint '{endpoint.Name}': {ex.Message}");
            }
        }
        PublishIfChanged(GetStatuses());
    }

    public ExternalEndpointOperationResult Restore(ExternalEndpointTransactionSnapshot snapshot)
    {
        var errors = new List<string>();
        foreach (var endpoint in snapshot.EndpointStates.Where(endpoint => endpoint.Available))
        {
            try
            {
                MmDeviceNative.SetEndpointVolumeState(
                    endpoint.Id,
                    endpoint.Muted,
                    endpoint.VolumeScalar,
                    _log);
            }
            catch (Exception ex)
            {
                errors.Add($"{endpoint.Name}: {ex.Message}");
            }
        }
        _policy = ClonePolicy(snapshot.Policy);
        _desiredMuted = snapshot.DesiredMuted;
        var statuses = GetStatuses();
        PublishIfChanged(statuses);
        return new ExternalEndpointOperationResult(errors.Count == 0, errors, statuses);
    }

    public IReadOnlyList<ExternalEndpointStatus> GetStatuses()
    {
        if (!_policy.Enabled) return [];
        return ResolveTargets(_policy)
            .Select(reference =>
            {
                var info = _audio.FindDevice(AudioFlow.Recording, reference.Id);
                if (info?.Availability != DeviceAvailability.Available)
                    return new ExternalEndpointStatus(reference.Id, DisplayName(reference), false, null, null);
                var state = MmDeviceNative.GetEndpointVolumeState(reference.Id, _log);
                return state.HasValue
                    ? new ExternalEndpointStatus(reference.Id, info.Name, true, state.Value.Muted, state.Value.VolumeScalar)
                    : new ExternalEndpointStatus(reference.Id, info.Name, false, null, null);
            })
            .ToArray();
    }

    private ExternalEndpointOperationResult ApplyDesiredState(bool requireEveryConfiguredEndpoint)
    {
        var targets = ResolveTargets(_policy);
        var errors = new List<string>();
        if (targets.Count == 0)
            errors.Add("No recording endpoint is selected for external mute control.");

        foreach (var target in targets)
        {
            var info = _audio.FindDevice(AudioFlow.Recording, target.Id);
            if (info?.Availability != DeviceAvailability.Available)
            {
                if (requireEveryConfiguredEndpoint)
                    errors.Add($"{DisplayName(target)} is unavailable.");
                continue;
            }
            try
            {
                MmDeviceNative.SetEndpointVolumeState(
                    target.Id,
                    _desiredMuted,
                    _policy.LockVolume ? _policy.TargetVolumeScalar : null,
                    _log);
            }
            catch (Exception ex)
            {
                errors.Add($"{info.Name}: {ex.Message}");
            }
        }
        var statuses = GetStatuses();
        PublishIfChanged(statuses);
        return new ExternalEndpointOperationResult(errors.Count == 0, errors, statuses);
    }

    private IReadOnlyList<SavedDeviceReference> ResolveTargets(ExternalEndpointPolicy policy)
    {
        if (!policy.Enabled) return [];
        if (policy.AllRecordingDevices)
            return _audio.GetDevices(AudioFlow.Recording)
                .Where(device => device.Availability == DeviceAvailability.Available)
                .Select(device => new SavedDeviceReference { Id = device.Id, Name = device.Name })
                .ToArray();
        return policy.Devices
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Id))
            .DistinctBy(reference => reference.Id, StringComparer.OrdinalIgnoreCase)
            .Select(CloneReference)
            .ToArray();
    }

    private void PublishIfChanged(IReadOnlyList<ExternalEndpointStatus> statuses)
    {
        var signature = string.Join("|", statuses.Select(endpoint =>
            $"{endpoint.Id}:{endpoint.Available}:{endpoint.Muted}:{endpoint.VolumeScalar:F3}"));
        if (signature == _lastStateSignature) return;
        _lastStateSignature = signature;
        StateChanged?.Invoke(statuses);
    }

    private static ExternalEndpointPolicy DisabledPolicy() => new(false, false, [], false, false, 1.0f);
    private static ExternalEndpointPolicy ClonePolicy(ExternalEndpointPolicy source) => new(
        source.Enabled,
        source.AllRecordingDevices,
        source.Devices.Select(CloneReference).ToArray(),
        source.ForceMuteState,
        source.LockVolume,
        source.TargetVolumeScalar);
    private static SavedDeviceReference CloneReference(SavedDeviceReference source) =>
        new() { Id = source.Id, Name = source.Name };
    private static string DisplayName(SavedDeviceReference reference) =>
        string.IsNullOrWhiteSpace(reference.Name) ? reference.Id : reference.Name;
}
