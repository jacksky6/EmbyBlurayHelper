# Optional Dolby Vision native dependency

The on-demand Infuse P7 to P8.1 module uses libdovi 3.4's C ABI.

- Windows x64: `dovi.dll`, staged and hash-checked by `stage-dovi-windows.ps1`.
- Linux: `libdovi.so`, built for the target CPU and libc version.
- macOS: `libdovi.dylib`, built for the target CPU and deployment target.

Keep the upstream MIT license with any distributed native library. A Windows DLL
cannot be used on Linux. In particular, a library built on a recent Linux
distribution may require a newer glibc than DSM 7.2 provides. Build for the target
baseline and verify it there before including it in an SPK.

An empty tools directory is intentional on platforms without a validated build:
the feature defaults off and reports the missing dependency when checked.
Do not replace Emby's original ffmpeg or ffprobe. See `INFUSE-DOLBY-VISION.md`.
