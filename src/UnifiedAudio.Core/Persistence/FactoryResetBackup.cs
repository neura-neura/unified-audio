using System.Text.Json;

namespace UnifiedAudio.Core.Persistence;

public sealed record FactoryResetFile(
    string Category,
    string SourcePath,
    string BackupPath);

public sealed record FactoryResetResult(
    string BackupDirectory,
    IReadOnlyList<FactoryResetFile> Files,
    bool StartWithWindowsPreserved);

/// <summary>
/// Moves the exact UnifiedAudio settings/cache files into a recoverable backup.
/// Logs and every file outside the allowlist are deliberately left untouched.
/// </summary>
public sealed class FactoryResetBackup
{
    private static readonly string[] LocalFiles =
    [
        "settings.json",
        "settings.bak.json",
        "settings.json.tmp"
    ];

    private static readonly string[] RoamingFiles =
    [
        "config.xml",
        "config.bak.xml",
        "plugin_cache.xml",
        "plugin_cache.xml.bak"
    ];

    private readonly string _localDirectory;
    private readonly string _roamingDirectory;
    private readonly string _backupRoot;
    private readonly Func<DateTimeOffset> _clock;

    public FactoryResetBackup(
        string localDirectory,
        string roamingDirectory,
        string backupRoot,
        Func<DateTimeOffset>? clock = null)
    {
        _localDirectory = NormalizeDirectory(localDirectory, nameof(localDirectory));
        _roamingDirectory = NormalizeDirectory(roamingDirectory, nameof(roamingDirectory));
        _backupRoot = NormalizeDirectory(backupRoot, nameof(backupRoot));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FactoryResetResult Execute(bool startWithWindowsPreserved, Action initializeFreshSettings)
    {
        ArgumentNullException.ThrowIfNull(initializeFreshSettings);
        var createdUtc = _clock();
        var backupDirectory = Path.Combine(
            _backupRoot,
            createdUtc.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var moved = new List<FactoryResetFile>();

        try
        {
            MoveAllowlistedFiles(_localDirectory, "shell", LocalFiles, backupDirectory, moved);
            MoveAllowlistedFiles(_roamingDirectory, "engine", RoamingFiles, backupDirectory, moved);
            initializeFreshSettings();

            Directory.CreateDirectory(backupDirectory);
            var manifestPath = Path.Combine(backupDirectory, "manifest.json");
            var manifest = new
            {
                schemaVersion = 1,
                createdUtc,
                startWithWindowsPreserved,
                files = moved.Select(file => new
                {
                    file.Category,
                    source = file.SourcePath,
                    backup = Path.GetRelativePath(backupDirectory, file.BackupPath)
                })
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            return new FactoryResetResult(backupDirectory, moved.AsReadOnly(), startWithWindowsPreserved);
        }
        catch (Exception operationError)
        {
            var rollbackErrors = RollBack(moved, backupDirectory);
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Factory reset failed and one or more original files could not be restored.",
                    new[] { operationError }.Concat(rollbackErrors));
            }

            throw;
        }
    }

    private static void MoveAllowlistedFiles(
        string sourceDirectory,
        string category,
        IEnumerable<string> names,
        string backupDirectory,
        ICollection<FactoryResetFile> moved)
    {
        foreach (var name in names)
        {
            var source = Path.GetFullPath(Path.Combine(sourceDirectory, name));
            EnsureDirectChild(source, sourceDirectory);
            if (!File.Exists(source)) continue;

            var categoryDirectory = Path.Combine(backupDirectory, category);
            Directory.CreateDirectory(categoryDirectory);
            var destination = Path.GetFullPath(Path.Combine(categoryDirectory, name));
            EnsureDirectChild(destination, categoryDirectory);
            File.Move(source, destination, overwrite: false);
            moved.Add(new FactoryResetFile(category, source, destination));
        }
    }

    private static List<Exception> RollBack(IReadOnlyList<FactoryResetFile> moved, string backupDirectory)
    {
        var errors = new List<Exception>();
        for (var index = moved.Count - 1; index >= 0; index--)
        {
            var file = moved[index];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.SourcePath)!);
                if (File.Exists(file.SourcePath))
                {
                    var failedOutputDirectory = Path.Combine(backupDirectory, "failed-reset-output", file.Category);
                    Directory.CreateDirectory(failedOutputDirectory);
                    var failedOutput = Path.Combine(failedOutputDirectory, Path.GetFileName(file.SourcePath));
                    File.Move(file.SourcePath, failedOutput, overwrite: true);
                }

                if (File.Exists(file.BackupPath))
                    File.Move(file.BackupPath, file.SourcePath, overwrite: false);
            }
            catch (Exception rollbackError)
            {
                errors.Add(rollbackError);
            }
        }

        return errors;
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A directory path is required.", parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureDirectChild(string path, string expectedParent)
    {
        var actualParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("The file has no parent directory.")));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedParent));
        if (!string.Equals(actualParent, normalizedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Factory-reset path escaped its expected directory: {path}");
    }
}
