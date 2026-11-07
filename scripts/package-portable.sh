#!/bin/sh
# Run from repository root, after dotnet publish. Does not include local data.
set -eu
helper_rid=${1:?usage: package-portable.sh linux-x64|linux-arm64|win-x64}
helper_output="artifacts/app/$helper_rid"
test -d "$helper_output"
mkdir -p "$helper_output/licenses" "$helper_output/emby-plugin" "$helper_output/tools" artifacts/downloads
cp artifacts/plugin/EmbyBlurayHelper.Plugin.dll "$helper_output/emby-plugin/"
cp THIRD-PARTY-LICENSES/BDInfo-LGPL-2.1.txt "$helper_output/licenses/"
cp THIRD-PARTY-LICENSES/DiscUtils-MIT.txt "$helper_output/licenses/"
cp THIRD-PARTY-LICENSES/libdovi-MIT.txt "$helper_output/licenses/"
cp packaging/native/README.md "$helper_output/tools/README.md"
cp docs/INFUSE-DOLBY-VISION.md "$helper_output/INFUSE-DOLBY-VISION.md"
cp docs/INFUSE-CONTAINERS.md "$helper_output/INFUSE-CONTAINERS.md"
cp docs/WEB-CONSOLE.md "$helper_output/READ-ME.md"
cp docs/LIBRARY-LANGUAGE.md "$helper_output/LIBRARY-LANGUAGE.md"
cp docs/NATIVE-CHAPTERS.md "$helper_output/NATIVE-CHAPTERS.md"
cp docs/DISC-THUMBNAILS.md "$helper_output/DISC-THUMBNAILS.md"
cp docs/PLUGIN-UPDATES.md "$helper_output/PLUGIN-UPDATES.md"
case "$helper_rid" in
  linux-*)
    cp scripts/start-linux.sh "$helper_output/start-linux.sh"
    chmod +x "$helper_output/EmbyBlurayHelper" "$helper_output/start-linux.sh"
    tar -czf "artifacts/downloads/EmbyBlurayHelper-0.2.0-$helper_rid.tar.gz" -C "$helper_output" EmbyBlurayHelper start-linux.sh READ-ME.md LIBRARY-LANGUAGE.md NATIVE-CHAPTERS.md DISC-THUMBNAILS.md PLUGIN-UPDATES.md INFUSE-DOLBY-VISION.md INFUSE-CONTAINERS.md licenses emby-plugin tools
    ;;
  win-x64)
    tar -czf "artifacts/downloads/EmbyBlurayHelper-0.2.0-$helper_rid.tar.gz" -C "$helper_output" EmbyBlurayHelper.exe READ-ME.md LIBRARY-LANGUAGE.md NATIVE-CHAPTERS.md DISC-THUMBNAILS.md PLUGIN-UPDATES.md INFUSE-DOLBY-VISION.md INFUSE-CONTAINERS.md licenses emby-plugin tools
    ;;
  *) exit 2 ;;
esac
