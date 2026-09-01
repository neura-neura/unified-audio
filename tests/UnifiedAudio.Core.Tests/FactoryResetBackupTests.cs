using UnifiedAudio.Core.Persistence;

namespace UnifiedAudio.Core.Tests;

public sealed class FactoryResetBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnifiedAudio.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MovesOnlyAllowlistedStateAndCreatesRecoverableManifest()
    {
        var local = Path.Combine(_root, "local");
        var roaming = Path.Combine(_root, "roaming");
        var backups = Path.Combine(local, "factory-reset-backups");
        Directory.CreateDirectory(Path.Combine(local, "logs"));
        Directory.CreateDirectory(Path.Combine(roaming, "logs"));
        WriteState(local, "settings.json", "old settings");
        WriteState(local, "settings.bak.json", "old settings backup");
        WriteState(local, "unrelated.txt", "keep me");
        WriteState(roaming, "config.xml", "old engine");
        WriteState(roaming, "config.bak.xml", "old engine backup");
        WriteState(roaming, "plugin_cache.xml", "old cache");
        WriteState(roaming, "plugin_cache.xml.bak", "old cache backup");

        var reset = new FactoryResetBackup(
            local,
            roaming,
            backups,
            () => new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var result = reset.Execute(startWithWindowsPreserved: true, () =>
            File.WriteAllText(Path.Combine(local, "settings.json"), "fresh; startup=true"));

        Assert.True(result.StartWithWindowsPreserved);
        Assert.Equal(6, result.Files.Count);
        Assert.Equal("fresh; startup=true", File.ReadAllText(Path.Combine(local, "settings.json")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(local, "unrelated.txt")));
        Assert.True(Directory.Exists(Path.Combine(local, "logs")));
        Assert.True(Directory.Exists(Path.Combine(roaming, "logs")));
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "shell", "settings.json")));
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "engine", "plugin_cache.xml.bak")));
        var manifest = File.ReadAllText(Path.Combine(result.BackupDirectory, "manifest.json"));
        Assert.Contains("startWithWindowsPreserved", manifest);
        Assert.Contains("plugin_cache.xml", manifest);
    }

    [Fact]
    public void RestoresEveryOriginalWhenFreshInitializationFails()
    {
        var local = Path.Combine(_root, "rollback-local");
        var roaming = Path.Combine(_root, "rollback-roaming");
        WriteState(local, "settings.json", "original settings");
        WriteState(roaming, "config.xml", "original engine");

        var reset = new FactoryResetBackup(local, roaming, Path.Combine(_root, "rollback-backups"));
        Assert.Throws<InvalidOperationException>(() => reset.Execute(false, () =>
        {
            File.WriteAllText(Path.Combine(local, "settings.json"), "incomplete reset");
            throw new InvalidOperationException("Injected initialization failure.");
        }));

        Assert.Equal("original settings", File.ReadAllText(Path.Combine(local, "settings.json")));
        Assert.Equal("original engine", File.ReadAllText(Path.Combine(roaming, "config.xml")));
    }

    private static void WriteState(string directory, string name, string contents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), contents);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnifiedAudio.Tests"));
        if (resolved.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
