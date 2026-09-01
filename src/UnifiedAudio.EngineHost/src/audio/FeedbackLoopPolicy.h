#pragma once

#include <juce_core/juce_core.h>

struct ProcessTreeNode
{
    unsigned long processId = 0;
    unsigned long parentProcessId = 0;
};

struct FeedbackProcessConsumer
{
    unsigned long processId = 0;
    juce::String displayName;
    juce::String executablePath;
};

// Evidence collected on the engine/message thread.  The real-time callback
// never calls this policy or touches MMDevice/COM.  Keeping the decision
// separate makes the dangerous routing cases deterministic and testable on
// machines without a live audio graph.
struct FeedbackLoopEvidence
{
    bool probeAvailable = false;
    bool systemRouteRequired = false;
    bool finalIdentityResolved = false;
    bool systemIdentityResolved = false;
    bool identityAmbiguous = false;
    bool propertyProbeFailed = false;
    bool finalVirtualCable = false;
    bool systemVirtualCable = false;
    bool directEndpoint = false;
    bool finalCableCaptureFound = false;
    bool finalCableCaptureAmbiguous = false;
    bool listenEnabled = false;
    bool listenTargetMatchesSystem = false;
    bool listenTargetMatchesFinal = false;
    bool listenTargetUsesDefault = false;
    bool listenTargetUnknown = false;
    bool physicalCaptureListenToFinal = false;
    bool pairedCaptureListenToSystem = false;
    bool finalCaptureConsumerProbeAvailable = false;
    bool finalCaptureConsumerDetected = false;
    bool processFilterEnabled = false;
    bool finalCaptureConsumersExcluded = false;
    int listenCaptureCount = 0;
    juce::Array<FeedbackProcessConsumer> finalCaptureConsumers;
    juce::String finalCaptureEndpointId;
    juce::String finalCaptureEndpointName;
    juce::String finalOutputName;
    juce::String systemOutputName;
    juce::String listeningCaptureName;
};

struct FeedbackLoopDecision
{
    bool risk = false;
    juce::String reason;
};

FeedbackLoopDecision evaluateFeedbackLoop (const FeedbackLoopEvidence& evidence);

// Pure process-tree relation used by the host safety check and tests. A
// process-loopback slot includes the target and its descendants, so a slot is
// unsafe when its target is either the consumer or an ancestor of it.
bool processTreeRelated (const juce::Array<ProcessTreeNode>& tree,
                         unsigned long leftProcessId,
                         unsigned long rightProcessId);
