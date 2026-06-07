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
