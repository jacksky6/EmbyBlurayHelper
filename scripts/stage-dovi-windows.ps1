param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $project 'artifacts/tools/libdovi-3.4.0'
$archive = Join-Path $cache 'libdovi-3.4.0-x86_64-pc-windows-msvc.zip'
$null = New-Item -ItemType Directory -Path $cache -Force
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -UseBasicParsing 'https://github.com/quietvoid/dovi_tool/releases/download/2.3.4/libdovi-3.4.0-x86_64-pc-windows-msvc.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'D3479F756C81CBB2B77DFEEFD070CAF06593EB30DA10876915F3BD3E1EBCC2E3') { throw 'libdovi archive SHA256 mismatch; preserve and inspect the download.' }
$library = Join-Path $cache 'dovi.dll'
if (-not (Test-Path -LiteralPath $library)) { Expand-Archive -LiteralPath $archive -DestinationPath $cache }
if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -ne 'D4AC7574B6A0B7DB3CEB86AEA0A801D7CA1CDBE88580DF1F793CC9F82BF0BEA7') { throw 'libdovi DLL SHA256 mismatch.' }
$tools = Join-Path $Destination 'tools'
$null = New-Item -ItemType Directory -Path $tools -Force
Copy-Item -LiteralPath $library -Destination (Join-Path $tools 'dovi.dll') -Force
Copy-Item -LiteralPath (Join-Path $project 'THIRD-PARTY-LICENSES/libdovi-MIT.txt') -Destination (Join-Path $tools 'libdovi-MIT.txt') -Force
Write-Output 'Staged libdovi 3.4.0 Windows x64 and its MIT license.'
