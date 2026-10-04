#!/usr/bin/env bash
# verify-slang-nupkg.sh: the ShadowDusk.Slang nupkg native gate (issue #226). ONE list, used by
# release.yml's pack-desktop job and by tools/verify-slang-packaging.sh (pack-consume.yml), and
# runnable locally against any packed nupkg (bash on Windows: Git Bash).
#
# Fails red (exit 1) if the package is missing the slangc executable or its slang-compiler
# library for any of the five RIDs ShadowDusk.Slang.csproj vendors, or THIRD-PARTY-NOTICES.txt.
# The csproj's pack entries are Exists()-conditioned, so a pack without restored natives
# SILENTLY produces a package whose every compile fails SD0621 on that RID; project_decisions.md
# says a missing native fails the release, it does not warn.
#
# usage: tools/verify-slang-nupkg.sh <path/to/ShadowDusk.Slang.<version>.nupkg>
set -euo pipefail

pkg="${1:-}"
if [ -z "$pkg" ] || [ ! -f "$pkg" ]; then
    echo "::error::ShadowDusk.Slang .nupkg not produced (got '${pkg}')" >&2
    exit 1
fi
echo "Inspecting $pkg"

# EXACT entry names, one per line, matched whole-line (grep -x): a substring match passed a
# package that held "native/slangc/slangc" instead of "native/slangc" (issue #225).
if command -v unzip >/dev/null 2>&1; then
    listing=$(unzip -Z1 "$pkg")
else
    py=$(command -v python3 || command -v python)
    listing=$("$py" -c 'import sys, zipfile; print("\n".join(zipfile.ZipFile(sys.argv[1]).namelist()))' "$pkg")
fi
listing=$(tr -d '\r' <<<"$listing")

# Keep in sync with ShadowDusk.Slang.csproj (SlangNativeVersion) and tools/restore.*.
missing=0
for entry in \
    'runtimes/win-x64/native/slangc.exe' \
    'runtimes/win-x64/native/slang-compiler.dll' \
    'runtimes/win-arm64/native/slangc.exe' \
    'runtimes/win-arm64/native/slang-compiler.dll' \
    'runtimes/linux-x64/native/slangc' \
    'runtimes/linux-x64/native/libslang-compiler.so.0.2026.14.1' \
    'runtimes/osx-x64/native/slangc' \
    'runtimes/osx-x64/native/libslang-compiler.0.2026.14.1.dylib' \
    'runtimes/osx-arm64/native/slangc' \
    'runtimes/osx-arm64/native/libslang-compiler.0.2026.14.1.dylib' \
    'THIRD-PARTY-NOTICES.txt' ; do
    if ! grep -qxF "$entry" <<<"$listing"; then
        if [ "$entry" = THIRD-PARTY-NOTICES.txt ]; then
            echo "::error::$pkg is missing $entry; the Apache-2.0 notice must ride along with the slangc binaries it covers." >&2
        else
            echo "::error::$pkg is missing $entry; every ShadowDusk.Slang compile on that RID would fail SD0621. Restore natives before packing (tools/restore.*; RELEASING.md 'Native binaries')." >&2
        fi
        missing=1
    fi
done
[ "$missing" -eq 0 ] || exit 1
echo "slang nupkg gate: all 10 slangc natives + THIRD-PARTY-NOTICES.txt present ($(wc -c < "$pkg") bytes)"
