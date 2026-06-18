# Test Shader Corpus — Provenance & Fresh Examples

**Last updated:** 2026-05-30

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

## 2. Provenance of the existing fixtures (best effort, 2026-05-30)

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
| `StateBlendAdditive.fx`, `StateDepthStencil.fx`, `StateRasterizer.fx`, `SamplerStatesFull.fx`, `AnnotatedTechnique.fx` | Phase 43 writer-fidelity corpus (pass blend/depth-stencil/rasterizer states incl. negative floats; baked `sampler_state` members; parameter/technique/pass annotations). All but `AnnotatedTechnique.fx` have real `mgfxc` 3.8.2.1105 goldens in `tests/fixtures/golden/{OpenGL,DirectX_11}/` (mgfxc's grammar cannot parse technique/pass annotations); validated by `MgfxStateGoldenMatchTests` (structural vs golden) and `validation/StateFidelity` (real MonoGame 3.8.2 `Effect` load + pixel-equal render vs the golden). | Project-owned |

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

Authored from scratch for ShadowDusk on 2026-05-30. **Provenance is fully
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

Authored from scratch for ShadowDusk on 2026-06-17 to pin issue #106 ("Shader
should be able to return ternary values"). Before the fix, a relational operator
(`<`, `<=`, `>`, `>=`), a ternary, an `if`/`else` branch, or a `for`-loop
condition appearing in a shader **body** was misparsed by the `FxPreParser` as the
start of an FX annotation and the compile failed loudly with **FX0001**. These
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

These are exercised by `tests/ShadowDusk.Integration.Tests/Issue106RegressionCorpusTests.cs`
(compile-asserts each on all three targets) and folded into the FNA SM3 corpus
census in `FnaCompileFixtureTests.Sm3Corpus()`. As with the other fresh fixtures,
they prove **"ShadowDusk compiles them into a valid effect,"** not pixel-equivalence
to `mgfxc`/`fxc` — the in-engine render-and-compare (a committed golden + a
`validation/*` driver) is the follow-up.

### Phase 45 FX pre-parser robustness set (dropped-operator bug class)

Authored from scratch for ShadowDusk on 2026-06-17 to pin the Phase 45 fixes
(`plan/DONE/PHASE-45-fx-preparser-robustness.md`, items B2-B9). Same shared root
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

These are exercised by `tests/ShadowDusk.Integration.Tests/Phase45PreParserRobustnessCorpusTests.cs`
(compile-asserts each on its applicable targets); the all-runtime ones (B3/B4/B6/B7/B8/B9/B10)
are also folded into `FnaCompileFixtureTests.Sm3Corpus()` (and `ExReservedWordUniform.fx` is in
the cross-host byte-identity corpus, pinning the GL offset-bridge path's determinism). As with
the other fresh fixtures, they prove **"ShadowDusk compiles them into a valid effect,"** not
pixel-equivalence to `mgfxc`/`fxc`.

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

Added 2026-06-17 (issue #106 / Phase 45 follow-up). Unlike the project-owned
fixtures in §3, these are **NOT project-owned** — they are real, shipping MonoGame
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

| File | Upstream | Targets (compile) | Feature / gap covered | Classification |
|---|---|---|---|---|
| `GaussianBlur.fx` | Nez (MIT) | GL + DX + FNA | A literal-bounded `for`-loop accumulating weighted taps over `float2[]`/`float[]` array uniforms (the corpus's only all-runtime SM3 loop). | all-runtime |
| `BloomCombine.fx` | Nez (MIT) | GL + DX + FNA | Helper fn `adjustSaturation()` called from the entry; 2nd sampler; `lerp`/`dot`/`saturate`. | all-runtime |
| `BloomExtract.fx` | Nez (MIT) | GL + DX + FNA | Bloom bright-pass; `saturate()` threshold remap. | all-runtime |
| `Twist.fx` | Nez (MIT) | GL + DX + FNA | Relational-driven `if (dist < radius)` in the body + `length`/`sin`/`cos` UV warp. | all-runtime |
| `Vignette.fx` | Nez (MIT) | GL + DX + FNA | Radial vignette: `dot`-based falloff + swizzle, no VS. | all-runtime |
| `HeatDistortion.fx` | Nez (MIT) | GL + DX + FNA | 2nd sampler declared with explicit `AddressU/V = Wrap` `sampler_state`; time-scrolled UV; remap-to-signed. | all-runtime |
| `Bevels.fx` | Nez (MIT) | GL + DX + FNA | Neighbor-tap edge-detect / emboss (offset `tex2D` taps, no loop). | all-runtime |
| `PixelGlitch.fx` | Nez (MIT) | GL + DX + FNA | Helper fn `hash11()` (`frac`/`floor`) called from the entry; row offset. | all-runtime |
| `SpriteBlinkEffect.fx` | Nez (MIT) | GL + DX + FNA | Tint via `lerp` by a uniform alpha; VS-output-struct PS. | all-runtime |
| `Letterbox.fx` | Nez (MIT) | GL + DX + FNA | `VPOS` screen-space + `min()` + relational `if`. Compiles on every target; VPOS->`gl_FragCoord` render-equivalence is **not** asserted. | all-runtime (VPOS) |
| `SpriteLines.fx` | Nez (MIT) | GL + DX + FNA | Two techniques (H/V); `VPOS` + `floor` + float modulo (`%`). Compiles everywhere; VPOS render-equivalence **not** asserted. | all-runtime (VPOS) |
| `Crosshatch.fx` | Nez (MIT) | DX + FNA | Nested `if` + `<` relationals + `VPOS` + float `%` + an `int` uniform. **Not GL:** `int` uniforms are not modelled on the MonoGame-GL path (loud `SD0210`, by design). | DX + FNA |
| `PaletteCycler.fx` | Nez (MIT) | FNA only | Palette swap via a 1-D LUT (`tex1D` / `sampler1D`). **Not GL/DX:** `tex1D` has no 1:1 modern `Texture` method, rejected with a targeted `FX0012` that points to FNA (which compiles it natively). | FNA only |
| `Reflection.fx` | Nez (MIT) | DX only | Two techniques, each **VS+PS** (mirror + water); world-space, `half2`, `frac`, relational `if`. **Not GL:** the multi-`TEXCOORD` interpolant block cannot be expressed in std140/std430 by SPIRV-Cross (`SD0100`). **Not FNA:** an int/relational construct hits the vkd3d 1.17 SM3 gap (`X0000`). | DX only |
| `Noise.fx` | Nez (MIT) | GL + DX + FNA | Film-grain; helper fn `rand()` (`frac`/`sin`/`dot`) called from the entry. A uniform literally named `noise` collides with a GLSL reserved word and SPIRV-Cross renames it `_noise`; this used to break the GL cbuffer/parameter join (`SD0012`), but **Phase 45 B10 fixed it** (offset-bridge fallback — see below), so it now compiles on GL too. | all-runtime (B10) |

These are exercised by `tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs`
(compile-asserts each on its classified targets; FNA via `[FnaTheory]` + the
MojoShader-rule fx_2_0 validator). The all-runtime ones are also folded into
`FnaCompileFixtureTests.Sm3Corpus()`, and all 15 are auto-globbed by the GL+DX
`Phase41StructuralDivergenceMatrixTests` structural census.

> **Scope (same as §3):** these prove **"ShadowDusk compiles them into a well-formed,
> loadable container,"** not pixel-equivalence to `mgfxc`/`fxc`. There is no committed
> golden for them; the render bar stays with the `validation/*` drivers. The VPOS shaders
> in particular compile on every target but their cross-path VPOS behavior is deliberately
> left unclaimed.

> **Phase-45 B10 — GLSL reserved-word uniform on GL (`Noise.fx`, formerly `SD0012`) — FIXED.**
> A free uniform whose name is a GLSL reserved word (e.g. `noise`, which collides with the
> deprecated `noise1`/`noise2`/... builtins) is renamed by SPIRV-Cross (to `_noise`). The GL
> cbuffer-record builder joins the rewriter's uniform layout to the reflected effect-parameter
> list **by name** (`CompilationPipeline.IndexOfParam`); the reflected list still carries the
> original `noise`, so the name join missed and emitted the internal `SD0012`. The fix
> (`CompilationPipeline.IndexOfParamByRegister`) adds an **offset bridge** that runs only on a
> name miss: the GL uniform's `BaseRegister * 16` byte offset locates the reflected `$Globals`
> cbuffer variable, whose ORIGINAL name recovers the parameter index — so the parameter stays
> exposed under `noise` and `effect.Parameters["noise"].SetValue(...)` binds. It is restricted
> to the single-`$Globals` case (the reserved-word case is always a free global); a multi-cbuffer
> shape falls through to keep `SD0012` rather than risk a mis-map (correctness over coverage).
> Because shaders that compile today never hit the name miss, output is byte-identical (the
> cross-host manifest gained only the new fixture's entries, with no existing hash changed).
> Pinned by `ExReservedWordUniform.fx` (GL+DX+FNA), `ReservedWordUniformBridgeTests`, and the
> re-enabled Nez `Noise.fx` GL arm. See `docs/glsl-uniform-naming.md` "Design notes".
