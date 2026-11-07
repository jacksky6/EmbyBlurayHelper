param([switch]$Apply, [switch]$HelperOnly, [string]$StagedPath)

# Workstation-only update. Without -Apply this performs read-only preflight checks.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $projectRoot
$live = Join-Path $projectRoot 'artifacts\app\win-x64'
$staged = if ([string]::IsNullOrWhiteSpace($StagedPath)) {
    Join-Path $projectRoot 'artifacts\checks\iso-app-batch-updated'
} else {
    (Resolve-Path -LiteralPath $StagedPath).Path
}
$helperExe = Join-Path $live 'EmbyBlurayHelper.exe'
$pluginSource = Join-Path $projectRoot 'artifacts\plugin\EmbyBlurayHelper.Plugin.dll'
$pluginTarget = Join-Path $workspace 'programdata\plugins\EmbyBlurayHelper.Plugin.dll'
$embyDirectory = Join-Path $workspace 'embyserver-win-x64-4.10.2.0'
$embyExe = Join-Path $embyDirectory 'EmbyServer.exe'
$embyDll = Join-Path $embyDirectory 'EmbyServer.dll'
$settingsFile = Join-Path $live 'data\settings.json'
$embyBase = 'http://127.0.0.1:8096'

foreach ($file in @($helperExe, $pluginSource, $pluginTarget, $embyExe, $embyDll,
                    $settingsFile, (Join-Path $staged 'EmbyBlurayHelper.exe'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
}
$config = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($config.embyApiKey)) { throw 'The saved Emby API key is empty.' }
$embyUri = [Uri]$config.embyUrl
if (-not $embyUri.IsLoopback -or $embyUri.Port -ne 8096) {
    throw 'This script only updates the local Emby on port 8096.'
}
if (([Uri]$config.managementUrl).Port -ne 8099 -or ([Uri]$config.proxyUrl).Port -ne 8097) {
    throw 'Expected the main Helper ports 8099 and 8097. No configuration was changed.'
}
$headers = @{ 'X-Emby-Token' = $config.embyApiKey }
if ($HelperOnly -and (Get-Item -LiteralPath $pluginSource).VersionInfo.FileVersion -ne (Get-Item -LiteralPath $pluginTarget).VersionInfo.FileVersion) {
    throw 'The plugin also needs updating. Omit -HelperOnly to perform the full update.'
}

function Find-ExactProcess([string]$executable) {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $executable } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}

function Start-LocalServices {
    if (-not (Find-ExactProcess $helperExe)) {
        $null = Start-Process -FilePath $helperExe -ArgumentList '--serve' -WorkingDirectory $live -WindowStyle Hidden
    }
    if (-not $HelperOnly -and -not (Find-ExactProcess $embyExe)) {
        # Match the startup arguments already verified on this workstation.
        $quotedDll = '"' + $embyDll + '"'
        $null = Start-Process -FilePath $embyExe -ArgumentList @($quotedDll, $quotedDll, '-noautorunwebapp') `
            -WorkingDirectory $workspace -WindowStyle Hidden
    }
}

Write-Output "Helper source: $staged"
Write-Output "Helper target: $live"
Write-Output "Plugin target: $pluginTarget"
Write-Output 'Ports: Emby 8096 / Helper management 8099 / Helper proxy 8097'
if ($HelperOnly) { Write-Output 'Helper-only update: Emby and the installed plugin will stay running.' }
if (-not $Apply) {
    Write-Output 'Preflight OK. No changes made. Run this script with -Apply to update.'
    return
}

$backup = Join-Path $projectRoot ('artifacts\checks\local-update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
$null = New-Item -ItemType Directory -Path $backup
Write-Output "Backup: $backup"
$replacing = $false
$toolsCopied = @()
$toolNames = @('dovi.dll', 'libdovi-MIT.txt')
try {
    $embyProcesses = @()
    if (-not $HelperOnly) { $embyProcesses = @(Find-ExactProcess $embyExe) }
    if ($embyProcesses.Count -gt 0) {
        $null = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$embyBase/emby/System/Shutdown" `
            -Headers $headers -TimeoutSec 15
        foreach ($process in $embyProcesses) {
            if (-not $process.WaitForExit(45000)) { throw 'Emby did not shut down; replacement was cancelled.' }
        }
    }
    foreach ($process in @(Find-ExactProcess $helperExe)) {
        Stop-Process -InputObject $process -ErrorAction Stop
        if (-not $process.WaitForExit(15000)) { throw 'Helper did not stop; replacement was cancelled.' }
    }

    Copy-Item -LiteralPath $helperExe -Destination (Join-Path $backup 'EmbyBlurayHelper.exe')
    $oldPdb = Join-Path $live 'EmbyBlurayHelper.pdb'
    if (Test-Path -LiteralPath $oldPdb) { Copy-Item -LiteralPath $oldPdb -Destination $backup }
    Copy-Item -LiteralPath $pluginTarget -Destination (Join-Path $backup 'EmbyBlurayHelper.Plugin.dll')
    Copy-Item -LiteralPath (Join-Path $live 'data') -Destination (Join-Path $backup 'helper-data') -Recurse
    foreach ($name in $toolNames) {
        $existing = Join-Path $live ('tools/' + $name)
        if (Test-Path -LiteralPath $existing) {
            $null = New-Item -ItemType Directory -Path (Join-Path $backup 'tools') -Force
            Copy-Item -LiteralPath $existing -Destination (Join-Path $backup ('tools/' + $name))
        }
    }
    if (-not $HelperOnly) {
        $databaseBackup = Join-Path $backup 'emby-database'
        $null = New-Item -ItemType Directory -Path $databaseBackup
        Get-ChildItem -LiteralPath (Join-Path $workspace 'programdata\data') -Filter 'library.db*' -File |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $databaseBackup }
    }

    $replacing = $true
    Copy-Item -LiteralPath (Join-Path $staged 'EmbyBlurayHelper.exe') -Destination $helperExe -Force
    foreach ($name in $toolNames) {
        $newTool = Join-Path $staged ('tools/' + $name)
        if (Test-Path -LiteralPath $newTool) {
            $null = New-Item -ItemType Directory -Path (Join-Path $live 'tools') -Force
            $toolsCopied += $name
            Copy-Item -LiteralPath $newTool -Destination (Join-Path $live ('tools/' + $name)) -Force
        }
    }
    $newPdb = Join-Path $staged 'EmbyBlurayHelper.pdb'
    if (Test-Path -LiteralPath $newPdb) { Copy-Item -LiteralPath $newPdb -Destination $live -Force }
    if (-not $HelperOnly) { Copy-Item -LiteralPath $pluginSource -Destination $pluginTarget -Force }
    Write-Output 'Update files copied. Existing settings and media files were preserved.'
} catch {
    if ($replacing) {
        Copy-Item -LiteralPath (Join-Path $backup 'EmbyBlurayHelper.exe') -Destination $helperExe -Force
        if (-not $HelperOnly) { Copy-Item -LiteralPath (Join-Path $backup 'EmbyBlurayHelper.Plugin.dll') -Destination $pluginTarget -Force }
        $savedPdb = Join-Path $backup 'EmbyBlurayHelper.pdb'
        if (Test-Path -LiteralPath $savedPdb) { Copy-Item -LiteralPath $savedPdb -Destination $live -Force }
        foreach ($name in $toolsCopied) {
            $oldTool = Join-Path $backup ('tools/' + $name)
            $liveTool = Join-Path $live ('tools/' + $name)
            if (Test-Path -LiteralPath $oldTool) { Copy-Item -LiteralPath $oldTool -Destination $liveTool -Force }
            else { Remove-Item -LiteralPath $liveTool }
        }
        Write-Warning 'Replacement failed. Previous binaries were restored.'
    }
    throw
} finally {
    Start-LocalServices
}

$ready = $false
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    try {
        $status = Invoke-RestMethod -Uri "$embyBase/emby/BlurayHelper/Status" -Headers $headers -TimeoutSec 2
        $management = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8099/' -TimeoutSec 2
        $proxy = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8097/' -TimeoutSec 2
        if ($status.status -eq 'ready' -and $management.StatusCode -eq 200 -and $proxy.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $ready) { throw "Services did not pass startup checks. Keep the console output. Backup: $backup" }
Write-Output 'DONE: Emby and Helper are running; plugin status is ready; ports 8096/8097/8099 are available.'
Write-Output 'No media scan or language completion task was started. Return to Codex for verification.'
