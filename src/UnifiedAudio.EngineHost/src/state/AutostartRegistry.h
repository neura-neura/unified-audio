#pragma once
#include <juce_core/juce_core.h>

// Verwaltet HKCU\...\Run\UnifiedAudio = "<exe>" --tray
namespace AutostartRegistry
{
    bool isEnabled();
    void setEnabled (bool shouldRun);
}
