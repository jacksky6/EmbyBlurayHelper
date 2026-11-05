#!/usr/bin/env python3
"""Build a DSM 7.2 x86_64 SPK using only the Python standard library."""
import argparse
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import re
import struct
import tarfile
import zlib

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = ROOT / "packaging/synology"


def source(path):
    return path.read_bytes().replace(b"\r\n", b"\n")


def add(archive, name, data, mode=0o644):
    # Explicit directory modes avoid inheriting the installer's restrictive umask.
    for parent in reversed(Path(name).parents):
        if str(parent) == "." or parent.as_posix() in archive.getnames():
            continue
        directory = tarfile.TarInfo(parent.as_posix())
        directory.type, directory.mode, directory.mtime = tarfile.DIRTYPE, 0o755, 0
        directory.uid = directory.gid = 0
        directory.uname = directory.gname = "root"
        archive.addfile(directory)
    entry = tarfile.TarInfo(name)
    entry.size, entry.mode, entry.mtime = len(data), mode, 0
    entry.uid = entry.gid = 0
    entry.uname = entry.gname = "root"
    archive.addfile(entry, io.BytesIO(data))


def icon(size):
    # The existing purple disc mark, rendered without a graphics dependency.
    pixels = bytearray()
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            px, py = (x + .5) / size, (y + .5) / size
            radius = math.hypot(px - .5, py - .5)
            corner = math.hypot(max(abs(px - .5) - .29, 0), max(abs(py - .5) - .29, 0))
            if corner > .20:
                color = (0, 0, 0, 0)
            elif abs(radius - .30) < .022 or radius < .064:
                color = (255, 255, 255, 255)
            else:
                color = (91, 88, 224, 255)
            pixels.extend(color)
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">2I5B", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(pixels), 9)) + chunk(b"IEND", b""))


def build(version, published, output):
    executable = published / "EmbyBlurayHelper"
    binary = executable.read_bytes()
    if binary[:5] != b"\x7fELF\x02" or struct.unpack_from("<H", binary, 18)[0] != 62:
        raise ValueError("Expected a self-contained Linux x86_64 executable")
    payload_buffer = io.BytesIO()
    with gzip.GzipFile(fileobj=payload_buffer, mode="wb", mtime=0, filename="") as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as payload:
            add(payload, "EmbyBlurayHelper", binary, 0o755)
            add(payload, "probe.sh", source(TEMPLATE / "probe.sh"), 0o755)
            add(payload, "READ-ME.md", source(ROOT / "docs/SYNOLOGY.md"))
            add(payload, "LIBRARY-LANGUAGE.md", source(ROOT / "docs/LIBRARY-LANGUAGE.md"))
            add(payload, "MEDIA-FEATURES.md", source(ROOT / "docs/MEDIA-FEATURES.md"))
            add(payload, "NATIVE-CHAPTERS.md", source(ROOT / "docs/NATIVE-CHAPTERS.md"))
            add(payload, "DISC-THUMBNAILS.md", source(ROOT / "docs/DISC-THUMBNAILS.md"))
            add(payload, "ARCHITECTURE.md", source(ROOT / "docs/ARCHITECTURE.md"))
            add(payload, "emby-plugin/EmbyBlurayHelper.Plugin.dll", (ROOT / "artifacts/plugin/EmbyBlurayHelper.Plugin.dll").read_bytes())
            add(payload, "licenses/BDInfo-LGPL-2.1.txt", source(ROOT / "THIRD-PARTY-LICENSES/BDInfo-LGPL-2.1.txt"))
            add(payload, "licenses/DiscUtils-MIT.txt", source(ROOT / "THIRD-PARTY-LICENSES/DiscUtils-MIT.txt"))
            for path in sorted((TEMPLATE / "ui").rglob("*")):
                if path.is_file():
                    add(payload, path.relative_to(TEMPLATE).as_posix(), source(path))
            for size in (16, 24, 32, 48, 64, 72, 128, 256):
                add(payload, f"ui/images/EmbyBlurayHelper-{size}.png", icon(size))
    packed = payload_buffer.getvalue()
    info = source(TEMPLATE / "INFO").replace(b"@VERSION@", version.encode()).replace(
        b"@CHECKSUM@", hashlib.md5(packed).hexdigest().encode())
    output.mkdir(parents=True, exist_ok=True)
    destination = output / f"EmbyBlurayHelper-{version}-dsm7.2-x86_64.spk"
    with tarfile.open(destination, "w", format=tarfile.USTAR_FORMAT) as package:
        add(package, "INFO", info)
        add(package, "package.tgz", packed)
        add(package, "PACKAGE_ICON.PNG", icon(64))
        add(package, "PACKAGE_ICON_256.PNG", icon(256))
        for folder in ("conf", "scripts"):
            for path in sorted((TEMPLATE / folder).rglob("*")):
                if path.is_file():
                    data = source(path)
                    if folder == "conf":
                        json.loads(data)
                    add(package, path.relative_to(TEMPLATE).as_posix(), data, 0o755 if folder == "scripts" else 0o644)
    digest = hashlib.sha256(destination.read_bytes()).hexdigest()
    destination.with_suffix(".spk.sha256").write_text(f"{digest}  {destination.name}\n", encoding="ascii")
    print(f"SPK: {destination}\nSHA256: {digest}")
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="0.2.0-22")
    parser.add_argument("--published", type=Path, default=ROOT / "artifacts/app/linux-x64")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/downloads")
    arguments = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+-[1-9]\d*", arguments.version):
        parser.error("Use a numeric package version, for example 0.2.0-1")
    build(arguments.version, arguments.published, arguments.output)
