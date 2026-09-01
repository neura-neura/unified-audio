#include "audio/AudioSessionCatalog.h"

#if JUCE_WINDOWS
 #define WIN32_LEAN_AND_MEAN
 #define NOMINMAX
 #include <windows.h>
 #include <audiopolicy.h>
 #include <endpointvolume.h>
 #include <propkeydef.h>
 #include <functiondiscoverykeys_devpkey.h>
 #include <mmdeviceapi.h>
 #include <propvarutil.h>
 #include <wrl/client.h>
#endif

namespace
{
#if JUCE_WINDOWS
using Microsoft::WRL::ComPtr;

juce::String takeCoTaskString (wchar_t* value)
{
    const juce::String result (value != nullptr ? value : L"");
    CoTaskMemFree (value);
    return result;
}

juce::String executableForProcess (DWORD processId)
{
    const auto process = OpenProcess (PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (process == nullptr) return {};
    wchar_t path[32768] {};
    DWORD length = static_cast<DWORD> (std::size (path));
    const auto succeeded = QueryFullProcessImageNameW (process, 0, path, &length) != FALSE;
    CloseHandle (process);
    return succeeded ? juce::String (path, static_cast<int> (length)) : juce::String();
}

juce::String friendlyName (IMMDevice* device)
{
    ComPtr<IPropertyStore> properties;
    if (device == nullptr || FAILED (device->OpenPropertyStore (STGM_READ, &properties))) return {};
    PROPVARIANT value;
    PropVariantInit (&value);
    const auto result = properties->GetValue (PKEY_Device_FriendlyName, &value);
    const juce::String name = SUCCEEDED (result) && value.vt == VT_LPWSTR ? value.pwszVal : L"";
    PropVariantClear (&value);
    return name;
}

ComPtr<IMMDevice> findRenderDevice (IMMDeviceEnumerator* enumerator,
                                    const juce::String& requestedName,
                                    const juce::String& requestedId)
{
    ComPtr<IMMDevice> result;
    if (requestedName.isEmpty() && requestedId.isEmpty())
    {
        enumerator->GetDefaultAudioEndpoint (eRender, eMultimedia, &result);
        return result;
    }

    ComPtr<IMMDeviceCollection> devices;
    if (FAILED (enumerator->EnumAudioEndpoints (eRender, DEVICE_STATE_ACTIVE, &devices))) return {};
    UINT count = 0;
    devices->GetCount (&count);
    int friendlyMatches = 0;
    int idMatches = 0;
    for (UINT index = 0; index < count; ++index)
    {
        ComPtr<IMMDevice> candidate;
        if (FAILED (devices->Item (index, &candidate))) continue;
        if (requestedId.isNotEmpty())
        {
            wchar_t* id = nullptr;
            const auto idResult = candidate->GetId (&id);
            const auto matches = SUCCEEDED (idResult) && id != nullptr
                && requestedId.equalsIgnoreCase (juce::String (id));
            CoTaskMemFree (id);
            if (matches)
            {
                ++idMatches;
                if (idMatches == 1) result = candidate;
                else result = nullptr;
            }
        }
        else if (friendlyName (candidate.Get()).equalsIgnoreCase (requestedName))
        {
            ++friendlyMatches;
            if (friendlyMatches == 1) result = candidate;
            else result = nullptr;
        }
    }
    if (requestedId.isNotEmpty())
        return idMatches == 1 ? result : ComPtr<IMMDevice>();
    return friendlyMatches > 1 ? ComPtr<IMMDevice>() : result;
}
#endif
}

juce::Array<AudioEndpointIdentity> listAudioEndpointIdentities (bool capture)
{
    juce::Array<AudioEndpointIdentity> result;
#if JUCE_WINDOWS
    const auto comResult = CoInitializeEx (nullptr, COINIT_MULTITHREADED);
    const auto uninitialize = comResult == S_OK || comResult == S_FALSE;
    if (FAILED (comResult) && comResult != RPC_E_CHANGED_MODE) return result;

    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IMMDeviceCollection> devices;
    if (SUCCEEDED (CoCreateInstance (__uuidof (MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                     IID_PPV_ARGS (&enumerator)))
        && SUCCEEDED (enumerator->EnumAudioEndpoints (capture ? eCapture : eRender,
                                                       DEVICE_STATEMASK_ALL, &devices)))
    {
        UINT count = 0;
        devices->GetCount (&count);
        for (UINT index = 0; index < count; ++index)
        {
            ComPtr<IMMDevice> device;
            wchar_t* id = nullptr;
            if (SUCCEEDED (devices->Item (index, &device)) && SUCCEEDED (device->GetId (&id)))
                result.add ({ takeCoTaskString (id), friendlyName (device.Get()) });
        }
    }
    if (uninitialize) CoUninitialize();
#else
    juce::ignoreUnused (capture);
#endif
    return result;
}

juce::String resolveAudioEndpointName (const juce::String& id, bool capture)
{
    if (id.isEmpty()) return {};
    for (const auto& endpoint : listAudioEndpointIdentities (capture))
        if (endpoint.id.equalsIgnoreCase (id)) return endpoint.name;
    return {};
}

juce::Array<AudioSessionInfo> listRenderAudioSessions (const juce::String& deviceName,
                                                       const juce::String& deviceId,
                                                       juce::String& error)
{
    juce::Array<AudioSessionInfo> result;
#if ! JUCE_WINDOWS
    juce::ignoreUnused (deviceName, deviceId);
    error = "Audio sessions are available only on Windows.";
#else
    const auto comResult = CoInitializeEx (nullptr, COINIT_MULTITHREADED);
    const auto uninitialize = comResult == S_OK || comResult == S_FALSE;
    if (FAILED (comResult) && comResult != RPC_E_CHANGED_MODE)
    {
        error = "CoInitializeEx failed while enumerating audio sessions.";
        return result;
    }

    ComPtr<IMMDeviceEnumerator> enumerator;
    auto hr = CoCreateInstance (__uuidof (MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                IID_PPV_ARGS (&enumerator));
    auto device = SUCCEEDED (hr) ? findRenderDevice (enumerator.Get(), deviceName, deviceId) : nullptr;
    if (device == nullptr)
    {
        error = deviceName.isEmpty() && deviceId.isEmpty()
            ? "The default render endpoint is unavailable."
            : "The selected render endpoint is unavailable: "
                + (deviceId.isNotEmpty() ? deviceId : deviceName);
        if (uninitialize) CoUninitialize();
        return result;
    }

    ComPtr<IAudioSessionManager2> manager;
    hr = device->Activate (__uuidof (IAudioSessionManager2), CLSCTX_ALL, nullptr,
                           reinterpret_cast<void**> (manager.GetAddressOf()));
    ComPtr<IAudioSessionEnumerator> sessions;
    if (FAILED (hr) || FAILED (manager->GetSessionEnumerator (&sessions)))
    {
        error = "WASAPI could not enumerate render sessions.";
        if (uninitialize) CoUninitialize();
        return result;
    }

    int count = 0;
    sessions->GetCount (&count);
    for (int index = 0; index < count; ++index)
    {
        ComPtr<IAudioSessionControl> control;
        ComPtr<IAudioSessionControl2> control2;
        if (FAILED (sessions->GetSession (index, &control)) || FAILED (control.As (&control2))) continue;

        DWORD processId = 0;
        AudioSessionState state = AudioSessionStateInactive;
        control2->GetProcessId (&processId);
        control->GetState (&state);
        if (processId == 0 || processId == GetCurrentProcessId()
            || control2->IsSystemSoundsSession() == S_OK) continue;

        wchar_t* sessionId = nullptr;
        wchar_t* display = nullptr;
        control2->GetSessionIdentifier (&sessionId);
        control->GetDisplayName (&display);

        AudioSessionInfo info;
        info.processId = processId;
        info.sessionId = takeCoTaskString (sessionId);
        info.displayName = takeCoTaskString (display);
        info.executablePath = executableForProcess (processId);
        info.processName = juce::File (info.executablePath).getFileNameWithoutExtension();
        if (info.displayName.isEmpty()) info.displayName = info.processName;
        if (info.displayName.isEmpty()) info.displayName = "PID " + juce::String (processId);
        info.active = state == AudioSessionStateActive;

        ComPtr<ISimpleAudioVolume> volume;
        if (SUCCEEDED (control.As (&volume)))
        {
            volume->GetMasterVolume (&info.sessionVolume);
            BOOL muted = FALSE;
            volume->GetMute (&muted);
            info.sessionMuted = muted != FALSE;
        }
        ComPtr<IAudioMeterInformation> meter;
        if (SUCCEEDED (control.As (&meter))) meter->GetPeakValue (&info.peak);

        // A process can expose more than one session. The process-loopback API
        // captures its complete tree, so expose one row per PID and keep the
        // strongest meter reading.
        auto existing = std::find_if (result.begin(), result.end(), [&info] (const auto& item)
        {
            return item.processId == info.processId;
        });
        if (existing == result.end()) result.add (std::move (info));
        else
        {
            existing->peak = juce::jmax (existing->peak, info.peak);
            existing->active = existing->active || info.active;
        }
    }

    std::sort (result.begin(), result.end(), [] (const auto& left, const auto& right)
    {
        if (left.active != right.active) return left.active > right.active;
        return left.displayName.compareIgnoreCase (right.displayName) < 0;
    });
    if (uninitialize) CoUninitialize();
#endif
    return result;
}
