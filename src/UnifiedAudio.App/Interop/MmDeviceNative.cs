using System.Runtime.InteropServices;
using UnifiedAudio.Services;

namespace UnifiedAudio.Interop;

/// <summary>
/// Raw IUnknown vtable calls for MMDevice and PolicyConfig.
/// WinUI / CsWinRT ComWrappers break classic [ComImport] casts for some Core Audio interfaces.
/// </summary>
internal static unsafe class MmDeviceNative
{
    private static readonly Guid EnumeratorClsid = CoreAudioConstants.MMDeviceEnumeratorClsid;
    private static readonly Guid EnumeratorIid = CoreAudioConstants.ImmDeviceEnumeratorIid;
    private static readonly Guid PolicyClsid = CoreAudioConstants.PolicyConfigClientClsid;
    private static readonly Guid[] PolicyIids =
    [
        CoreAudioConstants.IPolicyConfigIid,
        CoreAudioConstants.IPolicyConfig10Iid,
        CoreAudioConstants.IPolicyConfigVistaIid
    ];

    public static IReadOnlyList<RawDevice> Enumerate(EDataFlow flow, AppLog? log = null)
    {
        var enumerator = CreateEnumerator(log);
        if (enumerator == nint.Zero)
        {
            return [];
        }

        try
        {
            var enumFn = GetSlot<EnumAudioEndpoints>(enumerator, 3);
            nint collection;
            var hr = enumFn(enumerator, (int)flow, CoreAudioConstants.DeviceStateMaskAll, &collection);
            if (hr < 0)
            {
                log?.Error($"EnumAudioEndpoints failed for {flow}. HRESULT=0x{hr:X8}");
                return [];
            }

            if (collection == nint.Zero)
            {
                log?.Warn($"EnumAudioEndpoints returned no collection for {flow}.");
                return [];
            }

            try
            {
                var getCount = GetSlot<GetCount>(collection, 3);
                uint count;
                var countHr = getCount(collection, &count);
                if (countHr < 0)
                {
                    log?.Error($"IMMDeviceCollection.GetCount failed. HRESULT=0x{countHr:X8}");
                    return [];
                }

                var item = GetSlot<CollectionItem>(collection, 4);
                var devices = new List<RawDevice>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    nint device;
                    var itemHr = item(collection, i, &device);
                    if (itemHr < 0 || device == nint.Zero)
                    {
                        log?.Warn($"IMMDeviceCollection.Item({i}) failed. HRESULT=0x{itemHr:X8}");
                        continue;
                    }

                    try
                    {
                        var parsed = ReadDevice(device, log);
                        if (parsed is not null)
                        {
                            devices.Add(parsed.Value);
                        }
                    }
                    finally
                    {
                        Release(device);
                    }
                }

                return devices;
            }
            finally
            {
                Release(collection);
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    public static string? GetDefaultId(EDataFlow flow, ERole role, AppLog? log = null)
    {
        var enumerator = CreateEnumerator(log);
        if (enumerator == nint.Zero)
        {
            return null;
        }

        try
        {
            var getDefault = GetSlot<GetDefaultAudioEndpoint>(enumerator, 4);
            nint device;
            var hr = getDefault(enumerator, (int)flow, (int)role, &device);
            if (hr < 0 || device == nint.Zero)
            {
                if (hr != HResult.ENotFound)
                {
                    log?.Warn($"GetDefaultAudioEndpoint({flow},{role}) failed. HRESULT=0x{hr:X8}");
                }

                return null;
            }

            try
            {
                return ReadDevice(device, log)?.Id;
            }
            finally
            {
                Release(device);
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    public static void SetDefaultEndpoint(string deviceId, ERole[] roles, AppLog? log = null)
    {
        Exception? last = null;
        foreach (var iid in PolicyIids)
        {
            var localIid = iid;
            var clsid = PolicyClsid;
            var createHr = NativeMethods.CoCreateInstance(
                ref clsid,
                nint.Zero,
                NativeMethods.ClsctxInprocServer,
                ref localIid,
                out var ptr);
            if (createHr < 0 || ptr == nint.Zero)
            {
                log?.Warn($"PolicyConfig CoCreateInstance({iid}) failed. HRESULT=0x{createHr:X8}");
                continue;
            }

            try
            {
                var setDefault = GetSlot<SetDefaultEndpointFn>(ptr, 13);
                foreach (var role in roles)
                {
                    var result = setDefault(ptr, deviceId, (int)role);
                    if (result < 0)
                    {
                        throw Marshal.GetExceptionForHR(result)
                            ?? new COMException($"PolicyConfig.SetDefaultEndpoint failed for role {role}.", result);
                    }
                }

                return;
            }
            catch (Exception ex)
            {
                last = ex;
                log?.Warn($"PolicyConfig.SetDefaultEndpoint via {iid} failed: {ex.Message}");
            }
            finally
            {
                Release(ptr);
            }
        }

        throw last ?? new COMException("Windows refused to change the default audio device.", HResult.EFail);
    }

    public static EndpointVolumeState? GetEndpointVolumeState(string deviceId, AppLog? log = null)
    {
        var endpoint = ActivateEndpointVolume(deviceId, log);
        if (endpoint == nint.Zero) return null;
        try
        {
            float volume = 0;
            int muted = 0;
            var volumeHr = GetSlot<GetMasterVolumeLevelScalarFn>(endpoint, 9)(endpoint, &volume);
            var muteHr = GetSlot<GetMuteFn>(endpoint, 15)(endpoint, &muted);
            if (volumeHr < 0 || muteHr < 0)
            {
                log?.Warn($"IAudioEndpointVolume read failed for '{deviceId}'. HRESULT=0x{(volumeHr < 0 ? volumeHr : muteHr):X8}");
                return null;
            }
            return new EndpointVolumeState(Math.Clamp(volume, 0.0f, 1.0f), muted != 0);
        }
        finally
        {
            Release(endpoint);
        }
    }

    public static ListenProbeSnapshot ProbeListenEndpoints(AppLog? log = null)
    {
        var enumerator = CreateEnumerator(log);
        if (enumerator == nint.Zero)
        {
            return new ListenProbeSnapshot(false, 0, 0, 1, []);
        }

        try
        {
            var enumFn = GetSlot<EnumAudioEndpoints>(enumerator, 3);
            nint collection;
            var hr = enumFn(enumerator, (int)EDataFlow.eCapture, CoreAudioConstants.DeviceStateActive, &collection);
            if (hr < 0 || collection == nint.Zero)
            {
                log?.Warn($"Listen probe EnumAudioEndpoints failed. HRESULT=0x{hr:X8}");
                return new ListenProbeSnapshot(false, 0, 0, 1, []);
            }

            try
            {
                var getCount = GetSlot<GetCount>(collection, 3);
                uint count;
                if (getCount(collection, &count) < 0)
                    return new ListenProbeSnapshot(false, 0, 0, 1, []);
                var item = GetSlot<CollectionItem>(collection, 4);
                var endpoints = new List<ListenEndpointProbe>((int)count);
                var unknown = 0;
                var enabled = 0;
                for (uint index = 0; index < count; index++)
                {
                    nint device;
                    if (item(collection, index, &device) < 0 || device == nint.Zero)
                    {
                        unknown++;
                        continue;
                    }
                    try
                    {
                        var raw = ReadDevice(device, log);
                        if (raw is null)
                        {
                            unknown++;
                            continue;
                        }
                        var probe = ReadListenProbe(device, raw.Value, log);
                        endpoints.Add(probe);
                        if (probe.Enabled) enabled++;
                        if (probe.Unknown) unknown++;
                    }
                    finally
                    {
                        Release(device);
                    }
                }
                return new ListenProbeSnapshot(true, endpoints.Count, enabled, unknown, endpoints);
            }
            finally
            {
                Release(collection);
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    public static void SetEndpointVolumeState(
        string deviceId,
        bool? muted,
        float? volumeScalar,
        AppLog? log = null)
    {
        var endpoint = ActivateEndpointVolume(deviceId, log);
        if (endpoint == nint.Zero)
            throw new COMException($"Audio endpoint is unavailable: {deviceId}", HResult.ENotFound);
        try
        {
            if (volumeScalar.HasValue)
            {
                var clamped = Math.Clamp(volumeScalar.Value, 0.0f, 1.0f);
                var hr = GetSlot<SetMasterVolumeLevelScalarFn>(endpoint, 7)(endpoint, clamped, null);
                HResult.ThrowIfFailed(hr, $"Could not set endpoint volume for {deviceId}.");
            }
            if (muted.HasValue)
            {
                var hr = GetSlot<SetMuteFn>(endpoint, 14)(endpoint, muted.Value ? 1 : 0, null);
                HResult.ThrowIfFailed(hr, $"Could not set endpoint mute for {deviceId}.");
            }
        }
        finally
        {
            Release(endpoint);
        }
    }

    public static nint CreateNotificationEnumerator(AppLog? log = null) => CreateEnumerator(log);

    public static int RegisterNotifications(nint enumerator, nint client)
    {
        if (enumerator == nint.Zero || client == nint.Zero)
        {
            return HResult.EPointer;
        }

        var register = GetSlot<RegisterNotificationsFn>(enumerator, 6);
        return register(enumerator, client);
    }

    public static int UnregisterNotifications(nint enumerator, nint client)
    {
        if (enumerator == nint.Zero || client == nint.Zero)
        {
            return HResult.EPointer;
        }

        var unregister = GetSlot<UnregisterNotificationsFn>(enumerator, 7);
        return unregister(enumerator, client);
    }

    public static void ReleaseCom(nint comObject) => Release(comObject);

    private static RawDevice? ReadDevice(nint device, AppLog? log)
    {
        var getId = GetSlot<GetId>(device, 5);
        var getState = GetSlot<GetState>(device, 6);
        nint idPtr;
        var idHr = getId(device, &idPtr);
        if (idHr < 0 || idPtr == nint.Zero)
        {
            log?.Warn($"IMMDevice.GetId failed. HRESULT=0x{idHr:X8}");
            return null;
        }

        try
        {
            uint state = 0;
            getState(device, &state);
            var id = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new RawDevice(id, ReadFriendlyName(device) ?? id, state);
        }
        finally
        {
            Marshal.FreeCoTaskMem(idPtr);
        }
    }

    private static string? ReadFriendlyName(nint device)
    {
        var openStore = GetSlot<OpenPropertyStore>(device, 4);
        nint store;
        if (openStore(device, CoreAudioConstants.StgmRead, &store) < 0 || store == nint.Zero)
        {
            return null;
        }

        try
        {
            var getValue = GetSlot<GetPropertyValue>(store, 5);
            foreach (var keySource in new[] { PropertyKey.PkeyDeviceFriendlyName, PropertyKey.PkeyDeviceDescription })
            {
                var key = keySource;
                PropVariant value = default;
                if (getValue(store, &key, &value) < 0)
                {
                    continue;
                }

                try
                {
                    var name = value.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
                finally
                {
                    value.Clear();
                }
            }

            return null;
        }
        finally
        {
            Release(store);
        }
    }

    private static nint CreateEnumerator(AppLog? log)
    {
        var clsid = EnumeratorClsid;
        var iid = EnumeratorIid;
        var hr = NativeMethods.CoCreateInstance(
            ref clsid,
            nint.Zero,
            NativeMethods.ClsctxInprocServer,
            ref iid,
            out var ptr);
        if (hr < 0 || ptr == nint.Zero)
        {
            log?.Error($"MMDeviceEnumerator CoCreateInstance failed. HRESULT=0x{hr:X8}");
            return nint.Zero;
        }

        return ptr;
    }

    private static ListenEndpointProbe ReadListenProbe(nint device, RawDevice raw, AppLog? log)
    {
        var openStore = GetSlot<OpenPropertyStore>(device, 4);
        nint store;
        var openHr = openStore(device, CoreAudioConstants.StgmRead, &store);
        if (openHr < 0 || store == nint.Zero)
            return new ListenEndpointProbe(raw.Id, raw.Name, false, false, false, false, string.Empty, true);

        try
        {
            var getValue = GetSlot<GetPropertyValue>(store, 5);
            var settingsGuid = new Guid("24DBB0FC-9311-4B3D-9CF0-18FF155639D4");
            var checkboxKey = new PropertyKey { fmtid = settingsGuid, pid = 1 };
            PropVariant checkbox = default;
            var checkboxHr = getValue(store, &checkboxKey, &checkbox);
            var enabled = false;
            var unknown = false;
            var propertyKnown = checkboxHr >= 0;
            if (checkboxHr >= 0)
            {
                enabled = checkbox.vt switch
                {
                    11 => checkbox.data1 != nint.Zero,
                    17 => (byte)checkbox.data1 != 0,
                    18 => (ushort)checkbox.data1 != 0,
                    19 => (uint)checkbox.data1 != 0,
                    3 => (int)checkbox.data1 != 0,
                    _ => false
                };
                unknown = checkbox.vt is not (0 or 1 or 11 or 17 or 18 or 19 or 3);
            }
            checkbox.Clear();
            if (!enabled || unknown)
                return new ListenEndpointProbe(raw.Id, raw.Name, propertyKnown, enabled, true, false, string.Empty, unknown);

            var targetKnown = false;
            var usesDefault = false;
            var targetId = string.Empty;
            foreach (var pid in new uint[] { 0 })
            {
                var targetKey = new PropertyKey { fmtid = settingsGuid, pid = pid };
                PropVariant target = default;
                var targetHr = getValue(store, &targetKey, &target);
                if (targetHr < 0)
                {
                    target.Clear();
                    if (targetHr != HResult.ENotFound) unknown = true;
                    continue;
                }
                targetKnown = true;
                switch (target.vt)
                {
                    case 0:
                    case 1:
                        usesDefault = true;
                        break;
                    case 31 when target.data1 != nint.Zero:
                        targetId = Marshal.PtrToStringUni(target.data1) ?? string.Empty;
                        usesDefault = string.IsNullOrEmpty(targetId);
                        break;
                    case 8 when target.data1 != nint.Zero:
                        targetId = Marshal.PtrToStringBSTR(target.data1);
                        usesDefault = string.IsNullOrEmpty(targetId);
                        break;
                    default:
                        unknown = true;
                        break;
                }
                target.Clear();
                if (usesDefault || !string.IsNullOrWhiteSpace(targetId)) break;
            }
            if (!targetKnown) unknown = true;
            return new ListenEndpointProbe(raw.Id, raw.Name, propertyKnown, enabled,
                                           targetKnown, usesDefault, targetId, unknown);
        }
        catch (Exception ex)
        {
            log?.Warn($"Listen probe failed for '{raw.Id}': {ex.Message}");
            return new ListenEndpointProbe(raw.Id, raw.Name, false, false, false, false, string.Empty, true);
        }
        finally
        {
            Release(store);
        }
    }

    private static nint ActivateEndpointVolume(string deviceId, AppLog? log)
    {
        var enumerator = CreateEnumerator(log);
        if (enumerator == nint.Zero) return nint.Zero;
        try
        {
            nint device;
            var getDeviceHr = GetSlot<GetDeviceFn>(enumerator, 5)(enumerator, deviceId, &device);
            if (getDeviceHr < 0 || device == nint.Zero)
            {
                log?.Warn($"IMMDeviceEnumerator.GetDevice failed for '{deviceId}'. HRESULT=0x{getDeviceHr:X8}");
                return nint.Zero;
            }
            try
            {
                var iid = CoreAudioConstants.IAudioEndpointVolumeIid;
                nint endpoint;
                var activateHr = GetSlot<ActivateFn>(device, 3)(
                    device,
                    &iid,
                    NativeMethods.ClsctxInprocServer,
                    nint.Zero,
                    &endpoint);
                if (activateHr < 0 || endpoint == nint.Zero)
                {
                    log?.Warn($"IMMDevice.Activate(IAudioEndpointVolume) failed for '{deviceId}'. HRESULT=0x{activateHr:X8}");
                    return nint.Zero;
                }
                return endpoint;
            }
            finally
            {
                Release(device);
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    private static T GetSlot<T>(nint comObject, int index) where T : Delegate
    {
        var vtbl = *(nint**)comObject;
        return Marshal.GetDelegateForFunctionPointer<T>(vtbl[index]);
    }

    private static void Release(nint comObject)
    {
        if (comObject == nint.Zero)
        {
            return;
        }

        var release = GetSlot<ReleaseFn>(comObject, 2);
        release(comObject);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAudioEndpoints(nint self, int dataFlow, uint stateMask, nint* collection);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultAudioEndpoint(nint self, int dataFlow, int role, nint* device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceFn(nint self, [MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint* device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateFn(nint self, Guid* iid, uint classContext, nint activationParameters, nint* instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCount(nint self, uint* count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CollectionItem(nint self, uint index, nint* device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenPropertyStore(nint self, uint access, nint* store);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetId(nint self, nint* id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetState(nint self, uint* state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertyValue(nint self, PropertyKey* key, PropVariant* value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefaultEndpointFn(nint self, [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegisterNotificationsFn(nint self, nint client);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnregisterNotificationsFn(nint self, nint client);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMasterVolumeLevelScalarFn(nint self, float level, Guid* eventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMasterVolumeLevelScalarFn(nint self, float* level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMuteFn(nint self, int muted, Guid* eventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMuteFn(nint self, int* muted);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(nint self);

    internal readonly record struct RawDevice(string Id, string Name, uint State);
    internal readonly record struct EndpointVolumeState(float VolumeScalar, bool Muted);
    internal readonly record struct ListenEndpointProbe(
        string Id,
        string Name,
        bool PropertyKnown,
        bool Enabled,
        bool TargetKnown,
        bool UsesDefault,
        string TargetId,
        bool Unknown);
    internal readonly record struct ListenProbeSnapshot(
        bool Available,
        int EndpointCount,
        int EnabledCount,
        int UnknownCount,
        IReadOnlyList<ListenEndpointProbe> Endpoints);
}
