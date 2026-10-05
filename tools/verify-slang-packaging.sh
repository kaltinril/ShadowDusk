#!/usr/bin/env bash
# verify-slang-packaging.sh: pack ShadowDusk.Slang (+ the packages it pulls) into a local
# feed, gate the nupkg's natives, then consume it COLD from a scratch project outside the repo
# in both shapes a consumer builds (issue #225):
#   1. framework-dependent, non-RID-specific `dotnet run` (natives under bin/.../runtimes/<rid>/native/)
#   2. RID-specific self-contained `dotnet publish` (natives flattened next to the app)
# Exits non-zero on any failure. Used by .github/workflows/pack-consume.yml on all three OSes
# and runnable locally after tools/restore.sh (bash on Windows: Git Bash).
#
# usage: tools/verify-slang-packaging.sh [version] [tfm] [scratch-root]
#   version       package version to pack (default: <Directory.Build.props Version>-slangsmoke.local)
#   tfm           consumer TargetFramework (default net8.0)
#   scratch-root  directory OUTSIDE the repo for the feed + consumer (default: a mktemp dir)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

base=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/Directory.Build.props" | head -n1)
VERSION="${1:-${base}-slangsmoke.local}"
TFM="${2:-net8.0}"
SCRATCH="${3:-$(mktemp -d)}"
# Git Bash on Windows: use mixed "D:/a/..." paths, which both bash and the Windows-native
# dotnet/NuGet understand (a POSIX "/d/a/..." path in nuget.config would not resolve).
if command -v cygpath >/dev/null 2>&1; then
    SCRATCH="$(cygpath -m "$SCRATCH")"
    REPO_ROOT="$(cygpath -m "$REPO_ROOT")"
    SCRIPT_DIR="$(cygpath -m "$SCRIPT_DIR")"
fi
FEED="$SCRATCH/slang-feed"
APP="$SCRATCH/sd-scratch-slang-consumer"

case "$(uname -s)" in
    Linux*)  RID=linux-x64 ;;
    Darwin*) if [ "$(uname -m)" = "arm64" ]; then RID=osx-arm64; else RID=osx-x64; fi ;;
    MINGW*|MSYS*|CYGWIN*) RID=win-x64 ;;
    *) echo "verify-slang-packaging: unsupported host $(uname -s)" >&2; exit 1 ;;
esac

echo "== version $VERSION, consumer $TFM, host $RID, scratch $SCRATCH"
case "$SCRATCH" in "$REPO_ROOT"*)
    echo "::error::scratch root must be OUTSIDE the repo (a repo walk-up would find tools/slang/ and hide a packaging bug)" >&2
    exit 1 ;;
esac

# ---- pack ---------------------------------------------------------------------------------
mkdir -p "$FEED"
for proj in Core HLSL GLSL Compiler Slang; do
    dotnet pack "$REPO_ROOT/src/ShadowDusk.$proj/ShadowDusk.$proj.csproj" -c Release -o "$FEED" \
        -p:Version="$VERSION" --nologo -v quiet
done

# ---- pack gate: every RID's natives + the notice (the same script release.yml gates on) -----
bash "$SCRIPT_DIR/verify-slang-nupkg.sh" "$FEED/ShadowDusk.Slang.$VERSION.nupkg"

# ---- scaffold the cold consumer ----------------------------------------------------------
rm -rf "$APP"
mkdir -p "$APP"
cp "$SCRIPT_DIR/slang-consumer/Program.cs" "$APP/"
sed -e "s|SMOKE_VERSION|$VERSION|" \
    -e "s|<TargetFramework>net8.0</TargetFramework>|<TargetFramework>$TFM</TargetFramework>|" \
    "$SCRIPT_DIR/slang-consumer/ScratchSlangConsumer.csproj" > "$APP/ScratchSlangConsumer.csproj"
cp "$REPO_ROOT/plan/PHASE-65-appendix/slang-probe/shaders/GenericsProbe.slang" "$APP/"
cp "$REPO_ROOT/tests/fixtures/shaders/slang/WaveVertex.slang" "$APP/"
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

# A private package cache: the consumer's ShadowDusk.* must be extracted by NuGet from THIS
# feed, never satisfied from a global-cache copy of an earlier run with the same version.
export NUGET_PACKAGES="$SCRATCH/nuget-packages"

# ---- shape 1: framework-dependent, non-RID-specific (the default `dotnet run`) ------------
echo "== shape 1: framework-dependent dotnet run"
(cd "$APP" && dotnet run -c Release)

# ---- shape 2: RID-specific self-contained publish ------------------------------------------
echo "== shape 2: self-contained publish -r $RID"
(cd "$APP" && dotnet publish -c Release -r "$RID" --self-contained -o "$APP/publish-$RID" --nologo -v quiet)
exe="$APP/publish-$RID/ScratchSlangConsumer"
[ "$RID" = win-x64 ] && exe="$exe.exe"
(cd "$APP/publish-$RID" && "$exe")

echo "verify-slang-packaging: PASSED ($RID, $TFM, both consumer shapes)"
