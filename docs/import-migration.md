# Import and migration

The Settings page discovers installed source configurations and always shows a preview plus warnings before mutation. Import never happens at startup merely because an old file exists.

Import never writes to or deletes the source application's configuration. It produces a preview, records warnings and writes UnifiedAudio state only after confirmation.

## Sources

- MicVST: `%APPDATA%\MicVST\config.xml`. The engine imports physical input hint, custom plugin folders, VST order, bypass and binary state blobs. It deliberately keeps the current final render instead of restoring CABLE-A.
- AHK_MicMute: `config.json` beside the detected Scoop installation. Profiles, microphone hints, linked apps, AFK and OSD preference are imported. AHK hotkey expressions are warned and left for native re-entry; actions/scripts are never trusted implicitly.
- mic-mix: `%APPDATA%\com.neura.micmix\settings.json`. Stable endpoint IDs, mode, gains, ducking, startup/minimized flags and exclusion/volume rules are imported into a unified profile. Exclusions map to “todas salvo las desmarcadas”.
- Audio Profiles: `%LOCALAPPDATA%\AudioProfiles\settings.json`. Profiles, role assignments and hotkeys deserialize into the superset model. Imported profiles receive new IDs and collision-safe names.

## Collision policy

Existing UnifiedAudio profiles are never overwritten by name. A SHA-256 fingerprint of absolute source path plus content prevents accidental duplicate imports. Conflicting hotkeys remain disabled with a visible warning. Missing endpoints/plugins are retained by ID/path and display name so they can reconnect later. The current state is saved first; the following atomic save produces the recoverable backup. Source files are read-only throughout.
