using UnifiedAudio.Core.Persistence;

namespace UnifiedAudio.Core.Tests;

public sealed class CaseInsensitiveDictionaryNormalizerTests
{
    [Fact]
    public void KeepsOrdinalFirstEntryWhenKeysDifferOnlyByCase()
    {
        var source = new Dictionary<string, int>
        {
            ["monitor-a"] = 1,
            ["MONITOR-A"] = 2,
            ["Monitor-B"] = 3
        };

        var normalized = CaseInsensitiveDictionaryNormalizer.Normalize(
            source,
            _ => true,
            value => value);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(2, normalized["monitor-a"]);
        Assert.Equal(3, normalized["monitor-b"]);
        Assert.Equal(["MONITOR-A", "Monitor-B"], normalized.Keys);
    }

    [Fact]
    public void OmitsBlankKeysAndExcludedValuesBeforeNormalizing()
    {
        var source = new Dictionary<string, int?>
        {
            [" "] = 1,
            ["ignored"] = null,
            ["kept"] = 4
        };

        var normalized = CaseInsensitiveDictionaryNormalizer.Normalize(
            source,
            value => value is not null,
            value => value!.Value + 1);

        Assert.Single(normalized);
        Assert.Equal(5, normalized["KEPT"]);
    }
}
