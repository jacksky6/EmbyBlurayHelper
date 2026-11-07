param(
    [Parameter(Mandatory=$true)][string]$ExperimentPath,
    [string]$LibraryName = 'DV ISO Compatibility Test'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$settings = Get-Content (Join-Path $project 'artifacts/app/win-x64/data/settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$uri = [Uri]$settings.embyUrl
if (-not $uri.IsLoopback -or $uri.Port -ne 8096) { throw 'Local test Emby on 8096 only.' }
$headers = @{ 'X-Emby-Token' = $settings.embyApiKey }
$base = $settings.embyUrl.TrimEnd('/') + '/emby/'
$root = (Resolve-Path -LiteralPath $ExperimentPath).Path
$experiment = Get-Content (Join-Path $root 'experiment.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$inspection = Get-Content (Join-Path $root 'iso-inspection.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$verification = Get-Content (Join-Path $root 'verification-offline.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($verification.status -ne 'passed-offline-ready-for-local-registration' -or $inspection.samples.Count -ne 2) {
    throw 'Finish ISO structure/codec verification before registering.'
}
foreach ($sample in $inspection.samples) {
    if (-not (Test-Path -LiteralPath $sample.file -PathType Leaf) -or (Get-Item -LiteralPath $sample.file).Length -ne $sample.bytes) {
        throw 'Verified ISO file is missing or changed.'
    }
}
$media = (Resolve-Path -LiteralPath (Join-Path $root 'media')).Path
$controlDirectory = Join-Path $media 'DV ISO Test 06 - P81 MKV Control'
$control = Join-Path $controlDirectory 'DV ISO Test 06 - P81 MKV Control.mkv'
if (-not (Test-Path -LiteralPath $control)) {
    New-Item -ItemType Directory -Path $controlDirectory -Force | Out-Null
    Copy-Item -LiteralPath $experiment.source -Destination $control
}
if ((Get-FileHash -LiteralPath $control).Hash -ne (Get-FileHash -LiteralPath $experiment.source).Hash) {
    throw 'MKV control content differs from the verified source.'
}
function PostApi([string]$route, $body) {
    $parameters = @{ Method='Post'; Headers=$headers; Uri=($base+$route); TimeoutSec=30 }
    if ($null -ne $body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 30 -Compress))
    }
    $null = Invoke-RestMethod @parameters
}
$libraries = Invoke-RestMethod -Uri ($base+'Library/VirtualFolders') -Headers $headers -TimeoutSec 15
$library = @($libraries | Where-Object Name -eq $LibraryName)
if ($library.Count -gt 1 -or ($library.Count -eq 1 -and (@($library[0].Locations).Count -ne 1 -or $library[0].Locations[0] -cne $media))) {
    throw 'Existing library is ambiguous or points elsewhere; no library was changed.'
}
if ($library.Count -eq 0) {
    $options = @{
        PathInfos = @(@{ Path=$media })
        EnableRealtimeMonitor = $false
        EnableChapterImageExtraction = $false
        ExtractChapterImagesDuringLibraryScan = $false
        EnableMarkerDetection = $false
        EnableEmbeddedTitles = $true
        EnableMultiVersionByMetadata = $false
        EnableMultiVersionByFiles = $false
        EnableInternetProviders = $false
        SaveLocalMetadata = $false
        MetadataSavers = @()
        AutomaticRefreshIntervalDays = 0
        TypeOptions = @(@{ Type='Movie'; MetadataFetchers=@(); MetadataFetcherOrder=@(); ImageFetchers=@(); ImageFetcherOrder=@() })
    }
    $name = [Uri]::EscapeDataString($LibraryName)
    PostApi "Library/VirtualFolders?Name=$name&CollectionType=movies&RefreshLibrary=false" @{ LibraryOptions=$options }
    $libraries = Invoke-RestMethod -Uri ($base+'Library/VirtualFolders') -Headers $headers -TimeoutSec 15
    $library = @($libraries | Where-Object Name -eq $LibraryName)
}
if ($library.Count -ne 1 -or @($library[0].Locations).Count -ne 1 -or $library[0].Locations[0] -cne $media) {
    throw 'Test library registration verification failed.'
}
$id = $library[0].ItemId
# Refresh only the dedicated 3-item test library, never the whole media library.
PostApi "Items/$id/Refresh?Recursive=true&MetadataRefreshMode=Default&ImageRefreshMode=ValidationOnly&ReplaceAllMetadata=false&ReplaceAllImages=false" $null
$deadline = (Get-Date).AddSeconds(90)
do {
    $items = (Invoke-RestMethod -Uri ($base+"Items?ParentId=$id&Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources,MediaStreams,Chapters") -Headers $headers -TimeoutSec 15).Items
    $complete = @($items | Where-Object { $_.RunTimeTicks -gt 0 -and @($_.MediaSources).Count -gt 0 -and @($_.MediaSources[0].MediaStreams).Count -ge 2 })
    if ($complete.Count -eq 3) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
if (@($items).Count -ne 3 -or $complete.Count -ne 3) { throw 'Expected 3 fully probed samples; inspect the test library scan log.' }
$result = @{ libraryId=$id; libraryName=$LibraryName; mediaPath=$media; items=@($items | Select-Object Id,Name,Path,RunTimeTicks,MediaSources) }
[IO.File]::WriteAllText((Join-Path $root 'emby-registration.json'), ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Write-Output "Local ISO test library ready: $LibraryName (ID $id)"
$items | Sort-Object Name | ForEach-Object { Write-Output ($_.Id+' | '+$_.Name+' | '+[Math]::Round($_.RunTimeTicks/10000000,3)+' s') }
