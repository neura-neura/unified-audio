#pragma once

#include <juce_core/juce_core.h>
#include <juce_dsp/juce_dsp.h>

class SpectrumAnalyzer final : private juce::Thread
{
public:
    static constexpr std::size_t bandCount = 16;

    SpectrumAnalyzer();
    ~SpectrumAnalyzer() override;

    void push (const juce::AudioBuffer<float>& buffer, int frames) noexcept;
    std::array<float, bandCount> read() const noexcept;
    void setSampleRate (double rate) noexcept { sampleRate.store (rate); }

private:
    void run() override;
    void analyzeFrame() noexcept;

    static constexpr int fftOrder = 11;
    static constexpr int fftSize = 1 << fftOrder;
    static constexpr int ringCapacity = 32768;
    juce::AbstractFifo fifo { ringCapacity };
    std::array<float, ringCapacity> ring {};
    std::array<float, fftSize * 2> fftData {};
    std::array<std::atomic<float>, bandCount> bands {};
    juce::dsp::FFT fft { fftOrder };
    juce::dsp::WindowingFunction<float> window { fftSize, juce::dsp::WindowingFunction<float>::hann };
    juce::WaitableEvent dataAvailable;
    std::atomic<double> sampleRate { 48000.0 };
};
