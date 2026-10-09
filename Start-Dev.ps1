[CmdletBinding()]
param([switch]$Restore, [switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer was not found. Install the WinUI development workload first.'
}
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio x64 MSBuild was not found.' }

$project = Join-Path $PSScriptRoot 'Fonoo.Windows\Fonoo.Windows.csproj'
$arguments = @(
    $project,
    '/p:Configuration=Debug', '/p:Platform=x64', '/p:RuntimeIdentifier=win-x64',
    '/p:WindowsPackageType=None', '/p:WindowsAppSDKSelfContained=true',
    '/p:FonooDesignPreview=false',
    '/p:AppxPackageSigningEnabled=false', '/v:minimal', '/nologo'
)
if ($Restore) { $arguments += '/restore' }
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
if (-not $BuildOnly) {
    $executable = Join-Path $PSScriptRoot 'Fonoo.Windows\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Fonoo.Windows.exe'
    Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
}
