# Phase 28 — VS-Driven MonoGame Effects (vertex-shader support)

**Status:** Planned (written 2026-06-03). **Track:** Fidelity / completeness.

Today ShadowDusk faithfully compiles and renders the **PS-only** corpus on the MonoGame GL
path (10/10 in real DesktopGL — [Phase 17](DONE/PHASE-17-monogame-runtime-validation.md))
and DX path (10/10 in real WindowsDX — [Phase 18](DONE/PHASE-18-directx-dxbc.md)). Every
one of those effects reuses MonoGame's **built-in `SpriteEffect` vertex shader** and only
supplies a pixel shader. The common real-world case — an effect that ships **its own vertex
shader** (almost always taking a `float4x4` transform) — is **not yet supported on the GL
path**: `CompilationPipeline` gates the MonoGame-GL MojoShader rewrite to PS-only effects, so
a vertex-bearing pass falls through to the unmodified SPIRV-Cross dialect, which MonoGame's GL
runtime cannot link. This phase closes that gap and confirms parity on DX.

**Depends on:**
- **[Phase 17](DONE/PHASE-17-monogame-runtime-validation.md)** — proved in-engine equivalence for the PS-only SM3 corpus and built the GL `MonoGameGlslRewriter` + `validation/` harness this phase extends. The VS work is explicitly carried forward from Phase 17 §8.3.
- **[Phase 18](DONE/PHASE-18-directx-dxbc.md)** — the DX DXBC backend (`vkd3d` / `d3dcompiler_47` oracle) the DX confirmation leg rides on.

**Blocks:** nothing structurally, but it is a prerequisite for any sample/game whose effect
has a custom VS (e.g. a transform + lit/skinned vertex path), and a prerequisite for the
"VS-driven" backlog item to ever leave [Phase 100](PHASE-100-deferred-backlog.md).

> The product is the in-memory `IShaderCompiler` library (see `CLAUDE.md` → THE PURPOSE). A
> compiler that can only handle PS-only post-process effects is materially incomplete; a custom
> VS is the ordinary case, not an edge case. One faithful pipeline — **no** substitute compiler.

---

## Overview

Verified current behavior (the gap):

- **`src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs:112-113`** computes
  `bool monoGameGl = options.Target == PlatformTarget.OpenGL && fxParsed.Techniques.All(t => t.Passes.All(p => p.VertexEntryPoint is null))`.
  Only when **every pass is pixel-only** does the MojoShader rewrite + `ps_uniforms_vec4`
  layout apply. A VS-bearing pass passes `applyMonoGameGlsl: false` (line 194, with the comment
  *"VS-bearing pass is never MonoGame-rewritten"*).
- **`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs:92-96`** short-circuits the vertex stage:
  `if (stage == ShaderStage.Vertex) return new MonoGameGlslResult(glsl, Array.Empty<…>(), 0);`
  — it returns the raw SPIRV-Cross GLSL untouched.
- A PS `mat4` free-uniform is **not** expanded: line 232 emits
  `ps_uniforms_vec4[{idx}]/*TODO mat*/` instead of the four-register column/row form. A custom
  VS almost always takes a `float4x4` transform, so this is a hard prerequisite, not a polish item.
- `docs/glsl-uniform-naming.md` → *Known limitations* (lines 94-102) records both gaps and
  points at Phase 17 §8.3.

The rewriter and pipeline already produce the **PS** side of the contract (`ps_uniforms_vec4[]`,
`ps_s{slot}` samplers, legacy `varying` reads, `gl_FragColor`); MonoGame's GL runtime expects the
symmetric **VS** side, and `docs/glsl-uniform-naming.md:27` already names that block
`vs_uniforms_vec4[N]`. The work is to make the rewrite + cbuffer emission symmetric for VS,
emit the VS-side stage I/O contract, finish the matrix expansion, and prove it in-engine.

---

## Scope & Non-Goals

**In scope:**
- VS-stage MojoShader rewrite (`vs_uniforms_vec4[N]`, attribute/varying decls, `gl_Position`).
- Symmetric `vs_uniforms_vec4` cbuffer naming/emission in `CompilationPipeline`.
- Completing the PS (and VS) `mat4` free-uniform expansion (resolve the `/*TODO mat*/`).
- Lifting the `monoGameGl` PS-only gate to also cover VS-bearing passes (without regressing PS-only or the Phase-16 anchors).
- A VS-driven `.fx` fixture and `validation/` harness extension proving real-DesktopGL parity vs an `mgfxc` golden; DX confirmation of the same shader.

**Out of scope / Non-Goals:**
- Geometry / hull / domain / compute stages (DX/GL not supported by the corpus or MonoGame 3.8 GL Reach).
- New vertex semantics beyond what a real effect needs first: `POSITION` + `COLOR0` + `TEXCOORD0` (the SpriteBatch-compatible attribute set). Additional attributes are follow-ons.
- Linux/macOS *run* validation of this path (→ [Phase 30](PHASE-30-ci-and-nuget-release.md) CI) — author/verify on Windows DesktopGL first.
- Metal/Vulkan VS support (`ShadowDusk.Metal` is still a stub).

---

## Architecture & key decisions

- **Make `MonoGameGlslRewriter.Rewrite` symmetric per stage.** Replace the `ShaderStage.Vertex`
  early-return (`MonoGameGlslRewriter.cs:92-96`) with a vertex rewrite that mirrors the pixel one
  but emits the VS dialect: the `type_Globals` UBO → `uniform vec4 vs_uniforms_vec4[N];`, member
  uses → `vs_uniforms_vec4[i]<swizzle>`, SPIRV-Cross `in_var_<SEM>` vertex *inputs* → legacy
  `attribute` declarations, `out_var_<SEM>` vertex *outputs* → legacy `varying` declarations
  matching the names the PS reads (`vFrontColor`/`vBackColor`/`vTexCoord{n}` — see
  `SemanticToVaryingName`, `MonoGameGlslRewriter.cs:337-359`), and `gl_Position` written from the
  SV_Position output. The register-prefix (`ps_`/`vs_`) and the in/out direction become the only
  stage-dependent knobs; keep the shared rewrite passes (version strip, 420pack strip, texture
  fns, round lowering) common.
- **Resolve the `mat4` expansion** in the uniform-member rewrite (`MonoGameGlslRewriter.cs:225-242`):
  a `mat4` member occupies four consecutive registers, so `_Globals.<m>` must expand to
  `mat4(<reg>i, <reg>i+1, <reg>i+2, <reg>i+3)` (column-major, matching `glUniform4fv` upload and
  mgfxc's MojoShader layout). The register index already accounts for matrices on the `.mgfx`
  side — `BuildConstantBufferInfoList` (`CompilationPipeline.cs:543-555`) packs a `float4x4` as
  four 16-byte registers — so the GLSL indexing must agree exactly. Add an explicit unit test that
  the emitted indices match the reflected register offsets.
- **Symmetric cbuffer naming.** `BuildConstantBufferInfoList` (`CompilationPipeline.cs:583`)
  currently emits `cbName = gl ? "ps_uniforms_vec4" : …`. The VS path needs the same record named
  `vs_uniforms_vec4` for VS-bound cbuffers (MonoGame's GL runtime keys `glUniform4fv` on this
  name). Decide cbuffer→stage attribution from reflection (which stage binds the cbuffer) rather
  than from the PS-only assumption.
- **Lift the gate carefully.** The `monoGameGl` flag (`CompilationPipeline.cs:112-113`) must
  extend to VS-bearing passes **only when** the VS rewrite is in place, and **must not** regress
  (a) the PS-only corpus (Phase 17) or (b) the Phase-16 cross-validation anchors that rely on the
  unmodified SPIRV-Cross dialect for VS-bearing passes. Prefer per-stage `applyMonoGameGlsl` driven
  by `options.Target == OpenGL` rather than a single all-or-nothing technique-level flag.
- **DX confirmation is mostly free.** The DX path keeps HLSL packing (`gl == false`) and does not
  use the GL rewriter, so a VS-bearing DX effect should already route through `Vkd3dShaderCompiler`
  / `D3DCompilerShaderCompiler` (`CompilationPipeline.cs:138-142`). This phase *confirms* a
  VS-driven DX effect loads + renders, it does not add a DX code path.
- **Validation harness reuse.** `validation/Shared/EffectImageRenderer.cs` +
  `validation/Shared/ShaderInputs.cs` currently drive the PS-only corpus through SpriteBatch
  (which supplies its own VS). A VS-driven shader can't use SpriteBatch's VS — add a custom
  vertex-buffer draw path (quad with `POSITION`/`COLOR0`/`TEXCOORD0`) so the candidate `.mgfx`
  VS actually runs, and a `mgfxc`-compiled golden for the same `.fx` as the baseline.

---

## Tasks

- [ ] Add a `vs_uniforms_vec4` (and matrix) **VS rewrite** to `MonoGameGlslRewriter.Rewrite`, replacing the `ShaderStage.Vertex` passthrough; factor the stage-independent passes so PS and VS share them.
- [ ] Emit legacy VS-side stage I/O: `attribute` decls for `POSITION`/`COLOR0`/`TEXCOORD0` inputs, `varying` decls for outputs (matching the PS's `vFrontColor`/`vTexCoord{n}` reads), and write `gl_Position`.
- [ ] Resolve the PS `mat4` `/*TODO mat*/` (`MonoGameGlslRewriter.cs:232`) into a 4-register `mat4(...)` expansion; apply the same to VS matrix uniforms.
- [ ] Add the GL vertex-attribute table for `POSITION`/`COLOR0`/`TEXCOORD0` so MonoGame's GL runtime binds vertex attributes to the right locations.
- [ ] Name/emit the `vs_uniforms_vec4` cbuffer in `BuildConstantBufferInfoList` for VS-bound cbuffers; attribute cbuffers to stages from reflection.
- [ ] Lift the `monoGameGl` PS-only gate (`CompilationPipeline.cs:112-113`) to cover VS-bearing GL passes; pass per-stage `applyMonoGameGlsl` (drop the hardcoded `false` at line 194) without regressing PS-only or the Phase-16 anchors.
- [ ] Add a VS-driven `.fx` fixture (custom VS taking a `float4x4` transform + a texture/color path) under `tests/fixtures/shaders/`, with an `mgfxc` golden.
- [ ] Extend `validation/` (Shared renderer + ShaderInputs) with a custom vertex-buffer draw path so the candidate VS runs; add the new shader to the baseline/candidate comparison.
- [ ] Update `docs/glsl-uniform-naming.md` *Known limitations* to as-built (VS supported; matrix expansion done).

---

## Acceptance Criteria

- [ ] A VS-driven `.fx` (its own vertex shader + `float4x4` transform uniform) compiled by ShadowDusk for OpenGL **loads in a real MonoGame DesktopGL `Effect`** and renders **pixel-equivalent** to the `mgfxc`-compiled golden of the same `.fx` (same-backend, GL↔GL — rung 4 of the evidence ladder).
- [ ] The PS `mat4` free-uniform `/*TODO mat*/` is resolved; a PS-or-VS matrix uniform expands to the correct 4-register `mat4(...)` and the emitted register indices match the reflected cbuffer layout (unit-tested).
- [ ] The same VS-driven `.fx` compiled for DirectX loads in real WindowsDX and renders pixel-equivalent to its `mgfxc` DX golden (DX↔DX confirmation).
- [ ] **No regression:** the PS-only corpus still renders 10/10 in DesktopGL (Phase 17) and the Phase-16 cross-validation anchors still pass.

## Definition of Done

ShadowDusk's faithful GL pipeline correctly compiles effects with their **own vertex shader**:
the `MonoGameGlslRewriter` emits the symmetric `vs_uniforms_vec4` block, the VS-side
attribute/varying I/O contract, and a complete matrix-uniform expansion; the `monoGameGl` gate no
longer excludes VS-bearing passes; a VS-driven `.fx` renders pixel-equivalent to `mgfxc` in **both**
real DesktopGL and real WindowsDX; and the PS-only corpus + Phase-16 anchors are unregressed. The
"VS-driven MonoGame effects" item is removed from [Phase 100](PHASE-100-deferred-backlog.md) and
`docs/glsl-uniform-naming.md` is updated to as-built.

---

## Open questions / risks

- **Varying name/contract drift.** MonoGame's GL runtime links VS→PS **by varying name**; the VS
  outputs must exactly match the names the PS already reads (`vFrontColor`/`vTexCoord{n}`). A custom
  VS may emit semantics the current `SemanticToVaryingName` map (`MonoGameGlslRewriter.cs:337-359`)
  doesn't cover — the map likely needs extension, and a mismatch fails to link (or links silently
  wrong). Validate by name in the harness, not by index.
- **Matrix register layout must match the writer.** The GLSL `mat4` expansion and the `.mgfx`
  cbuffer register packing (`BuildConstantBufferInfoList`) must agree on column-vs-row major and
  the exact register offsets, or the transform is transposed/garbled at runtime — a classic silent
  fidelity bug. Pin with a unit test and an in-engine render check, not just structural assertions.
- **Gate-lifting regression surface.** The Phase-16 anchors deliberately depend on the *unmodified*
  SPIRV-Cross dialect for VS-bearing passes; the safe move is per-stage `applyMonoGameGlsl` so the
  new VS rewrite only activates on the MonoGame GL target. Re-run the full anchor + image suite.
- **Harness needs a real VS draw path.** SpriteBatch supplies its own VS, so it cannot exercise a
  custom-VS `.mgfx`; the validation harness must draw a vertex buffer through the candidate effect,
  which is new harness code (and the only way to reach evidence-ladder rung 4 for VS).
- **Vertex Y-flip / DX layout flags.** The OpenGL VS path uses `-fvk-use-dx-position-w` /
  `-fvk-use-dx-layout` (and the Phase-6 Y-flip discussion); confirm the emitted `gl_Position`
  handedness matches what `mgfxc`'s VS produces so geometry isn't vertically mirrored.
