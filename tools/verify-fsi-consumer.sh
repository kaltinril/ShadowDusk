#!/usr/bin/env bash
# verify-fsi-consumer.sh: the issue #350 review regression check. A host that loads ShadowDusk in
# place from the NuGet global packages folder (`dotnet fsi` with `#r "nuget: ..."`, .NET
# Interactive / Polyglot notebooks) has no app-local copy of the natives and no deps.json naming
# them: SPIRV-Cross is reachable only in silk.net.spirv.cross.native/<ver>/runtimes/<rid>/native,
# beside shadowdusk.glsl/<ver>. SpvcLoader must find it there (and vkd3d / DXC in their own package
# folders), so OpenGL, DirectX 11 and FNA all compile. Before the fix OpenGL failed SD0103.
#
# The packages are restored into a PRIVATE global packages folder, so the test proves the
# in-place load from THIS feed's packages, never from a copy already in the user's cache.
# Exits non-zero on any failure. Used by .github/workflows/pack-consume.yml on all three OSes.
# A user NuGet.Config with packageSourceMapping that sends ShadowDusk.* only to nuget.org would
# resolve the published release instead; the script detects that and fails rather than pass.
#
# usage: tools/verify-fsi-consumer.sh [--pack] <feed-dir> <version> [scratch-root]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

PACK=0
if [ "${1:-}" = "--pack" ]; then PACK=1; shift; fi
FEED="${1:?usage: verify-fsi-consumer.sh [--pack] <feed-dir> <version> [scratch-root]}"
VERSION="${2:?version required}"
SCRATCH="${3:-$(mktemp -d)}"

mkdir -p "$FEED" "$SCRATCH"
FEED="$(cd "$FEED" && pwd)"
SCRATCH="$(cd "$SCRATCH" && pwd)"
if command -v cygpath >/dev/null 2>&1; then
    FEED="$(cygpath -m "$FEED")"
    SCRATCH="$(cygpath -m "$SCRATCH")"
    REPO_ROOT="$(cygpath -m "$REPO_ROOT")"
    SCRIPT_DIR="$(cygpath -m "$SCRIPT_DIR")"
fi

if [ "$PACK" = 1 ]; then
    for proj in Core HLSL GLSL Compiler; do
        dotnet pack "$REPO_ROOT/src/ShadowDusk.$proj/ShadowDusk.$proj.csproj" -c Release -o "$FEED" \
            -p:Version="$VERSION" --nologo -v quiet
    done
fi

APP="$SCRATCH/sd-fsi-consumer"
rm -rf "$APP"
mkdir -p "$APP"
cp "$REPO_ROOT/tests/fixtures/shaders/Grayscale.fx" "$APP/"
{
    echo "#i \"nuget: $FEED\""
    echo "#r \"nuget: ShadowDusk.Compiler, [$VERSION]\""
    cat "$SCRIPT_DIR/fsi-consumer/consume.fsx"
} > "$APP/consume.fsx"

export NUGET_PACKAGES="$APP/packages"
(cd "$APP" && dotnet fsi consume.fsx) > "$APP/run.log" 2>&1 || true
cat "$APP/run.log"

fail=0
check() {
    local what="$1"; shift
    if "$@"; then echo "  ok    $what"; else echo "::error::$what"; fail=1; fi
}
value() { tr -d '\r' < "$APP/run.log" | sed -n "s/^$1=//p" | head -n1; }

glsl="$(value glsl)"
check "ShadowDusk.GLSL $VERSION was loaded in place from the private packages folder ($glsl)" \
    bash -c "echo '$glsl' | tr '\\\\' '/' | grep -qi 'packages/shadowdusk.glsl/$VERSION/lib/'"
check "OpenGL (SPIRV-Cross from silk.net.spirv.cross.native) compiles" test "$(value OpenGL)" = OK
check "DirectX 11 (vkd3d from shadowdusk.hlsl) compiles" test "$(value DirectX)" = OK
check "FNA (vkd3d) compiles" test "$(value Fna)" = OK

if [ "$fail" != 0 ]; then
    echo "verify-fsi-consumer: FAILED (logs under $APP)" >&2
    exit 1
fi
echo "verify-fsi-consumer: PASSED (dotnet fsi, packages loaded in place from the global packages folder)"
