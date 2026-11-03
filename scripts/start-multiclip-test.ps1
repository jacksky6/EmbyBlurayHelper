param(
    [string]$SettingsPath,
    [string]$ExecutablePath,
    [int]$ManagementPort = 18099,
    [int]$ProxyPort = 18097
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SettingsPath) { $SettingsPath = Join-Path $projectRoot 'artifacts/app/win-x64/data/settings.json' }
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $projectRoot 'artifacts/checks/app-win-x64/EmbyBlurayHelper.exe' }
if ($ManagementPort -eq $ProxyPort) { throw 'Management and proxy need different ports.' }
foreach ($port in @($ManagementPort, $ProxyPort)) {
    if ($port -lt 1024 -or $port -gt 65535) { throw "Invalid test port: $port" }
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "Port $port is already in use. Existing services were left running."
    }
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw "Publish the Windows app first: $ExecutablePath" }

$testDirectory = Join-Path $projectRoot 'artifacts/multiclip-test/win-x64'
$dataDirectory = Join-Path $testDirectory 'data'
$null = New-Item -ItemType Directory -Path $dataDirectory -Force
$testSettings = Join-Path $dataDirectory 'settings.json'
if (-not (Test-Path -LiteralPath $testSettings)) {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $settings.managementUrl = "http://0.0.0.0:$ManagementPort"
    $settings.proxyUrl = "http://0.0.0.0:$ProxyPort"
    # This instance must not replace the probe already configured in Emby.
    $settings.enableProbeLanguagePatch = $false
    $settings.enableInfuseLanguagePatch = $true
    $settings | Add-Member -NotePropertyName enableInfuseMultiClipPlayback -NotePropertyValue $false -Force
    [IO.File]::WriteAllText($testSettings, ($settings | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
} else {
    $settings = Get-Content -LiteralPath $testSettings -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.managementUrl -ne "http://0.0.0.0:$ManagementPort" -or $settings.proxyUrl -ne "http://0.0.0.0:$ProxyPort") {
        throw 'Saved test ports differ. Re-run with the saved ports; settings were preserved.'
    }
}
$testExecutable = Join-Path $testDirectory 'EmbyBlurayHelper.exe'
Copy-Item -LiteralPath $ExecutablePath -Destination $testExecutable -Force
$process = Start-Process -FilePath $testExecutable -WorkingDirectory $testDirectory -WindowStyle Hidden -ArgumentList @('--serve', '--data-dir', ('"' + $dataDirectory + '"')) -PassThru
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$ManagementPort/" -TimeoutSec 1
        if ($response.StatusCode -eq 200) {
            Write-Output "Test helper PID: $($process.Id)"
            Write-Output "Management: http://localhost:$ManagementPort (fresh account: admin / 123456)"
            Write-Output "Infuse proxy: http://<Windows-LAN-IP>:$ProxyPort"
            Write-Output 'Enable Infuse multi-clip playback in Features before testing. Existing services and Emby settings were left unchanged.'
            return
        }
    } catch { }
    if ($process.HasExited) { throw "Test helper exited: $($process.ExitCode)" }
    Start-Sleep -Milliseconds 250
}
throw "Test helper PID $($process.Id) started but did not answer. Inspect $dataDirectory/logs."
