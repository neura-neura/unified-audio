# Upstream snapshots audited

Audit date: 2026-08-15. Every repository was cloned from its remote default branch before design work started.

| Project | URL | Default branch | Audited SHA | License found in repository |
| --- | --- | --- | --- | --- |
| MicVST | https://github.com/philipz794/MicVST.git | `main` | `5b1403fce2276298a89d58a95903927ae24ed146` | GPL-3.0 |
| AHK_MicMute | https://github.com/neura-neura/AHK_MicMute.git | `master` | `bc5e0e6d8b27b034daf4a2742ce062ef06d4e41b` | Unlicense; bundled dependencies have additional terms |
| mic-mix | https://github.com/neura-neura/mic-mix.git | `main` | `d99632927e7590958e09322b4f7129f0de38b450` | No license file or package license field found; behavior is reimplemented, not copied |
| audio-profiles | https://github.com/neura-neura/audio-profiles.git | `main` | `e27cc2ea5ae1798b3ad2fe7a9adb45769ce5c077` | MIT |

The ignored `upstream/` directory contains the exact local checkouts used for the audit. Build evidence for those snapshots is recorded in [testing.md](testing.md).
