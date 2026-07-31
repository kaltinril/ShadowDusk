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
| `KniInCode-Shader.fx` | `Samples/KniGumInCode/KniGumInCodeContent/Shader.fx` | MIT | **FNA only** | **GL:** the file uses legacy D3D9 **effect-framework syntax** — `uniform extern texture CurrentTexture;`, `sampler_state { Texture = <CurrentTexture>; ... }`, and lowercase `pixelshader = compile ps_2_0 ...`. DXC rejects this on the modern HLSL path (`warning X0000: effect object ignored - effect syntax is deprecated [-Weffects-syntax]` → compile fails). Only the FNA / `fx_2_0` path (native legacy effects via vkd3d) accepts it — same shape as Nez `PaletteCycler` being FNA-only. A legitimate SM/dialect limit, not a parser defect. **DX** now declines it earlier and more precisely, with **`SD0015`** (since 2026-07-31, Phase 51 A10): `compile vs_2_0` is below MonoGame's `DirectX_11` floor, which is also what real `mgfxc /Profile:DirectX_11` reports. |
| `FnaSample-Shader.fx` | `Samples/FnaGum/FnaSample/Content/Shader.fx` | MIT | **none, but for honest per-target reasons (GAP-1 fixed on FNA)** | The technique is defined entirely inside a `#define TECHNIQUE(name, psname) technique name { ... }` **macro**. `FxPreParser` counts techniques BEFORE macro expansion = **Phase 41 GAP-1**. **GAP-1 is now fixed on the FNA path** (the zero-technique macro recovery extended to `RunFna`): FNA recovers the macro technique and then declines the shader's **`vs_1_1`** vertex profile with **`SD0300`** (ShadowDusk's documented FNA SM2 floor) - not `SD0010`. **GL** keeps `SD0010` (the GL macro-model gap - GL is gated out of recovery because the legacy DX9/SM2 expansion crashes DXC SPIR-V codegen). **DX** declines it with **`SD0015`** (since 2026-07-31, Phase 51 A10): its `vs_1_1` is below MonoGame's `DirectX_11` profile floor of SM 4.0 level 9.1, which is exactly what real `mgfxc /Profile:DirectX_11` reports for this file. It previously surfaced as an unlabelled `X0000` from further down the pipeline. Pinned by `GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap` (GL) and `Phase41MacroTechniqueTests.Fna_GumFnaSample_MacroRecovered_ThenRejectsVs11_Sd0300_NotSd0010` (FNA). |

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
- `FnaSample-Shader.fx` → its **GL** `SD0010` is pinned by
  `GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap` (the GL macro-model
  gap), and its **FNA** `SD0300` (GAP-1 recovered, then declined for `vs_1_1`) by
  `Phase41MacroTechniqueTests.Fna_GumFnaSample_MacroRecovered_ThenRejectsVs11_Sd0300_NotSd0010`.

The `Phase41StructuralDivergenceMatrixTests` GL+DX census auto-globs this directory, so
every file also gets a GL+DX structural-census cell (passing or failing-with-a-code).

**Scope:** a green compile to a well-formed container is the bar here — NOT a
pixel-equivalence claim to `mgfxc`/`fxc`. There are no committed goldens for these
shaders.
