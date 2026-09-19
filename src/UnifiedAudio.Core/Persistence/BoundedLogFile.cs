using System.Text;

namespace UnifiedAudio.Core.Persistence;

public sealed class BoundedLogFile(string path, int maximumBytes = 2 * 1024 * 1024)
{
    private readonly object _sync = new();

    public void Append(string message)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // Limit individual records too, including unusually large exception messages.
            var text = message.Length > 8192 ? message[..8192] + " [truncated]" : message;
            var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
            if (bytes.Length > maximumBytes) bytes = Encoding.UTF8.GetBytes("[record exceeds log limit]\n");
            foreach (var file in new[] { path, path + ".1", path + ".2" })
                if (File.Exists(file) && new FileInfo(file).Length > maximumBytes) File.Delete(file);
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maximumBytes)
            {
                if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", true);
                File.Move(path, path + ".1", true);
            }
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
    }
}
