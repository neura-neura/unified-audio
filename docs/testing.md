# Testing and verification

## Audited-upstream spikes (2026-08-15)

| Gate | Command | Result |
| --- | --- | --- |
| WinUI 3 baseline Debug x64 | `dotnet build upstream/audio-profiles/src/AudioProfiles/AudioProfiles.csproj -c Debug -p:Platform=x64` | PASS, 0 warnings, 0 errors |
| Rust/WASAPI/application-loopback compile | `cargo check --locked` in `upstream/mic-mix/src-tauri` | PASS |
| JUCE/VST3 configure | VS 2022 CMake configure of `upstream/MicVST` | PASS |
| JUCE/VST3 Release x64 | CMake build targets `MicVST MicVSTTests` | PASS |
| MicVST unit tests | `MicVSTTests.exe` | PASS, exit code 0 |

## Automated release gates

- Build Debug and Release x64 with warnings treated as errors for UnifiedAudio-owned code.
- Unit tests: model normalization, atomic store+backup, hotkey parsing/conflicts, profile preflight/order/rollback/partial result, channel maps, mute exact-zero, gains/duck envelope, bounded queues and scan cache fingerprints.
- Contract tests: version negotiation, frame bounds, stale graph revision, reconnect and meter-event coalescing.
- Native tests: mono/stereo conversion, VST bypass/reorder/state, failed/hung scan child, no allocation in callback instrumentation, silence on underrun, counters and clean cancellation.
- Integration/self-test: enumerate stable MMDevice IDs, switch and restore each Windows role, start/stop/restart graph, device loss/reconnect and feedback rejection.
- UI Automation: navigation, accessible names/statuses, focus order, 200% text, contrast theme and keyboard-only core flows.
- Publish self-contained x64 and build the per-user NSIS installer.

## UnifiedAudio evidence collected (2026-08-16)

| Gate | Evidence | Result |
| --- | --- | --- |
| Debug x64 | `dotnet build UnifiedAudio.slnx -c Debug` | PASS, 0 warnings/errors |
| Release x64 | `scripts/build-release.ps1` | PASS, 0 warnings/errors |
| Core tests | xUnit v3 Release | PASS, 50/50 |
| Native tests | `UnifiedAudioEngineTests.exe` | PASS, exit 0 |
| Feedback-loop policy | Native pure tests for direct endpoint, standard-cable Listen target, unknown target, and intentional Listen-to-other-endpoint mix | PASS, 5/5 scenarios; no live endpoint mutation |
| VST3 binary fingerprints/cache | same-size/same-mtime replacement; mutable non-binary content; cache round-trip; corrupt-primary `.bak` recovery | PASS; 7 installed bundle hashes persisted; backup recovery 7/7 |
| Pipeline/profile rollback | invalid render endpoint and missing plugin preset | PASS: `AtomicRollback=True`, `ProfilePluginRollback=True` |
| Engine crash recovery | force-stop only the adopted Debug host while Flow snapshots run | PASS: one replacement host, shell responsive, no duplicate host after serialized connect; circuit timing tests 2/2 |
| External endpoint self-test | Current default endpoint mute toggle plus mute/volume restore in finally | PASS |
| Default-follow source self-test | Windows default capture is CABLE-B; saved physical Ugreen fallback plus physical system output | PASS: resolver selected physical fallback/input and current physical output without mutation |
| Contract/device/VST smoke | `scripts/smoke-engine.ps1` | PASS: v1, 8 inputs, 12 outputs, 10 catalog entries, add/bypass/remove |
| Independent clocks | CABLE-B capture/render | PASS: capture 44.1 kHz → graph 48 kHz |
| Mute settled | smoke after 250 ms | PASS: `muteSettled=true` |
| Endpoint loopback | Realtek/Ugreen render source, mode Both | PASS: `systemCaptureRunning=true` |
| Feedback rejection | engine validation | PASS when system source equals final render |
| Windows Listen guard | Installed self-test read-only MMDevice probe plus feedback-policy tests; no Listen/default mutation | PASS: `available=True`, `captureEndpoints=8`, `enabled=0`, `unknown=3`; Both was safely blocked/fell back because consumer state was unresolved, with no user process closed |
| WinUI visual/accessibility tree | Computer Use against Debug app | PASS for native navigation, states, controls and error InfoBars |
| WinUI 2026-08-16 QA | UI Automation + desktop capture: all navigation pages, Mute, Settings, profile editor | PASS layout/focus; English all-page scan reports `NO_SPANISH_UIA_TEXT` |
| Flow/sidebar/1120 migration | Flow is the default route; adaptive NavigationView sidebar; InitialWindowWidth=1120; endpoint, volume and color pickers | PASS implementation and navigation scan; clean NVDA/high-contrast/keyboard and mixed-DPI visual gates remain manual |
| Localization restart QA | select Simplified Chinese and Spanish from Settings, restart, verify eight navigation routes plus dynamic Flow, restore System | PASS for zh-CN and es; app responsive, preference restored |
| Keyboard focus accessibility audit | enumerate focusable UI Automation descendants on all eight pages | PASS: 0 named-control violations after labeling refresh/theme/language controls |
| Main hotkey live QA | injected physical Numpad scan, hidden window, wildcard off/on with extra Shift, global right-mouse capture | PASS: state True then False settled on each toggle; QA binding restored to NumpadPgDn |
| Feedback output/overlay persistence | MediaPlayer output ComboBox, volume/color pickers, topmost circle overlay, opacity/scale/lock/locate controls and per-monitor map | PASS implementation/static Release control presence; real click-through and opacity code paths present; audible alternate-device and mixed-DPI/fullscreen hardware checks remain manual |
| Factory reset | exact shell/engine allowlist, recoverable manifest, fresh settings preserving startup, injected initialization failure rollback; Advanced UI inspected without invoking reset | PASS 2/2; UI Automation found accessible `Factory reset`, `Invoked=False` |
| Installer generation | self-contained .NET/Windows App SDK + NSIS x64; coreclr.dll present, one included framework, no framework dependency in runtimeconfig | PASS: 75,661,199 bytes; runtimeconfig has no framework dependency |
| Installer hash | SHA-256 sidecar, recomputed from final binary | `C2DBDD3537FF6FF2680B1285B2CE4111DEA0CE51C1426670F5452B965127138D` |
| Install/launch/self-test/smoke/uninstall | unique `UnifiedAudio-QA-LoopOverlay-20260816` LocalAppData target | PASS: `/S /LAUNCH` produced exactly one app and one host with a responsive window; installed self-test PASS with six default IDs exact and external mute/volume restored; Listen probe remained read-only (`enabled=0`, `unknown=3`). Voice smoke used Ugreen input → exact standard `CABLE Input (VB-Audio Virtual Cable)`, Voice, kHs Gain, 120 s scan wait and 5 s run; `AtomicRollback=True`, `ProfilePluginRollback=True`, `MuteSettled=True`, `OutputPeak=0`, `0/0` XRuns. Both/System Ugreen was not forced: guard PASS blocked it because the probe had three unknown consumers. `/S` uninstall removed target, QA registry and QA processes; normal-install registry plus settings/config/cache and all six Windows default roles were restored byte/hash-exact. |

### Required hardware chain result

Command:

```powershell
./scripts/smoke-engine.ps1 `
  -InputName "Micro Ugreen Soundcard (KT USB Audio)" `
  -OutputName "CABLE Input (VB-Audio Virtual Cable)" `
  -MixerMode Both `
  -SystemDeviceName "Speaker Ugreen Soundcard (KT USB Audio)" `
  -PluginQuery "kHs Gain" `
  -RunSeconds 15
```

Result from the earlier Both hardware gate: installed host running, kHs Gain loaded, Ugreen capture/graph at 48 kHz, standard CABLE Input (VB-Audio Virtual Cable) render, Both mode (MixerMode=2), SystemCaptureRunning=True, MuteSettled=True, exact OutputPeak=0, and 0 underruns/0 overruns. The command selected the standard CABLE endpoint by exact name; CABLE-A/B were not selected. The current loop/overlay acceptance intentionally ran Voice and refused to force Both after the read-only Listen probe returned `unknown=3`.

A prior Voice-only run with the same Ugreen input, kHs Gain and CABLE-B final produced nonzero input peak 0.000824, muteSettled=true and final OutputPeak=0, proving exact post-VST voice silence after the gate; the current release acceptance used the standard CABLE final.

The latest mismatched-clock run used `CABLE-B Output` at 44.1 kHz into Ugreen render at 48 kHz for 60 seconds, mute settled at exact zero, and recorded `0` underruns / `0` overruns. The eight-hour soak remains open and is not implied by this pass.

Process filtering was exercised in both policies against live sessions. Include-only captured ts3client_win64; exclusion mode marked that process excluded while other active apps produced system/final signal. Both earlier 5-second Ugreen→CABLE-B runs reported 0 XRuns. Snapshots preserved the rule, exclusion flag, gain, process-filter mode, 16 spectrum bands and stable input/output IDs.

## Reproducible manual matrix

1. Record original Console/Multimedia/Communications defaults for render and capture.
2. Select `Micro Ugreen Soundcard (KT USB Audio)` by endpoint ID; record the friendly name only as evidence.
3. If present, import the former CABLE-A→CABLE-B chain and prove the new graph uses only one final virtual render/capture pair.
4. Exercise VST mono/stereo, reorder, bypass, editor and restart restoration with a known plugin; repeat with a crashing and a hanging scan fixture.
5. Verify voice, PC and both modes; 0–400% gains; duck amount/attack/hold/release; per-app exclusion/gain; newly appearing and disappearing sessions.
6. Bind `NumpadPgDn`; verify toggle, mute/unmute split, PTT, inverted PTM, hybrid short/long press and release delay with the shell hidden.
7. Confirm settled mute output is exact digital zero and no plugin tail reaches the mixer.
8. Unplug/reconnect the Ugreen device, change Windows defaults while running and try incompatible sample rates.
9. Exercise tray, startup wait/delay, single instance, clean exit and default restoration.
10. Exercise overlay/OSD on multiple monitors, mixed DPI, fullscreen and borderless; move, lock, resize and reconnect displays.
11. Test System/Light/Dark/contrast themes, keyboard only, Narrator and NVDA, text-size slider, display zoom and Reduce Motion.
12. Install/uninstall from a clean standard-user account, then run update and migration tests without destroying source configurations.
13. Run at least eight hours while collecting CPU, private bytes, glitches, underruns, overruns, graph latency and handle counts.

Manual results must record machine, OS build, endpoint IDs, driver versions, plugin versions, start/end timestamps and logs. Clean standard-user install, audible receiving-app confirmation, physical hotkeys/long-duration State True/False, NVDA/high-contrast/keyboard, mixed-DPI/fullscreen overlay, and eight-hour soak remain open; a row without evidence remains open in the parity matrix.
