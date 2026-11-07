param([Parameter(Mandatory=$true)][string]$ExperimentPath)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ExperimentPath).Path
$experiment = Get-Content -LiteralPath (Join-Path $root 'experiment.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($experiment.status -ne 'ready-for-device-test') { throw 'Prepare a successful finite experiment first.' }
$conversion = $experiment.steps | Where-Object label -eq '09-p81-convert'
$tool = (Resolve-Path -LiteralPath $conversion.executable).Path
$output = Join-Path $root 'benchmark-p81.hevc'
if (Test-Path -LiteralPath $output) { throw 'Benchmark output already exists; preserve the earlier measurement.' }
$stdout = Join-Path $root 'benchmark-stdout.log'
$stderr = Join-Path $root 'benchmark-stderr.log'
$arguments = @('-m', '2', 'mux', '--bl', ('"'+(Join-Path $root 'base.hevc')+'"'),
    '--el', ('"'+(Join-Path $root 'enhancement.hevc')+'"'), '--discard', '-o', ('"'+$output+'"'))
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $tool -ArgumentList $arguments -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
# Hold the OS handle so exit-time CPU accounting remains readable.
$null = $process.Handle
$peak = [long]0
while (-not $process.HasExited) {
    $process.Refresh()
    if (-not $process.HasExited) { $peak = [Math]::Max($peak, $process.WorkingSet64) }
    Start-Sleep -Milliseconds 10
}
$process.WaitForExit()
$watch.Stop()
if ($process.ExitCode -ne 0) { throw "dovi_tool benchmark failed ($($process.ExitCode)); inspect benchmark logs." }
$report = [ordered]@{
    checkedUtc = [DateTime]::UtcNow.ToString('o')
    tool = $tool
    nominalClipSeconds = $experiment.seconds
    wallSeconds = $watch.Elapsed.TotalSeconds
    cpuSeconds = $process.TotalProcessorTime.TotalSeconds
    sampledPeakWorkingSetMiB = [Math]::Round($peak / 1MB, 2)
    outputBytes = (Get-Item -LiteralPath $output).Length
    warmFilesystemCache = $true
    scope = 'Local Windows conversion of already demuxed BL+EL; excludes source NAS I/O, demux, remux, HTTP, clients and seeking.'
}
$json = $report | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $root 'benchmark.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Output $json
