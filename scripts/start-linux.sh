#!/bin/sh
set -eu
helper_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$helper_dir"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$helper_dir/data/runtime"
exec "$helper_dir/EmbyBlurayHelper" --serve "$@"
