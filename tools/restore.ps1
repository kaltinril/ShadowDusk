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
# vkd3d-shader (cross-platform DXBC backend, Phase 18 Track A / Phase 39 FNA)
# ---------------------------------------------------------------------------
# The vkd3d-shader native lib is a RESTORED artifact, not checked into the repo.
# All four shipping RIDs are downloaded from the FIXED GitHub Release tag below and
# SHA-256-verified against the pins (Phase 37 C). Every host restores every RID:
# the binaries are small (~1-2 MB each) and that makes any machine pack-ready (the
# ShadowDusk.HLSL nupkg must contain all four — release.yml gates on it).
#
# Provenance: linux/osx binaries are built by .github/workflows/build-vkd3d-natives.yml
# from the pinned vkd3d 1.17 tarball (linux on ubuntu:20.04 = glibc 2.31 baseline;
# macOS at MACOSX_DEPLOYMENT_TARGET=11.0, per-arch). The win-x64 dll is the MSYS2
# build the Phase 18/39/40 goldens and rung-4 validation were proven against:
#   ./configure --host=x86_64-w64-mingw32 \
#       SONAME_LIBVULKAN=vulkan-1.dll \
#       LDFLAGS="-static-libgcc -Wl,-Bstatic -lwinpthread -Wl,-Bdynamic"
#   make    # from vkd3d-1.17.tar.xz (https://dl.winehq.org/vkd3d/source/)
# Invoked up-front so it always runs regardless of the spirv-cross restore state below.
$Vkd3dReleaseUrl = 'https://github.com/kaltinril/ShadowDusk/releases/download/native-vkd3d-1.17'

function Restore-Vkd3dFile([string]$Asset, [string]$DestRel, [string]$Sha256) {
    $Vkd3dDir = Join-Path $RepoRoot 'tools' 'vkd3d'
    $Dest = Join-Path $Vkd3dDir $DestRel
    EnsureDir (Split-Path $Dest)

    if (Test-Path $Dest) {
        $have = (Get-FileHash -Algorithm SHA256 -Path $Dest).Hash.ToLowerInvariant()
        if ($have -eq $Sha256) {
            Write-Host "restore.ps1: vkd3d-shader ($DestRel) present, hash OK"
            return
        }
        Write-Host "restore.ps1: vkd3d-shader ($DestRel) hash mismatch — re-downloading (had $have)"
    }

    $tmp = "$Dest.tmp"
    try {
        Invoke-WebRequest -Uri "$Vkd3dReleaseUrl/$Asset" -OutFile $tmp -UseBasicParsing
    } catch {
        Write-Warning ("restore.ps1: could not download $Asset from $Vkd3dReleaseUrl (offline?); " +
            "vkd3d-dependent paths (FNA target, DxbcBackend.Vkd3d) will fail SD0211 / skip in tests. $_")
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
        return   # non-fatal by design
    }
    $got = (Get-FileHash -Algorithm SHA256 -Path $tmp).Hash.ToLowerInvariant()
    if ($got -ne $Sha256) {
        Write-Warning "restore.ps1: $Asset SHA-256 mismatch (expected $Sha256, got $got); discarding."
        Remove-Item -Force $tmp
        return   # non-fatal, but the file is NOT placed
    }
    Move-Item -Force $tmp $Dest
    Write-Host "restore.ps1: vkd3d-shader ($DestRel) downloaded, hash OK"
}

function Restore-Vkd3dShader {
    Restore-Vkd3dFile 'libvkd3d-shader-1.dll' 'libvkd3d-shader-1.dll' `
        '500cd915002aa95b17995954e69474031b32837fb16355ae9aa31d7bdd6f6718'
    Restore-Vkd3dFile 'libvkd3d-shader.so.1' 'libvkd3d-shader.so.1' `
        '4799589c3e7abd4cdb4f1a0bae5a74937fbff310fb1e8daafa86b510c6272afc'
    Restore-Vkd3dFile 'libvkd3d-shader.1.osx-x64.dylib' (Join-Path 'osx-x64' 'libvkd3d-shader.1.dylib') `
        '4acb13b8d8c4faac2b2180c4747a6da8a431889f2d6a776013c61a394fff8b9d'
    Restore-Vkd3dFile 'libvkd3d-shader.1.osx-arm64.dylib' (Join-Path 'osx-arm64' 'libvkd3d-shader.1.dylib') `
        '887aa64611014d03b23a1827973822fd98ede6684d773632391736f8749a9bf4'
}

Restore-Vkd3dShader

# ---------------------------------------------------------------------------
# DXC macOS natives (libdxcompiler.dylib, Phase 37 A)
# ---------------------------------------------------------------------------
# Vortice.Dxc 3.3.4 ships NO macOS native (win-x64/win-arm64/linux-x64 only), so
# every CompileAsync on a Mac dies with DllNotFoundException — the Finding A
# product gap. The fix is OUR OWN libdxcompiler.dylib built from the EXACT pinned
# DXC commit the Vortice native reports (e043f4a1286f4e1026222ab1bc94e25de8d0e959,
# FileVersion 1.7.2212.40 — the same pin Restore-DxcWasm below uses; same compiler,
# never a substitute), by .github/workflows/dxc-build.yml (osx-arm64 on macos-14,
# osx-x64 on macos-15-intel; MACOSX_DEPLOYMENT_TARGET 11.0/10.15; otool gate =
# system-only linkage). Both arches share one file name, so the restored layout is
# per-arch (tools/dxc/osx-{x64,arm64}/), exactly like vkd3d's. Every host restores
# both RIDs (pack-ready pattern; ShadowDusk.HLSL.csproj packs them under
# runtimes/osx-{x64,arm64}/native). Mirrors Restore-Vkd3dShader.
#
# Pins enforced since 2026-06-11: dylibs built by dxc-build.yml run 27327330108
# (green on both RIDs, otool gate + ps_6_0 -spirv smoke passed) and hosted on the
# fixed tag below. Same pin-discipline as vkd3d: hash mismatch -> re-download;
# offline -> non-fatal warning.
$DxcReleaseUrl = 'https://github.com/kaltinril/ShadowDusk/releases/download/native-dxc-1.7.2212.40'
$DxcOsxX64Sha256   = '9e61d5c1993d2cd5a5ea6701011d0a86e8c8dd89c995ef0c4d03ff3b83dbbc17'
$DxcOsxArm64Sha256 = '4f29ef90af61426a39037a2e9d7215a48c7c746328a38a20028e456c1ee3d811'

function Restore-DxcFile([string]$Asset, [string]$DestRel, [string]$Sha256) {
    $DxcDir = Join-Path $RepoRoot 'tools' 'dxc'
    $Dest = Join-Path $DxcDir $DestRel

    if ($Sha256 -eq 'PENDING-FIRST-HOSTED-BUILD') {
        Write-Host ("restore.ps1: NOTICE — DXC macOS native ($DestRel) pin is a placeholder " +
            "(no hosted build yet); skipping. macOS DXC remains unavailable until Phase 37 A's " +
            "hosted artifacts land.")
        return   # non-fatal by design while the pins are placeholders
    }

    EnsureDir (Split-Path $Dest)
    if (Test-Path $Dest) {
        $have = (Get-FileHash -Algorithm SHA256 -Path $Dest).Hash.ToLowerInvariant()
        if ($have -eq $Sha256) {
            Write-Host "restore.ps1: DXC macOS native ($DestRel) present, hash OK"
            return
        }
        Write-Host "restore.ps1: DXC macOS native ($DestRel) hash mismatch — re-downloading (had $have)"
    }

    $tmp = "$Dest.tmp"
    try {
        Invoke-WebRequest -Uri "$DxcReleaseUrl/$Asset" -OutFile $tmp -UseBasicParsing
    } catch {
        Write-Warning ("restore.ps1: could not download $Asset from $DxcReleaseUrl (offline?); " +
            "DXC (the OpenGL pipeline frontend) will be unavailable on macOS. $_")
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
        return   # non-fatal by design
    }
    $got = (Get-FileHash -Algorithm SHA256 -Path $tmp).Hash.ToLowerInvariant()
    if ($got -ne $Sha256) {
        Write-Warning "restore.ps1: $Asset SHA-256 mismatch (expected $Sha256, got $got); discarding."
        Remove-Item -Force $tmp
        return   # non-fatal, but the file is NOT placed
    }
    Move-Item -Force $tmp $Dest
    Write-Host "restore.ps1: DXC macOS native ($DestRel) downloaded, hash OK"
}

function Restore-DxcMacos {
    Restore-DxcFile 'libdxcompiler.osx-x64.dylib' (Join-Path 'osx-x64' 'libdxcompiler.dylib') `
        $DxcOsxX64Sha256
    Restore-DxcFile 'libdxcompiler.osx-arm64.dylib' (Join-Path 'osx-arm64' 'libdxcompiler.dylib') `
        $DxcOsxArm64Sha256
}

Restore-DxcMacos

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

# ---------------------------------------------------------------------------
# vkd3d-shader -> WASM (faithful in-browser DXBC + FNA backend, Phase 4.1)
# ---------------------------------------------------------------------------
# The faithful in-browser DirectX (SM4/5 DXBC) and FNA (SM1-3 fx_2_0) backend is the
# SAME pinned vkd3d-shader 1.17 the desktop pipeline P/Invokes (tag native-vkd3d-1.17
# above), compiled to WebAssembly (emscripten, MODULARIZE + EXPORT_ES6) — NO
# substitute compiler; output is gated byte-identical to the desktop backend
# (tests/ShadowDusk.BrowserTests/node-test-vkd3d-wasm.mjs). vkd3d-shader.{js,wasm}
# are RESTORED artifacts placed into the ShadowDusk.Wasm package wwwroot/vkd3d/ so
# they ship as Blazor static web assets (served at _content/ShadowDusk.Wasm/vkd3d/).
# Mirrors Restore-DxcWasm (local-build copy) + Restore-DxcMacos (pinned download with
# the PENDING-FIRST-HOSTED-BUILD placeholder pattern). Runs unconditionally.
#
# Pins: SHA-256 of the assets hosted on the native-vkd3d-wasm-1.17 prerelease (built
# by .github/workflows/vkd3d-wasm-build.yml from the pinned vkd3d-1.17 tarball,
# emscripten 3.1.34). Re-running the build workflow re-pins here + in SHA256SUMS.
$Vkd3dWasmReleaseUrl = 'https://github.com/kaltinril/ShadowDusk/releases/download/native-vkd3d-wasm-1.17'
$Vkd3dWasmJsSha256   = 'aff3ae6dece4d9aea38d32e3e7ed4c2d809dc0e0bf1c12bbaa4ad97e3b5dd7aa'
$Vkd3dWasmWasmSha256 = 'c80b8bb8a887a629aeb00951e5273a64598e6153b8580db428ee824f70f161e0'

function Restore-Vkd3dWasmFile([string]$Asset, [string]$Sha256) {
    $PkgVkd3dDir = Join-Path $RepoRoot 'src' 'ShadowDusk.Wasm' 'wwwroot' 'vkd3d'
    $Dest = Join-Path $PkgVkd3dDir $Asset

    # A locally built module takes precedence over the (possibly placeholder-pinned)
    # download — the Restore-DxcWasm pattern for developers iterating on the build.
    $LocalBuild = Join-Path $RepoRoot '.wasm-build' 'vkd3d-wasm-out' $Asset
    if (Test-Path $LocalBuild) {
        if (-not (Test-Path $Dest) -or
            ((Get-Item $LocalBuild).Length -ne (Get-Item $Dest).Length)) {
            EnsureDir $PkgVkd3dDir
            Copy-Item -Force $LocalBuild $Dest
            Write-Host "restore.ps1: copied $Asset (.wasm-build local build) -> src/ShadowDusk.Wasm/wwwroot/vkd3d/"
        } else {
            Write-Host "restore.ps1: vkd3d-shader WASM ($Asset) present (local build) — OK"
        }
        return
    }

    if ($Sha256 -eq 'PENDING-FIRST-HOSTED-BUILD') {
        Write-Host ("restore.ps1: NOTICE — vkd3d-shader WASM ($Asset) pin is a placeholder " +
            "(no hosted build on $Vkd3dWasmReleaseUrl yet); skipping. Browser DirectX/FNA " +
            "export stays unavailable (SD1902) until the Phase 4.1 hosted artifacts land.")
        return   # non-fatal by design while the pins are placeholders
    }

    EnsureDir $PkgVkd3dDir
    if (Test-Path $Dest) {
        $have = (Get-FileHash -Algorithm SHA256 -Path $Dest).Hash.ToLowerInvariant()
        if ($have -eq $Sha256) {
            Write-Host "restore.ps1: vkd3d-shader WASM ($Asset) present, hash OK"
            return
        }
        Write-Host "restore.ps1: vkd3d-shader WASM ($Asset) hash mismatch — re-downloading (had $have)"
    }

    $tmp = "$Dest.tmp"
    try {
        Invoke-WebRequest -Uri "$Vkd3dWasmReleaseUrl/$Asset" -OutFile $tmp -UseBasicParsing
    } catch {
        Write-Warning ("restore.ps1: could not download $Asset from $Vkd3dWasmReleaseUrl (offline?); " +
            "browser DirectX/FNA export will fail SD1902 / the vkd3d-wasm gate will skip. $_")
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
        return   # non-fatal by design
    }
    $got = (Get-FileHash -Algorithm SHA256 -Path $tmp).Hash.ToLowerInvariant()
    if ($got -ne $Sha256) {
        Write-Warning "restore.ps1: $Asset SHA-256 mismatch (expected $Sha256, got $got); discarding."
        Remove-Item -Force $tmp
        return   # non-fatal, but the file is NOT placed
    }
    Move-Item -Force $tmp $Dest
    Write-Host "restore.ps1: vkd3d-shader WASM ($Asset) downloaded, hash OK"
}

function Restore-Vkd3dWasm {
    Restore-Vkd3dWasmFile 'vkd3d-shader.js'   $Vkd3dWasmJsSha256
    Restore-Vkd3dWasmFile 'vkd3d-shader.wasm' $Vkd3dWasmWasmSha256
}

Restore-Vkd3dWasm

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
