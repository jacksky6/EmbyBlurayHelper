param(
    [Parameter(Mandatory=$true)][string]$ExperimentPath,
    [string]$SettingsPath,
    [string]$LibraryName = 'DV Compatibility Test'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SettingsPath) { $SettingsPath = Join-Path $root 'artifacts/app/win-x64/data/settings.json' }
$settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$uri = [Uri]$settings.embyUrl
if (-not $uri.IsLoopback -or $uri.Port -ne 8096) { throw 'This experiment only registers with local Emby on port 8096.' }
$headers = @{ 'X-Emby-Token' = $settings.embyApiKey }
$base = $settings.embyUrl.TrimEnd('/') + '/emby/'
$experimentDirectory = (Resolve-Path -LiteralPath $ExperimentPath).Path
$experiment = Get-Content -LiteralPath (Join-Path $experimentDirectory 'experiment.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($experiment.status -ne 'ready-for-device-test' -or $experiment.samples.Count -ne 3) { throw 'Finish sample preparation first.' }
$media = (Resolve-Path -LiteralPath (Join-Path $experimentDirectory 'media')).Path
foreach ($sample in $experiment.samples) {
    if (-not (Test-Path -LiteralPath $sample.file -PathType Leaf)) { throw 'A test sample is missing.' }
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
if ($library.Count -gt 1) { throw 'Ambiguous test library name.' }
if ($library.Count -eq 1 -and (@($library[0].Locations).Count -ne 1 -or $library[0].Locations[0] -cne $media)) {
    throw 'Existing library points somewhere else; no library was changed.'
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
    throw 'Test library path verification failed; inspect local Emby before retrying.'
}
$id = $library[0].ItemId
# Refresh only the isolated test library; never start a whole-server scan.
PostApi "Items/$id/Refresh?Recursive=true&MetadataRefreshMode=Default&ImageRefreshMode=ValidationOnly&ReplaceAllMetadata=false&ReplaceAllImages=false" $null
$deadline = (Get-Date).AddSeconds(90)
do {
    $items = (Invoke-RestMethod -Uri ($base+"Items?ParentId=$id&Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources,MediaStreams") -Headers $headers -TimeoutSec 15).Items
    if (@($items | Where-Object { $_.RunTimeTicks -gt 0 -and @($_.MediaSources).Count -gt 0 -and @($_.MediaSources[0].MediaStreams).Count -ge 2 }).Count -eq 3) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
if (@($items).Count -ne 3 -or @($items | Where-Object { $_.RunTimeTicks -gt 0 -and @($_.MediaSources[0].MediaStreams).Count -ge 2 }).Count -ne 3) {
    throw 'Expected exactly three fully probed movies in the test library; inspect its refresh log.'
}
$result = @{
    libraryId = $id
    libraryName = $LibraryName
    mediaPath = $media
    items = @($items | Select-Object Id,Name,Path,RunTimeTicks,MediaSources)
}
$json = $result | ConvertTo-Json -Depth 30
[IO.File]::WriteAllText((Join-Path $experimentDirectory 'emby-registration.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Output "Local test library ready: $LibraryName (ID $id)"
$items | Sort-Object Name | ForEach-Object { Write-Output ($_.Id+' | '+$_.Name+' | '+[Math]::Round($_.RunTimeTicks/10000000,2)+' s') }
