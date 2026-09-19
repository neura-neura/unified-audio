using System.Text.Json;
using UnifiedAudio.Core.Engine;

namespace UnifiedAudio.Core.Tests;

public sealed class EngineFrameCodecTests
{
    private sealed record Rule(string Key, string DisplayName, float Gain, bool Excluded);
    private sealed record Plugin(string Id, bool Bypassed, string StateBase64);

    [Fact]
    public void NestedRulesAndPluginsUseTheNativeCaseSensitiveContract()
    {
        var payload = EngineFrameCodec.SerializePayload(new
        {
            mode = "Both",
            rules = new[] { new Rule("C:\\Player.exe", "Player", 0.75f, false) },
            plugins = new[] { new Plugin("rnnoise", true, "YWJj") }
        });
        var decoded = EngineFrameCodec.Decode(EngineFrameCodec.Encode(new EngineMessage { Payload = payload }));
        var rule = decoded.Payload.GetProperty("rules")[0];
        Assert.Equal("C:\\Player.exe", rule.GetProperty("key").GetString());
        Assert.Equal(0.75f, rule.GetProperty("gain").GetSingle());
        Assert.False(rule.GetProperty("excluded").GetBoolean());
        var plugin = decoded.Payload.GetProperty("plugins")[0];
        Assert.Equal("rnnoise", plugin.GetProperty("id").GetString());
        Assert.True(plugin.GetProperty("bypassed").GetBoolean());
        Assert.Equal("YWJj", plugin.GetProperty("stateBase64").GetString());
    }
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
