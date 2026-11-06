param(
    [string]$Version = '0.2.0-23',
    [string]$DotnetPath = 'E:\ProgramFiles\dotnet-8\dotnet.exe',
    [string]$Distribution = 'Ubuntu-26.04',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (-not $SkipPublish) {
        if (-not (Test-Path -LiteralPath $DotnetPath)) { throw "Missing .NET 8 SDK: $DotnetPath" }
        & $DotnetPath --version
        & $DotnetPath build src/EmbyBlurayHelper.EmbyPlugin -c Release -o artifacts/plugin
        if ($LASTEXITCODE -ne 0) { throw 'Emby plugin build failed' }
        & $DotnetPath publish src/EmbyBlurayHelper.App -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/app/linux-x64
        if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed' }
    }
    $linuxProject = & wsl.exe -d $Distribution --exec wslpath -u $projectRoot
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the project path inside WSL' }
    $linuxProject = $linuxProject.Trim()
    & wsl.exe -d $Distribution --cd $linuxProject --exec python3 scripts/package-synology.py --version $Version
    if ($LASTEXITCODE -ne 0) { throw 'SPK packaging failed' }
} finally { Pop-Location }
