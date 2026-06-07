# Builds SPIRV-Cross's C API (spirv_cross_c) to a standalone WebAssembly ES module
# with emscripten, for the in-browser SPIR-V -> GLSL backend used by
# samples/ShaderFiddle.Web (the `shadowdusk-spirv-cross` [JSImport] module).
#
# Source = KhronosGroup/SPIRV-Cross @ tag vulkan-sdk-1.4.335.0 (the closest public
# tag to the Silk.NET.SPIRV.Cross.Native 2.23.0 desktop binary; the exact commit
# Silk.NET pinned -- 94605142... -- is not present in the public repo). Byte-for-byte
# parity with the desktop GLSL is VERIFIED separately against the shipped
# spirv-cross.dll by node-test-spirv-cross.mjs.
#
# All five backends (GLSL/HLSL/MSL/CPP/REFLECT) are compiled with the SAME defines
# the spirv-cross-c-shared CMake target uses, so the C API behaves identically to
# the desktop shared library; only transpileToGlsl is reached at runtime.
$ErrorActionPreference = 'Stop'

$root      = 'C:\git\ShadowDusk\.wasm-build'
$src       = Join-Path $root 'spirv-cross-src'
$emsdk     = Join-Path $root 'emsdk'
$emcc      = Join-Path $emsdk 'upstream\emscripten\emcc.bat'
$outDir    = 'C:\git\ShadowDusk\samples\ShaderFiddle.Web\wwwroot\spirv-cross'
$outJs     = Join-Path $outDir 'spirv-cross.js'

$env:EMSDK     = $emsdk
$env:EM_CONFIG = Join-Path $emsdk '.emscripten'

# Authoritative .cpp list for spirv-cross-c-shared (CMakeLists.txt: core + c + all backend sources).
$sources = @(
    # core
    'spirv_cross.cpp', 'spirv_parser.cpp', 'spirv_cross_parsed_ir.cpp', 'spirv_cfg.cpp',
    # C API
    'spirv_cross_c.cpp',
    # backends (GLSL required; HLSL/MSL/CPP/REFLECT included to match the shared lib build)
    'spirv_glsl.cpp', 'spirv_hlsl.cpp', 'spirv_msl.cpp', 'spirv_cpp.cpp', 'spirv_reflect.cpp'
) | ForEach-Object { Join-Path $src $_ }

# Exactly the spvc_* functions SpvcNative.cs / the desktop transpiler calls, plus malloc/free.
$exportedFuncs = @(
    '_spvc_context_create',
    '_spvc_context_destroy',
    '_spvc_context_get_last_error_string',
    '_spvc_context_parse_spirv',
    '_spvc_context_create_compiler',
    '_spvc_compiler_create_compiler_options',
    '_spvc_compiler_options_set_bool',
    '_spvc_compiler_options_set_uint',
    '_spvc_compiler_install_compiler_options',
    '_spvc_compiler_build_combined_image_samplers',
    '_spvc_compiler_compile',
    '_malloc',
    '_free'
) -join ','

$runtimeMethods = @('cwrap','getValue','setValue','UTF8ToString','HEAPU8','HEAPU32') -join ','

$args = @(
    '-O3',
    '-std=c++17',
    '-fexceptions',                       # SPIRV-Cross uses C++ exceptions for error reporting; the C API catches them.
    '-DSPIRV_CROSS_C_API_GLSL=1',
    '-DSPIRV_CROSS_C_API_HLSL=1',
    '-DSPIRV_CROSS_C_API_MSL=1',
    '-DSPIRV_CROSS_C_API_CPP=1',
    '-DSPIRV_CROSS_C_API_REFLECT=1',
    "-I`"$src`"",
    '-sMODULARIZE=1',
    '-sEXPORT_ES6=1',
    '-sEXPORT_NAME=createSpirvCrossModule',
    '-sALLOW_MEMORY_GROWTH=1',
    '-sENVIRONMENT=web,node',
    '-sFILESYSTEM=0',                     # no FS needed; smaller module
    "-sEXPORTED_FUNCTIONS=$exportedFuncs",
    "-sEXPORTED_RUNTIME_METHODS=$runtimeMethods"
) + $sources + @('-o', "`"$outJs`"")

Write-Host "emcc: $emcc"
Write-Host "Compiling $($sources.Count) sources -> $outJs"
& $emcc @args
if ($LASTEXITCODE -ne 0) { throw "emcc failed with exit code $LASTEXITCODE" }

Write-Host "`n=== Build artifacts ==="
Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
