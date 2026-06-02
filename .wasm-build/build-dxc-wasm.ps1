#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the FAITHFUL pinned DXC -> WebAssembly frontend (Phase 23 M0).

.DESCRIPTION
    Produces dxcompiler.{js,wasm}: an emscripten ES module exporting
        compileToSpirv(hlsl: string, args: string[]) -> Uint8Array
    built from the SAME DirectXShaderCompiler version the desktop pipeline uses
    (Vortice.Dxc 3.3.4 == DXC 1.7.2212.40, commit e043f4a1), so its SPIR-V is
    byte-identical to the desktop CLI on the corpus. That byte-identity (verified by
    node-test-dxc-wasm.mjs against .wasm-build/corpus-spirv/) is M0's DoD.

    Mirrors .wasm-build/build-spirv-cross-wasm.ps1. emscripten is PINNED to 3.1.34
    (the .NET 8 WASM runtime's version). The build has THREE stages:

      Stage 0  Host tablegen.  LLVM/Clang tablegen (llvm-tblgen, clang-tblgen) are
               BUILD-TIME tools that must run natively; an emscripten build would
               compile them to WASM (unrunnable). We first build ONLY those two
               targets with the host (MSVC) toolchain, then point the WASM configure
               at them via -DLLVM_TABLEGEN / -DCLANG_TABLEGEN. This is THE classic
               LLVM-cross-compile blocker.

      Stage 1  WASM libdxcompiler.  emcmake cmake -GNinja with the DXC PredefinedParams
               cache, SPIR-V codegen ON, ALL tests OFF (no googletest/effcee/re2),
               C++ exceptions via -fwasm-exceptions, then `ninja dxcompiler` (output lib/libdxcompiler.so).

      Stage 2  Link glue.  em++ links dxc-wasm-glue.cpp (embind compileToSpirv) against
               the Stage-1 archives into a MODULARIZE'd ES module.

    Stages are independently skippable (-SkipHostTblgen / -SkipLib) so a long LLVM
    build can be resumed without redoing finished stages.

.NOTES
    Source = microsoft/DirectXShaderCompiler @ e043f4a1286f4e1026222ab1bc94e25de8d0e959
             (the commit Vortice.Dxc 3.3.4's dxcompiler.dll reports: FileVersion
             1.7.2212.40, ProductVersion "1.7.2212.40 (e043f4a12)"). Submodules
             SPIRV-Headers @ 1d31a10, SPIRV-Tools @ 40f5bf5, DirectX-Headers @ 980971e
             are pinned to DXC's gitlinked SHAs.
#>
param(
    [switch]$SkipHostTblgen,   # reuse an existing host-tablegen build
    [switch]$SkipLib,          # reuse an existing WASM libdxcompiler build
    [switch]$ConfigureOnly,    # stop after CMake configure (don't run the long ninja build)
    [switch]$BuildOnly         # skip the emcmake configure; just run ninja + Stage 2 link
                               # (use after a validated configure; re-running emcmake on a
                               #  live build tree collides with ninja's log)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root    = 'C:\git\ShadowDusk\.wasm-build'
$src     = Join-Path $root 'dxc-src'
$emsdk   = Join-Path $root 'emsdk'
$glue    = Join-Path $root 'dxc-wasm-glue.cpp'

# Output co-located with the SPIRV-Cross module pattern; M1/M2 copies it into the
# ShadowDusk.Wasm packaged wwwroot. Here we only PRODUCE + VERIFY the artifact.
$outDir  = Join-Path $root 'dxc-wasm-out'
$outJs   = Join-Path $outDir 'dxcompiler.js'

$hostBuild = Join-Path $src 'build-host-tblgen'   # native tablegen tools
$wasmBuild = Join-Path $src 'build-wasm'          # emscripten libdxcompiler

# --- Toolchains -------------------------------------------------------------
$env:EMSDK     = $emsdk
$env:EM_CONFIG = Join-Path $emsdk '.emscripten'
$emcmake = Join-Path $emsdk 'upstream\emscripten\emcmake.bat'
$empp    = Join-Path $emsdk 'upstream\emscripten\em++.bat'

# Host CMake + Ninja (Visual Studio bundles both). Override via $env:CMAKE_EXE / NINJA_EXE.
$cmake = $env:CMAKE_EXE
if (-not $cmake) {
    $cmake = (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022\*\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
$ninja = $env:NINJA_EXE
if (-not $ninja) {
    $ninja = (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022\*\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $cmake -or -not (Test-Path $cmake)) { throw "cmake.exe not found (set CMAKE_EXE env var)" }
if (-not $ninja -or -not (Test-Path $ninja)) { throw "ninja.exe not found (set NINJA_EXE env var)" }

Write-Host "DXC src : $src"
Write-Host "emsdk   : $emsdk  (emscripten 3.1.34 must be active)"
Write-Host "cmake   : $cmake"
Write-Host "ninja   : $ninja"

# Verify pinned emscripten.
$emVer = (Get-Content (Join-Path $emsdk 'upstream\emscripten\emscripten-version.txt') -Raw).Trim('"', ' ', "`r", "`n")
if ($emVer -ne '3.1.34') { throw "emscripten is '$emVer', expected 3.1.34. Run: emsdk activate 3.1.34" }

$predef = Join-Path $src 'cmake\caches\PredefinedParams.cmake'

# --- Apply DXC source patches for the emscripten/WASM SPIR-V build (idempotent) ------
# See dxc-wasm-patches.txt. PATCH 1: CMakeLists.txt forces LLVM_USE_HOST_TOOLS ON under
# CMAKE_CROSSCOMPILING (emscripten), which spawns a NATIVE host-LLVM sub-build that
# wrongly probes emcc.bat as the host C compiler and fails. We supply prebuilt host
# tablegen, so the NATIVE build is unneeded — honor an explicit -DLLVM_USE_HOST_TOOLS=OFF.
$dxcCMake = Join-Path $src 'CMakeLists.txt'
$cm = Get-Content -LiteralPath $dxcCMake -Raw
if ($cm -notmatch 'NOT DEFINED LLVM_USE_HOST_TOOLS') {
    $p1old = "if(CMAKE_CROSSCOMPILING OR (LLVM_OPTIMIZED_TABLEGEN AND LLVM_ENABLE_ASSERTIONS))`r`n  set(LLVM_USE_HOST_TOOLS ON)`r`nendif()"
    if ($cm -notmatch [regex]::Escape($p1old)) { $p1old = $p1old -replace "`r`n","`n" }
    if ($cm -notmatch [regex]::Escape($p1old)) { throw "PATCH 1 anchor not found in $dxcCMake" }
    $p1new = "if((CMAKE_CROSSCOMPILING OR (LLVM_OPTIMIZED_TABLEGEN AND LLVM_ENABLE_ASSERTIONS)) AND NOT DEFINED LLVM_USE_HOST_TOOLS) # ShadowDusk Phase23`n  set(LLVM_USE_HOST_TOOLS ON)`nendif()"
    $cm = $cm.Replace($p1old, $p1new)
    Set-Content -LiteralPath $dxcCMake -Value $cm -NoNewline
    Write-Host "Applied PATCH 1 (LLVM_USE_HOST_TOOLS guard) to CMakeLists.txt"
} else {
    Write-Host "PATCH 1 already present in CMakeLists.txt"
}

# PATCH 2: tools/llvm-config/CMakeLists.txt wires a NATIVE-host llvm-config under
# CMAKE_CROSSCOMPILING, referencing CONFIGURE_LLVM_NATIVE. With PATCH 1 skipping the
# NATIVE build, that target is absent and CMake generate fails (add_dependencies on a
# missing target). Guard the block on the NATIVE target actually existing.
$llvmConfigCMake = Join-Path $src 'tools\llvm-config\CMakeLists.txt'
$lc = Get-Content -LiteralPath $llvmConfigCMake -Raw
if ($lc -notmatch 'AND TARGET CONFIGURE_LLVM_NATIVE') {
    $p2old = 'if(CMAKE_CROSSCOMPILING)'
    if ($lc -notmatch [regex]::Escape($p2old)) { throw "PATCH 2 anchor not found in $llvmConfigCMake" }
    $p2new = 'if(CMAKE_CROSSCOMPILING AND TARGET CONFIGURE_LLVM_NATIVE) # ShadowDusk Phase23'
    $idx = $lc.IndexOf($p2old)
    $lc = $lc.Substring(0,$idx) + $p2new + $lc.Substring($idx + $p2old.Length)
    Set-Content -LiteralPath $llvmConfigCMake -Value $lc -NoNewline
    Write-Host "Applied PATCH 2 (llvm-config NATIVE guard)"
} else {
    Write-Host "PATCH 2 already present in llvm-config/CMakeLists.txt"
}

# ---------------------------------------------------------------------------
# Stage 0 - native (host) tablegen tools.
# ---------------------------------------------------------------------------
$llvmTblgen  = Join-Path $hostBuild 'bin\llvm-tblgen.exe'
$clangTblgen = Join-Path $hostBuild 'bin\clang-tblgen.exe'

if (-not $SkipHostTblgen) {
    Write-Host "`n=== Stage 0: host tablegen ($hostBuild) ==="
    New-Item -ItemType Directory -Force -Path $hostBuild | Out-Null
    & $cmake -G Ninja -S $src -B $hostBuild `
        "-DCMAKE_MAKE_PROGRAM=$ninja" `
        -DCMAKE_BUILD_TYPE=Release `
        -DLLVM_INCLUDE_TESTS=OFF -DCLANG_INCLUDE_TESTS=OFF `
        -DHLSL_INCLUDE_TESTS=OFF -DSPIRV_BUILD_TESTS=OFF `
        -DLLVM_TARGETS_TO_BUILD=None -DLLVM_INCLUDE_DOCS=OFF -DLLVM_INCLUDE_EXAMPLES=OFF `
        -C $predef
    if ($LASTEXITCODE -ne 0) { throw "Stage 0 configure failed ($LASTEXITCODE)" }
    & $ninja -C $hostBuild llvm-tblgen clang-tblgen
    if ($LASTEXITCODE -ne 0) { throw "Stage 0 build failed ($LASTEXITCODE)" }
}
if (-not (Test-Path $llvmTblgen) -or -not (Test-Path $clangTblgen)) {
    throw "host tablegen tools missing: $llvmTblgen / $clangTblgen"
}
Write-Host "host tablegen OK: $llvmTblgen ; $clangTblgen"

# ---------------------------------------------------------------------------
# Stage 1 - WASM libdxcompiler (the long LLVM-fork build).
# ---------------------------------------------------------------------------
if (-not $SkipLib) {
    Write-Host "`n=== Stage 1: WASM libdxcompiler ($wasmBuild) ==="
    New-Item -ItemType Directory -Force -Path $wasmBuild | Out-Null

    # -fwasm-exceptions across compile AND link (DXC throws internally; the default
    # no-exceptions wasm build traps). RTTI/EH on (PredefinedParams sets LLVM_ENABLE_EH/RTTI).
    $exFlags = '-fwasm-exceptions'

    if (-not $BuildOnly) {
    & $emcmake $cmake -G Ninja -S $src -B $wasmBuild `
        "-DCMAKE_MAKE_PROGRAM=$ninja" `
        -DCMAKE_BUILD_TYPE=Release `
        "-DLLVM_TABLEGEN=$llvmTblgen" `
        "-DCLANG_TABLEGEN=$clangTblgen" `
        -DENABLE_SPIRV_CODEGEN=ON `
        -DLLVM_INFERRED_HOST_TRIPLE=wasm32-unknown-emscripten `
        -DLLVM_USE_HOST_TOOLS=OFF `
        -DLLVM_INCLUDE_TESTS=OFF -DCLANG_INCLUDE_TESTS=OFF `
        -DHLSL_INCLUDE_TESTS=OFF -DSPIRV_BUILD_TESTS=OFF `
        -DLLVM_INCLUDE_DOCS=OFF -DLLVM_INCLUDE_EXAMPLES=OFF `
        -DLLVM_TARGETS_TO_BUILD=None `
        -DLLVM_ENABLE_THREADS=OFF `
        "-DCMAKE_CXX_FLAGS=$exFlags" "-DCMAKE_C_FLAGS=$exFlags" `
        "-DCMAKE_EXE_LINKER_FLAGS=$exFlags" `
        -C $predef
    if ($LASTEXITCODE -ne 0) { throw "Stage 1 configure failed ($LASTEXITCODE)" }

    if ($ConfigureOnly) { Write-Host "ConfigureOnly: stopping after Stage 1 configure."; return }
    } # end (-not $BuildOnly) configure

    & $ninja -C $wasmBuild dxcompiler
    if ($LASTEXITCODE -ne 0) { throw "Stage 1 build (dxcompiler) failed ($LASTEXITCODE)" }
}

# ---------------------------------------------------------------------------
# Stage 2 - link the embind glue into dxcompiler.{js,wasm}.
# ---------------------------------------------------------------------------
Write-Host "`n=== Stage 2: link glue -> $outJs ==="
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$libDir  = Join-Path $wasmBuild 'lib'
$incDir  = Join-Path $src 'include'

# Link the FULL DXC archive set. libdxcompiler.a holds only the DxcCreateInstance entry
# objects; the LLVM/clang/SPIRV-Tools code lives in ~49 sibling archives. emscripten's
# wasm-ld resolves across all .a on the line regardless of order, so we pass them all and
# wrap libdxcompiler in --whole-archive (its DllMain/registration objects aren't otherwise
# referenced but are needed for DxcCreateInstance to find the component table).
$dxcLib = Join-Path $libDir 'libdxcompiler.a'
$otherLibs = Get-ChildItem (Join-Path $libDir '*.a') | Where-Object { $_.Name -ne 'libdxcompiler.a' } | ForEach-Object { $_.FullName }

# -fms-extensions + -Wno-language-extension-token: WinAdapter.h uses __uuidof (an MS
# extension) for its CROSS_PLATFORM_UUIDOF COM shim; without these the glue fails to
# parse (DXC builds all its own sources with these flags too).
$linkArgs = @(
    $glue,
    '-std=c++17',
    '-fwasm-exceptions',
    '-fms-extensions',
    '-Wno-language-extension-token',
    '--bind',
    "-I`"$incDir`"",
    '-O3',
    '-sMODULARIZE=1',
    '-sEXPORT_ES6=1',
    '-sEXPORT_NAME=createDxcModule',
    '-sALLOW_MEMORY_GROWTH=1',
    '-sENVIRONMENT=web,node',
    '-sFILESYSTEM=0',
    # Stub dlopen/dlsym so DXC's OPTIONAL DXIL-validator probe (dxil.dll) fails
    # GRACEFULLY at init instead of trapping; the -spirv path never needs the signer.
    '--js-library', "`"$(Join-Path $root 'dxc-dlopen-stub.js')`"",
    '-Wl,--whole-archive', "`"$dxcLib`"", '-Wl,--no-whole-archive'
) + ($otherLibs | ForEach-Object { "`"$_`"" }) + @(
    '-o', "`"$outJs`""
)

Write-Host "em++ $($linkArgs -join ' ')"
& $empp @linkArgs
if ($LASTEXITCODE -ne 0) { throw "Stage 2 link failed ($LASTEXITCODE)" }

Write-Host "`n=== Build artifacts ==="
Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
