using System.Runtime.InteropServices;
using UnifiedAudio.Interop;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace UnifiedAudio.Services;

internal static class AppIdentity
{
    public const string AppUserModelId = "UnifiedAudio.Desktop";
    public const string DisplayName = "UnifiedAudio";
    public const string Author = "neura-neura";
    public const string AuthorUrl = "https://github.com/neura-neura";
    public const string RepositoryUrl = "https://github.com/neura-neura/unified-audio";
    public const string ReleasesApiUrl = "https://api.github.com/repos/neura-neura/unified-audio/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/neura-neura/unified-audio/releases";
    public const string InstallerAssetName = "UnifiedAudioSetup";

    public static void Initialize()
    {
        try
        {
            if (IsPackaged())
            {
                return;
            }

            EnsureInsightsResourceLoaded();
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            EnsureStartMenuShortcut();
            RestoreNotificationIdentity();
        }
        catch
        {
            // Identity setup is best-effort and must never block launch.
        }
    }

    public static bool IsPackaged()
    {
        try
        {
            return !string.IsNullOrEmpty(Package.Current?.Id?.Name);
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureInsightsResourceLoaded()
    {
        if (IsPackaged())
        {
            return;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.Insights.Resource.dll"),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "win-x64", "Microsoft.WindowsAppRuntime.Insights.Resource.dll"),
            Path.Combine(AppContext.BaseDirectory, "Runtime", "win-arm64", "Microsoft.WindowsAppRuntime.Insights.Resource.dll")
        };

        foreach (var candidate in candidates.Where(File.Exists))
        {
            if (NativeMethods.LoadLibrary(candidate) != nint.Zero)
            {
                return;
            }
        }
    }

    public static void PrepareNotificationIdentity()
    {
        if (IsPackaged())
        {
            return;
        }

        EnsureInsightsResourceLoaded();
        NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        EnsureStartMenuShortcut();
        RestoreNotificationIdentity();
    }

    public static void RestoreNotificationIdentity()
    {
        if (IsPackaged())
        {
            return;
        }

        try
        {
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId);
            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", DisplayName, RegistryValueKind.ExpandString);
            var iconPath = ResolveIconPath();
            if (File.Exists(iconPath))
            {
                key.SetValue("IconUri", iconPath, RegistryValueKind.ExpandString);
                key.SetValue("IconBackgroundColor", "0F766E");
            }

            var activator = key.GetValue("CustomActivator") as string;
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(activator) && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                using var server = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + activator.Trim() + @"\LocalServer32");
                server?.SetValue(null, "\"" + exe + "\" ----AppNotificationActivated:");
            }
        }
        catch
        {
            // Identity restore is best-effort.
        }
    }

    private static void EnsureStartMenuShortcut()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return;
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programs);
        var shortcutPath = Path.Combine(programs, DisplayName + ".lnk");
        var iconPath = ResolveIconPath();
        if (!File.Exists(iconPath))
        {
            iconPath = exe;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
        shortcut.Description = DisplayName;
        shortcut.IconLocation = iconPath;
        shortcut.Save();
        TryAssignAppUserModelId(shortcutPath);
    }

    private static string ResolveIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedAudio", "Assets", "AppIcon.ico")
        };
        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    }

    private static void TryAssignAppUserModelId(string shortcutPath)
    {
        var iid = typeof(IPropertyStore).GUID;
        var hr = SHGetPropertyStoreFromParsingName(shortcutPath, nint.Zero, GpsReadWrite, ref iid, out var store);
        if (hr < 0 || store is null)
        {
            return;
        }

        var value = PropVariant.FromString(AppUserModelId);
        try
        {
            var key = PkeyAppUserModelId;
            if (store.SetValue(ref key, ref value) >= 0)
            {
                store.Commit();
            }
        }
        finally
        {
            value.Clear();
            Marshal.ReleaseComObject(store);
        }
    }

    private const int GpsReadWrite = 0x00000002;

    private static readonly PropertyKey PkeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath,
        nint pbc,
        int flags,
        ref Guid riid,
        out IPropertyStore ppv);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint data1;
        public nint data2;

        public static PropVariant FromString(string value) => new()
        {
            vt = 31,
            data1 = Marshal.StringToCoTaskMemUni(value)
        };

        public void Clear() => PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
