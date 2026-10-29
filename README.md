# EmbyBlurayHelper

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

When Emby invokes the wrapper, arguments are forwarded unchanged to the
configured upstream `ffprobe`. Stdout, stderr, and the upstream exit code are
preserved. For a Blu-ray JSON probe, only missing stream language tags are
added. Wrapper diagnostics are written to stderr.

The wrapper uses the Jellyfin `BDInfo` 0.8.0 package (LGPL-2.1-only) for MPLS
and CLPI parsing. The license text is included in
`THIRD-PARTY-LICENSES/BDInfo-LGPL-2.1.txt` and in each published artifact.

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
