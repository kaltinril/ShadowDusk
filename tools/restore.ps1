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
Write-Warning @"

SPIRV-Cross C shared library not found. Manual steps to obtain it:

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

exit 1
