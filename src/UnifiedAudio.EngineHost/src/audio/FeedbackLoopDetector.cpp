#include "audio/FeedbackLoopDetector.h"

#if JUCE_WINDOWS
 #define WIN32_LEAN_AND_MEAN
 #define NOMINMAX
 #include <windows.h>
 #include <audiopolicy.h>
 #include <mmdeviceapi.h>
 #include <functiondiscoverykeys_devpkey.h>
 #include <wrl/client.h>
#endif

namespace
{
bool containsIgnoreCase (const juce::String& value, const char* token)
{
    return value.containsIgnoreCase (token);
}

bool looksLikeVirtualRender (const juce::String& name)
{
    if (name.isEmpty()) return false;
    const auto hasInput = containsIgnoreCase (name, "input");
    return (containsIgnoreCase (name, "cable") && hasInput)
        || (containsIgnoreCase (name, "vb-audio") && ! containsIgnoreCase (name, "output"))
        || (containsIgnoreCase (name, "virtual audio cable") && ! containsIgnoreCase (name, "output"))
        || (containsIgnoreCase (name, "voicemeeter") && hasInput);
}

bool looksLikeVirtualCapture (const juce::String& name)
{
    if (name.isEmpty()) return false;
    const auto hasInput = containsIgnoreCase (name, "input");
    const auto hasOutput = containsIgnoreCase (name, "output");
    return (containsIgnoreCase (name, "cable") && (hasOutput || ! hasInput))
        || (containsIgnoreCase (name, "vb-audio") && hasOutput)
        || (containsIgnoreCase (name, "virtual audio cable") && ! hasInput)
        || (containsIgnoreCase (name, "voicemeeter") && hasOutput);
}

juce::String familyKey (juce::String name)
{
    name = name.toLowerCase()
               .replace ("input", juce::String())
               .replace ("output", juce::String())
               .removeCharacters (" \t()[]{}_-:");
    return name;
}

#if JUCE_WINDOWS
using Microsoft::WRL::ComPtr;

constexpr GUID listenSettingsGuid
    { 0x24DBB0FC, 0x9311, 0x4B3D, { 0x9C, 0xF0, 0x18, 0xFF, 0x15, 0x56, 0x39, 0xD4 } };
constexpr DWORD listenCheckboxPropertyId = 1;
constexpr DWORD listenTargetPropertyId = 0;
constexpr DWORD listenTargetPropertyIdFallback = 2;

bool isPropertyMissing (HRESULT hr)
{
    return hr == HRESULT_FROM_WIN32 (ERROR_NOT_FOUND)
        || hr == HRESULT_FROM_WIN32 (ERROR_FILE_NOT_FOUND);
}

juce::String readDeviceId (IMMDevice* device)
{
    if (device == nullptr) return {};
    LPWSTR value = nullptr;
    if (FAILED (device->GetId (&value)) || value == nullptr) return {};
    const juce::String result (value);
    CoTaskMemFree (value);
    return result;
}

juce::String readFriendlyName (IMMDevice* device)
{
    if (device == nullptr) return {};
    ComPtr<IPropertyStore> store;
    if (FAILED (device->OpenPropertyStore (STGM_READ, &store))) return {};
    PROPVARIANT value;
    PropVariantInit (&value);
    const auto hr = store->GetValue (PKEY_Device_FriendlyName, &value);
    juce::String result;
    if (SUCCEEDED (hr) && value.vt == VT_LPWSTR && value.pwszVal != nullptr)
        result = juce::String (value.pwszVal);
    PropVariantClear (&value);
    return result;
}

struct ListenBool
{
    enum class State { missing, value, unknown, error } state = State::missing;
    bool enabled = false;
};

ListenBool readListenBool (IPropertyStore* store)
{
    if (store == nullptr) return { ListenBool::State::error, false };
    PROPERTYKEY key { listenSettingsGuid, listenCheckboxPropertyId };
    PROPVARIANT value;
    PropVariantInit (&value);
    const auto hr = store->GetValue (key, &value);
    if (FAILED (hr))
    {
        PropVariantClear (&value);
        return { isPropertyMissing (hr) ? ListenBool::State::missing : ListenBool::State::error, false };
    }

    ListenBool result { ListenBool::State::unknown, false };
    switch (value.vt)
    {
        case VT_BOOL: result = { ListenBool::State::value, value.boolVal != VARIANT_FALSE }; break;
        case VT_UI1: result = { ListenBool::State::value, value.bVal != 0 }; break;
        case VT_UI2: result = { ListenBool::State::value, value.uiVal != 0 }; break;
        case VT_UI4: result = { ListenBool::State::value, value.ulVal != 0 }; break;
        case VT_I4:  result = { ListenBool::State::value, value.lVal != 0 }; break;
        default: break;
    }
    PropVariantClear (&value);
    return result;
}

struct ListenTarget
{
    bool known = false;
    bool usesDefault = false;
    juce::String id;
};

ListenTarget readListenTarget (IPropertyStore* store, bool& probeFailed)
{
    ListenTarget result;
    if (store == nullptr)
    {
        probeFailed = true;
        return result;
    }

    const PROPERTYKEY keys[] = {
        { listenSettingsGuid, listenTargetPropertyId },
        { listenSettingsGuid, listenTargetPropertyIdFallback }
    };
    for (const auto& key : keys)
    {
        PROPVARIANT value;
        PropVariantInit (&value);
        const auto hr = store->GetValue (key, &value);
        if (FAILED (hr))
        {
            PropVariantClear (&value);
            if (! isPropertyMissing (hr)) probeFailed = true;
            continue;
        }

        result.known = true;
        if (value.vt == VT_EMPTY || value.vt == VT_NULL)
            result.usesDefault = true;
        else if (value.vt == VT_LPWSTR && value.pwszVal != nullptr)
        {
            result.id = juce::String (value.pwszVal);
            result.usesDefault = false;
        }
        else if (value.vt == VT_BSTR && value.bstrVal != nullptr)
        {
            result.id = juce::String (value.bstrVal);
            result.usesDefault = false;
        }
        else
            probeFailed = true;
        PropVariantClear (&value);
        if (result.id.isNotEmpty()) return result;
    }
    return result;
}

struct Endpoint
{
    juce::String id;
    juce::String name;
};

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

bool enumerateCaptureConsumers (IMMDevice* device,
                                juce::Array<FeedbackProcessConsumer>& consumers)
{
    if (device == nullptr) return false;
    ComPtr<IAudioSessionManager2> manager;
    auto hr = device->Activate (__uuidof (IAudioSessionManager2), CLSCTX_ALL, nullptr,
                                reinterpret_cast<void**> (manager.GetAddressOf()));
    if (FAILED (hr)) return false;

    ComPtr<IAudioSessionEnumerator> sessions;
    if (FAILED (manager->GetSessionEnumerator (&sessions))) return false;
    int count = 0;
    if (FAILED (sessions->GetCount (&count))) return false;
    for (int index = 0; index < count; ++index)
    {
        ComPtr<IAudioSessionControl> control;
        ComPtr<IAudioSessionControl2> control2;
        if (FAILED (sessions->GetSession (index, &control)) || FAILED (control.As (&control2)))
            continue;
        DWORD processId = 0;
        AudioSessionState state = AudioSessionStateInactive;
        if (FAILED (control2->GetProcessId (&processId)) || processId == 0
            || FAILED (control->GetState (&state)) || state != AudioSessionStateActive
            || control2->IsSystemSoundsSession() == S_OK)
            continue;
        if (std::find_if (consumers.begin(), consumers.end(), [processId] (const auto& item)
                          { return item.processId == processId; }) != consumers.end())
            continue;

        wchar_t* display = nullptr;
        control->GetDisplayName (&display);
        const juce::String displayName (display != nullptr ? display : L"");
        CoTaskMemFree (display);
        consumers.add ({ processId, displayName, executableForProcess (processId) });
    }
    return true;
}

juce::Array<Endpoint> enumerateEndpoints (IMMDeviceEnumerator* enumerator, EDataFlow flow,
                                          DWORD state, bool& succeeded)
{
    juce::Array<Endpoint> result;
    succeeded = false;
    if (enumerator == nullptr) return result;
    ComPtr<IMMDeviceCollection> devices;
    if (FAILED (enumerator->EnumAudioEndpoints (flow, state, &devices))) return result;
    succeeded = true;
    UINT count = 0;
    if (FAILED (devices->GetCount (&count))) { succeeded = false; return {}; }
    for (UINT index = 0; index < count; ++index)
    {
        ComPtr<IMMDevice> device;
        if (FAILED (devices->Item (index, &device))) { succeeded = false; continue; }
        result.add ({ readDeviceId (device.Get()), readFriendlyName (device.Get()) });
    }
    return result;
}

struct IdentityResolution
{
    Endpoint endpoint;
    bool resolved = false;
    bool ambiguous = false;
};

IdentityResolution resolveRender (const juce::Array<Endpoint>& endpoints,
                                   const juce::String& requestedName,
                                   const juce::String& requestedId)
{
    IdentityResolution result;
    if (requestedId.isNotEmpty())
    {
        for (const auto& endpoint : endpoints)
            if (endpoint.id.equalsIgnoreCase (requestedId))
            {
                if (result.resolved)
                {
                    result.ambiguous = true;
                    result.resolved = false;
                    continue;
                }
                result.endpoint = endpoint;
                result.resolved = true;
            }
        if (result.ambiguous) result.resolved = false;
        return result;
    }
    if (requestedName.isEmpty()) return result;
    for (const auto& endpoint : endpoints)
        if (endpoint.name.equalsIgnoreCase (requestedName))
        {
            if (result.resolved) { result.ambiguous = true; continue; }
            result.endpoint = endpoint;
            result.resolved = true;
        }
    if (result.ambiguous) result.resolved = false;
    return result;
}

juce::String defaultRenderId (IMMDeviceEnumerator* enumerator)
{
    if (enumerator == nullptr) return {};
    ComPtr<IMMDevice> device;
    auto hr = enumerator->GetDefaultAudioEndpoint (eRender, eMultimedia, &device);
    if (FAILED (hr)) hr = enumerator->GetDefaultAudioEndpoint (eRender, eConsole, &device);
    return SUCCEEDED (hr) ? readDeviceId (device.Get()) : juce::String();
}

bool idEquals (const juce::String& left, const juce::String& right)
{
    return left.isNotEmpty() && right.isNotEmpty() && left.equalsIgnoreCase (right);
}
#endif
}

FeedbackLoopEvidence detectWindowsFeedbackLoop (const juce::String& finalRenderName,
                                                const juce::String& finalRenderId,
                                                const juce::String& systemRenderName,
                                                const juce::String& systemRenderId)
{
    FeedbackLoopEvidence evidence;
    evidence.finalOutputName = finalRenderName;
    evidence.systemOutputName = systemRenderName;
    evidence.systemRouteRequired = systemRenderName.isNotEmpty() || systemRenderId.isNotEmpty();

    // Stable MMDevice IDs are authoritative. Friendly-name equality is only
    // considered by resolveRender below when one of the IDs is absent and
    // that name resolves to one endpoint without ambiguity.
    if (idEquals (finalRenderId, systemRenderId)) evidence.directEndpoint = true;

#if ! JUCE_WINDOWS
    juce::ignoreUnused (finalRenderId, systemRenderId);
    return evidence;
#else
    const auto comResult = CoInitializeEx (nullptr, COINIT_MULTITHREADED);
    const auto uninitialize = comResult == S_OK || comResult == S_FALSE;
    if (FAILED (comResult) && comResult != RPC_E_CHANGED_MODE) return evidence;
    auto finish = [&uninitialize] { if (uninitialize) CoUninitialize(); };

    using Microsoft::WRL::ComPtr;
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (FAILED (CoCreateInstance (__uuidof (MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  IID_PPV_ARGS (&enumerator))))
    {
        finish();
        return evidence;
    }

    bool renderEnumerationOk = false;
    const auto renders = enumerateEndpoints (enumerator.Get(), eRender, DEVICE_STATE_ACTIVE,
                                             renderEnumerationOk);
    if (! renderEnumerationOk)
    {
        finish();
        return evidence;
    }
    evidence.probeAvailable = true;

    const auto finalIdentity = resolveRender (renders, finalRenderName, finalRenderId);
    const auto systemIdentity = resolveRender (renders, systemRenderName, systemRenderId);
    for (const auto& endpoint : renders)
        if (endpoint.id.isEmpty() || endpoint.name.isEmpty())
            evidence.propertyProbeFailed = true;
    evidence.finalIdentityResolved = finalIdentity.resolved;
    evidence.systemIdentityResolved = ! evidence.systemRouteRequired || systemIdentity.resolved;
    evidence.identityAmbiguous = finalIdentity.ambiguous || systemIdentity.ambiguous;
    evidence.finalVirtualCable = finalIdentity.resolved
        ? looksLikeVirtualRender (finalIdentity.endpoint.name)
        : looksLikeVirtualRender (finalRenderName);
    evidence.systemVirtualCable = systemIdentity.resolved
        ? looksLikeVirtualRender (systemIdentity.endpoint.name)
        : looksLikeVirtualRender (systemRenderName);
    if (finalIdentity.resolved && systemIdentity.resolved
        && idEquals (finalIdentity.endpoint.id, systemIdentity.endpoint.id))
        evidence.directEndpoint = true;

    bool captureEnumerationOk = false;
    const auto captures = enumerateEndpoints (enumerator.Get(), eCapture, DEVICE_STATE_ACTIVE,
                                              captureEnumerationOk);
    if (! captureEnumerationOk)
    {
        evidence.probeAvailable = false;
        finish();
        return evidence;
    }
    for (const auto& endpoint : captures)
        if (endpoint.id.isEmpty() || endpoint.name.isEmpty())
            evidence.propertyProbeFailed = true;

    const auto finalFamily = finalIdentity.resolved ? familyKey (finalIdentity.endpoint.name)
                                                    : familyKey (finalRenderName);
    int pairedCaptureCount = 0;
    for (const auto& capture : captures)
        if (finalFamily.isNotEmpty() && familyKey (capture.name) == finalFamily
            && looksLikeVirtualCapture (capture.name))
            ++pairedCaptureCount;
    evidence.finalCableCaptureFound = pairedCaptureCount > 0;
    evidence.finalCableCaptureAmbiguous = pairedCaptureCount > 1;

    if (pairedCaptureCount == 1)
    {
        const auto paired = std::find_if (captures.begin(), captures.end(), [&finalFamily] (const auto& capture)
        {
            return finalFamily.isNotEmpty() && familyKey (capture.name) == finalFamily
                && looksLikeVirtualCapture (capture.name);
        });
        if (paired != captures.end())
        {
            evidence.finalCaptureEndpointId = paired->id;
            evidence.finalCaptureEndpointName = paired->name;
            ComPtr<IMMDevice> pairedDevice;
            if (paired->id.isNotEmpty()
                && SUCCEEDED (enumerator->GetDevice (paired->id.toWideCharPointer(), &pairedDevice)))
            {
                evidence.finalCaptureConsumerProbeAvailable = enumerateCaptureConsumers (
                    pairedDevice.Get(), evidence.finalCaptureConsumers);
                evidence.finalCaptureConsumerDetected = ! evidence.finalCaptureConsumers.isEmpty();
            }
        }
    }
    else if (evidence.finalVirtualCable && ! evidence.finalCableCaptureFound)
    {
        // No paired capture endpoint means there is no endpoint consumer to
        // feed back into the final cable. Keep the read-only probe explicitly
        // available rather than guessing from a friendly name.
        evidence.finalCaptureConsumerProbeAvailable = true;
    }

    const auto defaultId = defaultRenderId (enumerator.Get());
    for (const auto& capture : captures)
    {
        ComPtr<IMMDevice> device;
        if (capture.id.isEmpty())
        {
            evidence.propertyProbeFailed = true;
            continue;
        }
        if (FAILED (enumerator->GetDevice (capture.id.toWideCharPointer(), &device)))
        {
            evidence.propertyProbeFailed = true;
            continue;
        }
        ComPtr<IPropertyStore> store;
        if (FAILED (device->OpenPropertyStore (STGM_READ, &store)))
        {
            evidence.propertyProbeFailed = true;
            continue;
        }
        const auto listen = readListenBool (store.Get());
        if (listen.state == ListenBool::State::error || listen.state == ListenBool::State::unknown)
        {
            evidence.propertyProbeFailed = true;
            continue;
        }
        if (listen.state != ListenBool::State::value || ! listen.enabled) continue;

        ++evidence.listenCaptureCount;
        evidence.listenEnabled = true;
        bool targetProbeFailed = false;
        const auto target = readListenTarget (store.Get(), targetProbeFailed);
        evidence.propertyProbeFailed |= targetProbeFailed;
        if (! target.known)
        {
            evidence.listenTargetUnknown = true;
            continue;
        }

        const auto targetId = target.usesDefault ? defaultId : target.id;
        if (targetId.isEmpty())
        {
            evidence.listenTargetUnknown = true;
            continue;
        }
        const auto targetMatchesFinal = finalIdentity.resolved
            && idEquals (targetId, finalIdentity.endpoint.id);
        const auto targetMatchesSystem = systemIdentity.resolved
            && idEquals (targetId, systemIdentity.endpoint.id);
        evidence.listenTargetMatchesFinal |= targetMatchesFinal;
        evidence.listenTargetMatchesSystem |= targetMatchesSystem;
        evidence.listenTargetUsesDefault |= target.usesDefault;

        const auto isPaired = finalFamily.isNotEmpty()
            && familyKey (capture.name) == finalFamily
            && looksLikeVirtualCapture (capture.name);
        if (targetMatchesFinal && ! looksLikeVirtualCapture (capture.name))
            evidence.physicalCaptureListenToFinal = true;
        if (targetMatchesSystem && isPaired)
            evidence.pairedCaptureListenToSystem = true;
    }

    if (evidence.listenEnabled && evidence.propertyProbeFailed)
        evidence.listenTargetUnknown = true;
    finish();
    return evidence;
#endif
}
