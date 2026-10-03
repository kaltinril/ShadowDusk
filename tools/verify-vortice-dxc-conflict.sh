#!/usr/bin/env bash
# verify-vortice-dxc-conflict.sh: the issue #282 end-to-end check. A cold consumer of the packed
# ShadowDusk.Compiler whose graph lifts Vortice.Dxc above ShadowDusk's exact [3.3.4] range (what
# Evergine.DirectX12 does, measured: it pulls 3.8.3, whose natives are DXC 1.9.2602.17 and whose
# managed API is binary-incompatible) must get:
#   1. at BUILD time: warning SD0220 (buildTransitive/ShadowDusk.HLSL.targets) beside NuGet's NU1608,
#      and the build must still succeed (a warning, never an error);
#   2. at RUN time: SD0219 naming the resolved Vortice.Dxc for the DXC-backed target (OpenGL),
#      never a raw exception or a compile with a different DXC, while DirectX 11 and FNA (no DXC)
#      keep compiling;
# and the fix the messages name (pin Vortice.Dxc 3.3.4) must clear both. Exits non-zero on any
# failure. Used by .github/workflows/pack-consume.yml on all three OSes; runnable locally (bash on
# Windows: Git Bash) after packing Core/HLSL/GLSL/Compiler into a feed, or with --pack.
#
# usage: tools/verify-vortice-dxc-conflict.sh [--pack] <feed-dir> <version> [tfm] [scratch-root]
#   --pack        pack the four packages into <feed-dir> at <version> first (needs tools/restore.*)
#   feed-dir      local NuGet feed holding ShadowDusk.Compiler <version> and what it pulls
#   version       the ShadowDusk version to consume
#   tfm           consumer TargetFramework (default net8.0)
#   scratch-root  directory OUTSIDE the repo for the consumer (default: a mktemp dir)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

PACK=0
if [ "${1:-}" = "--pack" ]; then PACK=1; shift; fi
FEED="${1:?usage: verify-vortice-dxc-conflict.sh [--pack] <feed-dir> <version> [tfm] [scratch-root]}"
VERSION="${2:?version required}"
TFM="${3:-net8.0}"
SCRATCH="${4:-$(mktemp -d)}"
# The conflicting release: what Evergine.DirectX12 2026.5.26.2553 resolves (measured, issue #282).
CONFLICT=3.8.3
PINNED=3.3.4

mkdir -p "$FEED" "$SCRATCH"
FEED="$(cd "$FEED" && pwd)"
SCRATCH="$(cd "$SCRATCH" && pwd)"
# Git Bash on Windows: mixed "D:/a/..." paths, which both bash and the Windows dotnet/NuGet read.
if command -v cygpath >/dev/null 2>&1; then
    FEED="$(cygpath -m "$FEED")"
    SCRATCH="$(cygpath -m "$SCRATCH")"
    REPO_ROOT="$(cygpath -m "$REPO_ROOT")"
    SCRIPT_DIR="$(cygpath -m "$SCRIPT_DIR")"
fi
case "$SCRATCH" in "$REPO_ROOT"*)
    echo "::error::scratch root must be OUTSIDE the repo (the repo's Directory.*.props would leak in)" >&2
    exit 1 ;;
esac

if [ "$PACK" = 1 ]; then
    for proj in Core HLSL GLSL Compiler; do
        dotnet pack "$REPO_ROOT/src/ShadowDusk.$proj/ShadowDusk.$proj.csproj" -c Release -o "$FEED" \
            -p:Version="$VERSION" --nologo -v quiet
    done
fi

APP="$SCRATCH/sd-vortice-conflict-consumer"
rm -rf "$APP"
mkdir -p "$APP"
cp "$SCRIPT_DIR/vortice-conflict-consumer/Program.cs" "$APP/"
sed -e "s|<TargetFramework>net8.0</TargetFramework>|<TargetFramework>$TFM</TargetFramework>|" \
    "$SCRIPT_DIR/vortice-conflict-consumer/ScratchVorticeConflict.csproj" > "$APP/ScratchVorticeConflict.csproj"
cp "$REPO_ROOT/tests/fixtures/shaders/Grayscale.fx" "$APP/"
cat > "$APP/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="localfeed" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="localfeed"><package pattern="ShadowDusk.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF

fail=0
check() { # check <description> <command...>: record a failure instead of stopping at the first
    local what="$1"; shift
    if "$@"; then echo "  ok    $what"; else echo "::error::$what"; fail=1; fi
}

# scenario <name> <vortice version or ''>: build (output to build.log) and run (output to run.log).
scenario() {
    local name="$1" vortice="$2"
    local out="$APP/out-$name"
    echo "== $name: Vortice.Dxc ${vortice:-(transitive only)}"
    # --no-incremental + a per-scenario output dir: the warning comes from a target that runs after
    # ResolvePackageAssets, which an up-to-date incremental build would still run, but a clean
    # build keeps each scenario's evidence independent.
    if ! (cd "$APP" && dotnet build -c Release --no-incremental -o "$out" \
            -p:SdVersion="$VERSION" -p:VorticeVersion="$vortice" --nologo > "$APP/build-$name.log" 2>&1); then
        cat "$APP/build-$name.log"
        echo "::error::$name: the consumer build FAILED (SD0220 must be a warning, never an error)"
        fail=1
        return 1
    fi
    (cd "$out" && dotnet ScratchVorticeConflict.dll) > "$APP/run-$name.log" 2>&1 || true
    cat "$APP/run-$name.log"
}

# Lines are "Target=VALUE"; the value of one key, CR stripped (Windows dotnet writes CRLF).
value() { tr -d '\r' < "$1" | sed -n "s/^$2=//p" | head -n1; }

# ---- 1. the conflict: Vortice.Dxc 3.8.3 in the consumer's graph -----------------------------
if scenario conflict "$CONFLICT"; then
    check "conflict: build warns SD0220" grep -q "warning SD0220" "$APP/build-conflict.log"
    check "conflict: SD0220 names the resolved $CONFLICT and the pin $PINNED" \
        grep -q "resolves Vortice.Dxc $CONFLICT, but ShadowDusk.HLSL runs only with Vortice.Dxc $PINNED" "$APP/build-conflict.log"
    check "conflict: NuGet warns NU1608 for the same graph" grep -q "NU1608" "$APP/build-conflict.log"
    check "conflict: OpenGL (DXC) is refused with SD0219" test "$(value "$APP/run-conflict.log" OpenGL)" = SD0219
    check "conflict: the SD0219 names Vortice.Dxc $CONFLICT and the fix" \
        bash -c "tr -d '\r' < '$APP/run-conflict.log' | grep 'message.OpenGL=' | grep -q 'resolved Vortice.Dxc $CONFLICT.*Version=\"$PINNED\"'"
    check "conflict: DirectX 11 (no DXC) still compiles" test "$(value "$APP/run-conflict.log" DirectX)" = OK
    check "conflict: FNA (no DXC) still compiles" test "$(value "$APP/run-conflict.log" Fna)" = OK
fi

# ---- 2. the fix the messages name: pin Vortice.Dxc 3.3.4 ------------------------------------
if scenario pinned "$PINNED"; then
    check "pinned: no SD0220" bash -c "! grep -q 'SD0220' '$APP/build-pinned.log'"
    check "pinned: OpenGL compiles" test "$(value "$APP/run-pinned.log" OpenGL)" = OK
    check "pinned: DirectX 11 compiles" test "$(value "$APP/run-pinned.log" DirectX)" = OK
    check "pinned: FNA compiles" test "$(value "$APP/run-pinned.log" Fna)" = OK
fi

# ---- 3. no Vortice.Dxc reference at all: the ordinary consumer sees nothing new -------------
if scenario plain ""; then
    check "plain: no SD0220" bash -c "! grep -q 'SD0220' '$APP/build-plain.log'"
    check "plain: OpenGL compiles" test "$(value "$APP/run-plain.log" OpenGL)" = OK
fi

if [ "$fail" != 0 ]; then
    echo "verify-vortice-dxc-conflict: FAILED (logs under $APP)" >&2
    exit 1
fi
echo "verify-vortice-dxc-conflict: PASSED ($TFM; conflict warned + refused, pin clears it)"
