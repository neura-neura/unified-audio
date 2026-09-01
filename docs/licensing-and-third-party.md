# Licensing and third-party software

This repository is licensed GPL-3.0-or-later. The choice is intentional because MicVST is GPL-3.0 and its JUCE-based host is the behavioral/source reference for VST3 parity. A closed-source distribution would require a separate JUCE commercial license and a fresh legal review.

| Component/reference | License/status | Decision |
| --- | --- | --- |
| MicVST source | GPL-3.0 | Code may be adapted only within this GPL-compatible distribution; preserve notices and identify modifications. |
| JUCE 8.0.13 | GPL/AGPL path or commercial JUCE terms, depending on distribution | Use under the GPL-compatible path for this repository. Do not imply that VST3's MIT license overrides JUCE terms. |
| Steinberg VST3 SDK 3.8+ | MIT | Host only VST3; do not include VST2 headers. Preserve MIT notice. |
| AHK_MicMute source | Unlicense | Behavior and small implementation ideas may be reimplemented; third-party bundled libraries are reviewed separately. |
| audio-profiles source | MIT | WinUI/profile code may be adapted with its copyright and MIT notice. |
| mic-mix source | No repository license found at audited SHA | Do not copy source or assets. Reimplement documented behavior from Windows APIs and observed behavior. |
| Windows App SDK / Windows SDK | Microsoft terms | Consume through official packages/toolchain. |
| VB-CABLE or another final endpoint | Third-party driver terms | Detect and guide the user; do not redistribute in the installer without explicit vendor permission. |
| BASS | Proprietary/freeware terms vary by use | Not included. Feedback sounds use Windows audio APIs. |
| Aura SDK | Vendor terms and optional local dependency | Adapter remains optional and is loaded only when the user enables it and the licensed runtime is present. |
| Voicemeeter Remote API | Vendor terms | Optional adapter; no vendor binaries are redistributed without permission. |

Primary license references:

- Steinberg, [VST3 license](https://steinbergmedia.github.io/vst3_dev_portal/pages/VST%2B3%2BLicensing/VST3%2BLicense)
- JUCE, [JUCE 8 End User Licence Agreement](https://juce.com/legal/juce-8-licence/)
- Microsoft, [driver signing options and best practices](https://learn.microsoft.com/windows-hardware/drivers/dashboard/driver-signing-offerings)

This document is an engineering inventory, not legal advice. Before public binary distribution, the release gate must inventory resolved package licenses, confirm the chosen JUCE path, confirm plugin trademark wording and review optional vendor integrations.
