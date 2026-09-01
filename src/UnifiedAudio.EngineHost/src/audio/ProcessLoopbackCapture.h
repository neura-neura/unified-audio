#pragma once

#include <juce_core/juce_core.h>
#include "audio/AsyncAudioBridge.h"

class ProcessLoopbackCapture final : private juce::Thread
{
public:
    ProcessLoopbackCapture();
    ~ProcessLoopbackCapture() override;

    juce::String start (unsigned long processId, double destinationRate);
    void stop();
    bool isCapturing() const noexcept { return capturing.load (std::memory_order_acquire); }
    unsigned long processId() const noexcept { return requestedProcessId.load(); }
    void pop (float* left, float* right, int frames) noexcept { bridge.pop (left, right, frames); }

private:
    void run() override;
    juce::String runWasapi();

    AsyncAudioBridge bridge;
    std::atomic<unsigned long> requestedProcessId { 0 };
    double requestedDestinationRate = 48000.0;
    juce::String startupError;
    juce::WaitableEvent startupComplete;
    std::atomic<bool> capturing { false };
};
