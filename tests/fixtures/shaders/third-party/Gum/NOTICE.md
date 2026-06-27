# Third-party shaders — Gum

This directory vendors real `.fx` shaders authored in the **Gum** repository
(`vchelaru/gum`, the UI tool by Victor Chelaru / FlatRedBall). They are used as
**compile-level regression inputs** for ShadowDusk's corpus (Phase 49, requested by
vchelaru). They are NOT render-equivalence proofs — see `docs/test-shader-corpus.md`
for what each covers and on which targets.

These are Gum's **sample-project** shaders (Gum's core rendering leans on `SpriteBatch`
+ Apos.Shapes for shapes), but they are genuine, Vic-authored real-world `.fx` and they
exercise syntax the rest of the corpus under-covers — the legacy D3D9 effect-framework
forms, the `level_9_1` profiles, and the `TECHNIQUE()` macro idiom.

## Upstream project

- **Project:** Gum
- **Author / copyright:** Copyright (c) 2013-2024 FlatRedBall, LLC (Victor Chelaru et al.)
- **Repository:** <https://github.com/vchelaru/gum>
- **License:** MIT (verbatim text in `./LICENSE`)
- **Commit fetched (pinned for reproducibility):**
  `771bc5c3d18e97db65a45a803763946d17b7d1ea`
- **Fetched:** 2026-06-27

Each file was fetched with:

```sh
SHA=771bc5c3d18e97db65a45a803763946d17b7d1ea
gh api "repos/vchelaru/gum/contents/<UpstreamPath>?ref=$SHA" --jq '.content' | base64 -d > <File>
```

## Modifications

**The shader code itself is UNMODIFIED, byte-for-byte identical to upstream.** The ONLY
change to each file is a provenance/attribution comment block **prepended** at the top
(project, repo URL, commit SHA, upstream path, license, one-line "what it exercises"
note). No shader statement, declaration, technique, profile, or whitespace inside the
original source was altered. The local filenames differ from upstream (all three
upstream files are named `Shader.fx`/`Grayscale.fx` in different sample folders, so they
are disambiguated here by sample). The `LICENSE` file is the upstream `LICENSE` fetched
verbatim from the same commit.

## Files, upstream path, and classification

`Targets` = the delivery targets each shader compiles on through ShadowDusk, classified
by an **actual compile probe** on 2026-06-27. A target NOT listed fails for the
documented reason.

| Local file | Upstream path | License | Targets (compile) | Why a target is excluded |
|---|---|---|---|---|
| `MonoGameInCode-Grayscale.fx` | `Samples/MonoGameGumInCode/MonoGameGumInCode/Content/Grayscale.fx` | MIT | **GL + DX + FNA** (all-runtime) | (none) — `vs/ps_4_0_level_9_1`, `Texture2D` + `sampler2D` + `sampler_state`, `: COLOR0` output, PS-only technique, dot-luminance. |
| `KniInCode-Shader.fx` | `Samples/KniGumInCode/KniGumInCodeContent/Shader.fx` | MIT | **FNA only** | **GL + DX:** the file uses legacy D3D9 **effect-framework syntax** — `uniform extern texture CurrentTexture;`, `sampler_state { Texture = <CurrentTexture>; ... }`, and lowercase `pixelshader = compile ps_2_0 ...`. DXC rejects this on the modern HLSL path (`warning X0000: effect object ignored - effect syntax is deprecated [-Weffects-syntax]` → compile fails). Only the FNA / `fx_2_0` path (native legacy effects via vkd3d) accepts it — same shape as Nez `PaletteCycler` being FNA-only. A legitimate SM/dialect limit, not a parser defect. |
| `FnaSample-Shader.fx` | `Samples/FnaGum/FnaSample/Content/Shader.fx` | MIT | **none (known-failure — Phase 41 GAP-1)** | **GL + FNA:** rejected with **`SD0010` "Effect source contains no techniques"** because the technique is defined entirely inside a `#define TECHNIQUE(name, psname) technique name { ... }` **macro**, and `FxPreParser` counts techniques BEFORE the preprocessor expands macros. **DX:** fails `X0000` (same root cause — no real technique reaches the backend). This is the **Phase 41 GAP-1** product gap, surfaced by a real Vic-authored shader. It is **pinned as a known-failure test** (`GumFnaSampleShader_MacroTechnique_CurrentlyRejectedBy_SD0010_Phase41Gap1` in `ThirdPartyShaderCorpusTests`) so the gap is exercised and flips loudly when GAP-1 is fixed. |

**What `FnaSample-Shader.fx` additionally exercises (once GAP-1 lets it through):** the
`TECHNIQUE()` / `SAMPLE()` `#define` macro idiom, legacy `uniform extern texture`,
`sampler_state { Texture = <...> }`, a `float4x4 ... : VIEWPROJ` matrix semantic,
`: COLOR` PS outputs, `vs_1_1`/`ps_2_0` profiles, premultiply-alpha + linearize helpers,
and many blend-mode passes (Add/Subtract/Modulate/Inverse/Color/Interpolate, with
color-modifier and linear variants).

## How these are exercised

`tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs` compile-asserts each
shader on exactly its classified targets:

- `MonoGameInCode-Grayscale.fx` → GL + DX + FNA (and folded into
  `FnaCompileFixtureTests.Sm3Corpus()` as an all-runtime SM3 cell).
- `KniInCode-Shader.fx` → FNA only (via `[FnaTheory]` + the MojoShader-rule fx_2_0
  validator).
- `FnaSample-Shader.fx` → the dedicated **known-failure pin** (asserts the current
  `SD0010` GAP-1 behavior; see the test's comment for how to promote it after the fix).

The `Phase41StructuralDivergenceMatrixTests` GL+DX census auto-globs this directory, so
every file also gets a GL+DX structural-census cell (passing or failing-with-a-code).

**Scope:** a green compile to a well-formed container is the bar here — NOT a
pixel-equivalence claim to `mgfxc`/`fxc`. There are no committed goldens for these
shaders.
