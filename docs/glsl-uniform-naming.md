# GLSL Uniform Naming: MonoGame / MojoShader Convention

> **Status:** ✅ **Implemented (Phase 17, 2026-05-30).** Strategy 1 below is live as
> `MonoGameGlslRewriter` (`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`), gated to
> the PS-only OpenGL path. This document records the design as built, not a
> proposal. Resolves backlog **11-6-D**. (Originally researched in Phase 6 and
> deferred — that earlier "Phase 6/7" framing is superseded by what follows.)

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
native dependency) run over the SPIRV-Cross pixel-shader output. It is invoked from
`CompilationPipeline` only when the `monoGameGl` gate is set (PS-only OpenGL effects); other
targets keep the unmodified SPIRV-Cross dialect. The transform, by rule:

| # | SPIRV-Cross input | Rewritten to |
|---|---|---|
| 1 | `#version …` line; the `GL_ARB_shading_language_420pack` extension block | dropped; a `precision mediump` `#ifdef GL_ES` header is prepended |
| 3 | `uniform sampler2D <id>;` | `uniform sampler2D ps_s{slot};` (by declaration order); uses renamed in the body |
| 4 | `in <type> in_var_<SEM>;` | `varying vec4 <legacy>;` — `COLOR0`→`vFrontColor`, `COLOR1`→`vBackColor`, `TEXCOORD{n}`→`vTexCoord{n}`; uses get a width-truncating swizzle |
| 5 | `out vec4 out_var_SV_Target<N?>;` | declaration dropped; uses → `gl_FragColor` (or `gl_FragData[N]`) |
| 6 | `texture()/textureLod()/textureProj()` | `texture2D()/texture2DLod()/texture2DProj()` |
| 7 | `layout(std140) uniform type_Globals { … }` | `uniform vec4 ps_uniforms_vec4[N];`; member uses `_Globals.<m>` → `ps_uniforms_vec4[i]<swizzle>` |

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

Proven end-to-end in Phase 17: ShadowDusk's OpenGL `.mgfx` (this rewrite + the `MgfxWriter`
format rework) loads into a **real** `MonoGame.Framework.DesktopGL` `Effect` and renders
pixel-equivalent to the `mgfxc` goldens for all 10 SM3 PS-only shaders — including the
uniform-driven ones (TintShader, Sepia, Saturate, Scanlines, Dots) with parameters **set by
name** (`validation/Candidate` vs `validation/Baseline` + `compare.py`: 8 exact, Scanlines/Dots
maxd 1). Unit coverage: `tests/ShadowDusk.GLSL.Tests/MonoGameGlslRewriterTests.cs`.

## Known limitations (future work)

- **Pixel stage only.** For `ShaderStage.Vertex`, `Rewrite` currently passes the GLSL through
  unchanged. VS-driven MonoGame effects also need the symmetric `vs_uniforms_vec4` remap and
  the VS-side varying contract — tracked as Phase 17 §8.3 future work.
- **Matrix free-uniforms in the PS are not fully remapped.** A `mat4` member emits
  `ps_uniforms_vec4[i]/*TODO mat*/` (a column/row expansion is still owed). The PS-only
  post-process corpus uses only `float`/`vec` free uniforms, so this is unexercised today; it
  must be completed before a PS that takes a matrix uniform is supported.
