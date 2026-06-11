#!/usr/bin/env bash
# restore.sh — Restore native SPIRV-Cross C shared library for all supported platforms.
#
# SPIRV-Cross does not publish prebuilt binaries on its GitHub releases page.
# This script attempts to obtain the library from:
#   1. Vulkan SDK installation ($VULKAN_SDK environment variable)
#   2. System package manager (apt / brew)
#   3. vcpkg ($VCPKG_ROOT environment variable)
# If no source is available, manual instructions are printed and the script exits 1.
#
# Output paths:
#   tools/spirv-cross/win-x64/spirv-cross-c-shared.dll
#   tools/spirv-cross/linux-x64/libspirv-cross-c-shared.so
#   tools/spirv-cross/osx-x64/libspirv-cross-c-shared.dylib
#   tools/spirv-cross/osx-arm64/libspirv-cross-c-shared.dylib

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
TOOLS_DIR="$REPO_ROOT/tools/spirv-cross"

LINUX_SO="$TOOLS_DIR/linux-x64/libspirv-cross-c-shared.so"
OSX_X64="$TOOLS_DIR/osx-x64/libspirv-cross-c-shared.dylib"
OSX_ARM64="$TOOLS_DIR/osx-arm64/libspirv-cross-c-shared.dylib"

mkdir -p \
    "$TOOLS_DIR/win-x64" \
    "$TOOLS_DIR/linux-x64" \
    "$TOOLS_DIR/osx-x64" \
    "$TOOLS_DIR/osx-arm64"

FORCE="${1:-}"
OS="$(uname -s)"

# ---------------------------------------------------------------------------
# vkd3d-shader (cross-platform DXBC backend, Phase 18 Track A / Phase 39 FNA)
# ---------------------------------------------------------------------------
# The vkd3d-shader native lib is a RESTORED artifact, NOT checked into the repo.
# All four shipping RIDs are downloaded from the FIXED GitHub Release tag below and
# SHA-256-verified against the pins (Phase 37 C). Every host restores every RID:
# the binaries are small (~1-2 MB each) and that makes any machine pack-ready (the
# ShadowDusk.HLSL nupkg must contain all four — release.yml gates on it).
# Built from the pinned vkd3d 1.17 tarball by .github/workflows/build-vkd3d-natives.yml
# (linux on ubuntu:20.04 = glibc 2.31 baseline; macOS at MACOSX_DEPLOYMENT_TARGET=11.0;
# win-x64 is the MSYS2 build the Phase 18/39/40 goldens were proven against).
# Runs unconditionally (before the spirv-cross early exits below).
VKD3D_RELEASE_URL="https://github.com/kaltinril/ShadowDusk/releases/download/native-vkd3d-1.17"

# sha256 of a file, portable: coreutils sha256sum (linux, GH runners) or
# shasum (stock macOS). NOT a pipeline-with-|| — a pipeline's exit status is
# the last stage's (awk: always 0), so a || fallback there never fires.
vkd3d_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

# restore_vkd3d_file <asset-name> <dest-relative-to-tools/vkd3d> <sha256>
restore_vkd3d_file() {
    local asset="$1" dest_rel="$2" sha="$3"
    local vkd3d_dir="$REPO_ROOT/tools/vkd3d"
    local dest="$vkd3d_dir/$dest_rel"
    mkdir -p "$(dirname "$dest")"

    if [ -f "$dest" ]; then
        local have
        have="$(vkd3d_sha256 "$dest")"
        if [ "$have" = "$sha" ]; then
            echo "restore.sh: vkd3d-shader ($dest_rel) present, hash OK"
            return 0
        fi
        echo "restore.sh: vkd3d-shader ($dest_rel) hash mismatch — re-downloading (had $have)"
    fi

    if ! curl -fsSLo "$dest.tmp" "$VKD3D_RELEASE_URL/$asset"; then
        echo "restore.sh: WARNING — could not download $asset from $VKD3D_RELEASE_URL (offline?); vkd3d-dependent paths (FNA target, DxbcBackend.Vkd3d) will fail SD0211 / skip in tests." >&2
        rm -f "$dest.tmp"
        return 0   # non-fatal by design
    fi
    local got
    got="$(vkd3d_sha256 "$dest.tmp")"
    if [ "$got" != "$sha" ]; then
        echo "restore.sh: ERROR — $asset SHA-256 mismatch (expected $sha, got $got); discarding." >&2
        rm -f "$dest.tmp"
        return 0   # non-fatal, but the file is NOT placed
    fi
    mv -f "$dest.tmp" "$dest"
    echo "restore.sh: vkd3d-shader ($dest_rel) downloaded, hash OK"
}

restore_vkd3d_shader() {
    restore_vkd3d_file "libvkd3d-shader-1.dll" "libvkd3d-shader-1.dll" \
        "500cd915002aa95b17995954e69474031b32837fb16355ae9aa31d7bdd6f6718"
    restore_vkd3d_file "libvkd3d-shader.so.1" "libvkd3d-shader.so.1" \
        "4799589c3e7abd4cdb4f1a0bae5a74937fbff310fb1e8daafa86b510c6272afc"
    restore_vkd3d_file "libvkd3d-shader.1.osx-x64.dylib" "osx-x64/libvkd3d-shader.1.dylib" \
        "4acb13b8d8c4faac2b2180c4747a6da8a431889f2d6a776013c61a394fff8b9d"
    restore_vkd3d_file "libvkd3d-shader.1.osx-arm64.dylib" "osx-arm64/libvkd3d-shader.1.dylib" \
        "887aa64611014d03b23a1827973822fd98ede6684d773632391736f8749a9bf4"
}

restore_vkd3d_shader

# ---------------------------------------------------------------------------
# DXC macOS natives (libdxcompiler.dylib, Phase 37 A)
# ---------------------------------------------------------------------------
# Vortice.Dxc 3.3.4 ships NO macOS native (win-x64/win-arm64/linux-x64 only), so
# every CompileAsync on a Mac dies with DllNotFoundException — the Finding A
# product gap. The fix is OUR OWN libdxcompiler.dylib built from the EXACT pinned
# DXC commit the Vortice native reports (e043f4a1286f4e1026222ab1bc94e25de8d0e959,
# FileVersion 1.7.2212.40 — the same pin the WASM build below uses; same compiler,
# never a substitute), by .github/workflows/dxc-build.yml (osx-arm64 on macos-14,
# osx-x64 on macos-15-intel; MACOSX_DEPLOYMENT_TARGET 11.0/10.15; otool gate =
# system-only linkage). Both arches share one file name, so the restored layout is
# per-arch (tools/dxc/osx-{x64,arm64}/), exactly like vkd3d's. Every host restores
# both RIDs (pack-ready pattern; ShadowDusk.HLSL.csproj packs them under
# runtimes/osx-{x64,arm64}/native). Mirrors restore_vkd3d_shader.
#
# PENDING-FIRST-HOSTED-BUILD: the dylibs have not been built+hosted yet. Until the
# release tag below carries the assets and the SHA-256 pins replace the
# placeholders, this section skips with a notice (non-fatal — win/linux users are
# unaffected; macOS DXC stays a known gap). To finish: dispatch dxc-build.yml,
# download the artifacts, `gh release create native-dxc-1.7.2212.40 ...`, paste
# the printed SHA-256s here and in restore.ps1.
DXC_RELEASE_URL="https://github.com/kaltinril/ShadowDusk/releases/download/native-dxc-1.7.2212.40"
DXC_OSX_X64_SHA256="PENDING-FIRST-HOSTED-BUILD"
DXC_OSX_ARM64_SHA256="PENDING-FIRST-HOSTED-BUILD"

# restore_dxc_file <asset-name> <dest-relative-to-tools/dxc> <sha256>
restore_dxc_file() {
    local asset="$1" dest_rel="$2" sha="$3"
    local dxc_dir="$REPO_ROOT/tools/dxc"
    local dest="$dxc_dir/$dest_rel"

    if [ "$sha" = "PENDING-FIRST-HOSTED-BUILD" ]; then
        echo "restore.sh: NOTICE — DXC macOS native ($dest_rel) pin is a placeholder (no hosted build yet); skipping. macOS DXC remains unavailable until Phase 37 A's hosted artifacts land."
        return 0   # non-fatal by design while the pins are placeholders
    fi

    mkdir -p "$(dirname "$dest")"
    if [ -f "$dest" ]; then
        local have
        have="$(vkd3d_sha256 "$dest")"
        if [ "$have" = "$sha" ]; then
            echo "restore.sh: DXC macOS native ($dest_rel) present, hash OK"
            return 0
        fi
        echo "restore.sh: DXC macOS native ($dest_rel) hash mismatch — re-downloading (had $have)"
    fi

    if ! curl -fsSLo "$dest.tmp" "$DXC_RELEASE_URL/$asset"; then
        echo "restore.sh: WARNING — could not download $asset from $DXC_RELEASE_URL (offline?); DXC (the OpenGL pipeline frontend) will be unavailable on macOS." >&2
        rm -f "$dest.tmp"
        return 0   # non-fatal by design
    fi
    local got
    got="$(vkd3d_sha256 "$dest.tmp")"
    if [ "$got" != "$sha" ]; then
        echo "restore.sh: ERROR — $asset SHA-256 mismatch (expected $sha, got $got); discarding." >&2
        rm -f "$dest.tmp"
        return 0   # non-fatal, but the file is NOT placed
    fi
    mv -f "$dest.tmp" "$dest"
    echo "restore.sh: DXC macOS native ($dest_rel) downloaded, hash OK"
}

restore_dxc_macos() {
    restore_dxc_file "libdxcompiler.osx-x64.dylib" "osx-x64/libdxcompiler.dylib" \
        "$DXC_OSX_X64_SHA256"
    restore_dxc_file "libdxcompiler.osx-arm64.dylib" "osx-arm64/libdxcompiler.dylib" \
        "$DXC_OSX_ARM64_SHA256"
}

restore_dxc_macos

# ---------------------------------------------------------------------------
# DXC -> WASM (faithful in-browser HLSL -> SPIR-V frontend, Phase 23 M0)
# ---------------------------------------------------------------------------
# The faithful in-browser frontend is the SAME DirectXShaderCompiler the desktop
# pipeline uses (Vortice.Dxc 3.3.4), compiled to WebAssembly so its SPIR-V is
# byte-identical to the desktop CLI (Option A — NO substitute compiler; Slang is
# sample-only). dxcompiler.{js,wasm} is a BUILT artifact, NOT checked into the repo;
# the build is an out-of-band LLVM-fork emscripten build, scripted under .wasm-build/.
# This step verifies presence and documents the recipe. Runs unconditionally.
restore_dxc_wasm() {
    local out_dir="$REPO_ROOT/.wasm-build/dxc-wasm-out"
    # M1 (Phase 23) destination: the faithful DXC->WASM module must be present in the
    # ShadowDusk.Wasm package wwwroot/dxc/ so it ships as a Blazor static web asset
    # (served at _content/ShadowDusk.Wasm/dxc/). The 17.4 MB .wasm is gitignored and
    # copied here from the built artifact.
    local pkg_dir="$REPO_ROOT/src/ShadowDusk.Wasm/wwwroot/dxc"
    local pkg_wasm="$pkg_dir/dxcompiler.wasm"

    if [ -f "$out_dir/dxcompiler.wasm" ] && [ -f "$out_dir/dxcompiler.js" ]; then
        echo "restore.sh: DXC->WASM (dxcompiler.{js,wasm}) present in .wasm-build — OK"
        # Copy the built .wasm into the package wwwroot for pack if missing or stale.
        if [ ! -f "$pkg_wasm" ] || \
           [ "$(stat -c%s "$out_dir/dxcompiler.wasm" 2>/dev/null || stat -f%z "$out_dir/dxcompiler.wasm")" != \
             "$(stat -c%s "$pkg_wasm" 2>/dev/null || stat -f%z "$pkg_wasm" 2>/dev/null)" ]; then
            mkdir -p "$pkg_dir"
            cp -f "$out_dir/dxcompiler.wasm" "$pkg_wasm"
            echo "restore.sh: copied dxcompiler.wasm -> src/ShadowDusk.Wasm/wwwroot/dxc/ (for pack)"
        else
            echo "restore.sh: src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm present — OK"
        fi
        return 0
    fi

    # No built artifact under .wasm-build — but the package wwwroot copy may already be
    # populated. If so, that's enough for build/pack.
    if [ -f "$pkg_wasm" ]; then
        echo "restore.sh: src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm present — OK (no .wasm-build source needed)"
        return 0
    fi

    cat >&2 <<'EOF'

WARNING: DXC->WASM module (.wasm-build/dxc-wasm-out/dxcompiler.{js,wasm}) not found.

Build recipe (faithful pinned DXC -> WebAssembly, emscripten 3.1.34 — the .NET 8 pin):

  PINNED SOURCE — microsoft/DirectXShaderCompiler @ commit
    e043f4a1286f4e1026222ab1bc94e25de8d0e959
  This is the EXACT commit Vortice.Dxc 3.3.4's dxcompiler.dll reports (FileVersion
  1.7.2212.40 == DXC December-2022 release branch). Byte-identity requires this exact
  commit AND its gitlinked SPIR-V submodules:
    external/SPIRV-Headers   @ 1d31a100405cf8783ca7a31e31cdd727c9fc54c3
    external/SPIRV-Tools     @ 40f5bf59c6acb4754a0bffd3c53a715732883a12
    external/DirectX-Headers @ 980971e835876dc0cde415e8f9bc646e64667bf7

  1. Clone the pinned source with submodules into .wasm-build/dxc-src:
        git init .wasm-build/dxc-src && cd .wasm-build/dxc-src
        git remote add origin https://github.com/microsoft/DirectXShaderCompiler.git
        git fetch --depth 1 origin e043f4a1286f4e1026222ab1bc94e25de8d0e959
        git checkout FETCH_HEAD
        git -c advice.detachedHead=false submodule update --init --recursive --depth 1 \
            external/SPIRV-Headers external/SPIRV-Tools external/DirectX-Headers
  2. Install + activate emscripten 3.1.34 in .wasm-build/emsdk (the .NET 8 WASM pin;
     a mismatch fails at link/load, not cleanly):
        ./.wasm-build/emsdk/emsdk install 3.1.34 && ./.wasm-build/emsdk/emsdk activate 3.1.34
  3. Build (3 stages — captures all WASM patches):
       * Stage 0: native llvm-tblgen + clang-tblgen with the HOST compiler (LLVM
         tablegen must run natively; an emscripten build compiles it to WASM).
         Point the WASM configure at them via -DLLVM_TABLEGEN / -DCLANG_TABLEGEN.
       * Stage 1: emcmake cmake -GNinja -C cmake/caches/PredefinedParams.cmake
         -DENABLE_SPIRV_CODEGEN=ON, ALL tests OFF, C++ exceptions via -fwasm-exceptions
         (DXC throws internally; default no-exceptions WASM traps),
         -DLLVM_ENABLE_THREADS=OFF; ninja libdxcompiler. COM resolves via DXC's
         bundled WinAdapter; the DXIL validator/signer (dxil.dll) is NOT built and
         NOT needed for the -spirv target.
       * Stage 2: em++ --bind dxc-wasm-glue.cpp -ldxcompiler -sMODULARIZE=1
         -sEXPORT_ES6=1 -sEXPORT_NAME=createDxcModule -sFILESYSTEM=0, exporting
         compileToSpirv(hlsl, args[]) -> Uint8Array (the shadowdusk-dxc JS contract).
     On Windows the staged build is scripted: .wasm-build/build-dxc-wasm.ps1
     (launcher .wasm-build/Invoke-DxcWasmBuild.ps1 loads the MSVC host env first).
     On Linux/macOS the host tablegen uses the native toolchain (clang/gcc) — same
     emcmake/em++ flags; no MSVC step.
  4. Byte-identity gate (M0 DoD): capture the desktop SPIR-V oracle, then assert the
     WASM module matches byte-for-byte over the corpus:
        dotnet run --project .wasm-build/dxc-corpus-probe -- <repoRoot> .wasm-build/corpus-spirv
        node .wasm-build/node-test-dxc-wasm.mjs
  5. Output: .wasm-build/dxc-wasm-out/dxcompiler.{js,wasm}. This restore step (M1)
     then copies dxcompiler.wasm into src/ShadowDusk.Wasm/wwwroot/dxc/ so it ships as a
     packaged Blazor static web asset (served at _content/ShadowDusk.Wasm/dxc/).

NOTE: dxcompiler.wasm is NOT committed (.gitignore ignores both .wasm-build/ and the
package wwwroot copy). The full recipe + build report are in .wasm-build/DXC-WASM-BUILD.md.
EOF
    return 0
}

restore_dxc_wasm

# Determine which output file we are targeting on this platform.
if [ "$OS" = "Linux" ]; then
    TARGET_FILE="$LINUX_SO"
    LIB_BASENAME="libspirv-cross-c-shared.so"
elif [ "$OS" = "Darwin" ]; then
    ARCH="$(uname -m)"
    if [ "$ARCH" = "arm64" ]; then
        TARGET_FILE="$OSX_ARM64"
    else
        TARGET_FILE="$OSX_X64"
    fi
    LIB_BASENAME="libspirv-cross-c-shared.dylib"
else
    echo "restore.sh: running on non-Linux/macOS host — only win-x64 restore is supported here."
    echo "Run tools/restore.ps1 on Windows to restore the Windows binary."
    exit 0
fi

if [ -z "$FORCE" ] && [ -f "$TARGET_FILE" ]; then
    echo "$(basename "$TARGET_FILE") already present — skipping restore."
    exit 0
fi

# Attempt 1: Vulkan SDK
if [ -n "${VULKAN_SDK:-}" ]; then
    for candidate in \
        "$VULKAN_SDK/lib/$LIB_BASENAME" \
        "$VULKAN_SDK/Lib/$LIB_BASENAME" \
        "$VULKAN_SDK/lib64/$LIB_BASENAME"
    do
        if [ -f "$candidate" ]; then
            echo "Copying from Vulkan SDK: $candidate"
            cp "$candidate" "$TARGET_FILE"
            echo "$(basename "$TARGET_FILE") restored from Vulkan SDK."
            exit 0
        fi
    done
fi

# Attempt 2: System package manager
if [ "$OS" = "Linux" ] && command -v dpkg-query &>/dev/null; then
    SYS_LIB="$(dpkg-query -L libspirv-cross-c-shared-dev 2>/dev/null | grep '\.so$' | head -1 || true)"
    if [ -n "$SYS_LIB" ] && [ -f "$SYS_LIB" ]; then
        echo "Copying from system: $SYS_LIB"
        cp "$SYS_LIB" "$TARGET_FILE"
        echo "$(basename "$TARGET_FILE") restored from system package."
        exit 0
    fi
    # Try ldconfig
    SYS_LIB="$(ldconfig -p 2>/dev/null | awk '/libspirv-cross-c-shared/{print $NF}' | head -1 || true)"
    if [ -n "$SYS_LIB" ] && [ -f "$SYS_LIB" ]; then
        echo "Copying from ldconfig path: $SYS_LIB"
        cp "$SYS_LIB" "$TARGET_FILE"
        echo "$(basename "$TARGET_FILE") restored via ldconfig."
        exit 0
    fi
fi

if [ "$OS" = "Darwin" ] && command -v brew &>/dev/null; then
    BREW_PREFIX="$(brew --prefix spirv-cross 2>/dev/null || true)"
    if [ -n "$BREW_PREFIX" ]; then
        BREW_LIB="$BREW_PREFIX/lib/$LIB_BASENAME"
        if [ -f "$BREW_LIB" ]; then
            echo "Copying from Homebrew: $BREW_LIB"
            cp "$BREW_LIB" "$TARGET_FILE"
            echo "$(basename "$TARGET_FILE") restored from Homebrew."
            exit 0
        fi
    fi
fi

# Attempt 3: vcpkg
if [ -n "${VCPKG_ROOT:-}" ]; then
    for triplet in x64-linux x64-osx arm64-osx; do
        VCPKG_LIB="$VCPKG_ROOT/installed/$triplet/lib/$LIB_BASENAME"
        if [ -f "$VCPKG_LIB" ]; then
            echo "Copying from vcpkg: $VCPKG_LIB"
            cp "$VCPKG_LIB" "$TARGET_FILE"
            echo "$(basename "$TARGET_FILE") restored from vcpkg."
            exit 0
        fi
    done
fi

# No automatic source found — print manual instructions.
cat <<EOF

ERROR: SPIRV-Cross C shared library not found. Manual steps to obtain it:

  Option A — Vulkan SDK (recommended):
    Download from https://vulkan.lunarg.com/sdk/home
    Set VULKAN_SDK to the SDK root (the installer does this on Linux/macOS).
    Re-run: ./tools/restore.sh

  Option B — Package manager:
    Ubuntu/Debian:  sudo apt-get install libspirv-cross-c-shared-dev
    macOS (Homebrew): brew install spirv-cross
    Then re-run: ./tools/restore.sh

  Option C — vcpkg:
    Install vcpkg (https://vcpkg.io) and run:
      vcpkg install spirv-cross
    Set VCPKG_ROOT to your vcpkg root directory.
    Re-run: ./tools/restore.sh

  Option D — Manual copy:
    Copy the shared library to:
      $TARGET_FILE

  Option E — Build from source:
    git clone https://github.com/KhronosGroup/SPIRV-Cross
    cd SPIRV-Cross
    cmake -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_SHARED=ON -B build
    cmake --build build
    # Then copy build/libspirv-cross-c-shared.{so,dylib} to the path above.

EOF
exit 1
