# ShadowDusk — Compilation Pipeline

How a `.fx` source becomes loadable shader bytes, stage by stage, with what each
component is, how it works, and **why** it is done that way.

ShadowDusk is **one faithful compiler** with three backend tails. The front half (read
the FX file, resolve includes, inject platform macros) is shared. The back half forks by
target: **OpenGL/WebGL**, **DirectX 11**, and **FNA**. The headline pipeline is the
OpenGL branch:

```
HLSL  →[DXC]→  SPIR-V  →[SPIRV-Cross]→  GLSL  →[MonoGameGlslRewriter]→  .mgfx
```

The principle behind every stage: each host runs the *same* faithful components, and the
output is **deterministic** (same source + version + target ⇒ byte-identical bytes on every
OS, pinned by `CrossHostByteIdentityTests`). "Faithful" means the result **loads and renders
like the reference compiler** (`mgfxc` for MonoGame/KNI, `fxc /T fx_2_0` for FNA), not that
it is byte-identical to it (byte-identity to `mgfxc` is a non-goal).

```
INPUT  shader.fx  (HLSL + FX9 effect blocks)
   │
   ▼
┌──────────────────────────────┐   Stage 1
│  FX9 pre-parser (lexer +     │   strips technique/pass/sampler_state/render-state/
│  flat token scanner)         │   annotation blocks (not valid HLSL); rewrites legacy
└───────────────┬──────────────┘   texture/sampler/tex2D forms
        ┌────────┴────────┐
        ▼                 ▼
   StrippedHLSL       Metadata (techniques, passes [VS/PS entry + profile],
   (pure HLSL)        samplers, render states, annotations)
        │
        ▼
┌──────────────────────────────┐   Stage 2
│  Preprocessor                │   flatten #includes; inject platform macros
│  (managed; leaves #if to DXC)│   (DX: MGFX,HLSL,SM4 · GL: MGFX,GLSL,OPENGL · FNA: FNA,HLSL,SM3)
└───────────────┬──────────────┘
                ▼
        ┌───────────────── compile fork (by target) ─────────────────┐
        ▼                          ▼                                  ▼
   DirectX 11                 OpenGL / WebGL                        FNA
        │                          │                                  │
   vkd3d-shader               DXC (Vortice.Dxc)                  vkd3d-shader
   (or d3dcompiler_47)        -spirv                             D3D_BYTECODE
        │                          │                                  │
        ▼                          ▼                                  ▼
   DXBC (SM≤5)               SPIR-V ──[SPIRV-Cross]──► GLSL 140    D3D9 token stream
        │                          │                                  │
        │                          ▼                                  │
        │                  MonoGameGlslRewriter                       │
        │                  (MojoShader-dialect GLSL)                  │
        │                          │                                  │
        └────────────┬─────────────┘                                  │
                     ▼                                                 ▼
              Reflection (RdefReader / DXIL oracle /            CTAB reflection
              SpirvReflector → one ReflectedEffect)                   │
                     │                                                 ▼
                     ▼                                          Fx2EffectWriter
              MgfxWriter (.mgfx v10)                            (fx_2_0 .fxb)
                     │
            ┌────────┴────────┐
            ▼                 ▼
      file on disk      byte[] in memory
      (CLI)             (library / WASM / KNI)
```

---

## Stage 1 — FX9 pre-parser

**What it is.** A lexer plus a flat, single-pass token scanner (`src/ShadowDusk.HLSL/Lexer/FxLexer.cs`,
`src/ShadowDusk.HLSL/FxPreParser.cs`). FX9 *effect* blocks (`technique`, `pass`,
`sampler_state`, render states, FX annotations) are a legacy D3DX format inherited by XNA
and MonoGame. No HLSL compiler can parse them, so they must be removed before any compiler
sees the file.

**How it works.** The lexer turns the source into tokens, emitting single-character tokens
for `{ } < > ( ) ; = , / * . -` and **silently dropping** the operator characters
`: + [ ] & | ! ? % ^ ~` (they only ever occur inside code bodies). The scanner then walks
the token stream and:

- extracts the technique/pass tree (each pass's VS and PS entry-point names and shader
  profiles), the samplers, the render states, and the parameter annotations into a metadata
  object; and
- emits **StrippedHlsl**: the original source text with the FX blocks erased and a handful
  of legacy constructs rewritten forward so a modern compiler accepts them.

The stripped output is **rebuilt from the original source characters** (erase/replace spans),
never regenerated from tokens, so the dropped operator characters never corrupt emitted code,
they only affect the scanner's own pattern matching.

It runs in one of two modes. `RewriteToSm4` (MonoGame/KNI, the default) rewrites legacy D3D9
forms forward for DXC/vkd3d: legacy `texture`/`sampler` declarations to `Texture2D`/`SamplerState`,
`tex2D(s, uv)` to `s.Sample(...)`, and a `: COLOR` pixel return semantic to `: SV_Target`.
`PreserveSm3` (FNA) leaves all of that **verbatim** so vkd3d compiles the D3D9 source natively.

**Why.** Stripping FX syntax up front means every downstream compiler sees plain HLSL, and
keeping the transform a text-span edit preserves line numbers for diagnostics. The forward
rewrites are what let a single classic `#if OPENGL … #else …` shader compile on the modern
SM4-level GL/DX path *and* the SM3 FNA path from one source.

> The flat, scope-unaware design is also the source of a known bug class: because operator
> characters are dropped, a fragmented token stream inside a function body can look like an FX
> construct. The annotation, sampler, render-state, and legacy-texture heuristics each carry a
> discriminator to avoid mis-firing on valid HLSL. See `docs/glsl-uniform-naming.md` and the
> per-heuristic guards in `FxPreParser.cs`.

---

## Stage 2 — Preprocessor

**What it is.** A pure-managed include-flattener and macro-injector
(`src/ShadowDusk.Core/Preprocessor/Preprocessor.cs`). It deliberately does **not** evaluate
`#if`/`#define`/macro expansion, it inlines `#include`s and prepends `#define` lines, leaving
all conditional evaluation to the native compiler downstream.

**How it works.** `Preprocessor.Flatten` runs over the StrippedHlsl from Stage 1. It resolves
each `#include` against the including file's own directory first, then the configured search
paths (first match wins), honors `#pragma once`, and emits `#line` directives so downstream
diagnostics point at the original files. Cycle detection uses an include **stack**: a diamond
(two paths reaching one common header) is legal and the header is emitted twice; a true cycle
fails with `SD0002`. A missing include fails with `SD0001`. Directive scanning is comment- and
string-aware, so a commented-out `#include` is never honored.

It then prepends the platform macro set for the target. The macros are simple presence flags
(each defined as `1`):

| Target | Macros injected |
|---|---|
| **DirectX** | `MGFX`, `HLSL`, `SM4` |
| **OpenGL** | `MGFX`, `GLSL`, `OPENGL` |
| **Vulkan** | `MGFX`, `HLSL`, `VULKAN`, `SM6` |
| **FNA** | `FNA`, `HLSL`, `SM3` (intentionally no `MGFX`) |

**Why.** Flattening to one self-contained translation unit means no include handler has to be
threaded into the native compilers, and all three backends see identical pre-resolved source.
The macro sets are load-bearing: the **OpenGL** set must stay free of `SM4`/`SM6`, or every
`#if OPENGL` / `#if SM4` shader would take the wrong branch and the legacy DX9/SM2 branch fed
to the GL DXC→SPIR-V backend crashes DXC's SPIR-V codegen. The **FNA** set omits `MGFX` and
the SM4/SM6 flags so the standard MonoGame `Macros.fxh` falls through to its D3D9/SM2 branch.

---

## Stage 3 — the compile fork

The same StrippedHlsl is handed to a different backend per target, because the three runtimes
load different bytecode:

| Target | Frontend / backend | Produces | Loaded by |
|---|---|---|---|
| **OpenGL / WebGL** | DXC → SPIR-V → SPIRV-Cross → GLSL → rewriter | MojoShader-dialect GLSL text | MonoGame/KNI GL runtime |
| **DirectX 11** | vkd3d-shader (default) or `d3dcompiler_47` | DXBC (SM ≤ 5) | MonoGame/KNI DX11 runtime |
| **FNA** | vkd3d-shader (D3D_BYTECODE) | D3D9 token stream in an fx_2_0 container | FNA3D + MojoShader |

A pass's VS and PS entry points are compiled **separately**, one native invocation per
(stage, entry-point).

### Stage 3a — DXC (the HLSL frontend, OpenGL and Vulkan)

**What it is.** Microsoft's DirectXShaderCompiler, via the `Vortice.Dxc` package
(`src/ShadowDusk.HLSL/Dxc/DxcShaderCompiler.cs`). It is the faithful HLSL → SPIR-V frontend.
It is used **only** on the GL/Vulkan/Metal paths (and to produce a DXIL blob for reflection on
GL); it never produces the DX11 shader payload.

**How it works.** Each entry point is compiled with `-T <profile>` and `-E <entry>` (the
`.fx`-declared name flows straight through, no mangling). For OpenGL the profile is `ps_5_0` /
`vs_5_0` with `-spirv -fvk-use-dx-layout` (and `-auto-binding-space 1` for the pixel stage),
so SPIR-V carries D3D constant-buffer offsets and clip-space conventions. Matrices are packed
**row-major** (`-Zpr`). On the desktop GL path the source is actually compiled **twice**: once
to DXIL (used only to feed the native reflection oracle) and once to SPIR-V (fed to SPIRV-Cross);
the DXIL compile is skipped when a managed SPIR-V reflector is injected (the browser path). DXC
is constructed lazily and invoked through a per-platform raw vtable call rather than Vortice's
string wrapper, which is what makes it run on Linux and macOS at all.

**Why.** DXC is the canonical, cross-platform HLSL compiler, the whole "no Wine, no Windows
SDK" promise rests on it running natively everywhere. The DX-layout flags keep reflection and
the transpiled GLSL in agreement about cbuffer offsets.

### Stage 3b — the DXBC backend (DirectX 11)

**What it is.** A native D3D-bytecode backend behind the `IDxbcShaderCompiler` seam, with two
implementations: **vkd3d-shader** (the default, cross-platform;
`src/ShadowDusk.HLSL/Vkd3d/Vkd3dShaderCompiler.cs`) and **`d3dcompiler_47`** (a Windows-only
correctness oracle, opt-in via `CompilerOptions.DxbcBackend`).

**How it works and why DXC is not used here.** MonoGame 3.8's DX11 runtime loads **DXBC**
(Shader Model ≤ 5), but DXC's minimum DirectX output is **SM6 DXIL**, which that runtime
rejects. So the DX11 payload comes from a real DXBC producer. vkd3d-shader compiles the
StrippedHlsl to DXBC on every OS; the same DXBC bytes are **also** the reflection source
(parsed as DXBC, see Stage 4). One subtlety: the `d3dcompiler` oracle packs matrices
**column-major** (matching `fxc`, the runtime, and vkd3d), while the DXC/GL path uses
row-major, this is intentional and the two paths are validated independently.

### Stage 3c — the FNA fx_2_0 path

**What it is.** A wholly separate path for `PlatformTarget.Fna`
(`CompilationPipeline.RunFna`), producing a D3D9 Effects-framework binary (`fx_2_0`).

**How it works.** Stage 1 runs in `PreserveSm3` mode, so D3D9 constructs (`sampler2D`, `tex2D`,
`: COLOR` outputs) pass through unchanged. vkd3d compiles each SM2–3 stage to a **D3D9 token
stream** (`VKD3D_SHADER_TARGET_D3D_BYTECODE`); a small patch canonicalizes a few instruction
forms MojoShader is strict about. The per-shader constant table (CTAB) is reflected, and
`Fx2EffectWriter` assembles the `.fxb`. SM4+ is rejected (`SD0300`, MojoShader's ceiling is
`vs_3_0`/`ps_3_0`). This path shares nothing with the MGFX path, which guarantees that adding
FNA cannot change the GL/DX output.

**Why.** FNA consumes one `fx_2_0` `.fxb` for all of its backends via FNA3D + MojoShader, a
different container and a different shader model from MGFX, so it gets its own pipeline.

---

## Stage 4 — Reflection

**What it is.** Three interchangeable reflectors that each parse a different bytecode format
into the **same** `ReflectedEffect` shape (constant buffers, textures, samplers, signatures):

- `RdefReader` — DXBC RDEF (SM5), **pure-managed**, proven deeply equal to the Windows
  `D3DReflect` oracle.
- `DxilReflectionExtractor` — DXIL (SM6), the native `ID3D12ShaderReflection` oracle (runs
  inside the bundled `dxcompiler`; the one piece WASM cannot run).
- `SpirvReflector` — SPIR-V, **pure-managed**, mirrors the DXIL oracle's field semantics for
  the browser path.

**How it works.** A `ConstantBufferReflection` carries its variables in offset order, and each
`VariableReflection` records its `StartOffset` (byte offset within the buffer), size, and type
shape. A shared `ParameterListBuilder` flattens those cbuffer variables into the user-settable
`ParameterReflection` list (merging the FX annotations from Stage 1) and appends the texture
(and, on GL, sampler) object parameters. Because the SPIR-V was emitted with `-fvk-use-dx-layout`,
the managed SPIR-V reflector reads byte offsets directly from the SPIR-V `Offset` decorations,
so all three reflectors report the same layout. (SPIR-V discards HLSL semantic strings, so its
input/output signatures are intentionally empty, which is why it only serves the PS-only browser
corpus.)

**Why pure-managed reflectors matter.** Removing the Windows-only `D3DReflect` dependency lets
DirectX `.mgfx` compiles run on Linux, macOS, and in the browser. Only the *source* of the
`ReflectedEffect` changes per host; the `.mgfx` bytes do not.

---

## Stage 5 — SPIRV-Cross (OpenGL only)

**What it is.** Khronos' SPIR-V → GLSL transpiler, driven via P/Invoke on desktop and via JS
interop in the browser (`src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs`). It turns the SPIR-V
from Stage 3a into GLSL.

**How it works.** ShadowDusk sets exactly these options:

| Option | Value | Why |
|---|---|---|
| `FlipVertexY` | **off** | The Y-flip is **not** done here, see the rewriter's `posFixup` below |
| `FixupClipspaceDepthConvention` | **on** | Emits the D3D→GL depth line; the rewriter uses it as an insertion anchor |
| `GlslVersion` | **140** | Stripped again by the rewriter; the value only needs to be modern enough to transpile |
| `GlslEs` / `VulkanSemantics` | off / off | Desktop GLSL, no Vulkan binding semantics |
| combined image samplers | **always built** | Required, a safe no-op on texture-free shaders but a crash if skipped on textured ones |

**Why `FlipVertexY` is off (a deliberate correction).** A statically baked `gl_Position.y =
-gl_Position.y;` only matches MonoGame's render-target case and renders normal backbuffer draws
upside-down. The real contract is `mgfxc`/MojoShader's runtime `posFixup` uniform (set per draw
to `+1` for the backbuffer, `-1` for a render target), which the rewriter injects in Stage 6.
Turning `FlipVertexY` on would double-flip every vertex-driven GL effect.

**Reserved-word renaming.** SPIRV-Cross has its own internal sanitizer: if a SPIR-V identifier
collides with a GLSL reserved word (for example a uniform named `noise`, which clashes with the
deprecated GLSL `noiseN` built-ins), it renames it (typically by prefixing `_`, e.g. `_noise`)
so the emitted GLSL is legal. ShadowDusk does not configure or extend that list. This rename is
correct and required, but it has a downstream consequence, see *Design notes* below.

---

## Stage 6 — MonoGameGlslRewriter (OpenGL only)

**What it is.** A pure, dependency-free string transform
(`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`) that converts SPIRV-Cross's *modern* GLSL into
the *legacy* MojoShader-era dialect MonoGame's GL runtime actually binds against. The full
contract is in `docs/glsl-uniform-naming.md`.

**Why it exists (the core reason).** MonoGame's OpenGL runtime looks resources up by **fixed
names**, not the author's HLSL names: free uniforms are one packed array `ps_uniforms_vec4[N]` /
`vs_uniforms_vec4[N]`, samplers are `ps_s{slot}`, stage varyings are `vFrontColor` / `vTexCoord{n}`,
and the pixel output is `gl_FragColor`. SPIRV-Cross emits a modern `type_Globals` UBO,
`in_var_TEXCOORD0` varyings, and an opaque `_39` sampler. Loaded as-is, `glGetUniformLocation`
returns `-1` for everything, every parameter reads zero, and the shader **loads but renders
black**. The rewrite is what makes the output a faithful drop-in.

**How it works.** It strips the `#version` line (MojoShader GLSL is versionless 110-era), merges
the UBO block(s) into the packed register array (member uses become `ps_uniforms_vec4[i].<swizzle>`),
renames samplers to `ps_s{slot}`, maps stage I/O to the legacy varying names, routes the pixel
output to `gl_FragColor` (via a `#define ps_oC0 gl_FragColor` alias, MRT slots to `gl_FragData[n]`),
lowers `texture()` to dimension-specific legacy builtins (`texture2D`/`textureCube`/`texture3D`),
lowers `round`/`roundEven` to `floor((x)+0.5)` (valid in every GLSL profile), and on the vertex
stage injects the `posFixup` uniform plus its two fixup lines (the Y-flip and half-pixel offset),
remaps a legacy `: POSITION` output to `gl_Position`, and reconstructs `mat4` uniforms transposed
(to cancel SPIRV-Cross's `mul(v, M)` → `M * v` operand swap; a naive reconstruction renders
geometry inside-out). It is aggressively fail-loud: an unmodelled uniform member type, sampler
kind, vertex semantic, or a vertex-stage sampler fails with `SD0210` rather than emitting GLSL
that silently mis-renders.

Crucially, the packed array is indexed by **register position**, not by the member's emitted
name, so whatever name SPIRV-Cross chose for a uniform is irrelevant to the GLSL body itself.

---

## Stage 7 — the binary writers

**`.mgfx` (MonoGame/KNI)** — `src/ShadowDusk.Core/MgfxWriter.cs`. The body is serialized first so
the header can hash it. Layout: the 4-byte signature `MGFX`, a version byte, a profile byte
(OpenGL = 0, DirectX 11 = 1, Vulkan = 3), and a 4-byte effect key (a managed MD5 over the body,
managed because the WASM runtime lacks MD5); then the body, **constant buffers → shaders → parameters
→ techniques/passes (with their render-state blocks)**; then the trailing `MGFX` footer the
runtime validates. The shader blob is **GLSL text for the GL profile and DXBC for the DX profile**.
Size guards fail loudly (`SD0020`–`SD0022`) rather than truncate into a corrupt file. The default
version is **v10**, the most backwards-compatible choice: a v10 `.mgfx` loads on MonoGame 3.8.2,
every newer MonoGame, and KNI. (A `--mgfx-version 11` escape hatch adds the two per-shader
diagnostic strings MonoGame's v11 reader expects; it is opt-in and never required for correct
output.)

**`fx_2_0` `.fxb` (FNA)** — `src/ShadowDusk.Core/Fx2EffectWriter.cs`. A fundamentally different,
offset-addressed D3D9 container: an 8-byte header, a "data pool" of typedef/string/value blobs
addressed by offset, then a structured stream of parameter/technique/pass/object records. Because
MojoShader does no bounds-checking on those offsets, the writer runs an exhaustive validator that
enforces every invariant MojoShader relies on (ASCII-only names, square matrices only, textures
declared before the samplers that reference them, and every shader CTAB constant matching an
effect parameter name exactly). Full spec: `docs/fx2-binary-format.md`.

**KNIFX v11 (KNI)** — `src/ShadowDusk.Core/KnifxWriter.cs`. An additive newer container (signature
`KNIF`) carrying the **same** MojoShader-dialect GLSL body as the MGFX path, wrapped in a
multi-backend directory with packed-integer encoding. The render-state blocks are byte-identical
to v10 (it reuses the MGFX writer's render-state routine). It is opt-in; the default product
output stays MGFX v10.

---

## Design notes

### Parameter binding: name today, location is the robust contract

A `.mgfx` must let the game set parameters by name (`effect.Parameters["Foo"].SetValue(...)`),
so the file records, for each register in a constant-buffer record, **which effect parameter it
holds**. On the GL path that correlation is currently made by **name**: the rewriter's per-uniform
layout (the names parsed out of the SPIRV-Cross GLSL) is matched against the reflected parameter
list (`IndexOfParam`, by `ParameterReflection.Name`).

This was the one place that depends on the emitted GLSL identifier, and it is the weak link. When
SPIRV-Cross renames a reserved-word uniform (Stage 5), the GLSL side says `_noise` while the
reflected parameter is still `noise`, so a pure name match fails. DirectX and FNA never go through
GLSL, so they are unaffected; and `mgfxc` never hits this because MojoShader packs constants by
D3D9 register index and never emits named uniforms at all.

The fix is to **fall back to binding by location** the moment the name match
misses — never for a shader that already resolves by name, so existing output is byte-identical.
The data is already in the reflection: a `ConstantBufferReflection` lists its `VariableReflection`
members in offset order with both the original name and the byte `StartOffset`, and the GL uniform
layout carries each member's base register. `CompilationPipeline.IndexOfParamByRegister` correlates
the GL uniform's `BaseRegister × 16` to the cbuffer member's `StartOffset`, then takes that
member's original name, recovering the parameter without ever trusting the SPIRV-Cross-emitted
spelling and without replicating SPIRV-Cross's reserved-word list (the brittle alternative). The
parameter stays exposed under its original name in the `.mgfx`, so `effect.Parameters["noise"]
.SetValue(...)` binds. The bridge is restricted to the single-`$Globals` case (the reserved-word
case is always a free global, where a variable's effective offset is exactly its `StartOffset`); a
multi-cbuffer shape — where the rewriter's GLSL-declaration merge order is not guaranteed to match
the reflection's cbuffer order — falls through and keeps the loud `SD0012` rather than risk
mis-mapping (correctness over coverage). Only the previously-failing name-miss path is new, so the
`mgfxc`-equivalence and cross-host byte-identity of every existing shader are untouched.

### Determinism and cross-host byte-identity

Output is **deterministic**: the same source, the same compiler version, and the same target
produce **byte-identical** bytes on Windows, Linux, and macOS, pinned by `CrossHostByteIdentityTests`
for the GL, DX, and FNA outputs. Every stage above is referentially transparent in this sense, the
native compilers are pinned, the reflectors are byte-transparent (only the *source* of the
reflection changes per host, never the result), and the writers are fully managed. This is
ShadowDusk's own reproducibility guarantee. It is distinct from, and does not imply, byte-identity
to `mgfxc`, the bar against the reference compiler is behavioral and render equivalence, not equal
bytes.

---

## Notes and cross-references

- **DirectX does not use DXC.** DXC only emits SM6 DXIL, but MonoGame 3.8's DX11 runtime loads
  DXBC (SM ≤ 5), so the DX path routes HLSL → DXBC through vkd3d-shader (cross-platform default)
  with `d3dcompiler_47` as a Windows-only oracle. The DXBC bytes are also the reflection source.
- **In the browser.** The same pinned DXC and vkd3d-shader are compiled to WebAssembly and ship in
  `ShadowDusk.Wasm`, so every target compiles in-browser byte-identically to the desktop output;
  SPIRV-Cross runs as a JS-interop call there. The in-browser shader-fiddle is a *sample* of this
  reach, not the product.
- **The GLSL dialect contract** the rewriter enforces (uniform/sampler/varying naming, the
  `posFixup` and matrix conventions) is documented in full in `docs/glsl-uniform-naming.md`.
- **The FNA container format** is documented in `docs/fx2-binary-format.md`.
