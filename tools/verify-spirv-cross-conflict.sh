#!/usr/bin/env bash
# verify-spirv-cross-conflict.sh: the issue #350 end-to-end check, the SPIRV-Cross counterpart of
# verify-vortice-dxc-conflict.sh. A cold consumer of the packed ShadowDusk.Compiler whose graph
# resolves a Silk.NET.SPIRV.Cross.Native other than the 2.23.0 ShadowDusk pins (a Silk.NET game is
# the realistic case) must get:
#   1. at BUILD time: warning SD0226 (buildTransitive/ShadowDusk.GLSL.targets), and the build must
#      still succeed (a warning, never an error);
#   2. at RUN time: SD0103 for OpenGL (SpvcLoader refuses the different SPIRV-Cross by SHA-256),
#      never a raw exception or a compile with a different SPIRV-Cross, while DirectX 11 and FNA
#      (no SPIRV-Cross) keep compiling;
# and the fix the messages name (pin Silk.NET.SPIRV.Cross.Native 2.23.0) must clear both. 2.23.0
# is the newest release, so the conflict is forced DOWN to 2.22.0 (an explicit downgrade); a future
# release lifting it resolves the same way. Exits non-zero on any failure. Used by
# .github/workflows/pack-consume.yml on all three OSes; runnable locally (Git Bash on Windows) after
# packing Core/HLSL/GLSL/Compiler into a feed, or with --pack.
#
# usage: tools/verify-spirv-cross-conflict.sh [--pack] <feed-dir> <version> [tfm] [scratch-root]
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
FEED="${1:?usage: verify-spirv-cross-conflict.sh [--pack] <feed-dir> <version> [tfm] [scratch-root]}"
VERSION="${2:?version required}"
TFM="${3:-net8.0}"
SCRATCH="${4:-$(mktemp -d)}"
# The other release forced into the graph (2.23.0 is the newest, so the only other one is lower).
CONFLICT=2.22.0
PINNED=2.23.0

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

APP="$SCRATCH/sd-spirv-cross-conflict-consumer"
rm -rf "$APP"
mkdir -p "$APP"
cp "$SCRIPT_DIR/vortice-conflict-consumer/Program.cs" "$APP/"
sed -e "s|<TargetFramework>net8.0</TargetFramework>|<TargetFramework>$TFM</TargetFramework>|" \
    "$SCRIPT_DIR/spirv-cross-conflict-consumer/ScratchSpirvCrossConflict.csproj" > "$APP/ScratchSpirvCrossConflict.csproj"
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

# scenario <name> <silk version or ''>: build (output to build.log) and run (output to run.log).
scenario() {
    local name="$1" silk="$2"
    local out="$APP/out-$name"
    echo "== $name: Silk.NET.SPIRV.Cross.Native ${silk:-(transitive only)}"
    if ! (cd "$APP" && dotnet build -c Release --no-incremental -o "$out" \
            -p:SdVersion="$VERSION" -p:SilkVersion="$silk" --nologo > "$APP/build-$name.log" 2>&1); then
        cat "$APP/build-$name.log"
        echo "::error::$name: the consumer build FAILED (SD0226 must be a warning, never an error)"
        fail=1
        return 1
    fi
    (cd "$out" && dotnet ScratchSpirvCrossConflict.dll) > "$APP/run-$name.log" 2>&1 || true
    cat "$APP/run-$name.log"
}

# Lines are "Target=VALUE"; the value of one key, CR stripped (Windows dotnet writes CRLF).
value() { tr -d '\r' < "$1" | sed -n "s/^$2=//p" | head -n1; }

# ---- 1. the conflict: another Silk.NET.SPIRV.Cross.Native in the consumer's graph -----------
if scenario conflict "$CONFLICT"; then
    check "conflict: build warns SD0226" grep -q "warning SD0226" "$APP/build-conflict.log"
    check "conflict: SD0226 names the resolved $CONFLICT and the pin $PINNED" \
        grep -q "resolves Silk.NET.SPIRV.Cross.Native $CONFLICT, but ShadowDusk.GLSL runs only with Silk.NET.SPIRV.Cross.Native $PINNED" "$APP/build-conflict.log"
    check "conflict: OpenGL (SPIRV-Cross) is refused with SD0103" test "$(value "$APP/run-conflict.log" OpenGL)" = SD0103
    check "conflict: the SD0103 says it is not the pinned build and names the fix" \
        bash -c "tr -d '\r' < '$APP/run-conflict.log' | grep 'message.OpenGL=' | grep -q 'but not its pinned build.*Version=\"$PINNED\"'"
    check "conflict: DirectX 11 (no SPIRV-Cross) still compiles" test "$(value "$APP/run-conflict.log" DirectX)" = OK
    check "conflict: FNA (no SPIRV-Cross) still compiles" test "$(value "$APP/run-conflict.log" Fna)" = OK
fi

# ---- 2. the fix the messages name: pin Silk.NET.SPIRV.Cross.Native 2.23.0 -------------------
if scenario pinned "$PINNED"; then
    check "pinned: no SD0226" bash -c "! grep -q 'SD0226' '$APP/build-pinned.log'"
    check "pinned: OpenGL compiles" test "$(value "$APP/run-pinned.log" OpenGL)" = OK
    check "pinned: DirectX 11 compiles" test "$(value "$APP/run-pinned.log" DirectX)" = OK
    check "pinned: FNA compiles" test "$(value "$APP/run-pinned.log" Fna)" = OK
fi

# ---- 3. no Silk.NET.SPIRV.Cross.Native reference at all: the ordinary consumer sees nothing new
if scenario plain ""; then
    check "plain: no SD0226" bash -c "! grep -q 'SD0226' '$APP/build-plain.log'"
    check "plain: OpenGL compiles" test "$(value "$APP/run-plain.log" OpenGL)" = OK
fi

if [ "$fail" != 0 ]; then
    echo "verify-spirv-cross-conflict: FAILED (logs under $APP)" >&2
    exit 1
fi
echo "verify-spirv-cross-conflict: PASSED ($TFM; mismatch warned + refused, pin clears it)"
