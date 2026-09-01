#pragma once

#include <juce_core/juce_core.h>
#include "audio/AsyncAudioBridge.h"

class SystemLoopbackCapture final : private juce::Thread
{
public:
    explicit SystemLoopbackCapture (AsyncAudioBridge& destination);
    ~SystemLoopbackCapture() override;

    juce::String start (const juce::String& renderDeviceName,
                        const juce::String& renderDeviceId,
                        double destinationRate);
    void stop();
    bool isCapturing() const noexcept { return capturing.load (std::memory_order_acquire); }
    juce::String selectedDeviceName() const { return requestedDeviceName; }
    juce::String selectedDeviceId() const { return requestedDeviceId; }

private:
    void run() override;
    juce::String runWasapi();

    AsyncAudioBridge& bridge;
    juce::String requestedDeviceName;
    juce::String requestedDeviceId;
    juce::String startupError;
    double requestedDestinationRate = 48000.0;
    juce::WaitableEvent startupComplete;
    std::atomic<bool> capturing { false };
};
