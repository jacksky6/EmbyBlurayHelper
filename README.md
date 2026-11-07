# EmbyBlurayHelper

The unified 0.2.0 executable combines the existing ProbeWrapper and Infuse
language proxy with an embedded Web console for settings, connection checks,
disc inspection and rotating logs. See [Web console / Windows / native DSM setup](docs/WEB-CONSOLE.md).

The Features page also provides a library language check and a background batch
completion task for Blu-ray folder and ISO movies, with progress, cancellation,
verified results and per-track language logs. ISO support uses the same built-in
UDF/M2TS reader on every platform, without mounting or extracting the movie.
The optional Emby plugin also fills missing
runtime and file size, so ISO details include duration. See [批量检查和补全使用说明](docs/LIBRARY-LANGUAGE.md)
and [模块结构与维护边界](docs/ARCHITECTURE.md).
The standalone tools below remain available.

The optional **音视频规格** page adds automatic Atmos / Dolby Vision enrichment
for newly scanned Blu-ray folder and ISO movies, plus a persistent batch task
for existing movies with per-library selection (plugin 0.1.4+). Emby's original
ffprobe still supplies all baseline information; a separate modern ffprobe only
adds confirmed feature metadata. See [使用方式、探测器边界和本机验证](docs/MEDIA-FEATURES.md).

For native Intel/AMD 64-bit NAS installations, a DSM 7.2 SPK is available from
the build artifacts. See [Synology installation and repeatable packaging](docs/SYNOLOGY.md).

Infuse playback enhancement now supports on-demand **P7→P8.1 Dolby Vision in MKV**,
without re-encoding video or audio (FEL residual is discarded). Ordinary discs
default to the existing ISO path; the Web console offers an experimental MKV
copy-remux choice for BDMV / Blu-ray ISO comparison. Windows x64 and DSM/Linux
x86_64 packages bundle their native libdovi dependency. See
[封装格式与切换](docs/INFUSE-CONTAINERS.md), [Dolby Vision](docs/INFUSE-DOLBY-VISION.md)
and [main / historical recovery points](docs/BRANCHES.md).

The **原盘章节** page restores original MPLS chapter positions for BDMV / Blu-ray
ISO movies (plugin 0.1.5+), automatically after metadata scans or in a persistent
per-library batch. It reads only navigation files, preserves custom chapters and
intro/credits markers, and needs no ffprobe or remux. See [原盘章节补全](docs/NATIVE-CHAPTERS.md).

**Disc chapter thumbnails** (plugin 0.1.6+) follow Emby's existing per-library
thumbnail settings and native task schedule. BDMV and Blu-ray ISO use seekable
inputs and Emby's original image extraction engine. HDR / compatible Dolby Vision
base layers require verified tone mapping to SDR, avoiding uncorrected HDR or
purple/green previews. See [disc thumbnails](docs/DISC-THUMBNAILS.md).

Emby 4.10.x Blu-ray / UHD Blu-ray media-probing helper.

The wrapper starts Emby's original `ffprobe` as a child process. For JSON probes
of Blu-ray streams it reads matching MPLS/CLPI metadata and adds missing
`tags.language` values by PID, then forwards the rest of the original output.
It never replaces or modifies Emby's `ffmpeg`.

For Emby's Blu-ray `concat:` input, the wrapper matches the exact ordered clip
list to an MPLS playlist and uses that playlist's PID/language table. For a
direct `.m2ts` probe it reads the corresponding CLPI file instead. It reads
metadata files only, not movie payloads. If a directory is probed, only
unambiguous PID/language pairs across its CLPI files are used.

## Build

GitHub Actions publishes self-contained single-file builds for:

- Windows x64 (`win-x64`)
- Linux x64 (`linux-x64`)
- Linux ARM64 (`linux-arm64`)

For a local SDK build:

```powershell
dotnet publish src/EmbyBlurayHelper.ProbeWrapper/EmbyBlurayHelper.ProbeWrapper.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o artifacts/win-x64
```

## Configure

Copy `emby-bluray-helper.example.json` next to the wrapper and name it
`emby-bluray-helper.json`. Set `upstreamProbePath` to the original Emby
`ffprobe` executable. The `EMBY_BLURAY_HELPER_UPSTREAM_PROBE` environment
variable can be used instead and takes precedence over the JSON file.

Example for the local Windows Emby 4.10.2 test server:

```json
{
  "upstreamProbePath": "D:\\GIT\\Emby插件\\embyserver-win-x64-4.10.2.0\\ffprobe.exe"
}
```

Example for DSM 7.2 native Emby:

```json
{
  "upstreamProbePath": "/var/packages/EmbyServer/target/bin/ffprobe"
}
```

The wrapper can also use a platform default when no path is configured. An
explicit path is recommended, especially for Docker and side-by-side Emby
installations.

## Manual smoke test

```powershell
./EmbyBlurayHelper.ProbeWrapper.exe --helper-version
./EmbyBlurayHelper.ProbeWrapper.exe --check-upstream
./EmbyBlurayHelper.ProbeWrapper.exe -version
```

When Emby invokes the wrapper, arguments are forwarded to the configured
upstream `ffprobe`. For ISO JSON probes with language completion enabled, the
wrapper reads UDF navigation files and selects the longest complete, non-looping
main playlist (or the explicit `-playlist`). A short-lived, random localhost HTTP
source exposes its M2TS members with byte-range access to Emby's original ffprobe.
Only small navigation files and bounded media samples are read; the movie is
never mounted, fully extracted, remuxed or transcoded. MPLS supplies full-title
duration and chapters; actual probing supplies codec, HDR and channel information,
including Dolby Vision enhancement video and HD/core audio streams. Missing
languages are filled by playlist PID. All expected playlist PIDs must be found.
Failures return an error without falling back to the native Blu-ray protocol.
Ordinary media, disabled completion and non-JSON probes remain passthrough.
Diagnostics are written to stderr.

The wrapper uses the Jellyfin `BDInfo` 0.8.0 package (LGPL-2.1-only) for MPLS
and CLPI parsing, plus `DiscUtils.Udf` 0.16.13 (MIT) for ISO metadata. License
texts are included in `THIRD-PARTY-LICENSES/` and each published artifact.

## Infuse UHD Blu-ray language proxy (experimental)

`EmbyBlurayHelper.Proxy` adds missing audio and PGS subtitle language
descriptors to the transport stream as Infuse reads it over HTTP. It is a
separate executable; the existing ProbeWrapper remains useful for Emby's
metadata and built-in player.

This proxy patches MPEG-TS program map tables (PMTs) in memory, using existing
stuffing bytes or the next identical PMT repetition. It preserves all media packets,
timestamps, file length and byte positions. It never encodes video/audio or
generates a full remux. Work begins only when a media HTTP request arrives. Each request
uses a roughly 1.1 MiB block buffer with backpressure; only the blocks covering
the requested byte range are fetched. Small ranges can read less than one
extra block on either end. Cancelling playback cancels the upstream request.

The condition is an Infuse client header plus a static stream request for a
4K `bluray` media source. Other clients, metadata requests, HLS, and other
media sources are passed through unchanged. HTTP HEAD, normal/suffix byte
ranges and overlapping reads are supported. Canonical block boundaries keep
overlapping range responses identical. Multipart ranges are passed through.

Copy `emby-bluray-helper-proxy.example.json` next to the proxy and name it
`emby-bluray-helper-proxy.json`. For the Windows test server:

```json
{
  "listenUrl": "http://0.0.0.0:8097",
  "upstreamBaseUrl": "http://127.0.0.1:8096",
  "enableInfuseLanguagePatch": true,
  "enableInfuseMultiClipPlayback": false,
  "upstreamProbePath": "D:/EmbyServer/ffprobe.exe"
}
```

Run `EmbyBlurayHelper.Proxy.exe` on Windows, or `./EmbyBlurayHelper.Proxy`
on Linux. Connect Infuse to `http://<Emby-computer-IP>:8097` with the existing
Emby account. Emby stays on 8096. Stop the proxy or point Infuse back at 8096
to revert. No API key belongs in the proxy config; credentials are forwarded
from the client and Emby authenticates every media request.

For DSM, run the appropriate Linux build as an account that can read the
Blu-ray metadata folders. For Docker, mount the library at the same paths as
Emby sees (including BDMV/PLAYLIST and CLIPINF). If Emby is in another
container, set `upstreamBaseUrl` to its container hostname and publish proxy
port 8097. Do not use loopback to refer to another container. No FFmpeg binary
or temporary media volume is bundled. The multi-clip workaround has a built-in
reader for complete seamless clips verified against MPLS/CLPI, including DSM
builds whose FFmpeg has no Blu-ray protocol. Titles with trims, multiple angles
or complex time domains retain their upstream response. Metadata maps are cached in memory
for five minutes, with at most 128 entries.

The local Emby 4.10.2 test for item 12 confirms all seven audio streams and
twelve PGS subtitle streams have language tags in the served bytes, including
at a seek offset. A real Infuse-Direct 8.5.9 iPhone test also confirmed the
language display through the proxy.
PMTs without enough stuffing, damaged tables, and ambiguous language mappings
are left intact. A PMT spanning a canonical block boundary is also left intact;
later repeated PMTs can still be patched. No source file is modified.

Multi-clip compatibility is a separate opt-in feature (`enableInfuseMultiClipPlayback`,
default `false`). The unified Web console provides its own switch, status and
explanation; changing the switch saves immediately and does not change Emby's
probe or the language patch setting.

For multi-clip UHD titles, Emby can return HTTP 404/500 because it treats
pipe-separated clip paths as a single filename. If size and duration uniquely
identify a playlist, a built-in reader verifies that its single-angle clips
are complete using CLPI timestamps, time domains, packet counts and MPLS connection
conditions. Eligible titles are served directly through bounded memory with byte
seeks, without a native process. It never guesses trims or concatenates arbitrary
clip files. Titles outside this restricted subset retain their upstream response.
The former FFmpeg/libbluray reader was removed: DSM 4.10.2.0 lacks that protocol,
and native byte seeks can round to access points instead of exact HTTP offsets.
The built-in reader does not demux/remux the transport stream. The proxy validates the caller's item visibility and upstream
playback authorization on every request; 401/403 are never replaced. Ordinary
clients and successful upstream streams keep their existing behavior.

The isolated live test for item 5 on Emby 4.10.2.0 reproduces the upstream 500
and verifies HEAD, overlapping/suffix ranges, both clip boundaries, cancellation,
invalid credentials, HEVC/TrueHD and subtitle language tags over HTTP. Infuse
device playback still needs validation on the repaired stream.

Build and verify locally with .NET SDK 8:

```powershell
dotnet run --project tests/EmbyBlurayHelper.Proxy.Tests -c Release
dotnet publish src/EmbyBlurayHelper.Proxy -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/proxy/win-x64
```

GitHub Actions publishes separate Proxy artifacts for Windows x64, Linux x64
and Linux ARM64. The proxy also uses BDInfo 0.8.0; its license is included in
the proxy artifacts.

## Emby setup

Emby 4.10.2 does not expose the custom probe path on its regular Transcoding
settings page. Its FFmpeg Options service is available at
`/emby/Encoding/FfmpegOptions`; the `UseCustomProbePath` and `CustomProbePath`
fields can be set there. Keep Emby's original `ffmpeg` path unchanged.

For a native Windows server, use the wrapper's full Windows path. For native
DSM, use its full NAS path. For Docker, use the path as visible inside the
container and mount the wrapper/config directory into that container.

Do not point the custom probe path at the wrapper until its upstream path is
configured and `--check-upstream` succeeds. Revert `UseCustomProbePath` to
`false` to return to the bundled Emby probe.
