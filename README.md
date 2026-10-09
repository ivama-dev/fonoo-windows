# Fonoo for Windows

Native Windows softphone built with C#, WinUI 3 and **liblinphone SDK 5.5.24**. The client uses liblinphone exclusively; its provisioning request declares `sip_engine: "liblinphone"`. All call controls use actual SDK callbacks.

## Features

- Fonoo email-code/password sign-in, optional DPAPI-protected remembered login and company/extension provisioning.
- Dialpad, incoming/outgoing SRTP audio, mute, hold/resume and direct/consultative transfer.
- Account/company-scoped favorites, synchronized call history with All/Missed filters and shared single-call/list deletion.
- Windows/vCard contacts, read-only personal Outlook/Microsoft 365 mailbox contacts and an internal team directory.
- Shared PBX telephone presence in team/internal favorites, scoped counterpart names and a 15-second Unknown fallback.
- Audio device selection, measured microphone levels, local hearing/speaker tests and a compact call window.
- Native notification-area integration, hide-on-close, network/resume registration refresh and one app instance.

This repository contains the Windows application, tests, assets and build scripts. The Fonoo backend and Apple/Android clients are separate projects. Using Fonoo services requires an active Fonoo account, an assigned extension and company permissions. Publishing the client does not grant access to those services.

## Build

Use Windows 10 19041 or later, Visual Studio 2026 with WinUI application development, .NET SDK 10.0.401 and the Windows SDK 26100. The native SDK integration supports **x64**. ARM64 is explicitly rejected until a matching runtime is integrated.

Open `Fonoo.Windows.slnx` and select x64, or run:

```powershell
./Start-Dev.ps1 -Restore -BuildOnly
./Start-Dev.ps1
```

Dependencies are pinned in `Fonoo.Windows/packages.lock.json`; `NuGet.Config` maps the official Linphone feed. The first restore downloads the native SDK package. SDK binaries are not committed. Choose **Fonoo beenden** before rebuilding; closing the main window keeps the app in the notification area.

For a synthetic UI preview, build Debug with `-p:FonooDesignPreview=true`. The preview is visibly labeled, uses isolated test storage and creates no real account, SIP registration or call. Release builds cannot enable it.

## Tests

```powershell
./Test.ps1
./Verify-Source.ps1
```

The tests use synthetic identities, responses and audio. Desktop/Microsoft contact storage checks need a normal Windows user context for DPAPI; `./Test.ps1 -SkipDpapi` explicitly skips those checks in a restricted command sandbox. [Native SDK diagnostics](diagnostics/LinphoneProbe/README.md) additionally cover local SRTP call/transfer scenarios.

See [audio](AUDIO-INTEGRATION.md), [desktop behavior](DESKTOP-UI-2026-10-05.md), [Outlook contacts](MICROSOFT-CONTACTS.md), [telephone presence](TEAM-PRESENCE.md) and [call history](CALL-HISTORY.md). Hardware hotplug, sleep/network recovery, high DPI/accessibility, resource budgets and signed MSIX acceptance remain release checks.

## License and releases

Fonoo Windows is licensed under **AGPL-3.0-or-later**; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md). The exact upstream SDK tag's license is AGPLv3, despite the NuGet package metadata reporting GPLv3. [SDK source and dependency information](SDK-SOURCES.md) records the pinned release and this discrepancy.

The current publication is source code. Microsoft Store distribution additionally requires the corresponding SDK/dependency sources and build information for the shipped binaries, third-party notices, a signed-package acceptance test and Store certification. Source publication itself is not Store approval. [Store build instructions](STORE-BETA.md) describe the packaging process.

The Microsoft client ID and Store/package identities in the source are public application identifiers. No client secret, SIP password, bearer/refresh token, customer contact/history file, SSH key or signing key belongs in this repository.
