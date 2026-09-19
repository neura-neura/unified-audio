#pragma once

#include "audio/ActiveAudioBlock.h"
#include <iostream>

// Optional integration check against an installed VST3, without opening audio
// devices or changing the user's plugin presets.
inline int checkVstBlocks (const juce::String& path)
{
    juce::VST3PluginFormat format;
    juce::OwnedArray<juce::PluginDescription> descriptions;
    format.findAllTypesForFile (descriptions, path);
    if (descriptions.isEmpty()) return 1;
    int failures = 0;
    for (auto* description : descriptions)
    {
        if (description->isInstrument) continue;
        juce::String error;
        auto actual = format.createInstanceFromDescription (*description, 48000, 512, error);
        auto reference = format.createInstanceFromDescription (*description, 48000, 512, error);
        if (actual == nullptr || reference == nullptr)
        {
            std::cerr << "VST load failed: " << error << '\n';
            ++failures;
            continue;
        }
        juce::MemoryBlock state;
        std::cout << description->name << " inputs=" << actual->getTotalNumInputChannels()
                  << " outputs=" << actual->getTotalNumOutputChannels() << '\n';
        for (bool input : {true, false})
            for (int bus = 0; bus < actual->getBusCount(input); ++bus)
                std::cout << (input ? " IN " : " OUT ") << bus << " " << actual->getBus(input,bus)->getName()
                          << " channels=" << actual->getChannelCountOfBus(input,bus) << '\n';
        actual->getStateInformation (state);
        reference->setStateInformation (state.getData(), static_cast<int> (state.getSize()));
        actual->prepareToPlay (48000, 512);
        reference->prepareToPlay (48000, 512);
        const int channels = juce::jmax (1, actual->getTotalNumInputChannels(), actual->getTotalNumOutputChannels());
        juce::AudioBuffer<float> storage (channels, 8192);
        juce::MidiBuffer midi, referenceMidi;
        const int sizes[] { 480, 128, 512, 64, 441 };
        int elapsed = 0;
        float maxError = 0.0f;
        bool finite = true;
        for (int iteration = 0; iteration < 500; ++iteration)
        {
            const int frames = sizes[iteration % 5];
            juce::AudioBuffer<float> expected (channels, frames);
            for (int channel = 0; channel < channels; ++channel)
                for (int frame = 0; frame < storage.getNumSamples(); ++frame)
                {
                    const float sample = frame >= frames ? 0.875f
                        : (elapsed + frame < 96000
                            ? 0.15f * std::sin (static_cast<float> (elapsed + frame) * 0.057595865f) : 0.0f);
                    storage.setSample (channel, frame, sample);
                    if (frame < frames) expected.setSample (channel, frame, sample);
                }
            midi.clear();
            referenceMidi.clear();
            processActiveAudioBlock (*actual, storage, midi, frames);
            reference->processBlock (expected, referenceMidi);
            for (int channel = 0; channel < actual->getTotalNumOutputChannels(); ++channel)
                for (int frame = 0; frame < frames; ++frame)
                {
                    const float sample = storage.getSample (channel, frame);
                    finite = finite && std::isfinite (sample);
                    maxError = juce::jmax (maxError, std::abs (sample - expected.getSample (channel, frame)));
                }
            elapsed += frames;
        }
        actual->releaseResources();
        reference->releaseResources();
        const bool passed = finite && maxError < 0.00001f;
        failures += passed ? 0 : 1;
        std::cout << description->name << ": frames=" << elapsed << ", max error=" << maxError
                  << ", finite=" << finite << ", " << (passed ? "PASS" : "FAIL") << '\n';
    }
    return failures == 0 ? 0 : 1;
}
