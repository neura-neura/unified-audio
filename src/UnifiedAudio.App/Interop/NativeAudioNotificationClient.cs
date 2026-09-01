using System.Runtime.InteropServices;
using UnifiedAudio.Services;

namespace UnifiedAudio.Interop;

internal sealed unsafe class NativeAudioNotificationClient : IDisposable
{
    private readonly AppLog _log;
    private readonly Action _changed;
    private readonly QueryInterfaceDelegate _queryInterface;
    private readonly AddRefDelegate _addRef;
    private readonly ReleaseDelegate _release;
    private readonly DeviceEventDelegate _onDeviceStateChanged;
    private readonly DeviceIdDelegate _onDeviceAdded;
    private readonly DeviceIdDelegate _onDeviceRemoved;
    private readonly DefaultDeviceChangedDelegate _onDefaultDeviceChanged;
    private readonly PropertyChangedDelegate _onPropertyValueChanged;
    private nint _vtbl;
    private nint _unknown;
    private int _refCount = 1;
    private bool _disposed;

    public NativeAudioNotificationClient(AppLog log, Action changed)
    {
        _log = log;
        _changed = changed;
        _queryInterface = QueryInterface;
        _addRef = AddRef;
        _release = Release;
        _onDeviceStateChanged = OnDeviceStateChanged;
        _onDeviceAdded = OnDeviceAdded;
        _onDeviceRemoved = OnDeviceRemoved;
        _onDefaultDeviceChanged = OnDefaultDeviceChanged;
        _onPropertyValueChanged = OnPropertyValueChanged;

        _vtbl = Marshal.AllocHGlobal(sizeof(nint) * 8);
        var slots = (nint*)_vtbl;
        slots[0] = Marshal.GetFunctionPointerForDelegate(_queryInterface);
        slots[1] = Marshal.GetFunctionPointerForDelegate(_addRef);
        slots[2] = Marshal.GetFunctionPointerForDelegate(_release);
        slots[3] = Marshal.GetFunctionPointerForDelegate(_onDeviceStateChanged);
        slots[4] = Marshal.GetFunctionPointerForDelegate(_onDeviceAdded);
        slots[5] = Marshal.GetFunctionPointerForDelegate(_onDeviceRemoved);
        slots[6] = Marshal.GetFunctionPointerForDelegate(_onDefaultDeviceChanged);
        slots[7] = Marshal.GetFunctionPointerForDelegate(_onPropertyValueChanged);

        _unknown = Marshal.AllocHGlobal(sizeof(nint));
        *(nint*)_unknown = _vtbl;
    }

    public nint Pointer => _unknown;

    private int QueryInterface(nint self, Guid* iid, nint* ppv)
    {
        if (ppv == null)
        {
            return HResult.EPointer;
        }

        var requested = *iid;
        if (requested == IUnknownIid || requested == CoreAudioConstants.ImmNotificationClientIid)
        {
            *ppv = self;
            AddRef(self);
            return HResult.SOk;
        }

        *ppv = nint.Zero;
        return HResult.ENoInterface;
    }

    private uint AddRef(nint self)
    {
        _ = self;
        return (uint)Interlocked.Increment(ref _refCount);
    }

    private uint Release(nint self)
    {
        _ = self;
        var value = Interlocked.Decrement(ref _refCount);
        return (uint)Math.Max(value, 0);
    }

    private int OnDeviceStateChanged(nint self, nint deviceId, uint newState)
    {
        _ = self;
        _ = deviceId;
        _ = newState;
        Notify();
        return HResult.SOk;
    }

    private int OnDeviceAdded(nint self, nint deviceId)
    {
        _ = self;
        _ = deviceId;
        Notify();
        return HResult.SOk;
    }

    private int OnDeviceRemoved(nint self, nint deviceId)
    {
        _ = self;
        _ = deviceId;
        Notify();
        return HResult.SOk;
    }

    private int OnDefaultDeviceChanged(nint self, int flow, int role, nint deviceId)
    {
        _ = self;
        _ = flow;
        _ = role;
        _ = deviceId;
        Notify();
        return HResult.SOk;
    }

    private int OnPropertyValueChanged(nint self, nint deviceId, PropertyKey key)
    {
        _ = self;
        _ = deviceId;
        _ = key;
        return HResult.SOk;
    }

    private void Notify()
    {
        try
        {
            _changed();
        }
        catch (Exception ex)
        {
            _log.Error("Audio device notification callback failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_unknown != nint.Zero)
        {
            Marshal.FreeHGlobal(_unknown);
            _unknown = nint.Zero;
        }

        if (_vtbl != nint.Zero)
        {
            Marshal.FreeHGlobal(_vtbl);
            _vtbl = nint.Zero;
        }

        GC.KeepAlive(_queryInterface);
        GC.KeepAlive(_addRef);
        GC.KeepAlive(_release);
        GC.KeepAlive(_onDeviceStateChanged);
        GC.KeepAlive(_onDeviceAdded);
        GC.KeepAlive(_onDeviceRemoved);
        GC.KeepAlive(_onDefaultDeviceChanged);
        GC.KeepAlive(_onPropertyValueChanged);
    }

    private static readonly Guid IUnknownIid = new("00000000-0000-0000-C000-000000000046");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(nint self, Guid* iid, nint* ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceEventDelegate(nint self, nint deviceId, uint newState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceIdDelegate(nint self, nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DefaultDeviceChangedDelegate(nint self, int flow, int role, nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyChangedDelegate(nint self, nint deviceId, PropertyKey key);
}
