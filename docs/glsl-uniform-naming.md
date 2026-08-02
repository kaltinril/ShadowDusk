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

- Samplers named `ps_s{k}` (e.g. `ps_s0`), looked up **by uniform name**: the GL runtime calls
  `glUniform1i(GetUniformLocation("ps_s{k}"), TextureSlot)` and then binds the record's texture
  parameter to that unit. `k` is the record's position in the table, **not** an HLSL `register(sN)`
  bind slot — a shader whose only sampler is `register(s3)` still emits `ps_s0`.
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
| 3 | `uniform sampler2D <id>;` | `uniform sampler2D ps_s{slot};` — one uniform per **combined (texture, sampler) pair**, `slot` being the pair's texture's **HLSL declaration index** (fxc's allocation, honouring `register(sN)`), *not* its position in SPIRV-Cross's first-use pair list; uses renamed in the body |
| 4 | `in <type> in_var_<SEM>;` | `varying vec4 <legacy>;` — `COLOR0`→`vFrontColor`, `COLOR1`→`vBackColor`, `TEXCOORD{n}`→`vTexCoord{n}`; uses get a width-truncating swizzle. An **omitted semantic index is index 0** (`COLOR` ≡ `COLOR0`, `TEXCOORD` ≡ `TEXCOORD0`, matching fxc/mgfxc), so the bare spellings DXC passes through verbatim map to `vFrontColor` / `vTexCoord0` — without that collapse a pixel-only pass declaring `: COLOR` emitted `var_COLOR`, which MonoGame's built-in SpriteEffect VS can never link against. |
| 5 | `out vec4 out_var_SV_Target<N?>;` | declaration dropped; uses → `gl_FragColor` (or `gl_FragData[N]`) |
| 6 | `texture()` | dimension-specific legacy builtin per the sampler's declared type: `texture2D()` / `textureCube()` / `texture3D()` |
| 6b | `textureLod()` / `textureGrad()` / `textureProj()` | dimension-specific legacy names — `texture2DLod`/`textureCubeLod`/`texture3DLod`, `texture2DGrad` (2D only; cube/3D gradients fail loudly — no GLSL defines a legacy spelling), `texture2DProj`/`texture3DProj` — **plus** MojoShader's guarded extension header prepended once: `#if __VERSION__ >= 300` maps the legacy names back to the generic builtins (KNI HiDef/WebGL2, mirroring MojoShader's GLSLES3 preflight), `#elif defined(GL_ARB_shader_texture_lod)` / `#elif defined(GL_EXT_gpu_shader4)` enable the fragment-stage builtins on legacy desktop GL (Mesa accepts this; the bare generic spelling was a Linux Effect-load failure), `#else` degrades to a plain `texture2D()`-family call — never a compile failure. One artifact serves Reach, HiDef and desktop. |
| 7 | **every** `layout(binding=…, std140) uniform <Type> { … } <Inst>;` block — `type_Globals { … } _Globals` for loose globals AND `type_<Name> { … } <Name>` for each named cbuffer | ONE merged `uniform vec4 ps_uniforms_vec4[N];` covering all blocks in declaration order (MojoShader's model: D3D9 has a single float-constant register file per stage); member uses `<Inst>.<m>` → `ps_uniforms_vec4[i]<swizzle>`; array members `<Inst>.<m>[idx]` → `ps_uniforms_vec4[base + idx]<swizzle>` (element stride 1 register; `mat4` arrays stride 4, reconstructed per element); unmodelled member types (int/bool/mat3/struct/…), whole-array uses, and any surviving block-instance reference **fail loudly** (SD0210) instead of shipping GLSL that references a deleted block |
| 8 | `roundEven(x)` / `round(x)` (GLSL ES 3.00 / GL 1.30 only) | `floor((x) + 0.5)` — valid in every GLSL profile incl. WebGL1 / GLSL ES 1.00 (KNI's Reach profile), and exactly what mgfxc/MojoShader emits for HLSL `round`. Argument captured by a balanced-paren scan; on a replacement the scan resumes INSIDE the replaced text, so a call nested in another's argument — `round(round(x) * 0.5)` — is lowered too (issue #140; the replacement's `floor((` prefix can never re-match, so the scan still terminates). |
| 9a | one-shot `do { … } while(false);` that is a direct child of `main`'s body **or of a plain-block chain rooted at `main`** (SPIRV-Cross's wrapper for early returns — the entry point's own, and each inlined helper's, which nests inside the entry wrapper and lands in the plain block a previous unwrap leaves behind) | **unwrapped**: the body becomes a plain brace block, and each `break` whose nearest enclosing loop/switch is the one-shot loop becomes the statements control would run after the loop — the return-value-phi output writes, flattened through the enclosing plain blocks and through a trailing `{ … return; }` block a previous unwrap produced — plus `return;`; the fall-through path runs the in-place tail unchanged (issue #136). Unwraps iterate outside-in: entry wrapper first, then helper wrappers inside it. Straight-line `main` with conditional early returns is valid in every GLSL profile incl. ESSL 1.00, and is the exact shape mgfxc/MojoShader emits. This exists because ANGLE's D3D11 backend (WebGL in every Windows browser) silently zeroes **every gradient op** (`dFdx`/`dFdy`, and implicit-LOD mip selection) inside **any loop with a divergent exit** — a conditional `break` *or* `discard` — so the Rule-9b for-loop form, while load-safe, kills derivative-based AA there with no compile or link error. Preconditions the unwrap must prove (else it defers to 9b): not inside an if/else/loop/switch body; no loop-level `continue`; each tail parseable as simple `;`-terminated statements optionally ending in one `{ … return; }` block; **and the duplicated tail must contain no divergence-sensitive op** (`dFdx`/`dFdy`/`fwidth` or an implicit-LOD `texture*` sample — duplication would move it from convergent into divergent flow, undefined per GLSL §8.13.1; the explicit-LOD `*Lod`/`*Grad` spellings are safe). |
| 9b | any remaining one-shot `do { … } while(false);` (inside an if/else/loop/switch body, a loop-level `continue`, an unprovable tail, or a divergence-sensitive tail) | `for (int _spvonce_N = 0; _spvonce_N < 1; _spvonce_N++) { … }` — semantically identical (one iteration; `break`/`continue`/fall-through unchanged) but uses the GLSL ES 1.00 Appendix-A loop form WebGL1 / KNI Reach guarantees, so the effect loads in WebGL instead of failing on the do-while (issue #107). Genuine multi-iteration do-whiles are left untouched. **Residual:** a gradient op inside a 9b-lowered loop would still be ANGLE-poisoned; with 9a's plain-block recursion this now requires an unusual shape (e.g. a gradient-taking early-return helper called inside an `if` body, or a loop-level `continue`). The `AposShapesAa_OpenGl_NoGradientOpInsideDivergentLoop_Issue136` + `EarlyReturnHelperGradient_NoGradientInsideDivergentLoop_Issue136` pins fail if a corpus shader ever produces such a shape. |
| 10 | `pow(x, 2.0)` with a **simple** base operand (identifier / swizzle / literal, optionally signed) | `((x) * (x))` — GLSL leaves `pow` undefined for a negative base (drivers lowering to `exp2(y*log2(x))` return NaN), while fxc constant-folds `pow(x, 2)` into a multiply, so the multiply is both the well-defined form and the reference compiler's semantics, and exact where `pow` was approximate (issue #127). Complex bases are never duplicated — they keep the original `pow`. |
| 11 | `1.0 / (a / b)` where the division is provably the parenthesized group's root operator | `((b) / (a))` — one correctly-rounded division instead of two, matching fxc's folding of the reciprocal-of-quotient shape; value-equivalent across the zero/infinity edge cases (issue #127). Ambiguous shapes (top-level additive/ternary content, or a later `*`/`%` displacing the division as root) are left untouched. |
| 12 | a `for` loop whose index is advanced in the **body**, not the header — `for (int i = 0; i < N; ) { …; i++; continue; }` (constant-bounded, empty increment) | the increment is **hoisted into the header** (`for (int i = 0; i < N; i++)`) whenever the rewrite can prove it safe (no other write to the index, no other `continue` in the body) — GLSL ES 1.00 Appendix A (WebGL1 / KNI Reach) requires the increment to live in the header, so the unrewritten shape used to fail to *load* there even though it warned only (`SD0402`, issue #138, shape 2). Left as-is (still warns via `SD0402`) when the safety proof fails. |
| 13 | a header-less `for (;;) { if (i < bound) {…} else break; }` whose `bound` is traceable to a compile-time-constant expression (a literal, or a ternary between two literals) | the header is given that real, exact bound and the increment is hoisted the same way as Rule 12 — SPIRV-Cross renamed the bound into a runtime-looking temporary, but since its value is still knowable at compile time the rewrite is exact, not an approximation (issue #138, shape 1, e.g. `Apos.Shapes`' Newton-iteration SDF); the emitted header compares `<= provenMax`, so the `else` finalizer stays reachable at the max trip count (issue #160). The rewrite is deliberately narrow: an inclusive (`<=`) inner comparison is rewritten only when the bound can be extended to `provenMax + 1`, and a descending or compound step, or an index read after the loop, declines to the `SD0402` path (the rewriter source is the authority on the exact accepted shapes). A loop bounded by a genuine runtime uniform has no derivable constant and is left for `SD0402` to keep warning about. The bound must also be provably **loop-invariant**: if either branch writes it, or a statement between its declaration and the loop writes it, `provenMax` is not the real ceiling — the synthesized header would exit before the terminal `else` finalizer runs and leave its output undefined (issue #160 again) — so those shapes decline to `SD0402` too. |
| 14 | any `dFdx(` / `dFdy(` / `fwidth(` use in the rewritten fragment body | `#extension GL_OES_standard_derivatives : enable` prepended as the **first line**, before the precision header — mgfxc's exact behavior and position (`ShaderData.mojo.cs`; issue #139). In ESSL 1.00 the derivative builtins exist only under this extension, so strict GLES2 compilers reject derivative shaders without it; where derivatives are core (ES 3.00 / desktop 1.30+) the enable is at most a warning, so the one artifact still serves Reach, HiDef, and desktop. The scan includes `fwidth`, which SPIRV-Cross emits directly and mgfxc's two-token dFdx/dFdy scan never had to handle. |
| 15 | `trunc(x)` (GLSL ES 3.00 / GL 1.30 only) | `(sign((x)) * floor(abs((x))))` — truncate-toward-zero built from `sign`/`floor`/`abs`, all available since GLSL ES 1.00 / GLSL 1.10, component-wise for `float`/`vecN` alike. SPIRV-Cross emits bare `trunc()` when lowering HLSL's truncating `%`/`fmod` (`a - b * trunc(a / b)`; GLSL's own `mod()` is floored with sign-follows-divisor, so it can't stand in for fmod's sign-follows-dividend). Strict GLSL ES 1.00 front ends (ANGLE on macOS DesktopGL) reject `trunc` as an undeclared identifier where lenient desktop drivers accept it (Apos.Shapes issue #34). Argument captured by the same balanced-paren, resume-inside-replacement scan as Rule 8. The **whole product is parenthesized**: `trunc(x)` was a primary expression, so splicing a bare `a * b` over it re-associates wherever the surrounding operator binds at least as tightly — `1.0 / trunc(x)` became `(1.0 / sign(x)) * floor(abs(x))`, valid GLSL with a silently wrong value. |

`Rewrite` returns the rewritten GLSL plus the discovered sampler list (`ps_s{k}`) and the
`ps_uniforms_vec4` register count. The pipeline pairs this with the `.mgfx` side:

- The cbuffer is **named `ps_uniforms_vec4`**, with one 16-byte register per free parameter,
  register-aligned by size (SM 3.0 constant-register layout), so `Effect.Parameters[name]
  .SetValue(…)` lands in the right `vec4` slot.
- The per-shader sampler table has **one record per combined (texture, sampler) pair** — the same
  list as the sampler uniforms the emitted GLSL declares. That is the only list that can be
  correct, because the GL runtime resolves each record by its *uniform name*. Each record carries
  `Name = ps_s{slot}`, `TextureSlot = SamplerSlot = slot`, `Parameter` = the index of the pair's
  **texture**, and the baked `sampler_state` of the pair's **sampler** — so `SpriteBatch`'s
  texture reaches the sampler. Records are emitted **sorted by slot**, the way `mgfxc`'s goldens
  lay the table out.
  - **`slot` is the pair's HLSL DECLARATION index, not its position in the pair list.**
    SPIRV-Cross declares combined samplers in **first-use** order, but fxc — and therefore
    `mgfxc` — allocates GL sampler slots in **declaration** order. Numbering by list position put a
    `register(s0)` sampler on unit 1 whenever it was not also sampled first, and since
    `SpriteBatch` forces the sprite onto unit 0 *after* `EffectPass.Apply()`, that silently swapped
    textures (GitHub issue #189). The index comes from SPIR-V **module order**, not from the
    `Binding` decoration: those agree only while DXC auto-allocates, and once the source is
    annotated the binding is *register* order instead. Both the uniform names and the records come
    from `SpirvCombinedSamplerPairs.ResolveSlots`, the single definition of the rule, so the table
    and the GLSL cannot disagree.
  - **An explicit `register(sN)` overrides that index — but only on the LEGACY `sampler` form.**
    `mgfxc` honours the annotation there (at `ps_3_0` the legacy sampler *is* the combined sampler)
    and **ignores it on the modern spelling**: for `Texture2D T : register(t3); SamplerState S :
    register(s2);` its OpenGL build still allocates by texture declaration order. `FxPreParser`
    records the legacy index before its SM4 rewrite drops the clause; the modern one is deliberately
    not recorded, because honouring it would be a divergence.
  - **Per pair, not per sampler.** Two textures read through one shared `SamplerState` (the
    diffuse+lightmap idiom) produce **two** records; two samplers over one texture (the
    linear+point idiom) also produce two, each with its own state. Keying on the reflected
    samplers instead was the Phase 51 A7 bug: it rejected the first shape outright and, because
    SPIRV-Cross declares combined samplers in first-use rather than declaration order, silently
    **swapped** the texture parameter and sampler-type byte in the second. The linear+point shape
    is the one case where two pairs share a texture and so share a declaration index; `ResolveSlots`
    falls back to positional numbering there rather than colliding two uniforms onto one unit.
  - The pair list is derived in pure managed code from the SPIR-V (`SpirvCombinedSamplerPairs`)
    rather than via SPIRV-Cross's `spvc_compiler_get_combined_image_samplers`, which the browser
    host's `spirv-cross.wasm` does not export — a native call would fix desktop only and break
    the guarantee that the CLI and the browser emit identical bytes.
  - The count is cross-checked against the sampler uniforms actually declared in the emitted
    GLSL; a mismatch fails loudly with **`SD0217`** rather than shipping a mis-bound table.
    (`SD0215`/`SD0216`, which encoded the old sampler-keyed model, are retired and their numbers
    are do-not-reuse.)

### Rejected alternatives

- **Patch the MonoGame runtime** (look up by HLSL name) — breaks drop-in compatibility with
  stock `mgfxc`-compiled `.mgfx`; not viable.
- **Ship a UBO + binding points** — requires MonoGame runtime changes; same problem.

## Verification

ShadowDusk's OpenGL `.mgfx` (this rewrite + the `MgfxWriter` format rework) loads into a
**real** `MonoGame.Framework.DesktopGL` `Effect` and renders pixel-equivalent to the `mgfxc`
goldens, including the uniform-driven shaders with parameters **set by name**
(`validation/Candidate`; unit coverage in `MonoGameGlslRewriterTests`).

## Vertex stage

`Rewrite` is **stage-symmetric**: for `ShaderStage.Vertex` it emits the VS-side
MojoShader dialect that MonoGame's GL runtime links against. The shared passes run here
too — version/420pack strip, matrix expansion, and (since issue #137) the stage-agnostic
body lowerings **Rule 8** (round), **Rule 9b** (one-shot do-while → for), **Rule 10**
(pow-square), **Rule 11** (reciprocal fold), (since issue #138) **Rule 12** /
**Rule 13** (loop-hoist fixes), and (since Apos.Shapes issue #34) **Rule 15**
(`trunc()` → `sign(x)*floor(abs(x))`), applied before `posFixup` injection.
**Rule 9a is deliberately pixel-only**: it turns loop breaks into early `return;`
statements, and an early return in a VS would skip the posFixup tail appended at
end-of-main (the Y-flip / half-pixel contract); the 9b for-loop form has a single
fall-through exit, so the fixup always runs — and no derivative ops exist in the vertex
stage, so skipping 9a loses no issue-#136 coverage. Rules 5/6/6b/14 (fragment outputs,
texture builtins, the derivatives header) stay pixel-stage. The register prefix and the
in/out direction are the other stage knobs:

| SPIRV-Cross VS input | Rewritten to |
|---|---|
| `layout(std140) uniform type_Globals { … }` | `uniform vec4 vs_uniforms_vec4[N];` (a `mat4` counts as four registers) |
| `in <type> in_var_<SEM>;` (vertex **inputs**) | `attribute vec4 vs_v{k};` — renamed in declaration order; uses get a width-truncating swizzle (`vec4(vs_v0.xyz, 1.0)`) |
| `out <type> out_var_<SEM>;` (vertex **outputs**) | `varying vec4 <legacy>;` — the SAME names the PS reads (`vFrontColor`/`vTexCoord{n}`), so MonoGame links VS→PS **by name**; a narrower output writes a swizzled LHS (`vTexCoord0.xy = vs_v2.xy;`). **Exception — `out_var_POSITION{0}` → `gl_Position`** (issue #70): a VS output carrying the legacy D3D9 `POSITION`/`POSITION0` semantic **is** the clip position (the stock MonoGame GL template emits this via `#define SV_POSITION POSITION`). DXC (Shader Model 6) treats `: POSITION` as an ordinary user output (only `: SV_Position` is the builtin position), so without this remap the transform would land in a dead `var_POSITION` varying and `gl_Position` would be left **unwritten** — silently-broken geometry. The rewriter (`IsPositionSemantic`) drops its varying decl and rewrites its uses to `gl_Position`, so `posFixup` then applies as for any SV_Position shader. mgfxc maps `: POSITION` to the position natively (D3D9 SM3). |
| `gl_Position = … ;` (from `SV_Position`) | kept, then the mgfxc/MojoShader **`posFixup` contract** is injected: `uniform vec4 posFixup;` (declared after `vs_uniforms_vec4[]`, the golden's order) and the two fixup lines `gl_Position.y = gl_Position.y * posFixup.y;` + `gl_Position.xy += posFixup.zw * gl_Position.ww;` immediately before SPIRV-Cross's kept depth-convention line. MonoGame's GL runtime sets the uniform per draw (`+1` backbuffer / `-1` render target, half-pixel offset in `.zw` when `UseHalfPixelOffset`) and skips programs without it. SPIRV-Cross's `FlipVertexY` is **off** — the old baked `-gl_Position.y` only matched the render-target case and rendered backbuffer draws (the normal game case) vertically inverted. `FixupDepthConvention` stays on. |

The VS rewrite also returns the **vertex-attribute table** (each `vs_v{k}` →
`VertexElementUsage`+semantic-index: POSITION→0, COLOR→1, TEXCOORD→2, NORMAL→3) which the
pipeline writes into the `.mgfx` shader record so MonoGame binds each attribute to the right
vertex element. The `.mgfx` cbuffer for a VS-bound buffer is named **`vs_uniforms_vec4`**
(PS-bound stays `ps_uniforms_vec4`); attribution is from reflection, not a PS-only assumption.

## The cbuffer record model

The `.mgfx` constant-buffer records are built **per shader, from the rewriter's own
register layout** (`MonoGameGlslResult.Uniforms`), never by cross-stage reflection-name
dedup — so the record's offsets and the GLSL's `[i]` indices come from one allocation
and cannot diverge. mgfxc's model, pinned by its goldens:

- **A cbuffer bound by both stages → a record per stage** (`vs_uniforms_vec4` AND
  `ps_uniforms_vec4`), each stage's shader binding its record **by index**. Several
  records may share a name (the SkinnedEffect golden has three `vs_uniforms_vec4`).
  The old cross-stage dedup named the single record `ps_uniforms_vec4` while the VS
  GLSL read `vs_uniforms_vec4[]` — MonoGame never uploaded the VS array and VS
  uniforms silently read zero.
- **Multiple same-stage cbuffers → one merged record** in declaration order;
  identical records dedupe across shaders mgfxc-style (`ConstantBufferData.SameAs`).
- **Array members** occupy element-stride × count registers and the parameter carries
  N recursive **element sub-parameter records** (empty name/semantic, parent shape,
  zero-data leaf) — on every target, exactly MonoGame 3.8.2 `Effect.ReadParameters`'
  recursive wire format (elements first, then struct members), so
  `Parameters["X"].SetValue(array)` and `.Elements[i]` work beyond element 0.
- **Pinned divergence:** mgfxc's per-stage records contain only the constants fxc
  kept for that stage; ShadowDusk's carry the cbuffer's full declared layout per
  stage. Both are self-consistent with their own GLSL; parameters are set by name —
  render-proven equivalent (`validation/CbufferModel`).
- **Synthesized backing (issue #187):** the layout→parameter join runs in BOTH
  directions. A Scalar/Vector/Matrix parameter that reflection reports for a stage
  but the rewriter's layout missed — DXC's `-spirv` backend can fold a uniform's
  only reads and drop its then-unused cbuffer from the SPIR-V entirely, while the
  DXIL reflection source (like fxc) keeps it — gets a register slot appended after
  the live ones, cbuffer membership at that offset, and a covering
  `uniform vec4 {vs,ps}_uniforms_vec4[N];` declaration patched into the GLSL
  (`CompilationPipeline.PatchGlUniformArrayDeclaration`, inserted past the full
  leading prologue — `#extension` lines and balanced `#if…#endif` header blocks
  such as the `#ifdef GL_ES` precision block and the TexLod
  `#if __VERSION__ >= 300 … #endif` header, in any order). Footprint follows the RUNTIME'S write
  model: a Matrix is sized by **Columns** (MonoGame/KNI upload matrices transposed,
  writing `ColumnCount` 16-byte rows — `float2x4` ⇒ 4 registers; sizing by Rows
  under-allocates and crashes the first `EffectPass.Apply`), scalars/vectors one
  register, arrays once per element. Without this the
  parameter existed with **no** cbuffer record — `SetValue` wrote nowhere. The
  synthesized shape is exactly what the mgfxc golden carries for the one known
  member (`GradientToy.fx`/`iResolution`) and what real mgfxc's DirectX profile
  ships for any declared-but-unused uniform; the GL driver link-strips the unread
  array (a spec-sanctioned silent skip) or uploads data nothing reads — render-
  identical either way. Guarded by `GlPhantomParameterTests` (structural backing
  criterion + corpus-wide sweep); full record:
  `plan/DONE/ISSUE-187-gl-phantom-parameter-compile-fidelity.md`.
- **mgfxc bug not replicated:** an array read at only SOME static indices is broken
  in mgfxc+MonoGame GL itself — fxc references only the used registers, MojoShader
  emits a **compacted** uniform array, but mgfxc's record keeps the full layout, so
  MonoGame's full-buffer `glUniform4fv` lands element 0's data where the shader
  reads element 1 (verified: that golden renders garbage in real MonoGame 3.8.2).
  ShadowDusk always emits the full declared layout, rendering the source semantics
  correctly.

**Matrix free-uniforms.** A `mat4` member expands to the four consecutive registers it
occupies — `_Globals.M` → a `mat4` reconstructed **transposed** (the registers are taken as the
matrix's ROWS), open-coded with swizzles by `BuildUploadedMat4`:
`mat4(vec4(P[r].x, P[r+1].x, P[r+2].x, P[r+3].x), vec4(P[r].y, …), vec4(P[r].z, …), vec4(P[r].w, …))`.
**Why transposed (issue #70).** MonoGame/KNI's `EffectParameter.SetValue(Matrix)` uploads
register `k` = column `k` of the authored matrix — the layout mgfxc's golden reads with
`result[j] = dot(v, register[j])` for HLSL `mul(v, M)` (i.e. `v * mat4(reg0..reg3)`).
SPIRV-Cross, however, lowers `mul(v, M)` to GLSL `M * v` (operands swapped, since the
row/column-major decoration it carries — which this rewrite strips when it flattens the UBO
into the flat register array — is what would otherwise keep the result upright). A naive
`mat4(reg0..reg3)` (registers as columns) would therefore compute `M·v`, the **transpose** of
the intended `v·M`, rendering geometry garbled (issue #70's "exploded cube"). Reconstructing the
transpose cancels the operand swap — `Mᵀ * v == v * M == dot(register[i], v)`, the golden's
per-row dot — and is correct for every mul order. `transpose()` is **not** used (it is absent in
GLSL ES 1.00 / Reach / WebGL1 and versionless desktop GLSL 1.10). The register index is the
running register total so a `mat4` correctly shifts every member after it, agreeing exactly with
the `.mgfx` cbuffer packing (`BuildConstantBufferInfoList`). Unit-pinned in
`MonoGameGlslRewriterTests` and render-pinned by `Issue70MatrixTransposeRenderTests`
(non-identity asymmetric matrix vs the mgfxc golden). Applies to both stages.

**Verification:** the VS-driven fixture `VsTransformColorTexture.fx` (custom VS +
`float4x4` transform + POSITION/COLOR0/TEXCOORD0 + textured/tinted PS) compiled by ShadowDusk
loads in a **real** `MonoGame.Framework.DesktopGL` `Effect` and renders pixel-identical to the
mgfxc OpenGL golden via a custom vertex-buffer draw path (`validation/VsDriven`) — in **both**
the `RenderTarget2D` mode and the **backbuffer** mode (the case the static Y-flip got wrong).
The same `.fx` for DirectX renders pixel-identical to the mgfxc DX golden in real
`MonoGame.Framework.WindowsDX` via both DXBC backends (`validation/VsDrivenDx`).

## Known limitations (future work)

- **Vertex semantics beyond POSITION/COLOR/TEXCOORD/NORMAL.** The attribute-table map covers the
  SpriteBatch-compatible set; an unmodelled semantic (e.g. `BLENDWEIGHT`) fails loudly at compile
  time (`MonoGameGlslRewriteException`) rather than binding to the wrong vertex element. Extend
  `SemanticToVertexUsage` to add more.
- **Geometry / hull / domain / compute stages** are out of scope (MonoGame 3.8 GL Reach doesn't
  support them).
