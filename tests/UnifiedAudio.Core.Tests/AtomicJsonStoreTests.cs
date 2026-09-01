using UnifiedAudio.Core.Persistence;

namespace UnifiedAudio.Core.Tests;

public sealed class AtomicJsonStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UnifiedAudio.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTripAndBackupRecovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "settings.json");
        var store = new AtomicJsonStore<TestState>(path);
        await store.SaveAsync(new TestState { Value = "first" }, cancellationToken);
        await store.SaveAsync(new TestState { Value = "second" }, cancellationToken);

        Assert.Equal("second", (await store.LoadAsync(cancellationToken)).Value);

        await File.WriteAllTextAsync(path, "not json", cancellationToken);
        Assert.Equal("first", (await store.LoadAsync(cancellationToken)).Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public sealed class TestState
    {
        public string Value { get; init; } = string.Empty;
    }
}
