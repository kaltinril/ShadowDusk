# Test Shader Corpus — Provenance & Fresh Examples

**Last updated:** 2026-08-01 — the issue-#187 fix added the four `ExPhantom*` fixtures (the
phantom-parameter set below). Previously 2026-07-31: Phase 51 A10 added three
DirectX-profile-floor fixtures and **reclassified the vendored Nez set**, whose DirectX column
collapsed once ShadowDusk started enforcing mgfxc's own floor (see the note above that table).
Corpus on disk: **151 `.fx` + 7 `.fxh`** — 62 in the fixture root, 50 in `examples/`, 1 in
`shadertoy/`, 38 under `third-party/`.

This document records (1) what is known about where the existing `.fx` test
fixtures came from, (2) an integrity caveat about those fixtures, and (3) a set
of **fresh, project-owned example shaders** authored from scratch for ShadowDusk
that we use going forward — with fully known provenance — alongside the original
cross-validated corpus.

---

## 1. Why this document exists

ShadowDusk's fidelity claim rests on comparing its output against **`mgfxc`'s**,
using real third-party shaders as inputs (see `CLAUDE.md` → *What success
actually means*). For that to mean anything, the test inputs should have known,
honest provenance.

Two problems surfaced:

1. **The fixtures were modified before they were ever committed.** An earlier
   automated pass "fixed" several `.fx` fixtures (e.g. to make them compile
   cleanly) rather than keeping them byte-for-byte as their upstream originals.
   Because that happened *before* the initial commit (`cfbb039`), this repo's git
   history contains no pre-modification version to diff or revert to.
2. **Per-shader provenance was never recorded.** `docs/research.md` and
   `monogame_runtime_mgfx_compiler_research.md` contain many project and `.fx`
   links, but every one is a **toolchain** or **MonoGame-builtin** reference
   (`BasicEffect.fx`, the `hlslparser` repos, DXC/SPIRV-Cross/MojoShader, etc.).
   None records where `Grayscale`/`Dissolve`/`Scanlines`/… originally came from.

Consequence: we cannot cleanly "restore the originals" for the modified
fixtures — and `mgfxc` is not available in this environment to regenerate
goldens anyway (it needs Windows + `fxc.exe`). So going forward we add a small
set of **fresh fixtures we fully own and document**, and treat the original 10
cross-validated shaders as legacy-but-grandfathered (they already have `mgfxc`
goldens and pass the in-engine comparison in `validation/`).

---

## 2. Provenance of the existing fixtures (best effort)

Recovered by inspecting the shader code and confirming upstream repos by their
distinctive shader sets / comment style. Treat "Confirmed" as "the upstream
project is identified"; it does **not** guarantee the checked-in file matches
upstream verbatim (see the integrity caveat in §1).

| Fixture(s) | Upstream source | Confidence |
|---|---|---|
| `PenumbraHull.fx`, `PenumbraLight.fx`, `PenumbraShadow.fx`, `PenumbraTexture.fx` | [discosultan/penumbra](https://github.com/discosultan/penumbra) — 2D lighting w/ soft shadows for MonoGame | Confirmed |
| `BasicShader.fx`, `TintShader.fx`, `BlendShader.fx`, `MultiTexture.fx`/`MultiTextureOverlay.fx`, `SimpleLightShader.fx` | [manbeardgames/monogame-hlsl-examples](https://github.com/manbeardgames/monogame-hlsl-examples) — the four worked examples (Apply / PassingValues / MultipleTextures / Simple2DLighting); matches the verbose teaching-comment style | Confirmed (project); per-file naming adapted |
| Post-FX pack: `Grayscale.fx`, `Invert.fx`, `Sepia.fx`, `Saturate.fx`, `Pixelated.fx`, `Scanlines.fx`, `Fading.fx`, `Dots.fx` | A common MonoGame post-process tutorial pack; exact upstream not confidently identified | Unknown |
| `Dissolve.fx`, `ForwardLighting.fx`, `PolygonLight.fx` | Nez-style 2D framework (underscore-prefixed sampler convention, discard-based dissolve); exact upstream not confidently identified | Unknown |
| `Minimal.fx`, `cbuffer.fx`, `multipass.fx`, `multitechnique.fx`, `render-states.fx`, `annotations.fx`, `platform-macros.fx`, `basiceffect-mini.fx`, etc. | Purpose-built ShadowDusk structural fixtures (SM4/5 feature probes) | Project-owned |
| `StateBlendAdditive.fx`, `StateDepthStencil.fx`, `StateRasterizer.fx`, `SamplerStatesFull.fx`, `AnnotatedTechnique.fx` | Writer-fidelity corpus: pass blend/depth-stencil/rasterizer states, baked `sampler_state` members, parameter/technique/pass annotations. All but `AnnotatedTechnique.fx` have real `mgfxc` goldens (mgfxc's grammar cannot parse technique/pass annotations). | Project-owned |

> If you can supply the original source links for the "Unknown" rows, add them
> here — that lets us diff the checked-in files against upstream and decide,
> per shader, whether to restore the original.

### The 10 cross-validated (image-equivalence) shaders

`Grayscale, Invert, TintShader, Sepia, Saturate, Pixelated, Scanlines, Fading,
Dots, Dissolve` — these have checked-in `mgfxc` goldens under
`tests/fixtures/golden/OpenGL/` and are the corpus the `validation/` harness
renders in real MonoGame and compares pixel-for-pixel. They remain in use; this
document does not change them.

---

## 3. Fresh, project-owned example shaders

Authored from scratch for ShadowDusk. **Provenance is fully
known: we wrote them.** They are licensed with the repository and derive from no
third-party shader. They live in:

```
tests/fixtures/shaders/examples/
```

Each targets a distinct part of the legacy→modern rewrite surface so the
`FxPreParser` rewrites and the `monoGameGl` GL path have owned, documented
regression coverage. All are SM3 PS-only and follow
MonoGame's conventional `SpriteBatch`/`SpriteEffect` shape (the validated path).

| File | What it exercises |
|---|---|
| `ExBareSamplerTex2D.fx` | Bare `sampler s0;` + `tex2D` → synthesized `Texture2D` + `.Sample` (gap #2 Form 2 + gap #4). No free uniforms. |
| `ExSamplerStateUniform.fx` | `Texture2D` + `sampler2D = sampler_state { Texture = <T>; }` (gap #2 Form 1) + a free `float4` uniform set by name. |
| `ExDualTexture.fx` | **Two** textures/samplers, each `tex2D`-sampled and resolving to its own texture; a `float` blend uniform (multi-sampler binding). |
| `ExLegacyTextureDiscard.fx` | Legacy effect-framework `texture T;` rewritten to `Texture2D T;` (gap #3) + `sampler_state` bound to it + `clip()`/discard + scalar uniform. A clean, owned analogue of `Dissolve`. |
| `ExModernSample.fx` | Control / negative case: already-modern `Texture2D` + `SamplerState` + `.Sample()` + `SV_TARGET`. No rewrite should fire. |

### Issue #106 regression set (relationals / ternaries / helpers / loop)

Pins issue #106: a relational operator (`<`, `<=`, `>`, `>=`), a ternary, an
`if`/`else` branch, or a `for`-loop condition appearing in a shader **body** was
misparsed by the `FxPreParser` as the start of an FX annotation. These
fixtures are small, real (full technique + pass, renderable), project-owned
originals in the **all-runtime SM3/fx_2_0 subset**, so each compiles on **OpenGL
(MonoGame-GL / KNI), DirectX_11 (MonoGame-DX), and FNA (D3D9 fx_2_0)** — verified
exit 0 with non-empty output on all three.

| File | Bug-class it guards | Runtimes |
|---|---|---|
| `Issue106Repro.fx` | The **verbatim reporter shader** from issue #106: a helper (`TestEarlyReturn`) using an equality (`==`), a relational (`<=`), nested `if`, and an **early `return`** in its body, called from the PS entry. Kept exact (only a provenance header added) so the real reported shape is pinned, not just a synthetic stand-in. VS+PS sprite path. | GL + DX + FNA |
| `ExTernaryHelper.fx` | The canonical #106 shape: a helper function that **returns a ternary over a relational** (`value <= 0.5f ? 0 : 1`), called from the PS entry, plus a ternary in the entry body. VS+PS sprite path. | GL + DX + FNA |
| `ExRelationalThreshold.fx` | **Relational operators directly in the PS body** — `<`, `<=`, `>`, `>=` as scalar bool expressions (not inside a ternary, not inside `clip()`), each promoted to a 0/1 float for a banded threshold. | GL + DX + FNA |
| `ExRelationalBranch.fx` | A **relational-driven `if` / `else if` / `else`** branch in the body (not `clip()`) **and** a **nested / chained ternary** (4-band select). | GL + DX + FNA |
| `ExLoopRelational.fx` | A **relational condition in a `for`-loop header** (`for (int i = 0; i < N; i++)`) — also closes the corpus's missing all-runtime SM3 loop case. Literal-bounded so fxc unrolls it at `ps_3_0`/`ps_2_0`. | GL + DX + FNA |

These are exercised by `Issue106RegressionCorpusTests` (compile-asserts each on
all three targets) and the FNA SM3 corpus census. As with the other fresh
fixtures, they prove **"ShadowDusk compiles them into a valid effect,"** not
pixel-equivalence to `mgfxc`/`fxc`.

### One-shot do-while / ANGLE-derivative set (issues #107 + #136)

Pins the SPIRV-Cross structured-early-return idiom (`do { … } while(false)`) that
its GLSL backend wraps around an entry point / inlined helper containing an early
`return`. These fixtures drive the GL-stage `MonoGameGlslRewriter` Rule-9 handling
(9a unwrap, 9b for-loop fallback) end-to-end through the real pipeline.

| File | Bug-class it guards | Runtimes |
|---|---|---|
| `Issue107DoWhile.fx` | The verbatim #107 reporter helper (`TestEarlyReturn`, nested-`if` early return): the wrapper must not survive as a raw `do { … } while(false)` (WebGL1 / KNI Reach rejects it at load). Pinned by `HidefGeneralityFixtureTests` to be do-while-free and, since #136, wrapper-loop-free (unwrapped, not lowered to a for-loop). | GL (KNI Reach + HiDef) |
| `Issue136HelperGradient.fx` | An inlined helper that **both early-returns and takes a derivative** (`fwidth`) with an entry-point early return around it — the nested-wrapper shape that ANGLE D3D11 (WebGL on Windows) poisons if left as a divergent loop. Pinned by `EarlyReturnHelperGradient…Issue136` so no gradient op lands inside a loop with a divergent exit in the emitted GL GLSL. | GL (KNI Reach + HiDef) |

These are compile + structural pins (emitted-GLSL shape), not pixel-equivalence
claims; the render side is proven separately by `validation/AngleDerivativeProbe`
and the KNI GL drivers. The vendored `apos-shapes-aa.fx` (below) is the real-world
derivative-AA shader of the same class.

### FX pre-parser robustness set (dropped-operator bug class)

Pins the dropped-operator bug class
(`plan/DONE/PHASE-45-fx-preparser-robustness.md`). Same shared root
cause as #106: the `FxLexer` drops several operators (`: + [ ] & | ! ? % ^ ~`), so
a flat heuristic in `FxPreParser` pattern-matched the fragmented token stream and
acted wrongly. Each fixture is small, real (full technique + pass, renderable), and
project-owned.

| File | Bug it guards | Runtimes |
|---|---|---|
| `ExModernSamplerState.fx` | **B2** — a `sampler S = sampler_state { Texture = <T>; }` declaration USED through the modern `T.Sample(S, uv)` method (not `tex2D`). Was erased → DXC "undeclared identifier 'S'"; now rewritten to a passthrough `SamplerState S;`. The MonoGame HiDef `SpriteEffect` / modern KNI 2D shape. | GL + DX (`.Sample` is SM4 method syntax; FNA N/A) |
| `ExColorWriteMask.fx` | **B3** — `ColorWriteEnable = Red \| Green \| Blue;`. The lexer drops `\|`, so the value arrived as three adjacent identifiers; the pass parser stopped at the first and demanded `;` (FX0008). | GL + DX + FNA |
| `ExLegacyTextureAnnotation.fx` | **B4** — a legacy `texture T < string Name = "x"; >;` (FX annotation on a `texture` object). The annotation has its own inner `;`, so `ConsumeLegacyTextureDecl` stopped early and leaked `>;` → DXC "expected unqualified-id"; the consume now tracks angle-bracket depth. Ubiquitous FX Composer / RenderMonkey / NVIDIA-sample shape. | GL + DX + FNA |
| `ExTextureNamedTexture.fx` | **B5** — a modern resource whose VARIABLE NAME is a legacy keyword, `Texture2D Texture : register(t0);`. The legacy-texture rewrite fired in name position and produced the broken `Texture2D Texture2D register;`; it now declines when the keyword's predecessor is an identifier/`>` (name position). | GL + DX (`.Sample` is SM4 method syntax; FNA N/A) |
| `ExVsColorReturn.fx` | **B6** — a VERTEX shader whose function-return semantic is `: COLOR` (writes `POSITION` via an `out` param). fxc/mgfxc accept it, but the PS `COLOR`->`SV_Target` rewrite broke the VS; the rewrite is now deferred and skips `compile vs_*` entry points. | GL + DX + FNA |
| `ExSamplerRegisterState.fx` | **B8** — `sampler S : register(s0) = sampler_state { … };` (the `register` clause appears BEFORE the `=`). The dropped `:` mis-routed it to the bare-sampler path, leaking the state block to DXC. | GL + DX + FNA |
| `ExSamplerAnnotation.fx` | **B9** — `sampler2D S = sampler_state { … } < string UIName = "x"; >;` (a trailing sampler-level FX annotation). `ParseSamplerDecl` hard-required `;` right after `}` (FX0001 on `<`); the annotation is now consumed and stripped. | GL + DX + FNA |
| `ExArrayTernaryAssign.fx` | **B7** — an array-indexed relational with an assignment in a ternary arm inside a function body, `Thresholds[i] < x ? acc = w : acc;` (the issue-#106 residual). Once `?`/`:`/`[`/`]` are dropped, the `x acc =` tail satisfies the annotation-shape guard; the global annotation strip is now gated on brace depth 0, so an in-body expression can never be misread. | GL + DX + FNA |
| `ExReservedWordUniform.fx` | **B10** (a DIFFERENT class — a GLSL reserved-word / reflection-join bug, not a dropped-operator pre-parser one) — a free uniform named after a GLSL reserved word, `float noise;`, used in the body. On GL, SPIRV-Cross renames it `_noise`, so the cbuffer/parameter join (matched by name) missed and failed `SD0012`. The join now falls back to an offset bridge that recovers the parameter by its `BaseRegister * 16` byte offset, keeping it exposed under `noise`. See the third-party `Noise.fx` note below. | GL + DX + FNA |

These are exercised by `Phase45PreParserRobustnessCorpusTests` (compile-asserts
each on its applicable targets); the all-runtime ones are also in the FNA SM3
corpus census. Same scope as above: a valid-effect compile, not pixel-equivalence.

### Phantom-parameter set (issue #187 — synthesized GL register backing)

Pins the issue-#187 class (`plan/DONE/ISSUE-187-gl-phantom-parameter-compile-fidelity.md`): a
numeric uniform whose only reads form an algebraic identity DXC's `-spirv` backend cancels
(fxc and the DXIL reflection companion do not), so the OpenGL pipeline must SYNTHESIZE the
parameter's register backing. Each fixture pins one synthesis sub-shape found by the
adversarial reviews. All four are project-owned, authored from scratch 2026-08-01, and
asserted structurally by `GlPhantomParameterTests` (plus the corpus-wide backing sweep).

| File | Sub-shape it guards | Runtimes |
|---|---|---|
| `ExPhantomNonSquareMatrix.fx` | A `float2x4` phantom must be sized by the runtime's TRANSPOSED matrix write model — **Columns** registers (MonoGame/KNI upload `ColumnCount` 16-byte rows); sizing by Rows under-allocates and crashes the first `EffectPass.Apply`. | GL + DX (census) |
| `ExPhantomDerivativeUniform.fx` | A phantom in a derivative-using shader: the synthesized declaration must be INSERTED after the `#extension GL_OES_standard_derivatives` header + `#ifdef GL_ES` precision block (strict ESSL front ends reject it earlier; desktop GL is lenient, which is how it would slip past desktop gates). | GL + DX (census) |
| `ExPhantomSecondCbufferFold.fx` | One live cbuffer + one fully-folded cbuffer: synthesis must APPEND after the live registers and RESIZE the existing declaration (`[1]` → `[2]`) — the resize branch no fully-folded fixture can reach. | GL + DX (census) |
| `ExPhantomTexLodUniform.fx` | A phantom in an explicit-LOD (`SampleLevel`) shader: the insert must clear the BALANCED `#if __VERSION__ >= 300 … #elif … #extension … #endif` TexLod header, whose `#extension` directives live inside branches (Mesa hard-errors on a mid-shader `#extension` and takes the `GL_ARB_shader_texture_lod` branch). | GL + DX (census) |

### Sampler-pair set (Phase 51 A7 — the OpenGL/DX12 shared-`SamplerState` fix)

Project-owned, authored for the release that re-keyed the GL sampler table on
**(texture, sampler) pairs**. These are the fixtures the headline fix is proven against, and
they back `validation/SamplerPairsGl`:

- **`SharedSamplerPair.fx`** — two textures read through **one shared `SamplerState`** (the
  classic diffuse+lightmap shape). Ordinary HLSL that `mgfxc` has always compiled; ShadowDusk
  used to reject it outright with the now-retired `SD0216`. Two pairs ⇒ two sampler records.
- **`SamplerPairMirror.fx`** — two textures sampled in **reverse of declaration order**, which
  is what exposed the second, silent defect: SPIRV-Cross declares combined samplers in
  **first-use** order, so counts matched while the texture parameter and the sampler-type byte
  came out swapped. Deliberately samples asymmetrically so a mis-binding changes the picture.
  Its two textures hold **identical pixels** on purpose, so the arm isolates per-pair sampler
  *state* — which is also why it is structurally blind to slot NUMBERING, the gap `#189` fell
  through. Use `SamplerRegisterOrder.fx` for anything about which unit a pair lands on.
- **`SamplerRegisterOrder.fx`** — GitHub issue **#189**. `SpriteSampler : register(s0)` and
  `MaskSampler : register(s1)`, sampled in **reverse** declaration order, output
  `(sprite.r, mask.g, 0, 1)`. It is the canonical SpriteBatch custom-effect shape — `register(s0)`
  *is* the sprite texture, because `SpriteBatch` forces it onto unit 0 right after
  `EffectPass.Apply()`. Rendered with a red sprite and a green mask: **yellow = slots allocated in
  declaration order (correct, matches fxc/mgfxc)**, **black = first-use order (the #189 bug)**.
  Both channels flip together, so neither outcome can be mistaken for a tolerance artefact. Unlike
  the two fixtures above it uses **distinct** textures and leaves unit 0 to `SpriteBatch`, which is
  precisely what makes slot allocation observable. Goldens on `OpenGL` + `DirectX_11`.

- **`SamplerRegisterSparse.fx`** — GitHub issue **#189**, the sparse/offset half, and the
  deliberate complement to the fixture above. Two samplers at `s2`/`s3` with **nothing at
  `s0`/`s1`**, sampled strictly **in** declaration order, so ordering cannot be what it measures:
  the only variable is the **absolute register value**. Compacting to units 0/1 is order-preserving
  and still wrong, because `SpriteBatch` overwrites unit 0 with the sprite after
  `EffectPass.Apply()`. BLUE sprite + RED MaskA + GREEN MaskB: **yellow = registers honoured**,
  **green = compacted (the bug)** — only the red channel moves, and the untouched green is the
  control proving the harness bound anything at all. **Keep it in LEGACY `sampler` syntax**: at
  `ps_3_0` the legacy sampler *is* the combined object, so the annotation lands the pair on that
  exact unit. A modern `SamplerState : register(sN)` behaves differently — it RESERVES the register
  and the pair is allocated around it (one texture plus `S : register(s0)` yields `ps_s1`) — so
  rewriting this fixture in modern syntax would change what it measures. Goldens on `OpenGL`
  + `DirectX_11`.

### ShaderToy route fixture

- **`shadertoy/GradientToy.fx`** — the **pinned** output of converting `GradientToy.glsl` with
  the real `ShaderToyConverter`. `validation/ShaderToyRouteGl` and `validation/ShaderToyRouteDx`
  each assert the converter still emits this exact file before rendering, so converter drift
  turns the gates red instead of leaving a golden describing a different shader. It is also
  what surfaced Phase 51 **A10**: the converter used to emit `vs_3_0`/`ps_3_0` in *both* arms of
  its `#if OPENGL` header, which real `mgfxc /Profile:DirectX_11` rejects while ShadowDusk
  accepted it. **Both halves are fixed**: the header is now `SM4`-gated (DirectX gets the
  `*_4_0_level_9_1` pair; OpenGL and FNA keep SM3), the fixture is **golden-backed on both
  profiles**, and the DirectX target enforces the floor itself (`SD0015`). Regenerate it with
  `dotnet run --project tools/shadertoy2fx/src/ShadowDusk.ShaderToy.Cli -- tests/fixtures/shaders/shadertoy/GradientToy.glsl -o tests/fixtures/shaders/shadertoy/GradientToy.fx --name GradientToy --technique ShaderToy`
  (normalize to LF), then `tools/compile-fixtures.ps1 -Profiles DirectX_11,OpenGL`.

### DirectX compile-profile floor fixtures (Phase 51 A10)

`mgfxc`'s `DirectX_11` shader profile accepts only `{vs,ps}_4_0_level_9_1`, `_4_0_level_9_3`,
`_4_0`, `_4_1`, and `_5_0`. Anything else — including `_4_0_level_9_0` **and every SM6
profile** — it refuses with *"must be SM 4.0 level 9.1 or higher!"*. These three fixtures pin
ShadowDusk's matching `SD0015` rejection, one per way the condition arises:

- **`ExProfileSm3OnDirectX.fx`** — a bare literal `compile ps_3_0 MainPS()` (no header at all),
  the shape most real DesktopGL/FNA shaders ship with. Exercises the cheap literal path.
  Compiles on OpenGL and FNA, where SM3 is correct.
- **`ExProfileSm3BothArms.fx`** — an `#if OPENGL … #else …` header naming SM3 in **both** arms:
  a verbatim capture of what `ShaderToyConverter` emitted before A10. The compile target is a
  macro name, so this exercises the DXC `-P` expansion path.
- **`ExProfileSm6OnDirectX.fx`** — `compile ps_6_0`, which is numerically *higher* and still
  refused. It exists so that reimplementing the floor as a `major >= 4` comparison is caught by
  a test rather than by a consumer's failed Content Pipeline build.

### How they are used

- **Now (no `mgfxc` golden required):** compile-level coverage in
  `tests/ShadowDusk.Integration.Tests/Tests/CompileExampleFixtureTests.cs` —
  each compiles for OpenGL and produces a structurally valid `.mgfx`
  (`MGFX` signature, version 10, ≥1 shader blob). This asserts ShadowDusk
  *emits a well-formed, loadable container*, not pixel-equivalence.
- **Later (when `mgfxc` is available on a Windows + DirectX SDK box):**
  generate `mgfxc` goldens for these into `tests/fixtures/golden/OpenGL/` and
  add them to the `validation/` render-and-compare harness to get the full
  in-engine fidelity bar.

> **Scope honesty:** until those goldens exist, these fresh fixtures prove
> "ShadowDusk compiles them into a valid effect," **not** "renders the same as
> `mgfxc`." That stronger claim is still carried only by the original 10.

---

## 4. Third-party shader corpus (vendored, real shipping shaders)

Unlike the project-owned fixtures in §3, these are **NOT project-owned** — they are real, shipping MonoGame
post-process shaders **vendored verbatim** from the **Nez** framework
(`prime31/Nez`, **MIT**, Copyright (c) 2016 Mike), pinned at commit
`6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c`, upstream dir
`DefaultContentSource/effects/`. They live under
`tests/fixtures/shaders/third-party/Nez/`, with the verbatim upstream `LICENSE` and a
`NOTICE.md` recording the repo, exact commit, per-file upstream path, license, and the
single modification (a provenance comment header prepended to each `.fx`; **the shader
code itself is byte-for-byte upstream**). Licence gate: only MIT / MS-PL / BSD /
Apache-2.0 / public-domain are vendorable — Nez is MIT, so it qualifies; the
MonoGame-docs grayscale tutorial (CC-BY-NC-SA) was explicitly **rejected** as
non-permissive.

**Why these:** they broaden the corpus along the language features the project-owned
fixtures under-covered: a literal-bounded `for`-loop, helper functions called from an
entry point, relational-driven `if` branches in the body, bloom passes, UV distortion,
vignette, edge-detect, VPOS + float-modulo scanlines, a two-technique VS+PS effect, and
a 1-D-LUT palette swap.

Each shader was compile-classified on all three delivery targets and is wired in only on
the targets it actually compiles on (the rationale per shader is in the directory's
`NOTICE.md`):

> **Reclassified 2026-07-31 (Phase 51 A10) — the DirectX column collapsed, and that is the
> finding, not a regression.** Every Nez shader below names a legacy `ps_2_0`/`ps_3_0` (or
> `vs_2_0`) compile target outright, with no `#if OPENGL … #else …` header. MonoGame's
> `DirectX_11` shader profile **refuses** anything below SM 4.0 level 9.1 — *"Invalid profile
> 'ps_3_0'. Pixel shader 'PixelShaderFunction' must be SM 4.0 level 9.1 or higher!"* — and real
> `mgfxc /Profile:DirectX_11` was verified to fail every one of them. ShadowDusk used to accept
> them; since A10 it declines them with **`SD0015`**, so the table below now matches the
> reference compiler. **Nez targets DesktopGL**, so this is neither a Nez defect nor a
> ShadowDusk gap: a user who wants one of these on WindowsDX adds the standard cross-platform
> header, which is exactly what the diagnostic tells them. The DX-reject set is asserted *as a
> reject* by `ThirdPartyShaderCorpusTests.ThirdPartyShader_DirectX11_SubFloorProfile_RejectsWithSd0015`
> — it is covered, not merely dropped. Nothing else about these shaders changed, and no output
> bytes moved on any target.

| File | Upstream | Targets (compile) | Feature / gap covered | Classification |
|---|---|---|---|---|
| `GaussianBlur.fx` | Nez (MIT) | GL + FNA | A literal-bounded `for`-loop accumulating weighted taps over `float2[]`/`float[]` array uniforms (the corpus's only GL+FNA SM3 loop). | GL + FNA (`SD0015` on DX) |
| `BloomCombine.fx` | Nez (MIT) | GL + FNA | Helper fn `adjustSaturation()` called from the entry; 2nd sampler; `lerp`/`dot`/`saturate`. | GL + FNA (`SD0015` on DX) |
| `BloomExtract.fx` | Nez (MIT) | GL + FNA | Bloom bright-pass; `saturate()` threshold remap. | GL + FNA (`SD0015` on DX) |
| `Twist.fx` | Nez (MIT) | GL + FNA | Relational-driven `if (dist < radius)` in the body + `length`/`sin`/`cos` UV warp. | GL + FNA (`SD0015` on DX) |
| `Vignette.fx` | Nez (MIT) | GL + FNA | Radial vignette: `dot`-based falloff + swizzle, no VS. | GL + FNA (`SD0015` on DX) |
| `HeatDistortion.fx` | Nez (MIT) | GL + FNA | 2nd sampler declared with explicit `AddressU/V = Wrap` `sampler_state`; time-scrolled UV; remap-to-signed. | GL + FNA (`SD0015` on DX) |
| `Bevels.fx` | Nez (MIT) | GL + FNA | Neighbor-tap edge-detect / emboss (offset `tex2D` taps, no loop). | GL + FNA (`SD0015` on DX) |
| `PixelGlitch.fx` | Nez (MIT) | GL + FNA | Helper fn `hash11()` (`frac`/`floor`) called from the entry; row offset. | GL + FNA (`SD0015` on DX) |
| `SpriteBlinkEffect.fx` | Nez (MIT) | GL + FNA | Tint via `lerp` by a uniform alpha; VS-output-struct PS. | GL + FNA (`SD0015` on DX) |
| `Letterbox.fx` | Nez (MIT) | GL + FNA | `VPOS` screen-space + `min()` + relational `if`. VPOS->`gl_FragCoord` render-equivalence is **not** asserted. | GL + FNA (`SD0015` on DX), VPOS |
| `SpriteLines.fx` | Nez (MIT) | GL + FNA | Two techniques (H/V); `VPOS` + `floor` + float modulo (`%`). VPOS render-equivalence **not** asserted. | GL + FNA (`SD0015` on DX), VPOS |
| `Crosshatch.fx` | Nez (MIT) | FNA only | Nested `if` + `<` relationals + `VPOS` + float `%` + an `int` uniform. **Not GL:** `int` uniforms are not modelled on the MonoGame-GL path (loud `SD0210`, by design). **Not DX:** `compile ps_3_0` is below the DirectX floor (`SD0015`). | FNA only |
| `PaletteCycler.fx` | Nez (MIT) | FNA only | Palette swap via a 1-D LUT (`tex1D` / `sampler1D`). **Not GL/DX:** `tex1D` has no 1:1 modern `Texture` method, rejected with a targeted `FX0012` that points to FNA (which compiles it natively). | FNA only |
| `Reflection.fx` | Nez (MIT) | none (reject-only) | Two techniques, each **VS+PS** (mirror + water); world-space, `half2`, `frac`, relational `if`. **Not GL:** the multi-`TEXCOORD` interpolant block cannot be expressed in std140/std430 by SPIRV-Cross (`SD0100`). **Not FNA:** an int/relational construct hits the vkd3d 1.17 SM3 gap (`X0000`). **Not DX (since A10):** `compile vs_2_0` is below the DirectX floor (`SD0015`) — the profile mgfxc itself refuses. Retained as a reject-set fixture. | reject-only |
| `Noise.fx` | Nez (MIT) | GL + FNA | Film-grain; helper fn `rand()` (`frac`/`sin`/`dot`) called from the entry. A uniform literally named `noise` collides with a GLSL reserved word and SPIRV-Cross renames it `_noise`; this used to break the GL cbuffer/parameter join (`SD0012`), but the **B10 offset-bridge fallback fixed it** (see below), so it now compiles on GL too. | GL + FNA (`SD0015` on DX), B10 |

These are exercised by `ThirdPartyShaderCorpusTests` (compile-asserts each on its
classified targets) and the GL+DX structural census; the all-runtime ones are
also in the FNA SM3 corpus census.

> **Scope (same as §3):** these prove **"ShadowDusk compiles them into a well-formed,
> loadable container,"** not pixel-equivalence to `mgfxc`/`fxc`. There is no committed
> golden for them; the render bar stays with the `validation/*` drivers. The VPOS shaders
> in particular compile on every target but their cross-path VPOS behavior is deliberately
> left unclaimed.

> **GLSL reserved-word uniforms on GL (`Noise.fx`).** A free uniform named after
> a GLSL reserved word (e.g. `noise`) is renamed by SPIRV-Cross, which used to
> break the GL parameter join by name. The pipeline now falls back to matching by
> register offset, so the parameter stays exposed under its original name
> (`effect.Parameters["noise"]` binds); an ambiguous multi-cbuffer shape still
> fails loudly rather than risk a mis-map. Details: `docs/glsl-uniform-naming.md`
> "Design notes".

### Gum / Apos.Shapes (the Gum-ecosystem shaders)

Requested by **Victor Chelaru / vchelaru** (Gum's author; Gum uses **Apos.Shapes**
for its UI shape rendering). Two more vendored sets, both **MIT**, both classified
by an actual compile probe:

- `tests/fixtures/shaders/third-party/Apos.Shapes/` — Apos.Shapes' SDF shape renderer
  (`Apostolique/Apos.Shapes`, MIT, Copyright (c) 2021 Jean-David Moisan, commit
  `3fb73b8d…`, upstream `Source/Content/apos-shapes.fx`).
- `tests/fixtures/shaders/third-party/Gum/` — Gum's own sample-project shaders
  (`vchelaru/gum`, MIT, Copyright (c) 2013-2024 FlatRedBall LLC, commit `771bc5c3…`).

| File | Upstream | Targets (compile) | Feature / gap covered | Classification |
|---|---|---|---|---|
| `Apos.Shapes/apos-shapes.fx` | Apos.Shapes (MIT) | GL + DX | One large **VS+PS** SDF effect: 10 `TEXCOORD` interpolants, `__KNIFX__`/`OPENGL` macro profile branch, a Newton-iteration `for`-loop (`EllipseSDF`), `int` locals, 11-way `if/else` shape dispatch, `%` modulo, ternaries, `discard`, `tex2D`, two samplers (one `register(s0)`), Oklab + gradient math. **Not FNA:** no SM3/FNA profile branch (its `#else` selects `ps_4_0`) + a dense PS exceeds the vkd3d `fx_2_0`/SM3 ceiling (`X0000`) — a legit SM limit (Apos.Shapes ships for MonoGame GL/DX, not FNA). | GL + DX |
| `Apos.Shapes/apos-shapes-aa.fx` | Apos.Shapes (MIT) | GL + DX | The later **derivative-based-antialiasing** revision (commit `d507a734…`, issue #136): `ddx`/`ddy` of the SDF and of an interpolated position drive the AA footprint, alongside conditional `discard`, inlined-helper early returns (SPIRV-Cross's one-shot do-while), genuine Newton loops, and a third sampler (`register(s2)` blue-noise dither). Carries the `AposShapesAa_OpenGl_NoGradientOpInsideDivergentLoop_Issue136` pin: the emitted GL GLSL must never place a gradient op inside a loop with a divergent exit (ANGLE D3D11 zeroes derivatives there). **Not FNA:** same SM ceiling as above plus the gradient intrinsics. | GL + DX |
| `Apos.Shapes/apos-shapes-sm6.fx` | Apos.Shapes (MIT) | GL + DX + Vulkan | The CURRENT upstream revision (commit `ea38c6d8…`, the issue #145 reproducer): an `#elif SM6` branch for Vulkan (`vs_6_0`/`ps_6_0`, three `Texture2D`/`SamplerState` pairs), a base-2048 packed-color quantization (`Pack11`/`DecodeDigit`) replacing the earlier `apos-shapes.fx`'s Cantor-pair packing, a 13-element vertex input, and blue-noise dithering. **Render-proven on DX and Vulkan** (maxd 0, `docs/validation-matrix.md` §1/§6) but deliberately **NOT** the fixture GL's render-proof uses (see the callout above) — its real mgfxc GL compile renders solid black, a confirmed MojoShader/fxc codegen bug. **Not FNA:** exceeds the vkd3d `fx_2_0`/SM3 instruction ceiling (`SD0305`). | GL + DX + Vulkan (compile); DX + Vulkan (render-proof) |
| `Gum/MonoGameInCode-Grayscale.fx` | Gum (MIT) | GL + DX + FNA | `vs/ps_4_0_level_9_1` profiles, `Texture2D` + `sampler2D` + `sampler_state`, `: COLOR0` output, PS-only technique, dot-luminance. | all-runtime |
| `Gum/KniInCode-Shader.fx` | Gum (MIT) | FNA only | Legacy D3D9 **effect-framework syntax**: `uniform extern texture`, `sampler_state { Texture = <…> }`, `: VIEWPROJ` matrix semantic, `: COLOR` outputs, lowercase `pixelshader = compile ps_2_0`. **Not GL/DX:** DXC rejects effect syntax (`-Weffects-syntax`); only the FNA/`fx_2_0` native-effects path accepts it (same shape as `PaletteCycler` being FNA-only). | FNA only |
| `Gum/FnaSample-Shader.fx` | Gum (MIT) | none (honest per-target limits) | The `TECHNIQUE()`/`SAMPLE()` `#define` macro idiom (a `technique` defined inside a macro), legacy `uniform extern texture`, `: VIEWPROJ`, `vs_1_1`/`ps_2_0`. FNA recovers the macro technique but declines the sub-SM2 `vs_1_1` profile (`SD0300`); GL keeps `SD0010` (the GL macro-model gap, below); DX fails `X0000` (`vs_1_1`/`ps_2_0` aren't DX11-compilable). All documented limits, not technique-blindness. | per-target limits |

These are exercised by `ThirdPartyShaderCorpusTests` on their classified targets
(with dedicated pins for `FnaSample-Shader.fx`'s per-target rejections) and the
GL+DX structural census. Same **scope** as the Nez set above: a well-formed-container
compile, not pixel-equivalence (no committed goldens). The one genuinely notable
result is that **`apos-shapes.fx` — the shader Gum's shape rendering actually
depends on — compiles on GL and DX**, the targets Gum ships on.

**Beyond compile-only: Apos.Shapes is now render-proven on DX, Vulkan, and GL (Phase 51 A3,
2026-07-23).** The current-upstream revision `apos-shapes-sm6.fx` (see
`third-party/Apos.Shapes/NOTICE.md` — it also compiles GL/DX/Vulkan, FNA excluded on a
legitimate SM ceiling) is pixel-diffed against the real `mgfxc` golden on **DirectX**
(`validation/VsDrivenDx -- apos`, **maxd 0** on both ShadowDusk DXBC backends) and **Vulkan**
(`validation/VsDrivenVulkan -- apos`, also maxd 0). **GL uses a different fixture on purpose:** `apos-shapes.fx` (the Phase 49 pin, below),
not `apos-shapes-sm6.fx` — that later revision's real mgfxc GL compile is confirmed to render
solid black, a MojoShader/fxc codegen bug, not a ShadowDusk defect. `validation/VsDriven --
apos` pixel-diffs `apos-shapes.fx` against the real mgfxc OpenGL golden at **maxd 2/255** (see
`docs/validation-matrix.md` §1/§6). See `NOTICE.md` and `validation/VsDriven`'s
`AposShapesRenderer` for the full trace. FNA is permanently excluded (a legitimate SM3
instruction-slot ceiling, not an open rung).

**Phase 55 (2026-07-23): the render-proof above exercised exactly one shape (a circle) — now
expanded to the FULL `ShapeBatch` shape gallery** (every `Draw*`/`Fill*`/`Border*` method:
circle, rectangle+corner-radii, line, path, hexagon, triangle, ellipse, arc, ring; gradients,
dashes, rotation), using the REAL `Apos.Shapes` NuGet package (0.7.7, confirmed byte-identical to
`apos-shapes-sm6.fx` above modulo one comment) as the render harness via its
`ShapeBatch(GraphicsDevice, Effect?)` effect-injection constructor — no more hand-rolled vertex
structs. DX11: **maxd 0** across all 30 cells on both DXBC backends — the `d3dcompiler_47`
oracle arm against the real, locally-generated `mgfxc` golden, the vkd3d arm against the
package's own embedded effect (which disassembles as itself vkd3d-shader-compiled, so it serves
only as a same-toolchain baseline, never an oracle). Vulkan: **maxd 0** against the package's
DXC-family embedded effect. DX12: within 1/255 against the real local `mgfxc` golden, differing on
11 pixels of 402,984 — **root-caused 2026-07-31 to the pinned DXC build, not a ShadowDusk defect**
(ours `dxcoob 1.7.2212.40`, the golden's `dxcoob 1.8.2505.32`; ShadowDusk's own HLSL and flags
through a DXC 1.8 build reproduce the golden's DXIL instruction-for-instruction and render at
maxd 0 — see `docs/validation-matrix.md` §7).
GL gets a candidate-only visibility check (30/30 shapes render visible content) — no golden
exists for this gallery on GL (the same confirmed MojoShader black-render bug applies to nearly
every shape). See `third-party/Apos.Shapes/NOTICE.md` §"Phase 55" and
`plan/DONE/PHASE-55-apos-shapes-shape-gallery-render-proof.md`.

> **Macro-defined techniques.** The DX and FNA paths recover `TECHNIQUE()`-macro
> techniques, so the SM2-fitting MonoGame stock effects compile on FNA (the ones
> that don't fail for honest SM2-limit reasons). **OpenGL does not**: the legacy
> DX9/SM2 branch these effects expand to on GL crashes DXC's SPIR-V codegen, so
> GL keeps a loud `SD0010` instead. Tracked in `plan/` (Phase 41 GAP-1 / GL).

### MonoGame's own test effects — the reference compiler's acceptance set (issue #145)

`tests/fixtures/shaders/third-party/MonoGame/` vendors the 17 `.fx` + 2 `.fxh` assets from
`MonoGame/MonoGame` `Tests/Assets/Effects/` at tag `v3.8.5` (**Ms-PL**, Copyright (C) MonoGame
Foundation, Inc). These are what MonoGame itself builds in its own test suite, and upstream's
per-profile `.mgcb` files state exactly which effects each backend must compile — including a
**`Vulkan.mgcb`**, which is why they were added: issue #145 exposed that the Vulkan proof ran
ten PS-only, matrix-free, modern-syntax fixtures and therefore could not see either bug.

Per-file upstream paths, the `.mgcb` membership, the measured per-target compile status, and
the reasons behind each non-compile are in the directory's `NOTICE.md`. The headline additions
to corpus coverage:

- **`Instancing.fx`** — VS-driven with a **`float4x4` vertex input** on `BLENDWEIGHT` (four
  consecutive input locations) plus View/Projection matrices. Exactly the shape issue #145's
  bug 1 (a transposed matrix on Vulkan) needed to be visible.
- **`VertexTextureEffect.fx`** — vertex-texture fetch with an SM6/SM4/SM3 profile branch.
- **`ParameterTypes.fx`** — the parameter-class/type sweep (scalars, vectors, matrices, arrays,
  structs).
- **`ParserTest.fx` / `PreprocessorTest.fx` / `DefinesTest.fx`** — the reference compiler's own
  parser and preprocessor torture tests.
- **`TextureArrayEffect.fx`, `CustomSpriteBatchEffect*.fx`** — array textures, two
  texture/sampler pairs, and a comparison sampler.

**Two real defects surfaced the day they landed**, both fixed in the same change: anonymous
`technique { … }` blocks were rejected outright (`FX0001`) even though mgfxc compiles them and
writes an empty technique name; and `SamplerComparisonState` on the FNA target crashed the
whole process inside vkd3d's SM1 lowering (now a loud `FX0013`).

Coverage note: every fixture in the corpus — these included — is exercised by the corpus-wide
**`VulkanCorpusStructuralTests`** gate, which requires each one to either produce a
structurally valid Vulkan container (combined descriptors at binding ≥ 32, unique bindings,
column-major matrices, `main` entry point, no `SPV_GOOGLE_*` extensions) or fail with a real
diagnostic. There is no skip list to quietly grow.
