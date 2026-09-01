#pragma once

#include "audio/FeedbackLoopPolicy.h"

// Reads the user-owned Windows Listen properties and active MMDevice
// identities.  It is deliberately a read-only operation and is called only
// from the engine message/timer thread.
FeedbackLoopEvidence detectWindowsFeedbackLoop (const juce::String& finalRenderName,
                                                const juce::String& finalRenderId,
                                                const juce::String& systemRenderName,
                                                const juce::String& systemRenderId);
