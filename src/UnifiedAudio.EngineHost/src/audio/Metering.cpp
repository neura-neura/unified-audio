#include "audio/Metering.h"

LevelReading computeLevel (const juce::AudioBuffer<float>& buffer)
{
    return computeLevel (buffer.getArrayOfReadPointers(), buffer.getNumChannels(), buffer.getNumSamples());
}

LevelReading computeLevel (const float* const* channels, int numChannels, int numSamples)
{
    if (channels == nullptr || numChannels <= 0 || numSamples <= 0) return {};

    double sumSq = 0.0;
    float  peak  = 0.0f;
    int contributingChannels = 0;
    for (int ch = 0; ch < numChannels; ++ch)
    {
        const float* d = channels[ch];
        if (d == nullptr) continue;
        ++contributingChannels;
        for (int i = 0; i < numSamples; ++i)
        {
            const float s = d[i];
            sumSq += (double) s * s;
            peak = juce::jmax (peak, std::abs (s));
        }
    }
    if (contributingChannels == 0) return {};
    const float rms = (float) std::sqrt (sumSq / (double) (contributingChannels * numSamples));
    return { rms, peak };
}
