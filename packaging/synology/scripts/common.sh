#!/bin/sh
set -eu
helper_target=${SYNOPKG_PKGDEST:-/var/packages/EmbyBlurayHelper/target}
helper_var=${SYNOPKG_PKGVAR:-/var/packages/EmbyBlurayHelper/var}
helper_exe="$helper_target/EmbyBlurayHelper"
helper_pidfile="$helper_var/helper.pid"
helper_bootlog="$helper_var/startup.log"
export EMBY_BLURAY_HELPER_DATA="$helper_var/data"
export EMBY_BLURAY_HELPER_PROBE_CONFIG="$helper_var/probe-settings.json"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$helper_var/runtime"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
umask 077

helper_prepare() {
    mkdir -p "$helper_var/data" "$helper_var/runtime"
    # Emby needs to traverse this directory to read the two-field snapshot only.
    chmod 711 "$helper_var"
    chmod 700 "$helper_var/data" "$helper_var/runtime"
    chmod 755 "$helper_target" "$helper_exe" "$helper_target/probe.sh"
    if [ ! -e "$helper_target/probe-settings.json" ] && [ ! -L "$helper_target/probe-settings.json" ]; then
        ln -s "$helper_var/probe-settings.json" "$helper_target/probe-settings.json"
    fi
}

helper_running() {
    [ -f "$helper_pidfile" ] || return 1
    helper_pid=$(cat "$helper_pidfile")
    case "$helper_pid" in ''|*[!0-9]*|0|1) return 1 ;; esac
    kill -0 "$helper_pid" 2>/dev/null || return 1
    # Never signal a different process after a stale PID has been reused.
    [ "$(readlink "/proc/$helper_pid/exe" 2>/dev/null)" = "$(readlink -f "$helper_exe")" ] || return 1
}

helper_message() {
    echo "$*" >&2
    if [ -n "${SYNOPKG_TEMP_LOGFILE:-}" ]; then
        printf '%s\n' "$*" >> "$SYNOPKG_TEMP_LOGFILE"
    fi
}
