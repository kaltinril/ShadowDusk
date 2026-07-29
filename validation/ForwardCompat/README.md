# Forward-compat version matrix (Phase 35, Area A)

Proves ShadowDusk's **existing, unchanged** v10 OpenGL `.mgfx` output
**loads into a real `Effect` and renders pixel-identically on every MonoGame
version that can load it** — currently **seven consecutive releases, 3.8.1.263
through 3.8.5** — with **zero consumer action**.

This is validation-only. It does **not** change the product:

- `Directory.Packages.props` stays `MonoGame.Framework.DesktopGL` = **3.8.2.1105**.
- `CompilerOptions.MgfxVersion` stays **10**.
- Each newer MonoGame is pulled in **only** by this project, per-run, via a
  project-local `<PackageReference ... VersionOverride="$(ForwardCompatMonoGameVersion)" />`
  (see `ForwardCompat.csproj` for why VersionOverride is the cleanest non-invasive
  choice under the repo's central package management). This project is **not** in
  `ShadowDusk.slnx` and is never packed.

## What it does

One parametrized harness, built+run **once per version in the matrix**. For each
version it:

1. Compiles the SM3 PS-only corpus (the 10 shaders proven in Phase 17) with the
   **actual, unchanged** ShadowDusk `EffectCompiler` → default options → **v10 GL
   `.mgfx`** (byte-identical across versions — it's one compile per shader).
2. Loads those exact bytes into a **real `MonoGame.Framework.DesktopGL` `Effect`
   of that version** and renders the cat offscreen → 10 PNGs in
   `validation/output/versionmatrix/<version>/`. A **runtime-integrity guard**
   fails the cell if the loaded MonoGame version doesn't match the requested one
   (so a `VersionOverride` that silently didn't take effect can't pass).
3. The compare step then checks:
   - **forward-compat** — every version's renders are **pixel-identical** to the
     floor (`3.8.2.1105`): same bytes, only the runtime differs; and
   - **fidelity** — every version is within tolerance of the **mgfxc goldens**
     (`output/baseline/`), the same bar as the original Phase 17 check.

## How to run

```pwsh
# Full, self-contained regression guard (renders every version + baseline, compares).
# Exit 0 = matrix holds; non-zero = a render failed or images diverged.
pwsh validation/ForwardCompat/run-forwardcompat.ps1

# Override the matrix (first entry is the forward-compat reference floor):
pwsh validation/ForwardCompat/run-forwardcompat.ps1 -Versions 3.8.1.263,3.8.5 -Tolerance 4

# Just one cell (writes output/versionmatrix/3.8.5/*.png):
$env:MATRIX_VERSION_LABEL='3.8.5'
dotnet run --project validation/ForwardCompat/ForwardCompat.csproj -c Debug -p:ForwardCompatMonoGameVersion=3.8.5

# Just the compare (after the cells + baseline exist):
python validation/compare_forwardcompat.py --versions 3.8.1.263 3.8.2.1105 3.8.5 --vs-baseline
```

Requires a real GPU / DesktopGL context (rung-4 render, like Phase 17/33/34) and
Python with `pillow` + `numpy` for the pixel compare.

**Extending the matrix:** append the NuGet version string to `-Versions` when a new
MonoGame ships. The first entry is the forward-compat reference floor — the *oldest*
runtime that accepts MGFX v10 (see the floor measurement below); prepending anything
older than `3.8.1.263` will fail for a real MonoGame reason.

## Result — the FULL supported range (swept 2026-07-28, Windows DesktopGL)

ShadowDusk compiler unchanged; the **same v10 `.mgfx` bytes** in every cell (one compile
per shader, then seven runtimes load those identical bytes). `tolerance = 4/255`. Every
loaded runtime was confirmed by the integrity guard.

**7 versions × 10 shaders = 70 renders, all green.**

| Shader     | 3.8.1.263 (floor) | 3.8.1.303 | 3.8.2.1105 | 3.8.3 | 3.8.4 | 3.8.4.1 | 3.8.5 | each vs floor | each vs mgfxc golden |
|------------|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:-------------:|:--------------------:|
| Grayscale  | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Invert     | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| TintShader | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Sepia      | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Saturate   | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Pixelated  | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Scanlines  | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 1) |
| Fading     | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Dots       | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 1) |
| Dissolve   | OK | OK | OK | OK | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |

One build of the output renders **pixel-identically on every MonoGame release that can
load it** (max per-channel delta **0** between runtimes), and every cell stays within
tolerance of the mgfxc goldens (`Scanlines` and `Dots` at 1/255, the other eight at 0 —
unchanged from the original Phase 17 result). **The consumer does nothing.**

## The floor is measured, not assumed

Every stable `MonoGame.Framework.DesktopGL` release was probed on 2026-07-28 by running
this harness against it:

| MonoGame | v10 `.mgfx` loads + renders? |
|---|---|
| 3.8.0.1641 | ❌ **0/10** — `new Effect()` throws *"This MGFX effect seems to be for a newer release of MonoGame."* Its loader predates MGFX v10. |
| **3.8.1.263 → 3.8.5** (7 releases) | ✅ **10/10 each**, all pixel-identical |

So **3.8.1.263 is the true floor** and is the matrix's forward-compat reference. 3.8.0's
rejection is a real MonoGame version boundary, not a ShadowDusk defect — it is recorded
here so nobody has to rediscover it, and so the floor is a measured fact rather than a
number someone once picked.

> **This matrix is not tied to any single MonoGame version.** `Directory.Packages.props`
> names `3.8.2.1105`, but that is only the default the *other* GL harnesses render
> against — no project referencing it is even in `ShadowDusk.slnx`, and no shipped
> `ShadowDusk.*` package depends on MonoGame at all. The product's actual commitment is
> the **output format (MGFX v10)**, and this matrix is what establishes its range.

## Version landscape (verified live 2026-07-28 against nuget.org)

- Stable DesktopGL releases: 3.5.0.1678, 3.5.1.1679, 3.6.0.1625, 3.7.0.1708, 3.7.1.189,
  3.8.0.1641, 3.8.1.263, 3.8.1.303, 3.8.2.1105, 3.8.3, 3.8.4, 3.8.4.1, **3.8.5**.
  3.7.x and older are pre-`net8.0`-era and out of scope; 3.8.0 is the measured
  floor-minus-one above; everything from 3.8.1.263 up is in the matrix.
- **3.8.5** — shipped **STABLE 2026-07-15**, latest on nuget.org, in the matrix since
  2026-07-28. The classic `MonoGame.Framework.DesktopGL` package continues at 3.8.5, so
  this harness needed no structural change — only the version string. Source-verified
  that its loader accepts the MGFX range `[10, 11]` (it adds `MGFXMinVersion = 10`), and
  now render-verified too.
- 3.8.5 also introduced the additive `MonoGame.Framework.Native` + `MonoGame.Runtime.*`
  architecture (`DesktopVK`, `WindowsDX12`). Those are separate targets with their own
  harnesses (`validation/CandidateVulkan`, `validation/CandidateDx12`), not cells here —
  this matrix is specifically about the classic DesktopGL runtime loading unchanged v10.
