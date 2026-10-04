#!/usr/bin/env bash
# tools/build-dxc-android.sh: the ONE recipe for ShadowDusk's Android DXC (libdxcompiler.so for
# android-arm64 and android-x64). .github/workflows/dxc-android-build.yml runs it; run it on Linux
# to reproduce a hosted file.
#
#   tools/build-dxc-android.sh <ndk-dir> <out-dir> <work-dir> [abi...]
#     abi: arm64-v8a and/or x86_64 (default both)
#
# Everything that decides the output bytes is pinned here:
#   * DXC_COMMIT e043f4a1 (DXC 1.7.2212.40, what Vortice.Dxc 3.3.4's Windows dxcompiler.dll
#     reports; the same commit the macOS and WASM builds use; Vortice's linux-x64 .so is the earlier
#     v1.7.2212 tag, see project_facts.md) and its three gitlinked submodules, each checked.
#   * The two CMAKE_CROSSCOMPILING patches the WASM recipe (.wasm-build/build-dxc-wasm.ps1)
#     applies, so the cross build uses the host tablegen built in stage 0 instead of spawning a
#     NATIVE sub-build. They change CMake only, never compiled code. The patched files are marked
#     assume-unchanged so `git describe --dirty` (DXC embeds it in its version string) reads
#     `e043f4a1`, as the clean macOS build does: "dxc(private) 1.7.0.1 (e043f4a1)".
#   * NDK_VERSION (checked), ANDROID_API, the CMake flags below.
#   * 16 KB pages: -z max-page-size / common-page-size = 16384 (Google Play requires 16 KB page
#     support for apps targeting Android 15+), checked by the workflow.
#   * -g0, -ffile-prefix-map, SOURCE_DATE_EPOCH (the commit time) and llvm-strip
#     --strip-unneeded: no debug info, no build path, no build time in the binary.
set -euo pipefail

DXC_REPO='https://github.com/microsoft/DirectXShaderCompiler.git'
DXC_COMMIT='e043f4a1286f4e1026222ab1bc94e25de8d0e959'
DXC_COMMIT_EPOCH='1677632696'   # 2023-03-01T01:04:56Z, the commit's committer date
SPIRV_HEADERS_COMMIT='1d31a100405cf8783ca7a31e31cdd727c9fc54c3'
SPIRV_TOOLS_COMMIT='40f5bf59c6acb4754a0bffd3c53a715732883a12'
DIRECTX_HEADERS_COMMIT='980971e835876dc0cde415e8f9bc646e64667bf7'
NDK_VERSION='27.2.12479018'      # NDK r27c
ANDROID_API='24'                 # the API level the hosted DXC has always been built for

ndk="${1:?usage: $0 <ndk-dir> <out-dir> <work-dir> [abi...]}"
out="${2:?usage: $0 <ndk-dir> <out-dir> <work-dir> [abi...]}"
work="${3:?usage: $0 <ndk-dir> <out-dir> <work-dir> [abi...]}"
shift 3
abis=("$@")
[ ${#abis[@]} -gt 0 ] || abis=(arm64-v8a x86_64)

have_ndk="$(sed -n 's/^Pkg.Revision *= *//p' "$ndk/source.properties" | tr -d '\r')"
if [ "$have_ndk" != "$NDK_VERSION" ]; then
    echo "error: NDK at $ndk is $have_ndk; this recipe is pinned to $NDK_VERSION" >&2
    exit 1
fi

cmake_exe="${CMAKE_EXE:-cmake}"
ninja_exe="${NINJA_EXE:-ninja}"
mkdir -p "$work" "$out"
work="$(cd "$work" && pwd)"
out="$(cd "$out" && pwd)"
src="$work/dxc-src"

# --- Source at the pinned commit + the three pinned submodules ---------------------------
if [ ! -d "$src/.git" ]; then
    git init -q "$src"
    git -C "$src" remote add origin "$DXC_REPO"
    git -C "$src" fetch -q --depth 1 origin "$DXC_COMMIT"
    git -C "$src" -c advice.detachedHead=false checkout -q FETCH_HEAD
    git -C "$src" submodule update -q --init --depth 1 \
        external/SPIRV-Headers external/SPIRV-Tools external/DirectX-Headers
fi
[ "$(git -C "$src" rev-parse HEAD)" = "$DXC_COMMIT" ] || { echo "error: dxc-src is not at $DXC_COMMIT" >&2; exit 1; }
for pair in "external/SPIRV-Headers:$SPIRV_HEADERS_COMMIT" \
            "external/SPIRV-Tools:$SPIRV_TOOLS_COMMIT" \
            "external/DirectX-Headers:$DIRECTX_HEADERS_COMMIT"; do
    path="${pair%%:*}"; want="${pair##*:}"
    have="$(git -C "$src/$path" rev-parse HEAD)"
    [ "$have" = "$want" ] || { echo "error: $path is at $have, pinned $want" >&2; exit 1; }
done

# --- The two cross-compile patches (CMake only; idempotent) -------------------------------
python3 - "$src" <<'PY'
import sys, pathlib
src = pathlib.Path(sys.argv[1])
p = src / 'CMakeLists.txt'
s = p.read_text().replace('\r\n', '\n')
old = 'if(CMAKE_CROSSCOMPILING OR (LLVM_OPTIMIZED_TABLEGEN AND LLVM_ENABLE_ASSERTIONS))\n  set(LLVM_USE_HOST_TOOLS ON)\nendif()'
new = 'if((CMAKE_CROSSCOMPILING OR (LLVM_OPTIMIZED_TABLEGEN AND LLVM_ENABLE_ASSERTIONS)) AND NOT DEFINED LLVM_USE_HOST_TOOLS) # ShadowDusk Phase23\n  set(LLVM_USE_HOST_TOOLS ON)\nendif()'
if 'NOT DEFINED LLVM_USE_HOST_TOOLS' not in s:
    assert old in s, 'PATCH 1 anchor not found'
    p.write_text(s.replace(old, new))
p = src / 'tools' / 'llvm-config' / 'CMakeLists.txt'
s = p.read_text()
if 'AND TARGET CONFIGURE_LLVM_NATIVE' not in s:
    assert 'if(CMAKE_CROSSCOMPILING)' in s, 'PATCH 2 anchor not found'
    p.write_text(s.replace('if(CMAKE_CROSSCOMPILING)', 'if(CMAKE_CROSSCOMPILING AND TARGET CONFIGURE_LLVM_NATIVE) # ShadowDusk Phase23', 1))
PY
git -C "$src" update-index --assume-unchanged CMakeLists.txt tools/llvm-config/CMakeLists.txt
[ -z "$(git -C "$src" status --porcelain --untracked-files=no)" ] || { echo "error: dxc-src is dirty" >&2; git -C "$src" status --short >&2; exit 1; }

predef="$src/cmake/caches/PredefinedParams.cmake"
common=(-G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja_exe" -DCMAKE_BUILD_TYPE=Release
        -DLLVM_INCLUDE_TESTS=OFF -DCLANG_INCLUDE_TESTS=OFF -DHLSL_INCLUDE_TESTS=OFF
        -DSPIRV_BUILD_TESTS=OFF -DLLVM_INCLUDE_DOCS=OFF -DLLVM_INCLUDE_EXAMPLES=OFF
        -DLLVM_TARGETS_TO_BUILD=None)

# --- Stage 0: host tablegen (its OUTPUT is generated source, independent of the host) ----
llvm_tblgen="${LLVM_TBLGEN:-}"
clang_tblgen="${CLANG_TBLGEN:-}"
if [ -z "$llvm_tblgen" ]; then
    host="$work/build-host-tblgen"
    "$cmake_exe" -S "$src" -B "$host" "${common[@]}" -C "$predef" > "$work/host.configure.log"
    "$ninja_exe" -C "$host" llvm-tblgen clang-tblgen > "$work/host.build.log"
    llvm_tblgen="$host/bin/llvm-tblgen"
    clang_tblgen="$host/bin/clang-tblgen"
fi

strip="$(ls -d "$ndk"/toolchains/llvm/prebuilt/*/bin | head -1)/llvm-strip"
export SOURCE_DATE_EPOCH="$DXC_COMMIT_EPOCH"

for abi in "${abis[@]}"; do
    case "$abi" in
        arm64-v8a) triple='aarch64-unknown-linux-android'; rid='android-arm64' ;;
        x86_64)    triple='x86_64-unknown-linux-android';  rid='android-x64' ;;
        *) echo "error: unsupported ABI $abi" >&2; exit 1 ;;
    esac
    build="$work/build-$rid"
    rm -rf "$build"
    flags="-g0 -ffile-prefix-map=$src=. -ffile-prefix-map=$build=. -ffile-prefix-map=$ndk=ndk"
    pages='-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384'
    "$cmake_exe" -S "$src" -B "$build" "${common[@]}" \
        "-DCMAKE_TOOLCHAIN_FILE=$ndk/build/cmake/android.toolchain.cmake" \
        "-DANDROID_ABI=$abi" "-DANDROID_PLATFORM=android-$ANDROID_API" \
        -DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON \
        "-DCMAKE_C_FLAGS=$flags" "-DCMAKE_CXX_FLAGS=$flags" \
        "-DCMAKE_SHARED_LINKER_FLAGS=$pages" \
        "-DLLVM_TABLEGEN=$llvm_tblgen" "-DCLANG_TABLEGEN=$clang_tblgen" \
        -DLLVM_USE_HOST_TOOLS=OFF "-DLLVM_INFERRED_HOST_TRIPLE=$triple" \
        -DENABLE_SPIRV_CODEGEN=ON \
        -C "$predef" > "$build.configure.log"
    "$ninja_exe" -C "$build" dxcompiler > "$build.build.log"
    mkdir -p "$out/$rid"
    cp "$build/lib/libdxcompiler.so" "$out/$rid/libdxcompiler.so"
    "$strip" --strip-unneeded "$out/$rid/libdxcompiler.so"
done

echo "DXC $DXC_COMMIT, NDK $NDK_VERSION, API $ANDROID_API"
(cd "$out" && sha256sum */libdxcompiler.so)
