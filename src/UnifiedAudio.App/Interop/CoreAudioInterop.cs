using System.Runtime.InteropServices;

namespace UnifiedAudio.Interop;

internal static class CoreAudioConstants
{
    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid ImmDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid ImmNotificationClientIid = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    public static readonly Guid IAudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    public static readonly Guid IPolicyConfigIid = new("F8679F50-850A-41CF-9C72-430F290290C8");
    public static readonly Guid IPolicyConfigVistaIid = new("568B9108-44BF-40B4-9006-86AFE5B5A620");
    public static readonly Guid IPolicyConfig10Iid = new("CA286FC3-91FD-42C3-8E9B-CAAFA66242E3");
    public const uint DeviceStateActive = 0x00000001;
    public const uint DeviceStateDisabled = 0x00000002;
    public const uint DeviceStateNotPresent = 0x00000004;
    public const uint DeviceStateUnplugged = 0x00000008;
    public const uint DeviceStateMaskAll = 0x0000000F;
    public const uint StgmRead = 0x00000000;
}

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;

    public static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    public static readonly PropertyKey PkeyDeviceDescription = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 2
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public nint data1;
    public nint data2;

    public string? GetString()
    {
        const ushort vtLpWStr = 31;
        return vt == vtLpWStr && data1 != nint.Zero
            ? Marshal.PtrToStringUni(data1)
            : null;
    }

    public void Clear()
    {
        PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}

internal static class HResult
{
    public const int SOk = 0;
    public const int ENotFound = unchecked((int)0x80070490);
    public const int EPointer = unchecked((int)0x80004003);
    public const int EFail = unchecked((int)0x80004005);
    public const int ENoInterface = unchecked((int)0x80004002);

    public static void ThrowIfFailed(int hr, string message)
    {
        if (hr < 0)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new COMException(message, hr);
        }
    }
}
