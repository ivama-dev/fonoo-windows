# Microsoft Store packaging

The official Fonoo package identity is declared in `Fonoo.Windows/Package.appxmanifest`. Forks must use their own Store identity and signing configuration. The application is x64-only in this revision.

```powershell
./Build-StoreBeta.ps1 -Restore
./Verify-StoreBeta.ps1 -UploadPath "C:\path\Fonoo.Windows_0.1.0.0_x64.msixupload" -ReportPath "C:\path\package-check.json"
```

The build creates an unsigned Release/x64 Store upload candidate, with the .NET runtime bundled and the Windows App SDK as a Store framework dependency. Build outputs, signing keys and SDK DLLs must not be committed. The local verifier checks identity, runtime resources and exclusion of development/private files; it does not replace installation, certification or a complete redistribution review.

Before distributing a binary:

1. Publish the exact application commit and corresponding liblinphone/native dependency sources, licenses and build instructions for that binary, as described in [SDK-SOURCES.md](SDK-SOURCES.md).
2. Verify signed MSIX installation, remembered login, Microsoft/WAM contacts, incoming/outgoing audio, headset changes, tray behavior, restart and upgrades on a clean Windows account.
3. Run the Windows App Certification Kit and complete Store metadata, privacy information, age rating and accurate synthetic screenshots.
4. Upload the reviewed package and complete Microsoft certification. For private testing, select a private known-user audience; a hidden public listing is not a private beta.

This source publication does not upload an MSIX or submit certification. Microsoft references: [visibility](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options), [known-user groups](https://learn.microsoft.com/en-us/windows/apps/publish/create-customer-groups) and [package upload](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages).
