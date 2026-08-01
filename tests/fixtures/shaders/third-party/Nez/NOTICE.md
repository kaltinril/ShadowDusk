# Third-party shaders — Nez

This directory vendors real, shipping `.fx` shaders from the **Nez** MonoGame
framework, used as **compile-level regression inputs** for ShadowDusk's corpus
(issue #106 / Phase 45 follow-up). They are NOT render-equivalence proofs — see
`docs/test-shader-corpus.md` for what each one covers and on which targets.

## Upstream project

- **Project:** Nez
- **Author / copyright:** Copyright (c) 2016 Mike
- **Repository:** <https://github.com/prime31/Nez>
- **License:** MIT (verbatim text in `./LICENSE`)
- **Commit fetched (pinned for reproducibility):**
  `6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c`
- **Upstream directory:** `DefaultContentSource/effects/`
- **Fetched:** 2026-06-17

Each file was downloaded with:

```sh
SHA=6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
curl -sL "https://raw.githubusercontent.com/prime31/Nez/$SHA/DefaultContentSource/effects/<File>.fx" -o <File>.fx
```

## Modifications

**The shader code itself is UNMODIFIED — byte-for-byte identical to upstream.**

The ONLY change to each `.fx` file is a provenance/attribution comment block
**prepended** at the top of the file (project, repo URL, commit SHA, upstream
path, license, and a one-line note of what it exercises). No shader statement,
declaration, technique, profile, or whitespace inside the original source was
altered, reformatted, or removed. The upstream files carried no copyright header
of their own, so this header was added per ShadowDusk's vendoring convention
without touching the code below it.

(The `LICENSE` file in this directory is the upstream `LICENSE` fetched verbatim
from the same commit, unchanged.)

## Files vendored, upstream path, and ShadowDusk classification

`Upstream path` is `DefaultContentSource/effects/<File>` for every row.
`Targets` = the delivery targets the shader compiles on through ShadowDusk
(OpenGL = MonoGame-GL / KNI, DirectX_11 = MonoGame-DX, FNA = D3D9 fx_2_0),
verified 2026-06-17, **re-verified and revised 2026-07-31**. A target NOT listed fails by a
**legitimate** shader-model limitation (noted), not a ShadowDusk parser defect. (`Noise.fx`
was the one ShadowDusk-bug exception; Phase 45 B10 fixed it, so it now compiles on GL too.)

> **2026-07-31 — the DirectX column collapsed (Phase 51 A10), and it is correct.** These
> shaders name a legacy `ps_2_0`/`ps_3_0` (or `vs_2_0`) compile target with no
> `#if OPENGL … #else …` header. MonoGame's `DirectX_11` shader profile refuses anything below
> SM 4.0 level 9.1, and real `mgfxc /Profile:DirectX_11` was verified to fail every row marked
> `SD0015` below. ShadowDusk used to accept them; it now matches the reference compiler.
> **Nez targets DesktopGL**, so this is upstream's correct choice, not a defect on either side
> — and the fix a user applies is the standard cross-platform header, which the diagnostic
> names. These rows are still exercised, as an asserted DirectX **reject** set.

| File | License | Targets (compile) | Why a target is excluded |
|---|---|---|---|
| `Bevels.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `BloomCombine.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `BloomExtract.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `GaussianBlur.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `HeatDistortion.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `Letterbox.fx` | MIT | GL + FNA | DX: `compile ps_3_0`, below the `DirectX_11` floor (`SD0015`). (Uses VPOS — compiles on GL, render-equivalence of VPOS not claimed.) |
| `PixelGlitch.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `SpriteBlinkEffect.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `SpriteLines.fx` | MIT | GL + FNA | DX: `compile ps_3_0`, below the `DirectX_11` floor (`SD0015`). (Uses VPOS + float `%` — compiles on GL, render-equivalence not claimed.) |
| `Twist.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `Vignette.fx` | MIT | GL + FNA | DX: the pass names a legacy `ps_2_0`/`ps_3_0` compile target, below MonoGame's `DirectX_11` floor (`SD0015`; mgfxc refuses it too) |
| `Crosshatch.fx` | MIT | FNA only | GL: `int crossHatchSize;` integer uniform is not modelled on the MonoGame-GL target (loud `SD0210`, by design — MojoShader puts ints in a separate register set ShadowDusk does not emit). DX: `compile ps_3_0`, below the `DirectX_11` floor (`SD0015`). |
| `PaletteCycler.fx` | MIT | FNA only | GL + DX: `tex1D` has no 1:1 modern `Texture` method, so it is rejected with a targeted `FX0012` that explicitly directs the user to the FNA target (which compiles it natively). A documented SM limitation, not a defect. |
| `Reflection.fx` | MIT | none (reject-only) | GL: the multi-`TEXCOORD` interpolant cbuffer cannot be expressed in std140/std430 by SPIRV-Cross (`SD0100`). FNA: an int-typed/relational construct hits the known vkd3d 1.17 SM <= 3 gap (`X0000`). DX (since 2026-07-31): `compile vs_2_0`, below the `DirectX_11` floor (`SD0015`). All three are real limits on this shader, not parser defects; it is retained as a reject-set fixture. |
| `Noise.fx` | MIT | GL + FNA | A uniform literally named `noise` is renamed to `_noise` by SPIRV-Cross (GLSL reserved word). This used to break the GL cbuffer/parameter join (`SD0012`), so the shader was wired DX + FNA only. **Phase 45 B10 fixed it** (the join now falls back to an offset bridge that recovers the parameter by byte offset, keeping it named `noise`), so `Noise.fx` compiles on GL too. DX: `compile ps_3_0`, below the `DirectX_11` floor (`SD0015`). |

## How these are exercised

`tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs` compile-asserts
each shader on exactly its classified targets (GL/DX via the MGFX pipeline,
FNA via `[FnaTheory]` + the MojoShader-rule fx_2_0 validator). The all-runtime
subset is also folded into `FnaCompileFixtureTests.Sm3Corpus()`. The
`Phase41StructuralDivergenceMatrixTests` GL+DX structural census auto-globs this
directory, so every file additionally gets a GL+DX compile-census cell.
