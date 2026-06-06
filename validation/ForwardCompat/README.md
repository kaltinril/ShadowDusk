# Forward-compat version matrix (Phase 35, Area A)

Proves ShadowDusk's **existing, unchanged** v10 OpenGL `.mgfx` output
**loads into a real `Effect` and renders pixel-identically on every MonoGame
version we support** — the product's pinned floor *and* the latest stable — with
**zero consumer action**.

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
pwsh validation/ForwardCompat/run-forwardcompat.ps1 -Versions 3.8.2.1105,3.8.4.1 -Tolerance 4

# Just one cell (writes output/versionmatrix/3.8.4.1/*.png):
$env:MATRIX_VERSION_LABEL='3.8.4.1'
dotnet run --project validation/ForwardCompat/ForwardCompat.csproj -c Debug -p:ForwardCompatMonoGameVersion=3.8.4.1

# Just the compare (after the cells + baseline exist):
python validation/compare_forwardcompat.py --versions 3.8.2.1105 3.8.4.1 --vs-baseline
```

Requires a real GPU / DesktopGL context (rung-4 render, like Phase 17/33/34) and
Python with `pillow` + `numpy` for the pixel compare.

**Extending the matrix:** add the NuGet version string to `-Versions` (e.g. a
future `3.8.5` stable). The first entry is the forward-compat reference floor and
must stay `3.8.2.1105` (the product's compat promise).

## Result — version matrix (recorded 2026-06-05, Windows DesktopGL)

ShadowDusk compiler unchanged; same v10 `.mgfx` bytes across all cells (one compile
per shader). `tolerance = 4/255`. Loaded runtimes verified by the integrity guard:
`3.8.2.1105` and `3.8.4.1+f3420072…`.

| Shader     | Compile (ShadowDusk v10) | Render 3.8.2.1105 (floor) | Render **3.8.4.1** | 3.8.4.1 vs floor (same bytes) | each vs mgfxc golden |
|------------|:------------------------:|:-------------------------:|:------------------:|:-----------------------------:|:--------------------:|
| Grayscale  | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Invert     | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| TintShader | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Sepia      | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Saturate   | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Pixelated  | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Scanlines  | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 1) |
| Fading     | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |
| Dots       | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 1) |
| Dissolve   | OK | OK | OK | MATCH (maxΔ 0) | MATCH (maxΔ 0) |

**10/10** load and render on **both** MonoGame **3.8.2.1105** and **3.8.4.1**,
**pixel-identical** between the two runtimes (max per-channel delta **0**), and
within tolerance of the mgfxc goldens (max delta ≤ 1, same as the original Phase 17
result). **The existing v10 output works forward unchanged — the consumer does
nothing.**

## Version landscape (verified live 2026-06-05 against nuget.org)

- **3.8.2.1105** — product pin / matrix floor (unchanged).
- **3.8.4.1** — latest **stable** 3.8.x on nuget.org; in the matrix.
- **3.8.5-*** — only `-develop` / `-preview` published (Vulkan + DX12). Not stable;
  not in the matrix (don't gate on a preview). When 3.8.5 goes stable, add it to
  `-Versions` (and Areas C/D of Phase 35 unblock). Source-verified that 3.8.5's
  loader accepts the MGFX range `[10, 11]` (it adds `MGFXMinVersion = 10`), so our
  v10 output stays forward-safe into 3.8.5 too.
