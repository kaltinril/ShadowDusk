# Third-party shaders — MonoGame's own test effects

This directory vendors the **reference compiler's own test effects**: the `.fx`/`.fxh` assets
MonoGame builds in its own test suite. They are the closest thing to a statement of *"what
mgfxc must compile"* that exists, and `Tests/Assets/Effects/*.mgcb` records **which effects
MonoGame itself builds for each profile** — including a `Vulkan.mgcb`, which is why they were
vendored (issue #145: ShadowDusk's Vulkan corpus was PS-only, matrix-free and modern-syntax
only, and both bugs in that issue lived in the space these effects cover).

## Upstream project

- **Project:** MonoGame
- **Author / copyright:** Copyright (C) MonoGame Foundation, Inc
- **Repository:** <https://github.com/MonoGame/MonoGame>
- **License:** Microsoft Public License (Ms-PL) — verbatim text in `./LICENSE`
- **Tag fetched (pinned for reproducibility):** `v3.8.5`
- **Upstream directory:** `Tests/Assets/Effects/`
- **Fetched:** 2026-07-22

```sh
curl -sL "https://raw.githubusercontent.com/MonoGame/MonoGame/v3.8.5/Tests/Assets/Effects/<File>" -o <File>
```

## Modifications

**The shader code itself is UNMODIFIED — byte-for-byte identical to upstream**, including
upstream's own copyright header. The ONLY change is a provenance/attribution comment block
**prepended** above it (project, repo, tag, upstream path, license, and a one-line note of what
the file exercises). No shader statement, declaration, technique, profile, or whitespace was
altered.

`Include.fxh` and `PreprocessorInclude.fxh` are vendored because the effects `#include` them;
`Mobile/test.fx` + `Mobile/Macros.fxh` were **not** vendored (a separate mobile macro layer,
out of scope for this pass).

## What MonoGame builds these for

From upstream's own `.mgcb` files at `v3.8.5`:

- **`Vulkan.mgcb` (14):** Bevels, BlackOut, ColorFlip, Grayscale, HighContrast, Invert,
  NoEffect, RainbowH, Instancing, VertexTextureEffect, CustomSpriteBatchEffect,
  CustomSpriteBatchEffectComparisonSampler, TextureArrayEffect, ParameterTypes.
- **`DirectX.mgcb`:** the same set plus `ParserTest`.
- `DirectX12.mgcb` / `OpenGL.mgcb` cover the remaining targets.

## Files vendored, and ShadowDusk's compile status per target

Measured 2026-07-22 with the CLI on this branch (`/Profile:<target>`). "OK" = compiles;
everything else is a **loud, specific diagnostic**, never a crash. Nothing here is silently
skipped — every one of these files is also exercised by
`tests/ShadowDusk.Integration.Tests/Tests/VulkanCorpusStructuralTests.cs`, which requires a
fixture to either produce a structurally valid Vulkan container or fail with a diagnostic.

| File | GL | DX11 | Vulkan | FNA | Notes |
|---|---|---|---|---|---|
| `Bevels.fx` | — | OK | OK | OK | |
| `BlackOut.fx` | — | OK | OK | OK | |
| `ColorFlip.fx` | — | OK | OK | OK | |
| `CustomSpriteBatchEffect.fx` | — | OK | OK | OK | Two texture/sampler pairs at explicit registers |
| `CustomSpriteBatchEffectComparisonSampler.fx` | — | OK | OK | — | FNA: `FX0013` (`SamplerComparisonState` has no SM1-3 lowering) |
| `DefinesTest.fx` | — | — | — | — | Needs `-DMACRO_DEFINE_TEST=3`; deliberately hides invalid syntax behind an undefined `#if` |
| `Grayscale.fx` | — | OK | OK | OK | |
| `HighContrast.fx` | — | OK | OK | OK | |
| `Instancing.fx` | — | OK | OK | OK | **VS-driven, matrix vertex input (`float4x4 : BLENDWEIGHT`)** |
| `Invert.fx` | — | OK | OK | OK | |
| `NoEffect.fx` | — | OK | OK | OK | |
| `ParameterTypes.fx` | — | — | OK | — | DX/FNA: DXC `E5017` (non-constant vector addressing / flatten not implemented) |
| `ParserTest.fx` | — | OK | OK | OK | The reference compiler's own parser torture test |
| `PreprocessorTest.fx` | — | — | — | — | Needs `-DTEST=<n>`; also probes an intentionally malformed `#if foo(TEST)` |
| `RainbowH.fx` | — | OK | OK | OK | |
| `TextureArrayEffect.fx` | — | OK | OK | — | FNA: `FX0013` (`Texture2DArray` has no SM1-3 equivalent) |
| `VertexTextureEffect.fx` | — | — | — | OK | `FX0012` (`tex2Dlod` has no 1:1 modern rewrite) on GL/DX/Vulkan; FNA compiles it natively through vkd3d |

### Why every one of them fails on OpenGL

`Include.fxh` selects its branch on `SM6` / `SM4`; ShadowDusk's OpenGL macro set is
deliberately `{MGFX, GLSL, OPENGL}` with **no shader-model macro**, so these expand to the
legacy DX9 branch (`sampler2D`, `tex2D`, `COLOR0`) inside a *macro body* — where FxPreParser's
legacy-to-modern conversion cannot see the declaration to rewrite it, and DXC then rejects
`sampler2D`. This is the known **GL macro-model gap** (Phase 41 follow-up), not a new defect;
these fixtures now give it concrete, reproducible evidence.

### Bugs these fixtures found on the day they were added (issue #145)

1. **Anonymous techniques were rejected.** `technique { pass { … } }` (no name) is legal FX and
   is what most of these files use; ShadowDusk raised `FX0001: Expected technique name`. Fixed
   in `FxPreParser.ParseTechnique` — verified against mgfxc 3.8.5, which compiles them and
   writes an **empty** technique name into the container. This unlocked 5 more of these
   fixtures on both DirectX_11 and Vulkan.
2. **A native process crash on the FNA path.** `SamplerComparisonState` made vkd3d's SM1
   lowering hit "Unreachable code reached" and take the whole process down with an
   `AccessViolationException`. Now a loud `FX0013` before vkd3d ever sees the source.
3. **The FNA writer was stricter than the reference compiler about technique names.** Once
   anonymous techniques parsed, `SD0302` rejected the empty name they produce — but
   `d3dcompiler_47` at `fx_2_0` accepts an anonymous technique (S_OK, only the usual X4717
   deprecation warning; probed directly). The check now rejects only a NON-ASCII name, which
   put four more of these effects (`CustomSpriteBatchEffect`, `Instancing`, `ParserTest`,
   `VertexTextureEffect`) on the FNA target.
