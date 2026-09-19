#pragma once

#include <juce_audio_processors/juce_audio_processors.h>

// Storage capacity is not the callback length. Keep plugin time and delay lines
// advancing only by the frames that will actually be sent to the device.
inline void processActiveAudioBlock (juce::AudioProcessor& processor,
                                    juce::AudioBuffer<float>& storage,
                                    juce::MidiBuffer& midi, int frames)
{
    jassert (frames > 0 && frames <= storage.getNumSamples());
    juce::AudioBuffer<float> block (storage.getArrayOfWritePointers(),
                                  storage.getNumChannels(), frames);
    processor.processBlock (block, midi);
}
