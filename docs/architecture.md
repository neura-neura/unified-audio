# UnifiedAudio architecture

## Implemented process boundary

UnifiedAudio is a WinUI 3 shell plus an out-of-process C++ audio/VST host. `UnifiedAudio` is the provisional internal/product identifier. Shell identity, storage and installer strings are mostly centralized; a future rebrand still needs an asset/manifest pass.

```text
UnifiedAudio.exe (WinUI 3)
  pages, profiles, Core Audio defaults, hotkeys, tray, OSD/overlay,
  actions, startup, updates, JSON settings and diagnostics
                  │ named pipe: framed JSON contract v1
                  ▼
UnifiedAudio Engine Host.exe (JUCE/C++20)
  independent WASAPI capture/render, asynchronous sample bridge,
  VST3 graph/scanner, mute, endpoint loopback, mixer and XML state
                  │ one child process per VST3 bundle
                  └── UnifiedAudio Engine Host.exe --scan <bundle> --out <file>
```

The process boundary protects the WinUI shell from a live-host crash and keeps scanning off the UI thread. It does not yet restart a crashed live engine automatically.

## Implemented ownership

| Component | Current responsibility | Current boundary/gap |
| --- | --- | --- |
| WinUI shell | Integrated TitleBar, Mica, adaptive NavigationView, pages, InfoBars, en/es/zh runtime catalogs, settings and 250 ms snapshots | English all-page UI Automation scan passes; full zh/es visual, keyboard and NVDA acceptance is open |
| Shell store | Per-user JSON, temp replace, backup, normalization and confirmed legacy import with fingerprints | AHK expressions/actions that cannot be represented safely remain disabled with warnings |
| Profile activation | Validates endpoints, snapshots engine/VST/filter/external/default state, applies one host transaction then Windows roles, rolls back failures | Overlay remains global and full injected-failure coverage is open |
| Device inventory | MMDevice IDs cross the UI/engine contract, persist natively and resolve current names; all endpoint states, role defaults and notifications | JUCE ultimately selects WASAPI devices by its display string |
| Engine host | JUCE WASAPI shared-low-latency device managers, graph, meters, VST3 and persistence; active profiles debounce/reapply after endpoint notifications; unexpected exits restart through a serialized connection and 3-in-30-second circuit breaker | Per-plugin quarantine after a crash in the live processing callback remains open |
| Voice bridge | Fixed SPSC storage, linear interpolation, 35/100 ms prebuffer, proportional clock PLL, zero-fill and counters | 60-second 44.1→48 passed; eight-hour soak open |
| Scanner | Standard JUCE VST3 roots plus `VST3_PATH`/custom folders, SHA-256 binary audit worker, separate child, timeout, skip/retry/rescan, atomic cache plus backup recovery | Live plugin failure after discovery still requires engine recovery |
| Mute/hotkeys | Post-VST gate, exact zero, configurable keyboard/mouse low-level hook, physical `NumpadPgDn` default, wildcard/passthrough/L-R modifiers, toggle/PTT/PTM/hybrid/release delay, separate mute/unmute and overlay bindings | Physical-device and long-duration manual matrix remains open |
| System capture/mixer | Event-driven endpoint and process loopback, Voice/System/Both, 0–400% gains, ducking, per-app rules/gains and direct/Windows-Listen feedback rejection with Voice fallback | Protected-process and long churn acceptance remain open |
| Feedback | Four generated/custom WAV states, stable-ID output selector, configurable-color no-activate OSD, per-monitor relative/scalable/movable/lockable overlay with icon/text/activity, fullscreen exclusion | Audible alternate-output, mixed-DPI and exclusive-fullscreen acceptance remain open |
| Automation | Trusted Program/PowerShell/Voicemeeter actions, variables, timeout, foreground/process linked profiles, normal auto-exit and AFK | Aura is open |
| Lifecycle | Single engine instance, tray, per-user startup shortcut, mutually exclusive delay/process gate and clean protocol shutdown | Full multi-instance redirection acceptance is open |

## Control contract v1

The engine exposes `\\.\pipe\UnifiedAudio.Engine.v1`. A Windows DACL limits the pipe to the owner and SYSTEM. Frames are UTF-8 JSON prefixed by a 32-bit little-endian length and are rejected above 8 MiB.

```json
{
  "contractVersion": 1,
  "messageId": "correlation-guid",
  "sequence": 42,
  "kind": "command",
  "name": "engine.snapshot",
  "payload": {}
}
```

Implemented command families:

- `hello`, `host.shutdown`;
- `devices.list`, `engine.start`, `engine.stop`, `engine.snapshot`;
- `mute.set`, `mute.toggle`;
- `mixer.configure`;
- `scan.start`, `scan.rescan`, `scan.retry`, `scan.skip`;
- `plugins.list`, `plugin.add/remove/move/bypass/openEditor`;
- `plugin.folders.list`, `plugin.folder.add/remove`.

Commands are serialized by the shell client and correlated by `messageId`. The current v1 implementation is request/response; graph revisions, event streaming and separate binary blob transfer are target extensions, not current claims.

## Real-time path

Capture and render use separate JUCE WASAPI device managers so their supported rates do not need to intersect. Capture pushes at most two channels into a preallocated power-of-two SPSC bridge. Render owns the read phase, performs linear interpolation with a bounded proportional clock correction, processes the VST graph, applies the 64-sample mute ramp, pulls the system-loopback bridge, applies ducking/gains and writes the final output.

The audio callbacks do not enumerate COM devices, access files, log text or call the UI. Buffers and ring storage are allocated before streaming. Current graph mutations are dispatched on the engine message thread; block-boundary graph swapping and stronger no-allocation instrumentation remain future hardening.

Endpoint loopback is a dedicated event-driven WASAPI thread requesting 48 kHz stereo float with Windows shared-mode conversion. The engine rejects a loopback source whose stable ID/name equals the final render endpoint and inspects the read-only MMDevice Listen properties to catch `final cable render → cable capture Listen → system source → loopback → final render`. The source/capture cable family is normalized from endpoint names, so standard VB-CABLE is supported without hard-coding an A/B variant. If Listen is enabled after startup, the message-thread guard stops system capture and falls back to Voice; intentional System/Both mixing remains available when Listen targets another playback endpoint.

## State and activation

Shell state lives under `%LOCALAPPDATA%\UnifiedAudio`; engine/VST/mixer state lives under `%APPDATA%\UnifiedAudio`. Both keep backups. Engine XML now uses a temporary target replacement; shell JSON writes a temp file then replaces the target.

Implemented profile order:

1. Resolve stable MMDevice references and validate availability.
2. Connect to the engine and snapshot devices, mute, mixer/filter, VST preset, external endpoint state and Windows defaults.
3. Atomically apply devices, mixer/filter, VST preset and mute inside the host.
4. Apply the explicit external-endpoint policy.
5. Apply Windows default endpoints/roles.
6. Persist the active profile after non-failed activation.
7. On failure, restore Windows roles, external endpoints and the full engine snapshot; report a partial rollback if any restore step fails.

The general `ProfileActivationCoordinator` in `UnifiedAudio.Core` separately tests ordered participants, validation and reverse rollback. The app adapter still needs to move every side effect into those participant interfaces.

## Endpoint-virtual decision

A user-mode desktop process cannot appear as a microphone endpoint in Discord or Telegram. A selectable endpoint requires an audio driver. The base product therefore removes CABLE-A/intermediate hops and supports one separately installed final cable. Shipping a first-party SysVAD-derived driver would require a distinct signing, HLK, installer privilege and support decision.

## Accessibility and UI

The main shell uses WinUI resources, system typography, Mica, native controls and semantic brushes. Main status uses icon plus text, not color alone. OSD/overlay use functional red/blue/green/gray but also expose an icon, state text and automation name. UI Automation verified all primary pages and found zero focusable controls without accessible names. Full text scaling, high contrast, Reduce Motion, keyboard-only traversal and NVDA acceptance remain manual gates.

Primary references:

- [Structure a modern WinUI 3 desktop app](https://learn.microsoft.com/windows/apps/develop/ui/windows-app-sdk-app-structure)
- [Windows accessibility checklist](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-checklist)
- [Low latency audio](https://learn.microsoft.com/windows-hardware/drivers/audio/low-latency-audio)
- [Loopback recording](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording)
- [`PROCESS_LOOPBACK_MODE`](https://learn.microsoft.com/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode)
