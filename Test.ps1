[CmdletBinding()]
param([switch]$SkipDpapi, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$suites = @('AccountFlow.Tests','DeviceProfiles.Tests','PhoneData.Tests','HistorySync.Tests','Presence.Tests','DesktopData.Tests','MicrosoftContacts.Tests','AudioData.Tests')
foreach ($suite in $suites) {
    $arguments = @('run','--project',(Join-Path $PSScriptRoot "tests\$suite"))
    if ($NoRestore) { $arguments += '--no-restore' }
    if ($SkipDpapi -and $suite -in @('DesktopData.Tests','MicrosoftContacts.Tests')) { $arguments += @('--','--skip-dpapi') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Test suite failed: $suite" }
}
