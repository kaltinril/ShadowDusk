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
| `SD0000` | CLI informational notes (severity `Note`, never a failure) |
| `SD0001`–`SD0009` | Preprocessor (`#include` handling), input-format detection, source-provenance notes, and the ShaderToy/GLSL front end |
| `SD0010`–`SD0019` | Pipeline-level effect validation |
| `SD0020`–`SD0029` | MGFX writer range guards, container/target guards, and source-level target-binding guards (e.g. `SD0028`) |
| `SD0100`–`SD0199` | Reflection / transpilation backends |
| `SD0200`–`SD0299` | Platform / backend availability |
| `SD0300`–`SD0399` | FNA (fx_2_0) target |
| `SD0400`–`SD0499` | GL portability lint (`GlslPortabilityAnalyzer`) — always warnings, never errors |
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
| `FX0013` | An SM4+ resource or sampler type (e.g. `SamplerComparisonState`) appeared in a shader targeting FNA's D3D9 `fx_2_0` profile, where no SM1–3 lowering exists. Raised *before* the source reaches vkd3d, which does not reject these cleanly (a `SamplerComparisonState` makes its SM1 lowering take the whole process down with an access violation). |

## SD — ShadowDusk pipeline

| Code | Meaning | Emitted by |
|---|---|---|
| `SD0000` | **Note.** Informational output from the CLI, never a failure — currently the drivable effect parameters listed by `--print-uniforms`. | `PipelineRunner` |
| `SD0001` | `#include` file not found on any search path. | `ShaderError.IncludeNotFound` |
| `SD0002` | Circular `#include` (true cycle on the include stack; a diamond include is legal). | `ShaderError.CircularInclude` |
| `SD0003` | **Note.** A ShaderToy/GLSL input was converted to `.fx` and that generated HLSL failed to compile — the diagnostics that follow refer to the **generated** source, not your original line numbers. | `PipelineRunner` |
| `SD0004` | `#include` file was found on a search path but could not be read (locked, access denied, or deleted between the existence check and the read). | `FileSystemIncludeResolver` |
| `SD0005` | Input format could not be detected: the source has no HLSL `technique` block and no ShaderToy `mainImage` / `void main` entry point. Pass `--input-format fx` or `--input-format glsl`. | `InputFormatDetector` |
| `SD0006` | ShaderToy/GLSL front-end conversion failed: a construct outside the supported single-pass image subset (e.g. `mainSound` / `mainVR` / `mainCubemap`, or a malformed `mainImage` signature). Located on the **original** `.glsl`, not the generated `.fx`. | `PipelineRunner`, `ShaderFiddle.Web` |
| `SD0007` | **Warning.** A non-fatal ShaderToy/GLSL conversion adjustment (e.g. a local auto-renamed because it shadows a called function, or a dropped standalone `void main()` wrapper). Located on the **original** `.glsl`. | `PipelineRunner`, `ShaderFiddle.Web` |
| `SD0010` | Effect source contains no techniques. | `CompilationPipeline` |
| `SD0011` | Unrecognised value for a render-state key. | `RenderStateParser` |
| `SD0012` | Internal: a GL uniform in the rewriter's register layout has no matching reflected effect parameter — the GLSL uniform layout and the reflection diverged. A ShadowDusk bug if ever seen. | `CompilationPipeline` |
| `SD0013` | A pass `compile <target>` token does not resolve (after macro expansion) to a recognized shader profile — e.g. a typo (`compile A …`), an undefined `*_SHADERMODEL` macro (the `#if OPENGL … #else …` header was removed), or a profile-shaped-but-bogus literal (`ps_9_9`). Matches mgfxc/fxc's `unrecognized compiler target`. Fires on the GL/DX/Vulkan AND the FNA path. | `CompilationPipeline` |
| `SD0014` | A pass `compile <target>` resolves to a profile whose stage prefix does not match the slot it is bound to — e.g. `VertexShader = compile ps_3_0 …` (a `ps_*` profile in a `VertexShader` slot) or `PixelShader = compile vs_3_0 …`. mgfxc/fxc reject this cross-stage binding. GL/DX/Vulkan path; the FNA path reports the same condition as `SD0300` via `ResolveFnaProfile`. | `CompilationPipeline` |
| `SD0015` | **DirectX target only.** A pass `compile <target>` resolves to a *recognized* profile that is nevertheless below MonoGame's `DirectX_11` floor — e.g. `compile ps_3_0 …`, or the legacy `#if OPENGL … #else …` header that names SM3 in **both** arms. `mgfxc` rejects these with *"Invalid profile 'vs_3_0'. Vertex shader 'VSMain' must be SM 4.0 level 9.1 or higher!"*; ShadowDusk used to compile them, so the effect built here and then failed the consumer's real Content Pipeline build. Accepted set (measured against the pinned `mgfxc`): `*_4_0_level_9_1`, `*_4_0_level_9_3`, `*_4_0`, `*_4_1`, `*_5_0` — note that `*_4_0_level_9_0` **and every SM6 profile** are refused too. Distinct from `SD0013` (token is not a profile at all) and `SD0014` (profile's stage does not match its slot). | `CompilationPipeline` |
| `SD0020` | Constant-buffer size exceeds the MGFX int16 maximum. | `MgfxWriter` |
| `SD0021` | Shader index exceeds the MGFX int16 maximum. | `MgfxWriter` |
| `SD0022` | A count/index serialized as a single byte in the `.mgfx` shader record is outside 0–255 (samplers, constant-buffer indices, vertex attributes, sampler parameter index). | `MgfxWriter` |
| `SD0023` | `CompilerOptions.MgfxVersion` is outside the MGFX header's byte range (0–255). | `CompilationPipeline` |
| `SD0024` | A `sampler_state` member has an unparseable value for a recognized key (MinFilter/MagFilter/MipFilter/Filter, AddressU/V/W, BorderColor, MaxAnisotropy, MaxMipLevel, MipLodBias). | `MgfxSamplerStateResolver` |
| `SD0025` | The Vulkan target was requested together with the KNIFX container (KNI ships no Vulkan platform). | `CompilationPipeline` |
| `SD0026` | A shader declares more than one constant buffer for a single stage on the Vulkan target, which the format does not support — merge the globals into one `cbuffer`. | `CompilationPipeline` |
| `SD0027` | The DirectX12 target was requested together with the KNIFX container (KNI ships no DX12 platform). | `CompilationPipeline` |
| `SD0028` | Vulkan target: two distinct textures are sampled through one shared sampler in the same live code path — both would land on one combined-image-sampler descriptor, leaving the second unbound at draw. Located at the offending `Sample` call; give each texture its own `SamplerState`. (`SD0213` is this condition's post-compile reflection backstop.) | `VulkanTextureSamplerBindingRewriter` |
| `SD0100` | SPIRV-Cross SPIR-V→GLSL transpilation failed (includes a SPIR-V blob whose byte length is not a multiple of 4). | `SpirvCrossGlslTranspiler` |
| `SD0101` | Pure-managed reflection failed (DXBC `RdefReader`, `SpirvReflector`). | `RdefReader`, `SpirvReflector` |
| `SD0102` | Native DXIL reflection (`ID3D12ShaderReflection`) failed. | `DxilReflectionExtractor` |
| `SD0103` | SPIRV-Cross native library missing or unloadable (run `tools/restore.ps1`). | `SpirvCrossGlslTranspiler` |
| `SD0200` | Metal target not yet supported. | `CompilationPipeline` |
| `SD0201` | A capability-profile shader feature (e.g. vertex texture fetch, texture arrays) has no shipping runtime support yet and cannot be enabled. Reserved for a future runtime proven to consume it. | `ShaderFeatureSupport` |
| `SD0210` | **Two historical meanings (known shared code):** (a) the d3dcompiler_47 oracle backend refused the request (requires Windows, or a `ProfileOverride` it never serves); (b) the MonoGame GLSL rewriter could not lower a construct to MonoGame's GL dialect — incl. int/bool/mat3/struct uniform-block members, a whole-array uniform use, or any surviving reference to a rewritten uniform block. | `D3DCompilerShaderCompiler`, `CompilationPipeline` |
| `SD0211` | vkd3d-shader native library missing or unloadable (run `tools/restore.ps1`). | `Vkd3dShaderCompiler` |
| `SD0212` | vkd3d-shader compile failed and emitted no diagnostic text at all (unparseable non-empty text passes through verbatim as `X0000`). | `Vkd3dCompileContract` |
| `SD0213` | Vulkan target: reflection shows two distinct textures co-located onto a single combined-image-sampler binding — the second texture would silently sample the first's data on DesktopVK. The post-compile backstop of `SD0028`, for shared-sampler shapes the source-level scan cannot see. Give each texture its own `SamplerState` (or explicit register). | `CompilationPipeline` |
| `SD0214` | **Warning.** DirectX12 DXIL compiled on a non-Windows host is unsigned (`dxil.dll` validation/signing is Windows-only): it loads only with Windows Developer Mode enabled, and retail D3D12 rejects it at pipeline-state creation. Compile DirectX12 effects on Windows. | `DxcShaderCompiler` |
| ~~`SD0215`~~ | **Retired (Phase 51 A7).** Rejected sampler registers that were not contiguous from `s0`, because the GL record was named `ps_s{sampler.BindSlot}`. The record index is now the combined sampler's declaration position, so bind slots are never consulted for GL naming and `register(s3)` compiles correctly. **Do not reuse this number.** | — |
| ~~`SD0216`~~ | **Retired (Phase 51 A7).** Rejected the shared-`SamplerState` shape (N textures through one sampler) because the GL sampler table was keyed on the reflected samplers and could not name every `ps_s{k}` the GLSL declared. The table is now keyed on the (texture, sampler) **pairs**, so the shape compiles as `mgfxc` does. **Do not reuse this number.** | — |
| `SD0217` | OpenGL target: the combined-sampler declaration order the GL sampler table must mirror could not be determined from the SPIR-V — an input shape `SpirvCombinedSamplerPairs` does not model (an already-combined sampled image at module scope, an array/struct of separate samplers, a sampling operand that does not resolve to a module-level declaration), or an internal mismatch between the derived pair count and the number of sampler uniforms the emitted GLSL declares. Never raised by ordinary HLSL. | `SpirvCombinedSamplerPairs`, `CompilationPipeline` |
| `SD0300` | FNA profile policy violation (SM4+/SM1 profile, or stage/profile prefix mismatch). | `CompilationPipeline.ResolveFnaProfile` |
| `SD0301` | D3D9 CTAB reflection failed. | `CtabReader` |
| `SD0302` | fx_2_0 effect validation failed at write time. | `Fx2EffectWriter` |
| `SD0303` | FNA effect build failed. | `Fx2EffectBuilder` |
| `SD0305` | MojoShader-compatibility bytecode patch failed. | `D3d9BytecodePatcher` |
| `SD0400` | **Warning.** A gradient op (`dFdx`/`dFdy`/`fwidth`) sits inside a loop with a divergent exit (conditional `break`/`discard`) in the emitted GL fragment source. ANGLE Direct3D11 (WebGL in every Windows browser) silently evaluates such derivatives to 0.0; fxc warns X3553 and force-unrolls the same HLSL (issue #141). | `GlslPortabilityAnalyzer` |
| `SD0401` | **Warning.** A pass with no vertex shader whose pixel shader reads interpolants SpriteBatch's built-in SpriteEffect VS never writes (anything beyond COLOR0 → `vFrontColor` / TEXCOORD0 → `vTexCoord0`). Drawn with SpriteBatch on GL, the program link fails on strict drivers at the FIRST draw with the engine's generic exception. | `GlslPortabilityAnalyzer` |
| `SD0402` | **Warning.** A loop shape outside GLSL ES 1.00 Appendix A in the emitted GL source (header-less `for (;;)`, empty-increment `for` with the index advanced in the body, `while`, or a genuine `do-while`) — may fail to load on WebGL1 / KNI Reach (issue #138); desktop GL, WebGL2, and KNI HiDef are unaffected. | `GlslPortabilityAnalyzer` |
| `SD0403` | **Warning.** A GLSL-1.30+/ES-3.00-only construct survived into the versionless emitted GL source (`transpose`, `sinh`-family, `isnan`/`isinf`, `texelFetch`, bit-cast builtins, `switch`, `uint` types/literals, non-square matrices, integer shifts, the integer bitwise/modulo operators `&` `|` `^` `~` `%`, or a `round`/`roundEven`/`trunc` a lowering missed). Lenient desktop drivers accept it; strict front ends (macOS OpenGL, Mesa, WebGL1) reject it at Effect-load time — the class behind issues #149 and #163. | `GlslPortabilityAnalyzer` |
| `SD1900` | Browser/WASM DXC backend failed. | `JsDxcShaderCompiler` |
| `SD1901` | Browser/WASM SPIRV-Cross backend failed. | `JsSpirvToGlslTranspiler` |
| `SD1902` | Browser/WASM vkd3d backend failed. | `WasmVkd3dShaderCompiler` |
| `SD1903` | Synchronous `Compile()` called before the WASM compiler was initialized. | `WasmCompilerInitialization` |

## X — CLI / general (mgfxc-style)

| Code | Meaning |
|---|---|
| `X0000` | A diagnostic from the underlying compiler, passed through as-is: either a parsed `file:line:col` entry (DXC emits no per-diagnostic codes) or, when nothing parses, the compiler's complete text VERBATIM as the message (never a generic sentence). The `…with no diagnostics` message form means the compiler failed while emitting no text at all. |
| `X0001` | Source file could not be read (I/O or access denied). |
| `X0002` | Output file could not be written (I/O or access denied). |
| `X0003` | Missing required CLI argument (`<SourceFile>` / `<OutputFile>`). |
| `X0004` | Unknown CLI profile. |
| `X0005` | Invalid `--mgfx-version` value (only 10 and 11). |
| `X0006` | Invalid `/DxbcBackend` value (only `vkd3d`, `d3dcompiler`). |
| `X0007` | CLI compile timed out (5-minute watchdog). |
| `X0008` | Invalid `--target-runtime` value (only `monogame-gl`, `monogame-dx`, `monogame-gl-v11`, `kni-knifx`, `fna`). |
| `X0009` | A CLI flag that requires a value (`/I`, `--mgfx-version`, `--target-runtime`, …) reached the end of the argument list with no value supplied. |
| `X0010` | Platform not supported by ShadowDusk (e.g. PlayStation4, XboxOne, Switch). |
| `X0011` | Invalid `--input-format` value (only `auto`, `fx`, `glsl`). |
| `X0099` | Unexpected internal error (catch-all; a bug if a consumer ever sees it). |
