# Windows Linphone binding probe

This standalone diagnostic checks the official C# wrapper and production SipEngine on .NET 10/x64. It is not referenced by the application solution. Default and `--audio` modes create no account/call or microphone capture. `--calls` uses isolated loopback SIP and synthetic file audio, with no production account or physical audio device.

## Reproduce

Download and extract the official [LinphoneSDK.Windows 5.5.24 package](https://gitlab.linphone.org/api/v4/projects/411/packages/nuget/download/LinphoneSDK.Windows/5.5.24/linphonesdk.windows.5.5.24.nupkg) outside this repository. Package SHA-256 observed on 2026-09-29: `E3A8AF43C82AF5BE5A75DBD47D6F82C8C64C54E862A8F0677B3896F746BDADF0`.

Run from the Windows directory, replacing the SDK directory:

```powershell
dotnet run --project diagnostics/LinphoneProbe/LinphoneProbe.csproj --property:LinphoneSdkRoot="C:\path\to\extracted-sdk"
```

The SDK binaries and resources are intentionally not committed. The diagnostic copies native x64 DLLs, WASAPI and WebRTC audio plugins and runtime resources. OpenH264 is omitted because video is outside scope and that plugin requires an additional DLL. The vendor's default targets copy considerably more content, including debug symbols; production packaging needs a separate dependency audit.

## Observed result, 2026-09-29

All three create/start/iterate/stop cycles completed on one dedicated thread. Each reported SDK 5.5.24, six audio devices, three recording-capable and three playback-capable devices. No missing-plugin or grammar errors remained. Warnings about disabled databases and fallback ringtone remain in this diagnostic configuration.

Setting `Factory.TopResourcesDir` before core creation is required: omitting the supplied BELR grammars caused a native fatal error in the first experiment. This must become a checked packaging prerequisite in the app.

Not established by this test: audio capture/playback quality, TLS/SRTP interoperability, hotplug, suspend/resume, CPU/memory budgets or lifetime safety under sustained use. The wrapper exposes native lifetime through finalization rather than public IDisposable; production shutdown/callback ownership needs review. This package contains x64 binaries; do not infer ARM64 support from the app's target list.

The package metadata declares `GPL-3.0-or-later`, while the exact SDK tag's license is AGPLv3. This Windows project uses `AGPL-3.0-or-later`; see [SDK-SOURCES.md](../../SDK-SOURCES.md). The diagnostic is not a Store release approval.

## Desktop call checks, 2026-10-05

```powershell
dotnet run --project diagnostics/LinphoneProbe --no-restore -p:LinphoneSdkRoot="C:\path\to\extracted-sdk" -- --audio
dotnet run --project diagnostics/LinphoneProbe --no-restore -p:LinphoneSdkRoot="C:\path\to\extracted-sdk" -- --calls
```

`--audio` runs three production-engine lifecycle cycles and checks actual route enumeration/refresh, invalid-device rejection and shutdown without starting media capture. `--calls` creates three distinct local identities on ephemeral UDP loopback ports, uses mandatory SRTP with synthetic audio, and checks acceptance, mute, DTMF, hold/resume, consultation/return, attended/direct transfer, rejected transfer recovery, DND and one-time call-history emission. The SDK requires serialized global initialization before those independent workers start. Its native REFER and Replaces callbacks are covered by the transfer checks; the remaining remote parties must stay active after Fonoo leaves.

Loopback setup and optional `--trace` output are compiled only into this diagnostic. They do not change the production requirement for authenticated TLS registration or enable production SIP logging. These tests passed locally; they do not establish interoperability with the live PBX or audible hardware quality.
