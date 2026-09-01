#pragma once

#include <juce_core/juce_core.h>

struct AudioSessionInfo
{
    unsigned long processId = 0;
    juce::String sessionId;
    juce::String displayName;
    juce::String processName;
    juce::String executablePath;
    float peak = 0.0f;
    float sessionVolume = 1.0f;
    bool sessionMuted = false;
    bool active = false;
};

struct AudioEndpointIdentity
{
    juce::String id;
    juce::String name;
};

// Enumerates render sessions on a selected endpoint. This runs only on the
// engine message thread; it is never called from a real-time audio callback.
juce::Array<AudioSessionInfo> listRenderAudioSessions (const juce::String& deviceName,
                                                       const juce::String& deviceId,
                                                       juce::String& error);
juce::Array<AudioEndpointIdentity> listAudioEndpointIdentities (bool capture);
juce::String resolveAudioEndpointName (const juce::String& id, bool capture);
