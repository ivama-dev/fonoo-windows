# Third-party software and licenses

This is a summary of the dependencies currently identified in the Windows application. For details, read [NOTICE.md](NOTICE.md), [SDK-SOURCES.md](SDK-SOURCES.md), and the pinned dependency lock files. Each component retains its own copyright and license terms.

- **Liblinphone / LinphoneSDK.Windows 5.5.24** — Belledonne Communications and contributors. The upstream release's license text is GNU AGPLv3; NuGet package metadata instead says `GPL-3.0-or-later`. The discrepancy is recorded in [SDK-SOURCES.md](SDK-SOURCES.md); the upstream dependency tree and binary redistribution obligations require review.
- **Windows App SDK / WinUI** — Microsoft and contributors. License notices must be collected from the exact packages shipped.
- **MSAL.NET and MSAL extensions** — Microsoft and contributors; upstream MIT licensing applies to the relevant packages.
- **.NET runtime and dependencies** — Microsoft, .NET Foundation, and contributors. Relevant runtime licenses and notices apply if these components are bundled.
- **Windows SDK and build tools** — Microsoft. Build tooling is not relicensed as part of the Fonoo application.

Before a Store/MSIX or other binary release, generate a **complete, version-accurate bill of materials and third-party notice bundle** and provide the corresponding source/build information required by the licenses. This summary alone is not a complete release notice bundle.

The Fonoo application's own source license is `AGPL-3.0-or-later`; see [LICENSE](LICENSE). This license does not replace any third-party licenses.
