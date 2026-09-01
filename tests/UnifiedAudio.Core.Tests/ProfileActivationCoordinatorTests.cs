using UnifiedAudio.Core.Models;
using UnifiedAudio.Core.Profiles;

namespace UnifiedAudio.Core.Tests;

public sealed class ProfileActivationCoordinatorTests
{
    [Fact]
    public async Task ValidatesEverythingBeforeMutation()
    {
        var good = new FakeParticipant("graph", 100);
        var invalid = new FakeParticipant("defaults", 500) { ValidationError = "missing device" };
        var coordinator = new ProfileActivationCoordinator([good, invalid]);

        var report = await coordinator.ActivateAsync(
            new UnifiedProfile { Name = "Test" },
            TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Single(report.ValidationIssues);
        Assert.Empty(good.Events);
        Assert.Empty(invalid.Events);
    }

    [Fact]
    public async Task RollsBackAppliedStepsInReverseOrder()
    {
        var events = new List<string>();
        var graph = new FakeParticipant("graph", 100, events);
        var mute = new FakeParticipant("mute", 300, events);
        var defaults = new FakeParticipant("defaults", 500, events) { ApplyError = "denied" };
        var coordinator = new ProfileActivationCoordinator([defaults, mute, graph]);

        var report = await coordinator.ActivateAsync(
            new UnifiedProfile { Name = "Test" },
            TestContext.Current.CancellationToken);

        Assert.Equal(ActivationOutcome.FailedRolledBack, report.Outcome);
        Assert.Equal(
            ["graph:capture", "mute:capture", "defaults:capture", "graph:apply", "mute:apply", "defaults:apply", "mute:rollback", "graph:rollback"],
            events);
    }

    [Fact]
    public async Task ReportsPartialWhenRollbackFails()
    {
        var graph = new FakeParticipant("graph", 100) { RollbackError = "engine gone" };
        var defaults = new FakeParticipant("defaults", 500) { ApplyError = "denied" };
        var coordinator = new ProfileActivationCoordinator([graph, defaults]);

        var report = await coordinator.ActivateAsync(
            new UnifiedProfile(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ActivationOutcome.PartialRollback, report.Outcome);
        Assert.Equal("engine gone", report.Steps.Single(step => step.Participant == "graph").RollbackError);
    }

    private sealed class FakeParticipant : IProfileParticipant
    {
        private readonly List<string> _events;

        public FakeParticipant(string name, int order, List<string>? events = null)
        {
            Name = name;
            Order = order;
            _events = events ?? [];
        }

        public string Name { get; }
        public int Order { get; }
        public string? ValidationError { get; init; }
        public string? ApplyError { get; init; }
        public string? RollbackError { get; init; }
        public IReadOnlyList<string> Events => _events;

        public ValueTask<IReadOnlyList<ProfileValidationIssue>> ValidateAsync(UnifiedProfile profile, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProfileValidationIssue>>(
                ValidationError is null ? [] : [new(Name, "invalid", ValidationError)]);

        public ValueTask<object?> CaptureAsync(CancellationToken cancellationToken)
        {
            _events.Add($"{Name}:capture");
            return ValueTask.FromResult<object?>(Name + "-snapshot");
        }

        public ValueTask ApplyAsync(UnifiedProfile profile, CancellationToken cancellationToken)
        {
            _events.Add($"{Name}:apply");
            return ApplyError is null ? ValueTask.CompletedTask : ValueTask.FromException(new InvalidOperationException(ApplyError));
        }

        public ValueTask RollbackAsync(object? snapshot, CancellationToken cancellationToken)
        {
            _events.Add($"{Name}:rollback");
            return RollbackError is null ? ValueTask.CompletedTask : ValueTask.FromException(new InvalidOperationException(RollbackError));
        }
    }
}
