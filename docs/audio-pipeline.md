# Audio pipeline

## Current live graph

```text
JUCE WASAPI capture (device A, its own clock/rate)
  → bounded stereo SPSC bridge + interpolation/clock PLL
  → AudioProcessorGraph input
  → ordered VST3 / Mono→Stereo / Stereo→Mono nodes
  → 64-sample internal mute ramp → exact zero when settled
  → voice gain ──────────────────────────────────────────────┐
                                                              ├→ sum → JUCE WASAPI render
selected Windows render endpoint                              │   (final virtual endpoint)
  → event-driven WASAPI loopback at 48 kHz float stereo       │
  → bounded SPSC bridge → system gain + duck envelope ───────┘
```

The Flow page polls this graph's real snapshot: running/scanning/mute, input/system/output meters, selected system source, mixer mode, plugin count and final endpoint state. It is not a static illustration.

## Capture and render clocks

Capture and render are opened independently. This removed the original requirement that both devices expose a common sample rate. Hardware evidence:

```text
CABLE-B Output capture: 44,100 Hz, 441-frame callback
CABLE-B Input render:    48,000 Hz, 480-frame callback
```

`AsyncAudioBridge` preallocates 2×262,144 float samples, uses monotonically increasing atomic frame counters and never locks in `push`/`pop`. Render interpolation has a target prebuffer (35 ms for equal nominal rates, 100 ms for different nominal rates) and bounded proportional correction for clock drift. Underrun produces zeros; overrun drops incoming frames and increments a counter. The latest 60-second 44.1→48 kHz run passed with zero XRuns; the eight-hour soak remains open.

## VST3 and channels

JUCE's `AudioProcessorGraph` contains explicit I/O nodes and an ordered plugin chain. Mutations supported by the control contract are add, remove, move and bypass. Built-in adapters duplicate mono to stereo or average L/R to mono. Plugins restore `getStateInformation` blobs, order and bypass from engine state. Native editors live in owned JUCE document windows in the engine process.

Discovery searches JUCE VST3 defaults, `VST3_PATH` and user folders. One child process scans one bundle with SEH guarding, timeout, skip and retry. A low-priority audit hashes only loadable `.vst3` binaries inside each bundle, persists total bytes plus SHA-256, and forces a rescan on content change while ignoring mutable logs/resources.

## System loopback and mixer

`SystemLoopbackCapture` opens an active render IMMDevice by friendly name, requests 48 kHz stereo IEEE float and uses `AUDCLNT_STREAMFLAGS_LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`. Captured packets are deinterleaved into the system bridge. It never records to disk or sends audio over a network.

Modes:

- Voice: only post-VST/post-mute voice;
- System: only endpoint loopback;
- Both: sum of both sources.

Voice and system gains accept 0–4. Ducking compares post-mute voice RMS against a dBFS threshold, applies amount/attack/hold/release and reports its current gain. The current sum has no final limiter, so gains above unity can clip at a downstream endpoint/plugin.

When filtering is enabled, `IAudioSessionManager2` discovers process/session identity and `ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK)` supplies one fixed-slot capture per active process tree. Rules use executable path (session identity fallback), `excluded` and 0–400% gain. “Solo marcadas” defaults unknown apps to excluded; “todas salvo desmarcadas” defaults new apps to included. A two-second reconciliation timer starts/stops slots without rebuilding the voice graph. The render callback only reads 32 preallocated slots.

The final output also feeds a fixed SPSC FIFO. A low-priority worker applies a Hann-windowed 2048-point FFT and publishes 16 logarithmic 60 Hz–16 kHz bands through atomics. FFT work never runs in the render callback.

## Mute semantics

Mute lives after VST processing and before voice enters the mixer. A fixed 64-sample de-click ramp reaches exactly `0.0f`; `muteSettled` becomes true only at exact zero. System audio is intentionally unaffected. Independent meters report raw input, processed voice after VST/gate, system/apps and final output.

## Feedback prevention

There are two different hazards:

* a direct digital loop, where the selected system-capture endpoint is the same
  endpoint as the final render; and
* a Windows Listen route, where `CABLE Output` is listened through the selected
  playback endpoint and the engine loopbacks that endpoint back into the final
  `CABLE Input`.

The native host rejects both routes before opening system loopback. It reads the
read-only MMDevice Listen properties (`{24DBB0FC-9311-4B3D-9CF0-18FF155639D4}`)
and matches the capture/render pair by its normalized provider family; it does
not assume `CABLE-A` or `CABLE-B`. An enabled Listen route whose target is the
selected system endpoint (or the current Windows default) is blocked. If
Windows exposes an enabled Listen route but its target cannot be resolved, the
route is blocked conservatively. Listen aimed at another playback endpoint does
not disable intentional System/Both mixing.

The engine also polls this external setting while running. If it is enabled
after startup, system/process capture stops and the mixer falls back to Voice;
the snapshot/Diagnostics/Mixer InfoBar reports the reason. Input & Plugins
starts in Voice by default and only preserves a prior System/Both choice when
the user checks the explicit “preserve PC mixing” option. Stable MMDevice IDs
remain authoritative; current friendly names are shown in snapshots and
diagnostics. All failed pipeline changes use the existing neutral-graph
transaction and restore the prior device/mixer/plugin/mute state.

The process-filter path uses the same guard before allocating per-process
captures. It cannot safely treat the Windows Listen receiver as an ordinary
application rule (the receiver may live in the Windows audio service), so a
proven Listen route blocks System/Both regardless of include/exclude mode.

## Virtual endpoint conclusion

Discord, Telegram and games enumerate audio driver endpoints. UnifiedAudio can produce the signal but cannot expose a user-mode process as a microphone. The base installation therefore uses at most one separately installed final virtual cable. It neither bundles nor silently installs a third-party driver. A first-party driver remains a separate signing/HLK/product decision.
