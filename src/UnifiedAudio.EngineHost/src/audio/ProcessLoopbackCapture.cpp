#include "audio/ProcessLoopbackCapture.h"

#if JUCE_WINDOWS
 #define WIN32_LEAN_AND_MEAN
 #define NOMINMAX
 #include <windows.h>
 #include <audioclient.h>
 #include <audioclientactivationparams.h>
 #include <mmdeviceapi.h>
 #include <wrl/client.h>
 #include <wrl/implements.h>
#endif

ProcessLoopbackCapture::ProcessLoopbackCapture() : juce::Thread ("ProcessLoopbackCapture") {}
ProcessLoopbackCapture::~ProcessLoopbackCapture() { stop(); }

juce::String ProcessLoopbackCapture::start (unsigned long pid, double destinationRate)
{
    stop();
    if (pid == 0) return "Process ID must be nonzero.";
    requestedProcessId.store (pid);
    requestedDestinationRate = destinationRate > 0.0 ? destinationRate : 48000.0;
    startupError.clear();
    startupComplete.reset();
    startThread (juce::Thread::Priority::high);
    if (! startupComplete.wait (5000))
    {
        stop();
        return "Process loopback startup timed out.";
    }
    return startupError;
}

void ProcessLoopbackCapture::stop()
{
    signalThreadShouldExit();
    stopThread (5000);
    capturing.store (false, std::memory_order_release);
}

void ProcessLoopbackCapture::run()
{
    startupError = runWasapi();
    capturing.store (false, std::memory_order_release);
    startupComplete.signal();
}

juce::String ProcessLoopbackCapture::runWasapi()
{
#if ! JUCE_WINDOWS
    return "Process loopback is available only on Windows.";
#else
    using namespace Microsoft::WRL;
    class ActivationHandler final
        : public RuntimeClass<RuntimeClassFlags<ClassicCom>, FtmBase, IActivateAudioInterfaceCompletionHandler>
    {
    public:
        ActivationHandler() { completed = CreateEventW (nullptr, FALSE, FALSE, nullptr); }
        ~ActivationHandler() override { if (completed != nullptr) CloseHandle (completed); }

        STDMETHODIMP ActivateCompleted (IActivateAudioInterfaceAsyncOperation* operation) override
        {
            ComPtr<IUnknown> unknown;
            result = operation->GetActivateResult (&activateResult, &unknown);
            if (SUCCEEDED (result) && SUCCEEDED (activateResult))
                result = unknown.As (&client);
            else if (SUCCEEDED (result))
                result = activateResult;
            SetEvent (completed);
            return S_OK;
        }

        HANDLE completed = nullptr;
        HRESULT result = E_UNEXPECTED;
        HRESULT activateResult = E_UNEXPECTED;
        ComPtr<IAudioClient> client;
    };

    const auto comResult = CoInitializeEx (nullptr, COINIT_MULTITHREADED);
    const auto uninitialize = comResult == S_OK || comResult == S_FALSE;
    if (FAILED (comResult) && comResult != RPC_E_CHANGED_MODE)
        return "CoInitializeEx failed for process loopback.";

    auto finish = [&uninitialize] { if (uninitialize) CoUninitialize(); };
    AUDIOCLIENT_ACTIVATION_PARAMS audioParams {};
    audioParams.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    audioParams.ProcessLoopbackParams.TargetProcessId = requestedProcessId.load();
    audioParams.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

    PROPVARIANT activationParams {};
    activationParams.vt = VT_BLOB;
    activationParams.blob.cbSize = sizeof (audioParams);
    activationParams.blob.pBlobData = reinterpret_cast<BYTE*> (&audioParams);

    auto handler = Make<ActivationHandler>();
    ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
    auto hr = ActivateAudioInterfaceAsync (
        VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
        __uuidof (IAudioClient),
        &activationParams,
        handler.Get(),
        &operation);
    if (FAILED (hr) || handler->completed == nullptr
        || WaitForSingleObject (handler->completed, 5000) != WAIT_OBJECT_0
        || FAILED (handler->result))
    {
        finish();
        return "ActivateAudioInterfaceAsync failed: 0x" + juce::String::toHexString (static_cast<int> (FAILED (hr) ? hr : handler->result));
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
    hr = handler->client->Initialize (AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, &format.Format, nullptr);
    if (FAILED (hr))
    {
        finish();
        return "Process loopback Initialize failed: 0x" + juce::String::toHexString (static_cast<int> (hr));
    }

    const auto audioEvent = CreateEventW (nullptr, FALSE, FALSE, nullptr);
    if (audioEvent == nullptr || FAILED (handler->client->SetEventHandle (audioEvent)))
    {
        if (audioEvent != nullptr) CloseHandle (audioEvent);
        finish();
        return "Process loopback event setup failed.";
    }
    ComPtr<IAudioCaptureClient> capture;
    hr = handler->client->GetService (IID_PPV_ARGS (&capture));
    if (FAILED (hr) || FAILED (handler->client->Start()))
    {
        CloseHandle (audioEvent); finish();
        return "Process loopback capture could not start.";
    }

    bridge.reset (48000.0, requestedDestinationRate);
    capturing.store (true, std::memory_order_release);
    startupComplete.signal();
    while (! threadShouldExit())
    {
        if (WaitForSingleObject (audioEvent, 200) != WAIT_OBJECT_0) continue;
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
                float separated[2][8192];
                const auto silent = (packetFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == nullptr;
                for (UINT32 frame = 0; frame < chunk; ++frame)
                {
                    separated[0][frame] = silent ? 0.0f : interleaved[(offset + frame) * 2];
                    separated[1][frame] = silent ? 0.0f : interleaved[(offset + frame) * 2 + 1];
                }
                const float* channels[] { separated[0], separated[1] };
                bridge.push (channels, 2, static_cast<int> (chunk));
                offset += chunk;
            }
            capture->ReleaseBuffer (packetFrames);
        }
    }

    handler->client->Stop();
    CloseHandle (audioEvent);
    finish();
    return {};
#endif
}
