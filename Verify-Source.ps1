[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$patterns = @{
    'private key' = '-----BEGIN(?: [A-Z]+)* PRIVATE KEY-----'
    'GitHub credential' = '\b(?:gh[opusr]_|github_pat_)[A-Za-z0-9_]{20,}'
    'OpenAI credential' = '\bsk-(?:proj-|svcacct-)[A-Za-z0-9_-]{20,}'
    'AWS access key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'literal bearer' = 'Bearer\s+[A-Za-z0-9_-]{32,}'
}
$files = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|\.vs|bin|obj|AppPackages|qa|artifacts)[\\/]'
})
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    if ($file.Extension -match '^\.(protected|pfx|p12|p8|key|pem|sqlite|db|pcap|pcapng|dll|exe|pdb|nupkg|log)$' -or $file.Name -match '^\.env(?:\.|$)|^id_(rsa|ed25519)|secrets?\.json$') {
        throw "Private/generated file in source snapshot: $relative"
    }
    if ($file.Extension -in @('.png','.ico','.wav')) { continue }
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($category in $patterns.Keys) { if ($text -match $patterns[$category]) { throw "Source audit requires review ($category): $relative" } }
    if ($text -match '(?i)b[a]resip') { throw "Unexpected alternative SIP implementation: $relative" }
}
Write-Output "PASS: $($files.Count) source/resource files; no blocked credentials, private/runtime files or alternative SIP implementation found."
