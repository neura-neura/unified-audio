#include "audio/SpectrumAnalyzer.h"

SpectrumAnalyzer::SpectrumAnalyzer() : juce::Thread ("OutputSpectrum")
{
    startThread (juce::Thread::Priority::low);
}

SpectrumAnalyzer::~SpectrumAnalyzer()
{
    signalThreadShouldExit();
    dataAvailable.signal();
    stopThread (2000);
}

void SpectrumAnalyzer::push (const juce::AudioBuffer<float>& buffer, int frames) noexcept
{
    const auto channels = buffer.getNumChannels();
    if (channels <= 0 || frames <= 0) return;
    const auto toWrite = juce::jmin (frames, fifo.getFreeSpace());
    int start1 = 0, size1 = 0, start2 = 0, size2 = 0;
    fifo.prepareToWrite (toWrite, start1, size1, start2, size2);
    const auto* left = buffer.getReadPointer (0);
    const auto* right = buffer.getReadPointer (juce::jmin (1, channels - 1));
    for (int index = 0; index < size1; ++index)
        ring[static_cast<std::size_t> (start1 + index)] = 0.5f * (left[index] + right[index]);
    for (int index = 0; index < size2; ++index)
        ring[static_cast<std::size_t> (start2 + index)] = 0.5f * (left[size1 + index] + right[size1 + index]);
    fifo.finishedWrite (size1 + size2);
    if (fifo.getNumReady() >= fftSize) dataAvailable.signal();
}

std::array<float, SpectrumAnalyzer::bandCount> SpectrumAnalyzer::read() const noexcept
{
    std::array<float, bandCount> result {};
    for (std::size_t index = 0; index < bandCount; ++index)
        result[index] = bands[index].load (std::memory_order_relaxed);
    return result;
}

void SpectrumAnalyzer::run()
{
    while (! threadShouldExit())
    {
        dataAvailable.wait (250);
        while (! threadShouldExit() && fifo.getNumReady() >= fftSize)
        {
            int start1 = 0, size1 = 0, start2 = 0, size2 = 0;
            fifo.prepareToRead (fftSize, start1, size1, start2, size2);
            std::copy_n (ring.data() + start1, size1, fftData.data());
            std::copy_n (ring.data() + start2, size2, fftData.data() + size1);
            fifo.finishedRead (size1 + size2);
            std::fill (fftData.begin() + fftSize, fftData.end(), 0.0f);
            analyzeFrame();
        }
    }
}

void SpectrumAnalyzer::analyzeFrame() noexcept
{
    window.multiplyWithWindowingTable (fftData.data(), fftSize);
    fft.performFrequencyOnlyForwardTransform (fftData.data());
    const auto rate = sampleRate.load (std::memory_order_relaxed);
    constexpr double minimumHz = 60.0;
    constexpr double maximumHz = 16000.0;
    for (std::size_t band = 0; band < bandCount; ++band)
    {
        const auto lowRatio = static_cast<double> (band) / bandCount;
        const auto highRatio = static_cast<double> (band + 1) / bandCount;
        const auto lowHz = minimumHz * std::pow (maximumHz / minimumHz, lowRatio);
        const auto highHz = minimumHz * std::pow (maximumHz / minimumHz, highRatio);
        const auto firstBin = juce::jlimit (1, fftSize / 2,
            static_cast<int> (std::floor (lowHz * fftSize / rate)));
        const auto lastBin = juce::jlimit (firstBin, fftSize / 2,
            static_cast<int> (std::ceil (highHz * fftSize / rate)));
        auto peak = 0.0f;
        for (int bin = firstBin; bin <= lastBin; ++bin)
            peak = juce::jmax (peak, fftData[static_cast<std::size_t> (bin)] / fftSize);
        const auto db = peak > 0.0f ? 20.0f * std::log10 (peak) : -100.0f;
        const auto normalized = juce::jlimit (0.0f, 1.0f, (db + 80.0f) / 80.0f);
        const auto previous = bands[band].load (std::memory_order_relaxed);
        const auto smoothed = juce::jmax (normalized, previous * 0.82f);
        bands[band].store (smoothed < 1.0e-6f ? 0.0f : smoothed, std::memory_order_relaxed);
    }
}
