param([switch]$Apply, [switch]$Restore, [switch]$Supervisor, [switch]$UntilRestored,
    [ValidateSet('known-good-iso','real-iso-transport','native-iso-metadata','virtual-iso-native')][string]$Mode = 'known-good-iso',
    [ValidateRange(1,60)][int]$Minutes = 30)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$live = Join-Path $project 'artifacts\app\win-x64'
$helper = Join-Path $live 'EmbyBlurayHelper.exe'
$diagnostic = Join-Path $project 'artifacts\checks\infuse-isolation\EmbyBlurayHelper.InfuseIsolation.exe'
$settings = Join-Path $live 'data\settings.json'
foreach ($file in @($helper,$diagnostic,$settings)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
}
function Find-Exact([string]$path) {
    @(Get-CimInstance Win32_Process | Where-Object ExecutablePath -EQ $path |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
}
function Start-Main {
    if (-not (Find-Exact $helper)) {
        $null = Start-Process -FilePath $helper -ArgumentList '--serve' -WorkingDirectory $live -WindowStyle Hidden
    }
}
function Stop-Diagnostic {
    foreach ($process in @(Find-Exact $diagnostic)) {
        Stop-Process -InputObject $process -ErrorAction Stop
        $null = $process.WaitForExit(15000)
    }
}
if ($Restore) {
    $wasRunning = @(Find-Exact $diagnostic).Count -gt 0
    Stop-Diagnostic
    # Let the supervisor own its normal restart before taking the fallback,
    # so manual restoration cannot race it into starting two Helpers.
    if ($wasRunning) {
        $deadline = (Get-Date).AddSeconds(5)
        while (-not (Find-Exact $helper) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    }
    Start-Main
    Write-Output 'Restoring normal Helper 8097/8099; no settings, media or Emby changes.'
    return
}
if (-not $Apply -and -not $Supervisor) {
    Write-Output 'Preflight OK. -Apply temporarily replaces only the local 8097 Helper with the ISO diagnostic.'
    Write-Output "Selected diagnostic mode: $Mode."
    if ($UntilRestored) {
        Write-Output 'Diagnostic stays active until -Restore or diagnostic process exit; no time limit.'
    } else {
        Write-Output "Automatic normal Helper restoration after $Minutes minutes, or use -Restore immediately."
    }
    return
}
if ($Supervisor) {
    $session = Join-Path $project ('artifacts\checks\infuse-isolation-session-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $null = New-Item -ItemType Directory -Path $session
    $process = $null
    try {
        if (Find-Exact $diagnostic) { throw 'Diagnostic already running.' }
        $listeners = @(Get-NetTCPConnection -LocalPort 8097 -State Listen -ErrorAction SilentlyContinue)
        $main = @(Find-Exact $helper)
        if ($listeners | Where-Object { $_.OwningProcess -notin @($main.Id) }) { throw 'Port 8097 belongs to another process.' }
        foreach ($old in $main) {
            Stop-Process -InputObject $old
            if (-not $old.WaitForExit(15000)) { throw 'Main Helper did not stop.' }
        }
        $process = Start-Process -FilePath $diagnostic -ArgumentList @(('"' + $settings + '"'),'12','1886',$Mode) `
            -WorkingDirectory $project -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $session 'proxy.log') -RedirectStandardError (Join-Path $session 'error.log')
        if ($UntilRestored) {
            Write-Output "Diagnostic PID $($process.Id); logs $session; active until -Restore or process exit."
            $process.WaitForExit()
        } else {
            Write-Output "Diagnostic PID $($process.Id); logs $session; maximum $Minutes minutes."
            $null = $process.WaitForExit($Minutes * 60000)
        }
    } finally {
        if ($process -and -not $process.HasExited) { Stop-Process -InputObject $process }
        Start-Main
        Write-Output 'Normal Helper restored. Emby and data were untouched.'
    }
    return
}
if (Find-Exact $diagnostic) { throw 'Diagnostic already running; do not start a second session.' }
$supervisorLog = Join-Path $project ('artifacts\checks\infuse-isolation-supervisor-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$supervisorArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Supervisor','-Minutes',$Minutes,'-Mode',$Mode)
if ($UntilRestored) { $supervisorArguments += '-UntilRestored' }
$null = Start-Process -FilePath 'powershell.exe' -ArgumentList $supervisorArguments `
    -WorkingDirectory $project -WindowStyle Hidden `
    -RedirectStandardOutput ($supervisorLog + '.log') -RedirectStandardError ($supervisorLog + '.error.log')
$deadline = (Get-Date).AddSeconds(30)
do {
    try {
        $status = Invoke-RestMethod http://127.0.0.1:8097/__diagnostic/status -TimeoutSec 2
        if ($status.mode -eq $Mode) {
            if ($Mode -eq 'real-iso-transport') {
                Write-Output 'READY: open real ISO item 1886 on 8097. Emby metadata is unchanged; only its HTTP stream uses the chapter transport.'
            } elseif ($Mode -eq 'native-iso-metadata') {
                Write-Output 'READY: open item 12 on 8097. Both its media source metadata and served ISO content now come from real ISO item 1886.'
            } elseif ($Mode -eq 'virtual-iso-native') {
                Write-Output 'READY: open item 12 on 8097. Its own Minority Report virtual ISO is served with native-style ISO metadata and its original tracks, duration and chapters.'
            } else {
                Write-Output 'READY: item 12 on 8097 now serves real ISO item 1886.'
            }
            if ($UntilRestored) {
                Write-Output 'Active until -Restore or diagnostic process exit; no time limit.'
            } else {
                Write-Output "Auto-restores normal Helper within $Minutes minutes."
            }
            if ($Mode -eq 'virtual-iso-native') {
                Write-Output 'Expected video: Minority Report. Management 8099 is temporarily offline. No settings or media changes.'
            } else {
                Write-Output 'Expected video: Shutter Island. Management 8099 is temporarily offline. No settings or media changes.'
            }
            return
        }
    } catch { }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
Stop-Diagnostic
Start-Main
throw "Diagnostic startup failed; restored normal Helper. Inspect $supervisorLog.error.log"
