#include "audio/AsyncAudioBridge.h"

#include <algorithm>
#include <cmath>

AsyncAudioBridge::AsyncAudioBridge()
{
    for (auto& channel : samples)
        channel.resize (capacity, 0.0f);
}

void AsyncAudioBridge::reset (double newSourceRate, double newDestinationRate) noexcept
{
    sourceRate.store (newSourceRate > 0.0 ? newSourceRate : 48000.0, std::memory_order_relaxed);
    destinationRate.store (newDestinationRate > 0.0 ? newDestinationRate : 48000.0, std::memory_order_relaxed);
    writeFrame.store (0, std::memory_order_release);
    readFrame.store (0, std::memory_order_release);
    underrunCount.store (0, std::memory_order_relaxed);
    overrunCount.store (0, std::memory_order_relaxed);
    fractionalReadFrame = 0.0;
    primed = false;
}

void AsyncAudioBridge::push (const float* const* channels, int numChannels, int numFrames) noexcept
{
    if (channels == nullptr || numChannels <= 0 || numFrames <= 0)
        return;

    const auto write = writeFrame.load (std::memory_order_relaxed);
    const auto read = readFrame.load (std::memory_order_acquire);
    const auto used = write - read;
    const auto freeFrames = used < capacity ? capacity - static_cast<std::size_t> (used) : 0;
    const auto framesToWrite = std::min<std::size_t> (static_cast<std::size_t> (numFrames), freeFrames);

    if (framesToWrite < static_cast<std::size_t> (numFrames))
        overrunCount.fetch_add (1, std::memory_order_relaxed);

    const auto* left = channels[0];
    const auto* right = numChannels > 1 && channels[1] != nullptr ? channels[1] : left;
    if (left == nullptr)
        return;

    for (std::size_t frame = 0; frame < framesToWrite; ++frame)
    {
        const auto index = static_cast<std::size_t> (write + frame) & mask;
        samples[0][index] = left[frame];
        samples[1][index] = right[frame];
    }
    writeFrame.store (write + framesToWrite, std::memory_order_release);
}

void AsyncAudioBridge::pop (float* left, float* right, int numFrames) noexcept
{
    if (left == nullptr || right == nullptr || numFrames <= 0)
        return;

    std::fill_n (left, numFrames, 0.0f);
    std::fill_n (right, numFrames, 0.0f);

    const auto write = writeFrame.load (std::memory_order_acquire);
    auto read = readFrame.load (std::memory_order_relaxed);
    const auto available = write - read;
    const auto source = sourceRate.load (std::memory_order_relaxed);
    const auto destination = destinationRate.load (std::memory_order_relaxed);
    // Separate WASAPI clocks can deliver callbacks in short bursts. Mismatched
    // nominal rates need a larger reserve because resampling and two unrelated
    // hardware periods can align unfavourably. Same-rate paths stay at 35 ms.
    const auto targetSeconds = std::abs (source - destination) < 1.0 ? 0.035 : 0.100;
    const auto targetFill = static_cast<std::uint64_t> (std::max (source * targetSeconds, 64.0));

    if (! primed)
    {
        if (available < targetFill)
            return;
        fractionalReadFrame = static_cast<double> (read);
        primed = true;
    }

    // A small proportional PLL keeps unrelated hardware clocks centred. Large
    // corrections occur only while recovering from scheduler stalls.
    const auto fillError = static_cast<double> (available) - static_cast<double> (targetFill);
    const auto correction = std::clamp (fillError / std::max (1.0, static_cast<double> (targetFill)) * 0.05,
                                        -0.05, 0.05);
    const auto step = source / destination * (1.0 + correction);
    int produced = 0;
    for (; produced < numFrames; ++produced)
    {
        const auto base = static_cast<std::uint64_t> (fractionalReadFrame);
        if (base + 1 >= write)
            break;

        const auto fraction = static_cast<float> (fractionalReadFrame - static_cast<double> (base));
        const auto index0 = static_cast<std::size_t> (base) & mask;
        const auto index1 = static_cast<std::size_t> (base + 1) & mask;
        for (int channel = 0; channel < 2; ++channel)
        {
            const auto first = samples[static_cast<std::size_t> (channel)][index0];
            const auto second = samples[static_cast<std::size_t> (channel)][index1];
            (channel == 0 ? left : right)[produced] = first + (second - first) * fraction;
        }
        fractionalReadFrame += step;
    }

    read = static_cast<std::uint64_t> (fractionalReadFrame);
    readFrame.store (read, std::memory_order_release);
    if (produced < numFrames)
    {
        underrunCount.fetch_add (1, std::memory_order_relaxed);
        primed = false;
    }
}

std::uint64_t AsyncAudioBridge::queuedFrames() const noexcept
{
    const auto write = writeFrame.load (std::memory_order_acquire);
    const auto read = readFrame.load (std::memory_order_acquire);
    return write - read;
}
