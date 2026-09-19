#include <juce_gui_basics/juce_gui_basics.h>
#include "audio/AudioEngine.h"
#include "state/Persistence.h"
#include "BoundedLogger.h"

#if JUCE_WINDOWS
 #define WIN32_LEAN_AND_MEAN
 #define NOMINMAX
 #include <windows.h>
 #include <sddl.h>
 #include <excpt.h>
#endif

namespace
{
constexpr int contractVersion = 1;
constexpr int maxFrameBytes = 8 * 1024 * 1024;
constexpr const wchar_t* pipePath = L"\\\\.\\pipe\\UnifiedAudio.Engine.v1";

juce::var objectWith (std::initializer_list<std::pair<juce::Identifier, juce::var>> properties)
{
    auto result = std::make_unique<juce::DynamicObject>();
    for (const auto& [name, value] : properties)
        result->setProperty (name, value);
    return juce::var (result.release());
}

juce::var arrayOfStrings (const juce::StringArray& strings)
{
    juce::Array<juce::var> values;
    for (const auto& value : strings)
        values.add (value);
    return juce::var (values);
}

juce::String responseFor (const juce::var& request, juce::var payload,
                          const juce::String& errorCode = {}, const juce::String& errorMessage = {})
{
    const auto requestId = request.getProperty ("messageId", {}).toString();
    auto error = juce::var();
    if (errorCode.isNotEmpty())
        error = objectWith ({ { "code", errorCode }, { "message", errorMessage } });

    return juce::JSON::toString (objectWith ({
        { "contractVersion", contractVersion },
        { "messageId", requestId },
        { "sequence", request.getProperty ("sequence", 0) },
        { "kind", "response" },
        { "name", request.getProperty ("name", {}).toString() },
        { "payload", payload },
        { "error", error }
    }), true);
}

#if JUCE_WINDOWS
unsigned int findTypesWithSehGuard (juce::VST3PluginFormat& format,
                                    juce::OwnedArray<juce::PluginDescription>& types,
                                    const juce::String& path)
{
    __try
    {
        format.findAllTypesForFile (types, path);
        return 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return static_cast<unsigned int> (GetExceptionCode());
    }
}
#endif

int runScanChildMode()
{
#if JUCE_WINDOWS
    SetErrorMode (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
#endif
    const auto args = juce::JUCEApplicationBase::getCommandLineParameterArray();
    juce::String pluginPath, outputPath;
    for (int index = 0; index < args.size() - 1; ++index)
    {
        if (args[index] == "--scan") pluginPath = args[index + 1].unquoted();
        if (args[index] == "--out") outputPath = args[index + 1].unquoted();
    }
    if (pluginPath.isEmpty() || outputPath.isEmpty())
        return 2;

    juce::VST3PluginFormat format;
    juce::OwnedArray<juce::PluginDescription> types;
#if JUCE_WINDOWS
    const auto crashCode = findTypesWithSehGuard (format, types, pluginPath);
#else
    const unsigned int crashCode = 0;
    format.findAllTypesForFile (types, pluginPath);
#endif
    if (crashCode == 0 && types.isEmpty())
        return 1;

    juce::XmlElement root ("UnifiedAudioScanResult");
    if (crashCode != 0)
        root.setAttribute ("crashCode", "0x" + juce::String::toHexString (static_cast<int> (crashCode)).toUpperCase());
    for (auto* type : types)
        root.addChildElement (type->createXml().release());
    if (! root.writeTo (juce::File (outputPath)))
        return 1;
    return crashCode == 0 ? 0 : 3;
}

class HostApplication;

#if JUCE_WINDOWS
class PipeServer final : private juce::Thread
{
public:
    explicit PipeServer (std::function<juce::String (juce::String)> dispatchIn)
        : juce::Thread ("EngineControlPipe"), dispatch (std::move (dispatchIn))
    {
        startThread();
    }

    ~PipeServer() override
    {
        signalThreadShouldExit();
        if (auto handle = currentPipe.exchange (INVALID_HANDLE_VALUE); handle != INVALID_HANDLE_VALUE)
        {
            CancelIoEx (handle, nullptr);
            DisconnectNamedPipe (handle);
            CloseHandle (handle);
        }
        stopThread (5000);
    }

private:
    static bool readExact (HANDLE pipe, void* destination, DWORD bytes)
    {
        auto* output = static_cast<unsigned char*> (destination);
        DWORD done = 0;
        while (done < bytes)
        {
            DWORD received = 0;
            if (! ReadFile (pipe, output + done, bytes - done, &received, nullptr) || received == 0)
                return false;
            done += received;
        }
        return true;
    }

    static bool writeExact (HANDLE pipe, const void* source, DWORD bytes)
    {
        const auto* input = static_cast<const unsigned char*> (source);
        DWORD done = 0;
        while (done < bytes)
        {
            DWORD written = 0;
            if (! WriteFile (pipe, input + done, bytes - done, &written, nullptr) || written == 0)
                return false;
            done += written;
        }
        return true;
    }

    void serveClient (HANDLE pipe)
    {
        while (! threadShouldExit())
        {
            std::uint32_t length = 0;
            if (! readExact (pipe, &length, sizeof (length)))
                return;
            if (length == 0 || length > maxFrameBytes)
                return;

            juce::MemoryBlock body (length, true);
            if (! readExact (pipe, body.getData(), length))
                return;

            const juce::String request (juce::CharPointer_UTF8 (static_cast<const char*> (body.getData())),
                                        static_cast<int> (length));
            const auto response = dispatch (request);
            const auto utf8 = response.toUTF8();
            const auto responseLength = static_cast<std::uint32_t> (utf8.sizeInBytes() - 1);
            if (! writeExact (pipe, &responseLength, sizeof (responseLength))
                || ! writeExact (pipe, utf8.getAddress(), responseLength))
                return;
        }
    }

    void run() override
    {
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        ConvertStringSecurityDescriptorToSecurityDescriptorW (
            L"D:P(A;;GA;;;SY)(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr);
        SECURITY_ATTRIBUTES attributes { sizeof (SECURITY_ATTRIBUTES), descriptor, FALSE };

        while (! threadShouldExit())
        {
            const auto pipe = CreateNamedPipeW (
                pipePath,
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                64 * 1024,
                64 * 1024,
                0,
                descriptor != nullptr ? &attributes : nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
                break;
            currentPipe.store (pipe);

            const bool connected = ConnectNamedPipe (pipe, nullptr) != FALSE
                || GetLastError() == ERROR_PIPE_CONNECTED;
            if (connected && ! threadShouldExit())
                serveClient (pipe);

            FlushFileBuffers (pipe);
            DisconnectNamedPipe (pipe);
            if (currentPipe.exchange (INVALID_HANDLE_VALUE) == pipe)
                CloseHandle (pipe);
        }

        if (descriptor != nullptr)
            LocalFree (descriptor);
    }

    std::function<juce::String (juce::String)> dispatch;
    std::atomic<HANDLE> currentPipe { INVALID_HANDLE_VALUE };
};
#endif

class PluginEditorWindow final : public juce::DocumentWindow
{
public:
    PluginEditorWindow (const juce::String& title, juce::AudioProcessorEditor* editor)
        : juce::DocumentWindow (title,
                                juce::Desktop::getInstance().getDefaultLookAndFeel()
                                    .findColour (juce::ResizableWindow::backgroundColourId),
                                juce::DocumentWindow::closeButton)
    {
        setUsingNativeTitleBar (true);
        setContentOwned (editor, true);
        setResizable (editor->isResizable(), true);
        centreWithSize (juce::jmax (editor->getWidth(), 420),
                        juce::jmax (editor->getHeight(), 260));
        setVisible (true);
#if JUCE_WINDOWS
        // The headless host is launched with STARTF_USESHOWWINDOW/SW_HIDE.
        // Explicitly show its first editor after that startup hint is consumed.
        if (auto* peer = getPeer())
            ShowWindow (static_cast<HWND> (peer->getNativeHandle()), SW_SHOWNORMAL);
#endif
        toFront (true);
    }

    void closeButtonPressed() override { setVisible (false); }
};

class HostApplication final : public juce::JUCEApplication,
                              private juce::Timer
{
public:
    const juce::String getApplicationName() override { return "UnifiedAudio Engine Host"; }
    const juce::String getApplicationVersion() override { return "0.1.4"; }
    bool moreThanOneInstanceAllowed() override { return true; }

    void initialise (const juce::String&) override
    {
        if (juce::JUCEApplicationBase::getCommandLineParameterArray().contains ("--scan"))
        {
            setApplicationReturnValue (runScanChildMode());
            quit();
            return;
        }

        instanceLock = std::make_unique<juce::InterProcessLock> ("UnifiedAudio.EngineHost.SingleInstance");
        if (! instanceLock->enter (0))
        {
            setApplicationReturnValue (2);
            quit();
            return;
        }

        const auto logDirectory = juce::File::getSpecialLocation (juce::File::userApplicationDataDirectory)
                                      .getChildFile ("UnifiedAudio").getChildFile ("logs");
        logDirectory.createDirectory();
        logger = std::make_unique<BoundedLogger> (logDirectory.getChildFile ("engine.log"));
        logger->logMessage ("UnifiedAudio Engine Host");
        juce::Logger::setCurrentLogger (logger.get());

        engine = std::make_unique<AudioEngine>();
        const auto state = loadState();
        engine->setPluginFolders (state.pluginFolders);
        engine->loadPluginCache();
        engine->applyState (state);
        engine->onStateChanged = [this] { persist(); };
        engine->onDeviceChanged = [this] { persist(); };
        engine->startBackgroundScan();
        // Listen can be toggled outside UnifiedAudio.  Keep the guard's
        // response bounded while the process-filter reconciliation remains
        // lightweight and unchanged.
        startTimer (500);

#if JUCE_WINDOWS
        pipeServer = std::make_unique<PipeServer> ([this] (juce::String request)
        {
            struct SharedResult
            {
                juce::WaitableEvent completed;
                juce::String response;
            };
            auto result = std::make_shared<SharedResult>();
            juce::MessageManager::callAsync ([this, result, request = std::move (request)]
            {
                result->response = dispatchRequest (request);
                result->completed.signal();
            });
            if (! result->completed.wait (30000))
                return juce::JSON::toString (objectWith ({
                    { "contractVersion", contractVersion },
                    { "kind", "response" },
                    { "error", objectWith ({ { "code", "timeout" }, { "message", "Engine command timed out." } }) }
                }));
            return result->response;
        });
#endif
    }

    void shutdown() override
    {
        stopTimer();
#if JUCE_WINDOWS
        pipeServer.reset();
#endif
        pluginEditors.clear (true);
        persist();
        engine.reset();
        juce::Logger::setCurrentLogger (nullptr);
        logger.reset();
        if (instanceLock != nullptr)
            instanceLock->exit();
        instanceLock.reset();
    }

private:
    void timerCallback() override
    {
        if (engine == nullptr) return;
        if (engine->refreshFeedbackLoopGuard()) persist();
        engine->refreshProcessFilter();
    }

    void persist()
    {
        if (engine != nullptr)
            saveState (engine->captureState());
    }

    juce::var snapshot()
    {
        // Listen-to-device is an external Windows route and can be toggled
        // while the engine is running.  Probe before every published snapshot
        // so the guard takes effect promptly without touching the callback.
        const auto guarded = engine->refreshFeedbackLoopGuard();
        if (guarded) persist();

        const auto input = engine->inputLevel();
        const auto voice = engine->voiceLevel();
        const auto system = engine->systemLevel();
        const auto output = engine->outputLevel();
        const auto spectrum = engine->outputSpectrum();
        juce::Array<juce::var> spectrumValues;
        for (const auto value : spectrum) spectrumValues.add (value);
        juce::Array<juce::var> processRules;
        for (const auto& rule : engine->getProcessMixRules())
            processRules.add (objectWith ({
                { "key", rule.executablePath },
                { "displayName", rule.displayName },
                { "gain", rule.gain },
                { "excluded", rule.excluded }
            }));
        return objectWith ({
            { "running", engine->isRunning() },
            { "mutedRequested", engine->isMutedRequested() },
            { "muteSettled", engine->isMuteSettled() },
            { "scanning", engine->isScanning() },
            { "inputRms", input.rms },
            { "inputPeak", input.peak },
            { "voiceRms", voice.rms },
            { "voicePeak", voice.peak },
            { "outputRms", output.rms },
            { "outputPeak", output.peak },
            { "spectrum", spectrumValues },
            { "systemRms", system.rms },
            { "systemPeak", system.peak },
            { "inputDeviceName", engine->getInputDeviceName() },
            { "outputDeviceName", engine->getOutputDeviceName() },
            { "inputDeviceId", engine->getInputDeviceId() },
            { "outputDeviceId", engine->getOutputDeviceId() },
            { "captureSampleRate", engine->getCaptureSampleRate() },
            { "graphSampleRate", engine->getGraphSampleRate() },
            { "bufferSize", engine->getPreferredBufferSize() },
            { "queuedCaptureFrames", static_cast<juce::int64> (engine->getQueuedCaptureFrames()) },
            { "underruns", static_cast<juce::int64> (engine->getUnderrunCount()) },
            { "overruns", static_cast<juce::int64> (engine->getOverrunCount()) },
            { "mixerMode", static_cast<int> (engine->getMixerMode()) },
            { "voiceGain", engine->getVoiceGain() },
            { "systemGain", engine->getSystemGain() },
            { "duckingGain", engine->getDuckingGain() },
            { "duckingEnabled", engine->getDuckingEnabled() },
            { "duckAmount", engine->getDuckAmount() },
            { "duckThresholdDb", engine->getDuckThresholdDb() },
            { "duckAttackMs", engine->getDuckAttackMs() },
            { "duckHoldMs", engine->getDuckHoldMs() },
            { "duckReleaseMs", engine->getDuckReleaseMs() },
            { "systemCaptureRunning", engine->isSystemCaptureRunning() },
            { "systemCaptureDeviceName", engine->getSystemCaptureDeviceName() },
            { "systemCaptureDeviceId", engine->getSystemCaptureDeviceId() },
            { "feedbackLoopDetected", engine->isFeedbackLoopDetected() },
            { "feedbackLoopGuarded", engine->isFeedbackLoopGuarded() },
            { "listenRouteActive", engine->isListenRouteActive() },
            { "feedbackLoopDescription", engine->getFeedbackLoopDescription() },
            { "feedbackProbeAvailable", engine->isFeedbackProbeAvailable() },
            { "feedbackProbeUnknown", engine->isFeedbackProbeUnknown() },
            { "finalCaptureConsumerProbeAvailable", engine->isFinalCaptureConsumerProbeAvailable() },
            { "finalCaptureConsumerDetected", engine->areFinalCaptureConsumersDetected() },
            { "finalCaptureEndpointId", engine->getFinalCaptureEndpointId() },
            { "finalCaptureEndpointName", engine->getFinalCaptureEndpointName() },
            { "finalCaptureConsumerSummary", engine->getFinalCaptureConsumerSummary() },
            { "processFilterEnabled", engine->isProcessFilterEnabled() },
            { "processFilterExclusionMode", engine->isProcessFilterExclusionMode() },
            { "activeProcessCaptureCount", engine->getActiveProcessCaptureCount() },
            { "configuredProcessRuleCount", engine->getProcessMixRules().size() },
            { "processRules", processRules },
            { "pluginCount", static_cast<int> (engine->getChain().entries().size()) },
            { "skippedPluginCount", engine->getSkippedPlugins().size() }
        });
    }

    static bool parseMixerMode (const juce::String& modeName, AudioEngine::MixerMode& mode)
    {
        if (modeName.equalsIgnoreCase ("Voice")) mode = AudioEngine::MixerMode::voice;
        else if (modeName.equalsIgnoreCase ("System")) mode = AudioEngine::MixerMode::system;
        else if (modeName.equalsIgnoreCase ("Both")) mode = AudioEngine::MixerMode::both;
        else return false;
        return true;
    }

    static juce::Array<ProcessMixRuleState> processRulesFrom (const juce::var& payload)
    {
        juce::Array<ProcessMixRuleState> rules;
        if (auto* values = payload.getProperty ("rules", juce::var()).getArray())
        {
            for (const auto& value : *values)
            {
                ProcessMixRuleState rule;
                rule.executablePath = value.getProperty ("key", {}).toString();
                rule.displayName = value.getProperty ("displayName", {}).toString();
                rule.gain = static_cast<float> (value.getProperty ("gain", 1.0));
                rule.excluded = static_cast<bool> (value.getProperty ("excluded", false));
                rules.add (std::move (rule));
            }
        }
        return rules;
    }

    static juce::Array<PluginEntryState> pluginStatesFrom (const juce::var& payload)
    {
        juce::Array<PluginEntryState> plugins;
        if (auto* values = payload.getProperty ("plugins", juce::var()).getArray())
        {
            for (const auto& value : *values)
            {
                PluginEntryState plugin;
                plugin.fileOrId = value.getProperty ("id", {}).toString();
                plugin.bypassed = static_cast<bool> (value.getProperty ("bypassed", false));
                plugin.state.fromBase64Encoding (value.getProperty ("stateBase64", {}).toString());
                plugins.add (std::move (plugin));
            }
        }
        return plugins;
    }

    juce::var profileEngineState() const
    {
        juce::Array<juce::var> plugins;
        for (const auto& plugin : engine->captureState().plugins)
            plugins.add (objectWith ({
                { "id", plugin.fileOrId },
                { "bypassed", plugin.bypassed },
                { "stateBase64", plugin.state.toBase64Encoding() }
            }));
        return objectWith ({ { "plugins", plugins } });
    }

    juce::String configurePipeline (const juce::var& payload)
    {
        const auto configureEngine = static_cast<bool> (payload.getProperty ("configureEngine", false));
        const auto configureMixer = static_cast<bool> (payload.getProperty ("configureMixer", false));
        const auto resetMixer = static_cast<bool> (payload.getProperty ("resetMixer", false));
        const auto configurePlugins = static_cast<bool> (payload.getProperty ("configurePlugins", false));
        const auto applyMute = static_cast<bool> (payload.getProperty ("applyMute", false));
        if (! configureEngine && ! configureMixer && ! configurePlugins && ! applyMute)
            return "The pipeline transaction does not contain any changes.";

        const auto prior = engine->captureState();
        const auto priorRunning = engine->isRunning();
        const auto priorMuted = engine->isMutedRequested();

        auto desiredMode = static_cast<AudioEngine::MixerMode> (juce::jlimit (0, 2, prior.mixerMode));
        auto desiredSystemDevice = prior.systemCaptureDevice;
        auto desiredSystemDeviceId = prior.systemCaptureDeviceId;
        auto desiredVoiceGain = prior.voiceGain;
        auto desiredSystemGain = prior.systemGain;
        auto desiredDucking = prior.duckingEnabled;
        auto desiredDuckAmount = prior.duckAmount;
        auto desiredThreshold = prior.duckThresholdDb;
        auto desiredAttack = prior.duckAttackMs;
        auto desiredHold = prior.duckHoldMs;
        auto desiredRelease = prior.duckReleaseMs;
        auto desiredFilterEnabled = prior.processFilterEnabled;
        auto desiredExclusionMode = prior.processFilterExclusionMode;
        auto desiredRules = prior.processMixRules;
        auto desiredPlugins = prior.plugins;

        // Starting/reopening endpoints from Input & Plugins is not a mixer
        // selection.  Unless that command carries an explicit mixer, its safe
        // default is Voice; the old System/Both state must not wake up merely
        // because the device was started again.  Profile transactions leave
        // resetMixer false and therefore preserve their intentional mixer.
        if (resetMixer && ! configureMixer)
        {
            desiredMode = AudioEngine::MixerMode::voice;
            desiredSystemDevice.clear();
            desiredSystemDeviceId.clear();
        }

        if (configureMixer)
        {
            const auto modeName = payload.getProperty ("mode", "Voice").toString();
            if (! parseMixerMode (modeName, desiredMode))
                return "Unknown mixer mode: " + modeName;
            desiredSystemDevice = payload.getProperty ("systemDeviceName", {}).toString();
            desiredSystemDeviceId = payload.getProperty ("systemDeviceId", {}).toString();
            desiredVoiceGain = static_cast<float> (payload.getProperty ("voiceGain", 1.0));
            desiredSystemGain = static_cast<float> (payload.getProperty ("systemGain", 1.0));
            desiredDucking = static_cast<bool> (payload.getProperty ("duckingEnabled", false));
            desiredDuckAmount = static_cast<float> (payload.getProperty ("duckAmount", 0.55));
            desiredThreshold = static_cast<float> (payload.getProperty ("voiceThresholdDb", -36.0));
            desiredAttack = static_cast<int> (payload.getProperty ("attackMs", 20));
            desiredHold = static_cast<int> (payload.getProperty ("holdMs", 200));
            desiredRelease = static_cast<int> (payload.getProperty ("releaseMs", 280));
            desiredFilterEnabled = static_cast<bool> (payload.getProperty ("processFilterEnabled", false));
            desiredExclusionMode = static_cast<bool> (payload.getProperty ("processFilterExclusionMode", false));
            desiredRules = processRulesFrom (payload);
        }
        if (configurePlugins)
        {
            desiredPlugins = pluginStatesFrom (payload);
            for (const auto& plugin : desiredPlugins)
                if (plugin.fileOrId.isEmpty())
                    return "Every plugin preset entry needs an identity.";
        }

        const auto desiredInputName = configureEngine
            ? payload.getProperty ("inputName", {}).toString() : prior.inputDevice;
        const auto desiredOutputName = configureEngine
            ? payload.getProperty ("outputName", {}).toString() : prior.outputDevice;
        const auto desiredInputId = configureEngine
            ? payload.getProperty ("inputId", {}).toString() : prior.inputDeviceId;
        const auto desiredOutputId = configureEngine
            ? payload.getProperty ("outputId", {}).toString() : prior.outputDeviceId;
        const auto desiredBufferSize = configureEngine
            ? static_cast<int> (payload.getProperty ("bufferSize", 0)) : prior.bufferSize;

        if (configureEngine
            && ((desiredInputName.isEmpty() && desiredInputId.isEmpty())
                || (desiredOutputName.isEmpty() && desiredOutputId.isEmpty())))
            return "The input and output endpoints are required.";
        if (desiredBufferSize < 0)
            return "The buffer size cannot be negative.";
        if (desiredVoiceGain < 0.0f || desiredVoiceGain > 4.0f
            || desiredSystemGain < 0.0f || desiredSystemGain > 4.0f)
            return "Mixer gains must be between 0 and 4.";
        if (desiredDuckAmount < 0.0f || desiredDuckAmount > 1.0f)
            return "Ducking amount must be between 0 and 1.";
        if (desiredAttack < 0 || desiredHold < 0 || desiredRelease < 0)
            return "Ducking timing cannot be negative.";
        if (desiredMode != AudioEngine::MixerMode::voice
            && desiredSystemDevice.isEmpty() && desiredSystemDeviceId.isEmpty())
            return "A system render endpoint is required for PC audio.";
        for (const auto& rule : desiredRules)
            if (rule.executablePath.isEmpty() || rule.gain < 0.0f || rule.gain > 4.0f)
                return "Every application rule needs an identity and a gain between 0 and 4.";

        auto rollback = [&]()
        {
            juce::String rollbackError;
            engine->configureMixer (AudioEngine::MixerMode::voice, {}, {}, prior.voiceGain, prior.systemGain,
                                    prior.duckingEnabled, prior.duckAmount, prior.duckThresholdDb,
                                    prior.duckAttackMs, prior.duckHoldMs, prior.duckReleaseMs);
            auto error = engine->configureProcessFilter (prior.processFilterEnabled,
                                                          prior.processFilterExclusionMode,
                                                          prior.systemCaptureDevice,
                                                          prior.systemCaptureDeviceId,
                                                          prior.processMixRules);
            if (error.isNotEmpty()) rollbackError = error;
            engine->setPreferredBufferSize (prior.bufferSize);
            engine->setStableDeviceIds (prior.inputDeviceId, prior.outputDeviceId);
            if (priorRunning)
            {
                error = engine->initialise (prior.inputDevice, prior.outputDevice);
                if (error.isNotEmpty()) rollbackError = error;
            }
            else
            {
                engine->stop();
            }
            error = engine->configureMixer (
                static_cast<AudioEngine::MixerMode> (juce::jlimit (0, 2, prior.mixerMode)),
                prior.systemCaptureDevice, prior.systemCaptureDeviceId,
                prior.voiceGain, prior.systemGain,
                prior.duckingEnabled, prior.duckAmount, prior.duckThresholdDb,
                prior.duckAttackMs, prior.duckHoldMs, prior.duckReleaseMs);
            if (error.isNotEmpty()) rollbackError = error;
            if (! priorRunning) engine->stop();
            error = engine->applyPluginChainState (prior.plugins);
            if (error.isNotEmpty()) rollbackError = error;
            engine->setMuted (priorMuted);
            return rollbackError;
        };

        // Enter a neutral voice-only topology first. This prevents a valid target output
        // from being compared with a system-capture endpoint that belongs to the old graph.
        engine->configureMixer (AudioEngine::MixerMode::voice, {}, {}, desiredVoiceGain, desiredSystemGain,
                                desiredDucking, desiredDuckAmount, desiredThreshold,
                                desiredAttack, desiredHold, desiredRelease);
        auto error = engine->configureProcessFilter (desiredFilterEnabled, desiredExclusionMode,
                                                     desiredSystemDevice, desiredSystemDeviceId,
                                                     desiredRules);
        if (error.isEmpty() && configureEngine)
        {
            engine->setPreferredBufferSize (desiredBufferSize);
            engine->setStableDeviceIds (desiredInputId, desiredOutputId);
            error = engine->initialise (desiredInputName, desiredOutputName);
        }
        if (error.isEmpty())
            error = engine->configureMixer (desiredMode, desiredSystemDevice, desiredSystemDeviceId,
                                            desiredVoiceGain, desiredSystemGain,
                                            desiredDucking, desiredDuckAmount, desiredThreshold,
                                            desiredAttack, desiredHold, desiredRelease);
        if (error.isEmpty() && configurePlugins)
            error = engine->applyPluginChainState (desiredPlugins);
        if (error.isEmpty() && applyMute)
            engine->setMuted (static_cast<bool> (payload.getProperty ("muted", false)));

        if (error.isNotEmpty())
        {
            const auto rollbackError = rollback();
            return rollbackError.isEmpty() ? error : error + " | Rollback failed: " + rollbackError;
        }

        engine->requestPersist();
        return {};
    }

    juce::String dispatchRequest (const juce::String& text)
    {
        const auto request = juce::JSON::parse (text);
        if (! request.isObject())
            return responseFor (request, {}, "invalid-json", "Command must be a JSON object.");
        if (static_cast<int> (request.getProperty ("contractVersion", 0)) != contractVersion)
            return responseFor (request, {}, "contract-version", "Incompatible engine contract version.");

        const auto name = request.getProperty ("name", {}).toString();
        const auto payload = request.getProperty ("payload", juce::var());
        if (name == "hello")
            return responseFor (request, objectWith ({ { "contractVersion", contractVersion }, { "engineVersion", getApplicationVersion() } }));
        if (name == "engine.snapshot")
            return responseFor (request, snapshot());
        if (name == "profile.engine.capture")
            return responseFor (request, profileEngineState());
        if (name == "devices.list")
        {
            auto& manager = engine->getDeviceManager();
            manager.setCurrentAudioDeviceType (manager.preferredTypeName(), true);
            if (auto* type = manager.getCurrentDeviceTypeObject())
            {
                type->scanForDevices();
                juce::Array<juce::var> inputEndpoints, outputEndpoints;
                for (const auto& endpoint : listAudioEndpointIdentities (true))
                    inputEndpoints.add (objectWith ({ { "id", endpoint.id }, { "name", endpoint.name } }));
                for (const auto& endpoint : listAudioEndpointIdentities (false))
                    outputEndpoints.add (objectWith ({ { "id", endpoint.id }, { "name", endpoint.name } }));
                return responseFor (request, objectWith ({
                    { "inputs", arrayOfStrings (type->getDeviceNames (true)) },
                    { "outputs", arrayOfStrings (type->getDeviceNames (false)) },
                    { "inputEndpoints", inputEndpoints },
                    { "outputEndpoints", outputEndpoints }
                }));
            }
            return responseFor (request, {}, "device-enumeration", "WASAPI device type is unavailable.");
        }
        if (name == "audio.sessions.list")
        {
            juce::String error;
            const auto sessions = listRenderAudioSessions (
                payload.getProperty ("deviceName", {}).toString(),
                payload.getProperty ("deviceId", {}).toString(), error);
            if (error.isNotEmpty())
                return responseFor (request, {}, "session-enumeration", error);
            juce::Array<juce::var> rows;
            juce::StringArray returnedKeys;
            for (const auto& session : sessions)
            {
                const auto key = session.executablePath.isNotEmpty()
                    ? session.executablePath
                    : "session:" + session.sessionId;
                // One executable can own several WASAPI sessions/processes
                // (Discord/Chromium are common examples). The UI edits one
                // durable rule per application path; AudioEngine still expands
                // that rule back to every matching PID during reconciliation.
                if (returnedKeys.contains (key, true)) continue;
                auto included = engine->isProcessFilterExclusionMode();
                auto configuredGain = 1.0f;
                auto excluded = ! included;
                for (const auto& rule : engine->getProcessMixRules())
                    if (rule.executablePath.equalsIgnoreCase (key))
                    {
                        included = ! rule.excluded;
                        excluded = rule.excluded;
                        configuredGain = rule.gain;
                        break;
                    }
                rows.add (objectWith ({
                    { "key", key },
                    { "processId", static_cast<juce::int64> (session.processId) },
                    { "displayName", session.displayName },
                    { "processName", session.processName },
                    { "executablePath", session.executablePath },
                    { "peak", session.peak },
                    { "sessionVolume", session.sessionVolume },
                    { "sessionMuted", session.sessionMuted },
                    { "active", session.active },
                    { "included", included },
                    { "excluded", excluded },
                    { "configuredGain", configuredGain }
                }));
                returnedKeys.add (key);
            }
            for (const auto& rule : engine->getProcessMixRules())
            {
                if (returnedKeys.contains (rule.executablePath, true)) continue;
                rows.add (objectWith ({
                    { "key", rule.executablePath },
                    { "processId", static_cast<juce::int64> (0) },
                    { "displayName", rule.displayName },
                    { "processName", juce::File (rule.executablePath).getFileNameWithoutExtension() },
                    { "executablePath", rule.executablePath.startsWith ("session:") ? juce::String() : rule.executablePath },
                    { "peak", 0.0f },
                    { "sessionVolume", 1.0f },
                    { "sessionMuted", false },
                    { "active", false },
                    { "included", ! rule.excluded },
                    { "excluded", rule.excluded },
                    { "configuredGain", rule.gain }
                }));
            }
            return responseFor (request, objectWith ({ { "sessions", rows } }));
        }
        if (name == "plugins.list")
        {
            juce::Array<juce::var> catalog;
            for (const auto& plugin : engine->getKnownPlugins().getTypes())
                catalog.add (objectWith ({
                    { "id", plugin.fileOrIdentifier },
                    { "name", plugin.name },
                    { "manufacturer", plugin.manufacturerName },
                    { "category", plugin.category }
                }));

            catalog.add (objectWith ({
                { "id", PluginChain::monoToStereoId },
                { "name", "Mono -> Stereo" },
                { "manufacturer", "UnifiedAudio" },
                { "category", "Channel routing" }
            }));
            catalog.add (objectWith ({
                { "id", PluginChain::stereoToMonoId },
                { "name", "Stereo -> Mono" },
                { "manufacturer", "UnifiedAudio" },
                { "category", "Channel routing" }
            }));

            juce::Array<juce::var> chain;
            int index = 0;
            for (const auto& entry : engine->getChain().entries())
            {
                auto displayName = entry.fileOrId;
                auto manufacturer = juce::String ("UnifiedAudio");
                auto hasEditor = false;
                auto latencySamples = 0;
                if (auto* node = engine->getGraph().getNodeForId (entry.node))
                {
                    auto* processor = node->getProcessor();
                    displayName = processor->getName();
                    hasEditor = processor->hasEditor();
                    latencySamples = processor->getLatencySamples();
                    if (auto* instance = dynamic_cast<juce::AudioPluginInstance*> (processor))
                        manufacturer = instance->getPluginDescription().manufacturerName;
                }
                chain.add (objectWith ({
                    { "index", index++ },
                    { "id", entry.fileOrId },
                    { "name", displayName },
                    { "manufacturer", manufacturer },
                    { "bypassed", entry.bypassed },
                    { "hasEditor", hasEditor },
                    { "latencySamples", latencySamples }
                }));
            }
            return responseFor (request, objectWith ({ { "catalog", catalog }, { "chain", chain } }));
        }
        if (name == "plugin.folders.list")
            return responseFor (request, objectWith ({
                { "folders", arrayOfStrings (engine->getPluginFolders()) }
            }));
        if (name == "engine.start")
        {
            // The legacy start command has no mixer payload.  Treat it like
            // Input & Plugins and enter a neutral Voice topology first so an
            // old persisted System/Both state cannot start a loopback as a
            // side effect of opening new endpoints.
            const auto prior = engine->captureState();
            engine->configureMixer (AudioEngine::MixerMode::voice, {}, {},
                                     prior.voiceGain, prior.systemGain,
                                     prior.duckingEnabled, prior.duckAmount,
                                     prior.duckThresholdDb, prior.duckAttackMs,
                                     prior.duckHoldMs, prior.duckReleaseMs);
            engine->setPreferredBufferSize (static_cast<int> (payload.getProperty ("bufferSize", 0)));
            engine->setStableDeviceIds (payload.getProperty ("inputId", {}).toString(),
                                        payload.getProperty ("outputId", {}).toString());
            const auto error = engine->initialise (payload.getProperty ("inputName", {}).toString(),
                                                   payload.getProperty ("outputName", {}).toString());
            persist();
            return error.isEmpty() ? responseFor (request, snapshot())
                                   : responseFor (request, snapshot(), "audio-start", error);
        }
        if (name == "pipeline.configure")
        {
            const auto error = configurePipeline (payload);
            return error.isEmpty() ? responseFor (request, snapshot())
                                   : responseFor (request, snapshot(), "pipeline-configure", error);
        }
        if (name == "engine.stop")
        {
            engine->stop();
            return responseFor (request, snapshot());
        }
        if (name == "mute.set")
        {
            engine->setMuted (static_cast<bool> (payload.getProperty ("muted", false)));
            return responseFor (request, snapshot());
        }
        if (name == "mute.toggle")
        {
            engine->toggleMuted();
            return responseFor (request, snapshot());
        }
        if (name == "mixer.configure")
        {
            const auto modeName = payload.getProperty ("mode", "Voice").toString();
            auto mode = AudioEngine::MixerMode::voice;
            if (modeName.equalsIgnoreCase ("System")) mode = AudioEngine::MixerMode::system;
            else if (modeName.equalsIgnoreCase ("Both")) mode = AudioEngine::MixerMode::both;
            else if (! modeName.equalsIgnoreCase ("Voice"))
                return responseFor (request, snapshot(), "mixer-mode", "Unknown mixer mode: " + modeName);
            const auto error = engine->configureMixer (
                mode,
                payload.getProperty ("systemDeviceName", {}).toString(),
                payload.getProperty ("systemDeviceId", {}).toString(),
                static_cast<float> (payload.getProperty ("voiceGain", 1.0)),
                static_cast<float> (payload.getProperty ("systemGain", 1.0)),
                static_cast<bool> (payload.getProperty ("duckingEnabled", false)),
                static_cast<float> (payload.getProperty ("duckAmount", 0.55)),
                static_cast<float> (payload.getProperty ("voiceThresholdDb", -36.0)),
                static_cast<int> (payload.getProperty ("attackMs", 20)),
                static_cast<int> (payload.getProperty ("holdMs", 200)),
                static_cast<int> (payload.getProperty ("releaseMs", 280)));
            if (error.isEmpty()) engine->requestPersist();
            return error.isEmpty() ? responseFor (request, snapshot())
                                   : responseFor (request, snapshot(), "mixer-configure", error);
        }
        if (name == "mixer.processFilter.configure")
        {
            const auto rules = processRulesFrom (payload);
            const auto error = engine->configureProcessFilter (
                static_cast<bool> (payload.getProperty ("enabled", false)),
                static_cast<bool> (payload.getProperty ("exclusionMode", false)),
                payload.getProperty ("systemDeviceName", {}).toString(),
                payload.getProperty ("systemDeviceId", {}).toString(),
                rules);
            if (error.isEmpty()) engine->requestPersist();
            return error.isEmpty() ? responseFor (request, snapshot())
                                   : responseFor (request, snapshot(), "process-filter-configure", error);
        }
        if (name == "legacy.micvst.import")
        {
            const auto file = juce::File (payload.getProperty ("path", {}).toString());
            if (! file.existsAsFile())
                return responseFor (request, snapshot(), "legacy-import", "MicVST config.xml does not exist.");
            auto xml = juce::XmlDocument::parse (file);
            if (xml == nullptr || ! xml->hasTagName ("MicVST"))
                return responseFor (request, snapshot(), "legacy-import", "The selected file is not a MicVST configuration.");

            const auto tree = juce::ValueTree::fromXml (*xml);
            auto imported = engine->captureState();
            imported.inputDevice = tree.getProperty ("inputDevice").toString();
            imported.inputDeviceId.clear();
            imported.bufferSize = tree.getProperty ("userBufferSize", 0);
            imported.pluginFolders.clear();
            imported.pluginFolders.addLines (tree.getProperty ("pluginFolders").toString());
            imported.pluginFolders.removeEmptyStrings();
            imported.plugins.clear();
            for (const auto plugin : tree.getChildWithName ("plugins"))
            {
                PluginEntryState state;
                state.fileOrId = plugin.getProperty ("fileOrId").toString();
                state.bypassed = plugin.getProperty ("bypassed", false);
                state.state.fromBase64Encoding (plugin.getProperty ("state").toString());
                if (state.fileOrId.isNotEmpty()) imported.plugins.add (std::move (state));
            }
            engine->setPluginFolders (imported.pluginFolders);
            engine->applyState (imported);
            engine->requestPersist();
            return responseFor (request, objectWith ({
                { "snapshot", snapshot() },
                { "importedPluginCount", imported.plugins.size() },
                { "inputDeviceName", imported.inputDevice },
                { "keptOutputDeviceName", imported.outputDevice }
            }));
        }
        if (name == "host.shutdown")
        {
            juce::Timer::callAfterDelay (200, []
            {
                if (auto* application = juce::JUCEApplication::getInstance())
                    application->systemRequestedQuit();
            });
            return responseFor (request, snapshot());
        }
        if (name == "scan.start") engine->startBackgroundScan();
        else if (name == "scan.rescan") engine->rescanAllPlugins();
        else if (name == "scan.retry") engine->retrySkippedPlugins();
        else if (name == "scan.skip") engine->skipCurrentScanFile();
        else if (name == "plugin.add")
        {
            const auto path = payload.getProperty ("path", {}).toString();
            if (path == PluginChain::monoToStereoId) engine->getChain().addMonoToStereo();
            else if (path == PluginChain::stereoToMonoId) engine->getChain().addStereoToMono();
            else if (auto type = engine->getKnownPlugins().getTypeForFile (path))
            {
                const auto sampleRate = engine->getGraphSampleRate();
                juce::String error;
                if (! engine->getChain().addPlugin (engine->getFormatManager(), *type, sampleRate,
                                                   engine->getGraphBlockSize(), error))
                    return responseFor (request, snapshot(), "plugin-load", error);
            }
            else return responseFor (request, snapshot(), "plugin-not-found", "Plugin is not present in the scan cache.");
            engine->rebuildGraph();
            engine->requestPersist();
        }
        else if (name == "plugin.remove")
        {
            pluginEditors.clear (true);
            engine->getChain().removePlugin (static_cast<int> (payload.getProperty ("index", -1)));
            engine->rebuildGraph();
            engine->requestPersist();
        }
        else if (name == "plugin.openEditor")
        {
            const auto index = static_cast<int> (payload.getProperty ("index", -1));
            const auto& entries = engine->getChain().entries();
            if (! juce::isPositiveAndBelow (index, static_cast<int> (entries.size())))
                return responseFor (request, snapshot(), "plugin-index", "Plugin index is out of range.");
            auto* node = engine->getGraph().getNodeForId (entries[static_cast<std::size_t> (index)].node);
            auto* processor = node != nullptr ? node->getProcessor() : nullptr;
            if (processor == nullptr || ! processor->hasEditor())
                return responseFor (request, snapshot(), "plugin-editor", "This processor has no native editor.");
            if (auto* active = processor->getActiveEditor())
            {
                if (auto* window = active->getTopLevelComponent())
                {
                    window->setVisible (true);
                    window->toFront (true);
                }
            }
            else if (auto* editor = processor->createEditorAndMakeActive())
            {
                pluginEditors.add (new PluginEditorWindow (processor->getName(), editor));
            }
            else
            {
                return responseFor (request, snapshot(), "plugin-editor", "The plugin did not create an editor.");
            }
        }
        else if (name == "plugin.folder.add")
        {
            const auto folder = juce::File (payload.getProperty ("folder", {}).toString());
            if (! folder.isDirectory())
                return responseFor (request, snapshot(), "plugin-folder", "The selected VST3 folder does not exist.");
            engine->addPluginFolder (folder.getFullPathName());
            engine->requestPersist();
        }
        else if (name == "plugin.folder.remove")
        {
            engine->removePluginFolder (juce::File (payload.getProperty ("folder", {}).toString()).getFullPathName());
            engine->requestPersist();
        }
        else if (name == "plugin.move")
        {
            engine->getChain().movePlugin (static_cast<int> (payload.getProperty ("from", -1)),
                                           static_cast<int> (payload.getProperty ("to", -1)));
            engine->rebuildGraph();
            engine->requestPersist();
        }
        else if (name == "plugin.bypass")
        {
            engine->getChain().setBypass (static_cast<int> (payload.getProperty ("index", -1)),
                                          static_cast<bool> (payload.getProperty ("bypassed", false)));
            engine->rebuildGraph();
            engine->requestPersist();
        }
        else if (! name.startsWith ("scan."))
            return responseFor (request, {}, "unknown-command", "Unknown engine command: " + name);

        return responseFor (request, snapshot());
    }

    std::unique_ptr<BoundedLogger> logger;
    std::unique_ptr<juce::InterProcessLock> instanceLock;
    std::unique_ptr<AudioEngine> engine;
    juce::OwnedArray<PluginEditorWindow> pluginEditors;
#if JUCE_WINDOWS
    std::unique_ptr<PipeServer> pipeServer;
#endif
};
}

START_JUCE_APPLICATION (HostApplication)
