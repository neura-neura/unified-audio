using UnifiedAudio.Interop;
using UnifiedAudio.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace UnifiedAudio;

public static class Program
{
    public const string InstanceKey = "UnifiedAudio.SingleInstance";
    public static IReadOnlyList<string> LaunchArgs { get; private set; } = [];

    [STAThread]
    private static void Main(string[] args)
    {
        LaunchArgs = args;
        if (args.Any(a => string.Equals(a, "--mixer-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
            NativeMethods.CoInitializeEx(0, NativeMethods.CoInitApartmentThreaded);
            Environment.ExitCode = MixerSelfTest.RunAsync().GetAwaiter().GetResult() ? 0 : 1;
            return;
        }
        if (args.Any(a => string.Equals(a, "--overlay-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
            Environment.ExitCode = OverlaySelfTest.Run() ? 0 : 1;
            return;
        }
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
            Environment.ExitCode = AudioSelfTest.Run() ? 0 : 1;
            return;
        }

        AppIdentity.Initialize();
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var current = AppInstance.GetCurrent();
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            mainInstance.RedirectActivationToAsync(current.GetActivatedEventArgs()).AsTask().GetAwaiter().GetResult();
            return;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
