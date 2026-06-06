#Requires -Version 5.1
<#
.SYNOPSIS
    Restores native SPIRV-Cross C shared library binaries for all supported platforms.

.DESCRIPTION
    SPIRV-Cross does not publish prebuilt binaries on its GitHub releases page.
    This script attempts to obtain the library from the following sources in order:
      1. Vulkan SDK installation (VULKAN_SDK environment variable)
      2. vcpkg installed packages (VCPKG_ROOT environment variable)
    If neither source is available, the script prints instructions and exits with code 1.

    Place the resulting files at:
      tools/spirv-cross/win-x64/spirv-cross-c-shared.dll
      tools/spirv-cross/linux-x64/libspirv-cross-c-shared.so
      tools/spirv-cross/osx-x64/libspirv-cross-c-shared.dylib
      tools/spirv-cross/osx-arm64/libspirv-cross-c-shared.dylib
#>

param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolsDir = Join-Path $RepoRoot 'tools' 'spirv-cross'

$WinDll   = Join-Path $ToolsDir 'win-x64'   'spirv-cross-c-shared.dll'
$LinuxSo  = Join-Path $ToolsDir 'linux-x64' 'libspirv-cross-c-shared.so'
$OsxX64   = Join-Path $ToolsDir 'osx-x64'   'libspirv-cross-c-shared.dylib'
$OsxArm64 = Join-Path $ToolsDir 'osx-arm64' 'libspirv-cross-c-shared.dylib'

function EnsureDir([string]$Path) {
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null }
}

EnsureDir (Split-Path $WinDll)
EnsureDir (Split-Path $LinuxSo)
EnsureDir (Split-Path $OsxX64)
EnsureDir (Split-Path $OsxArm64)

# ---------------------------------------------------------------------------
# vkd3d-shader (cross-platform DXBC backend, Phase 18 Track A)
# ---------------------------------------------------------------------------
# The vkd3d-shader native lib is a RESTORED artifact, not checked into the repo.
# Today only a locally-built win-x64 binary exists; this step just verifies it is
# present and documents the build recipe. Hosting per-RID artifacts as a pinned
# GitHub Releases download is a follow-up (see note below). Invoked up-front so it
# always runs regardless of the spirv-cross restore state below.
function Restore-Vkd3dShader {
    $Vkd3dDir = Join-Path $RepoRoot 'tools' 'vkd3d'
    $WinVkd3d = Join-Path $Vkd3dDir 'libvkd3d-shader-1.dll'   # win-x64
    EnsureDir $Vkd3dDir

    if (Test-Path $WinVkd3d) {
        Write-Host "restore.ps1: vkd3d-shader (libvkd3d-shader-1.dll) present — OK"
        return
    }

    Write-Warning @"

vkd3d-shader native library not found at:
  $WinVkd3d

Build recipe (win-x64, vkd3d-shader 1.17, self-contained — zero non-system deps):
  1. Install a portable MSYS2 toolchain (mingw-w64 + autotools).
  2. Download the vkd3d 1.17 release tarball from
     https://dl.winehq.org/vkd3d/source/ (vkd3d-1.17.tar.xz).
  3. Configure with Vulkan resolved to the system loader by SONAME and statically
     linked libgcc/winpthread so the DLL has no external runtime deps:
        ./configure --host=x86_64-w64-mingw32 \
            SONAME_LIBVULKAN=vulkan-1.dll \
            LDFLAGS="-static-libgcc -Wl,-Bstatic -lwinpthread -Wl,-Bdynamic"
     make
  4. Copy the resulting libvkd3d-shader-1.dll to:
        $WinVkd3d
     (Full recipe is in memory/Track-A report.)

NOTE: This binary is NOT committed (.gitignore ignores tools/vkd3d/*.dll). Hosting
per-RID artifacts (win-x64 / linux-x64 / osx-*) as a pinned download is a follow-up.
"@
}

Restore-Vkd3dShader

# ---------------------------------------------------------------------------
# DXC -> WASM (faithful in-browser HLSL -> SPIR-V frontend, Phase 23 M0)
# ---------------------------------------------------------------------------
# The faithful in-browser frontend is the SAME DirectXShaderCompiler the desktop
# pipeline uses (Vortice.Dxc 3.3.4), compiled to WebAssembly so its SPIR-V is
# byte-identical to the desktop CLI (Option A — NO substitute compiler; Slang is
# sample-only). dxcompiler.{js,wasm} is a RESTORED/BUILT artifact, NOT checked into
# the repo (large). This step verifies presence and documents the build recipe; the
# build itself is out-of-band (an LLVM-fork emscripten build, scripted under
# .wasm-build/). Mirrors Restore-Vkd3dShader. Runs unconditionally.
function Restore-DxcWasm {
    $DxcWasmDir = Join-Path $RepoRoot '.wasm-build' 'dxc-wasm-out'
    $DxcWasmWasm = Join-Path $DxcWasmDir 'dxcompiler.wasm'
    $DxcWasmJs   = Join-Path $DxcWasmDir 'dxcompiler.js'

    # M1 (Phase 23) destination: the faithful DXC->WASM module must be present in the
    # ShadowDusk.Wasm package's packaged wwwroot/dxc/ so it ships as a Blazor static
    # web asset (served at _content/ShadowDusk.Wasm/dxc/). The 17.4 MB .wasm is
    # gitignored (see .gitignore) and copied here from the built artifact; the small
    # shadowdusk-dxc.js shim + dxc/dxcompiler.js loader are committed.
    $PkgDxcDir   = Join-Path $RepoRoot 'src' 'ShadowDusk.Wasm' 'wwwroot' 'dxc'
    $PkgDxcWasm  = Join-Path $PkgDxcDir 'dxcompiler.wasm'

    if ((Test-Path $DxcWasmWasm) -and (Test-Path $DxcWasmJs)) {
        Write-Host "restore.ps1: DXC->WASM (dxcompiler.{js,wasm}) present in .wasm-build — OK"
        # Copy the built .wasm into the package wwwroot for pack if it's missing or stale.
        if (-not (Test-Path $PkgDxcWasm) -or
            ((Get-Item $DxcWasmWasm).Length -ne (Get-Item $PkgDxcWasm).Length)) {
            New-Item -ItemType Directory -Force -Path $PkgDxcDir | Out-Null
            Copy-Item -Force $DxcWasmWasm $PkgDxcWasm
            Write-Host "restore.ps1: copied dxcompiler.wasm -> src/ShadowDusk.Wasm/wwwroot/dxc/ (for pack)"
        } else {
            Write-Host "restore.ps1: src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm present — OK"
        }
        return
    }

    # No built artifact under .wasm-build — but the package wwwroot copy may already be
    # populated (e.g. from a prior restore). If so, that's enough for build/pack.
    if (Test-Path $PkgDxcWasm) {
        Write-Host "restore.ps1: src/ShadowDusk.Wasm/wwwroot/dxc/dxcompiler.wasm present — OK (no .wasm-build source needed)"
        return
    }

    Write-Warning @"

DXC->WASM module not found at:
  $DxcWasmJs
  $DxcWasmWasm

Build recipe (faithful pinned DXC -> WebAssembly, emscripten 3.1.34 — the .NET 8 pin):

  PINNED SOURCE — microsoft/DirectXShaderCompiler @ commit
    e043f4a1286f4e1026222ab1bc94e25de8d0e959
  This is the EXACT commit Vortice.Dxc 3.3.4's dxcompiler.dll reports (FileVersion
  1.7.2212.40, ProductVersion '1.7.2212.40 (e043f4a12)') — the December-2022 DXC
  release branch (release-1.7.2212). Byte-identity requires this exact commit AND
  its gitlinked SPIR-V submodules:
    external/SPIRV-Headers  @ 1d31a100405cf8783ca7a31e31cdd727c9fc54c3
    external/SPIRV-Tools    @ 40f5bf59c6acb4754a0bffd3c53a715732883a12
    external/DirectX-Headers@ 980971e835876dc0cde415e8f9bc646e64667bf7

  1. Clone the pinned source with submodules into .wasm-build/dxc-src:
        git init .wasm-build/dxc-src
        cd .wasm-build/dxc-src
        git remote add origin https://github.com/microsoft/DirectXShaderCompiler.git
        git fetch --depth 1 origin e043f4a1286f4e1026222ab1bc94e25de8d0e959
        git checkout FETCH_HEAD
        git -c advice.detachedHead=false submodule update --init --recursive --depth 1 ``
            external/SPIRV-Headers external/SPIRV-Tools external/DirectX-Headers
  2. Install + activate emscripten 3.1.34 in .wasm-build/emsdk (the .NET 8 WASM
     runtime's pin; a mismatch fails at link/load, not cleanly):
        .wasm-build/emsdk/emsdk install 3.1.34
        .wasm-build/emsdk/emsdk activate 3.1.34
  3. Build via the scripted 3-stage recipe (host tablegen -> WASM libdxcompiler ->
     link the embind compileToSpirv glue), which captures all WASM patches:
       * Stage 0: build native llvm-tblgen + clang-tblgen with the HOST (MSVC)
         toolchain — LLVM tablegen is a build-time tool that must run natively;
         an emscripten build would compile it to WASM. Pointed at via
         -DLLVM_TABLEGEN / -DCLANG_TABLEGEN (the classic LLVM cross-compile gate).
       * Stage 1: emcmake cmake -GNinja -C cmake/caches/PredefinedParams.cmake
         -DENABLE_SPIRV_CODEGEN=ON, ALL tests OFF (LLVM/CLANG/HLSL/SPIRV), C++
         exceptions via -fwasm-exceptions (DXC throws internally; default
         no-exceptions WASM traps), -DLLVM_ENABLE_THREADS=OFF; then ninja libdxcompiler.
         COM resolves without the Windows runtime via DXC's bundled WinAdapter; the
         DXIL validator/signer (dxil.dll) is NOT built and NOT needed for -spirv.
       * Stage 2: em++ --bind dxc-wasm-glue.cpp -ldxcompiler -sMODULARIZE=1
         -sEXPORT_ES6=1 -sEXPORT_NAME=createDxcModule -sFILESYSTEM=0, exporting
         compileToSpirv(hlsl, args[]) -> Uint8Array (matches the shadowdusk-dxc
         JS contract; #includes are pre-flattened upstream so no FS is needed).
     Launcher (loads the MSVC host env, then runs the staged build):
        pwsh -NoProfile -File .wasm-build\Invoke-DxcWasmBuild.ps1
     (Resume long builds with -SkipHostTblgen / -SkipLib.)
  4. Byte-identity gate (M0 DoD): capture the desktop SPIR-V oracle, then assert the
     WASM module matches it byte-for-byte over the corpus:
        dotnet run --project .wasm-build\dxc-corpus-probe -- <repoRoot> .wasm-build\corpus-spirv
        node .wasm-build\node-test-dxc-wasm.mjs
  5. Output: .wasm-build/dxc-wasm-out/dxcompiler.{js,wasm}. This restore step (M1)
     then copies dxcompiler.wasm into src/ShadowDusk.Wasm/wwwroot/dxc/ so it ships as a
     packaged Blazor static web asset (served at _content/ShadowDusk.Wasm/dxc/). The
     package wwwroot copy is gitignored too (see .gitignore).

NOTE: dxcompiler.wasm is NOT committed (.gitignore ignores both .wasm-build/ and the
package wwwroot copy). The full recipe + a build report are in .wasm-build/DXC-WASM-BUILD.md.
"@
}

Restore-DxcWasm

if (-not $Force -and (Test-Path $WinDll)) {
    Write-Host "spirv-cross-c-shared.dll already present — skipping restore."
    exit 0
}

# Attempt 1: Vulkan SDK
if ($env:VULKAN_SDK) {
    $candidates = @(
        (Join-Path $env:VULKAN_SDK 'Bin'   'spirv-cross-c-shared.dll'),
        (Join-Path $env:VULKAN_SDK 'Lib'   'spirv-cross-c-shared.dll'),
        (Join-Path $env:VULKAN_SDK 'bin'   'spirv-cross-c-shared.dll')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            Write-Host "Copying from Vulkan SDK: $c"
            Copy-Item $c $WinDll -Force
            Write-Host "Win-x64 binary restored from Vulkan SDK."
            break
        }
    }
}

if (Test-Path $WinDll) {
    Write-Host "restore.ps1: win-x64 OK"
    exit 0
}

# Attempt 2: vcpkg
if ($env:VCPKG_ROOT) {
    $vcpkgDll = Join-Path $env:VCPKG_ROOT 'installed' 'x64-windows' 'bin' 'spirv-cross-c-shared.dll'
    if (Test-Path $vcpkgDll) {
        Write-Host "Copying from vcpkg: $vcpkgDll"
        Copy-Item $vcpkgDll $WinDll -Force
        Write-Host "Win-x64 binary restored from vcpkg."
        exit 0
    }
}

# No automatic source found — print manual instructions.
# NON-FATAL: SPIRV-Cross ships transitively via the Silk.NET.SPIRV.Cross.Native NuGet
# package (resolved at runtime under runtimes/<rid>/native/), so a tools/ copy is optional
# and its absence must NOT fail CI. This matches restore.sh, which only warns. The manual
# steps below are for niche local scenarios that bypass the NuGet-provided native.
Write-Warning @"

SPIRV-Cross C shared library not found in tools/ (optional — normally provided by the
Silk.NET.SPIRV.Cross.Native NuGet package). Manual steps if you need a tools/ copy:

  Option A — Vulkan SDK (recommended):
    1. Install the Vulkan SDK from https://vulkan.lunarg.com/sdk/home
    2. Ensure the VULKAN_SDK environment variable is set (the installer does this).
    3. Re-run this script.

  Option B — vcpkg:
    1. Install vcpkg (https://vcpkg.io)
    2. Run: vcpkg install spirv-cross:x64-windows
    3. Set VCPKG_ROOT to your vcpkg root directory.
    4. Re-run this script.

  Option C — Manual copy:
    Copy spirv-cross-c-shared.dll to:
      $WinDll

    For Linux cross-compilation output:
      $LinuxSo

    For macOS cross-compilation output:
      $OsxX64
      $OsxArm64

  Option D — Build from source:
    git clone https://github.com/KhronosGroup/SPIRV-Cross
    cd SPIRV-Cross
    cmake -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_SHARED=ON -B build
    cmake --build build --config Release
    # Copy build/Release/spirv-cross-c-shared.dll (Windows)
    #      build/libspirv-cross-c-shared.so        (Linux)
    #      build/libspirv-cross-c-shared.dylib      (macOS)
"@

# Exit 0: absence of the optional tools/ SPIRV-Cross copy is not a failure (see note above).
exit 0
