# MonoGame Runtime MGFX Compiler Research Document

**Purpose:** Design a custom runtime-capable MonoGame FX compiler that can compile shader source into MonoGame-compatible effect bytes and load them as a real `Microsoft.Xna.Framework.Graphics.Effect` without using MGCB or MonoGame's official shader compiler at runtime.

**Primary goal:** After compilation, the result must be a drop-in MonoGame `Effect` object.

```csharp
byte[] effectBytes = RuntimeMgfxCompiler.Compile(fxSource, options);
Effect effect = new Effect(graphicsDevice, effectBytes);
```

**Secondary goal:** Support a path that can work for WebAssembly/WASM scenarios, especially where invoking MonoGame's official MGCB/MGFXC toolchain is undesirable or impossible.

**Document status:** Research and architecture planning document.

**Date:** 2026-05-30

---

## 0. Project Alignment Note (ShadowDusk)

This document was originally written as greenfield research — *"how might one build a runtime MGFX compiler from scratch."* ShadowDusk has since **built most of it** (MGFX reader + writer, FX parser, DXC→SPIRV-Cross backend, an in-engine validation harness). Read the rest of this document with four corrections in mind, because the original framing is wrong about where the risk actually lives:

1. **The goal is not "loads as an `Effect`" — it is "renders the same pixels as `mgfxc`."** Loading is necessary but **not sufficient**. The bar is *in-engine behavioral equivalence*: a game whose `.fx` is compiled by ShadowDusk instead of `mgfxc`, loaded into the **real MonoGame/KNI runtime**, must render the same image. A `.mgfx` that constructs an `Effect` but draws the wrong thing is a failure, not a partial success. ShadowDusk's own renderer passing is a *proxy*, not the bar.

2. **The reason to exist is reach + fidelity, not runtime-only.** `mgfxc` already compiles `.fx` on Windows at build time, so matching it *only there* is pointless. ShadowDusk's differentiator is compiling `.fx` where `mgfxc` **cannot** run — Linux/macOS (no Wine, no DirectX SDK, no `fxc.exe`) and in-browser WASM — while producing output that behaves identically. Either axis alone is worthless; the product is *the result `mgfxc` gives, produced where `mgfxc` can't run.*

3. **The hard part is NOT the MGFX binary writer (largely solved). The hard part is emitting GLSL in MonoGame's MojoShader dialect with matching uniform/sampler naming, and keeping the MGFX metadata consistent with it.** SPIRV-Cross GLSL does **not** drop in. See the corrected §9.4 / §9.7 and §24, and `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`, which exists solely to bridge this gap.

4. **Compare same-backend only.** Validation always compares ShadowDusk vs `mgfxc` on the *same* target (GL↔GL, DX↔DX), never OpenGL output against DirectX output — they are different emitted artifacts (GLSL text vs GPU bytecode) loaded by different runtime paths. A green GL result says nothing about DX.

The sections below are kept for their architecture survey and reference value; treat the "roadmap" sections as a description of work ShadowDusk has *already done*, not a forward plan.

### 0.1 Where each part lives — map to ShadowDusk's implementation phases

Almost every "task" in this document already maps to a written phase plan under [`plan/`](plan/plan.md). Use this table to jump from a research-doc concept to the phase that actually implements (or will implement) it.

| Research-doc area | ShadowDusk phase | Status |
|---|---|---|
| §9.3 DXC — HLSL → SPIR-V / DXIL | [PHASE-4 DXC integration](plan/DONE/PHASE-4-dxc-integration.md) | ✅ Done |
| §12.6 Reflection (params, CBs, samplers, bindings) | [PHASE-5 shader reflection](plan/DONE/PHASE-5-shader-reflection.md) | ✅ Done |
| §9.4 SPIRV-Cross — SPIR-V → GLSL/ESSL | [PHASE-6 SPIRV-Cross transpilation](plan/DONE/PHASE-6-spirv-cross-glsl-transpilation.md) | ✅ Done |
| §6, §10.3, §12.8 MGFX binary writer | [PHASE-7 MGFX binary writer](plan/DONE/PHASE-7-mgfx-binary-writer.md) | ✅ Done |
| §11.2–11.6 Compiler orchestration / library split | [PHASE-8 compiler library](plan/DONE/PHASE-8-compiler-library.md) | ✅ Done |
| §11.7 CLI tool (`mgfxrt`-style → ShadowDusk's `mgfxc`) | [PHASE-9 CLI entry point](plan/DONE/PHASE-9-cli-entry-point.md) | ✅ Done |
| §14.1–14.3 Golden-file + reader + runtime-constructor tests | [PHASE-15 integration](plan/DONE/PHASE-15-integration-tests.md), [PHASE-16 image regression](plan/DONE/PHASE-16-image-regression-tests.md) | ✅ Done |
| §1, §5, §9.1–9.2, **§9.7 MojoShader**, §10.4, §14.4 — real `Effect` load + same-pixels equivalence + GL dialect | [PHASE-17 MonoGame runtime validation](plan/PHASE-17-monogame-runtime-validation.md) | 🚧 Active |
| §9.3 / §13 — DXC emits DXIL, not the SM≤5 **DXBC** MonoGame DX11 loads | [PHASE-18 DirectX DXBC](plan/PHASE-18-directx-dxbc.md) | 🆕 New (this gap) |
| §8 + §11.x + Task I — **WASM in-browser / runtime compilation** | [PHASE-19 WASM runtime compilation](plan/PHASE-19-wasm-runtime-compilation.md) | 🆕 New (this gap) |
| §15.4 native-binary distribution across OS | [PHASE-30 cross-platform CI](plan/PHASE-30-cross-platform-ci.md) | 🚧 Active |
| §15.4 / §8 path-safety + untrusted shader-source input validation | [PHASE-25 security hardening](plan/PHASE-25-security-hardening.md) | 🚧 Active |
| §11.8 MSBuild precompile task | [PHASE-20 deferred backlog](plan/PHASE-20-deferred-backlog.md) | 🗒️ Backlog |

The two **🆕 New** rows are the only substantive areas this document raised that lacked a phase plan — both now have one (created alongside this mapping). Everything else is already planned or built.

---

## 1. Executive Summary

MonoGame normally compiles `.fx` shader files through its Content Pipeline. The `.fx` file is imported and processed by MGCB / Pipeline Tool, passed through MonoGame's effect compiler, converted into MonoGame's MGFX effect format, and commonly wrapped into an `.xnb` content asset. At runtime, games usually call:

```csharp
Effect effect = Content.Load<Effect>("MyShader");
```

However, MonoGame also supports a lower-level loading path:

```csharp
Effect effect = new Effect(graphicsDevice, effectBytes);
```

This constructor accepts raw compiled MonoGame effect bytecode. MonoGame's MGFXC documentation explicitly states that directly compiled effects can be loaded using the `Effect` constructor that takes a byte array, while directly compiled effects are not content files and cannot be loaded by `ContentManager` unless wrapped through the content pipeline.

Therefore, a true drop-in runtime compiler should not primarily aim to generate `.xnb`. It should aim to generate **valid raw MGFX effect bytes** that the MonoGame `Effect` constructor accepts.

The project is best understood as an alternative implementation of MonoGame's MGFX compiler, not merely as a shader translator.

---

## 2. Definitions and Terminology

### 2.1 `.fx`

An FX shader source file. In XNA/MonoGame style usage, this is usually HLSL-like shader source plus FX structure such as:

- parameters
- textures
- samplers
- techniques
- passes
- vertex shader references
- pixel shader references
- optional preprocessor directives
- sometimes state-like metadata

### 2.2 MGCB

MonoGame Content Builder. It reads a `.mgcb` file and processes assets through importers and processors.

For effects, MGCB usually invokes the effect importer/processor path, which eventually compiles the `.fx` source and packages the result.

### 2.3 MGFXC / 2MGFX

MonoGame's effect compiler toolchain. It compiles `.fx` source into MonoGame's runtime MGFX effect format.

MGFXC can produce directly compiled effect output, often referred to as `.mgfxo` or similar raw compiled effect data depending on context and tooling.

### 2.4 `.mgfxo` / raw MGFX bytes

The compiled MonoGame effect data format that can be loaded with:

```csharp
new Effect(graphicsDevice, effectBytes)
```

This is the important target for a drop-in runtime compiler.

### 2.5 `.xnb`

The Content Pipeline packaged runtime asset format. An `.xnb` can contain many asset types, including compiled effects. It is loaded through `ContentManager`:

```csharp
Content.Load<Effect>("MyShader")
```

The `.xnb` is not the same thing as raw MGFX bytes. It is a content container/wrapper around processed content.

### 2.6 `Effect`

MonoGame's runtime shader/effect object:

```csharp
Microsoft.Xna.Framework.Graphics.Effect
```

The `Effect` class exposes the normal MonoGame shader usage API:

```csharp
effect.Parameters["WorldViewProj"].SetValue(matrix);
effect.CurrentTechnique = effect.Techniques["Basic"];
foreach (EffectPass pass in effect.CurrentTechnique.Passes)
{
    pass.Apply();
    // draw
}
```

The entire project should preserve this API if it wants to be a drop-in replacement.

---

## 3. Current MonoGame Workflow: End-to-End

The standard workflow is:

```text
MyShader.fx
  ↓
Added to .mgcb content project
  ↓
MGCB / Pipeline Tool builds content
  ↓
EffectImporter + EffectProcessor process effect
  ↓
MGFXC / 2MGFX compiles effect source
  ↓
Compiled MGFX effect data is produced
  ↓
Usually wrapped into MyShader.xnb
  ↓
Copied to Content output folder
  ↓
Runtime calls Content.Load<Effect>("MyShader")
  ↓
MonoGame creates an Effect instance
```

The runtime usage is then normal MonoGame:

```csharp
Effect effect = Content.Load<Effect>("MyShader");

effect.Parameters["WorldViewProj"].SetValue(worldViewProjection);

effect.CurrentTechnique = effect.Techniques["Basic"];

foreach (EffectPass pass in effect.CurrentTechnique.Passes)
{
    pass.Apply();
    graphicsDevice.DrawPrimitives(...);
}
```

For `SpriteBatch`, usage can look like:

```csharp
spriteBatch.Begin(effect: effect);
spriteBatch.Draw(texture, position, Color.White);
spriteBatch.End();
```

---

## 4. Desired Custom Workflow

The desired workflow removes MGCB and MonoGame's official compiler from the active runtime path:

```text
FX source text
  ↓
Custom runtime-capable compiler
  ↓
MonoGame-compatible raw MGFX byte[]
  ↓
new Effect(graphicsDevice, effectBytes)
  ↓
Normal MonoGame Effect usage
```

Possible user-facing API:

```csharp
string fxSource = File.ReadAllText("Shaders/MyShader.fx");

byte[] effectBytes = RuntimeMgfxCompiler.Compile(
    fxSource,
    new RuntimeMgfxCompileOptions
    {
        Profile = RuntimeMgfxProfile.OpenGL,
        Debug = true,
        IncludeResolver = path => File.ReadAllText(path)
    });

Effect effect = new Effect(GraphicsDevice, effectBytes);
```

Convenience API:

```csharp
Effect effect = RuntimeMgfxCompiler.CompileToEffect(
    GraphicsDevice,
    fxSource,
    new RuntimeMgfxCompileOptions
    {
        Profile = RuntimeMgfxProfile.OpenGL
    });
```

---

## 5. Critical Architectural Decision

There are two possible project directions:

### 5.1 Parallel Runtime Effect System

This would create a custom class:

```csharp
RuntimeEffect effect = RuntimeFxCompiler.Compile(...);
```

This class would mimic MonoGame's `Effect`, but it would not be a real `Microsoft.Xna.Framework.Graphics.Effect`.

This is **not** the desired path for this project because it would not be drop-in-compatible with existing MonoGame APIs such as:

```csharp
SpriteBatch.Begin(effect: effect)
```

or code expecting:

```csharp
Microsoft.Xna.Framework.Graphics.Effect
```

### 5.2 Real MonoGame MGFX Bytecode Output

This produces a real MonoGame `Effect`:

```csharp
byte[] bytes = RuntimeMgfxCompiler.Compile(...);
Effect effect = new Effect(graphicsDevice, bytes);
```

This is the correct path for a drop-in compiler.

The project should therefore define success as:

> The compiler output can be passed to `new Effect(GraphicsDevice, byte[])` and behaves like an effect compiled by MonoGame's official toolchain.

---

## 6. What the Custom Compiler Must Produce

The compiler must generate MonoGame-compatible MGFX binary data.

A simplified conceptual structure is:

```text
MGFX header
format version
platform/profile identifier
constant buffers
parameters
textures
samplers
techniques
passes
shader stage records
compiled shader blobs or translated shader text
reflection metadata
binding metadata
```

This is more than merely translating HLSL to GLSL. The MonoGame `Effect` runtime needs metadata to expose:

```csharp
effect.Parameters
effect.Techniques
effect.CurrentTechnique.Passes
```

and to bind constants, textures, and samplers correctly when `EffectPass.Apply()` is called.

---

## 7. Why Generating `.xnb` Is Not Required

For the drop-in runtime compiler goal, generating `.xnb` is unnecessary.

There are two valid runtime loading paths:

### ContentManager path

```text
.xnb → Content.Load<Effect>()
```

This requires the Content Pipeline wrapper format.

### Raw Effect bytecode path

```text
raw MGFX bytes → new Effect(GraphicsDevice, byte[])
```

This bypasses `ContentManager`.

The second path is the better target because:

- It avoids implementing the full `.xnb` writer.
- It avoids MGCB.
- It works with in-memory compiled output.
- It still returns a real MonoGame `Effect`.

---

## 8. WASM-Specific Considerations

WASM makes the problem harder.

A browser/WASM runtime generally cannot depend on the same native tooling that desktop MGCB/MGFXC workflows rely on. It also should avoid shipping a massive compiler stack unless absolutely necessary.

For WASM/WebGL, the practical runtime shader target is usually GLSL ES / ESSL, because WebGL consumes shader source through browser APIs.

Possible paths:

### 8.1 Compile HLSL/FX to MGFX entirely inside WASM

This is the most ambitious path.

It would require:

- parsing FX structure in .NET/WASM
- compiling or translating HLSL inside WASM
- generating GLSL ES or whatever MonoGame's WebGL backend expects
- writing MonoGame-compatible MGFX bytes in memory
- creating `Effect` from those bytes

Major difficulties:

- HLSL compilers are usually native, large, and complex.
- Compiling DXC, ShaderConductor, or SPIRV-Cross to WASM may be possible but non-trivial.
- Download size could become large.
- Browser memory and startup time could suffer.
- Interop between .NET WASM and native WASM compiler modules may be complex.
- MonoGame's Web/WASM backend may expect specific shader data layout in the MGFX bytes.

### 8.2 Precompile to MGFX bytes outside WASM, load bytes in WASM

This is the most practical production path.

```text
Development/build machine:
  .fx source
    ↓
  custom compiler CLI or MSBuild task
    ↓
  MGFX bytes for WebGL/OpenGL profile

WASM runtime:
  download/load MGFX bytes
    ↓
  new Effect(graphicsDevice, bytes)
```

This still avoids MGCB and `.xnb`, but it does not require browser-side HLSL compilation.

### 8.3 Hybrid approach

Support both:

- Desktop runtime compilation for development tools/editors.
- WASM precompiled byte loading for production.
- Optional future WASM compiler backend for limited shader subsets.

Recommended approach:

```text
Phase 1: desktop compiler outputs MonoGame-compatible MGFX bytes
Phase 2: WASM runtime loads those bytes
Phase 3: optional in-browser compilation for a restricted subset
```

---

## 9. Open-Source Tool Survey

### 9.1 MonoGame MGFXC / Effect Compiler

**Role:** Reference implementation and compatibility target.

MonoGame's own MGFXC is the tool that compiles effects for MonoGame. The documentation states that compiled effects can be loaded using the `Effect` constructor that takes a byte array, and that directly compiled effects are not content files for `ContentManager`.

Use cases for this project:

- Study source code to understand the MGFX binary layout.
- Reuse code if licensing and dependencies allow.
- Compare output from custom compiler against MGFXC output.
- Build golden test files.

Risks:

- The compiler may depend on native or platform-specific components.
- The internal format may change between MonoGame versions.
- Reusing it inside WASM may not be practical.

### 9.2 MonoGame `Effect` Runtime Source

**Role:** The final consumer of the generated bytes.

The custom compiler should be built by reading what `Effect` expects, not by guessing.

Important items to inspect:

- MGFX signature
- MGFX format version
- platform/profile values
- constant buffer layout
- parameter metadata reading
- technique/pass reading
- shader stage reading
- DirectX vs OpenGL differences
- error handling for incompatible versions/platforms

### 9.3 DirectX Shader Compiler / DXC

**Role:** Modern HLSL compiler.

DXC is Microsoft's open-source HLSL compiler. It compiles HLSL to DXIL and can also support SPIR-V generation.

Potential use:

```text
HLSL → DXIL for DirectX-style targets
HLSL → SPIR-V for cross-compilation path
```

Pros:

- Modern HLSL support.
- Open source.
- Cross-platform builds are possible.
- SPIR-V path exists.

Cons:

- Native dependency.
- Large and complex.
- WASM integration may be heavy.
- DXIL may not be what MonoGame's current runtime expects for all profiles.

### 9.4 SPIRV-Cross

**Role:** Convert SPIR-V to high-level shader languages and provide reflection.

Potential use:

```text
HLSL → SPIR-V → GLSL / ESSL
```

SPIRV-Cross is useful because it can emit GLSL/ESSL and provide reflection data. Reflection can help build parameter, texture, sampler, and uniform metadata.

Pros:

- Mature cross-compilation tool.
- Reflection support.
- Supports GLSL/ESSL output, useful for WebGL/OpenGL.

Cons:

- Native dependency unless wrapped or compiled appropriately.
- Reflection output may not map one-to-one to MonoGame's expected MGFX metadata.
- Need careful handling of uniform buffers, matrix layout, samplers, and bindings.

> **⚠️ Corrected per §0.3 — SPIRV-Cross GLSL does NOT drop into MonoGame's GL runtime.** This is *the* blocker ShadowDusk proved empirically (see [`PHASE-17 §3.6`](plan/PHASE-17-monogame-runtime-validation.md)). SPIRV-Cross emits a **modern** dialect: `#version 140`, `in_var_TEXCOORD0`/`out_var_SV_Target`, `texture()`, a named `type_Globals` UBO, samplers like `uniform sampler2D _39;`. MonoGame's OpenGL runtime instead consumes the **MojoShader** dialect: no `#version` (GLSL 110), `varying vec4 vTexCoord0;` (the runtime links the built-in SpriteEffect VS to the custom PS *by varying name*), `gl_FragColor`, `texture2D()`, `uniform sampler2D ps_s0;`, and free uniforms as `ps_uniforms_vec4[N]` bound via `glUniform4fv`. So SPIRV-Cross output must be **rewritten** to that dialect before it will even link — that is exactly what `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs` does. Treat "SPIR-V → GLSL via SPIRV-Cross" as *step one of two*; the MojoShader rewrite (§9.7) is the mandatory second step.

### 9.5 ShaderConductor

**Role:** One-stop HLSL cross-compilation pipeline.

ShaderConductor is designed to cross-compile HLSL to GLSL, ESSL, MSL, and older HLSL variants.

Potential use:

```text
HLSL → GLSL/ESSL/MSL/older HLSL
```

Pros:

- Built specifically for cross-compiling HLSL.
- Supports several shader stages.
- Could simplify backend management.

Cons:

- Native dependency.
- Project maturity must be evaluated.
- Need to verify output compatibility with MonoGame's runtime expectations.

### 9.6 glslang

**Role:** GLSL/ESSL front end and SPIR-V toolchain component.

Historically glslang included an HLSL frontend, but current status should be checked carefully. As of 2026, the glslang repository notes that its HLSL frontend is deprecated and planned for removal in a future major version.

Recommendation:

- Do not build a new long-term architecture around glslang's HLSL frontend.
- glslang may still be useful for GLSL/ESSL validation and SPIR-V workflows.

### 9.7 MojoShader — **the GLSL dialect ShadowDusk must reproduce (not just "study")**

**Role:** Defines the exact GLSL conventions MonoGame's OpenGL runtime consumes. This is **not** an optional legacy tool to evaluate — for the GL target it is the *output specification*.

> **⚠️ Corrected per §0.3.** `mgfxc` compiles `.fx` to DX bytecode and then runs **MojoShader** to translate that into GLSL at build time. MonoGame's GL runtime is built around the GLSL MojoShader emits, so any drop-in compiler must emit the *same conventions*: GLSL 110 (no `#version`), `varying` names that match the built-in SpriteEffect VS, `gl_FragColor`, `texture2D()`, `ps_s{n}` samplers, and `ps_uniforms_vec4[N]` / `vs_uniforms_vec4[N]` uniform arrays uploaded via `glUniform4fv` (`ConstantBuffer.PlatformApply`). ShadowDusk doesn't *use* MojoShader (it would require Wine/`fxc`), but it must **match MojoShader's output dialect** — which is why `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs` exists to rewrite SPIRV-Cross GLSL (§9.4) into it. See [`PHASE-17 §3.6`](plan/PHASE-17-monogame-runtime-validation.md) for the full field-by-field dialect diff that proved this.

Use it as:

- The **reference for the required GLSL output conventions** (varying names, sampler naming, uniform array layout) — not just "the existing path to study."
- A check on whether ShadowDusk's rewritten GLSL will link against MonoGame's SpriteEffect VS.

Risks / caveats:

- ShadowDusk deliberately does **not** run MojoShader itself (it depends on `fxc`/Wine). The risk is *failing to match* its dialect, not using it.
- MojoShader targets legacy shader models; ShadowDusk's DXC→SPIRV-Cross path can express more, so the rewrite must down-convert to what the GL 110 runtime accepts.

---

## 10. Recommended Implementation Strategy

### 10.1 Do Not Start by Supporting Full `.fx`

Full XNA/MonoGame `.fx` compatibility is a large project.

The initial implementation should support a constrained subset:

- vertex shader + pixel shader
- one or more techniques
- one or more passes
- scalar/vector/matrix parameters
- textures
- samplers
- basic preprocessor defines
- includes through callback
- OpenGL/WebGL-friendly shader profile

Avoid initially:

- full render-state blocks
- annotations
- complex arrays
- arbitrary structs everywhere
- compute shaders
- geometry/hull/domain shaders
- DirectX and OpenGL parity on day one
- exact full XNA compatibility

### 10.2 Prioritize OpenGL/WebGL Profile First

Because WASM is a major motivation, prioritize the target that maps best to WebGL/OpenGL.

Suggested initial target:

```text
RuntimeMgfxProfile.OpenGL
```

or more specific:

```text
RuntimeMgfxProfile.DesktopGL
RuntimeMgfxProfile.WebGL2
```

However, the final bytes must match what the MonoGame runtime expects for the actual platform.

### 10.3 Treat MGFX Binary Writing as a First-Class Component

The most important library component is the MGFX writer:

```csharp
public sealed class MgfxBinaryWriter
{
    public byte[] Write(MgfxEffectModel model, MgfxWriteOptions options);
}
```

The internal model should be explicit:

```csharp
public sealed class MgfxEffectModel
{
    public MgfxProfile Profile { get; init; }
    public List<MgfxParameter> Parameters { get; } = [];
    public List<MgfxTexture> Textures { get; } = [];
    public List<MgfxSampler> Samplers { get; } = [];
    public List<MgfxTechnique> Techniques { get; } = [];
    public List<MgfxShader> Shaders { get; } = [];
    public List<MgfxConstantBuffer> ConstantBuffers { get; } = [];
}
```

### 10.4 Build Against MonoGame's `Effect` Reader

The compiler should be developed from the runtime reader backwards.

Process:

1. Inspect MonoGame `Effect` loading code.
2. Document every field it reads.
3. Write a binary reader test that parses MGFXC output.
4. Write a binary writer that reproduces equivalent data.
5. Verify `new Effect(graphicsDevice, customBytes)` works.
6. Compare runtime behavior with official MGFXC output.

---

## 11. Proposed NuGet Package Structure

### 11.1 Package Overview

Recommended package split:

```text
MonoGame.RuntimeMgfx.Abstractions
MonoGame.RuntimeMgfx.Core
MonoGame.RuntimeMgfx.Compiler
MonoGame.RuntimeMgfx.Compiler.Native
MonoGame.RuntimeMgfx.MonoGame
MonoGame.RuntimeMgfx.Cli
MonoGame.RuntimeMgfx.MSBuild
MonoGame.RuntimeMgfx.Tests
```

Final names can be changed, but the separation is important.

---

### 11.2 `MonoGame.RuntimeMgfx.Abstractions`

Purpose:

- shared public interfaces
- compile options
- diagnostics
- target enums
- no native dependencies
- minimal or no MonoGame dependency

Example API:

```csharp
public interface IRuntimeMgfxCompiler
{
    RuntimeMgfxCompileResult Compile(RuntimeMgfxCompileRequest request);
}

public sealed class RuntimeMgfxCompileRequest
{
    public required string SourceText { get; init; }
    public required RuntimeMgfxCompileOptions Options { get; init; }
}

public sealed class RuntimeMgfxCompileResult
{
    public bool Success { get; init; }
    public byte[]? EffectBytes { get; init; }
    public IReadOnlyList<RuntimeMgfxDiagnostic> Diagnostics { get; init; } = [];
}

public sealed class RuntimeMgfxDiagnostic
{
    public RuntimeMgfxDiagnosticSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
}
```

Compile options:

```csharp
public sealed class RuntimeMgfxCompileOptions
{
    public RuntimeMgfxProfile Profile { get; init; }
    public bool Debug { get; init; }
    public bool Optimize { get; init; } = true;
    public Dictionary<string, string> Defines { get; init; } = new();
    public Func<string, string?>? IncludeResolver { get; init; }
}

public enum RuntimeMgfxProfile
{
    DirectX11,
    OpenGL,
    DesktopGL,
    WebGL1,
    WebGL2
}
```

---

### 11.3 `MonoGame.RuntimeMgfx.Core`

Purpose:

- FX parser
- preprocessor wrapper
- effect AST
- intermediate representation
- MGFX binary reader/writer
- no native compiler dependencies if possible

Important classes:

```csharp
public sealed class FxParser
public sealed class FxPreprocessor
public sealed class MgfxEffectModel
public sealed class MgfxBinaryReader
public sealed class MgfxBinaryWriter
public sealed class MgfxCompatibilityValidator
```

The core package should be testable without a graphics device.

---

### 11.4 `MonoGame.RuntimeMgfx.Compiler`

Purpose:

- high-level compile orchestration
- connects parser, shader backend, reflection, and MGFX writer

Example:

```csharp
public sealed class RuntimeMgfxCompiler : IRuntimeMgfxCompiler
{
    private readonly IShaderCompilerBackend _shaderBackend;

    public RuntimeMgfxCompileResult Compile(RuntimeMgfxCompileRequest request)
    {
        // 1. preprocess
        // 2. parse FX structure
        // 3. compile shader stages
        // 4. reflect parameters
        // 5. build MgfxEffectModel
        // 6. write MGFX bytes
    }
}
```

---

### 11.5 `MonoGame.RuntimeMgfx.Compiler.Native`

Purpose:

- optional native compiler integrations
- DXC backend
- ShaderConductor backend
- SPIRV-Cross backend

This package should be optional because native dependencies complicate WASM and package distribution.

Interfaces:

```csharp
public interface IShaderCompilerBackend
{
    ShaderCompileResult Compile(ShaderCompileRequest request);
}

public sealed class ShaderCompileRequest
{
    public string SourceText { get; init; } = string.Empty;
    public string EntryPoint { get; init; } = string.Empty;
    public ShaderStage Stage { get; init; }
    public RuntimeMgfxProfile TargetProfile { get; init; }
    public Dictionary<string, string> Defines { get; init; } = new();
}
```

Possible implementations:

```csharp
public sealed class DxcShaderCompilerBackend : IShaderCompilerBackend
public sealed class ShaderConductorBackend : IShaderCompilerBackend
public sealed class SpirvCrossBackend : IShaderCompilerBackend
public sealed class PrecompiledShaderBackend : IShaderCompilerBackend
```

---

### 11.6 `MonoGame.RuntimeMgfx.MonoGame`

Purpose:

- MonoGame integration helpers
- returns actual `Effect`
- depends on `MonoGame.Framework`

Example:

```csharp
public static class RuntimeMgfxEffectLoader
{
    public static Effect CompileToEffect(
        GraphicsDevice graphicsDevice,
        string fxSource,
        RuntimeMgfxCompileOptions options)
    {
        RuntimeMgfxCompileResult result = RuntimeMgfxCompiler.Default.Compile(
            new RuntimeMgfxCompileRequest
            {
                SourceText = fxSource,
                Options = options
            });

        if (!result.Success || result.EffectBytes is null)
            throw new RuntimeMgfxCompileException(result.Diagnostics);

        return new Effect(graphicsDevice, result.EffectBytes);
    }
}
```

---

### 11.7 `MonoGame.RuntimeMgfx.Cli`

Purpose:

- developer tool
- compile `.fx` to raw MGFX bytes without MGCB
- validate output
- compare output with MonoGame MGFXC

Example command:

```bash
mgfxrt compile MyShader.fx --profile OpenGL --out MyShader.mgfxo
```

Useful commands:

```bash
mgfxrt compile
mgfxrt inspect
mgfxrt validate
mgfxrt compare
mgfxrt dump
```

Example:

```bash
mgfxrt inspect MyShader.mgfxo
```

Output:

```text
MGFX Version: 10
Profile: OpenGL
Parameters:
  WorldViewProj : Matrix
  Texture       : Texture2D
Techniques:
  Basic
    Pass P0
      VertexShader: VSMain
      PixelShader: PSMain
```

---

### 11.8 `MonoGame.RuntimeMgfx.MSBuild`

Purpose:

- optional build-time precompilation without MGCB
- useful for WASM production
- integrates into `.csproj`

Example project usage:

```xml
<ItemGroup>
  <RuntimeMgfx Include="Shaders/**/*.fx" Profile="OpenGL" />
</ItemGroup>
```

Build output:

```text
bin/Debug/net8.0/Content/Shaders/MyShader.mgfxo
```

Runtime loading:

```csharp
byte[] bytes = File.ReadAllBytes("Content/Shaders/MyShader.mgfxo");
Effect effect = new Effect(GraphicsDevice, bytes);
```

---

## 12. Proposed Internal Compilation Pipeline

```text
Input FX source text
  ↓
Source manager
  ↓
Include resolution
  ↓
Preprocessing / defines
  ↓
FX parser
  ↓
Effect AST
  ↓
Entry point extraction
  ↓
Shader stage compilation / translation
  ↓
Reflection extraction
  ↓
MGFX effect model construction
  ↓
MGFX binary writer
  ↓
byte[] accepted by MonoGame Effect constructor
```

Detailed steps:

### 12.1 Source Manager

Responsibilities:

- track source file name
- track include files
- normalize line endings
- preserve line mappings for diagnostics

### 12.2 Include Resolver

Support callback-based includes:

```csharp
public Func<string, string?>? IncludeResolver { get; init; }
```

This lets users load includes from:

- disk
- embedded resources
- HTTP
- zip packages
- virtual file systems
- in-memory dictionaries

### 12.3 Preprocessor

Minimum support:

- `#define`
- `#ifdef`
- `#ifndef`
- `#if`
- `#else`
- `#elif`
- `#endif`
- `#include`

This could initially delegate to a third-party HLSL preprocessor if available, or be handled by the selected shader compiler backend.

### 12.4 FX Parser

Parse:

- global parameters
- texture declarations
- sampler declarations
- shader functions
- techniques
- passes
- vertex shader assignments
- pixel shader assignments

Example FX:

```hlsl
float4x4 WorldViewProj;
Texture2D MainTexture;
sampler2D MainSampler = sampler_state
{
    Texture = <MainTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

VertexShaderOutput VSMain(VertexShaderInput input)
{
    // ...
}

float4 PSMain(VertexShaderOutput input) : COLOR0
{
    // ...
}

technique Basic
{
    pass P0
    {
        VertexShader = compile vs_3_0 VSMain();
        PixelShader = compile ps_3_0 PSMain();
    }
}
```

### 12.5 Shader Backend

Compile or translate each referenced stage.

Example pass:

```text
Pass P0:
  VertexShader = VSMain
  PixelShader = PSMain
```

Backend receives:

```text
source text
entry point
stage
target profile
defines
```

Backend returns:

```text
compiled shader blob or shader source
reflection info
diagnostics
```

### 12.6 Reflection

Reflection must discover or preserve:

- parameter names
- parameter types
- constant buffer offsets
- matrix layout
- texture bindings
- sampler bindings
- uniform names
- shader stage usage

This is essential because MonoGame's `EffectParameterCollection` must work.

### 12.7 MGFX Model Construction

Build an internal model that mirrors what MonoGame expects.

Example:

```csharp
MgfxEffectModel model = new()
{
    Profile = RuntimeMgfxProfile.OpenGL,
    Parameters = ...,
    Techniques = ...,
    Shaders = ...,
    ConstantBuffers = ...
};
```

### 12.8 MGFX Binary Writing

Serialize the model to the exact byte layout expected by MonoGame's current `Effect` reader.

This must be versioned.

Example:

```csharp
byte[] bytes = new MgfxBinaryWriter().Write(model, new MgfxWriteOptions
{
    MonoGameVersion = SupportedMonoGameVersion.V3_8_4,
    Profile = RuntimeMgfxProfile.OpenGL
});
```

---

## 13. Compatibility Matrix

The compiler should explicitly declare supported MonoGame versions.

Example:

| MonoGame Version | MGFX Format | Status | Notes |
|---|---:|---|---|
| 3.8.1 | version-specific | Planned | Need source inspection and tests |
| 3.8.2 | version-specific | Planned | Need source inspection and tests |
| 3.8.2 | version-specific | **ShadowDusk's actual first target** (MGFX v10, default) | DesktopGL 3.8.2.x; see plan.md "Key Decisions" |
| 3.8.4 | version-specific | Later candidate | Evaluate after 3.8.2 is green |
| develop/nightly | may change | Experimental | Avoid promising stable support |

The MGFX format is versioned. A compiler that emits the wrong version can fail at runtime with errors such as older/newer MGFX format or invalid MGFX file.

Recommendation:

- Lock the first release to one exact MonoGame version.
- Add explicit runtime checks.
- Include clear diagnostics.

---

## 14. Testing Strategy

### 14.1 Golden File Tests

Generate official MGFXC output for small shaders.

Test cases:

- minimal color shader
- texture sampling shader
- matrix transform shader
- multiple parameters
- multiple passes
- multiple techniques
- sampler variations

Compare:

- parseability
- metadata
- runtime behavior
- rendered output

### 14.2 Binary Reader Tests

Write a tool that reads official MGFXC output and prints a normalized representation.

Example:

```json
{
  "profile": "OpenGL",
  "parameters": [
    { "name": "WorldViewProj", "type": "Matrix" },
    { "name": "MainTexture", "type": "Texture2D" }
  ],
  "techniques": [
    {
      "name": "Basic",
      "passes": ["P0"]
    }
  ]
}
```

Then compare the normalized representation of official output and custom output.

### 14.3 Runtime Constructor Tests

For every generated byte array:

```csharp
using Effect effect = new Effect(graphicsDevice, bytes);
```

Verify:

- constructor succeeds
- parameter names exist
- technique names exist
- pass count matches
- `Apply()` succeeds
- drawing succeeds

### 14.4 Render Snapshot Tests

Render a known scene and compare pixels or image hash.

Example cases:

- solid color triangle
- textured quad
- SpriteBatch effect
- matrix transform
- alpha handling

### 14.5 WASM Smoke Tests

For WASM:

- precompile MGFX bytes outside browser
- load bytes as embedded resource or downloaded asset
- create `Effect`
- draw a simple quad
- verify browser console has no shader compile errors

---

## 15. Risks and Unknowns

### 15.1 MGFX Binary Format Stability

The MGFX binary format is internal/versioned. It can change between MonoGame versions.

Mitigation:

- Support exact MonoGame versions.
- Add compatibility checks.
- Include binary inspectors.
- Keep test matrix aligned with MonoGame releases.

### 15.2 WebGL Backend Expectations

Even if the bytes load on DesktopGL, the Web/WASM backend may have differences.

Mitigation:

- Test on actual WASM target early.
- Avoid assuming DesktopGL success means WebGL success.
- Keep shader subset WebGL-friendly.

### 15.3 HLSL Feature Support

Modern HLSL features may not map cleanly to older MonoGame effect profiles or WebGL.

Mitigation:

- Define a supported subset.
- Fail clearly on unsupported features.
- Provide diagnostics and suggested rewrites.

### 15.4 Native Compiler Distribution

DXC, ShaderConductor, and SPIRV-Cross introduce native dependency issues.

Mitigation:

- Keep native backends optional.
- Ship platform-specific packages if needed.
- Provide CLI/build-time compilation for production.
- Avoid requiring native compiler dependencies in WASM runtime package.

### 15.5 SpriteBatch Compatibility

If the compiler emits a real `Effect`, `SpriteBatch.Begin(effect: effect)` should work as long as the shader matches SpriteBatch expectations.

But SpriteBatch shaders require compatible inputs/semantics.

Mitigation:

- Provide SpriteBatch shader templates.
- Test SpriteBatch-specific effects.
- Document expected vertex/pixel inputs.

---

## 16. MVP Scope

### 16.1 MVP Goal

Compile a small FX source string into MonoGame-compatible MGFX bytes and load it with:

```csharp
Effect effect = new Effect(graphicsDevice, bytes);
```

### 16.2 MVP Supported Features

- one `.fx` source string
- simple `#define` support
- optional include resolver
- vertex shader and pixel shader
- one technique
- one pass
- parameters:
  - `float`
  - `float2`
  - `float3`
  - `float4`
  - `float4x4`
- `Texture2D`
- basic sampler
- OpenGL/DesktopGL target first
- output raw MGFX bytes

### 16.3 MVP Exclusions

- `.xnb` generation
- ContentManager integration
- full FX annotation support
- render state blocks
- compute shaders
- geometry shaders
- DirectX + OpenGL parity
- arbitrary preprocessor complexity
- guaranteed browser-side HLSL compilation

---

## 17. Suggested Development Roadmap

### Phase 0: Source Research

- Inspect MonoGame `Effect` loading code.
- Inspect MonoGame MGFXC output code.
- Document binary layout.
- Compile sample shaders with official MGFXC.
- Build an MGFX inspector tool.

Deliverable:

```text
mgfxrt inspect OfficialShader.mgfxo
```

### Phase 1: Binary Reader and Writer

- Implement MGFX binary reader.
- Implement normalized model.
- Implement MGFX binary writer.
- Round-trip simple official MGFX files.

Deliverable:

```text
official.mgfxo → read → model → write → custom.mgfxo
```

Then:

```csharp
new Effect(graphicsDevice, customBytes)
```

### Phase 2: Minimal FX Parser

- Parse parameters.
- Parse technique/pass structure.
- Extract VS/PS entry points.

Deliverable:

```text
simple.fx → MgfxEffectModel without real shader compilation
```

### Phase 3: Shader Backend Integration

- Integrate one backend.
- Recommended first backend depends on target:
  - Use existing MonoGame-compatible path if possible for quickest proof.
  - Otherwise use ShaderConductor or DXC/SPIRV-Cross for GLSL/ESSL.

Deliverable:

```text
simple.fx → real MGFX bytes → Effect loads → draw succeeds
```

### Phase 4: MonoGame Integration Package

- Add `CompileToEffect` helper.
- Add diagnostics.
- Add examples.

Deliverable:

```csharp
Effect effect = RuntimeMgfx.CompileToEffect(GraphicsDevice, fxSource, options);
```

### Phase 5: CLI and MSBuild

- Add CLI compiler.
- Add MSBuild task.
- Support precompilation without MGCB.

Deliverable:

```bash
mgfxrt compile MyShader.fx --profile OpenGL --out MyShader.mgfxo
```

### Phase 6: WASM Validation

- Use precompiled MGFX bytes in WASM.
- Load into MonoGame Web/WASM build.
- Draw known scene.

Deliverable:

```text
WASM sample renders shader compiled by custom tool, no MGCB involved.
```

### Phase 7: Optional Runtime WASM Compiler

- Evaluate compiling shader compiler backend to WASM.
- Consider restricted shader subset.
- Measure download size, memory, and startup cost.

Deliverable:

```text
Browser-side source → MGFX bytes → Effect
```

Only pursue if the build-time/precompiled approach is insufficient.

---

## 18. Example User-Facing API Design

### 18.1 Direct Compile to Bytes

```csharp
RuntimeMgfxCompileResult result = RuntimeMgfxCompiler.Default.Compile(
    new RuntimeMgfxCompileRequest
    {
        SourceText = fxSource,
        Options = new RuntimeMgfxCompileOptions
        {
            Profile = RuntimeMgfxProfile.OpenGL,
            Debug = true,
            Defines =
            {
                ["OPENGL"] = "1"
            },
            IncludeResolver = includePath =>
            {
                string fullPath = Path.Combine("Shaders", includePath);
                return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            }
        }
    });

if (!result.Success)
{
    foreach (RuntimeMgfxDiagnostic diagnostic in result.Diagnostics)
        Console.WriteLine(diagnostic);

    throw new InvalidOperationException("Shader compilation failed.");
}

Effect effect = new Effect(GraphicsDevice, result.EffectBytes!);
```

### 18.2 Convenience Compile to Effect

```csharp
Effect effect = RuntimeMgfxEffect.Compile(
    GraphicsDevice,
    fxSource,
    new RuntimeMgfxCompileOptions
    {
        Profile = RuntimeMgfxProfile.OpenGL
    });
```

### 18.3 Loading Precompiled Bytes

```csharp
byte[] bytes = File.ReadAllBytes("Shaders/MyShader.mgfxo");
Effect effect = new Effect(GraphicsDevice, bytes);
```

### 18.4 WASM-Friendly Embedded Asset

```csharp
await using Stream stream = await httpClient.GetStreamAsync("Shaders/MyShader.mgfxo");
using MemoryStream memory = new();
await stream.CopyToAsync(memory);

Effect effect = new Effect(GraphicsDevice, memory.ToArray());
```

---

## 19. Example Shader Subset for Initial Support

```hlsl
float4x4 WorldViewProj;
Texture2D MainTexture;
sampler2D MainSampler = sampler_state
{
    Texture = <MainTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

struct VSInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 PSMain(VSOutput input) : COLOR0
{
    return tex2D(MainSampler, input.TexCoord);
}

technique Basic
{
    pass P0
    {
        VertexShader = compile vs_3_0 VSMain();
        PixelShader = compile ps_3_0 PSMain();
    }
}
```

---

## 20. Agent-Decomposable Task List

This section is intentionally written as task chunks for another agent.

### Task A: Inspect MonoGame Effect Runtime

Goal:

- Determine exact MGFX binary fields read by `Effect`.

Inputs:

- MonoGame source repository.
- `MonoGame.Framework/Graphics/Effect/Effect.cs`.
- Related effect reader classes.

Outputs:

- `docs/mgfx-binary-layout.md`
- list of fields, types, order, version behavior
- platform/profile identifiers

### Task B: Inspect MonoGame Effect Compiler

Goal:

- Determine how official MGFXC writes MGFX bytes.

Inputs:

- MonoGame source repository.
- `Tools/MonoGame.Effect.Compiler`.

Outputs:

- compiler architecture notes
- serializer/writer notes
- dependency list
- reuse feasibility analysis

### Task C: Create Golden Shader Corpus

Goal:

- Build official MGFXC output samples.

Outputs:

```text
tests/GoldenShaders/MinimalColor.fx
tests/GoldenShaders/TexturedSprite.fx
tests/GoldenShaders/MatrixTransform.fx
tests/GoldenShaders/MultiPass.fx
tests/GoldenShaders/Official/*.mgfxo
```

### Task D: Build MGFX Inspector

Goal:

- Parse official MGFX files and dump normalized JSON.

Command:

```bash
mgfxrt inspect shader.mgfxo --json
```

Outputs:

- `MgfxBinaryReader`
- `MgfxDumpModel`
- JSON output

### Task E: Build MGFX Writer

Goal:

- Write valid MGFX bytes from a model.

Tests:

- model → bytes → reader → equivalent model
- bytes → `new Effect(graphicsDevice, bytes)` succeeds

### Task F: Build Minimal FX Parser

Goal:

- Parse enough FX syntax to identify parameters, techniques, passes, and entry points.

Outputs:

- `FxParser`
- `FxAst`
- diagnostics with line/column

### Task G: Integrate Shader Backend

Goal:

- Compile/translate shader stages.

Candidate backends:

- MonoGame compiler reuse
- DXC
- ShaderConductor
- SPIRV-Cross pipeline

Outputs:

- `IShaderCompilerBackend`
- at least one implementation
- reflection model

### Task H: Runtime MonoGame Integration

Goal:

- Provide helper API returning real `Effect`.

Outputs:

- `RuntimeMgfxEffect.Compile(...)`
- sample MonoGame project

### Task I: WASM Validation

Goal:

- Prove precompiled custom MGFX bytes load and render in WASM.

Outputs:

- WASM sample
- known shader render
- documented limitations

---

## 21. Recommended Repo Layout

```text
RuntimeMgfx/
  README.md
  LICENSE
  docs/
    architecture.md
    mgfx-binary-layout.md
    compatibility.md
    wasm-notes.md
    shader-subset.md
  src/
    RuntimeMgfx.Abstractions/
    RuntimeMgfx.Core/
    RuntimeMgfx.Compiler/
    RuntimeMgfx.Compiler.Native/
    RuntimeMgfx.MonoGame/
    RuntimeMgfx.Cli/
    RuntimeMgfx.MSBuild/
  samples/
    MinimalDesktopGL/
    SpriteBatchEffect/
    WasmPrecompiledEffect/
  tests/
    RuntimeMgfx.Core.Tests/
    RuntimeMgfx.Compiler.Tests/
    RuntimeMgfx.MonoGame.Tests/
    GoldenShaders/
  tools/
    generate-golden-files.ps1
    compare-mgfx-output.ps1
```

---

## 22. Documentation Needed for Users

### 22.1 User README

Must explain:

- this does not generate `.xnb`
- this generates raw MGFX bytes
- load with `new Effect(GraphicsDevice, bytes)`
- use normal MonoGame `Effect` API after loading
- supported MonoGame versions
- supported shader subset
- platform/profile limitations

### 22.2 Compatibility Page

Must include:

- MonoGame version compatibility
- MGFX format version compatibility
- DesktopGL status
- WindowsDX status
- Web/WASM status
- known unsupported syntax

### 22.3 Troubleshooting Page

Common errors:

- invalid MGFX signature
- unsupported MGFX version
- built for wrong platform/profile
- parameter not found
- shader compile failed
- WebGL shader compile failed
- SpriteBatch input mismatch

---

## 23. Key Design Principles

1. **Real MonoGame `Effect` or nothing.**
   - The compiler must output bytes accepted by `new Effect(GraphicsDevice, byte[])`.

2. **Do not require `.xnb`.**
   - `.xnb` is a Content Pipeline container. Raw MGFX bytes are enough for runtime `Effect` construction.

3. **Keep WASM runtime small.**
   - Prefer precompiled MGFX bytes for WASM initially.

4. **Version compatibility must be explicit.**
   - MGFX is versioned. Support exact MonoGame versions.

5. **Start with a small shader subset.**
   - Full FX compatibility can come later.

6. **Build tooling around inspection and comparison.**
   - The fastest path to correctness is comparing against official MGFXC output.

7. **Separate compiler backends from MGFX writing — but the GLSL dialect is a co-equal compatibility core (§0.3).**
   - The MGFX writer is *one* compatibility core; for OpenGL the **MojoShader GLSL dialect** (§9.7) is the other. DXC/ShaderConductor/SPIRV-Cross are replaceable backends, but their GLSL output must be rewritten to MonoGame's dialect regardless of which one you pick.

---

## 24. Research Conclusions

> **⚠️ Corrected per §0.3 — this section's original "hard part" was half right.** ShadowDusk has since built the MGFX writer; the binary container is **largely solved** (see `src/ShadowDusk.Core/MgfxWriter.cs`). The remaining wall for the OpenGL target is **emitting GLSL in MonoGame's MojoShader dialect** (§9.4 / §9.7) with metadata that matches it — not the binary layout. Read the paragraph below as "*two* co-equal hard parts: the MGFX container **and** the GLSL dialect," with the dialect being the one greenfield builders underestimate.

The requested project is feasible in concept, but the hard part is not simply shader compilation. There are **two** hard parts: (1) generating the MonoGame-specific MGFX binary format exactly enough that MonoGame's `Effect` class accepts it, **and** (2) — for OpenGL — emitting GLSL in the MojoShader dialect MonoGame's GL runtime expects so the shader actually links and reads its parameters. A perfect `.mgfx` container is **necessary but not sufficient**: ShadowDusk proved a structurally-correct `.mgfx` still renders wrong (or fails to link) until the GLSL is rewritten to MojoShader conventions ([`PHASE-17 §3.6`](plan/PHASE-17-monogame-runtime-validation.md)).

The most practical path is:

```text
1. Reverse-document MonoGame's current MGFX reader/writer behavior.
2. Build an MGFX inspector.
3. Build an MGFX writer.
4. Verify output with `new Effect(GraphicsDevice, byte[])`.
5. Add a minimal FX parser.
6. Add shader backend integration.
7. Package as NuGet with CLI/MSBuild support.
8. Use precompiled raw MGFX bytes for WASM first.
9. Consider in-WASM compilation only after the desktop/precompiled workflow is proven.
```

The correct initial user promise should be:

> Compile FX source into raw MonoGame-compatible MGFX bytes without MGCB, then load those bytes as a real MonoGame `Effect`.

The project should avoid promising:

> Full in-browser HLSL/FX compilation with complete XNA/MonoGame FX compatibility.

That can be a future goal, but it is too large and risky for the MVP.

---

## 25. References

1. MonoGame MGFXC documentation: https://docs.monogame.net/articles/getting_started/tools/mgfxc.html
2. MonoGame Custom Effects documentation: https://docs.monogame.net/articles/getting_started/content_pipeline/custom_effects.html
3. MonoGame `Effect` API documentation: https://docs.monogame.net/api/Microsoft.Xna.Framework.Graphics.Effect.html
4. MonoGame `Effect.cs` source: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs
5. MonoGame runtime shader compilation discussion #8931: https://github.com/MonoGame/MonoGame/discussions/8931
6. MonoGame effect system refactor issue #9188: https://github.com/MonoGame/MonoGame/issues/9188
7. MonoGame modernization of 2MGFX issue #6968: https://github.com/MonoGame/MonoGame/issues/6968
8. DirectX Shader Compiler / DXC: https://github.com/microsoft/DirectXShaderCompiler
9. DXC SPIR-V documentation: https://github.com/microsoft/DirectXShaderCompiler/blob/main/docs/SPIR-V.rst
10. SPIRV-Cross: https://github.com/KhronosGroup/SPIRV-Cross
11. SPIRV-Cross reflection guide: https://github.com/KhronosGroup/SPIRV-Cross/wiki/Reflection-API-user-guide
12. ShaderConductor: https://github.com/microsoft/ShaderConductor
13. glslang: https://github.com/KhronosGroup/glslang
14. Khronos article on glslang HLSL to SPIR-V: https://www.khronos.org/news/permalink/use-glslang-to-translate-hlsl-shaders-to-spir-v
