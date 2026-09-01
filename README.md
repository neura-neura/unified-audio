# UnifiedAudio

UnifiedAudio is a Windows 11 WinUI 3 desktop application for routing microphone and system audio through VST3 effects, mute controls, endpoint profiles, hotkeys, feedback sounds, and local automation.

## Features

- Native WinUI 3 interface with adaptive navigation and system theme support.
- WASAPI shared/low-latency capture and render paths with queue and XRuns diagnostics.
- Out-of-process VST3 scanning, caching, ordering, bypass, and editor support.
- Voice, PC audio, and mixed modes with configurable gain and ducking.
- Global keyboard/mouse hotkeys, push-to-talk, push-to-mute, hybrid mode, and feedback sounds.
- Transactional profiles for input/output endpoints, mixer state, mute state, and per-application routing.
- Process Loopback capture for selected Windows applications.
- Persistent settings, rollback on activation failures, logs, diagnostics, tray mode, and startup options.
- English, Spanish, and Simplified Chinese UI resources.

UnifiedAudio does not record, retain, or upload audio. It does not include third-party virtual-audio drivers; install one final virtual endpoint such as VB-CABLE when an application needs to consume the mixed microphone path.

## Installation

Download the Windows installer from the [latest release](../../releases/latest), then run `UnifiedAudioSetup-0.1.0-x64.exe`. The per-user installer installs under `%LOCALAPPDATA%\\Programs\\UnifiedAudio` and keeps settings and logs when uninstalled.

## Build from source

Requirements:

- Windows 11 x64
- Visual Studio 2022 with Desktop development with C++ and the Windows SDK
- .NET SDK 10.0.400
- CMake
- NSIS 3 for the installer

```powershell
./scripts/build-release.ps1
```

The release script builds the native engine and WinUI application, runs native and .NET tests, publishes self-contained binaries, and writes the installer plus SHA-256 metadata to `artifacts/`.

For a quick development build:

```powershell
dotnet build ./UnifiedAudio.slnx -c Debug
dotnet test ./tests/UnifiedAudio.Core.Tests/UnifiedAudio.Core.Tests.csproj -c Debug --no-build
```

## Architecture and limitations

See `docs/architecture.md`, `docs/audio-pipeline.md`, and `docs/feature-parity-matrix.md`. The project intentionally keeps generated binaries, upstream snapshots, and build directories out of Git; releases contain the distributable installer.

## License

See [LICENSE](LICENSE) and `docs/licensing-and-third-party.md`.
