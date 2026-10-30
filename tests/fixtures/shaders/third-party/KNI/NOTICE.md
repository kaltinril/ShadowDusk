# Third-party shaders - KNI's own effect test assets

This directory vendors the test effects of **KNI's own effect test suite**,
`Tests/Framework.Graphics/EffectTest.cs`, which KNI's author wrote and checked against XNA. They are
the closest statement there is of how an XNA-compatible runtime expects an effect's parameters,
textures and samplers to behave after `EffectPass.Apply()`, and they exercise legacy XNA effect
syntax (`sampler2D` with `sampler_state`, texture-less samplers, `vs_2_0`/`ps_2_0`) that the other
corpora barely touch. They feed the `validation/KniEffectTest*` render gate (see
`docs/validation-matrix.md` section 6), which ports every test of that file to a real engine.

## Upstream project

- **Project:** KNI (a fork of MonoGame)
- **Author / copyright:** Copyright (c) 2014-2025 Nick Kastellanos (KNI); Copyright (c) 2009-2021 The
  MonoGame Team; portions Copyright (c) The Mono.Xna Team (MIT)
- **Repository:** <https://github.com/kniEngine/kni>
- **License:** Microsoft Public License (Ms-PL), with the MIT portion for Mono.Xna - KNI's
  `LICENSE.txt`, copied verbatim to `./LICENSE`
- **Commit fetched (pinned for reproducibility):** `1dda67cf2fc7c0cc68ea87ebc905f6ebb5d99468`
  (`main`, 2026-10-25). The effect assets last changed upstream in `611d1957` (2026-08-09) and
  `EffectTest.cs` in `80431200` (2025-08-31).
- **Upstream directory:** `Tests/Assets/Effects/`
- **Fetched:** 2026-10-28. Every file was checked against the upstream tree at that commit by git
  blob SHA (all identical). For the `.fx` files that holds for the bytes BELOW the prepended
  provenance header (the vendored file is header + upstream bytes); the `.xnb` files and `LICENSE`
  match upstream as committed.

```sh
curl -sL "https://raw.githubusercontent.com/kniEngine/kni/1dda67cf2fc7c0cc68ea87ebc905f6ebb5d99468/Tests/Assets/Effects/<File>" -o <File>
```

## What was taken

- The 11 `.fx` files `EffectTest.cs` loads: `OldSyntax_x`, `OldSyntax_s0s1`, `OldSyntax_t0t1`,
  `OldSyntax_t1t0`, `OldSyntax_s0s1t0t1`, `DeferredBasicEffect`, `NewSyntax_t0t1`,
  `NewSyntax_s0s1t0t1`, `ComputeShader`, `OptimizedMatrix`, `OptimizedMatrixArray`.
- KNI's own prebuilt reference builds of eight of them, `DirectX/*.xnb` and `OpenGL/*.xnb`
  (upstream `Tests/Assets/Effects/DirectX/` and `OpenGL/`), loaded by the gate through a real KNI
  `ContentManager` as a reference arm. They are compiled form; they are redistributed under the
  same Ms-PL, unmodified. Upstream's `XNA/*.xnb` were **not** taken (no runtime here can load them).
  They load only on **KNI 4.3.9001**: KNI 4.2.9001 refuses every one with "This effect seems to be
  for a newer version of KNI", so on the 4.2 runs that arm prints only that. On KNI 4.3.9001
  WinForms.DX11, KNI's own `OldSyntax_t1t0.xnb` binds the two textures SWAPPED
  (`Textures[0]=texture1`; KNI's test file carries a TODO for it) and its `DeferredBasicEffect.xnb`
  binds none, where ShadowDusk's DX build passes both; the OpenGL ones pass every check.
- `EffectTest.cs` itself was **not** vendored; the gate's driver (`validation/SharedKniEffectTest`)
  is a port of its assertions, written for this repository.

## Modifications

**The shader code itself is UNMODIFIED - byte-for-byte identical to upstream.** The ONLY change to
each `.fx` is a provenance/attribution comment block **prepended** above it (project, repo,
commit, upstream path, license, and a one-line note of what the file exercises). Upstream's own
attribution comment in `ComputeShader.fx` (the code's origin in cpt-max's compute fork) is kept.
The `.xnb` files and `LICENSE` are byte-identical to upstream.

## Licence obligations (Ms-PL section 3) and how they are met

- **3(C) retain all copyright, patent, trademark and attribution notices:** the upstream files
  carry no notice of their own besides `ComputeShader.fx`'s attribution line, which is retained;
  KNI's and MonoGame's copyright lines are reproduced in `./LICENSE` and in every prepended header.
- **3(D) source-form distribution only under Ms-PL, with a complete copy of the licence:** the
  complete `LICENSE.txt` is `./LICENSE`; these files are distributed under it and nothing here
  relicenses them. The compiled `.xnb` files are distributed under the same licence.
- **3(A) no trademark licence:** "KNI" and "MonoGame" are used only to identify the source.
- Nothing in this directory is shipped in a `ShadowDusk.*` package; it is test input only.

## What the gate measures on them

The full per-runtime table (which rows are gated, which are known ShadowDusk bugs and which are
runtime limits) is printed by each `validation/KniEffectTest*` driver and summarised in
`docs/validation-matrix.md` section 6. In short, measured 2026-10-28:

- Passing and gated on every runtime that can run them: the texture/sampler binding of
  `OldSyntax_t0t1`, `OldSyntax_t1t0`, `OldSyntax_s0s1t0t1`, `NewSyntax_t0t1`, `NewSyntax_s0s1t0t1`,
  the render of the two `t0t1`/`t1t0` cases, the sampler states of `OldSyntax_x`/`OldSyntax_s0s1`,
  `DeferredBasicEffect` on DirectX 11, and the matrix round trips of `OptimizedMatrix(Array)`.
- On DirectX 11, `ps_2_0`/`vs_2_0` is refused with `SD0015` for MGFX, exactly as `mgfxc` refuses it;
  the gated DirectX build applies KNI's own profile substitution (`s_2_0` -> `s_4_0_level_9_1`,
  `ShaderProfileDX11`).
- Known ShadowDusk bugs (listed rows that must keep failing until their fix flips them):
  KNIFX on DirectX refuses `ps_2_0`/`ps_3_0` and records `_level_9_x` as SM 4.0 (PR #430); KNIFX
  exposes a texture-less legacy sampler as a parameter; OpenGL refuses `BINORMAL`/`TANGENT` vertex
  inputs (`DeferredBasicEffect`).
- Informational: `ComputeShader.fx` is refused with `FX0014` (no runtime can load a compute
  stage); `OptimizedMatrix`'s default reads as the HLSL initializer, not XNA's transpose (a
  deliberate decision); MGFX exposes texture-less samplers as parameters, as `mgfxc` does.

The compile corpora (`Phase41StructuralDivergenceMatrixTests`, `VulkanCorpusStructuralTests`, the
explicit-slot and reservation sweeps, the byte-identity manifests) pick these files up like any
other fixture.
