#pragma once
#include <juce_audio_processors/juce_audio_processors.h>

// Ein beim Scan übersprungenes Plugin. fileTimeMs = Änderungszeit der Datei beim Skip;
// ändert sich die Datei (Update installiert), wird der Skip-Eintrag verworfen und neu probiert.
struct SkippedPlugin
{
    juce::String file;      // voller Pfad der .vst3
    juce::String reason;    // "unresponsive" | "skipped" | "failed" | "failed (exit 0x...)"
                             // | "crashed (N plugin(s) rescued[, 0x...])"
    juce::int64  fileTimeMs = 0;
};

struct PluginBinaryFingerprint
{
    juce::String file;
    juce::String sha256;
    juce::int64 totalBytes = 0;
};

// Persistenter Scan-Cache: KnownPluginList + Skip-Liste als atomar ersetztes XML
// plus .bak (%APPDATA%\UnifiedAudio\plugin_cache.xml). Reine Serialisierung, kein Scan-Wissen.
namespace PluginScanCache
{
    std::unique_ptr<juce::XmlElement> toXml (const juce::KnownPluginList& list,
                                             const juce::Array<SkippedPlugin>& skipped,
                                             const juce::Array<PluginBinaryFingerprint>& fingerprints = {});
    bool fromXml (const juce::XmlElement& xml, juce::KnownPluginList& list,
                  juce::Array<SkippedPlugin>& skipped,
                  juce::Array<PluginBinaryFingerprint>* fingerprints = nullptr);
    bool save (const juce::File& file, const juce::KnownPluginList& list,
               const juce::Array<SkippedPlugin>& skipped,
               const juce::Array<PluginBinaryFingerprint>& fingerprints = {});
    bool load (const juce::File& file, juce::KnownPluginList& list,
               juce::Array<SkippedPlugin>& skipped,
               juce::Array<PluginBinaryFingerprint>* fingerprints = nullptr);
}
