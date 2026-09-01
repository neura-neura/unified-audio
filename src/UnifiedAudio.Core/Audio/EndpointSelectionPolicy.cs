using UnifiedAudio.Core.Models;

namespace UnifiedAudio.Core.Audio;

/// <summary>
/// The semantic kind of an audio endpoint inferred from its direction and
/// friendly name.  The policy deliberately treats cable endpoints as virtual
/// even when a driver exposes an unexpected label; this prevents a cable
/// capture endpoint from being offered as a physical microphone.
/// </summary>
public enum EndpointKind
{
    Unknown,
    Physical,
    VirtualCableRender,
    VirtualCableCapture
}

/// <summary>
/// A platform-neutral endpoint descriptor.  The app layer can map its native
/// MMDevice inventory to this type without making the selection policy depend
/// on Win32 or WinUI types.
/// </summary>
public sealed record AudioEndpointDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public EndpointFlow Flow { get; init; }
    public EndpointAvailability Availability { get; init; } = EndpointAvailability.Available;

    public bool IsAvailable => Availability == EndpointAvailability.Available;
}

/// <summary>
/// Result of resolving the final virtual render endpoint.
/// </summary>
public sealed record EndpointSelectionDecision
{
    public AudioEndpointDescriptor? Selected { get; init; }
    public bool PreservedExisting { get; init; }
    public bool ExistingSelectionUnavailable { get; init; }

    public bool HasSelection => Selected is not null;
    public bool SuggestedNewSelection => Selected is not null && !PreservedExisting;
}

/// <summary>
/// Shared, deterministic endpoint policy used by UI and activation layers.
/// It only suggests a final virtual render when no existing selection is
/// present.  A disconnected or stale saved selection is never silently
/// replaced.
/// </summary>
public static class EndpointSelectionPolicy
{
    private const string StandardCableInput = "CABLE Input (VB-Audio Virtual Cable)";

    public static EndpointKind Classify(AudioEndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(endpoint.Name))
            return EndpointKind.Unknown;

        if (!LooksLikeVirtualCable(endpoint.Name))
            return EndpointKind.Physical;

        return endpoint.Flow == EndpointFlow.Capture
            ? EndpointKind.VirtualCableCapture
            : EndpointKind.VirtualCableRender;
    }

    /// <summary>
    /// Returns only available physical capture endpoints.  In particular,
    /// CABLE Output and all other virtual cable capture endpoints are omitted.
    /// The source order is retained; this method never chooses one implicitly.
    /// </summary>
    public static IReadOnlyList<AudioEndpointDescriptor> GetPhysicalCaptureInputs(
        IEnumerable<AudioEndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints
            .Where(endpoint => endpoint.IsAvailable
                               && endpoint.Flow == EndpointFlow.Capture
                               && Classify(endpoint) == EndpointKind.Physical)
            .ToArray();
    }

    public static bool IsPhysicalCapture(AudioEndpointDescriptor? endpoint) =>
        endpoint is not null
        && endpoint.IsAvailable
        && endpoint.Flow == EndpointFlow.Capture
        && Classify(endpoint) == EndpointKind.Physical;

    /// <summary>
    /// Resolves a saved final render, or makes a deterministic suggestion only
    /// when no saved selection exists.  A saved ID/name that cannot be found
    /// remains unresolved instead of being replaced by another cable.
    /// </summary>
    public static EndpointSelectionDecision ResolveFinalVirtualRender(
        IEnumerable<AudioEndpointDescriptor> endpoints,
        EndpointReference? saved = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inventory = endpoints.ToArray();
        var availableRenders = inventory
            .Where(endpoint => endpoint.IsAvailable && endpoint.Flow == EndpointFlow.Render)
            .ToArray();

        if (HasSavedSelection(saved))
        {
            var savedMatch = FindSavedRender(availableRenders, saved!);
            return savedMatch is not null
                ? new EndpointSelectionDecision
                {
                    Selected = savedMatch,
                    PreservedExisting = true
                }
                : new EndpointSelectionDecision
                {
                    ExistingSelectionUnavailable = true
                };
        }

        var suggested = availableRenders
            .Where(IsFinalVirtualRenderCandidate)
            .OrderBy(PreferenceRank)
            .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return new EndpointSelectionDecision
        {
            Selected = suggested
        };
    }

    private static AudioEndpointDescriptor? FindSavedRender(
        IReadOnlyList<AudioEndpointDescriptor> availableRenders,
        EndpointReference saved)
    {
        if (!string.IsNullOrWhiteSpace(saved.Id))
        {
            var idMatches = availableRenders.Where(endpoint =>
                string.Equals(endpoint.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
            return idMatches.Count() == 1 ? idMatches.Single() : null;
        }

        if (!string.IsNullOrWhiteSpace(saved.Name))
        {
            var nameMatches = availableRenders.Where(endpoint =>
                string.Equals(endpoint.Name.Trim(), saved.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            return nameMatches.Count() == 1 ? nameMatches.Single() : null;
        }

        return null;
    }

    private static bool HasSavedSelection(EndpointReference? saved) =>
        saved is not null
        && (!string.IsNullOrWhiteSpace(saved.Id) || !string.IsNullOrWhiteSpace(saved.Name));

    private static bool IsFinalVirtualRenderCandidate(AudioEndpointDescriptor endpoint)
    {
        if (Classify(endpoint) != EndpointKind.VirtualCableRender)
            return false;

        // A render endpoint normally says "Input" (the signal enters the
        // cable there).  Exclude a mislabeled capture side if it appears in a
        // render inventory; generic cable names without a side label remain
        // valid candidates.
        return !HasToken(endpoint.Name, "OUTPUT") || HasToken(endpoint.Name, "INPUT");
    }

    private static int PreferenceRank(AudioEndpointDescriptor endpoint)
    {
        if (IsStandardCableInput(endpoint.Name)) return 0;
        if (HasToken(endpoint.Name, "CABLE") && HasToken(endpoint.Name, "INPUT")) return 1;
        if (HasToken(endpoint.Name, "VB-AUDIO")) return 2;
        if (HasToken(endpoint.Name, "VOICEMEETER")) return 3;
        return 4; // Virtual Audio Cable and other recognized virtual cable names.
    }

    private static bool IsStandardCableInput(string name) =>
        string.Equals(name.Trim(), StandardCableInput, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeVirtualCable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        return HasToken(name, "VB-AUDIO")
               || HasToken(name, "VIRTUAL AUDIO CABLE")
               || HasToken(name, "VOICEMEETER")
               || HasToken(name, "CABLE")
                  && (HasToken(name, "INPUT") || HasToken(name, "OUTPUT"));
    }

    private static bool HasToken(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
