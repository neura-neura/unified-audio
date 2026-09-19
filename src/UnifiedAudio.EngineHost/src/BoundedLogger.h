#pragma once
#include <juce_core/juce_core.h>

class BoundedLogger final : public juce::Logger
{
public:
    explicit BoundedLogger (juce::File destination) : file (std::move (destination)) {}
    void logMessage (const juce::String& message) override
    {
        const juce::ScopedLock guard (lock);
        const auto record = juce::Time::getCurrentTime().toISO8601 (true) + " "
            + message.substring (0, 8192) + "\n";
        const auto first = file.getSiblingFile (file.getFileName() + ".1");
        const auto second = file.getSiblingFile (file.getFileName() + ".2");
        for (const auto& candidate : { file, first, second })
            if (candidate.getSize() > maximumBytes) candidate.deleteFile();
        if (file.getSize() + static_cast<juce::int64> (record.getNumBytesAsUTF8()) > maximumBytes)
        {
            if (first.existsAsFile())
            {
                second.deleteFile();
                if (! first.moveFileTo (second)) return;
            }
            if (! file.moveFileTo (first)) return;
        }
        file.appendText (record, false, false, nullptr);
    }
    static constexpr juce::int64 maximumBytes = 2 * 1024 * 1024;
private:
    juce::File file;
    juce::CriticalSection lock;
};
