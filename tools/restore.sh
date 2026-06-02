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
# vkd3d-shader (cross-platform DXBC backend, Phase 18 Track A)
# ---------------------------------------------------------------------------
# The vkd3d-shader native lib is a RESTORED artifact, NOT checked into the repo.
# Today only a locally-built win-x64 binary exists; this step verifies presence and
# documents the build recipe. Hosting per-RID artifacts (win-x64 / linux-x64 /
# osx-*) as a pinned GitHub Releases download is a follow-up. Runs unconditionally
# (before the spirv-cross early exits below).
restore_vkd3d_shader() {
    local vkd3d_dir="$REPO_ROOT/tools/vkd3d"
    mkdir -p "$vkd3d_dir"
    case "$OS" in
        Linux)  local vkd3d_lib="$vkd3d_dir/libvkd3d-shader.so.1" ;;
        Darwin) local vkd3d_lib="$vkd3d_dir/libvkd3d-shader.dylib" ;;
        *)      local vkd3d_lib="$vkd3d_dir/libvkd3d-shader-1.dll" ;;
    esac

    if [ -f "$vkd3d_lib" ]; then
        echo "restore.sh: vkd3d-shader ($(basename "$vkd3d_lib")) present — OK"
        return 0
    fi

    cat >&2 <<EOF

WARNING: vkd3d-shader native library not found at:
  $vkd3d_lib

Build recipe (vkd3d-shader 1.17, self-contained — zero non-system runtime deps).
The win-x64 binary was built with a portable MSYS2 + autotools toolchain:
  1. Download the vkd3d 1.17 release tarball:
       https://dl.winehq.org/vkd3d/source/  (vkd3d-1.17.tar.xz)
  2. Configure with Vulkan resolved to the system loader by SONAME and statically
     linked libgcc/winpthread so the DLL has no external runtime deps:
       ./configure --host=x86_64-w64-mingw32 \\
           SONAME_LIBVULKAN=vulkan-1.dll \\
           LDFLAGS="-static-libgcc -Wl,-Bstatic -lwinpthread -Wl,-Bdynamic"
       make
  3. Copy the resulting library to:
       $vkd3d_lib
     (Full recipe is in memory/Track-A report.)

  For Linux/macOS build the equivalent with the native autotools toolchain (the
  SONAME_LIBVULKAN / static-libgcc tricks are Windows-specific).

NOTE: This binary is NOT committed (.gitignore ignores tools/vkd3d/{*.dll,*.so*,*.dylib}).
EOF
    # Non-fatal: vkd3d is opt-in (default DXBC backend is the d3dcompiler oracle).
    return 0
}

restore_vkd3d_shader

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
    if [ -f "$out_dir/dxcompiler.wasm" ] && [ -f "$out_dir/dxcompiler.js" ]; then
        echo "restore.sh: DXC->WASM (dxcompiler.{js,wasm}) present — OK"
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
  5. Output: .wasm-build/dxc-wasm-out/dxcompiler.{js,wasm}. Wiring it into the
     ShadowDusk.Wasm packaged wwwroot is M1/M2 (NOT done here).

NOTE: dxcompiler.wasm is NOT committed (.gitignore ignores .wasm-build/). The full
recipe + build report are in .wasm-build/DXC-WASM-BUILD.md.
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
