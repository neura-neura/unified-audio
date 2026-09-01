using UnifiedAudio.Core.Models;

namespace UnifiedAudio.Core.Profiles;

public enum ActivationOutcome
{
    Success,
    FailedRolledBack,
    PartialRollback
}

public sealed record ProfileValidationIssue(string Participant, string Code, string Message);

public sealed record ProfileStepResult(
    string Participant,
    bool Applied,
    bool RolledBack,
    string? Error = null,
    string? RollbackError = null);

public sealed record ProfileActivationReport(
    ActivationOutcome Outcome,
    IReadOnlyList<ProfileValidationIssue> ValidationIssues,
    IReadOnlyList<ProfileStepResult> Steps)
{
    public bool Succeeded => Outcome == ActivationOutcome.Success;
}

public interface IProfileParticipant
{
    string Name { get; }
    int Order { get; }
    ValueTask<IReadOnlyList<ProfileValidationIssue>> ValidateAsync(
        UnifiedProfile profile,
        CancellationToken cancellationToken);
    ValueTask<object?> CaptureAsync(CancellationToken cancellationToken);
    ValueTask ApplyAsync(UnifiedProfile profile, CancellationToken cancellationToken);
    ValueTask RollbackAsync(object? snapshot, CancellationToken cancellationToken);
}

public sealed class ProfileActivationCoordinator
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private readonly IReadOnlyList<IProfileParticipant> _participants;

    public ProfileActivationCoordinator(IEnumerable<IProfileParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        _participants = participants.OrderBy(participant => participant.Order).ToArray();
        var duplicate = _participants.GroupBy(participant => participant.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate profile participant '{duplicate.Key}'.", nameof(participants));
        }
    }

    public async Task<ProfileActivationReport> ActivateAsync(
        UnifiedProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validation = new List<ProfileValidationIssue>();
            foreach (var participant in _participants)
            {
                validation.AddRange(await participant.ValidateAsync(profile, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (validation.Count > 0)
            {
                return new ProfileActivationReport(
                    ActivationOutcome.FailedRolledBack,
                    validation,
                    []);
            }

            var snapshots = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var participant in _participants)
            {
                snapshots[participant.Name] = await participant.CaptureAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var applied = new List<IProfileParticipant>();
            var results = new List<ProfileStepResult>();
            try
            {
                foreach (var participant in _participants)
                {
                    await participant.ApplyAsync(profile, cancellationToken).ConfigureAwait(false);
                    applied.Add(participant);
                    results.Add(new ProfileStepResult(participant.Name, Applied: true, RolledBack: false));
                }

                return new ProfileActivationReport(ActivationOutcome.Success, [], results);
            }
            catch (Exception applyError) when (applyError is not StackOverflowException)
            {
                if (_participants.Count > applied.Count)
                {
                    var failedParticipant = _participants[applied.Count];
                    results.Add(new ProfileStepResult(
                        failedParticipant.Name,
                        Applied: false,
                        RolledBack: false,
                        Error: applyError.Message));
                }

                var rollbackFailed = false;
                foreach (var participant in applied.AsEnumerable().Reverse())
                {
                    var index = results.FindIndex(result => result.Participant == participant.Name);
                    try
                    {
                        // Rollback must get a chance even if the caller cancelled during apply.
                        await participant.RollbackAsync(snapshots[participant.Name], CancellationToken.None)
                            .ConfigureAwait(false);
                        results[index] = results[index] with { RolledBack = true };
                    }
                    catch (Exception rollbackError) when (rollbackError is not StackOverflowException)
                    {
                        rollbackFailed = true;
                        results[index] = results[index] with { RollbackError = rollbackError.Message };
                    }
                }

                return new ProfileActivationReport(
                    rollbackFailed ? ActivationOutcome.PartialRollback : ActivationOutcome.FailedRolledBack,
                    [],
                    results);
            }
        }
        finally
        {
            _activationGate.Release();
        }
    }
}

public static class ProfileActivationOrder
{
    public const int PrepareGraph = 100;
    public const int CommitGraph = 200;
    public const int MuteAndMix = 300;
    public const int FeedbackAndAutomation = 400;
    public const int WindowsDefaults = 500;
    public const int PersistActiveProfile = 600;
}
