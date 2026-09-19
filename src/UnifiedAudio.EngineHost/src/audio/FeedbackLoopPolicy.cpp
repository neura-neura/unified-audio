#include "audio/FeedbackLoopPolicy.h"

FeedbackLoopDecision evaluateFeedbackLoop (const FeedbackLoopEvidence& evidence)
{
    if (evidence.directEndpoint)
    {
        return { true,
                 "the system capture endpoint is the final render endpoint" };
    }

    if (evidence.systemRouteRequired && evidence.systemVirtualCable)
    {
        return { true,
                 "the selected system capture endpoint is a virtual cable render endpoint" };
    }

    // System/Both must never proceed when the endpoint identity or the
    // read-only Windows Listen probe is incomplete. Voice remains usable; the
    // caller applies this decision only when a system route is requested.
    if (evidence.systemRouteRequired
        && (! evidence.probeAvailable || evidence.propertyProbeFailed
            || ! evidence.finalIdentityResolved || ! evidence.systemIdentityResolved
            || evidence.identityAmbiguous))
    {
        return { true, "Windows audio endpoint/Listen state could not be verified safely" };
    }

    // A microphone consumer alone does not prove a return route: normal call
    // and recording apps must be able to receive Both. Endpoint loopback is
    // guarded by the actual endpoint/Listen routes below. Process-tree capture
    // is endpoint-independent, so retain its explicit receiver exclusions.
    if (evidence.systemRouteRequired && evidence.processFilterEnabled && evidence.finalVirtualCable
        && ! evidence.finalCaptureConsumerProbeAvailable)
    {
        return { true,
                 "consumers of the final cable capture could not be enumerated safely" };
    }
    if (evidence.systemRouteRequired && evidence.processFilterEnabled
        && evidence.finalCaptureConsumerDetected && ! evidence.finalCaptureConsumersExcluded)
    {
        return { true,
                 "an active process consumes the final cable capture and is not excluded from system capture" };
    }

    // A cable is not dangerous merely because it is virtual.  System mixing
    // into a final cable is an intentional supported workflow.  It becomes a
    // deterministic digital loop only when Windows is also listening to the
    // final cable's capture side and sends that signal to the endpoint that we
    // capture for the system source.
    if (! evidence.listenEnabled)
        return {};

    if (evidence.listenTargetMatchesFinal || evidence.listenTargetMatchesSystem
        || evidence.physicalCaptureListenToFinal || evidence.pairedCaptureListenToSystem
        || evidence.listenTargetUnknown
        || (evidence.finalVirtualCable && evidence.finalCableCaptureAmbiguous))
    {
        auto reason = evidence.listenTargetUnknown || evidence.propertyProbeFailed
            ? "Windows Listen is enabled on the final cable capture, but its playback target could not be resolved"
            : evidence.listenTargetMatchesFinal || evidence.physicalCaptureListenToFinal
                ? "Windows Listen routes a capture endpoint into the final render endpoint"
                : "Windows Listen routes the final cable capture into the selected system source";
        return { true, reason };
    }

    // Listen may be active but directed to a different playback endpoint.
    // That is not a path back into this graph, so preserve intentional system
    // mixing and let the UI expose the ordinary telemetry.
    return {};
}

namespace
{
bool reachesAncestor (const juce::Array<ProcessTreeNode>& tree,
                       unsigned long ancestor,
                       unsigned long process)
{
    if (ancestor == 0 || process == 0) return false;
    juce::Array<unsigned long> visited;
    for (int depth = 0; depth < 512 && process != 0; ++depth)
    {
        if (process == ancestor) return true;
        if (visited.contains (process)) return false;
        visited.add (process);
        const auto node = std::find_if (tree.begin(), tree.end(), [process] (const auto& item)
        {
            return item.processId == process;
        });
        if (node == tree.end()) return false;
        process = node->parentProcessId;
    }
    return false;
}
}

bool processTreeRelated (const juce::Array<ProcessTreeNode>& tree,
                         unsigned long leftProcessId,
                         unsigned long rightProcessId)
{
    return leftProcessId != 0 && rightProcessId != 0
        && (reachesAncestor (tree, leftProcessId, rightProcessId)
            || reachesAncestor (tree, rightProcessId, leftProcessId));
}
