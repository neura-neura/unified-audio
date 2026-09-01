#include "audio/SystemLoopbackCapture.h"

#if JUCE_WINDOWS
 #define WIN32_LEAN_AND_MEAN
 #define NOMINMAX
 #include <windows.h>
 #include <audioclient.h>
 #include <mmdeviceapi.h>
 #include <functiondiscoverykeys_devpkey.h>
 #include <wrl/client.h>
 #include <vector>
#endif

SystemLoopbackCapture::SystemLoopbackCapture (AsyncAudioBridge& destination)
    : juce::Thread ("SystemLoopbackCapture"), bridge (destination)
{
}

SystemLoopbackCapture::~SystemLoopbackCapture() { stop(); }

juce::String SystemLoopbackCapture::start (const juce::String& renderDeviceName,
                                           const juce::String& renderDeviceId,
                                           double destinationRate)
{
    stop();
    requestedDeviceName = renderDeviceName;
    requestedDeviceId = renderDeviceId;
    requestedDestinationRate = destinationRate > 0.0 ? destinationRate : 48000.0;
    startupError.clear();
    startupComplete.reset();
    startThread (juce::Thread::Priority::high);
    if (! startupComplete.wait (5000))
    {
        stop();
        return "System loopback startup timed out.";
    }
    return startupError;
}

void SystemLoopbackCapture::stop()
{
    signalThreadShouldExit();
    stopThread (5000);
    capturing.store (false, std::memory_order_release);
}

void SystemLoopbackCapture::run()
{
    startupError = runWasapi();
    capturing.store (false, std::memory_order_release);
    startupComplete.signal();
}

juce::String SystemLoopbackCapture::runWasapi()
{
#if ! JUCE_WINDOWS
    return "System loopback is available only on Windows.";
#else
    using Microsoft::WRL::ComPtr;
    const auto comResult = CoInitializeEx (nullptr, COINIT_MULTITHREADED);
    const auto uninitialize = comResult == S_OK || comResult == S_FALSE;
    if (FAILED (comResult) && comResult != RPC_E_CHANGED_MODE)
    {
        return "CoInitializeEx failed: 0x" + juce::String::toHexString (static_cast<int> (comResult));
    }

    auto finish = [&uninitialize]
    {
        if (uninitialize) CoUninitialize();
    };

    ComPtr<IMMDeviceEnumerator> enumerator;
    auto hr = CoCreateInstance (__uuidof (MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                IID_PPV_ARGS (&enumerator));
    if (FAILED (hr))
    {
        finish();
        return "MMDeviceEnumerator failed: 0x" + juce::String::toHexString (static_cast<int> (hr));
    }

    ComPtr<IMMDevice> device;
    if (requestedDeviceName.isEmpty() && requestedDeviceId.isEmpty())
    {
        hr = enumerator->GetDefaultAudioEndpoint (eRender, eMultimedia, &device);
    }
    else
    {
        ComPtr<IMMDeviceCollection> collection;
        hr = enumerator->EnumAudioEndpoints (eRender, DEVICE_STATE_ACTIVE, &collection);
        if (SUCCEEDED (hr))
        {
            UINT count = 0;
            collection->GetCount (&count);
            int friendlyMatches = 0;
            int idMatches = 0;
            for (UINT index = 0; index < count; ++index)
            {
                ComPtr<IMMDevice> candidate;
                ComPtr<IPropertyStore> properties;
                PROPVARIANT value;
                PropVariantInit (&value);
                if (SUCCEEDED (collection->Item (index, &candidate)))
                {
                    LPWSTR id = nullptr;
                    const auto idOk = SUCCEEDED (candidate->GetId (&id));
                    const juce::String candidateId = idOk && id != nullptr ? juce::String (id) : juce::String();
                    if (id != nullptr) CoTaskMemFree (id);
                    if (requestedDeviceId.isNotEmpty())
                    {
                        if (candidateId.equalsIgnoreCase (requestedDeviceId))
                        {
                            ++idMatches;
                            if (idMatches == 1) device = candidate;
                            else device = nullptr;
                        }
                    }
                    else if (SUCCEEDED (candidate->OpenPropertyStore (STGM_READ, &properties))
                             && SUCCEEDED (properties->GetValue (PKEY_Device_FriendlyName, &value))
                             && value.vt == VT_LPWSTR
                             && requestedDeviceName == juce::String (value.pwszVal))
                    {
                        ++friendlyMatches;
                        if (friendlyMatches == 1) device = candidate;
                        else device = nullptr;
                    }
                }
                PropVariantClear (&value);
            }
            if (requestedDeviceId.isEmpty() && friendlyMatches > 1)
                hr = HRESULT_FROM_WIN32 (ERROR_DUP_NAME);
            if (requestedDeviceId.isNotEmpty() && idMatches > 1)
                hr = HRESULT_FROM_WIN32 (ERROR_DUP_NAME);
        }
        if (device == nullptr && SUCCEEDED (hr))
            hr = HRESULT_FROM_WIN32 (ERROR_NOT_FOUND);
    }
    if (FAILED (hr) || device == nullptr)
    {
        finish();
        return "System render endpoint was not found: " + requestedDeviceName;
    }

    ComPtr<IAudioClient> client;
    hr = device->Activate (__uuidof (IAudioClient), CLSCTX_ALL, nullptr,
                           reinterpret_cast<void**> (client.GetAddressOf()));
    if (FAILED (hr))
    {
        finish();
        return "IAudioClient activation failed: 0x" + juce::String::toHexString (static_cast<int> (hr));
    }

    WAVEFORMATEXTENSIBLE format {};
    format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    format.Format.nChannels = 2;
    format.Format.nSamplesPerSec = 48000;
    format.Format.wBitsPerSample = 32;
    format.Format.nBlockAlign = 8;
    format.Format.nAvgBytesPerSec = format.Format.nSamplesPerSec * format.Format.nBlockAlign;
    format.Format.cbSize = sizeof (WAVEFORMATEXTENSIBLE) - sizeof (WAVEFORMATEX);
    format.Samples.wValidBitsPerSample = 32;
    format.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
    format.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

    constexpr DWORD flags = AUDCLNT_STREAMFLAGS_LOOPBACK
                          | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                          | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                          | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    hr = client->Initialize (AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, &format.Format, nullptr);
    if (FAILED (hr))
    {
        finish();
        return "Loopback initialization failed: 0x" + juce::String::toHexString (static_cast<int> (hr));
    }

    const auto audioEvent = CreateEventW (nullptr, FALSE, FALSE, nullptr);
    const auto stopEvent = CreateEventW (nullptr, TRUE, FALSE, nullptr);
    if (audioEvent == nullptr || stopEvent == nullptr)
    {
        if (audioEvent != nullptr) CloseHandle (audioEvent);
        if (stopEvent != nullptr) CloseHandle (stopEvent);
        finish();
        return "Could not create loopback events.";
    }
    client->SetEventHandle (audioEvent);

    ComPtr<IAudioCaptureClient> capture;
    hr = client->GetService (IID_PPV_ARGS (&capture));
    if (FAILED (hr) || FAILED (client->Start()))
    {
        CloseHandle (audioEvent); CloseHandle (stopEvent);
        finish();
        return "Could not start system loopback capture.";
    }

    bridge.reset (48000.0, requestedDestinationRate);
    capturing.store (true, std::memory_order_release);
    startupComplete.signal();
    HANDLE waits[] { audioEvent, stopEvent };
    while (! threadShouldExit())
    {
        const auto waitResult = WaitForMultipleObjects (2, waits, FALSE, 200);
        if (waitResult != WAIT_OBJECT_0) continue;
        UINT32 packetFrames = 0;
        while (SUCCEEDED (capture->GetNextPacketSize (&packetFrames)) && packetFrames > 0)
        {
            BYTE* data = nullptr;
            DWORD packetFlags = 0;
            UINT64 position = 0, qpc = 0;
            if (FAILED (capture->GetBuffer (&data, &packetFrames, &packetFlags, &position, &qpc))) break;
            const auto* interleaved = reinterpret_cast<const float*> (data);
            UINT32 offset = 0;
            while (offset < packetFrames)
            {
                const auto chunk = juce::jmin<UINT32> (packetFrames - offset, 8192);
                float deinterleaved[2][8192];
                const auto silent = (packetFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == nullptr;
                for (UINT32 frame = 0; frame < chunk; ++frame)
                {
                    deinterleaved[0][frame] = silent ? 0.0f : interleaved[(offset + frame) * 2];
                    deinterleaved[1][frame] = silent ? 0.0f : interleaved[(offset + frame) * 2 + 1];
                }
                const float* channels[] { deinterleaved[0], deinterleaved[1] };
                bridge.push (channels, 2, static_cast<int> (chunk));
                offset += chunk;
            }
            capture->ReleaseBuffer (packetFrames);
        }
    }

    SetEvent (stopEvent);
    client->Stop();
    CloseHandle (audioEvent);
    CloseHandle (stopEvent);
    finish();
    return {};
#endif
}
