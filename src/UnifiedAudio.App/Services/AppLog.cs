using System.Text;

namespace UnifiedAudio.Services;

public sealed class AppLog
{
    private readonly UnifiedAudio.Core.Persistence.BoundedLogFile _writer;
    private readonly string _directory;
    private readonly string _filePath;

    public AppLog()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnifiedAudio",
            "logs");
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "unified-audio.log");
        _writer = new(_filePath);
    }

    public string DirectoryPath => _directory;
    public string FilePath => _filePath;
    public bool Verbose { get; set; }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void VerboseInfo(string message)
    {
        if (Verbose)
        {
            Write("DEBUG", message, null);
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [")
                .Append(level)
                .Append("] ")
                .Append(message);
            if (exception is not null)
            {
                builder.Append(" | ").Append(exception);
            }

            _writer.Append(builder.ToString());
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
