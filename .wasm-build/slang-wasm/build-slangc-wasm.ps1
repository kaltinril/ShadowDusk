#!/usr/bin/env pwsh
# Builds the in-process slangc WebAssembly module ShadowDusk.Slang uses where no process can
# be spawned (browser; issue #257). Output: shadowdusk-slangc.{js,wasm}.
#
# What it links: UPSTREAM'S OWN prebuilt static libraries from the official shader-slang
# release asset slang-<ver>-wasm-libs.zip (libslang-compiler.a and friends; the same build
# upstream links its own slang-wasm.js from), plus slangc-wasm-glue.cpp, which replays
# slangc's innerMain so ShadowDusk's slangc argument list reaches slang's own option parser
# verbatim. No slang source is built or patched here.
#
# Toolchain: emsdk 6.0.0, the version upstream's release.yml pins for its wasm build at
# v2026.14.1 (the static libraries' object format and libc ABI come from that toolchain).
#
# Usage:
#   pwsh .wasm-build/slang-wasm/build-slangc-wasm.ps1 [-EmsdkRoot <emsdk clone>] [-WorkDir <scratch>] [-OutDir <dir>]
# -OutDir defaults to src/ShadowDusk.Slang.Wasm/wwwroot/slangc/, where the browser package serves it.
# If -EmsdkRoot is omitted, the script clones emsdk into -WorkDir and installs 6.0.0.
param(
    [string]$EmsdkRoot,
    [string]$StackSize = '8MB',
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'shadowdusk-slangc-wasm'),
    [string]$OutDir = (Join-Path $PSScriptRoot '../../src/ShadowDusk.Slang.Wasm/wwwroot/slangc')
)
$ErrorActionPreference = 'Stop'

# Keep in sync with SlangToolPath.SlangVersion and tools/restore.* (the native slangc pin).
$SlangVersion = '2026.14.1'
$LibsZip = "slang-$SlangVersion-wasm-libs.zip"
$LibsZipSha256 = 'c6f1942f83324bb951c7d24aa30a5c32125206b182b2cf04508cc2ac4d42febd'
$EmsdkVersion = '6.0.0'

New-Item -ItemType Directory -Force $WorkDir, $OutDir | Out-Null

# 1. Upstream's prebuilt wasm static libraries, hash-pinned.
$zipPath = Join-Path $WorkDir $LibsZip
if (-not (Test-Path $zipPath) -or (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $LibsZipSha256) {
    Invoke-WebRequest "https://github.com/shader-slang/slang/releases/download/v$SlangVersion/$LibsZip" -OutFile $zipPath
}
$got = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($got -ne $LibsZipSha256) { throw "$LibsZip SHA-256 mismatch: expected $LibsZipSha256, got $got" }
$libs = Join-Path $WorkDir 'libs'
if (-not (Test-Path (Join-Path $libs 'lib/libslang-compiler.a'))) {
    Expand-Archive -Force $zipPath $libs
}

# 2. emsdk at upstream's pinned version.
if (-not $EmsdkRoot) {
    $EmsdkRoot = Join-Path $WorkDir 'emsdk'
    if (-not (Test-Path $EmsdkRoot)) {
        git clone --depth 1 https://github.com/emscripten-core/emsdk.git $EmsdkRoot
    }
}
$emsdkCmd = if ($IsWindows) { Join-Path $EmsdkRoot 'emsdk.bat' } else { Join-Path $EmsdkRoot 'emsdk' }
& $emsdkCmd install $EmsdkVersion | Out-Host
& $emsdkCmd activate $EmsdkVersion | Out-Host
$env:EM_CONFIG = Join-Path $EmsdkRoot '.emscripten'
$emxx = Join-Path $EmsdkRoot 'upstream/emscripten' ($IsWindows ? 'em++.bat' : 'em++')
if ($IsWindows -and -not (Test-Path $emxx)) { $emxx = Join-Path $EmsdkRoot 'upstream/emscripten/em++.exe' }

# 3. Link. Flags mirror upstream's emscripten preset (-fwasm-exceptions -Os,
# ALLOW_MEMORY_GROWTH); MODULARIZE/EXPORT_ES6 give the same lazy-loaded ES-module shape
# the other ShadowDusk.Wasm modules use. FORCE_FILESYSTEM: the glue stages stdin in MEMFS.
# STACK_SIZE: emscripten's default stack is 64 KB, against 1 MB for native slangc on Windows
# and 8 MB on Linux/macOS. slang's parser and IR passes recurse per nesting level, so at 64 KB
# valid shaders trapped ('memory access out of bounds') at ~70 nested ternaries or ~200 added
# terms where native slangc compiles twice that (PR #266 review). 8 MB matches the Linux/macOS
# main-thread default; wasm frames are larger than native ones, so the depth this buys is
# measured by node-test-slangc-wasm.mjs's depth probe, not assumed.
$lib = Join-Path $libs 'lib'
$emArgs = @(
    '-std=c++17', '-Os', '-fwasm-exceptions',
    "-I$(Join-Path $libs 'include')",
    (Join-Path $PSScriptRoot 'slangc-wasm-glue.cpp'),
    (Join-Path $lib 'libslang-compiler.a'),
    (Join-Path $lib 'libcompiler-core.a'),
    (Join-Path $lib 'libcore.a'),
    (Join-Path $lib 'libminiz.a'),
    (Join-Path $lib 'liblz4.a'),
    (Join-Path $lib 'libcmark-gfm.a'),
    '--bind',
    '-sMODULARIZE=1', '-sEXPORT_ES6=1', '-sEXPORT_NAME=createShadowDuskSlangc',
    '-sALLOW_MEMORY_GROWTH=1', '-sENVIRONMENT=web,worker,node', '-sFORCE_FILESYSTEM=1',
    "-sSTACK_SIZE=$StackSize",
    '-o', (Join-Path $OutDir 'shadowdusk-slangc.js')
)
& $emxx @emArgs
if ($LASTEXITCODE -ne 0) { throw "em++ failed ($LASTEXITCODE)" }

Get-ChildItem $OutDir -Filter 'shadowdusk-slangc.*' | ForEach-Object {
    '{0}  {1,12:N0}  {2}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Length, $_.Name
}
