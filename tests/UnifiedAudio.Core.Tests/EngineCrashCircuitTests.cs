using UnifiedAudio.Core.Engine;

namespace UnifiedAudio.Core.Tests;

public sealed class EngineCrashCircuitTests
{
    [Fact]
    public void ThreeRapidExitsOpenCircuitUntilWindowExpires()
    {
        var circuit = new EngineCrashCircuit(TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.Parse("2026-08-16T00:00:00Z");

        var first = circuit.RecordExit(start);
        var second = circuit.RecordExit(start.AddSeconds(1));
        var third = circuit.RecordExit(start.AddSeconds(2));

        Assert.Equal(1, first.RecentExitCount);
        Assert.Equal(TimeSpan.FromSeconds(1), first.RetryAfter);
        Assert.Equal(2, second.RecentExitCount);
        Assert.Equal(TimeSpan.FromSeconds(2), second.RetryAfter);
        Assert.True(third.CircuitOpen);
        Assert.Equal(TimeSpan.FromSeconds(28), third.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(18), circuit.RecoveryDelay(start.AddSeconds(12)));
        Assert.Equal(TimeSpan.Zero, circuit.RecoveryDelay(start.AddSeconds(30)));
    }

    [Fact]
    public void ExitAfterWindowStartsFreshSequence()
    {
        var circuit = new EngineCrashCircuit(TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
        circuit.RecordExit(start);

        var later = circuit.RecordExit(start.AddSeconds(31));

        Assert.Equal(1, later.RecentExitCount);
        Assert.False(later.CircuitOpen);
        Assert.Equal(TimeSpan.FromSeconds(1), later.RetryAfter);
    }
}
