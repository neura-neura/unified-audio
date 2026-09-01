#include "audio/AudioEngine.h"
#include "audio/FeedbackLoopDetector.h"
using IOProc = juce::AudioProcessorGraph::AudioGraphIOProcessor;

#if JUCE_WINDOWS
 #define WIN32_LEAN_AND_MEAN
 #define NOMINMAX
 #include <windows.h>
 #include <tlhelp32.h>
 #include <unordered_map>
 #include <unordered_set>
#endif

namespace
{
bool isRelevantListenRoute (const FeedbackLoopEvidence& evidence)
{
    return evidence.listenEnabled
        && (evidence.listenTargetMatchesFinal
            || evidence.listenTargetMatchesSystem
            || evidence.physicalCaptureListenToFinal
            || evidence.pairedCaptureListenToSystem
            || evidence.listenTargetUnknown);
}

#if JUCE_WINDOWS
bool processTreeRelatedAtRuntime (unsigned long leftProcessId, unsigned long rightProcessId)
{
    if (leftProcessId == 0 || rightProcessId == 0) return false;
    if (leftProcessId == rightProcessId) return true;
    const auto snapshot = CreateToolhelp32Snapshot (TH32CS_SNAPPROCESS, 0);
    // A consumer with an uninspectable process tree is unsafe by default.
    if (snapshot == INVALID_HANDLE_VALUE) return true;
    std::unordered_map<DWORD, DWORD> parents;
    PROCESSENTRY32W entry {};
    entry.dwSize = sizeof (entry);
    if (Process32FirstW (snapshot, &entry) != FALSE)
        do { parents[entry.th32ProcessID] = entry.th32ParentProcessID; }
        while (Process32NextW (snapshot, &entry) != FALSE);
    CloseHandle (snapshot);
    if (! parents.contains (static_cast<DWORD> (leftProcessId))
        || ! parents.contains (static_cast<DWORD> (rightProcessId)))
        return true;

    auto reaches = [&parents] (DWORD ancestor, DWORD process)
    {
        std::unordered_set<DWORD> visited;
        for (int depth = 0; depth < 512 && process != 0; ++depth)
        {
            if (process == ancestor) return true;
            if (! visited.insert (process).second) return false;
            const auto found = parents.find (process);
            if (found == parents.end() || found->second == process) return false;
            process = found->second;
        }
        return false;
    };
    return reaches (static_cast<DWORD> (leftProcessId), static_cast<DWORD> (rightProcessId))
        || reaches (static_cast<DWORD> (rightProcessId), static_cast<DWORD> (leftProcessId));
}
#else
bool processTreeRelatedAtRuntime (unsigned long leftProcessId, unsigned long rightProcessId)
{
    return leftProcessId != 0 && leftProcessId == rightProcessId;
}
#endif
}

AudioEngine::AudioEngine()
{
    inputManager.getAvailableDeviceTypes();
    outputManager.getAvailableDeviceTypes();
    inventoryManager.getAvailableDeviceTypes();
    inputManager.addChangeListener (this);
    outputManager.addChangeListener (this);
    processMidi.ensureSize (256);
    for (std::size_t index = 0; index < maxProcessCaptures; ++index)
    {
        processCaptures[index] = std::make_unique<ProcessLoopbackCapture>();
        processMixBuffers[index].setSize (2, 8192, false, true, false);
        processCaptureGains[index].store (1.0f);
    }
}

AudioEngine::~AudioEngine()
{
    inputManager.removeChangeListener (this);
    outputManager.removeChangeListener (this);
    stop();
}

FeedbackLoopEvidence AudioEngine::probeFeedbackLoop (const juce::String& systemDeviceName,
                                                     const juce::String& systemDeviceId) const
{
    auto evidence = detectWindowsFeedbackLoop (getOutputDeviceName(), stableOutputDeviceId,
                                               systemDeviceName, systemDeviceId);
    evidence.processFilterEnabled = isProcessFilterEnabled();
    evidence.finalCaptureConsumersExcluded = finalCaptureConsumersExcluded (evidence);
    return evidence;
}

bool AudioEngine::finalCaptureConsumersExcluded (const FeedbackLoopEvidence& evidence) const
{
    if (! evidence.finalCaptureConsumerDetected) return true;
    if (! isProcessFilterEnabled()) return false;
    for (const auto& consumer : evidence.finalCaptureConsumers)
        for (const auto& pid : processCapturePids)
            if (pid.load (std::memory_order_acquire) != 0
                && processTreeRelatedAtRuntime (pid.load (std::memory_order_acquire), consumer.processId))
                return false;
    return true;
}

bool AudioEngine::applyFeedbackLoopStatus (const FeedbackLoopEvidence& evidence,
                                           bool autoProtect)
{
    const auto decision = evaluateFeedbackLoop (evidence);
    const auto wasGuarded = feedbackLoopGuarded.load (std::memory_order_acquire);
    feedbackLoopDetected.store (decision.risk, std::memory_order_release);
    listenRouteActive.store (isRelevantListenRoute (evidence),
                             std::memory_order_release);
    feedbackProbeAvailable.store (evidence.probeAvailable, std::memory_order_release);
    feedbackProbeUnknown.store (! evidence.probeAvailable || evidence.propertyProbeFailed
                                || (evidence.finalVirtualCable
                                    && ! evidence.finalCaptureConsumerProbeAvailable),
                                std::memory_order_release);
    finalCaptureConsumerProbeAvailable.store (evidence.finalCaptureConsumerProbeAvailable,
                                              std::memory_order_release);
    finalCaptureConsumerDetected.store (evidence.finalCaptureConsumerDetected,
                                        std::memory_order_release);
    finalCaptureEndpointId = evidence.finalCaptureEndpointId;
    finalCaptureEndpointName = evidence.finalCaptureEndpointName;
    juce::StringArray consumerSummary;
    for (const auto& consumer : evidence.finalCaptureConsumers)
    {
        auto label = consumer.displayName;
        if (label.isEmpty()) label = consumer.executablePath;
        if (label.isEmpty()) label = "PID " + juce::String (consumer.processId);
        consumerSummary.add (label + " [PID " + juce::String (consumer.processId) + "]");
    }
    finalCaptureConsumerSummary = consumerSummary.joinIntoString (", ");
    feedbackLoopDescription = decision.reason;

    if (! autoProtect || ! decision.risk)
    {
        feedbackLoopGuarded.store (false, std::memory_order_release);
        return false;
    }
    if (getMixerMode() == MixerMode::voice)
    {
        feedbackLoopGuarded.store (wasGuarded, std::memory_order_release);
        return false;
    }

    // This is intentionally a message/timer-thread operation.  It stops the
    // source that closes the loop and leaves the voice path available.  The
    // user's requested System/Both configuration is not silently rewritten in
    // storage by this method; the host persists the guarded Voice state and an
    // explicit Mixer action is required after the external route is safe.
    systemCapture.stop();
    stopProcessCaptures();
    mixerMode.store (static_cast<int> (MixerMode::voice), std::memory_order_release);
    feedbackLoopGuarded.store (true, std::memory_order_release);
    juce::Logger::writeToLog ("Feedback guard: system capture stopped; mixer fell back to Voice. "
                              + decision.reason);
    return true;
}

bool AudioEngine::refreshFeedbackLoopGuard()
{
    const auto systemDeviceName = isProcessFilterEnabled() ? processFilterDeviceName
                                                            : systemCapture.selectedDeviceName();
    const auto systemDeviceId = isProcessFilterEnabled() ? processFilterDeviceId
                                                          : systemCapture.selectedDeviceId();
    const auto evidence = probeFeedbackLoop (systemDeviceName, systemDeviceId);
    return applyFeedbackLoopStatus (evidence, true);
}

void AudioEngine::stop()
{
    systemCapture.stop();
    stopProcessCaptures();
    outputManager.removeAudioCallback (&renderCallback);
    inputManager.removeAudioCallback (&captureCallback);
    outputManager.closeAudioDevice();
    inputManager.closeAudioDevice();
    graph.releaseResources();
    if (onStatusChanged) onStatusChanged();
}

bool AudioEngine::isRunning() const
{
    auto* input = inputManager.getCurrentAudioDevice();
    auto* output = outputManager.getCurrentAudioDevice();
    return input != nullptr && output != nullptr && input->isPlaying() && output->isPlaying();
}

void AudioEngine::changeListenerCallback (juce::ChangeBroadcaster*)
{
    juce::Logger::writeToLog (isRunning() ? "Audio: running"
                                          : "Audio: idle (device disconnected?)");
    if (onStatusChanged) onStatusChanged();
    if (onDeviceChanged) onDeviceChanged();   // Geräte-Einstellungen persistieren
}

juce::String AudioEngine::detectCableOutput()
{
    // Render-Endpunkte bekannter virtueller Kabel, nach Priorität (VB-Cable zuerst).
    static const char* const cablePatterns[] = {
        "CABLE Input",          // VB-Audio Virtual Cable (empfohlen)
        "VB-Audio",             // weitere VB-Audio-Kabel (Hi-Fi Cable etc.)
        "VoiceMeeter Input",    // VoiceMeeter VAIO/Aux
        "Virtual Audio Cable"   // VAC ("Line 1 (Virtual Audio Cable)")
    };

    inventoryManager.setCurrentAudioDeviceType (inventoryManager.preferredTypeName(), true);
    if (auto* type = inventoryManager.getCurrentDeviceTypeObject())
    {
        type->scanForDevices();
        auto outs = type->getDeviceNames (false /* output */);
        for (auto& name : outs)
            if (name.trim().equalsIgnoreCase ("CABLE Input (VB-Audio Virtual Cable)"))
                return name;

        for (auto* pat : cablePatterns)
            for (auto& name : outs)
                if (name.containsIgnoreCase (pat)
                    && (! name.containsIgnoreCase ("Output") || name.containsIgnoreCase ("Input")))
                    return name;
    }
    return {};
}

juce::String AudioEngine::initialise (const juce::String& inputDeviceName,
                                      const juce::String& outputDeviceName)
{
    const auto resolvedInputName = [&]
    {
        const auto byId = resolveAudioEndpointName (stableInputDeviceId, true);
        return byId.isNotEmpty() ? byId : inputDeviceName;
    }();
    const auto resolvedOutputName = [&]
    {
        const auto byId = resolveAudioEndpointName (stableOutputDeviceId, false);
        return byId.isNotEmpty() ? byId : outputDeviceName;
    }();
    const auto systemDeviceName = isProcessFilterEnabled() ? processFilterDeviceName
                                                            : systemCapture.selectedDeviceName();
    const auto systemDeviceId = isProcessFilterEnabled() ? processFilterDeviceId
                                                          : systemCapture.selectedDeviceId();
    systemCapture.stop();
    stopProcessCaptures();
    outputManager.removeAudioCallback (&renderCallback);
    inputManager.removeAudioCallback (&captureCallback);
    outputManager.closeAudioDevice();
    inputManager.closeAudioDevice();

    inputManager.setCurrentAudioDeviceType (inputManager.preferredTypeName(), true);
    outputManager.setCurrentAudioDeviceType (outputManager.preferredTypeName(), true);

    auto inputSetup = inputManager.getAudioDeviceSetup();
    inputSetup.inputDeviceName = resolvedInputName;
    inputSetup.outputDeviceName.clear();
    inputSetup.useDefaultInputChannels = false;
    inputSetup.useDefaultOutputChannels = false;
    inputSetup.inputChannels.clear();
    inputSetup.inputChannels.setRange (0, 2, true);
    inputSetup.outputChannels.clear();
    inputSetup.sampleRate = 48000.0;
    if (preferredBufferSize > 0) inputSetup.bufferSize = preferredBufferSize;

    auto outputSetup = outputManager.getAudioDeviceSetup();
    outputSetup.inputDeviceName.clear();
    outputSetup.outputDeviceName = resolvedOutputName;
    outputSetup.useDefaultInputChannels = false;
    outputSetup.useDefaultOutputChannels = false;
    outputSetup.inputChannels.clear();
    outputSetup.outputChannels.clear();
    outputSetup.outputChannels.setRange (0, 2, true);
    outputSetup.sampleRate = 48000.0;
    if (preferredBufferSize > 0) outputSetup.bufferSize = preferredBufferSize;

    const auto inputError = inputManager.setAudioDeviceSetup (inputSetup, true);
    const auto outputError = outputManager.setAudioDeviceSetup (outputSetup, true);
    juce::String error;
    if (inputError.isNotEmpty()) error << "Capture: " << inputError;
    if (outputError.isNotEmpty())
    {
        if (error.isNotEmpty()) error << " | ";
        error << "Render: " << outputError;
    }

    if (inputManager.getCurrentAudioDevice() == nullptr)
    {
        if (error.isNotEmpty()) error << " | ";
        error << "Capture: no input device is open";
    }
    if (outputManager.getCurrentAudioDevice() == nullptr)
    {
        if (error.isNotEmpty()) error << " | ";
        error << "Render: no output device is open";
    }

    if (pluginChain == nullptr)
    {
        graph.clear();
        auto inNode = graph.addNode (std::make_unique<IOProc> (IOProc::audioInputNode));
        auto outNode = graph.addNode (std::make_unique<IOProc> (IOProc::audioOutputNode));
        pluginChain = std::make_unique<PluginChain> (graph, inNode->nodeID, outNode->nodeID);
        rebuildGraph();
        graph.setPlayHead (&playHead);
    }

    if (error.isNotEmpty())
    {
        outputManager.closeAudioDevice();
        inputManager.closeAudioDevice();
        juce::Logger::writeToLog ("Independent device setup failed: " + error);
        return error;
    }

    const auto inputRate = inputManager.getCurrentAudioDevice()->getCurrentSampleRate();
    const auto outputRate = outputManager.getCurrentAudioDevice()->getCurrentSampleRate();
    captureSampleRate.store (inputRate);
    graphSampleRate.store (outputRate);
    playHead.sampleRate.store (outputRate);
    audioBridge.reset (inputRate, outputRate);

    juce::Logger::writeToLog ("Independent setup: in=" + resolvedInputName
        + " [" + stableInputDeviceId + "] @ " + juce::String (inputRate, 0)
        + " Hz | out=" + resolvedOutputName + " [" + stableOutputDeviceId + "]"
        + " @ " + juce::String (outputRate, 0) + " Hz");

    inputManager.addAudioCallback (&captureCallback);
    outputManager.addAudioCallback (&renderCallback);
    if (getMixerMode() != MixerMode::voice
        && (systemDeviceName.isNotEmpty() || systemDeviceId.isNotEmpty()))
    {
        const auto feedbackEvidence = probeFeedbackLoop (systemDeviceName, systemDeviceId);
        const auto feedbackDecision = evaluateFeedbackLoop (feedbackEvidence);
        feedbackLoopDetected.store (feedbackDecision.risk, std::memory_order_release);
        feedbackLoopGuarded.store (false, std::memory_order_release);
        listenRouteActive.store (isRelevantListenRoute (feedbackEvidence),
                                 std::memory_order_release);
        feedbackLoopDescription = feedbackDecision.reason;
        if (feedbackDecision.risk)
        {
            mixerMode.store (static_cast<int> (MixerMode::voice), std::memory_order_release);
            stop();
            return "Feedback prevention: " + feedbackDecision.reason
                + ". Choose Voice or disable Windows Listen before enabling PC audio.";
        }
        const auto loopbackError = isProcessFilterEnabled()
            ? reconcileProcessCaptures()
            : systemCapture.start (systemDeviceName, systemDeviceId, outputRate);
        if (loopbackError.isNotEmpty())
        {
            stop();
            return "System loopback: " + loopbackError;
        }
    }
    return {};
}

void AudioEngine::setDeviceConfig (const juce::String& input, const juce::String& output,
                                  double sampleRate, int bufferSize)
{
    juce::ignoreUnused (sampleRate);
    if (bufferSize > 0) preferredBufferSize = bufferSize;
    initialise (input, output);
}

juce::String AudioEngine::configureMixer (MixerMode mode,
                                          const juce::String& systemDeviceName,
                                          const juce::String& systemDeviceId,
                                          float newVoiceGain,
                                          float newSystemGain,
                                          bool newDuckingEnabled,
                                          float newDuckAmount,
                                          float newVoiceThresholdDb,
                                          int newAttackMs,
                                          int newHoldMs,
                                          int newReleaseMs)
{
    if (newVoiceGain < 0.0f || newVoiceGain > 4.0f
        || newSystemGain < 0.0f || newSystemGain > 4.0f)
        return "Mixer gains must be between 0 and 4.";
    if (newDuckAmount < 0.0f || newDuckAmount > 1.0f)
        return "Ducking amount must be between 0 and 1.";
    if (newAttackMs < 0 || newHoldMs < 0 || newReleaseMs < 0)
        return "Ducking timing cannot be negative.";
    if (mode != MixerMode::voice && systemDeviceName.isEmpty() && systemDeviceId.isEmpty())
        return "A system render endpoint is required for PC audio.";

    const auto feedbackEvidence = probeFeedbackLoop (systemDeviceName, systemDeviceId);
    const auto feedbackDecision = evaluateFeedbackLoop (feedbackEvidence);
    feedbackLoopDetected.store (feedbackDecision.risk, std::memory_order_release);
    feedbackLoopGuarded.store (false, std::memory_order_release);
    listenRouteActive.store (isRelevantListenRoute (feedbackEvidence),
                             std::memory_order_release);
    feedbackLoopDescription = feedbackDecision.reason;
    if (mode != MixerMode::voice && feedbackDecision.risk)
        return "Feedback prevention: " + feedbackDecision.reason
            + ". Choose Voice or disable Windows Listen before enabling PC audio.";

    voiceGain.store (newVoiceGain);
    systemGain.store (newSystemGain);
    duckingEnabled.store (newDuckingEnabled);
    duckAmount.store (newDuckAmount);
    duckThresholdDb.store (newVoiceThresholdDb);
    duckAttackMs.store (newAttackMs);
    duckHoldMs.store (newHoldMs);
    duckReleaseMs.store (newReleaseMs);

    if (mode == MixerMode::voice)
    {
        systemCapture.stop();
        stopProcessCaptures();
        mixerMode.store (static_cast<int> (mode));
        return {};
    }

    processFilterDeviceName = systemDeviceName;
    processFilterDeviceId = systemDeviceId;
    systemCapture.stop();
    juce::String error;
    if (isProcessFilterEnabled()) error = reconcileProcessCaptures();
    else
    {
        stopProcessCaptures();
        error = systemCapture.start (systemDeviceName, systemDeviceId, graphSampleRate.load());
    }
    if (error.isNotEmpty())
    {
        mixerMode.store (static_cast<int> (MixerMode::voice));
        return error;
    }
    mixerMode.store (static_cast<int> (mode));
    return {};
}

juce::String AudioEngine::configureProcessFilter (
    bool enabled,
    bool exclusionMode,
    const juce::String& systemDeviceName,
    const juce::String& systemDeviceId,
    const juce::Array<ProcessMixRuleState>& rules)
{
    for (const auto& rule : rules)
        if (rule.executablePath.isEmpty() || rule.gain < 0.0f || rule.gain > 4.0f)
            return "Every application rule needs an identity and a gain between 0 and 4.";

    if (getMixerMode() == MixerMode::voice)
    {
        processFilterDeviceName = systemDeviceName;
        processFilterDeviceId = systemDeviceId;
        processMixRules = rules;
        processFilterExclusionMode.store (exclusionMode, std::memory_order_release);
        processFilterEnabled.store (enabled, std::memory_order_release);
        systemCapture.stop();
        stopProcessCaptures();
        return {};
    }

    if (systemDeviceName.isEmpty() && systemDeviceId.isEmpty())
        return "A system render endpoint is required for PC audio.";

    const auto feedbackEvidence = probeFeedbackLoop (systemDeviceName, systemDeviceId);
    const auto feedbackDecision = evaluateFeedbackLoop (feedbackEvidence);
    feedbackLoopDetected.store (feedbackDecision.risk, std::memory_order_release);
    feedbackLoopGuarded.store (false, std::memory_order_release);
    listenRouteActive.store (isRelevantListenRoute (feedbackEvidence),
                             std::memory_order_release);
    feedbackLoopDescription = feedbackDecision.reason;
    if (feedbackDecision.risk)
        return "Feedback prevention: " + feedbackDecision.reason
            + ". Choose Voice or disable Windows Listen before enabling PC audio.";

    processFilterDeviceName = systemDeviceName;
    processFilterDeviceId = systemDeviceId;
    processMixRules = rules;
    processFilterExclusionMode.store (exclusionMode, std::memory_order_release);
    processFilterEnabled.store (enabled, std::memory_order_release);

    if (enabled)
    {
        systemCapture.stop();
        return reconcileProcessCaptures();
    }

    stopProcessCaptures();
    return systemCapture.start (systemDeviceName, systemDeviceId, graphSampleRate.load());
}

int AudioEngine::getActiveProcessCaptureCount() const
{
    int count = 0;
    for (const auto& included : processCaptureIncluded)
        if (included.load (std::memory_order_acquire)) ++count;
    return count;
}

void AudioEngine::stopProcessCaptures()
{
    for (std::size_t index = 0; index < maxProcessCaptures; ++index)
    {
        processCaptureIncluded[index].store (false, std::memory_order_release);
        processCaptures[index]->stop();
        processCapturePids[index].store (0, std::memory_order_release);
        processCaptureKeys[index].clear();
    }
}

juce::String AudioEngine::reconcileProcessCaptures()
{
    if (! isProcessFilterEnabled() || getMixerMode() == MixerMode::voice) return {};

    juce::String error;
    const auto sessions = listRenderAudioSessions (processFilterDeviceName, processFilterDeviceId, error);
    if (error.isNotEmpty())
    {
        stopProcessCaptures();
        mixerMode.store (static_cast<int> (MixerMode::voice), std::memory_order_release);
        feedbackLoopDetected.store (true, std::memory_order_release);
        feedbackLoopGuarded.store (true, std::memory_order_release);
        feedbackProbeUnknown.store (true, std::memory_order_release);
        feedbackLoopDescription = "System process consumers could not be enumerated safely: " + error;
        return feedbackLoopDescription;
    }

    struct DesiredCapture { unsigned long pid; juce::String key; float gain; };
    juce::Array<DesiredCapture> desired;
    for (const auto& session : sessions)
    {
        if (! session.active) continue;
        const auto sessionKey = session.executablePath.isNotEmpty()
            ? session.executablePath
            : "session:" + session.sessionId;
        auto included = isProcessFilterExclusionMode();
        auto gain = 1.0f;
        for (const auto& rule : processMixRules)
        {
            if (rule.executablePath.equalsIgnoreCase (sessionKey))
            {
                included = ! rule.excluded;
                gain = juce::jlimit (0.0f, 4.0f, rule.gain);
                break;
            }
        }
        if (! included) continue;
        const auto alreadyAdded = std::find_if (desired.begin(), desired.end(), [&session] (const auto& item)
        {
            return item.pid == session.processId;
        }) != desired.end();
        if (! alreadyAdded) desired.add ({ session.processId, sessionKey, gain });
    }

    for (std::size_t index = 0; index < maxProcessCaptures; ++index)
    {
        const auto pid = processCapturePids[index].load (std::memory_order_acquire);
        if (pid == 0) continue;
        const auto match = std::find_if (desired.begin(), desired.end(), [pid] (const auto& item)
        {
            return item.pid == pid;
        });
        if (match == desired.end())
        {
            processCaptureIncluded[index].store (false, std::memory_order_release);
            processCaptures[index]->stop();
            processCapturePids[index].store (0, std::memory_order_release);
            processCaptureKeys[index].clear();
        }
        else
        {
            processCaptureGains[index].store (match->gain, std::memory_order_release);
            desired.remove (static_cast<int> (match - desired.begin()));
        }
    }

    // Re-probe after removing slots that are no longer desired.  A receiver
    // may have appeared since the last tick, and a process-loopback target
    // includes its entire child tree. Never create a new slot whose tree
    // contains a process consuming the final cable capture.
    auto feedbackEvidence = probeFeedbackLoop (processFilterDeviceName, processFilterDeviceId);
    if (feedbackEvidence.finalCaptureConsumerDetected)
    {
        for (const auto& item : desired)
            for (const auto& consumer : feedbackEvidence.finalCaptureConsumers)
                if (processTreeRelatedAtRuntime (item.pid, consumer.processId))
                    feedbackEvidence.finalCaptureConsumersExcluded = false;
    }
    const auto feedbackDecision = evaluateFeedbackLoop (feedbackEvidence);
    if (feedbackDecision.risk)
    {
        applyFeedbackLoopStatus (feedbackEvidence, true);
        return "Feedback prevention: " + feedbackDecision.reason
            + ". System mixing returned to Voice.";
    }

    juce::StringArray failures;
    for (const auto& item : desired)
    {
        std::size_t freeIndex = maxProcessCaptures;
        for (std::size_t index = 0; index < maxProcessCaptures; ++index)
            if (processCapturePids[index].load (std::memory_order_acquire) == 0)
                { freeIndex = index; break; }
        if (freeIndex == maxProcessCaptures)
        {
            failures.add ("No free application capture slot for PID " + juce::String (item.pid));
            continue;
        }

        processCaptureIncluded[freeIndex].store (false, std::memory_order_release);
        const auto startError = processCaptures[freeIndex]->start (item.pid, graphSampleRate.load());
        if (startError.isNotEmpty())
        {
            failures.add ("PID " + juce::String (item.pid) + ": " + startError);
            continue;
        }
        processCaptureKeys[freeIndex] = item.key;
        processCaptureGains[freeIndex].store (item.gain, std::memory_order_release);
        processCapturePids[freeIndex].store (item.pid, std::memory_order_release);
        processCaptureIncluded[freeIndex].store (true, std::memory_order_release);
    }
    return failures.joinIntoString (" | ");
}

void AudioEngine::refreshProcessFilter()
{
    if (isProcessFilterEnabled() && getMixerMode() != MixerMode::voice)
        if (const auto error = reconcileProcessCaptures(); error.isNotEmpty())
            juce::Logger::writeToLog ("Application capture refresh: " + error);
}

void AudioEngine::rebuildGraph()
{
    if (pluginChain != nullptr)
        pluginChain->rebuildConnections();
}

juce::File AudioEngine::pluginCacheFile()
{
    return juce::File::getSpecialLocation (juce::File::userApplicationDataDirectory)
              .getChildFile ("UnifiedAudio").getChildFile ("plugin_cache.xml");
}

juce::StringArray AudioEngine::scanRoots() const
{
    // JUCE-Default-Orte statt hartkodiertem Pfad: deckt neben Program Files auch
    // %LOCALAPPDATA%\Programs\Common\VST3 und die VST3_PATH-Umgebungsvariable ab.
    UnifiedAudio3Format vst3;
    juce::StringArray roots;
    const auto defaults = vst3.getDefaultLocationsToSearch();
    for (int i = 0; i < defaults.getNumPaths(); ++i)
        roots.add (defaults[i].getFullPathName());
    for (auto& f : pluginFolders)
        if (f.isNotEmpty()) roots.add (f);
    return roots;
}

juce::StringArray AudioEngine::listVst3Files() const
{
    UnifiedAudio3Format vst3;
    juce::FileSearchPath paths;
    for (auto& r : scanRoots())
        paths.add (juce::File (r));
    paths.removeRedundantPaths();
    return vst3.searchPathsForPlugins (paths, true, true);
}

void AudioEngine::loadPluginCache()
{
    if (! PluginScanCache::load (pluginCacheFile(), knownPlugins, skippedPlugins, &pluginFingerprints))
        juce::Logger::writeToLog ("Plugin-Cache fehlt/korrupt -> voller Scan");
}

void AudioEngine::startBackgroundScan (int timeoutMs, const juce::StringArray& forceRescan)
{
    if (isScanning()) { rescanQueued = true; return; }

    const auto allFiles = listVst3Files();
    fingerprintAuditor = std::make_unique<PluginFingerprintCoordinator> (
        allFiles,
        pluginFingerprints,
        [this, allFiles, forceRescan, timeoutMs] (PluginFingerprintAuditOutcome outcome)
        {
            fingerprintAuditor = nullptr;
            if (! outcome.completed) return;
            handleFingerprintAudit (outcome, allFiles, forceRescan, timeoutMs);
            if (scanner != nullptr) return;
            if (rescanQueued)
            {
                rescanQueued = false;
                startBackgroundScan (timeoutMs);
            }
        });
}

void AudioEngine::handleFingerprintAudit (const PluginFingerprintAuditOutcome& outcome,
                                          const juce::StringArray& allFiles,
                                          const juce::StringArray& forceRescan,
                                          int timeoutMs)
{
    pluginFingerprints = outcome.current;
    auto forced = forceRescan;
    forced.addArray (outcome.changed);
    forced.removeDuplicates (false);

    UnifiedAudio3Format vst3;
    auto files = filterFilesNeedingScan (allFiles, knownPlugins, vst3, skippedPlugins, forced);
    juce::Logger::writeToLog ("Scan: " + juce::String (files.size()) + " Datei(en) zu scannen");
    if (files.isEmpty())
    {
        PluginScanCache::save (pluginCacheFile(), knownPlugins, skippedPlugins, pluginFingerprints);
        if (! pendingPlugins.isEmpty()) { applyPluginChainState (pendingPlugins); pendingPlugins.clear(); }
        if (onScanFinished) onScanFinished();
        return;
    }

    scanner = std::make_unique<ScanCoordinator> (files,
        [this] (int cur, int total, juce::String name)
        {
            if (onScanProgress) onScanProgress (cur, total, name);
        },
        [this] (ScanOutcome outcome) { handleScanFinished (outcome); },
        timeoutMs);
}

void AudioEngine::handleScanFinished (const ScanOutcome& outcome)
{
    scanner = nullptr;   // Callback kommt via callAsync -> wir sind auf dem Message-Thread

    mergeScanResults (knownPlugins, outcome);
    for (auto& s : outcome.skipped)
    {
        skippedPlugins.add (s);
        juce::Logger::writeToLog ("Scan übersprungen (" + s.reason + "): " + s.file);
    }

    // Ordner können während des Scans entfernt worden sein -> NACH dem Übernehmen wegputzen,
    // damit auch frisch gescannte Fremd-Ergebnisse rausfliegen.
    pruneOutsideFolders();

    PluginScanCache::save (pluginCacheFile(), knownPlugins, skippedPlugins, pluginFingerprints);

    if (! pendingPlugins.isEmpty())
    {
        applyPluginChainState (pendingPlugins);
        pendingPlugins.clear();
    }
    if (onScanFinished) onScanFinished();

    if (rescanQueued) { rescanQueued = false; startBackgroundScan(); }
}

void AudioEngine::rescanAllPlugins()
{
    if (isScanning()) return;
    knownPlugins.clear();
    skippedPlugins.clear();
    pluginFingerprints.clear();
    pluginCacheFile().deleteFile();
    startBackgroundScan();
}

void AudioEngine::retrySkippedPlugins()
{
    if (isScanning() || skippedPlugins.isEmpty()) return;

    // Crash-Rescue: Gerettete Typen sind im Cache schon mit AKTUELLER effectiveModTime
    // gestempelt (mergeScanResults) -> ohne forceRescan hielte filterFilesNeedingScan die
    // Datei für up-to-date und der Retry würde für sie stillschweigend nichts tun. Vor dem
    // Leeren einsammeln (skippedPlugins ist danach weg), nur noch existierende Dateien.
    juce::StringArray forceRescan;
    for (auto& s : skippedPlugins)
        if (juce::File (s.file).exists())
            forceRescan.add (s.file);

    skippedPlugins.clear();   // Cache/Fundliste bleiben -> nur die Geskippten werden gescannt
    startBackgroundScan (ScanCoordinator::retryTimeoutMs, forceRescan);
}

void AudioEngine::skipCurrentScanFile()
{
    if (scanner != nullptr) scanner->skipCurrentFile();
}

void AudioEngine::pruneOutsideFolders()
{
    const auto roots = scanRoots();
    for (auto& t : knownPlugins.getTypes())
        if (! pathIsInsideAnyFolder (t.fileOrIdentifier, roots)) knownPlugins.removeType (t);
    for (int i = skippedPlugins.size(); --i >= 0;)
        if (! pathIsInsideAnyFolder (skippedPlugins[i].file, roots)) skippedPlugins.remove (i);
    for (int i = pluginFingerprints.size(); --i >= 0;)
        if (! pathIsInsideAnyFolder (pluginFingerprints.getReference (i).file, roots))
            pluginFingerprints.remove (i);
}

void AudioEngine::addPluginFolder (const juce::String& folder)
{
    if (folder.isNotEmpty() && ! pluginFolders.contains (folder))
        pluginFolders.add (folder);
    startBackgroundScan();
}

void AudioEngine::removePluginFolder (const juce::String& folder)
{
    pluginFolders.removeString (folder);
    pruneOutsideFolders();
    PluginScanCache::save (pluginCacheFile(), knownPlugins, skippedPlugins, pluginFingerprints);
    startBackgroundScan();
}

UnifiedAudioState AudioEngine::captureState()
{
    UnifiedAudioState s;
    const auto inputSetup = inputManager.getAudioDeviceSetup();
    const auto outputSetup = outputManager.getAudioDeviceSetup();
    s.inputDevice  = inputSetup.inputDeviceName;
    s.outputDevice = outputSetup.outputDeviceName;
    s.inputDeviceId = stableInputDeviceId;
    s.outputDeviceId = stableOutputDeviceId;
    s.sampleRate   = graphSampleRate.load();
    s.bufferSize   = preferredBufferSize;
    s.pluginFolders = pluginFolders;
    s.mixerMode = static_cast<int> (getMixerMode());
    s.systemCaptureDevice = getSystemCaptureDeviceName();
    s.systemCaptureDeviceId = getSystemCaptureDeviceId();
    s.voiceGain = getVoiceGain();
    s.systemGain = getSystemGain();
    s.duckingEnabled = getDuckingEnabled();
    s.duckAmount = getDuckAmount();
    s.duckThresholdDb = getDuckThresholdDb();
    s.duckAttackMs = getDuckAttackMs();
    s.duckHoldMs = getDuckHoldMs();
    s.duckReleaseMs = getDuckReleaseMs();
    s.processFilterEnabled = isProcessFilterEnabled();
    s.processFilterExclusionMode = isProcessFilterExclusionMode();
    s.processMixRules = processMixRules;

    // Ketten-Restore steht noch aus -> gemerkten Zustand verbatim zurückgeben,
    // sonst würde persistState() die gespeicherte Kette mit "leer" überschreiben.
    if (! pendingPlugins.isEmpty()) { s.plugins = pendingPlugins; return s; }
    if (pluginChain == nullptr) return s;

    for (auto& e : pluginChain->entries())
    {
        PluginEntryState p;
        p.fileOrId = e.fileOrId;
        p.bypassed = e.bypassed;
        if (auto* node = graph.getNodeForId (e.node))
        {
            auto* proc = node->getProcessor();
            const juce::ScopedLock sl (proc->getCallbackLock());   // gegen Race mit processBlock
            proc->getStateInformation (p.state);
        }
        s.plugins.add (p);
    }
    return s;
}

void AudioEngine::applyState (const UnifiedAudioState& s)
{
    setPreferredBufferSize (s.bufferSize);
    setStableDeviceIds (s.inputDeviceId, s.outputDeviceId);
    initialise (s.inputDevice, s.outputDevice);
    configureProcessFilter (s.processFilterEnabled, s.processFilterExclusionMode,
                            s.systemCaptureDevice, s.systemCaptureDeviceId,
                            s.processMixRules);
    configureMixer (static_cast<MixerMode> (juce::jlimit (0, 2, s.mixerMode)),
                    s.systemCaptureDevice, s.systemCaptureDeviceId,
                    s.voiceGain, s.systemGain,
                    s.duckingEnabled, s.duckAmount, s.duckThresholdDb,
                    s.duckAttackMs, s.duckHoldMs, s.duckReleaseMs);

    // Kette nur wiederherstellen, wenn alle Nicht-Builtin-Plugins im Cache auflösbar sind.
    // Sonst bis Scan-Ende zurückstellen (captureState liefert solange pendingPlugins,
    // damit persistState die Kette nicht mit "leer" überschreibt).
    bool allResolvable = true;
    for (auto& p : s.plugins)
        if (! p.fileOrId.startsWith ("builtin:") && knownPlugins.getTypeForFile (p.fileOrId) == nullptr)
            { allResolvable = false; break; }

    if (allResolvable) applyPluginChainState (s.plugins);
    else               { pendingPlugins = s.plugins;
                         juce::Logger::writeToLog ("Ketten-Restore wartet auf Plugin-Scan"); }
    rebuildGraph();
}

juce::String AudioEngine::applyPluginChainState (const juce::Array<PluginEntryState>& plugins)
{
    if (pluginChain == nullptr)
        return "The audio graph is not initialized.";

    while (! pluginChain->entries().empty())
        pluginChain->removePlugin (static_cast<int> (pluginChain->entries().size()) - 1);
    return restoreChain (plugins);
}

juce::String AudioEngine::restoreChain (const juce::Array<PluginEntryState>& plugins)
{
    const double sr = graphSampleRate.load();
    juce::String firstError;

    for (auto& p : plugins)
    {
        if (p.fileOrId == PluginChain::monoToStereoId || p.fileOrId == PluginChain::stereoToMonoId)
        {
            const auto previousSize = pluginChain->entries().size();
            if (p.fileOrId == PluginChain::monoToStereoId) pluginChain->addMonoToStereo();
            else                                           pluginChain->addStereoToMono();
            if (pluginChain->entries().size() == previousSize)
            {
                if (firstError.isEmpty()) firstError = "Could not add built-in channel adapter: " + p.fileOrId;
                continue;
            }
            pluginChain->setBypass ((int) pluginChain->entries().size() - 1, p.bypassed);
            continue;
        }

        auto type = knownPlugins.getTypeForFile (p.fileOrId);
        if (type == nullptr)
        {
            const auto message = "Plugin is unavailable: " + p.fileOrId;
            juce::Logger::writeToLog (message);
            if (firstError.isEmpty()) firstError = message;
            continue;
        }
        juce::String err;
        if (pluginChain->addPlugin (formatManager, *type, sr, 128, err))
        {
            const int idx = (int) pluginChain->entries().size() - 1;
            if (auto* node = graph.getNodeForId (pluginChain->entries()[(size_t) idx].node))
                node->getProcessor()->setStateInformation (p.state.getData(), (int) p.state.getSize());
            pluginChain->setBypass (idx, p.bypassed);
        }
        else
        {
            const auto message = "Plugin load failed for " + p.fileOrId + ": " + err;
            juce::Logger::writeToLog (message);
            if (firstError.isEmpty()) firstError = message;
        }
    }
    rebuildGraph();
    return firstError;
}

void AudioEngine::CaptureCallback::audioDeviceIOCallbackWithContext (
    const float* const* inputs, int numInputs, float* const* outputs, int numOutputs,
    int numSamples, const juce::AudioIODeviceCallbackContext&)
{
    for (int channel = 0; channel < numOutputs; ++channel)
        if (outputs[channel] != nullptr) juce::FloatVectorOperations::clear (outputs[channel], numSamples);
    owner.captureAudio (inputs, numInputs, numSamples);
}

void AudioEngine::CaptureCallback::audioDeviceAboutToStart (juce::AudioIODevice* device)
{
    owner.captureAboutToStart (device);
}

void AudioEngine::CaptureCallback::audioDeviceStopped() { owner.captureStopped(); }

void AudioEngine::RenderCallback::audioDeviceIOCallbackWithContext (
    const float* const*, int, float* const* outputs, int numOutputs,
    int numSamples, const juce::AudioIODeviceCallbackContext&)
{
    owner.renderAudio (outputs, numOutputs, numSamples);
}

void AudioEngine::RenderCallback::audioDeviceAboutToStart (juce::AudioIODevice* device)
{
    owner.renderAboutToStart (device);
}

void AudioEngine::RenderCallback::audioDeviceStopped() { owner.renderStopped(); }

void AudioEngine::captureAboutToStart (juce::AudioIODevice* device)
{
    const auto rate = device->getCurrentSampleRate();
    captureSampleRate.store (rate);
    audioBridge.reset (rate, graphSampleRate.load());
    juce::Logger::writeToLog ("Capture start: '" + device->getName() + "' | channels="
        + juce::String (device->getActiveInputChannels().countNumberOfSetBits())
        + " | sr=" + juce::String (rate, 0)
        + " | buffer=" + juce::String (device->getCurrentBufferSizeSamples()));
}

void AudioEngine::renderAboutToStart (juce::AudioIODevice* device)
{
    const auto rate = device->getCurrentSampleRate();
    const auto block = juce::jmax (device->getCurrentBufferSizeSamples(), 1);
    const auto channels = juce::jmax (device->getActiveOutputChannels().countNumberOfSetBits(), 1);
    graphSampleRate.store (rate);
    spectrumAnalyzer.setSampleRate (rate);
    graphBlockSize.store (block);
    playHead.sampleRate.store (rate);
    processBuffer.setSize (juce::jmax (2, channels), juce::jmax (8192, block), false, true, false);
    systemBuffer.setSize (2, juce::jmax (8192, block), false, true, false);
    for (auto& buffer : processMixBuffers)
        buffer.setSize (2, juce::jmax (8192, block), false, true, false);
    audioBridge.reset (captureSampleRate.load(), rate);
    graph.setPlayConfigDetails (2, channels, rate, block);
    graph.prepareToPlay (rate, block);
    juce::Logger::writeToLog ("Render start: '" + device->getName() + "' | channels="
        + juce::String (channels) + " | sr=" + juce::String (rate, 0)
        + " | buffer=" + juce::String (block));
}

void AudioEngine::captureStopped() {}
void AudioEngine::renderStopped() { graph.releaseResources(); }

void AudioEngine::captureAudio (const float* const* inputChannelData,
                                int numInputChannels,
                                int numSamples) noexcept
{
    inputMeter.process (inputChannelData, numInputChannels, numSamples);
    audioBridge.push (inputChannelData, numInputChannels, numSamples);
}

void AudioEngine::renderAudio (float* const* outputChannelData,
                               int numOutputChannels,
                               int numSamples) noexcept
{
    for (int channel = 0; channel < numOutputChannels; ++channel)
        if (outputChannelData[channel] != nullptr)
            juce::FloatVectorOperations::clear (outputChannelData[channel], numSamples);

    const auto bufferCapacity = processBuffer.getNumSamples();
    int offset = 0;
    while (offset < numSamples)
    {
        const auto chunk = juce::jmin (bufferCapacity, numSamples - offset);
        processBuffer.clear (0, chunk);
        auto* left = processBuffer.getWritePointer (0);
        auto* right = processBuffer.getWritePointer (1);
        audioBridge.pop (left, right, chunk);
        processMidi.clear();
        graph.processBlock (processBuffer, processMidi);

        const bool shouldMute = muteTarget.load (std::memory_order_acquire);
        const float targetGain = shouldMute ? 0.0f : 1.0f;
        constexpr float rampStep = 1.0f / 64.0f;
        for (int sample = 0; sample < chunk; ++sample)
        {
            if (muteGain < targetGain) muteGain = juce::jmin (targetGain, muteGain + rampStep);
            if (muteGain > targetGain) muteGain = juce::jmax (targetGain, muteGain - rampStep);
            for (int channel = 0; channel < processBuffer.getNumChannels(); ++channel)
                processBuffer.setSample (channel, sample, muteGain == 0.0f
                    ? 0.0f
                    : processBuffer.getSample (channel, sample) * muteGain);
        }
        muteSettled.store (shouldMute && muteGain == 0.0f, std::memory_order_release);
        voiceMeter.process (processBuffer.getArrayOfWritePointers(), processBuffer.getNumChannels(), chunk);

        systemBuffer.clear (0, chunk);
        if (processFilterEnabled.load (std::memory_order_acquire))
        {
            for (std::size_t index = 0; index < maxProcessCaptures; ++index)
            {
                if (! processCaptureIncluded[index].load (std::memory_order_acquire)) continue;
                auto& source = processMixBuffers[index];
                source.clear (0, chunk);
                processCaptures[index]->pop (source.getWritePointer (0), source.getWritePointer (1), chunk);
                const auto gain = processCaptureGains[index].load (std::memory_order_relaxed);
                systemBuffer.addFrom (0, 0, source, 0, 0, chunk, gain);
                systemBuffer.addFrom (1, 0, source, 1, 0, chunk, gain);
            }
        }
        else
        {
            systemBridge.pop (systemBuffer.getWritePointer (0), systemBuffer.getWritePointer (1), chunk);
        }
        systemMeter.process (systemBuffer.getArrayOfWritePointers(), 2, chunk);

        const auto voiceReading = computeLevel (processBuffer.getArrayOfReadPointers(),
                                                processBuffer.getNumChannels(), chunk);
        const auto voiceDb = voiceReading.rms > 0.0f
            ? 20.0f * std::log10 (voiceReading.rms)
            : -160.0f;
        const auto rate = static_cast<float> (graphSampleRate.load());
        const auto duckEnabled = duckingEnabled.load();
        if (duckEnabled && voiceDb >= duckThresholdDb.load())
            duckHoldFramesRemaining = static_cast<int> (rate * duckHoldMs.load() / 1000.0f);

        const auto mode = getMixerMode();
        const auto includeVoice = mode != MixerMode::system;
        const auto includeSystem = mode != MixerMode::voice;
        const auto requestedDuckGain = duckEnabled && (voiceDb >= duckThresholdDb.load() || duckHoldFramesRemaining > 0)
            ? 1.0f - duckAmount.load()
            : 1.0f;
        auto currentDuckGain = duckEnvelope.load (std::memory_order_relaxed);
        const auto attackFrames = juce::jmax (1.0f, rate * duckAttackMs.load() / 1000.0f);
        const auto releaseFrames = juce::jmax (1.0f, rate * duckReleaseMs.load() / 1000.0f);
        const auto attackStep = 1.0f / attackFrames;
        const auto releaseStep = 1.0f / releaseFrames;
        const auto voiceLevel = voiceGain.load();
        const auto pcLevel = systemGain.load();
        for (int sample = 0; sample < chunk; ++sample)
        {
            if (currentDuckGain > requestedDuckGain)
                currentDuckGain = juce::jmax (requestedDuckGain, currentDuckGain - attackStep);
            else if (currentDuckGain < requestedDuckGain)
                currentDuckGain = juce::jmin (requestedDuckGain, currentDuckGain + releaseStep);
            for (int channel = 0; channel < processBuffer.getNumChannels(); ++channel)
            {
                const auto voice = includeVoice ? processBuffer.getSample (channel, sample) * voiceLevel : 0.0f;
                const auto systemChannel = juce::jmin (channel, 1);
                const auto pc = includeSystem
                    ? systemBuffer.getSample (systemChannel, sample) * pcLevel * currentDuckGain
                    : 0.0f;
                processBuffer.setSample (channel, sample, voice + pc);
            }
            if (duckHoldFramesRemaining > 0) --duckHoldFramesRemaining;
        }
        duckEnvelope.store (currentDuckGain, std::memory_order_relaxed);

        outputMeter.process (processBuffer.getArrayOfWritePointers(), processBuffer.getNumChannels(), chunk);
        spectrumAnalyzer.push (processBuffer, chunk);
        for (int channel = 0; channel < numOutputChannels; ++channel)
        {
            if (outputChannelData[channel] == nullptr) continue;
            const auto sourceChannel = juce::jmin (channel, processBuffer.getNumChannels() - 1);
            juce::FloatVectorOperations::copy (outputChannelData[channel] + offset,
                                               processBuffer.getReadPointer (sourceChannel), chunk);
        }
        playHead.samples.fetch_add (chunk, std::memory_order_relaxed);
        offset += chunk;
    }
}
