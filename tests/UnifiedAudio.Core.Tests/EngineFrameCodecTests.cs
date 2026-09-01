using System.Text.Json;
using UnifiedAudio.Core.Engine;

namespace UnifiedAudio.Core.Tests;

public sealed class EngineFrameCodecTests
{
    [Fact]
    public void RoundTripPreservesEnvelope()
    {
        var message = new EngineMessage
        {
            Sequence = 42,
            Kind = EngineMessageKind.Command,
            Name = "graph.applyPatch",
            Payload = JsonSerializer.SerializeToElement(new { revision = 7 })
        };

        var decoded = EngineFrameCodec.Decode(EngineFrameCodec.Encode(message));

        Assert.Equal(message.MessageId, decoded.MessageId);
        Assert.Equal(42, decoded.Sequence);
        Assert.Equal("graph.applyPatch", decoded.Name);
        Assert.Equal(7, decoded.Payload.GetProperty("revision").GetInt32());
    }

    [Fact]
    public void RejectsInvalidLength()
    {
        Assert.Throws<InvalidDataException>(() => EngineFrameCodec.Decode([20, 0, 0, 0, 1]));
    }
}
