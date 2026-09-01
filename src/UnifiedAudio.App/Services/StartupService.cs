using UnifiedAudio.Models;
using Windows.ApplicationModel;
using System.Diagnostics;

namespace UnifiedAudio.Services;

public sealed class StartupService
{
    private readonly AppLog _log;
    private const string TaskId = "UnifiedAudioStartup";

    public StartupService(AppLog log)
    {
        _log = log;
    }

    public async Task ApplyAsync(AppSettings settings)
    {
        try
        {
            if (IsPackaged())
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (settings.StartWithWindows)
                {
                    if (task.State is StartupTaskState.Disabled)
                    {
                        await task.RequestEnableAsync();
                    }
                }
                else if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                {
                    task.Disable();
                }
                return;
            }

            ApplyStartupShortcut(settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to update Start with Windows.", ex);
            ApplyStartupShortcut(settings.StartWithWindows);
        }
    }

    public bool WasStartedByWindows()
    {
        try
        {
            var args = Program.LaunchArgs.Concat(Environment.GetCommandLineArgs());
            if (args.Any(a => a.Contains("startup", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // Ignore.
        }

        return false;
    }

    public bool ShouldShowInForeground()
    {
        try
        {
            return Program.LaunchArgs
                .Concat(Environment.GetCommandLineArgs())
                .Any(a => string.Equals(a, "--foreground", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task WaitForReadyAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!WasStartedByWindows() || settings.StartupGate == StartupGateMode.None) return;
        if (settings.StartupGate == StartupGateMode.Delay)
        {
            var delay = Math.Clamp(settings.StartupDelaySeconds, 0, 300);
            if (delay <= 0) return;
            _log.Info($"Startup gate: delaying engine initialization for {delay} seconds.");
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(settings.StartupWaitProcess?.Trim());
        if (string.IsNullOrWhiteSpace(processName))
        {
            _log.Warn("Startup process gate is enabled but no process is configured.");
            return;
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.StartupWaitTimeoutSeconds, 1, 180));
        var deadline = DateTime.UtcNow + timeout;
        _log.Info($"Startup gate: waiting up to {timeout.TotalSeconds:F0} seconds for '{processName}'.");
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length > 0)
                {
                    _log.Info($"Startup gate satisfied by '{processName}'.");
                    return;
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        _log.Warn($"Startup gate timed out waiting for '{processName}'; continuing safely.");
    }

    private void ApplyStartupShortcut(bool enabled)
    {
        try
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var shortcutPath = Path.Combine(startupFolder, "UnifiedAudio.lnk");
            if (!enabled)
            {
                if (File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                }
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                _log.Error("WScript.Shell is unavailable for startup shortcut creation.");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Arguments = "--startup";
            shortcut.Description = "UnifiedAudio";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to create or remove the startup shortcut.", ex);
        }
    }

    private static bool IsPackaged()
    {
        return AppIdentity.IsPackaged();
    }
}
