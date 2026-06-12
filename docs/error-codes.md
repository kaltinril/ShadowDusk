# ShadowDusk diagnostic code registry

The central registry of every diagnostic code ShadowDusk itself emits. **Every code maps
to exactly one condition** (one historical exception is flagged below). When adding a new
error, pick an unused number from the matching range and add it here in the same change.

Codes from the *underlying* compilers (DXC, d3dcompiler_47, vkd3d-shader) are passed
through verbatim (constraint 5: fail loudly, no reformatting) and are not listed here.

## Ranges

| Range | Owner |
|---|---|
| `FX0001`–`FX0099` | FX9 pre-parser (`FxPreParser` / `FxLexer`) |
| `SD0001`–`SD0009` | Preprocessor (`#include` handling) |
| `SD0010`–`SD0019` | Pipeline-level effect validation |
| `SD0020`–`SD0029` | MGFX writer range guards |
| `SD0100`–`SD0199` | Reflection / transpilation backends |
| `SD0200`–`SD0299` | Platform / backend availability |
| `SD0300`–`SD0399` | FNA (fx_2_0) target |
| `SD1900`–`SD1999` | Browser/WASM host backends |
| `X0000`–`X0099` | CLI and pipeline general errors (mgfxc-style) |

## FX — FX9 pre-parser

| Code | Meaning |
|---|---|
| `FX0001` | Unexpected token during FX9 parsing. |
| `FX0002` | Source ended before the current construct was closed. |
| `FX0003` | Malformed `compile` expression in a pass. |
| `FX0004` | Unrecognized shader profile string. |
| `FX0005` | Duplicate technique name. |
| `FX0006` | Duplicate pass name within a technique. |
| `FX0007` | Annotation block opened but never closed. |
| `FX0008` | Missing required `;` after a statement. |
| `FX0009` | `sampler_state` block opened but never closed. |
| `FX0010` | Unrecognized render-state key (non-fatal). |
| `FX0011` | Unknown character in effect source (e.g. `@`, `` ` ``). |
| `FX0012` | Legacy D3D9 sampling intrinsic (e.g. `tex2Dlod`) whose arguments cannot be rewritten 1:1 to a modern `Texture2D` method. |

## SD — ShadowDusk pipeline

| Code | Meaning | Emitted by |
|---|---|---|
| `SD0001` | `#include` file not found on any search path. | `ShaderError.IncludeNotFound` |
| `SD0002` | Circular `#include` (true cycle on the include stack; a diamond include is legal). | `ShaderError.CircularInclude` |
| `SD0010` | Effect source contains no techniques. | `CompilationPipeline` |
| `SD0011` | Unrecognised value for a render-state key. | `RenderStateParser` |
| `SD0020` | Constant-buffer size exceeds the MGFX int16 maximum. | `MgfxWriter` |
| `SD0021` | Shader index exceeds the MGFX int16 maximum. | `MgfxWriter` |
| `SD0022` | A count/index serialized as a single byte in the `.mgfx` shader record is outside 0–255 (samplers, constant-buffer indices, vertex attributes, sampler parameter index). | `MgfxWriter` |
| `SD0023` | `CompilerOptions.MgfxVersion` is outside the MGFX header's byte range (0–255). | `CompilationPipeline` |
| `SD0100` | SPIRV-Cross SPIR-V→GLSL transpilation failed (includes a SPIR-V blob whose byte length is not a multiple of 4). | `SpirvCrossGlslTranspiler` |
| `SD0101` | Pure-managed reflection failed (DXBC `RdefReader`, `SpirvReflector`). | `RdefReader`, `SpirvReflector` |
| `SD0102` | Native DXIL reflection (`ID3D12ShaderReflection`) failed. | `DxilReflectionExtractor` |
| `SD0103` | SPIRV-Cross native library missing or unloadable (run `tools/restore.ps1`). | `SpirvCrossGlslTranspiler` |
| `SD0200` | Metal target not yet supported. | `CompilationPipeline` |
| `SD0210` | **Two historical meanings (known shared code):** (a) the d3dcompiler_47 oracle backend refused the request (requires Windows, or a `ProfileOverride` it never serves); (b) the MonoGame GLSL rewriter could not lower a construct to MonoGame's GL dialect. | `D3DCompilerShaderCompiler`, `CompilationPipeline` |
| `SD0211` | vkd3d-shader native library missing or unloadable (run `tools/restore.ps1`). | `Vkd3dShaderCompiler` |
| `SD0212` | vkd3d-shader compile failed without parseable diagnostics. | `Vkd3dCompileContract` |
| `SD0300` | FNA profile policy violation (SM4+/SM1 profile, or stage/profile prefix mismatch). | `CompilationPipeline.ResolveFnaProfile` |
| `SD0301` | D3D9 CTAB reflection failed. | `CtabReader` |
| `SD0302` | fx_2_0 effect validation failed at write time. | `Fx2EffectWriter` |
| `SD0303` | FNA effect build failed. | `Fx2EffectBuilder` |
| `SD0305` | MojoShader-compatibility bytecode patch failed. | `D3d9BytecodePatcher` |
| `SD1900` | Browser/WASM DXC backend failed. | `JsDxcShaderCompiler` |
| `SD1901` | Browser/WASM SPIRV-Cross backend failed. | `JsSpirvToGlslTranspiler` |
| `SD1902` | Browser/WASM vkd3d backend failed. | `WasmVkd3dShaderCompiler` |
| `SD1903` | Synchronous `Compile()` called before the WASM compiler was initialized. | `WasmCompilerInitialization` |

## X — CLI / general (mgfxc-style)

| Code | Meaning |
|---|---|
| `X0000` | Underlying compiler failed but emitted no parseable diagnostics. |
| `X0001` | Source file could not be read (I/O or access denied). |
| `X0002` | Output file could not be written (I/O or access denied). |
| `X0003` | Missing required CLI argument (`<SourceFile>` / `<OutputFile>`). |
| `X0004` | Unknown CLI profile. |
| `X0005` | Invalid `--mgfx-version` value (only 10 and 11). |
| `X0006` | Invalid `/DxbcBackend` value (only `vkd3d`, `d3dcompiler`). |
| `X0007` | CLI compile timed out (5-minute watchdog). |
| `X0010` | Platform not supported by ShadowDusk (e.g. PlayStation4, XboxOne, Switch). |
| `X0099` | Unexpected internal error (catch-all; a bug if a consumer ever sees it). |
