using UnifiedAudio.Core.Audio;

namespace UnifiedAudio.Core.Tests;

public sealed class MuteGateTests
{
    [Fact]
    public void SettledMuteProducesExactDigitalZero()
    {
        var gate = new MuteGate(rampSamples: 4);
        gate.SetMuted(true);
        var first = Enumerable.Repeat(1f, 8).ToArray();
        gate.Process(first);

        var settled = Enumerable.Repeat(0.75f, 64).ToArray();
        gate.Process(settled);

        Assert.True(gate.IsMuted);
        Assert.All(settled, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void UnmuteRampsInsteadOfJumping()
    {
        var gate = new MuteGate(rampSamples: 4);
        gate.SetMuted(true);
        var mute = Enumerable.Repeat(1f, 8).ToArray();
        gate.Process(mute);
        gate.SetMuted(false);
        var unmute = Enumerable.Repeat(1f, 4).ToArray();

        gate.Process(unmute);

        Assert.Equal([0.25f, 0.5f, 0.75f, 1f], unmute);
    }
}
