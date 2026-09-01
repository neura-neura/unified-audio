using UnifiedAudio.Helpers;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;
using CoreAudioEndpoint = UnifiedAudio.Core.Audio.AudioEndpointDescriptor;
using CoreEndpointAvailability = UnifiedAudio.Core.Models.EndpointAvailability;
using CoreEndpointFlow = UnifiedAudio.Core.Models.EndpointFlow;
using CoreEndpointReference = UnifiedAudio.Core.Models.EndpointReference;
using CoreEndpointSelectionDecision = UnifiedAudio.Core.Audio.EndpointSelectionDecision;
using CoreEndpointSelectionPolicy = UnifiedAudio.Core.Audio.EndpointSelectionPolicy;

namespace UnifiedAudio.Services;

public sealed class AudioDeviceService : IDisposable
{
    private readonly AppLog _log;
    private readonly object _sync = new();
    private nint _enumerator;
    private NativeAudioNotificationClient? _notificationClient;
    private bool _disposed;
    private Timer? _debounce;

    public event EventHandler? DevicesChanged;

    public AudioDeviceService(AppLog log)
    {
        _log = log;
        try
        {
            NativeMethods.CoInitializeEx(nint.Zero, NativeMethods.CoInitApartmentThreaded);
            _enumerator = MmDeviceNative.CreateNotificationEnumerator(_log);
            if (_enumerator == nint.Zero)
            {
                _log.Error("Failed to create a native MMDevice enumerator.");
                return;
            }

            _notificationClient = new NativeAudioNotificationClient(_log, OnHardwareChanged);
            var hr = MmDeviceNative.RegisterNotifications(_enumerator, _notificationClient.Pointer);
            if (hr < 0)
            {
                _log.Error($"Failed to register audio device notifications. HRESULT=0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to initialize Core Audio enumerator.", ex);
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioFlow flow)
    {
        try
        {
            var dataFlow = flow == AudioFlow.Playback ? EDataFlow.eRender : EDataFlow.eCapture;
            var defaults = GetDefaultIds(dataFlow);
            var raw = MmDeviceNative.Enumerate(dataFlow, _log);
            return raw
                .Select(device => ToDeviceInfo(device, flow, defaults))
                .OrderBy(d => d.Availability != DeviceAvailability.Available)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Device enumeration failed for {flow}.", ex);
            return [];
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetPhysicalCaptureDevices() =>
        GetDevices(AudioFlow.Recording)
            .Where(device => CoreEndpointSelectionPolicy.IsPhysicalCapture(ToCoreEndpoint(device)))
            .ToList();

    public CoreEndpointSelectionDecision ResolveFinalVirtualRender(
        IReadOnlyList<AudioDeviceInfo> devices,
        SavedDeviceReference? saved = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var savedReference = saved is null
            ? null
            : new CoreEndpointReference
            {
                Id = saved.Id,
                Name = saved.Name,
                Flow = CoreEndpointFlow.Render
            };
        return CoreEndpointSelectionPolicy.ResolveFinalVirtualRender(
            devices.Select(ToCoreEndpoint),
            savedReference);
    }

    public AudioDeviceInfo? FindDevice(AudioFlow flow, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var matches = GetDevices(flow)
            .Where(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public DeviceAvailability GetAvailability(AudioFlow flow, SavedDeviceReference reference)
    {
        var device = FindDevice(flow, reference.Id);
        return device?.Availability ?? DeviceAvailability.Disconnected;
    }

    public string GetDisplayName(AudioFlow flow, SavedDeviceReference reference)
    {
        var device = FindDevice(flow, reference.Id);
        return device?.Name ?? (string.IsNullOrWhiteSpace(reference.Name) ? reference.Id : reference.Name);
    }

    public (string? OutputId, string? InputId) GetCurrentDefaults()
    {
        return (
            MmDeviceNative.GetDefaultId(EDataFlow.eRender, ERole.eMultimedia, _log)
                ?? MmDeviceNative.GetDefaultId(EDataFlow.eRender, ERole.eConsole, _log),
            MmDeviceNative.GetDefaultId(EDataFlow.eCapture, ERole.eMultimedia, _log)
                ?? MmDeviceNative.GetDefaultId(EDataFlow.eCapture, ERole.eConsole, _log));
    }

    public DeviceSwitchResult SetDefaultDevice(SavedDeviceReference reference, AudioFlow flow, bool applyAllRoles = true)
    {
        var roles = applyAllRoles
            ? new[] { AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications }
            : new[] { AudioRole.Multimedia };
        return SetDefaultDevice(reference, flow, roles);
    }

    public DeviceSwitchResult SetDefaultDevice(SavedDeviceReference reference, AudioFlow flow, AudioRole role) =>
        SetDefaultDevice(reference, flow, [role]);

    public DeviceSwitchResult SetDefaultDevice(SavedDeviceReference reference, AudioFlow flow, IReadOnlyList<AudioRole> roles)
    {
        if (string.IsNullOrWhiteSpace(reference.Id))
        {
            return new DeviceSwitchResult
            {
                Requested = reference,
                Succeeded = false,
                ErrorMessage = Loc.Format("DeviceUnavailable", reference.Name)
            };
        }

        var device = FindDevice(flow, reference.Id);
        if (device is null || device.Availability != DeviceAvailability.Available)
        {
            var name = string.IsNullOrWhiteSpace(reference.Name) ? reference.Id : reference.Name;
            return new DeviceSwitchResult
            {
                Requested = reference,
                Succeeded = false,
                ErrorMessage = Loc.Format("DeviceUnavailable", name)
            };
        }

        try
        {
            var nativeRoles = roles.Select(ToNativeRole).ToArray();
            MmDeviceNative.SetDefaultEndpoint(device.Id, nativeRoles, _log);
            return new DeviceSwitchResult
            {
                Requested = new SavedDeviceReference { Id = device.Id, Name = device.Name },
                Succeeded = true
            };
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to set default {flow} device '{device.Name}'.", ex);
            return new DeviceSwitchResult
            {
                Requested = reference,
                Succeeded = false,
                ErrorMessage = Loc.Format("DeviceUnavailable", device.Name)
            };
        }
    }

    public (string? ConsoleId, string? MultimediaId, string? CommunicationsId) GetDefaultIds(AudioFlow flow)
    {
        var dataFlow = flow == AudioFlow.Playback ? EDataFlow.eRender : EDataFlow.eCapture;
        var defaults = GetDefaultIds(dataFlow);
        return (defaults.Console, defaults.Multimedia, defaults.Communications);
    }

    private static ERole ToNativeRole(AudioRole role) => role switch
    {
        AudioRole.Console => ERole.eConsole,
        AudioRole.Communications => ERole.eCommunications,
        _ => ERole.eMultimedia
    };

    private DefaultIds GetDefaultIds(EDataFlow flow) => new(
        MmDeviceNative.GetDefaultId(flow, ERole.eConsole, _log),
        MmDeviceNative.GetDefaultId(flow, ERole.eMultimedia, _log),
        MmDeviceNative.GetDefaultId(flow, ERole.eCommunications, _log));

    private static AudioDeviceInfo ToDeviceInfo(MmDeviceNative.RawDevice device, AudioFlow flow, DefaultIds defaults)
    {
        var availability = device.State switch
        {
            CoreAudioConstants.DeviceStateActive => DeviceAvailability.Available,
            CoreAudioConstants.DeviceStateDisabled => DeviceAvailability.Disabled,
            CoreAudioConstants.DeviceStateNotPresent => DeviceAvailability.Unknown,
            CoreAudioConstants.DeviceStateUnplugged => DeviceAvailability.Disconnected,
            _ => DeviceAvailability.Unknown
        };

        return new AudioDeviceInfo
        {
            Id = device.Id,
            Name = device.Name,
            Flow = flow,
            Availability = availability,
            IsDefaultConsole = string.Equals(device.Id, defaults.Console, StringComparison.OrdinalIgnoreCase),
            IsDefaultMultimedia = string.Equals(device.Id, defaults.Multimedia, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications = string.Equals(device.Id, defaults.Communications, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static CoreAudioEndpoint ToCoreEndpoint(AudioDeviceInfo device) => new()
    {
        Id = device.Id,
        Name = device.Name,
        Flow = device.Flow == AudioFlow.Playback ? CoreEndpointFlow.Render : CoreEndpointFlow.Capture,
        Availability = device.Availability switch
        {
            DeviceAvailability.Available => CoreEndpointAvailability.Available,
            DeviceAvailability.Disconnected => CoreEndpointAvailability.Disconnected,
            DeviceAvailability.Disabled => CoreEndpointAvailability.Disabled,
            _ => CoreEndpointAvailability.Unknown
        }
    };

    private void OnHardwareChanged()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                if (!_disposed)
                {
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                }
            }, null, 250, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = null;
            if (_enumerator != nint.Zero && _notificationClient is not null)
            {
                try
                {
                    MmDeviceNative.UnregisterNotifications(_enumerator, _notificationClient.Pointer);
                }
                catch
                {
                }
            }

            if (_enumerator != nint.Zero)
            {
                MmDeviceNative.ReleaseCom(_enumerator);
                _enumerator = nint.Zero;
            }

            _notificationClient?.Dispose();
            _notificationClient = null;
        }
    }

    private readonly record struct DefaultIds(string? Console, string? Multimedia, string? Communications);
}
