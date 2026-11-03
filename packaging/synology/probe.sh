#!/bin/sh
# Called as the Emby account, independently of the helper service account.
set -eu
helper_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ ! -r "$helper_dir/probe-settings.json" ]; then
    echo 'EmbyBlurayHelper: shared probe settings are missing; start the helper package first.' >&2
    exit 1
fi
exec "$helper_dir/EmbyBlurayHelper" "$@"
