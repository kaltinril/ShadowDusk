#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the FAITHFUL pinned DXC -> android-arm64 libdxcompiler.so (Phase 50).

.DESCRIPTION
    The Android NDK analogue of build-dxc-wasm.ps1. It REUSES .wasm-build/dxc-src - the
    pinned DirectXShaderCompiler @ e043f4a1 (== Vortice.Dxc 3.3.4 / DXC 1.7.2212.40, the
    same compiler the desktop + WASM builds use, never a substitute) that build-dxc-wasm.ps1
    already cloned, patched (the two CMAKE_CROSSCOMPILING patches), and for which it built the
    HOST tablegen tools (llvm-tblgen.exe / clang-tblgen.exe). Those host tools are toolchain-
    agnostic, so this script skips Stage 0 and reuses them directly.

    It swaps the WASM/emscripten Stage 1 for an Android NDK cross-compile. Unlike the WASM
    build (which emits .a archives linked into a .wasm module in a Stage 2), a native ELF
    build emits lib/libdxcompiler.so DIRECTLY, so there is no Stage 2.

      Stage 1  cmake -G Ninja with the NDK android.toolchain.cmake (ANDROID_ABI=arm64-v8a),
               the reused host tablegen, SPIR-V codegen ON, all tests/docs OFF,
               LLVM_TARGETS_TO_BUILD=None, then `ninja dxcompiler`.

    Output: tools/dxc/android-arm64/libdxcompiler.so (DxcLoader bare-SONAME loads it from the
    APK lib/arm64-v8a/; ShadowDusk.HLSL.csproj packs it under runtimes/android-arm64/native).

.NOTES
    Prereq: run build-dxc-wasm.ps1 at least through Stage 0 first (it clones+patches dxc-src
    and builds the host tablegen this script reuses). This is the local-build recipe; the
    CI form is dxc-android-build.yml.
#>
param(
    [switch]$ConfigureOnly,   # stop after CMake configure (don't run the long ninja build)
    [switch]$BuildOnly,       # skip configure; just run ninja (after a validated configure)
    [string]$Ndk = $env:ANDROID_NDK_HOME,
    [string]$AndroidApi = '24',
    [string]$Abi = 'arm64-v8a'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = 'C:\git\ShadowDusk\.wasm-build'
$src  = Join-Path $root 'dxc-src'
$hostBuild   = Join-Path $src 'build-host-tblgen'
$llvmTblgen  = Join-Path $hostBuild 'bin\llvm-tblgen.exe'
$clangTblgen = Join-Path $hostBuild 'bin\clang-tblgen.exe'
$predef      = Join-Path $src 'cmake\caches\PredefinedParams.cmake'
$andBuild    = Join-Path $src "build-android-$Abi"

if (-not $Ndk) { $Ndk = 'E:\Android\SDK\ndk\27.2.12479018' }
$toolchain = Join-Path $Ndk 'build\cmake\android.toolchain.cmake'

# ABI -> LLVM host triple (bypasses config.guess) and -> .NET RID staging dir.
switch ($Abi) {
    'arm64-v8a' { $triple = 'aarch64-unknown-linux-android'; $rid = 'android-arm64' }
    'x86_64'    { $triple = 'x86_64-unknown-linux-android';  $rid = 'android-x64'   }
    default     { throw "unsupported ABI '$Abi' (use arm64-v8a or x86_64)" }
}

# cmake + ninja from the Android SDK cmake (it bundles ninja). Override via env.
$cmake = $env:CMAKE_EXE; if (-not $cmake) { $cmake = 'E:\Android\SDK\cmake\3.31.6\bin\cmake.exe' }
$ninja = $env:NINJA_EXE; if (-not $ninja) { $ninja = 'E:\Android\SDK\cmake\3.31.6\bin\ninja.exe' }

foreach ($p in @($src, $toolchain, $llvmTblgen, $clangTblgen, $predef, $cmake, $ninja)) {
    if (-not (Test-Path $p)) { throw "missing prerequisite: $p" }
}
if ((Get-Content (Join-Path $src 'CMakeLists.txt') -Raw) -notmatch 'ShadowDusk Phase23') {
    throw "dxc-src is missing the cross-compile patches; run build-dxc-wasm.ps1 first (clone+patch+host tablegen)."
}

Write-Host "NDK   : $Ndk"
Write-Host "ABI   : $Abi   API: $AndroidApi"
Write-Host "host tablegen: $llvmTblgen"
Write-Host "build : $andBuild"

if (-not $BuildOnly) {
    Write-Host "`n=== Configure (NDK arm64) ==="
    & $cmake -G Ninja -S $src -B $andBuild `
        "-DCMAKE_MAKE_PROGRAM=$ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
        "-DANDROID_ABI=$Abi" `
        "-DANDROID_PLATFORM=android-$AndroidApi" `
        -DCMAKE_BUILD_TYPE=Release `
        "-DLLVM_TABLEGEN=$llvmTblgen" `
        "-DCLANG_TABLEGEN=$clangTblgen" `
        -DENABLE_SPIRV_CODEGEN=ON `
        -DLLVM_USE_HOST_TOOLS=OFF `
        "-DLLVM_INFERRED_HOST_TRIPLE=$triple" `
        -DLLVM_INCLUDE_TESTS=OFF -DCLANG_INCLUDE_TESTS=OFF `
        -DHLSL_INCLUDE_TESTS=OFF -DSPIRV_BUILD_TESTS=OFF `
        -DLLVM_INCLUDE_DOCS=OFF -DLLVM_INCLUDE_EXAMPLES=OFF `
        -DLLVM_TARGETS_TO_BUILD=None `
        -C $predef
    if ($LASTEXITCODE -ne 0) { throw "configure failed ($LASTEXITCODE)" }
    if ($ConfigureOnly) { Write-Host "ConfigureOnly: stopping after configure."; return }
}

Write-Host "`n=== Build (ninja dxcompiler) - the long LLVM-fork compile ==="
& $ninja -C $andBuild dxcompiler
if ($LASTEXITCODE -ne 0) { throw "build (dxcompiler) failed ($LASTEXITCODE)" }

# Stage the shared lib for the loader/csproj.
$so = Join-Path $andBuild 'lib\libdxcompiler.so'
if (-not (Test-Path $so)) {
    $found = Get-ChildItem $andBuild -Recurse -Filter 'libdxcompiler.so' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $so = $found.FullName }
}
if (-not $so -or -not (Test-Path $so)) { throw "libdxcompiler.so not found under $andBuild" }

$dst = "C:\git\ShadowDusk\tools\dxc\$rid"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
$dstSo = Join-Path $dst 'libdxcompiler.so'
Copy-Item $so $dstSo -Force

# Strip debug info: a Release LLVM libdxcompiler.so still carries ~400 MB of debug_info;
# --strip-debug drops it to ~33 MB while KEEPING the exported DxcCreateInstance symbols.
$strip = Join-Path $Ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-strip.exe'
if (Test-Path $strip) {
    & $strip --strip-debug $dstSo
    Write-Host "stripped debug info"
}
Write-Host "STAGED tools/dxc/$rid/libdxcompiler.so = $((Get-Item $dstSo).Length) bytes"
