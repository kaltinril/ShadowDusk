# GLSL Uniform Naming: MonoGame / MojoShader Convention

> ShadowDusk's `MonoGameGlslRewriter` (`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`)
> implements the convention described here. This document records the GLSL
> uniform-naming and dialect contract it enforces.

This rewrite is the **managed MojoShader-dialect** step of the OpenGL branch in the
overall compilation pipeline; see `docs/references/compilation-pipeline.md` for where it
sits (HLSL →[DXC]→ SPIR-V →[SPIRV-Cross]→ GLSL → **this rewrite** → `.mgfx`).

## Background

When MonoGame's OpenGL backend loads a compiled `.mgfx` effect, it calls `glGetUniformLocation`
on the GLSL shader source embedded in the file. The name string passed to `glGetUniformLocation`
must exactly match the uniform declarations in the GLSL source.

## MonoGame's Expected Convention

MonoGame's OpenGL path uses the MojoShader uniform naming convention. The runtime binds free
(non-resource) uniforms as a single `vec4[N]` array **named after the constant buffer** —
`ConstantBuffer.PlatformApply` calls `GetUniformLocation(cbufferName)` and uploads with
`glUniform4fv`. `mgfxc` names that cbuffer `ps_uniforms_vec4` / `vs_uniforms_vec4`:

```glsl
uniform vec4 vs_uniforms_vec4[N];   // vertex constant buffer
uniform vec4 ps_uniforms_vec4[N];   // pixel constant buffer
```

MonoGame looks up uniforms by these fixed array names, **not** by the original HLSL variable
names (`WorldViewProj`, `DiffuseColor`, …). It also expects:

- Samplers named `ps_s{slot}` (e.g. `ps_s0`), looked up by slot.
- Stage I/O carried over legacy `varying` names that match the built-in `SpriteEffect` VS
  outputs (MonoGame links the VS to the custom PS **by varying name**): `vFrontColor`
  (`COLOR0`), `vBackColor` (`COLOR1`), `vTexCoord{n}` (`TEXCOORD{n}`).
- Pixel output via `gl_FragColor` (or `gl_FragData[n]`), legacy `texture2D()` sampling, and
  **no** `#version` directive (MojoShader GLSL is GLSL 110-era).

## What SPIRV-Cross Emits by Default

SPIRV-Cross emits modern GLSL that is **incompatible** with the above: a `#version 140`
directive, `in`/`out` stage variables (`in_var_TEXCOORD0`, `out_var_SV_Target`), `texture()`
sampling, an opaque sampler name (`_39`), and free uniforms packed into a **`std140`
`type_Globals` UBO block**. Loaded as-is, `GetUniformLocation("type_Globals")` returns `-1`,
`ConstantBuffer.PlatformApply` early-returns, and every parameter reads zero (e.g. a tint
shader renders black) even though the GLSL itself compiles cleanly.

So a byte-correct `.mgfx` container is **necessary but not sufficient** — the embedded GLSL
must also be in MonoGame's dialect or the custom PS will not link with the built-in VS.

## Implemented Design — Strategy 1 (GLSL post-process)

`MonoGameGlslRewriter.Rewrite(glsl, stage)` is a **pure string transform** (no SPIRV-Cross /
native dependency) run over the SPIRV-Cross output. It is invoked from
`CompilationPipeline` whenever the `monoGameGl` gate is set (**any** OpenGL effect — see
*Vertex stage* below for the symmetric VS rules);
other targets keep the unmodified SPIRV-Cross dialect. The pixel-stage transform, by rule:

| # | SPIRV-Cross input | Rewritten to |
|---|---|---|
| 1 | `#version …` line; the `GL_ARB_shading_language_420pack` extension block | dropped; a `precision mediump` `#ifdef GL_ES` header is prepended |
| 3 | `uniform sampler2D <id>;` | `uniform sampler2D ps_s{slot};` (by declaration order); uses renamed in the body |
| 4 | `in <type> in_var_<SEM>;` | `varying vec4 <legacy>;` — `COLOR0`→`vFrontColor`, `COLOR1`→`vBackColor`, `TEXCOORD{n}`→`vTexCoord{n}`; uses get a width-truncating swizzle |
| 5 | `out vec4 out_var_SV_Target<N?>;` | declaration dropped; uses → `gl_FragColor` (or `gl_FragData[N]`) |
| 6 | `texture()/textureLod()/textureProj()` | `texture2D()/texture2DLod()/texture2DProj()` |
| 7 | `layout(std140) uniform type_Globals { … }` | `uniform vec4 ps_uniforms_vec4[N];`; member uses `_Globals.<m>` → `ps_uniforms_vec4[i]<swizzle>` |
| 8 | `roundEven(x)` / `round(x)` (GLSL ES 3.00 / GL 1.30 only) | `floor((x) + 0.5)` — valid in every GLSL profile incl. WebGL1 / GLSL ES 1.00 (KNI's Reach profile), and exactly what mgfxc/MojoShader emits for HLSL `round`. Argument captured by a balanced-paren scan so nested calls lower correctly. |

`Rewrite` returns the rewritten GLSL plus the discovered sampler list (`ps_s{slot}`) and the
`ps_uniforms_vec4` register count. The pipeline pairs this with the `.mgfx` side:

- The cbuffer is **named `ps_uniforms_vec4`**, with one 16-byte register per free parameter,
  register-aligned by size (SM 3.0 constant-register layout), so `Effect.Parameters[name]
  .SetValue(…)` lands in the right `vec4` slot.
- The per-shader sampler table binds slot → `ps_s{slot}` with the texture parameter index, so
  `SpriteBatch`'s texture reaches the sampler.

### Rejected alternatives

- **Patch the MonoGame runtime** (look up by HLSL name) — breaks drop-in compatibility with
  stock `mgfxc`-compiled `.mgfx`; not viable.
- **Ship a UBO + binding points** — requires MonoGame runtime changes; same problem.

## Verification

ShadowDusk's OpenGL `.mgfx` (this rewrite + the `MgfxWriter`
format rework) loads into a **real** `MonoGame.Framework.DesktopGL` `Effect` and renders
pixel-equivalent to the `mgfxc` goldens for all 10 SM3 PS-only shaders — including the
uniform-driven ones (TintShader, Sepia, Saturate, Scanlines, Dots) with parameters **set by
name** (`validation/Candidate` vs `validation/Baseline` + `compare.py`: 8 exact, Scanlines/Dots
maxd 1). Unit coverage: `tests/ShadowDusk.GLSL.Tests/MonoGameGlslRewriterTests.cs`.

## Vertex stage

`Rewrite` is **stage-symmetric**: for `ShaderStage.Vertex` it emits the VS-side
MojoShader dialect that MonoGame's GL runtime links against. Same shared passes
(version/420pack strip, matrix expansion, round lowering); the register prefix and the
in/out direction are the only stage knobs:

| SPIRV-Cross VS input | Rewritten to |
|---|---|
| `layout(std140) uniform type_Globals { … }` | `uniform vec4 vs_uniforms_vec4[N];` (a `mat4` counts as four registers) |
| `in <type> in_var_<SEM>;` (vertex **inputs**) | `attribute vec4 vs_v{k};` — renamed in declaration order; uses get a width-truncating swizzle (`vec4(vs_v0.xyz, 1.0)`) |
| `out <type> out_var_<SEM>;` (vertex **outputs**) | `varying vec4 <legacy>;` — the SAME names the PS reads (`vFrontColor`/`vTexCoord{n}`), so MonoGame links VS→PS **by name**; a narrower output writes a swizzled LHS (`vTexCoord0.xy = vs_v2.xy;`) |
| `gl_Position = … ;` (from `SV_Position`) | kept as-is; SPIRV-Cross's `FlipVertexY` + `FixupDepthConvention` already bake the Y-flip and DX→GL depth range (mgfxc uses a `posFixup` uniform for the same effect — both produce the same `gl_Position`) |

The VS rewrite also returns the **vertex-attribute table** (each `vs_v{k}` →
`VertexElementUsage`+semantic-index: POSITION→0, COLOR→1, TEXCOORD→2, NORMAL→3) which the
pipeline writes into the `.mgfx` shader record so MonoGame binds each attribute to the right
vertex element. The `.mgfx` cbuffer for a VS-bound buffer is named **`vs_uniforms_vec4`**
(PS-bound stays `ps_uniforms_vec4`); attribution is from reflection, not a PS-only assumption.

**Matrix free-uniforms.** A `mat4` member expands to the four consecutive
registers it occupies — `_Globals.M` → `mat4(<prefix>_uniforms_vec4[r], [r+1], [r+2], [r+3])`
(column-major: std140 stores each matrix column at a 16-byte register, and GLSL
`mat4(c0,c1,c2,c3)` takes columns, so the reconstruction is byte-faithful to the original
SPIRV-Cross `mat4`). The register index is the running register total so a `mat4` correctly
shifts every member after it, agreeing exactly with the `.mgfx` cbuffer packing
(`BuildConstantBufferInfoList`). Unit-pinned in `MonoGameGlslRewriterTests`
(`Matrix_ExpandsToFourConsecutiveRegisters_IndicesMatchCbufferLayout`,
`PixelStage_Mat4Uniform_ExpandsToFourRegisters_NoTodoLeft`). Applies to both stages.

**Verification:** the VS-driven fixture `VsTransformColorTexture.fx` (custom VS +
`float4x4` transform + POSITION/COLOR0/TEXCOORD0 + textured/tinted PS) compiled by ShadowDusk
loads in a **real** `MonoGame.Framework.DesktopGL` `Effect` and renders **pixel-identical**
(max delta 0) to the mgfxc OpenGL golden, via a custom vertex-buffer draw path
(`validation/VsDriven`). The same `.fx` for DirectX loads in real `MonoGame.Framework.WindowsDX`
and renders pixel-identical to the mgfxc DX golden via **both** the d3dcompiler oracle and the
cross-platform vkd3d backend (`validation/VsDrivenDx`). The PS-only corpus and
image-regression anchors remain unregressed (10/10).

## Known limitations (future work)

- **Vertex semantics beyond POSITION/COLOR/TEXCOORD/NORMAL.** The attribute-table map covers the
  SpriteBatch-compatible set; an unmodelled semantic (e.g. `BLENDWEIGHT`) fails loudly at compile
  time (`MonoGameGlslRewriteException`) rather than binding to the wrong vertex element. Extend
  `SemanticToVertexUsage` to add more.
- **Geometry / hull / domain / compute stages** are out of scope (MonoGame 3.8 GL Reach doesn't
  support them).
