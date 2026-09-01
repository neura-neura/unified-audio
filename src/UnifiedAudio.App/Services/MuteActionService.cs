using System.Diagnostics;
using System.Text;
using UnifiedAudio.Models;
using System.Runtime.InteropServices;

namespace UnifiedAudio.Services;

public sealed class MuteActionService
{
    private readonly AppLog _log;

    public MuteActionService(AppLog log)
    {
        _log = log;
    }

    public async Task ExecuteAsync(AudioProfile profile, EngineSnapshot snapshot, string origin)
    {
        foreach (var action in profile.MuteActions.Where(action =>
                     action.Enabled
                     && action.Trusted
                     && (snapshot.MutedRequested ? action.RunWhenMuted : action.RunWhenUnmuted)))
        {
            try
            {
                await ExecuteOneAsync(action, profile, snapshot, origin).ConfigureAwait(false);
                _log.Info($"Mute action '{action.Name}' completed. Type={action.Type}; origin={origin}.");
            }
            catch (Exception ex)
            {
                _log.Error($"Mute action '{action.Name}' failed. Type={action.Type}.", ex);
            }
        }
    }

    private async Task ExecuteOneAsync(
        MuteActionDefinition action,
        AudioProfile profile,
        EngineSnapshot snapshot,
        string origin)
    {
        if (action.Type == MuteActionType.Voicemeeter)
        {
            await Task.Run(() => ExecuteVoicemeeter(action, snapshot.MutedRequested)).ConfigureAwait(false);
            return;
        }

        ProcessStartInfo startInfo;
        if (action.Type == MuteActionType.Program)
        {
            if (string.IsNullOrWhiteSpace(action.FileName))
                throw new InvalidOperationException("Program action has no executable.");
            var executable = Path.GetFullPath(Environment.ExpandEnvironmentVariables(action.FileName));
            if (!File.Exists(executable))
                throw new FileNotFoundException("Program action executable was not found.", executable);
            startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = Substitute(action.Arguments, profile, snapshot, origin),
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }
        else if (action.Type == MuteActionType.PowerShell)
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershell))
                throw new FileNotFoundException("Windows PowerShell was not found.", powershell);
            var script = Substitute(action.PowerShellScript, profile, snapshot, origin);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported mute action type: {action.Type}.");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The mute action process did not start.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(action.TimeoutSeconds, 1, 300));
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Mute action exceeded {timeout.TotalSeconds:F0} seconds.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Mute action exited with code {process.ExitCode}.");
    }

    private static void ExecuteVoicemeeter(MuteActionDefinition action, bool muted)
    {
        if (string.IsNullOrWhiteSpace(action.VoicemeeterParameter))
            throw new InvalidOperationException("Voicemeeter action has no parameter name.");
        var dll = ResolveVoicemeeterDll(action.FileName);
        var module = NativeLibrary.Load(dll);
        try
        {
            var login = Marshal.GetDelegateForFunctionPointer<VoicemeeterLogin>(
                NativeLibrary.GetExport(module, "VBVMR_Login"));
            var logout = Marshal.GetDelegateForFunctionPointer<VoicemeeterLogout>(
                NativeLibrary.GetExport(module, "VBVMR_Logout"));
            var setParameter = Marshal.GetDelegateForFunctionPointer<VoicemeeterSetParameterFloat>(
                NativeLibrary.GetExport(module, "VBVMR_SetParameterFloat"));
            var isDirty = Marshal.GetDelegateForFunctionPointer<VoicemeeterIsParametersDirty>(
                NativeLibrary.GetExport(module, "VBVMR_IsParametersDirty"));
            var loginResult = login();
            if (loginResult < 0) throw new InvalidOperationException($"Voicemeeter login failed ({loginResult}).");
            if (loginResult == 1) throw new InvalidOperationException("Voicemeeter is installed but is not running.");
            try
            {
                _ = isDirty();
                var value = muted ? action.VoicemeeterMutedValue : action.VoicemeeterUnmutedValue;
                var result = setParameter(action.VoicemeeterParameter, value);
                if (result < 0)
                    throw new InvalidOperationException($"Voicemeeter rejected '{action.VoicemeeterParameter}' ({result}).");
            }
            finally
            {
                logout();
            }
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    private static string ResolveVoicemeeterDll(string configuredPath)
    {
        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(configuredPath ?? string.Empty),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VB", "Voicemeeter", "VoicemeeterRemote64.dll")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("VoicemeeterRemote64.dll was not found. Install Voicemeeter or choose its Remote DLL.");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VoicemeeterLogin();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VoicemeeterLogout();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VoicemeeterIsParametersDirty();
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int VoicemeeterSetParameterFloat([MarshalAs(UnmanagedType.LPStr)] string parameter, float value);

    private static string Substitute(
        string template,
        AudioProfile profile,
        EngineSnapshot snapshot,
        string origin)
    {
        var state = snapshot.MutedRequested ? "muted" : "unmuted";
        return (template ?? string.Empty)
            .Replace("{profile}", profile.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{state}", state, StringComparison.OrdinalIgnoreCase)
            .Replace("{isMuted}", snapshot.MutedRequested ? "true" : "false", StringComparison.OrdinalIgnoreCase)
            .Replace("{origin}", origin, StringComparison.OrdinalIgnoreCase)
            .Replace("{inputDevice}", snapshot.InputDeviceName, StringComparison.OrdinalIgnoreCase)
            .Replace("{outputDevice}", snapshot.OutputDeviceName, StringComparison.OrdinalIgnoreCase);
    }
}
