# Copyright and third-party software

Copyright (C) 2026 Fonoo contributors.

The original Windows client code and accompanying project material in this repository are provided under GNU Affero General Public License version 3 or (at your option) any later version, without warranty. See `LICENSE`. Fonoo names and marks do not grant an endorsement or a right to present modified software as an official Fonoo release.

Dependencies retain their own copyrights and licenses; the application's license does not relicense third-party software:

- **Linphone SDK 5.5.24**, Belledonne Communications and upstream contributors. The exact upstream tag includes AGPLv3; its NuGet metadata instead reports GPL-3.0-or-later. See `SDK-SOURCES.md`. Components, codecs and native transitive dependencies require their own notices/corresponding sources when redistributed.
- **Microsoft Windows App SDK / WinUI**, Microsoft and contributors; Microsoft-published package/source license terms apply.
- **MSAL.NET and MSAL extensions**, Microsoft and contributors, MIT-licensed upstream projects.
- **.NET runtime/SDK**, .NET Foundation, Microsoft and contributors; original runtime and third-party notices apply when bundled.
- **Windows SDK build tooling**, Microsoft; development-tool terms apply. Its presence in NuGet restore does not place the tool under this application's license.

Native SDK/runtime binaries are restored from their official feeds and are not included in this source repository. The `.NET` dependency lock file identifies the resolved packages. A binary release needs its own complete third-party inventory and corresponding-source bundle; this notice is not a claim that those release artifacts have already been prepared.
