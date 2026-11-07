# Optional Dolby Vision native dependency

The on-demand Infuse P7 to P8.1 module uses libdovi 3.4's C ABI.

- Windows x64: `dovi.dll`, staged and hash-checked by `stage-dovi-windows.ps1`.
- Linux x86_64 / DSM: `libdovi.so`, built by `scripts/build-dovi-linux.sh`
  from pinned upstream 2.3.4 sources (libdovi 3.4.0), Rust 1.90.0 and Zig 0.13.0.
  The target is glibc 2.17; the validated binary requires symbols up to 2.14.
  The build checks C ABI loading, FEL/MEL reference vectors and mode 2 round trips.
- Linux arm64: supply a compatible native build separately.
- macOS: `libdovi.dylib`, built for the target CPU and deployment target.

Keep the upstream MIT license with any distributed native library. A Windows DLL
cannot be used on Linux. In particular, a library built on a recent Linux
distribution may require a newer glibc than DSM 7.2 provides. Build for the target
baseline and verify it there before including it in an SPK.

Linux x86_64 SPK and portable packages include this library and its MIT license.
Actual DSM playback still needs device verification. No media tools are replaced.
An empty tools directory is intentional on other platforms without a validated build:
the feature defaults off and reports the missing dependency when checked.
Do not replace Emby's original ffmpeg or ffprobe. See `INFUSE-DOLBY-VISION.md`.
