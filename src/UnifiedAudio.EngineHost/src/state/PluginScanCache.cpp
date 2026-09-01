#include "state/PluginScanCache.h"

namespace PluginScanCache
{

std::unique_ptr<juce::XmlElement> toXml (const juce::KnownPluginList& list,
                                         const juce::Array<SkippedPlugin>& skipped,
                                         const juce::Array<PluginBinaryFingerprint>& fingerprints)
{
    auto root = std::make_unique<juce::XmlElement> ("UnifiedAudioPluginCache");
    if (auto types = list.createXml())
        root->addChildElement (types.release());

    auto* skips = root->createNewChildElement ("SKIPPED");
    for (auto& s : skipped)
    {
        auto* e = skips->createNewChildElement ("PLUGIN");
        e->setAttribute ("file", s.file);
        e->setAttribute ("reason", s.reason);
        e->setAttribute ("fileTimeMs", juce::String (s.fileTimeMs));
    }
    auto* hashes = root->createNewChildElement ("FINGERPRINTS");
    for (const auto& fingerprint : fingerprints)
    {
        auto* e = hashes->createNewChildElement ("PLUGIN");
        e->setAttribute ("file", fingerprint.file);
        e->setAttribute ("sha256", fingerprint.sha256);
        e->setAttribute ("totalBytes", juce::String (fingerprint.totalBytes));
    }
    return root;
}

bool fromXml (const juce::XmlElement& xml, juce::KnownPluginList& list,
              juce::Array<SkippedPlugin>& skipped,
              juce::Array<PluginBinaryFingerprint>* fingerprints)
{
    if (! xml.hasTagName ("UnifiedAudioPluginCache")) return false;

    if (auto* types = xml.getChildByName ("KNOWNPLUGINS"))
        list.recreateFromXml (*types);

    if (auto* skips = xml.getChildByName ("SKIPPED"))
        for (auto* e : skips->getChildWithTagNameIterator ("PLUGIN"))
            skipped.add ({ e->getStringAttribute ("file"),
                           e->getStringAttribute ("reason"),
                           e->getStringAttribute ("fileTimeMs").getLargeIntValue() });
    if (fingerprints != nullptr)
    {
        fingerprints->clear();
        if (auto* hashes = xml.getChildByName ("FINGERPRINTS"))
            for (auto* e : hashes->getChildWithTagNameIterator ("PLUGIN"))
                fingerprints->add ({ e->getStringAttribute ("file"),
                                     e->getStringAttribute ("sha256"),
                                     e->getStringAttribute ("totalBytes").getLargeIntValue() });
    }
    return true;
}

bool save (const juce::File& file, const juce::KnownPluginList& list,
           const juce::Array<SkippedPlugin>& skipped,
           const juce::Array<PluginBinaryFingerprint>& fingerprints)
{
    file.getParentDirectory().createDirectory();
    juce::TemporaryFile temporary (file);
    if (! toXml (list, skipped, fingerprints)->writeTo (temporary.getFile()))
        return false;
    if (file.existsAsFile())
    {
        auto backup = file.getSiblingFile (file.getFileName() + ".bak");
        if (! file.copyFileTo (backup))
            return false;
    }
    return temporary.overwriteTargetFileWithTemporary();
}

bool load (const juce::File& file, juce::KnownPluginList& list,
           juce::Array<SkippedPlugin>& skipped,
           juce::Array<PluginBinaryFingerprint>* fingerprints)
{
    const auto tryLoad = [&list, &skipped, fingerprints] (const juce::File& candidate)
    {
        if (! candidate.existsAsFile()) return false;
        auto xml = juce::parseXML (candidate);
        return xml != nullptr && fromXml (*xml, list, skipped, fingerprints);
    };
    if (tryLoad (file)) return true;

    auto backup = file.getSiblingFile (file.getFileName() + ".bak");
    if (tryLoad (backup))
    {
        backup.copyFileTo (file);
        return true;
    }
    return false;
}

} // namespace PluginScanCache
