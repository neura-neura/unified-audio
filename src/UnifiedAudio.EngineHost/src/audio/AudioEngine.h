#pragma once
#include <juce_audio_devices/juce_audio_devices.h>
#include <juce_audio_processors/juce_audio_processors.h>
#include <juce_audio_utils/juce_audio_utils.h>   // AudioProcessorPlayer lebt in juce_audio_utils
#include "audio/Metering.h"
#include "audio/AsyncAudioBridge.h"
#include "audio/SystemLoopbackCapture.h"
#include "audio/ProcessLoopbackCapture.h"
#include "audio/AudioSessionCatalog.h"
#include "audio/SpectrumAnalyzer.h"
#include "audio/PluginChain.h"
#include "audio/UnifiedAudioDeviceManager.h"
#include "audio/ScanCoordinator.h"
#include "audio/FeedbackLoopPolicy.h"
#include "state/Persistence.h"

// Besitzt AudioDeviceManager + AudioProcessorGraph. Der Graph läuft über einen
// internen AudioProcessorPlayer; AudioEngine bleibt der Device-Callback und
// metert Input/Output rund um den Player herum.
class AudioEngine : private juce::ChangeListener
{
public:
    enum class MixerMode { voice = 0, system = 1, both = 2 };
    AudioEngine();
    ~AudioEngine() override;

    juce::String initialise (const juce::String& inputDeviceName,
                             const juce::String& outputDeviceName);
    void setStableDeviceIds (const juce::String& inputId, const juce::String& outputId)
    {
        stableInputDeviceId = inputId;
        stableOutputDeviceId = outputId;
    }
    juce::String getInputDeviceId() const { return stableInputDeviceId; }
    juce::String getOutputDeviceId() const { return stableOutputDeviceId; }
    void stop();

    // The authoritative mute gate lives after the VST graph and before any mixer source.
    // The callback reads an atomic target and owns the de-click ramp state.
    void setMuted (bool shouldMute) { muteTarget.store (shouldMute, std::memory_order_release); }
    void toggleMuted() { setMuted (! isMutedRequested()); }
    bool isMutedRequested() const { return muteTarget.load (std::memory_order_acquire); }
    bool isMuteSettled() const { return muteSettled.load (std::memory_order_acquire); }

    juce::String configureMixer (MixerMode mode,
                                 const juce::String& systemDeviceName,
                                 const juce::String& systemDeviceId,
                                 float voiceGain,
                                 float systemGain,
                                 bool duckingEnabled,
                                 float duckAmount,
                                 float voiceThresholdDb,
                                 int attackMs,
                                 int holdMs,
                                 int releaseMs);
    juce::String configureProcessFilter (bool enabled,
                                         bool exclusionMode,
                                         const juce::String& systemDeviceName,
                                         const juce::String& systemDeviceId,
                                         const juce::Array<ProcessMixRuleState>& rules);
    void refreshProcessFilter();
    bool isProcessFilterEnabled() const { return processFilterEnabled.load(); }
    bool isProcessFilterExclusionMode() const { return processFilterExclusionMode.load(); }
    int getActiveProcessCaptureCount() const;
    const juce::Array<ProcessMixRuleState>& getProcessMixRules() const { return processMixRules; }
    MixerMode getMixerMode() const { return static_cast<MixerMode> (mixerMode.load()); }
    float getVoiceGain() const { return voiceGain.load(); }
    float getSystemGain() const { return systemGain.load(); }
    float getDuckingGain() const { return duckEnvelope.load(); }
    bool getDuckingEnabled() const { return duckingEnabled.load(); }
    float getDuckAmount() const { return duckAmount.load(); }
    float getDuckThresholdDb() const { return duckThresholdDb.load(); }
    int getDuckAttackMs() const { return duckAttackMs.load(); }
    int getDuckHoldMs() const { return duckHoldMs.load(); }
    int getDuckReleaseMs() const { return duckReleaseMs.load(); }
    bool isSystemCaptureRunning() const
    {
        return isProcessFilterEnabled() ? getActiveProcessCaptureCount() > 0
                                        : systemCapture.isCapturing();
    }
    juce::String getSystemCaptureDeviceName() const
    {
        return isProcessFilterEnabled() ? processFilterDeviceName
                                        : systemCapture.selectedDeviceName();
    }
    juce::String getSystemCaptureDeviceId() const
    {
        return isProcessFilterEnabled() ? processFilterDeviceId
                                        : systemCapture.selectedDeviceId();
    }

    // Windows Listen-to-device is an external route, so it can be enabled
    // after the graph has started.  This probe runs on the engine message or
    // timer thread and never from an audio callback.  A detected route is
    // auto-protected by stopping system capture and falling back to Voice.
    bool refreshFeedbackLoopGuard();
    bool isFeedbackLoopDetected() const { return feedbackLoopDetected.load (std::memory_order_acquire); }
    bool isFeedbackLoopGuarded() const { return feedbackLoopGuarded.load (std::memory_order_acquire); }
    bool isListenRouteActive() const { return listenRouteActive.load (std::memory_order_acquire); }
    bool isFeedbackProbeAvailable() const { return feedbackProbeAvailable.load (std::memory_order_acquire); }
    bool isFeedbackProbeUnknown() const { return feedbackProbeUnknown.load (std::memory_order_acquire); }
    bool areFinalCaptureConsumersDetected() const { return finalCaptureConsumerDetected.load (std::memory_order_acquire); }
    bool isFinalCaptureConsumerProbeAvailable() const { return finalCaptureConsumerProbeAvailable.load (std::memory_order_acquire); }
    juce::String getFinalCaptureEndpointId() const { return finalCaptureEndpointId; }
    juce::String getFinalCaptureEndpointName() const { return finalCaptureEndpointName; }
    juce::String getFinalCaptureConsumerSummary() const { return finalCaptureConsumerSummary; }
    juce::String getFeedbackLoopDescription() const { return feedbackLoopDescription; }

    // Laufzeit-Geräteumschaltung (vom DevicePanel): setzt Geräte/Samplerate/Buffer neu,
    // OHNE Graph + Plugin-Kette neu aufzubauen. sampleRate<=0 / bufferSize<=0 = unverändert.
    void setDeviceConfig (const juce::String& input, const juce::String& output,
                          double sampleRate, int bufferSize);

    // Buffer-Wunsch des Users in Samples; 0 = Auto (Geräte-Default-Periode).
    // Wird von applyState gesetzt und in captureState persistiert.
    void setPreferredBufferSize (int samples) { preferredBufferSize = samples; }
    int  getPreferredBufferSize() const       { return preferredBufferSize; }

    // Sucht ein installiertes virtuelles Audio-Kabel als Output (VB-Cable, VoiceMeeter, VAC),
    // die Render->Capture selbst spiegeln. Leerer String = kein Kabel gefunden.
    juce::String detectCableOutput();

    UnifiedAudioState captureState();            // liest Devices + Plugin-Kette + Blobs
    void          applyState (const UnifiedAudioState&);   // lädt Devices + Plugins + setStateInformation
    juce::String  applyPluginChainState (const juce::Array<PluginEntryState>& plugins);

    bool isRunning() const;                 // true wenn ein Audio-Device offen ist und spielt
    std::function<void()> onStatusChanged;  // wird bei Device-Änderungen aufgerufen (UI-Status)
    std::function<void()> onDeviceChanged;  // wird bei Geräte-Änderungen aufgerufen (zum Persistieren)

    // Von der UI bei Ketten-/Ordner-Änderungen gerufen. Die App persistiert dann den
    // GESAMTEN Zustand (inkl. windowState + Update-Check-Feldern, die captureState nicht
    // kennt) — ein direktes saveState(captureState()) würde diese Felder zurücksetzen.
    std::function<void()> onStateChanged;
    void requestPersist() { if (onStateChanged) onStateChanged(); }

    // Factory-Reset (UI-Menü): Die App löscht config + Plugin-Cache und beendet sich
    // für einen frischen Erststart. Engine-seitig nur der Durchreich-Callback.
    std::function<void()> onFactoryResetRequested;
    void requestFactoryReset() { if (onFactoryResetRequested) onFactoryResetRequested(); }

    UnifiedAudioDeviceManager&      getDeviceManager() { return inventoryManager; }
    double                          getGraphSampleRate() const { return graphSampleRate.load(); }
    double                          getCaptureSampleRate() const { return captureSampleRate.load(); }
    std::uint64_t                   getUnderrunCount() const { return audioBridge.underruns(); }
    std::uint64_t                   getOverrunCount() const { return audioBridge.overruns(); }
    std::uint64_t                   getQueuedCaptureFrames() const { return audioBridge.queuedFrames(); }
    juce::String                    getInputDeviceName() const { return inputManager.getAudioDeviceSetup().inputDeviceName; }
    juce::String                    getOutputDeviceName() const { return outputManager.getAudioDeviceSetup().outputDeviceName; }
    juce::AudioProcessorGraph&      getGraph()         { return graph; }
    PluginChain&                    getChain()         { return *pluginChain; }
    juce::AudioPluginFormatManager& getFormatManager() { return formatManager; }
    juce::KnownPluginList&          getKnownPlugins()  { return knownPlugins; }

    // --- Plugin-Scan (out-of-process, asynchron, gecacht) ---
    void loadPluginCache();                    // beim Start VOR applyState aufrufen
    // forceRescan: siehe filterFilesNeedingScan -- Dateien, die trotz aktuell aussehendem
    // Cache erneut versucht werden sollen (retrySkippedPlugins nach Crash-Rescue).
    void startBackgroundScan (int timeoutMs = ScanCoordinator::defaultTimeoutMs,
                              const juce::StringArray& forceRescan = {});
    void rescanAllPlugins();                   // Cache + Skip-Liste leeren, alles neu
    void retrySkippedPlugins();                // nur Skip-Liste leeren, mit großem Timeout scannen
    void skipCurrentScanFile();                // Skip-Button: aktuelle Datei überspringen
    bool isScanning() const { return scanner != nullptr || fingerprintAuditor != nullptr; }
    const juce::Array<SkippedPlugin>& getSkippedPlugins() const { return skippedPlugins; }
    std::function<void (int, int, juce::String)> onScanProgress;   // current(1-based), total, name
    std::function<void()> onScanFinished;      // nach Cache-Save + ggf. Ketten-Restore

    void addPluginFolder (const juce::String& folder);
    void removePluginFolder (const juce::String& folder);
    void setPluginFolders (const juce::StringArray& f) { pluginFolders = f; }
    const juce::StringArray& getPluginFolders() const  { return pluginFolders; }

    static juce::File pluginCacheFile();

    LevelReading inputLevel()  const { return inputMeter.read(); }
    LevelReading voiceLevel()  const { return voiceMeter.read(); }
    LevelReading outputLevel() const { return outputMeter.read(); }
    LevelReading systemLevel() const { return systemMeter.read(); }
    std::array<float, SpectrumAnalyzer::bandCount> outputSpectrum() const { return spectrumAnalyzer.read(); }

    void rebuildGraph();   // Graph-Verbindungen neu aufbauen (inkl. Mono->Stereo-Fanout)

private:
    class CaptureCallback final : public juce::AudioIODeviceCallback
    {
    public:
        explicit CaptureCallback (AudioEngine& ownerToUse) : owner (ownerToUse) {}
        void audioDeviceIOCallbackWithContext (const float* const*, int, float* const*, int, int,
                                               const juce::AudioIODeviceCallbackContext&) override;
        void audioDeviceAboutToStart (juce::AudioIODevice*) override;
        void audioDeviceStopped() override;
    private:
        AudioEngine& owner;
    };

    class RenderCallback final : public juce::AudioIODeviceCallback
    {
    public:
        explicit RenderCallback (AudioEngine& ownerToUse) : owner (ownerToUse) {}
        void audioDeviceIOCallbackWithContext (const float* const*, int, float* const*, int, int,
                                               const juce::AudioIODeviceCallbackContext&) override;
        void audioDeviceAboutToStart (juce::AudioIODevice*) override;
        void audioDeviceStopped() override;
    private:
        AudioEngine& owner;
    };

    // Playhead, der dem Graph (und damit allen Plugins) durchgehend "Transport läuft"
    // meldet. Nötig, weil der Default-Playhead des AudioProcessorPlayer isPlaying NICHT
    // setzt — manche Routing/Streaming-Plugins senden aber nur bei laufendem Transport.
    struct PlayingHead : juce::AudioPlayHead
    {
        std::atomic<juce::int64> samples { 0 };
        std::atomic<double> sampleRate { 48000.0 };
        juce::Optional<PositionInfo> getPosition() const override
        {
            const auto s  = samples.load (std::memory_order_relaxed);
            const auto sr = sampleRate.load (std::memory_order_relaxed);
            PositionInfo info;
            info.setIsPlaying (true);
            info.setIsRecording (false);
            info.setIsLooping (false);
            info.setTimeInSamples (s);
            info.setTimeInSeconds ((double) s / sr);
            info.setBpm (120.0);
            info.setTimeSignature (juce::AudioPlayHead::TimeSignature{});
            info.setPpqPosition (((double) s / sr) * (120.0 / 60.0));
            return info;
        }
    };
    PlayingHead playHead;

    void captureAudio (const float* const* inputChannelData, int numInputChannels, int numSamples) noexcept;
    void renderAudio (float* const* outputChannelData, int numOutputChannels, int numSamples) noexcept;
    void captureAboutToStart (juce::AudioIODevice*);
    void renderAboutToStart (juce::AudioIODevice*);
    void captureStopped();
    void renderStopped();
    void changeListenerCallback (juce::ChangeBroadcaster*) override;
    void stopProcessCaptures();
    juce::String reconcileProcessCaptures();
    FeedbackLoopEvidence probeFeedbackLoop (const juce::String& systemDeviceName,
                                             const juce::String& systemDeviceId) const;
    bool finalCaptureConsumersExcluded (const FeedbackLoopEvidence&) const;
    bool applyFeedbackLoopStatus (const FeedbackLoopEvidence&, bool autoProtect);

    UnifiedAudioDeviceManager inputManager;
    UnifiedAudioDeviceManager outputManager;
    UnifiedAudioDeviceManager inventoryManager;
    CaptureCallback captureCallback { *this };
    RenderCallback renderCallback { *this };
    AsyncAudioBridge audioBridge;
    AsyncAudioBridge systemBridge;
    SystemLoopbackCapture systemCapture { systemBridge };
    juce::AudioProcessorGraph graph;
    juce::AudioBuffer<float> processBuffer { 2, 8192 };
    juce::AudioBuffer<float> systemBuffer { 2, 8192 };
    static constexpr std::size_t maxProcessCaptures = 32;
    std::array<std::unique_ptr<ProcessLoopbackCapture>, maxProcessCaptures> processCaptures;
    std::array<juce::AudioBuffer<float>, maxProcessCaptures> processMixBuffers;
    std::array<std::atomic<unsigned long>, maxProcessCaptures> processCapturePids {};
    std::array<std::atomic<float>, maxProcessCaptures> processCaptureGains {};
    std::array<std::atomic<bool>, maxProcessCaptures> processCaptureIncluded {};
    std::array<juce::String, maxProcessCaptures> processCaptureKeys;
    std::atomic<bool> processFilterEnabled { false };
    std::atomic<bool> processFilterExclusionMode { false };
    juce::String processFilterDeviceName;
    juce::String processFilterDeviceId;
    juce::Array<ProcessMixRuleState> processMixRules;
    juce::MidiBuffer processMidi;
    std::atomic<double> captureSampleRate { 48000.0 };
    std::atomic<double> graphSampleRate { 48000.0 };
    std::atomic<int> graphBlockSize { 512 };
    std::unique_ptr<PluginChain> pluginChain;
    juce::AudioPluginFormatManager formatManager;
    juce::KnownPluginList knownPlugins;
    juce::StringArray pluginFolders;   // zusätzliche VST3-Suchordner (persistiert)
    int preferredBufferSize = 0;   // Buffer-Wunsch des Users in Samples; 0 = Auto
    juce::String stableInputDeviceId, stableOutputDeviceId;
    LevelMeter inputMeter, voiceMeter, systemMeter, outputMeter;
    SpectrumAnalyzer spectrumAnalyzer;
    std::atomic<bool> muteTarget { false };
    std::atomic<bool> muteSettled { false };
    float muteGain = 1.0f; // audio-callback thread only
    std::atomic<int> mixerMode { static_cast<int> (MixerMode::voice) };
    std::atomic<float> voiceGain { 1.0f };
    std::atomic<float> systemGain { 1.0f };
    std::atomic<bool> duckingEnabled { false };
    std::atomic<float> duckAmount { 0.55f };
    std::atomic<float> duckThresholdDb { -36.0f };
    std::atomic<int> duckAttackMs { 20 };
    std::atomic<int> duckHoldMs { 200 };
    std::atomic<int> duckReleaseMs { 280 };
    std::atomic<float> duckEnvelope { 1.0f };
    std::atomic<bool> feedbackLoopDetected { false };
    std::atomic<bool> feedbackLoopGuarded { false };
    std::atomic<bool> listenRouteActive { false };
    std::atomic<bool> feedbackProbeAvailable { false };
    std::atomic<bool> feedbackProbeUnknown { true };
    std::atomic<bool> finalCaptureConsumerProbeAvailable { false };
    std::atomic<bool> finalCaptureConsumerDetected { false };
    juce::String finalCaptureEndpointId;
    juce::String finalCaptureEndpointName;
    juce::String finalCaptureConsumerSummary;
    juce::String feedbackLoopDescription;
    int duckHoldFramesRemaining = 0; // render thread only

    std::unique_ptr<ScanCoordinator> scanner;      // != nullptr solange ein Scan läuft
    std::unique_ptr<PluginFingerprintCoordinator> fingerprintAuditor;
    juce::Array<PluginBinaryFingerprint> pluginFingerprints;
    bool rescanQueued = false;   // merkt einen während des Scans angeforderten Folgescan vor
    juce::Array<SkippedPlugin> skippedPlugins;     // persistiert im Cache
    juce::Array<PluginEntryState> pendingPlugins;  // Ketten-Restore wartet auf Scan-Ende
    juce::StringArray scanRoots() const;           // JUCE-Default-VST3-Orte + Custom-Ordner
    juce::StringArray listVst3Files() const;       // Standard- + Custom-Ordner enumerieren
    juce::String restoreChain (const juce::Array<PluginEntryState>& plugins);
    void handleScanFinished (const ScanOutcome&);
    void handleFingerprintAudit (const PluginFingerprintAuditOutcome&,
                                 const juce::StringArray& allFiles,
                                 const juce::StringArray& forceRescan,
                                 int timeoutMs);
    void pruneOutsideFolders();                    // Cache-Einträge entfernter Ordner löschen
};
