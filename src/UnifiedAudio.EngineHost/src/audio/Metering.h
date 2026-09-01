#pragma once
#include <juce_audio_basics/juce_audio_basics.h>
#include <atomic>

struct LevelReading { float rms = 0.0f; float peak = 0.0f; };

// Reine Funktion: RMS/Peak über alle Kanäle eines Buffers. Testbar ohne Hardware.
LevelReading computeLevel (const juce::AudioBuffer<float>& buffer);
LevelReading computeLevel (const float* const* channels, int numChannels, int numSamples);

// Thread-sicherer Meter: process() im Audio-Thread, read() im GUI-Thread.
class LevelMeter
{
public:
    void process (const juce::AudioBuffer<float>& buffer)
    {
        const auto r = computeLevel (buffer);
        store (r);
    }
    void process (const float* const* channels, int numChannels, int numSamples)
    {
        const auto r = computeLevel (channels, numChannels, numSamples);
        store (r);
    }
    void process (float* const* channels, int numChannels, int numSamples)
    {
        process (const_cast<const float* const*> (channels), numChannels, numSamples);
    }
    LevelReading read() const
    {
        return { rms_.load (std::memory_order_relaxed),
                 peak_.load (std::memory_order_relaxed) };
    }
private:
    void store (LevelReading r)
    {
        rms_.store (r.rms,  std::memory_order_relaxed);
        peak_.store (r.peak, std::memory_order_relaxed);
    }
    std::atomic<float> rms_  { 0.0f };
    std::atomic<float> peak_ { 0.0f };
};
