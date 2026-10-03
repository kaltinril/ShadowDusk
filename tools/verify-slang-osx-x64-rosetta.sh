#!/usr/bin/env bash
# verify-slang-osx-x64-rosetta.sh: execute the PACKAGED osx-x64 slangc (issue #352).
#
# CI's macOS runners are arm64, so the osx-arm64 slangc is exercised by every macOS consumer
# run, but nothing ever executed the osx-x64 one: the nupkg gate (verify-slang-nupkg.sh) only
# proves it is present. This script takes the packed ShadowDusk.Slang nupkg, extracts BOTH
# macOS natives from it (exactly the two-file runtimes/<rid>/native/ set a consumer gets), runs
# the osx-x64 slangc under Rosetta 2 and the osx-arm64 slangc natively on the same inputs with
# the same flags, and fails unless:
#   - the osx-x64 slangc is an x86_64-only Mach-O (so it can only run translated),
#   - Rosetta translation is really in effect (sysctl.proc_translated = 1 under arch -x86_64),
#   - every entry point compiles (exit 0, non-empty HLSL naming the entry) on both, and
#   - the two outputs are byte-identical.
# Rosetta is installed if the runner lacks it. Exits non-zero on any failure.
#
# usage: tools/verify-slang-osx-x64-rosetta.sh <path/to/ShadowDusk.Slang.<version>.nupkg> [scratch-dir]
# Host: macOS on Apple silicon (arm64) only.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

pkg="${1:-}"
SCRATCH="${2:-$(mktemp -d)}"
if [ -z "$pkg" ] || [ ! -f "$pkg" ]; then
    echo "::error::ShadowDusk.Slang .nupkg not found (got '${pkg}')" >&2
    exit 1
fi
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    echo "::error::verify-slang-osx-x64-rosetta.sh needs macOS on arm64 (got $(uname -s) $(uname -m))" >&2
    exit 1
fi

# ---- Rosetta 2 --------------------------------------------------------------------------
if ! arch -x86_64 /usr/bin/true 2>/dev/null; then
    echo "== Rosetta 2 not available; installing"
    sudo softwareupdate --install-rosetta --agree-to-license
    arch -x86_64 /usr/bin/true || { echo "::error::Rosetta 2 install did not make x86_64 executable" >&2; exit 1; }
fi
translated=$(arch -x86_64 /usr/sbin/sysctl -n sysctl.proc_translated)
if [ "$translated" != 1 ]; then
    echo "::error::arch -x86_64 did not run under Rosetta translation (sysctl.proc_translated=$translated)" >&2
    exit 1
fi
echo "== Rosetta 2 active (sysctl.proc_translated=1 under arch -x86_64)"

# ---- extract both macOS natives from the PACKAGE, not from tools/slang ------------------
X64="$SCRATCH/osx-x64"
ARM="$SCRATCH/osx-arm64"
rm -rf "$X64" "$ARM"
mkdir -p "$X64" "$ARM"
unzip -q -j "$pkg" 'runtimes/osx-x64/native/*' -d "$X64"
unzip -q -j "$pkg" 'runtimes/osx-arm64/native/*' -d "$ARM"
chmod +x "$X64/slangc" "$ARM/slangc"
echo "osx-x64 natives:   $(ls "$X64" | tr '\n' ' ')"
echo "osx-arm64 natives: $(ls "$ARM" | tr '\n' ' ')"

x64_archs=$(lipo -archs "$X64/slangc")
arm_archs=$(lipo -archs "$ARM/slangc")
echo "osx-x64 slangc archs: $x64_archs   osx-arm64 slangc archs: $arm_archs"
if [ "$x64_archs" != x86_64 ]; then
    echo "::error::packaged osx-x64 slangc is not an x86_64-only Mach-O (lipo -archs: $x64_archs)" >&2
    exit 1
fi
if [ "$arm_archs" != arm64 ]; then
    echo "::error::packaged osx-arm64 slangc is not an arm64-only Mach-O (lipo -archs: $arm_archs)" >&2
    exit 1
fi

# ---- compile the same inputs on both, compare -------------------------------------------
# Same flag shape as SlangcArguments.Build (the product's slangc command line): source on
# stdin, -target hlsl, flat cbuffers, -no-mangle, one entry point per run.
SHADERS="$SCRATCH/shaders"
rm -rf "$SHADERS"
mkdir -p "$SHADERS"
cp "$REPO_ROOT/tests/fixtures/shaders/slang/WaveVertex.slang" "$SHADERS/"
cp "$REPO_ROOT/plan/PHASE-65-appendix/slang-probe/shaders/GenericsProbe.slang" "$SHADERS/"

run_slangc() { # <slangc-cmd...> -- reads source from stdin, HLSL on stdout
    "$@" -lang slang -DOPENGL=1 -DGLSL=1 -DMGFX=1 -target hlsl \
        -no-hlsl-pack-constant-buffer-elements -no-mangle \
        -entry "$ENTRY" -stage "$STAGE" -- -
}

failed=0
count=0
# file:entry:stage
for spec in WaveVertex.slang:MainVS:vertex WaveVertex.slang:MainPS:fragment GenericsProbe.slang:MainPS:fragment; do
    IFS=: read -r file ENTRY STAGE <<<"$spec"
    out_x64="$SCRATCH/$file.$ENTRY.x64.hlsl"
    out_arm="$SCRATCH/$file.$ENTRY.arm64.hlsl"
    if ! (cd "$SHADERS" && run_slangc arch -x86_64 "$X64/slangc" <"$file" >"$out_x64" 2>"$out_x64.err"); then
        echo "::error::osx-x64 slangc (Rosetta) failed on $file $ENTRY:" >&2; cat "$out_x64.err" >&2
        failed=1; continue
    fi
    if ! (cd "$SHADERS" && run_slangc "$ARM/slangc" <"$file" >"$out_arm" 2>"$out_arm.err"); then
        echo "::error::osx-arm64 slangc failed on $file $ENTRY:" >&2; cat "$out_arm.err" >&2
        failed=1; continue
    fi
    if [ ! -s "$out_x64" ] || ! grep -qF "$ENTRY" "$out_x64"; then
        echo "::error::osx-x64 slangc produced no HLSL for $file $ENTRY" >&2
        failed=1; continue
    fi
    if ! cmp -s "$out_x64" "$out_arm"; then
        echo "::error::osx-x64 (Rosetta) and osx-arm64 slangc emit DIFFERENT HLSL for $file $ENTRY:" >&2
        diff "$out_arm" "$out_x64" | head -40 >&2 || true
        failed=1; continue
    fi
    count=$((count + 1))
    echo "  $file $ENTRY ($STAGE): identical, $(wc -c <"$out_x64" | tr -d ' ') bytes, sha256 $(shasum -a 256 "$out_x64" | cut -c1-16)"
done
[ "$failed" -eq 0 ] || exit 1
echo "verify-slang-osx-x64-rosetta: PASSED ($count/3 entry points, packaged osx-x64 slangc under Rosetta 2 == osx-arm64 slangc, byte-identical)"
