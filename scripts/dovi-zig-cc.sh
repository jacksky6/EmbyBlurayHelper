#!/bin/sh
exec "${DOVI_ZIG:?Set DOVI_ZIG to the project-local Zig binary}" cc -target x86_64-linux-gnu.2.17 "$@"
