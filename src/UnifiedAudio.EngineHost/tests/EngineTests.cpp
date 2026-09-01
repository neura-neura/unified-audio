#include <juce_core/juce_core.h>
#include <juce_audio_basics/juce_audio_basics.h>
#include "audio/Metering.h"
#include "audio/AsyncAudioBridge.h"
#include "audio/GraphConnections.h"
#include "audio/FeedbackLoopPolicy.h"
#include "audio/ScanCoordinator.h"
#include "state/Persistence.h"
#include "state/PluginScanCache.h"
#include <iostream>

namespace
{
struct MeteringTests final : juce::UnitTest
{
    MeteringTests() : juce::UnitTest ("Metering") {}
    void runTest() override
    {
        beginTest ("Silence is exact zero");
        juce::AudioBuffer<float> buffer (2, 128);
        buffer.clear();
        const auto reading = computeLevel (buffer);
        expectEquals (reading.rms, 0.0f);
        expectEquals (reading.peak, 0.0f);

        beginTest ("Peak is absolute across channels");
        buffer.setSample (1, 10, -0.75f);
        expectWithinAbsoluteError (computeLevel (buffer).peak, 0.75f, 0.0001f);
    }
};
MeteringTests meteringTests;

struct AsyncBridgeTests final : juce::UnitTest
{
    AsyncBridgeTests() : juce::UnitTest ("Asynchronous audio bridge") {}
    void runTest() override
    {
        beginTest ("Prebuffer emits digital silence without counting an underrun");
        AsyncAudioBridge bridge;
        bridge.reset (44100.0, 48000.0);
        float outputLeft[64] {};
        float outputRight[64] {};
        bridge.pop (outputLeft, outputRight, 64);
        expectEquals (bridge.underruns(), std::uint64_t { 0 });
        expectEquals (outputLeft[0], 0.0f);

        beginTest ("Independent sample rates are linearly resampled");
        // The 44.1 -> 48 kHz bridge intentionally prebuffers 100 ms; provide more than
        // that so this assertion exercises resampling rather than startup silence.
        std::vector<float> source (8192);
        for (std::size_t index = 0; index < source.size(); ++index)
            source[index] = static_cast<float> (index) / static_cast<float> (source.size());
        const float* inputs[] { source.data() };
        bridge.push (inputs, 1, static_cast<int> (source.size()));
        bridge.pop (outputLeft, outputRight, 64);
        expect (outputLeft[20] > outputLeft[1]);
        expectWithinAbsoluteError (outputLeft[20], outputRight[20], 0.00001f);
        expect (bridge.queuedFrames() < source.size());
    }
};
AsyncBridgeTests asyncBridgeTests;

struct GraphTests final : juce::UnitTest
{
    GraphTests() : juce::UnitTest ("Channel graph") {}
    void runTest() override
    {
        beginTest ("Mono to stereo transition keeps explicit channels");
        const NodeID input { 1 }, converter { 2 }, output { 3 };
        const auto connections = computeChainConnections ({
            { input, 0, 1 }, { converter, 1, 2 }, { output, 2, 0 }
        });
        expectEquals (static_cast<int> (connections.size()), 3);
    }
};
GraphTests graphTests;

struct FeedbackLoopPolicyTests final : juce::UnitTest
{
    FeedbackLoopPolicyTests() : juce::UnitTest ("Feedback loop policy") {}

    static FeedbackLoopEvidence verifiedSystemRoute()
    {
        FeedbackLoopEvidence evidence;
        evidence.probeAvailable = true;
        evidence.systemRouteRequired = true;
        evidence.finalIdentityResolved = true;
        evidence.systemIdentityResolved = true;
        evidence.finalCaptureConsumerProbeAvailable = true;
        return evidence;
    }

    void runTest() override
    {
        beginTest ("Direct system endpoint equal to final is blocked");
        FeedbackLoopEvidence direct;
        direct.directEndpoint = true;
        expect (evaluateFeedbackLoop (direct).risk);

        beginTest ("Standard cable Listen route back into the selected source is blocked");
        auto listen = verifiedSystemRoute();
        listen.finalVirtualCable = true;
        listen.finalCableCaptureFound = true;
        listen.listenEnabled = true;
        listen.listenTargetMatchesSystem = true;
        const auto listenDecision = evaluateFeedbackLoop (listen);
        expect (listenDecision.risk);

        beginTest ("An active Listen route to another endpoint preserves intentional system mixing");
        listen.listenTargetMatchesSystem = false;
        expect (! evaluateFeedbackLoop (listen).risk);

        beginTest ("Unknown Listen target is fail-safe blocked");
        listen.listenTargetUnknown = true;
        expect (evaluateFeedbackLoop (listen).risk);

        beginTest ("A physical Ugreen capture listening into final CABLE Input is blocked");
        auto physicalToFinal = verifiedSystemRoute();
        physicalToFinal.finalVirtualCable = true;
        physicalToFinal.listenEnabled = true;
        physicalToFinal.physicalCaptureListenToFinal = true;
        physicalToFinal.listenTargetMatchesFinal = true;
        expect (evaluateFeedbackLoop (physicalToFinal).risk);

        beginTest ("Paired CABLE Output listening into Ugreen/default system source is blocked");
        auto pairedToSystem = verifiedSystemRoute();
        pairedToSystem.finalVirtualCable = true;
        pairedToSystem.finalCableCaptureFound = true;
        pairedToSystem.listenEnabled = true;
        pairedToSystem.pairedCaptureListenToSystem = true;
        pairedToSystem.listenTargetMatchesSystem = true;
        pairedToSystem.listenTargetUsesDefault = true;
        expect (evaluateFeedbackLoop (pairedToSystem).risk);

        beginTest ("Listen target PKEY resolving to final render ID is blocked");
        auto pkeyFinal = verifiedSystemRoute();
        pkeyFinal.finalVirtualCable = true;
        pkeyFinal.listenEnabled = true;
        pkeyFinal.listenTargetMatchesFinal = true;
        expect (evaluateFeedbackLoop (pkeyFinal).risk);

        beginTest ("Unknown PROPVARIANT or COM probe failure is fail-safe for Both/System");
        auto probeFailure = verifiedSystemRoute();
        probeFailure.probeAvailable = false;
        probeFailure.propertyProbeFailed = true;
        expect (evaluateFeedbackLoop (probeFailure).risk);

        beginTest ("Duplicate friendly names or unresolved saved IDs are fail-safe");
        auto duplicate = verifiedSystemRoute();
        duplicate.systemIdentityResolved = false;
        duplicate.identityAmbiguous = true;
        expect (evaluateFeedbackLoop (duplicate).risk);

        beginTest ("Stable ID equality blocks endpoints with different friendly names");
        auto equalIds = verifiedSystemRoute();
        equalIds.directEndpoint = true;
        expect (evaluateFeedbackLoop (equalIds).risk);

        beginTest ("Same friendly name with different stable IDs is not a direct endpoint");
        auto distinctStableIds = verifiedSystemRoute();
        distinctStableIds.directEndpoint = false;
        expect (! evaluateFeedbackLoop (distinctStableIds).risk);

        beginTest ("Missing stable ID with ambiguous friendly-name resolution is fail-safe");
        auto ambiguousNameOnly = verifiedSystemRoute();
        ambiguousNameOnly.finalIdentityResolved = false;
        ambiguousNameOnly.identityAmbiguous = true;
        expect (evaluateFeedbackLoop (ambiguousNameOnly).risk);

        beginTest ("A virtual cable render cannot be selected as the system source");
        auto virtualSystem = verifiedSystemRoute();
        virtualSystem.systemVirtualCable = true;
        expect (evaluateFeedbackLoop (virtualSystem).risk);

        beginTest ("Endpoint-full loopback is unsafe when a receiver consumes final CABLE Output");
        auto endpointConsumer = verifiedSystemRoute();
        endpointConsumer.finalVirtualCable = true;
        endpointConsumer.finalCaptureConsumerDetected = true;
        endpointConsumer.processFilterEnabled = false;
        expect (evaluateFeedbackLoop (endpointConsumer).risk);

        beginTest ("A receiver excluded from process capture remains safe");
        auto excludedConsumer = verifiedSystemRoute();
        excludedConsumer.finalVirtualCable = true;
        excludedConsumer.finalCaptureConsumerDetected = true;
        excludedConsumer.processFilterEnabled = true;
        excludedConsumer.finalCaptureConsumersExcluded = true;
        expect (! evaluateFeedbackLoop (excludedConsumer).risk);

        beginTest ("A newly appearing consumer tree triggers the hot guard");
        auto hotConsumer = excludedConsumer;
        hotConsumer.finalCaptureConsumersExcluded = false;
        expect (evaluateFeedbackLoop (hotConsumer).risk);

        beginTest ("No final-capture consumers permits a verified process filter");
        auto noConsumer = verifiedSystemRoute();
        noConsumer.finalVirtualCable = true;
        noConsumer.processFilterEnabled = true;
        expect (! evaluateFeedbackLoop (noConsumer).risk);

        beginTest ("Process-loopback ancestor/child relation is treated as one unsafe tree");
        juce::Array<ProcessTreeNode> tree {
            ProcessTreeNode { 100, 1 }, ProcessTreeNode { 200, 100 }, ProcessTreeNode { 300, 200 }
        };
        expect (processTreeRelated (tree, 100, 300));
        expect (processTreeRelated (tree, 300, 100));
        expect (! processTreeRelated (tree, 300, 400));

        beginTest ("Explicit Both with a verified Listen-off probe remains allowed");
        auto verifiedOff = verifiedSystemRoute();
        expect (! evaluateFeedbackLoop (verifiedOff).risk);

        beginTest ("Voice/system without a cable Listen route remains available");
        FeedbackLoopEvidence ordinary;
        ordinary.finalVirtualCable = true;
        ordinary.finalCableCaptureFound = true;
        expect (! evaluateFeedbackLoop (ordinary).risk);
    }
};
FeedbackLoopPolicyTests feedbackLoopPolicyTests;

struct MixerGuardTransactionTests final : juce::UnitTest
{
    MixerGuardTransactionTests() : juce::UnitTest ("Mixer guard transaction") {}

    struct Config
    {
        int mode = 0;
        juce::String systemDevice;
    };

    static bool apply (Config& current,
                       const FeedbackLoopEvidence& evidence,
                       bool explicitMixer,
                       int requestedMode,
                       const juce::String& requestedSystem)
    {
        const auto prior = current;
        if (! explicitMixer)
        {
            // This is the contract sent by Input & Plugins when its preserve
            // checkbox is clear: opening endpoints is a Voice-only change.
            current = { 0, {} };
            return true;
        }

        if (evaluateFeedbackLoop (evidence).risk)
        {
            // Model the host transaction's neutral-graph failure path.  The
            // production HostMain rollback restores the same two fields plus
            // devices/plugins/mute; this pure fixture keeps the smoke test
            // independent of Windows audio endpoints.
            current = prior;
            return false;
        }

        current = { requestedMode, requestedSystem };
        return true;
    }

    static bool hotGuard (Config& current, const FeedbackLoopEvidence& evidence)
    {
        if (! evaluateFeedbackLoop (evidence).risk) return false;
        current = { 0, {} };
        return true;
    }

    void runTest() override
    {
        beginTest ("Standard CABLE with Listen disabled starts Voice when not explicit");
        Config persisted { 2, "Speaker Ugreen Soundcard (KT USB Audio)" };
        FeedbackLoopEvidence noListen;
        noListen.probeAvailable = true;
        noListen.systemRouteRequired = true;
        noListen.finalIdentityResolved = true;
        noListen.systemIdentityResolved = true;
        noListen.finalCaptureConsumerProbeAvailable = true;
        noListen.finalVirtualCable = true;
        noListen.finalCableCaptureFound = true;
        expect (apply (persisted, noListen, false, 2,
                       "Speaker Ugreen Soundcard (KT USB Audio)"));
        expectEquals (persisted.mode, 0);
        expect (persisted.systemDevice.isEmpty());

        beginTest ("Explicit Both keeps the physical Ugreen system source");
        Config explicitVoice;
        expect (apply (explicitVoice, noListen, true, 2,
                       "Speaker Ugreen Soundcard (KT USB Audio)"));
        expectEquals (explicitVoice.mode, 2);
        expectEquals (explicitVoice.systemDevice,
                      juce::String ("Speaker Ugreen Soundcard (KT USB Audio)"));

        beginTest ("Synthetic Listen route blocks Both and rolls back");
        Config prior { 0, {} };
        auto listen = noListen;
        listen.listenEnabled = true;
        listen.listenTargetMatchesSystem = true;
        expect (! apply (prior, listen, true, 2,
                         "Speaker Ugreen Soundcard (KT USB Audio)"));
        expectEquals (prior.mode, 0);
        expect (prior.systemDevice.isEmpty());

        beginTest ("Hot Listen change falls back to Voice and preserves rollback state");
        Config hotChange { 2, "Speaker Ugreen Soundcard (KT USB Audio)" };
        auto hotListen = noListen;
        hotListen.listenEnabled = true;
        hotListen.physicalCaptureListenToFinal = true;
        hotListen.listenTargetMatchesFinal = true;
        expect (hotGuard (hotChange, hotListen));
        expectEquals (hotChange.mode, 0);
        expect (hotChange.systemDevice.isEmpty());
    }
};
MixerGuardTransactionTests mixerGuardTransactionTests;

struct PersistenceTests final : juce::UnitTest
{
    PersistenceTests() : juce::UnitTest ("Engine persistence") {}
    void runTest() override
    {
        beginTest ("Plugin blob round trip");
        UnifiedAudioState source;
        source.inputDevice = "Input";
        source.inputDeviceId = "{capture-id}";
        source.outputDeviceId = "{render-id}";
        source.mixerMode = 2;
        source.systemCaptureDevice = "Speakers";
        source.systemCaptureDeviceId = "{system-capture-id}";
        source.duckingEnabled = true;
        source.processFilterEnabled = true;
        source.processFilterExclusionMode = true;
        source.processMixRules.add ({ "C:\\Apps\\chat.exe", "Chat", 0.75f, true });
        PluginEntryState plugin;
        plugin.fileOrId = "builtin:mono2stereo";
        const std::uint8_t state[] { 1, 2, 3 };
        plugin.state.append (state, sizeof (state));
        source.plugins.add (plugin);
        const auto restored = fromValueTree (toValueTree (source));
        expectEquals (restored.inputDevice, juce::String ("Input"));
        expectEquals (restored.inputDeviceId, juce::String ("{capture-id}"));
        expectEquals (restored.outputDeviceId, juce::String ("{render-id}"));
        expectEquals (restored.mixerMode, 2);
        expectEquals (restored.systemCaptureDevice, juce::String ("Speakers"));
        expectEquals (restored.systemCaptureDeviceId, juce::String ("{system-capture-id}"));
        expect (restored.duckingEnabled);
        expect (restored.processFilterEnabled);
        expect (restored.processFilterExclusionMode);
        expectEquals (restored.processMixRules.size(), 1);
        expect (restored.processMixRules[0].excluded);
        expectWithinAbsoluteError (restored.processMixRules[0].gain, 0.75f, 0.0001f);
        expect (restored.plugins[0].state == plugin.state);
    }
};
PersistenceTests persistenceTests;

struct PluginFingerprintTests final : juce::UnitTest
{
    PluginFingerprintTests() : juce::UnitTest ("VST3 binary fingerprint") {}
    void runTest() override
    {
        const auto temporaryRoot = juce::File::getSpecialLocation (juce::File::tempDirectory)
            .getNonexistentChildFile ("UnifiedAudioFingerprintTest", {});
        const auto bundle = temporaryRoot.getChildFile ("Fixture.vst3");
        const auto binary = bundle.getChildFile ("Contents")
            .getChildFile ("x86_64-win").getChildFile ("Fixture.vst3");
        binary.getParentDirectory().createDirectory();
        binary.replaceWithText ("AAAA");
        const auto fixedTime = juce::Time (1700000000000);
        binary.setLastModificationTime (fixedTime);

        beginTest ("Same-size binary replacement changes SHA-256 even with preserved mtime");
        const auto first = fingerprintPluginBinary (bundle);
        binary.replaceWithText ("BBBB");
        binary.setLastModificationTime (fixedTime);
        const auto second = fingerprintPluginBinary (bundle);
        expectEquals (first.totalBytes, second.totalBytes);
        expect (first.sha256.isNotEmpty());
        expect (first.sha256 != second.sha256);

        beginTest ("Mutable non-binary bundle content is ignored");
        const auto beforeContent = fingerprintPluginBinary (bundle);
        bundle.getChildFile ("Contents").getChildFile ("Resources")
            .getChildFile ("runtime.log").replaceWithText ("mutable runtime content");
        const auto afterContent = fingerprintPluginBinary (bundle);
        expectEquals (beforeContent.sha256, afterContent.sha256);

        beginTest ("Fingerprint cache round trip");
        juce::KnownPluginList list;
        juce::Array<SkippedPlugin> skipped;
        juce::Array<PluginBinaryFingerprint> fingerprints { second };
        const auto xml = PluginScanCache::toXml (list, skipped, fingerprints);
        juce::KnownPluginList restoredList;
        juce::Array<SkippedPlugin> restoredSkipped;
        juce::Array<PluginBinaryFingerprint> restoredFingerprints;
        expect (PluginScanCache::fromXml (*xml, restoredList, restoredSkipped, &restoredFingerprints));
        expectEquals (restoredFingerprints.size(), 1);
        expectEquals (restoredFingerprints[0].sha256, second.sha256);
        expectEquals (restoredFingerprints[0].totalBytes, second.totalBytes);

        beginTest ("Fingerprint cache recovers from backup after corruption");
        const auto cacheFile = temporaryRoot.getChildFile ("plugin_cache.xml");
        expect (PluginScanCache::save (cacheFile, list, skipped, fingerprints));
        auto updated = second;
        updated.sha256 = juce::String::repeatedString ("a", 64);
        fingerprints.set (0, updated);
        expect (PluginScanCache::save (cacheFile, list, skipped, fingerprints));
        expect (cacheFile.getSiblingFile (cacheFile.getFileName() + ".bak").existsAsFile());
        expect (cacheFile.replaceWithText ("not xml"));
        restoredFingerprints.clear();
        expect (PluginScanCache::load (cacheFile, restoredList, restoredSkipped, &restoredFingerprints));
        expectEquals (restoredFingerprints.size(), 1);
        expectEquals (restoredFingerprints[0].sha256, second.sha256);

        temporaryRoot.deleteRecursively();
    }
};
PluginFingerprintTests pluginFingerprintTests;
}

int main()
{
    juce::UnitTestRunner runner;
    runner.runAllTests();
    int failures = 0;
    for (int index = 0; index < runner.getNumResults(); ++index)
    {
        const auto* result = runner.getResult (index);
        failures += result->failures;
        std::cout << result->unitTestName << " / " << result->subcategoryName
                  << ": " << result->passes << " passed, " << result->failures << " failed\n";
        for (const auto& message : result->messages)
            std::cout << "  " << message << "\n";
    }
    return failures == 0 ? 0 : 1;
}
