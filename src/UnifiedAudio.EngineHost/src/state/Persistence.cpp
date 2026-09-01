#include "state/Persistence.h"

namespace ids
{
    const juce::Identifier root ("UnifiedAudio"), inDev ("inputDevice"), outDev ("outputDevice"),
        inDevId ("inputDeviceId"), outDevId ("outputDeviceId"),
        sr ("sampleRate"), userBuf ("userBufferSize"), folders ("pluginFolders"), window ("windowState"),
        mixMode ("mixerMode"), systemDevice ("systemCaptureDevice"), systemDeviceId ("systemCaptureDeviceId"), voiceGain ("voiceGain"),
        systemGain ("systemGain"), duckEnabled ("duckingEnabled"), duckAmount ("duckAmount"),
        duckThreshold ("duckThresholdDb"), duckAttack ("duckAttackMs"), duckHold ("duckHoldMs"),
        duckRelease ("duckReleaseMs"), processFilter ("processFilterEnabled"),
        processFilterExclusion ("processFilterExclusionMode"),
        updEnabled ("updateCheckEnabled"), updAsked ("updateCheckAsked"), updLast ("lastNotifiedVersion"),
        plugins ("plugins"), plugin ("plugin"), fileId ("fileOrId"), byp ("bypassed"), blob ("state"),
        processRules ("processMixRules"), processRule ("processMixRule"), executable ("executablePath"),
        display ("displayName"), gain ("gain"), excluded ("excluded");
}

juce::ValueTree toValueTree (const UnifiedAudioState& s)
{
    juce::ValueTree t (ids::root);
    t.setProperty (ids::inDev, s.inputDevice, nullptr);
    t.setProperty (ids::outDev, s.outputDevice, nullptr);
    t.setProperty (ids::inDevId, s.inputDeviceId, nullptr);
    t.setProperty (ids::outDevId, s.outputDeviceId, nullptr);
    t.setProperty (ids::sr, s.sampleRate, nullptr);
    t.setProperty (ids::userBuf, s.bufferSize, nullptr);
    t.setProperty (ids::folders, s.pluginFolders.joinIntoString ("\n"), nullptr);
    t.setProperty (ids::window, s.windowState, nullptr);
    t.setProperty (ids::mixMode, s.mixerMode, nullptr);
    t.setProperty (ids::systemDevice, s.systemCaptureDevice, nullptr);
    t.setProperty (ids::systemDeviceId, s.systemCaptureDeviceId, nullptr);
    t.setProperty (ids::voiceGain, s.voiceGain, nullptr);
    t.setProperty (ids::systemGain, s.systemGain, nullptr);
    t.setProperty (ids::duckEnabled, s.duckingEnabled, nullptr);
    t.setProperty (ids::duckAmount, s.duckAmount, nullptr);
    t.setProperty (ids::duckThreshold, s.duckThresholdDb, nullptr);
    t.setProperty (ids::duckAttack, s.duckAttackMs, nullptr);
    t.setProperty (ids::duckHold, s.duckHoldMs, nullptr);
    t.setProperty (ids::duckRelease, s.duckReleaseMs, nullptr);
    t.setProperty (ids::processFilter, s.processFilterEnabled, nullptr);
    t.setProperty (ids::processFilterExclusion, s.processFilterExclusionMode, nullptr);
    t.setProperty (ids::updEnabled, s.updateCheckEnabled, nullptr);
    t.setProperty (ids::updAsked, s.updateCheckAsked, nullptr);
    t.setProperty (ids::updLast, s.lastNotifiedVersion, nullptr);

    juce::ValueTree list (ids::plugins);
    for (auto& p : s.plugins)
    {
        juce::ValueTree pt (ids::plugin);
        pt.setProperty (ids::fileId, p.fileOrId, nullptr);
        pt.setProperty (ids::byp, p.bypassed, nullptr);
        pt.setProperty (ids::blob, p.state.toBase64Encoding(), nullptr);
        list.appendChild (pt, nullptr);
    }
    t.appendChild (list, nullptr);
    juce::ValueTree rules (ids::processRules);
    for (const auto& rule : s.processMixRules)
    {
        juce::ValueTree item (ids::processRule);
        item.setProperty (ids::executable, rule.executablePath, nullptr);
        item.setProperty (ids::display, rule.displayName, nullptr);
        item.setProperty (ids::gain, rule.gain, nullptr);
        item.setProperty (ids::excluded, rule.excluded, nullptr);
        rules.appendChild (item, nullptr);
    }
    t.appendChild (rules, nullptr);
    return t;
}

UnifiedAudioState fromValueTree (const juce::ValueTree& t)
{
    UnifiedAudioState s;
    s.inputDevice  = t.getProperty (ids::inDev);
    s.outputDevice = t.getProperty (ids::outDev);
    s.inputDeviceId = t.getProperty (ids::inDevId).toString();
    s.outputDeviceId = t.getProperty (ids::outDevId).toString();
    s.sampleRate   = t.getProperty (ids::sr, 48000.0);
    // Migration v1.0.x: der alte Key "bufferSize" (immer 128) wird bewusst ignoriert --
    // im Shared-Modus war er nie wirksam. Bestandsnutzer starten mit Auto.
    s.bufferSize   = t.getProperty (ids::userBuf, 0);
    {
        const auto f = t.getProperty (ids::folders).toString();
        if (f.isNotEmpty()) { s.pluginFolders.addLines (f); s.pluginFolders.removeEmptyStrings(); }
    }
    s.windowState = t.getProperty (ids::window).toString();
    s.mixerMode = t.getProperty (ids::mixMode, 0);
    s.systemCaptureDevice = t.getProperty (ids::systemDevice).toString();
    s.systemCaptureDeviceId = t.getProperty (ids::systemDeviceId).toString();
    s.voiceGain = t.getProperty (ids::voiceGain, 1.0f);
    s.systemGain = t.getProperty (ids::systemGain, 1.0f);
    s.duckingEnabled = t.getProperty (ids::duckEnabled, false);
    s.duckAmount = t.getProperty (ids::duckAmount, 0.55f);
    s.duckThresholdDb = t.getProperty (ids::duckThreshold, -36.0f);
    s.duckAttackMs = t.getProperty (ids::duckAttack, 20);
    s.duckHoldMs = t.getProperty (ids::duckHold, 200);
    s.duckReleaseMs = t.getProperty (ids::duckRelease, 280);
    s.processFilterEnabled = t.getProperty (ids::processFilter, false);
    s.processFilterExclusionMode = t.getProperty (ids::processFilterExclusion, false);
    s.updateCheckEnabled  = t.getProperty (ids::updEnabled, false);
    s.updateCheckAsked    = t.getProperty (ids::updAsked, false);
    s.lastNotifiedVersion = t.getProperty (ids::updLast).toString();

    auto list = t.getChildWithName (ids::plugins);
    for (auto pt : list)
    {
        PluginEntryState p;
        p.fileOrId = pt.getProperty (ids::fileId);
        p.bypassed = pt.getProperty (ids::byp, false);
        p.state.fromBase64Encoding (pt.getProperty (ids::blob).toString());
        s.plugins.add (p);
    }
    for (auto item : t.getChildWithName (ids::processRules))
    {
        ProcessMixRuleState rule;
        rule.executablePath = item.getProperty (ids::executable).toString();
        rule.displayName = item.getProperty (ids::display).toString();
        rule.gain = juce::jlimit (0.0f, 4.0f, static_cast<float> (item.getProperty (ids::gain, 1.0f)));
        rule.excluded = item.getProperty (ids::excluded, false);
        if (rule.executablePath.isNotEmpty()) s.processMixRules.add (std::move (rule));
    }
    return s;
}

juce::File configFile()
{
    return juce::File::getSpecialLocation (juce::File::userApplicationDataDirectory)
              .getChildFile ("UnifiedAudio").getChildFile ("config.xml");
}

bool saveState (const UnifiedAudioState& s)
{
    auto f = configFile();
    f.getParentDirectory().createDirectory();
    const auto backup = f.getSiblingFile (f.getFileNameWithoutExtension() + ".bak.xml");
    juce::TemporaryFile temporary (f);
    if (auto xml = toValueTree (s).createXml())
    {
        if (! xml->writeTo (temporary.getFile())) return false;
        if (f.existsAsFile()) f.copyFileTo (backup);
        return temporary.overwriteTargetFileWithTemporary();
    }
    return false;
}

UnifiedAudioState loadState()
{
    auto f = configFile();
    if (! f.existsAsFile()) return {};
    if (auto xml = juce::XmlDocument::parse (f))
        return fromValueTree (juce::ValueTree::fromXml (*xml));
    const auto backup = f.getSiblingFile (f.getFileNameWithoutExtension() + ".bak.xml");
    if (auto xml = juce::XmlDocument::parse (backup))
        return fromValueTree (juce::ValueTree::fromXml (*xml));
    return {};
}
