# ShadowDusk Research: MonoGame Shader Compilation Pipeline

Reference document for building a proof-of-concept cross-platform MonoGame shader compiler. Covers HLSL/.fx format, the existing `mgfxc` pipeline, native toolchain, and gaps the project must address.

> **Status note (2026-06-02):** This is **point-in-time pre-PoC research** — kept for its toolchain/format reference value, not as a description of the shipped design. Most of the "the project must address" / "ShadowDusk implements Option B" framing is now **built and validated**: the OpenGL path (DXC → SPIR-V → SPIRV-Cross → GLSL + a managed MojoShader-dialect rewrite + MGFX writer) renders pixel-equivalent to `mgfxc` in the real MonoGame DesktopGL runtime (Phase 17), the DirectX path ships **vkd3d-shader → DXBC SM5** (with Windows-only `d3dcompiler_47` as a correctness oracle — DX11 does **not** use DXC, since DXC emits DXIL/SM6, not the DXBC the DX11 runtime loads; Phase 18), and the in-browser frontend is the **pinned DirectXShaderCompiler compiled to WebAssembly** (byte-identical SPIR-V to desktop), render-proven in headless KNI WebGL (Phases 23/24). The actual product is a **self-contained, in-memory, cross-platform NuGet library** — `IShaderCompiler.CompileAsync(fx) → .mgfx bytes`; the CLI/MGCB plugin are delivery shapes and the browser fiddle is only a sample. For the current architecture see [`monogame_runtime_mgfx_compiler_research.md`](../monogame_runtime_mgfx_compiler_research.md) §0 and the root `CLAUDE.md` *THE PURPOSE* section. Where this doc still names ShaderConductor or glslang's HLSL frontend as candidate backends, those are **not** used (ShaderConductor was archived Aug 2025; the chosen frontend is faithful DXC everywhere).

---

## Table of Contents

1. [MonoGame's Shader Format](#1-monogames-shader-format)
2. [HLSL Effect File Format (.fx)](#2-hlsl-effect-file-format-fx)
3. [Compilation Toolchain](#3-compilation-toolchain)
4. [Open Source Projects](#4-open-source-projects)
5. [Compilation Phases](#5-compilation-phases)
6. [MonoGame-Specific Details](#6-monogame-specific-details)
7. [Things the Project May Not Have Considered](#7-things-the-project-may-not-have-considered) — reflection, sampler parsing, MojoShader, GLSL/MSL correctness, DXIL signing, MGCB integration tiers

---

## 1. MonoGame's Shader Format

> **Version scope:** This document targets MonoGame's `develop` branch (post-3.8.2, pre-4.0). Binary format constants (`MGFXVersion=11`), the SharpDX→Vortice migration, and the Vulkan profile are all `develop`-branch state. MonoGame 3.8.2 (latest stable as of this writing) uses `MGFXVersion=10` and SharpDX.

### What is mgfxc?

`mgfxc` (MonoGame Effects Compiler) compiles DirectX Effect `.fx` files into MonoGame's binary `.mgfxo` format for consumption by the `Effect` class at runtime.

**Install:**
```
dotnet tool install -g dotnet-mgfxc
```

**CLI:**
```
mgfxc <SourceFile> <OutputFile> [/Debug] [/Profile:<Platform>]
```

**Profile values:** `DirectX_11`, `OpenGL`, `PlayStation4`, `XboxOne`, `Switch`

**Output:** Not an XNB file. Load manually via `new Effect(graphicsDevice, File.ReadAllBytes(path))` or let MGCB wrap it.

**Official docs:** https://docs.monogame.net/articles/getting_started/tools/mgfxc.html

### mgfxc Source Code

All under the MonoGame `develop` branch:

| Path | Content |
|------|---------|
| [`Tools/MonoGame.Effect.Compiler/`](https://github.com/MonoGame/MonoGame/tree/develop/Tools/MonoGame.Effect.Compiler) | Top-level: `Program.cs`, `WineHelper.cs`, `mgfxc_wine_setup.sh` |
| [`Effect/EffectObject.cs`](https://github.com/MonoGame/MonoGame/blob/develop/Tools/MonoGame.Effect.Compiler/Effect/EffectObject.cs) | Main IR data structures: `d3dx_technique`, `d3dx_pass`, `d3dx_parameter`, `d3dx_state` |
| `Effect/EffectObject.hlsl.cs` | HLSL parsing, `CompileHLSL()` via SharpDX or WineHelper |
| `Effect/EffectObject.writer.cs` | Binary format serialization (writes `.mgfxo`) |
| `Effect/ShaderProfile.DirectX11.cs` | DX11 compilation (SM4.0 level 9.1 min, uses SharpDX/FXC) |
| `Effect/ShaderProfile.OpenGL.cs` | OpenGL (SM3.0 max, uses FXC → MojoShader DXBC→GLSL) |
| `Effect/ShaderProfile.Vulkan.cs` | Vulkan (SM6.0, uses DXC with `-spirv`) |
| `Effect/ConstantBufferData.cs` | Constant buffer representation and layout |
| `Effect/MojoShader.cs` | P/Invoke wrapper for MojoShader native library |
| `Effect/Preprocessor.cs` | Shader preprocessor (flattens #include before FXC) |
| `Effect/Spirv/` | SPIR-V reflection: `SpirvReflectionInfo`, `SpirvVariable`, `SpirvDecoration` |
| [`MonoGame.Framework/Graphics/Effect/Effect.cs`](https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs) | Runtime reader |
| [`BasicEffect.fx`](https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources/BasicEffect.fx) | Reference .fx with 32 technique permutations |

### .mgfx Binary Format

The compiled output file extension is `.mgfx` in the current MonoGame `develop` branch (older versions used `.mgfxo`; both names appear in community docs). The runtime reader does not enforce the extension — it validates the binary signature. The extension is whatever the caller passes as the output path argument.

**Header constants (from `Effect.cs`):**

| Constant | Value |
|----------|-------|
| `MGFXSignature` | `0x4D474658` (ASCII "MGFX") |
| `MGFXVersion` | `11` (current) |
| `MGFXMinVersion` | `10` (minimum supported) |

**Binary layout (in order):**

**Profile ID byte values** (defined in `ShaderProfile.cs` — verify against source before implementing the writer):

| Profile | Byte value |
|---------|-----------|
| OpenGL | `0` |
| DirectX_11 | `1` |
| Vulkan | `3` |

> These values are derived from `ShaderProfileType` enum ordinals in the MonoGame source. Verify against `ShaderProfile.cs` before finalizing the writer — a wrong profile byte causes `Effect` to select the wrong shader path at runtime with no useful error.

| Section | What is written |
|---------|----------------|
| Header | 4-byte signature, 1-byte version, 1-byte profile ID |
| Constant Buffers | Count (int), then per buffer: name, size (int16), parameter index count, parameter indices (int32), parameter offsets (uint16) |
| Shaders | Count (int), then per shader: `int32` byte-length followed by raw bytecode bytes. For OpenGL the "bytecode" is GLSL source as UTF-8 text; for DX11 it is DXBC; for Vulkan it is SPIR-V words. Verify exact write calls in `EffectObject.writer.cs` — the length prefix width is critical. |
| Parameters | Count (int), then per parameter: class (byte), type (byte), name, semantic, annotations, rows/columns (bytes), member/element indices |
| Techniques | Count (int), then per technique: name, annotations, pass count |
| Passes | Per pass: name, annotations, vertex/pixel shader indices |
| Render States | Per pass: optional BlendState, DepthStencilState, RasterizerState |

**String encoding:** All string fields (names, semantics) are written with C# `BinaryWriter.Write(string)`, which uses a 7-bit length-encoded prefix (not a fixed `int32` and not null-terminated). The length is encoded as a variable-width integer: values < 128 take 1 byte; larger values take 2 bytes. Implementing this incorrectly causes every field after the first string to be read at the wrong offset, corrupting the entire effect. Use `BinaryWriter.Write(string)` or replicate its 7-bit encoding exactly.

**Note:** Technique/shader/pass counts were previously packed into nibbles (4 bits each, max 15). PR [#7397](https://github.com/MonoGame/MonoGame/pull/7397) changed them to full bytes (max 255) and bumped `MGFXVersion` from 9 to 10. A subsequent change bumped it from 10 to 11 (current). Default to version 10 (MonoGame 3.8.2 compatibility); version 11 is opt-in via `--mgfx-version 11` flag.

**Community reverse-engineering thread:** https://community.monogame.net/t/solved-shader-different-mgfx-binary-format-between-mono-and-net-core/11689/21

---

## 2. HLSL Effect File Format (.fx)

### File Structure

An `.fx` file contains:
- **Global variable declarations** (constants, textures, samplers) — implicitly in a `$Global` cbuffer
- **cbuffer / tbuffer blocks** (SM4+ explicit constant buffers)
- **Sampler state declarations** (`SamplerState`, `sampler2D`, `sampler_state {}`)
- **HLSL functions** (vertex and pixel shader entry points)
- **Techniques** (named blocks containing one or more passes)
- **Passes** (vertex + pixel shader entry points + optional render state)
- **Annotations** (metadata: `<float foo = 1.0;>`)

### FX9-style Technique Syntax (Used by MonoGame)

```hlsl
technique MyTechnique
{
    pass Pass1
    {
        VertexShader = compile vs_3_0 VSMain();
        PixelShader  = compile ps_3_0 PSMain();
    }
}
```

MonoGame uses FX9-style `technique` syntax. The newer `technique11` syntax (D3D11 fx_5_0) is not used.

### Sampler State Declarations

```hlsl
sampler2D MySampler = sampler_state {
    Texture   = <MyTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU  = Wrap;
    AddressV  = Clamp;
};
```

### Annotations

```hlsl
float MyParam < string UIName = "My Parameter"; float UIMin = 0; float UIMax = 1; > = 0.5;
```

### Render State in Passes

```hlsl
pass Pass1
{
    CullMode        = None;
    AlphaBlendEnable = True;
    SrcBlend        = SrcAlpha;
    DestBlend       = InvSrcAlpha;
}
```

### HLSL Semantics

**D3D9-style vertex inputs:** `BINORMAL[n]`, `BLENDINDICES[n]`, `BLENDWEIGHT[n]`, `COLOR[n]`, `NORMAL[n]`, `POSITION[n]`, `POSITIONT`, `PSIZE[n]`, `TANGENT[n]`, `TEXCOORD[n]`

**D3D9 vertex outputs:** `COLOR[n]`, `FOG`, `POSITION[n]`, `PSIZE`, `TESSFACTOR[n]`

**D3D9 pixel inputs/outputs:** `COLOR[n]`, `TEXCOORD[n]`, `VFACE`, `VPOS`, `DEPTH[n]`

**System-value semantics (SV_ prefix):** `SV_Position`, `SV_Target[0-7]`, `SV_Depth`, `SV_VertexID`, `SV_InstanceID`, `SV_IsFrontFace`, `SV_PrimitiveID`, `SV_ClipDistance[n]`, `SV_CullDistance[n]`, `SV_Coverage`, `SV_RenderTargetArrayIndex`, `SV_ViewportArrayIndex`, compute: `SV_GroupID`, `SV_GroupThreadID`, `SV_DispatchThreadID`, `SV_GroupIndex`

Full reference: https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics

### Constant Buffer Packing Rules

- Variables are packed into 16-byte rows; a variable never crosses a row boundary (padding inserted)
- Every array element padded to 16 bytes regardless of element size
- Matrices: each row is 16-byte aligned in legacy cbuffers
- Use `packoffset(c#.xyzw)` for manual control

**References:**
- https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-packing-rules
- https://github.com/microsoft/DirectXShaderCompiler/wiki/Buffer-Packing
- Interactive visualizer: https://maraneshi.github.io/HLSL-ConstantBufferLayoutVisualizer/

### Formal HLSL Grammar

No complete published BNF exists for legacy HLSL (FX9 constructs aren't fully specced). Closest available:
- Working draft spec: https://github.com/microsoft/hlsl-specs
- Spec PDF: https://microsoft.github.io/hlsl-specs/specs/hlsl.pdf
- Spec HTML: https://microsoft.github.io/hlsl-specs/specs/index.html

### Key Documentation Links

| Resource | URL |
|----------|-----|
| HLSL Reference | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-reference |
| cbuffer / tbuffer | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-constants |
| Semantics | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics |
| Packing Rules | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-packing-rules |
| Preprocessor Directives | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-appendix-preprocessor |
| Effects (D3D11) | https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d11-graphics-programming-guide-effects |
| Writing HLSL Shaders (D3D9) | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-writing-shaders-9 |
| NVIDIA D3DX Effects + HLSL PDF | https://www.nvidia.com/docs/IO/8228/D3DTutorial2_FX_HLSL.pdf |

---

## 3. Compilation Toolchain

### FXC (fxc.exe) — Legacy HLSL Compiler

Compiles HLSL SM1.x–SM5.1 → DXBC (DirectX Bytecode). Windows-only. Topped out at SM5.1; no new shader models.

**Key flags:**

| Flag | Purpose |
|------|---------|
| `/T <profile>` | Target profile (e.g. `ps_3_0`, `vs_4_0_level_9_1`, `ps_5_0`) |
| `/E <name>` | Entry point (default: `main`) |
| `/Fo <file>` | Output object (DXBC) |
| `/Fc <file>` | Assembly listing |
| `/D <id>=<text>` | Define macro |
| `/I <path>` | Include path |
| `/P <file>` | Preprocess only |
| `/Zi` | Debug information |
| `/Od` | Disable optimizations |
| `/O0`–`/O3` | Optimization level (O1 is default) |
| `/Gec` | Backward compatibility mode |
| `/WX` | Warnings as errors |
| `/Qstrip_debug` | Strip debug data (SM4+) |
| `/Qstrip_reflect` | Strip reflection data (SM4+) |
| `/Zpc` / `/Zpr` | Column / row-major matrix packing |

**Profiles:** `vs_1_1`–`vs_5_1`, `ps_2_0`–`ps_5_1`, `cs_4_0`–`cs_5_0`, `gs_4_0`/`gs_5_0`, `hs_5_0`, `ds_5_0`

**Docs:**
- https://learn.microsoft.com/en-us/windows/win32/direct3dtools/fxc
- https://learn.microsoft.com/en-us/windows/win32/direct3dtools/dx-graphics-tools-fxc-syntax

### DXC — Modern HLSL Compiler (Cross-Platform)

The modern HLSL compiler (SM5.0–SM6.9), based on LLVM/Clang. Outputs DXIL (SM6.0+) or SPIR-V (SM5.0+). Has prebuilt binaries for Linux, macOS, and Windows. For the cross-platform SPIR-V path DXC targets `vs_5_0`/`ps_5_0` minimum — SM3 targets are not supported.

**Repository:** https://github.com/microsoft/DirectXShaderCompiler
**Releases (prebuilts):** https://github.com/microsoft/DirectXShaderCompiler/releases
**NuGet:** https://www.nuget.org/packages/Microsoft.Direct3D.DXC

**Key CLI flags:**

| Flag | Purpose |
|------|---------|
| `-T <profile>` | Target (e.g. `ps_6_0`, `vs_6_6`) |
| `-E <name>` | Entry point |
| `-Fo <file>` | Output object |
| `-D <macro>` | Define macro |
| `-I <path>` | Include path |
| `-spirv` | Emit SPIR-V instead of DXIL |
| `-fvk-use-dx-layout` | Use DX cbuffer packing rules (HLSL 16-byte alignment) instead of Vulkan's default std430. **Required** when C# game code uploads struct data laid out for HLSL cbuffer rules — without it, SPIR-V uses std430 and constants are misread. |
| `-fspv-reflect` | Embed reflection in SPIR-V |
| `-fvk-invert-y` | Flip Y for Vulkan coordinate system (vertex shaders) |
| `-auto-binding-space <N>` | Assign all resources to binding space N |
| `-HV <year>` | HLSL version (2016/2017/2018/2021) |
| `-Zi` | Debug info |
| `-Fre <file>` | Save reflection separately |

**Programmatic C++ API (`libdxcompiler`):**
- `DxcCreateInstance()` → create instances
- `IDxcCompiler3::Compile()` → compile with string-array args
- `IDxcResult::GetOutput(DXC_OUT_OBJECT)` → shader bytecode
- `IDxcResult::GetOutput(DXC_OUT_REFLECTION)` → reflection → `ID3D12ShaderReflection`
- Not thread-safe — use separate instances per thread

**References:**
- Wiki: https://github.com/microsoft/DirectXShaderCompiler/wiki/Using-dxc.exe-and-dxcompiler.dll
- C++ API tutorial: https://simoncoenen.com/blog/programming/graphics/DxcCompiling
- C bindings: https://github.com/milliewalky/dxc.c
- Porting from FXC: https://github.com/microsoft/DirectXShaderCompiler/wiki/Porting-shaders-from-FXC-to-DXC
- Build quality analysis: https://devlog.hexops.org/2024/building-the-directx-shader-compiler-better-than-microsoft/

### Vortice.Dxc — Managed C# Wrapper for DXC (Recommended Integration Path)

[`Vortice.Dxc`](https://www.nuget.org/packages/Vortice.Dxc) is a C#/.NET NuGet package that wraps `libdxcompiler` with managed P/Invoke bindings and bundles pre-built native DXC binaries for Windows, Linux, and macOS. It is actively maintained by the author of Vortice.Windows (the same library MonoGame uses for its DX runtime path) and is already used in MonoGame-adjacent tools.

**NuGet:** `dotnet add package Vortice.Dxc`

For ShadowDusk this is the recommended integration path for DXC rather than writing raw P/Invoke against `libdxcompiler` directly. It avoids the native binary restore complexity for the DXC tool itself.

**Repository:** https://github.com/amerkoleci/Vortice.Windows (part of the Vortice umbrella)

---

### glslang / glslangValidator

Khronos reference GLSL compiler; has limited HLSL input support. Compiles to SPIR-V.

**Key flags:** `-V[ver]` (SPIR-V Vulkan), `-G[ver]` (SPIR-V OpenGL), `-D` (HLSL mode), `-e <entry>`, `-o <file>`, `-S <stage>` (vert/frag/comp/geom/tesc/tese)

**HLSL FAQ:** https://github.com/KhronosGroup/glslang/wiki/HLSL-FAQ

**Important limitation:** glslang's HLSL support does not handle FX9 Effect constructs (`technique`, `pass`, `sampler_state`, annotations). It can only compile individual HLSL shader functions, not a full `.fx` file. It cannot be used as a drop-in FXC/DXC replacement for the Effect pipeline.

### shaderc (Google)

Wraps glslang; provides `glslc`, a clang-style frontend with `#include` support.

**Repository:** https://github.com/google/shaderc

### SPIRV-Tools (Khronos)

Provides `spirv-val` (SPIR-V validator) and `spirv-opt` (optimization passes). Not a compiler — used for validating SPIR-V output from DXC and optimizing before handing to SPIRV-Cross. Cross-platform.

**Repository:** https://github.com/KhronosGroup/SPIRV-Tools

### SPIRV-Cross

Transpiles SPIR-V → GLSL, GLSL ES, MSL, HLSL, or JSON reflection. The critical link in the DXC→SPIRV-Cross→GLSL/MSL pipeline.

**Repository:** https://github.com/KhronosGroup/SPIRV-Cross
**License:** Apache 2.0

**Integration path for ShadowDusk:** Use raw P/Invoke against the SPIRV-Cross C API directly. Veldrid.SPIRV (see Section 4) is an alternative but is designed for Veldrid's use case and does not expose all options needed (Y-flip, depth clip, GLSL version). The C API is stable, C89-compatible, and gives full control.

**C API usage sequence (P/Invoke):**
```c
spvc_context_create(&ctx)
spvc_context_parse_spirv(ctx, spirv_words, word_count, &ir)
spvc_context_create_compiler(ctx, SPVC_BACKEND_GLSL, ir, SPVC_CAPTURE_MODE_COPY, &compiler)
// — or SPVC_BACKEND_MSL_APPLE for Metal —
spvc_compiler_create_compiler_options(compiler, &opts)
spvc_compiler_options_set_uint(opts, SPVC_COMPILER_OPTION_GLSL_VERSION, 130)
spvc_compiler_options_set_bool(opts, SPVC_COMPILER_OPTION_GLSL_ES, SPVC_FALSE)
spvc_compiler_options_set_bool(opts, SPVC_COMPILER_OPTION_FLIP_VERTEX_Y, SPVC_TRUE)  // vertex only
spvc_compiler_install_compiler_options(compiler, opts)
spvc_compiler_build_combined_image_samplers(compiler)  // must be before compile()
spvc_compiler_compile(compiler, &source)               // returns GLSL or MSL string
spvc_compiler_create_shader_resources(compiler, &resources)  // for reflection
spvc_compiler_get_decoration(compiler, id, SpvDecorationBinding)
```

**Backend constants:**
| Target | Backend constant |
|--------|----------------|
| GLSL (OpenGL / DesktopGL) | `SPVC_BACKEND_GLSL` |
| MSL (macOS / iOS Metal) | `SPVC_BACKEND_MSL_APPLE` |
| HLSL (for inspection only) | `SPVC_BACKEND_HLSL` |

**Reflection API guide:** https://github.com/KhronosGroup/SPIRV-Cross/wiki/Reflection-API-user-guide

---

## 4. Open Source Projects

> **Test shader provenance:** the links in this section are **toolchain /
> MonoGame-builtin** references, not the origins of the `.fx` test fixtures.
> Per-shader fixture provenance (what little is recoverable) and the fresh,
> project-owned example shaders are documented separately in
> [`docs/test-shader-corpus.md`](test-shader-corpus.md). Confirmed fixture
> upstreams: [discosultan/penumbra](https://github.com/discosultan/penumbra)
> (the `Penumbra*.fx`) and
> [manbeardgames/monogame-hlsl-examples](https://github.com/manbeardgames/monogame-hlsl-examples)
> (`BasicShader`/`TintShader`/`BlendShader`/`MultiTexture`/`SimpleLightShader`).

### MonoGame

| Resource | URL |
|----------|-----|
| Main repo | https://github.com/MonoGame/MonoGame |
| mgfxc directory | https://github.com/MonoGame/MonoGame/tree/develop/Tools/MonoGame.Effect.Compiler |
| Effect.cs (runtime) | https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs |
| BasicEffect.fx | https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources/BasicEffect.fx |
| MojoShader fork | https://github.com/MonoGame/mojoshader |
| mgfxc_wine_setup.sh | https://github.com/MonoGame/MonoGame/blob/develop/Tools/MonoGame.Effect.Compiler/mgfxc_wine_setup.sh |
| Modernisation issue #6968 | https://github.com/MonoGame/MonoGame/issues/6968 |

### HLSL Parsers / Lexers

| Project | URL | Notes |
|---------|-----|-------|
| hlslparser (Unknown Worlds) | https://github.com/unknownworlds/hlslparser | Recursive descent HLSL→GLSL |
| hlslparser (Thekla / The Witness) | https://github.com/Thekla/hlslparser | D3D9 HLSL → HLSL10 + MSL |
| hlslparser (Nomoresleep) | https://github.com/Nomoresleep/hlslparser | HLSL/GLSL/MSL |
| hlsl-specs (formal spec) | https://github.com/microsoft/hlsl-specs | Working draft in TeX |

### Transpilers

| Project | URL | Notes |
|---------|-----|-------|
| DirectXShaderCompiler | https://github.com/microsoft/DirectXShaderCompiler | Primary: HLSL→DXIL/SPIR-V |
| SPIRV-Cross | https://github.com/KhronosGroup/SPIRV-Cross | SPIR-V→GLSL/MSL/HLSL |
| HLSLcc (Unity) | https://github.com/Unity-Technologies/HLSLcc | DXBC→GLSL/GLSL ES/Vulkan/MSL |
| HLSLCrossCompiler (original) | https://github.com/James-Jones/HLSLCrossCompiler | Original DXBC→GLSL |
| hlsl2glslfork (Aras) | https://github.com/aras-p/hlsl2glslfork | ATI HLSL2GLSL; used in older Unity |
| ShaderConductor | https://github.com/microsoft/ShaderConductor | **Archived Aug 2025 — do not use**; was HLSL→DXC→SPIR-V→SPIRV-Cross |
| CrossShader | https://github.com/alaingalvan/CrossShader | SPIR-V/GLSL/HLSL/MSL cross-compile |
| RavEngine ShaderTranspiler | https://github.com/RavEngine/ShaderTranspiler | C++ lib: GLSL→HLSL/Metal/Vulkan/WebGPU |
| MojoShader | https://github.com/MonoGame/mojoshader | DXBC (SM1-3) → GLSL; C library |
| shaderc | https://github.com/google/shaderc | Google SPIR-V compiler |
| Veldrid.SPIRV | https://github.com/mellinoe/veldrid-spirvcross | .NET P/Invoke bindings for SPIRV-Cross; NuGet-distributed, cross-platform |

---

## 5. Compilation Phases

### Pipeline for HLSL

0. **FX9 technique extraction (pre-phase)** — The `.fx` file's `technique`/`pass` blocks are NOT valid HLSL. The HLSL compiler (FXC or DXC) will reject them as syntax errors if passed the full file. A custom parser must first extract: per-pass entry point names, target profiles (`vs_3_0`, `ps_6_0`, etc.), and render state declarations. The technique/pass blocks are then stripped (or commented out) before the HLSL source is handed to the compiler. MonoGame's `EffectObject.hlsl.cs` does this. ShadowDusk needs this same pre-pass.
1. **Preprocessing** — `#define`, `#include`, `#if`/`#ifdef`/`#endif`, `#pragma`, macro expansion; platform macros (`MGFX=1`, `GLSL=1`, etc.) injected here
2. **Lexing** — tokenization of HLSL source into keywords, identifiers, literals, operators
3. **Parsing** — builds AST for core HLSL (functions, cbuffers, global variables, sampler declarations)
4. **Semantic analysis** — type checking, semantic binding validation, constant folding
5. **Lowering to IR** — DXC: LLVM IR; FXC: proprietary IR
6. **Optimization** — dead code elimination, constant propagation, loop unrolling
7. **Code generation** — emit DXBC (FXC) or DXIL (DXC) or SPIR-V (DXC `-spirv`)
8. **Transpilation (SPIR-V path)** — SPIR-V → GLSL or SPIR-V → MSL via SPIRV-Cross. This is where Y-flip, depth range remapping, and combined sampler merging are applied (see Section 7).
9. **Validation** — DXIL validator (`dxil.dll`); SPIR-V validator (`spirv-val`)
10. **Reflection extraction** — embedded in bytecode container; extracted via `ID3D12ShaderReflection` (DXC) or `ID3D11ShaderReflection` (FXC)
11. **Binary assembly** — all per-pass blobs packed into the `.mgfx` binary format

### DXBC vs DXIL vs SPIR-V

| Format | Compiler | Shader Models | Platform | Notes |
|--------|----------|---------------|----------|-------|
| DXBC | FXC | SM1.0–SM5.1 | Windows only | Legacy binary; proprietary container |
| DXIL | DXC | SM6.0–SM6.9 | Windows (+ Linux via dxc) | LLVM 3.7 bitcode; requires `dxil.dll` on Windows for validation |
| SPIR-V | DXC `-spirv`, glslang, shaderc | — | Cross-platform | Khronos standard; used by Vulkan; intermediate for SPIRV-Cross |

**References:**
- https://github.com/Microsoft/DirectXShaderCompiler/blob/main/docs/DXIL.rst
- https://asawicki.info/news_1719_two_shader_compilers_of_direct3d_12
- https://themaister.net/blog/2021/09/05/my-personal-hell-of-translating-dxil-to-spir-v-part-1/

### Reflection API Reference

- DXC (cross-platform): `IDxcUtils::CreateReflection()` → `ID3D12ShaderReflection`
- FXC (Windows-only): `ID3D11ShaderReflection` from `d3dcompiler.dll`
- SPIR-V (cross-platform): `spvc_compiler_create_shader_resources()` via SPIRV-Cross C API
- Shader reflection blog with code: https://rtarun9.github.io/blogs/shader_reflection/
- `ID3D12ShaderReflection` docs: https://learn.microsoft.com/en-us/windows/win32/api/d3d12shader/nn-d3d12shader-id3d12shaderreflection
- Resource binding in HLSL: https://learn.microsoft.com/en-us/windows/win32/direct3d12/resource-binding-in-hlsl

---

## 6. MonoGame-Specific Details

### Platform → Shader Language Matrix

| MonoGame Platform | Backend | Shader Language | SM Cap | Toolchain |
|-------------------|---------|----------------|--------|-----------|
| WindowsDX | DirectX 11 | HLSL / DXBC | SM5.0 | FXC via Vortice.Windows (formerly SharpDX, which is deprecated/archived) |
| DesktopGL (Win/Linux/macOS) | OpenGL 2.0+ | GLSL (from DXBC) | SM3.0 max | FXC → MojoShader DXBC→GLSL at runtime |
| Vulkan (in-progress) | Vulkan | SPIR-V | SM6.0 | DXC with `-spirv -fvk-use-dx-layout -fspv-reflect` |
| macOS / iOS (Metal) | Metal | MSL | SM5.0 equivalent | **ShadowDusk extension — not a drop-in target.** mgfxc has no `/Profile:Metal` argument. The toolchain would be DXC `-spirv` → SPIRV-Cross (`SPVC_BACKEND_MSL_APPLE`) → MSL source text. Out of scope until OpenGL path is working. |

> **Note:** PS4, XboxOne, and Switch require licensed platform SDKs and are outside the scope of ShadowDusk. They use platform-specific compilers invoked by mgfxc when the profile is set accordingly.

**OpenGL minimum:** SM2.0 (`vs_2_0`/`ps_2_0`) for game content. ShadowDusk's SPIR-V path has a practical floor of SM5.0 semantics compiled as `vs_5_0`/`ps_5_0` (DXC cannot target SM3).
**Vulkan flags:** vertex shaders also get `-fvk-invert-y -fvk-use-dx-position-w`; pixel shaders get `-auto-binding-space 1`

`-auto-binding-space 1` for pixel shaders prevents resource binding collisions: DXC assigns `t0`/`s0`/`b0` starting from 0 for each stage independently, so without separate binding spaces a VS texture `t0` and PS texture `t0` collide in the Vulkan descriptor set layout.

### Preprocessor Macros Injected by mgfxc

| Platform | Macros defined |
|----------|---------------|
| DX11 | `HLSL=1`, `SM4=1`, `MGFX=1` |
| OpenGL | `GLSL=1`, `OPENGL=1`, `MGFX=1` |
| Vulkan | `HLSL=1`, `SM6=1`, `VULKAN=1`, `MGFX=1` |
| Metal / macOS | `GLSL=1`, `MGFX=1` (transpiled GLSL intermediary) or platform-specific if MonoGame adds a Metal profile |
| All | `MGFX=1` |

`SM4=1` is the key guard used by MonoGame's built-in effects (e.g. `BasicEffect.fx` uses `#if SM4` blocks to switch between cbuffer and legacy constant syntax). Missing this macro causes those shaders to compile against D3D9 constant syntax even when targeting DX11. ShadowDusk must inject these same macros to maintain drop-in compatibility.

### The Wine Problem — What ShadowDusk Solves

MonoGame's current Linux/macOS approach:
1. Check `MGFXC_WINE_PATH` environment variable
2. If set, re-invoke itself via `dotnet` inside a Wine prefix (which has Windows FXC)
3. `mgfxc_wine_setup.sh` automates Wine 8.0+ prefix setup

ShadowDusk replaces this with a native toolchain: DXC (cross-platform) → SPIR-V → SPIRV-Cross. This is the direction proposed in MonoGame issue [#6968](https://github.com/MonoGame/MonoGame/issues/6968), but not yet implemented in MonoGame itself.

### Effect Parameter Types

**EffectParameterClass:** `Scalar=0`, `Vector=1`, `Matrix=2`, `Object=3` (texture/sampler/string), `Struct=4`

**EffectParameterType:** `Void=0`, `Bool=1`, `Int32=2`, `Single=3` (float), `String=4`, `Texture=5`, `Texture1D=6`, `Texture2D=7`, `Texture3D=8`, `TextureCube=9`

- https://docs.monogame.net/api/Microsoft.Xna.Framework.Graphics.EffectParameterClass.html
- https://docs.monogame.net/api/Microsoft.Xna.Framework.Graphics.EffectParameterType.html

### How `Effect` Loads Shader Bytecode

1. `ReadEffect()` validates "MGFX" signature and version (10–11)
2. Reads constant buffers, shaders, parameters, techniques/passes
3. **OpenGL:** the `.mgfx` blob contains pre-translated GLSL source text (MojoShader ran at compile time in mgfxc, not at runtime). The GPU driver compiles that GLSL at load time via `glCompileShader`.
4. **DX11:** DXBC handed to Direct3D → `ID3D11VertexShader`/`ID3D11PixelShader`
5. **Vulkan:** SPIR-V bytecode used directly

### MGCB Error Format

FXC emits errors in this format; MGCB and IDE integration parse it:
```
Filename.fx(line,col-col): error X####: message text
```
Example: `Problem.fx(11,44-55): error X4502: invalid vs_3_0 input semantic 'SV_VERTEXID'`

ShadowDusk **must** emit errors in this exact format for MGCB IDE integration to work.

**Process contract (exit codes and output routing):**

| Condition | Exit code | Output stream |
|-----------|-----------|---------------|
| Successful compile | `0` | Silent (no stdout/stderr) |
| Shader compile error | `1` | Error messages to **stderr** |
| Bad arguments / usage | `1` | Usage message to **stderr** |

Diagnostics must go to **stderr**, not stdout. MGCB captures stderr for IDE error-list display; stdout is ignored. Verify exact exit codes against `Program.cs` in the MonoGame source before finalizing — a wrong exit code causes MGCB to misclassify the result (e.g. treating a compile failure as a missing tool).

**Related issues:**
- https://github.com/MonoGame/MonoGame/issues/6833
- https://github.com/MonoGame/MonoGame/issues/7159

### Shader Model Constraints

| Path | SM cap | Why |
|------|--------|-----|
| OpenGL (MojoShader) | SM3.0 | MojoShader only decodes DXBC SM1-3 |
| DX11 | SM5.0 | FXC max |
| Vulkan | SM6.0 | DXC only |

Reference: https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-models

---

## 7. Things the Project May Not Have Considered

### Shader Reflection (Critical Gap)

ShadowDusk must extract parameter metadata (names, types, sizes, binding slots) from compiled bytecode to populate `.mgfxo` parameter structures. The cross-platform problem:

| Method | Platform | API |
|--------|----------|-----|
| DXBC reflection | **Windows-only** | `ID3D11ShaderReflection` from `d3dcompiler.dll` |
| DXIL reflection | Cross-platform (via `libdxcompiler`) | `IDxcUtils::CreateReflection()` → `ID3D12ShaderReflection` |
| SPIR-V reflection | Cross-platform | `spvc_compiler_create_shader_resources()` via SPIRV-Cross C API |

**Recommendation:** Use DXC's `libdxcompiler` for DXIL reflection (cross-platform) or SPIRV-Cross C API for SPIR-V reflection. Avoid `d3dcompiler.dll` entirely.

### Sampler State Parsing

FX9 `sampler_state {}` blocks must be parsed and mapped to MonoGame's `SamplerState` objects. The OpenGL path (MojoShader) must reconstruct GLSL sampler bindings from these declarations. This requires a custom parser for the `sampler_state {}` syntax — it is not part of standard HLSL.

### The MojoShader Dependency (OpenGL Path)

The OpenGL path does **not** use SPIRV-Cross. It:
1. Compiles HLSL SM1-3 to DXBC using FXC
2. P/Invokes into `libmojoshader` to translate DXBC → GLSL at runtime

MojoShader supports SM1-3 only, has no SM4+ support, and does not accept SPIR-V. There are two options:
- **Option A (compatibility):** Replicate the DXBC→MojoShader approach (but FXC is Windows-only — same Wine problem)
- **Option B (modern):** Use DXC → SPIR-V → SPIRV-Cross → GLSL. DXC's minimum supported target for SPIR-V emission is `vs_5_0`/`ps_5_0` (not SM3). DXC cannot compile `vs_3_0`/`ps_3_0` at all. The SM3-compatible HLSL code can be compiled as `vs_5_0`/`ps_5_0` in DXC as long as it avoids SM4+ features. The resulting SPIR-V → GLSL output must be accepted by MonoGame's OpenGL runtime. This is the direction proposed in [issue #6968](https://github.com/MonoGame/MonoGame/issues/6968).

> **ShadowDusk implements Option B.** Option A is rejected because it reintroduces the Windows-only FXC dependency that ShadowDusk exists to eliminate. All OpenGL path work targets DXC → SPIR-V → SPIRV-Cross → GLSL.

### GLSL/MSL Transpilation Correctness (Critical)

These issues produce visually wrong output if not handled. They are not optional.

All SPIRV-Cross options below are expressed in both C++ API and **C API** form. ShadowDusk uses the C API via P/Invoke (see Section 3). The pattern is: create options with `spvc_compiler_create_compiler_options()`, set values, apply with `spvc_compiler_install_compiler_options()`.

**Y-axis flip (NDC).** DirectX NDC: `SV_Position.y = +1` is the top of the screen. OpenGL NDC (without `GL_ARB_clip_control`): `y = +1` is the bottom. Without correction, all geometry renders upside-down.
- C++ API: `CompilerGLSL::Options::flip_vert_y = true`
- C API: `spvc_compiler_options_set_bool(opts, SPVC_COMPILER_OPTION_FLIP_VERTEX_Y, SPVC_TRUE)`

**Depth range (NDC Z).** DirectX NDC Z range is [0, 1]. OpenGL NDC Z is [-1, 1] by default. Without `GL_ARB_clip_control` (requires GL 4.5), the depth buffer is entirely wrong. Fix: remap depth in the vertex shader: `gl_Position.z = gl_Position.z * 2.0 - gl_Position.w`.
- C++ API: `CompilerGLSL::Options::depth_clip_mode = DepthClipZeroToOne`
- C API: `spvc_compiler_options_set_bool(opts, SPVC_COMPILER_OPTION_GLSL_DEPTH_ZERO_TO_ONE, SPVC_TRUE)` — tells SPIRV-Cross the source uses [0,1] depth range matching DXC's Vulkan output, so GLSL output does not insert an unwanted conversion.

**Combined image samplers.** HLSL separates `Texture2D` and `SamplerState` into distinct resource bindings. GLSL requires combined `sampler2D`. **Must be called before `compile()`** or the generated GLSL is invalid and will fail `glCompileShader`.
- C++ API: `compiler.build_combined_image_samplers()`
- C API: `spvc_compiler_build_combined_image_samplers(compiler)`

**`mul()` row vs column vector semantics.** In HLSL, `mul(M, v)` treats `v` as a column vector (right-multiply); `mul(v, M)` treats `v` as a row vector (left-multiply). GLSL's `*` operator for matrix × vector is always column-vector right-multiply. If the HLSL shader uses `mul(v, M)` (row-vector convention, common in D3D9 code) and it transpiles incorrectly, all transforms are wrong. SPIRV-Cross handles this automatically from the SPIR-V IR, but it must be verified with a known-good transform in testing. No option to set — handled automatically.

**`POSITION` vs `SV_Position` for SM3 vertex output.** Not applicable — ShadowDusk uses Option B (DXC/SPIR-V path), which handles this automatically.

**GLSL version targeting.** SPIRV-Cross must be told which GLSL version to emit. MonoGame's DesktopGL minimum is OpenGL 2.0+; the safe target is `#version 130` (OpenGL 3.0). Setting `330` or higher would break on older hardware.
- C++ API: `CompilerGLSL::Options::version = 130; CompilerGLSL::Options::es = false`
- C API: `spvc_compiler_options_set_uint(opts, SPVC_COMPILER_OPTION_GLSL_VERSION, 130)` and `spvc_compiler_options_set_bool(opts, SPVC_COMPILER_OPTION_GLSL_ES, SPVC_FALSE)`

**cbuffer to GLSL std140 layout mismatch.** HLSL cbuffer packing and GLSL `std140` differ: in std140, a `vec3` occupies 16 bytes (padded to `vec4` size), while HLSL packs a `float3` in 12 bytes. If the SPIR-V path uses SPIRV-Cross without DXC's `-fvk-use-dx-layout`, the GLSL uniform block uses std140 rules and the C# game's constant data (laid out for HLSL rules) is misinterpreted. Always compile with `-fvk-use-dx-layout` and ensure SPIRV-Cross emits a matching layout.

### Preprocessor and #include Handling

MonoGame's `Preprocessor.cs` flattens all `#include` directives before passing to FXC because FXC requires Windows file system access. ShadowDusk must implement a platform-independent include resolver.

**Where and how to inject platform macros:** Macros must be injected at **two points**:

1. **FX9 pre-pass (technique extraction):** The custom parser that strips `technique`/`pass` blocks runs before DXC. If the shader uses `#if OPENGL` / `#if HLSL` guards around technique blocks themselves (uncommon but possible), those conditionals must be resolved at this stage. Pass macros to whatever preprocessor is used here.

2. **DXC compilation:** Pass macros as `-D` flags on the DXC command line (e.g. `-D GLSL=1 -D OPENGL=1 -D MGFX=1`). This is the primary injection point for shader body conditionals (`#if SM4`, etc.).

Injecting only as DXC `-D` flags (step 2) is sufficient for most shaders. The technique pre-pass only needs macro awareness if `#if` conditionals guard entire `technique` blocks — verify against MonoGame's built-in effects before implementing step 1.

**Include path CLI flags:** MGCB passes include search paths to mgfxc using the same `/I <path>` flag convention as FXC. ShadowDusk must accept `/I <path>` (and `-I <path>`) and forward them to the preprocessor and to DXC's `-I` flag. The exact flags MGCB uses can be confirmed from MonoGame's `EffectProcessor.cs`.

Related issue: https://github.com/MonoGame/MonoGame/issues/1972

### Effect Annotations

Annotations are metadata attached to parameters, techniques, and passes. MonoGame reads them into `EffectAnnotation` collections (largely ignored at runtime but serialized to `.mgfxo`). The binary format includes annotation counts and data, so ShadowDusk must parse and serialize them even if unused.

API: https://docs.monogame.net/api/Microsoft.Xna.Framework.Graphics.EffectAnnotation.html

### Render State in Passes

`BlendState`, `DepthStencilState`, and `RasterizerState` can be declared inline in FX9 passes using D3D9-style tokens (`CullMode`, `AlphaBlendEnable`, `SrcBlend`, etc.). ShadowDusk's FX parser must parse and map these tokens to MonoGame's state objects, which are then serialized per-pass in `.mgfxo`.

### Constant Buffer Layout Gap (OpenGL vs DX)

MojoShader (SM3 / OpenGL path) does not use `cbuffer` syntax. Constants are communicated as individual float4/float registers (D3D9 `c0`–`c255` slots). The `.mgfxo` format stores per-constant buffer offsets extracted by reflection and must be mapped back to GLSL uniform locations by MojoShader at runtime. This is a non-trivial bridge for the OpenGL target.

### GLSL Uniform Naming Convention (Blocking Prerequisite)

MonoGame's OpenGL runtime binds C# `Effect.Parameters["name"]` to GLSL uniforms using `glGetUniformLocation`. The name passed must exactly match the uniform name in the compiled GLSL source.

**MojoShader path:** Generates register-based uniform names (`vs_c0`, `ps_c0`, etc. for D3D9 float4 slots). MonoGame's OpenGL runtime is built around these conventions.

**SPIRV-Cross path (Option B):** Preserves HLSL variable names by default (e.g. `WorldViewProj`, `DiffuseColor`). MonoGame's OpenGL runtime must look up by these HLSL names for them to bind correctly.

**The risk:** If MonoGame's OpenGL runtime (in `Effect.OpenGL.cs`) still uses MojoShader register-name lookups rather than HLSL-name lookups, the SPIRV-Cross GLSL output will compile without error but all parameter bindings will silently fail (uniforms default to 0). This must be verified in `Effect.OpenGL.cs` before the OpenGL path is considered viable. If register names are expected, SPIRV-Cross output would need post-processing to rename uniforms.

**Where to verify:** Must read MonoGame's `Effect.OpenGL.cs` source before implementing the OpenGL transpilation path to determine whether `glGetUniformLocation` uses HLSL variable names or MojoShader register names.

**UBO vs plain uniforms:** Also verify whether MonoGame's OpenGL runtime uses `glUniformBlockBinding` (UBOs) or `glUniform4fv` (plain uniforms), to determine whether to set `SPVC_COMPILER_OPTION_GLSL_EMIT_UNIFORM_BUFFER_AS_PLAIN_UNIFORMS`.

### Shader Permutations via Preprocessor

MonoGame's built-in effects use `#define`-based permutation patterns combined with many technique definitions (e.g. `BasicEffect.fx` has 32 techniques). ShadowDusk does not generate permutations, but must correctly handle `#if OPENGL`, `#if HLSL`, `#ifdef SM4` conditional blocks and compile each technique's passes with the appropriate platform macros.

### DXIL Signing Requirement (DX Path, Windows Only)

When DXC emits DXIL targeting DirectX 12, the bytecode must be cryptographically signed by `dxil.dll` before the Windows GPU driver will accept it. Without the signing step, the shader is silently rejected at runtime with an unhelpful error. This applies **only** to the DXIL→Direct3D path on Windows; the SPIR-V output path does not require signing.

`dxil.dll` ships with the Windows SDK and cannot be redistributed. For ShadowDusk's DX11 path (which uses DXBC from FXC, not DXIL from DXC), this is not an issue. If ShadowDusk ever adds a DX12/DXIL path, it must document the `dxil.dll` requirement for Windows deployments.

### MGCB Plugin Interface

For `ShadowDusk.MgcbPlugin`, there are **two distinct integration tiers** with very different implementation costs:

**Tier 1 — PATH-based drop-in (minimal, preferred first):** Place a binary named `mgfxc` (or `mgfxc.exe` on Windows) in `PATH`. MGCB calls it transparently. This is zero additional code beyond the CLI itself and is the primary goal described in CLAUDE.md. No NuGet plugin needed.

**Tier 2 — MGCB content processor plugin (full integration):** Implement MonoGame's `ContentProcessor<EffectContent, CompiledEffectContent>` interface and package as a NuGet plugin. This enables richer IDE integration (asset browser, error squiggles in MGCB editor) but requires implementing the full MonoGame Content Pipeline plugin contract. This is a separate, larger undertaking from the CLI tool.

The compiled `.mgfxo` output in Tier 2 is wrapped in XNB by the Content Pipeline.

Related source: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework.Content.Pipeline/MonoGame.Framework.Content.Pipeline.csproj

### Binary Format Version Targeting

ShadowDusk must decide which `MGFXVersion` to emit:

| Target runtime | MGFXVersion | Notes |
|----------------|-------------|-------|
| MonoGame 3.8.2 (stable) | `10` | Most users are here today |
| MonoGame `develop` / 4.0 | `11` | Newer format, not yet released |

A binary compiled with version 11 will be **rejected** by a version 10 runtime (`ReadEffect()` checks the version and throws). ShadowDusk should expose a `--mgfx-version` flag (or infer from a `--monogame-version` flag) and default to `10` until MonoGame 4.0 ships. Emitting version 11 only should be opt-in.

### BasicEffect.fx Permutation Pattern

`BasicEffect.fx` selects techniques by **index** from C# (not by name). The technique ordering in the `.fx` file must exactly match the index constants in `BasicEffect.cs`. ShadowDusk must preserve technique ordering when serializing to `.mgfxo`.

Source: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources/BasicEffect.fx

### Deterministic Output

CLAUDE.md requires byte-identical output given the same source and target. Both DXC and SPIRV-Cross are deterministic for fixed inputs — the concern is not their internal algorithms but **version drift**:

- DXC: same source + same flags + **same DXC version** = same SPIR-V. Different DXC versions will produce different (but valid) SPIR-V.
- SPIRV-Cross: same SPIR-V + **same SPIRV-Cross version** = same GLSL/MSL.

**How to achieve it:** Pin both tool versions in the `tools/restore.sh` script by exact release tag and verify hashes. The restore script is the version lock. As long as the pinned versions don't change, output is deterministic across machines and CI runs.

**What does not need special flags:** There is no need for `spirv-opt --freeze-spec-const` or special DXC ordering flags for this use case — the non-determinism risk in SPIR-V toolchains is version-to-version, not run-to-run for the same version.

### DXC Build Quality

If vendoring DXC prebuilts, read this analysis of binary reproducibility and build quality before choosing a source:
https://devlog.hexops.org/2024/building-the-directx-shader-compiler-better-than-microsoft/

---

---

## 8. WASM / Browser Runtime Path

ShadowDusk must also run inside .NET WASM for use in browser-based tools (XNA Fiddle by Vic). This section documents the constraints and integration approach.

### Why native binaries can't run in WASM

Both Vortice.Dxc and SPIRV-Cross are P/Invoke wrappers around native x64/arm64 binaries. These cannot execute inside a WebAssembly sandbox. P/Invoke itself is unavailable in the .NET WASM runtime unless the native library is itself compiled to WASM.

### WASM builds of DXC and SPIRV-Cross

Both tools have been compiled to WASM and are used in web-based shader playgrounds:

- **DXC WASM:** Exists as `dxc.js` / `dxc.wasm` in various community builds. Powers https://shader-playground.timjones.io/ and similar tools.
- **SPIRV-Cross WASM:** Official Emscripten build exists in the SPIRV-Cross repo. Used by several online shader conversion tools.

These are NOT available as NuGet packages and must be vendored as static assets bundled with the web app.

### ShadowDusk.Wasm architecture

`ShadowDusk.Wasm` contains a WASM-safe implementation of `IShaderCompiler`:

```csharp
public sealed class WasmShaderCompiler : IShaderCompiler
{
    // Uses [JSImport] to call into WASM-compiled DXC and SPIRV-Cross
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource, CompilerOptions options, CancellationToken ct);
}
```

The JS interop layer calls exported functions from the WASM modules. The compiled GLSL is then assembled into a `.mgfx` blob using the same binary writer as the CLI path — KNI and MonoGame use the same `.mgfx` format.

### Output format

KNI uses the same `.mgfx` binary format as MonoGame. For the browser/WebGL target, the GLSL payload is embedded inside the `.mgfx` blob exactly as the desktop OpenGL path does. No special output format is needed — the format difference is transparent to the caller.

### GLSL version for WebGL

WebGL 1.0 requires GLSL ES 1.00 (`#version 100`); WebGL 2.0 requires GLSL ES 3.00 (`#version 300 es`). KNI maps its two graphics profiles onto these: **`GraphicsProfile.Reach` → WebGL1 / ES-1.00** and **`GraphicsProfile.HiDef` → WebGL2 / ES-3.00**.

**As built (do NOT configure SPIRV-Cross for `300 es`):** ShadowDusk does *not* emit native ES-3.00. It emits the **legacy MojoShader GLSL dialect** (the same `mgfxc` produces — `#define ps_oC0 gl_FragColor`, `varying`, `texture2D`, `precision mediump`), valid under WebGL1/Reach *and* desktop GL. For **HiDef/WebGL2**, KNI's runtime converter (`ConvertGLSLToGLSL300es`, present since KNI **v3.14.9001**) rewrites that dialect to ES 3.00 at load time — so **one `.mgfx` serves both profiles** with no per-profile output and no SPIRV-Cross `300 es` configuration. The earlier guidance here (set `GLSL_VERSION=300, GLSL_ES=true`) was a greenfield assumption and is **not** what ships.

The load-bearing requirement: the fragment colour output must be emitted as a `#define`-aliased form (`#define ps_oC0 gl_FragColor`), because KNI's converter rewrites *only* that form — a raw `gl_FragColor` write survives untouched into ES 3.00 and fails to compile (GitHub issue #7). See [`plan/PHASE-33-webgl2-es300-hidef-output.md`](../plan/PHASE-33-webgl2-es300-hidef-output.md).

### Key references

| Resource | URL |
|----------|-----|
| KNI repo (nkast) | https://github.com/nkast/Kni |
| .NET WASM JS interop | https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/import-export-interop |
| Shader Playground (uses DXC WASM) | https://shader-playground.timjones.io/ |
| SPIRV-Cross Emscripten build | https://github.com/KhronosGroup/SPIRV-Cross/tree/main/wasm |

---

## Key URL Index

| Resource | URL |
|----------|-----|
| MonoGame main repo | https://github.com/MonoGame/MonoGame |
| mgfxc source | https://github.com/MonoGame/MonoGame/tree/develop/Tools/MonoGame.Effect.Compiler |
| Effect.cs runtime reader | https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs |
| BasicEffect.fx | https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources/BasicEffect.fx |
| MojoShader (MonoGame fork) | https://github.com/MonoGame/mojoshader |
| DirectXShaderCompiler | https://github.com/microsoft/DirectXShaderCompiler |
| DXC releases | https://github.com/microsoft/DirectXShaderCompiler/releases |
| DXC wiki | https://github.com/microsoft/DirectXShaderCompiler/wiki |
| DXC C++ API tutorial | https://simoncoenen.com/blog/programming/graphics/DxcCompiling |
| SPIRV-Cross | https://github.com/KhronosGroup/SPIRV-Cross |
| SPIRV-Cross reflection guide | https://github.com/KhronosGroup/SPIRV-Cross/wiki/Reflection-API-user-guide |
| HLSLcc (Unity) | https://github.com/Unity-Technologies/HLSLcc |
| hlslparser (Thekla) | https://github.com/Thekla/hlslparser |
| hlslparser (Unknown Worlds) | https://github.com/unknownworlds/hlslparser |
| shaderc | https://github.com/google/shaderc |
| CrossShader | https://github.com/alaingalvan/CrossShader |
| hlsl-specs | https://github.com/microsoft/hlsl-specs |
| HLSL spec PDF | https://microsoft.github.io/hlsl-specs/specs/hlsl.pdf |
| HLSL semantics (MS) | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics |
| HLSL packing rules (MS) | https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-packing-rules |
| FXC docs | https://learn.microsoft.com/en-us/windows/win32/direct3dtools/fxc |
| MonoGame mgfxc docs | https://docs.monogame.net/articles/getting_started/tools/mgfxc.html |
| Modernisation issue #6968 | https://github.com/MonoGame/MonoGame/issues/6968 |
| Hexops DXC build blog | https://devlog.hexops.org/2024/building-the-directx-shader-compiler-better-than-microsoft/ |
| Shader reflection blog | https://rtarun9.github.io/blogs/shader_reflection/ |
| cbuffer layout visualizer | https://maraneshi.github.io/HLSL-ConstantBufferLayoutVisualizer/ |
| DXIL spec | https://github.com/Microsoft/DirectXShaderCompiler/blob/main/docs/DXIL.rst |
| Two DX12 compilers blog | https://asawicki.info/news_1719_two_shader_compilers_of_direct3d_12 |
| Vortice.Dxc (C# DXC wrapper) | https://github.com/amerkoleci/Vortice.Windows |
| Veldrid.SPIRV (.NET SPIRV-Cross bindings) | https://github.com/mellinoe/veldrid-spirvcross |
| SPIRV-Tools (spirv-val / spirv-opt) | https://github.com/KhronosGroup/SPIRV-Tools |
