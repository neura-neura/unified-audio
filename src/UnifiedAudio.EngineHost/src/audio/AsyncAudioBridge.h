#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <vector>

// Bounded single-producer/single-consumer bridge between unrelated WASAPI clocks.
// Capture owns push(); render owns pop(). Both methods are allocation-free and lock-free.
class AsyncAudioBridge
{
public:
    static constexpr std::size_t capacity = 1u << 18; // ~5.4 s at 48 kHz

    AsyncAudioBridge();

    void reset (double sourceRate, double destinationRate) noexcept;
    void push (const float* const* channels, int numChannels, int numFrames) noexcept;
    void pop (float* left, float* right, int numFrames) noexcept;

    std::uint64_t underruns() const noexcept { return underrunCount.load (std::memory_order_relaxed); }
    std::uint64_t overruns() const noexcept { return overrunCount.load (std::memory_order_relaxed); }
    std::uint64_t queuedFrames() const noexcept;
    double sourceSampleRate() const noexcept { return sourceRate.load (std::memory_order_relaxed); }
    double destinationSampleRate() const noexcept { return destinationRate.load (std::memory_order_relaxed); }

private:
    static constexpr std::size_t mask = capacity - 1;
    static_assert ((capacity & mask) == 0, "capacity must be a power of two");

    std::array<std::vector<float>, 2> samples;
    std::atomic<std::uint64_t> writeFrame { 0 };
    std::atomic<std::uint64_t> readFrame { 0 };
    std::atomic<std::uint64_t> underrunCount { 0 };
    std::atomic<std::uint64_t> overrunCount { 0 };
    std::atomic<double> sourceRate { 48000.0 };
    std::atomic<double> destinationRate { 48000.0 };
    double fractionalReadFrame = 0.0; // render thread only
    bool primed = false;              // render thread only
};
