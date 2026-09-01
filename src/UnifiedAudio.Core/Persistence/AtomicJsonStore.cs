using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedAudio.Core.Persistence;

public sealed class AtomicJsonStore<T> where T : class, new()
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options;

    public AtomicJsonStore(string filePath, JsonSerializerOptions? options = null)
    {
        FilePath = Path.GetFullPath(filePath ?? throw new ArgumentNullException(nameof(filePath)));
        BackupPath = FilePath + ".bak";
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    public async Task<T> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return new T();
            }

            try
            {
                return await DeserializeAsync(FilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception primaryError) when (primaryError is JsonException or IOException)
            {
                if (!File.Exists(BackupPath))
                {
                    throw;
                }

                return await DeserializeAsync(BackupPath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath)
                ?? throw new InvalidOperationException("Settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath))
                {
                    File.Copy(FilePath, BackupPath, overwrite: true);
                }

                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> DeserializeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException("Settings document deserialized to null.");
    }
}
