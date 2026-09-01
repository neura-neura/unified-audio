#pragma once
#include <juce_data_structures/juce_data_structures.h>
#include <juce_core/juce_core.h>

struct PluginEntryState
{
    juce::String fileOrId;
    bool bypassed = false;
    juce::MemoryBlock state;   // getStateInformation()-Blob
};

struct ProcessMixRuleState
{
    juce::String executablePath;
    juce::String displayName;
    float gain = 1.0f;
    bool excluded = false;
};

struct UnifiedAudioState
{
    juce::String inputDevice, outputDevice;
    juce::String inputDeviceId, outputDeviceId;
    double sampleRate = 48000.0;
    int    bufferSize = 0;   // Buffer-Wunsch in Samples; 0 = Auto (Geräte-Default)
    juce::Array<PluginEntryState> plugins;
    juce::StringArray pluginFolders;   // zusätzliche VST3-Suchordner
    juce::String windowState;          // DocumentWindow::getWindowStateAsString() (Größe/Position)
    int mixerMode = 0;
    juce::String systemCaptureDevice;
    juce::String systemCaptureDeviceId;
    float voiceGain = 1.0f;
    float systemGain = 1.0f;
    bool duckingEnabled = false;
    float duckAmount = 0.55f;
    float duckThresholdDb = -36.0f;
    int duckAttackMs = 20;
    int duckHoldMs = 200;
    int duckReleaseMs = 280;
    bool processFilterEnabled = false;
    bool processFilterExclusionMode = false;
    juce::Array<ProcessMixRuleState> processMixRules;

    // Opt-in Auto-Update-Check (siehe UpdateChecker). Default: aus, nie gefragt.
    bool updateCheckEnabled = false;   // Checkbox-Zustand
    bool updateCheckAsked   = false;   // Erststart-Popup schon gezeigt?
    juce::String lastNotifiedVersion;  // letzte per Tray-Bubble gemeldete Version (Dedup)
};

juce::ValueTree  toValueTree (const UnifiedAudioState&);
UnifiedAudioState    fromValueTree (const juce::ValueTree&);

// Datei unter %APPDATA%\UnifiedAudio\config.xml
juce::File    configFile();
bool          saveState (const UnifiedAudioState&);
UnifiedAudioState loadState();
