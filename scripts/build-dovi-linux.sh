#!/usr/bin/env bash
# Project-local toolchain only; no system packages or shell profile changes.
set -euo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
tools="$root/artifacts/tools/dovi-linux-build"
mkdir -p "$tools"
export RUSTUP_HOME="$tools/rustup" CARGO_HOME="$tools/cargo"
export RUSTUP_TOOLCHAIN=1.90.0
export PATH="$CARGO_HOME/bin:$PATH"
if ! test -x "$CARGO_HOME/bin/rustc"; then
    # Optional mirror is useful on networks with very slow static.rust-lang.org.
    export RUSTUP_DIST_SERVER="${RUSTUP_DIST_SERVER:-https://static.rust-lang.org}"
    export RUSTUP_UPDATE_ROOT="${RUSTUP_UPDATE_ROOT:-https://static.rust-lang.org/rustup}"
    if ! test -s "$tools/rustup-init-bin"; then
        curl --fail --location --retry 2 "$RUSTUP_UPDATE_ROOT/dist/x86_64-unknown-linux-gnu/rustup-init" -o "$tools/rustup-init-bin"
    fi
    chmod +x "$tools/rustup-init-bin"
    "$tools/rustup-init-bin" -y --no-modify-path --profile minimal --default-toolchain 1.90.0
fi
if ! "$tools/venv/bin/python" -c 'import ziglang' 2>/dev/null; then
    python3 -m venv "$tools/venv"
    "$tools/venv/bin/pip" install ziglang==0.13.0
fi
export PATH="$tools/venv/bin:$PATH"
export DOVI_ZIG="$("$tools/venv/bin/python" -c 'from pathlib import Path; import ziglang; print(Path(ziglang.__file__).parent / "zig")')"
rustc --version
test "$("$DOVI_ZIG" version)" = 0.13.0
source_dir="$root/artifacts/tools/dovi-source-2.3.4"
if ! test -d "$source_dir/.git"; then
    git clone --depth 1 --branch 2.3.4 https://github.com/quietvoid/dovi_tool.git "$source_dir"
fi
test "$(git -C "$source_dir" rev-parse HEAD)" = 614c816b6446dcd1dbaf433403d499a6026fbb5a
test -z "$(git -C "$source_dir" status --porcelain --untracked-files=no)"
# cargo-c's cfg exposes the same C ABI; cargo rustc selects a cdylib without
# modifying the pinned upstream manifest. Zig targets glibc 2.17 explicitly.
export CC="$root/scripts/dovi-zig-cc.sh"
chmod +x "$CC"
export CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_LINKER="$CC"
export RUSTFLAGS='--cfg cargo_c -C panic=abort'
cargo rustc --manifest-path "$source_dir/dolby_vision/Cargo.toml" --release --locked --features capi \
    --target x86_64-unknown-linux-gnu --target-dir "$tools/target" --lib --crate-type cdylib
destination="${1:-$root/artifacts/app/linux-x64}"
mkdir -p "$destination/tools"
cp "$tools/target/x86_64-unknown-linux-gnu/release/libdolby_vision.so" "$destination/tools/libdovi.so"
cp THIRD-PARTY-LICENSES/libdovi-MIT.txt "$destination/tools/libdovi-MIT.txt"
python3 tests/synology/check-dovi.py "$destination/tools/libdovi.so" "$source_dir/assets/tests"
sha256sum "$destination/tools/libdovi.so"
