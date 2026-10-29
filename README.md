# EmbyBlurayHelper

Emby 4.10.x Blu-ray / UHD Blu-ray media-probing helper.

The first milestone is a conservative `ffprobe` wrapper. Emby starts it as a
child process, and it forwards the original Emby `ffprobe` output and exit code
without replacing or modifying Emby's `ffmpeg`.

> Current status: pass-through only. BDMV/MPLS/CLPI language extraction and the
> persistent probe cache are not implemented yet. This version does not fix
> missing Blu-ray stream languages.

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
preserved. Wrapper diagnostics are written to stderr.

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
