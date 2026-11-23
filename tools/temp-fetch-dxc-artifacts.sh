#!/usr/bin/env bash
# TEMPORARY, Phase 70 draft only: never merge. The win-arm64 and Android DXC natives are pinned for
# the native-dxc-1.9.2607 release tag, which is not published yet. Until it is, CI takes the same
# files from the workflow runs that built them, checked against the SHA-256 pins in tools/restore.sh.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
pin() { grep "^$1=" tools/restore.sh | cut -d'"' -f2; }

fetch() { # run artifact path-in-artifact dest pin-variable
    local want; want="$(pin "$5")"
    if [ -f "$4" ] && [ "$(sha "$4")" = "$want" ]; then return 0; fi
    local dir; dir="$(mktemp -d)"
    gh run download "$1" -R "$GITHUB_REPOSITORY" -n "$2" -D "$dir"
    mkdir -p "$(dirname "$4")"
    cp "$dir/$3" "$4"
    [ "$(sha "$4")" = "$want" ] || { echo "::error::$4 does not match its pin"; exit 1; }
    echo "temp-fetch: $4 from run $1, hash OK"
}

fetch 45090088637 dxc-win-arm64 dxcompiler.dll tools/dxc/win-arm64/dxcompiler.dll DXC_WIN_ARM64_COMPILER_SHA256
fetch 45090088637 dxc-win-arm64 dxil.dll tools/dxc/win-arm64/dxil.dll DXC_WIN_ARM64_VALIDATOR_SHA256
fetch 45091569109 dxc-android-android-arm64-a android-arm64/libdxcompiler.so tools/dxc/android-arm64/libdxcompiler.so DXC_ANDROID_ARM64_SHA256
fetch 45091569109 dxc-android-android-x64-a android-x64/libdxcompiler.so tools/dxc/android-x64/libdxcompiler.so DXC_ANDROID_X64_SHA256
