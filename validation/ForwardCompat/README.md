# Forward-compat validation (Phase 35, Area A)

Proves ShadowDusk's **existing, unchanged** v10 OpenGL `.mgfx` output still
**loads into a real `Effect` and renders pixel-equivalent** on a **newer
MonoGame** than the one the product is pinned to — with **zero consumer action**.

This is validation-only. It does **not** change the product:

- `Directory.Packages.props` stays `MonoGame.Framework.DesktopGL` = **3.8.2.1105**.
- `CompilerOptions.MgfxVersion` stays **10**.
- The newer MonoGame is pulled in **only** by this project, via a project-local
  `<PackageReference ... VersionOverride="3.8.4.1" />` (see `ForwardCompat.csproj`
  for why VersionOverride is the cleanest non-invasive choice under the repo's
  central package management). This project is **not** in `ShadowDusk.slnx` and is
  never packed.

## What it does

1. Compiles the SM3 PS-only corpus (the 10 shaders proven in Phase 17) with the
   **actual, unchanged** ShadowDusk `EffectCompiler` → default options → **v10 GL
   `.mgfx`**.
2. Loads those exact bytes into a **real `MonoGame.Framework.DesktopGL` `Effect`
   on 3.8.4.1** and renders the cat offscreen → 10 PNGs in
   `validation/output/forwardcompat/`.
3. Pixel-compares those renders against:
   - **`output/candidate/`** — the *same* ShadowDusk bytes rendered on the
     product-pinned **3.8.2.1105** (the primary proof: same bytes, only the
     runtime version differs), and
   - **`output/baseline/`** — the **mgfxc goldens** rendered on 3.8.2.1105 (holds
     the forward run to the same bar as the original Phase 17 fidelity check).

## How to run

```pwsh
# Full, self-contained regression guard (renders all three sets + compares).
# Exit 0 = forward-compat holds; non-zero = a render failed or images diverged.
pwsh validation/ForwardCompat/run-forwardcompat.ps1

# Just the forward render on 3.8.4.1 (writes output/forwardcompat/*.png):
dotnet run --project validation/ForwardCompat/ForwardCompat.csproj -c Debug

# Just the compare (after the three sets exist):
python validation/compare_forwardcompat.py --vs-baseline
```

Requires a real GPU / DesktopGL context (rung-4 render, like Phase 17/33/34) and
Python with `pillow` + `numpy` for the pixel compare.

## Result — forward-compat matrix (recorded 2026-06-05, Windows DesktopGL)

ShadowDusk compiler unchanged; same v10 `.mgfx` bytes (byte-identical across both
runtimes — they come from one compile). `tolerance = 4/255`.

| Shader     | Compile (ShadowDusk v10) | Load+Render 3.8.2.1105 | Load+Render **3.8.4.1** | vs 3.8.2 render (same bytes) | vs mgfxc golden |
|------------|:------------------------:|:----------------------:|:-----------------------:|:----------------------------:|:---------------:|
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

**10/10** load and render on MonoGame **3.8.4.1**, **pixel-identical** to the
3.8.2.1105 renders of the same bytes (max per-channel delta **0**), and within
tolerance of the mgfxc goldens (max delta ≤ 1, same as the original Phase 17
result). **The existing v10 output works forward unchanged — the consumer does
nothing.**

## Version landscape (verified live 2026-06-05 against nuget.org)

- **3.8.2.1105** — product pin (unchanged).
- **3.8.4.1** — latest **stable** 3.8.x on nuget.org; used here.
- **3.8.5-*** — only `-develop` / `-preview` published (Vulkan + DX12). Not stable;
  not targeted here. When 3.8.5 goes stable, bump `ForwardCompatMonoGameVersion`
  in `ForwardCompat.csproj` (and Areas C/D of Phase 35 unblock).
