using UnifiedAudio.Core.Persistence;

namespace UnifiedAudio.Core.Tests;

public sealed class BoundedLogTests
{
    [Fact]
    public void RotationBoundsDiskUsageAndKeepsNewestRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "test.log");
            // Existing unbounded logs must also be reclaimed.
            File.WriteAllText(path, new string('x', 10000));
            File.WriteAllText(path + ".2", new string('x', 10000));
            var log = new BoundedLogFile(path, 1024);
            for (var i = 0; i < 100; i++) log.Append($"Record {i}: " + new string('ñ', 100));
            log.Append(new string('x', 20000));
            log.Append("Newest record");
            var files = new DirectoryInfo(directory).GetFiles();
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(file.Length, 1, 1024));
            Assert.Contains("Newest record", File.ReadAllText(path));
            Assert.Contains("Record 99", string.Join("", files.Select(f => File.ReadAllText(f.FullName))));
        }
        finally { Directory.Delete(directory, true); }
    }
}
