#!/usr/bin/env bash
# tools/build-spirv-cross-android.sh: the ONE recipe for ShadowDusk's Android SPIRV-Cross
# (libspirv-cross.so for android-arm64 and android-x64). .github/workflows/
# spirv-cross-android-build.yml runs it; run it locally to reproduce a hosted file.
#
#   tools/build-spirv-cross-android.sh <ndk-dir> <out-dir> [work-dir]
#
# Everything that decides the output bytes is pinned here:
#   * SPIRV_CROSS_REF: the SPIRV-Cross commit the DESKTOP natives (Silk.NET.SPIRV.Cross.Native
#     2.23.0) are built from, so Android transpiles SPIR-V to GLSL with the same compiler as the
#     desktop. Measured, not taken from Silk.NET's submodule pointer: see project_facts.md.
#   * the NDK (NDK_VERSION, checked against <ndk-dir>/source.properties), the API level, and the
#     CMake flags below.
#   * -g0 and -ffile-prefix-map: no debug info and no build path in the binary, so the bytes (and
#     the GNU build id SpvcLoader pins) do not depend on where the build ran. The workflow
#     builds every ABI twice in different directories and fails unless the two are identical.
set -euo pipefail

SPIRV_CROSS_REPO='https://github.com/KhronosGroup/SPIRV-Cross'
SPIRV_CROSS_REF='d8e3e2b141b8c8a167b2e3984736a6baacff316c'
SPIRV_CROSS_DATE='2025-07-01T10:36:51'   # the commit's committer date (UTC), stamped as the build time
NDK_VERSION='27.2.12479018'   # NDK r27c
ANDROID_API='21'              # validation/AndroidGl's SupportedOSPlatformVersion / GLES 2.0 baseline

ndk="${1:?usage: $0 <ndk-dir> <out-dir> [work-dir]}"
out="${2:?usage: $0 <ndk-dir> <out-dir> [work-dir]}"
work="${3:-$(mktemp -d)}"

have_ndk="$(sed -n 's/^Pkg.Revision *= *//p' "$ndk/source.properties" | tr -d '\r')"
if [ "$have_ndk" != "$NDK_VERSION" ]; then
    echo "error: NDK at $ndk is $have_ndk; this recipe is pinned to $NDK_VERSION" >&2
    exit 1
fi

mkdir -p "$work" "$out"
work="$(cd "$work" && pwd)"
out="$(cd "$out" && pwd)"
src="$work/SPIRV-Cross"
if [ ! -d "$src" ]; then
    curl -fsSL -o "$work/src.tgz" "$SPIRV_CROSS_REPO/archive/$SPIRV_CROSS_REF.tar.gz"
    tar -xzf "$work/src.tgz" -C "$work"
    mv "$work/SPIRV-Cross-$SPIRV_CROSS_REF" "$src"
    rm -f "$work/src.tgz"
fi

cmake_exe="${CMAKE_EXE:-cmake}"
ninja_args=()
if [ -n "${NINJA_EXE:-}" ]; then ninja_args=("-DCMAKE_MAKE_PROGRAM=$NINJA_EXE"); fi

for pair in "arm64-v8a:android-arm64" "x86_64:android-x64"; do
    abi="${pair%%:*}"
    rid="${pair##*:}"
    build="$work/build-$rid"
    rm -rf "$build"
    flags="-g0 -ffile-prefix-map=$src=. -ffile-prefix-map=$build=. -ffile-prefix-map=$ndk=ndk"
    "$cmake_exe" -S "$src" -B "$build" -G Ninja "${ninja_args[@]}" \
        -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
        -DANDROID_ABI="$abi" \
        -DANDROID_PLATFORM="android-$ANDROID_API" \
        -DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_C_FLAGS="$flags" \
        -DCMAKE_CXX_FLAGS="$flags" \
        -DSPIRV_CROSS_SHARED=ON \
        -DSPIRV_CROSS_STATIC=OFF \
        -DSPIRV_CROSS_CLI=OFF \
        -DSPIRV_CROSS_ENABLE_TESTS=OFF \
        -DCMAKE_DISABLE_FIND_PACKAGE_Git=ON > "$build.configure.log"
    # SPIRV-Cross stamps `git describe` and the CONFIGURE time into the library
    # (spvc_get_commit_revision_and_timestamp); pin both to the source commit so two builds
    # of one commit are byte-identical.
    printf '%s\n' \
        '#ifndef SPIRV_CROSS_GIT_VERSION_H_' \
        '#define SPIRV_CROSS_GIT_VERSION_H_' \
        "#define SPIRV_CROSS_GIT_REVISION \"Git commit: ${SPIRV_CROSS_REF:0:7} Timestamp: $SPIRV_CROSS_DATE\"" \
        '#endif' > "$build/gitversion.h"
    "$cmake_exe" --build "$build" > "$build.build.log"
    so="$(find "$build" -maxdepth 1 -name 'libspirv-cross-c-shared.so*' -type f | head -1)"
    [ -n "$so" ] || { echo "error: no libspirv-cross-c-shared.so under $build" >&2; exit 1; }
    mkdir -p "$out/$rid"
    cp "$so" "$out/$rid/libspirv-cross.so"
done

echo "SPIRV-Cross $SPIRV_CROSS_REF, NDK $NDK_VERSION, API $ANDROID_API"
(cd "$out" && sha256sum */libspirv-cross.so)
