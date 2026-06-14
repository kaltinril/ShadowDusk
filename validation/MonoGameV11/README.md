# validation/MonoGameV11 — MGFX v11 render validation (Phase 35 Area B)

Proves ShadowDusk's **MGFX v11** output loads + renders in a **v11-capable MonoGame** runtime.

## What it proves

MonoGame **3.8.5** (develop/preview line) is the version that shipped MGFX v11 (PR #8813: the per-shader
`SourceFile` + `Entrypoint` diagnostic strings; its `Effect` loader accepts version range `[10, 11]`). This
harness compiles the 10-shader SM3 PS-only corpus with ShadowDusk's `EffectCompiler` at **`MgfxVersion = 11`**
(or 10 via the `v10` arg) and renders those bytes in **real `MonoGame.Framework.DesktopGL 3.8.5-preview.6`**.

A malformed v11 file (e.g. the old header-byte-only stub) throws on `new Effect(...)` because the reader's
`ReadString()` desyncs the stream, so **10/10 load + render is the proof the v11 byte stream is correct.**
Comparing the v11 render to the v10 render in the same runtime confirms the diagnostic-only strings don't
change the picture.

## Result (2026-06-14, real MonoGame 3.8.5.0)

**10/10 load + render.** `compare_mgfxv11.py` (tolerance 4/255):

| Comparison | Verdict | Max delta |
|---|---|---|
| MGFX v11 vs MGFX v10 (both rendered in MonoGame 3.8.5) | 10/10 MATCH | **0 for all 10** |
| MGFX v11 vs the mgfxc v10 goldens (cross-runtime, 3.8.5 vs 3.8.2) | 10/10 MATCH | 0 except Scanlines/Dots = 1 (driver rounding) |

So ShadowDusk emits a **faithful MGFX v11** that loads + renders in real MonoGame 3.8.5, pixel-equivalent to
v10. A runtime-integrity guard asserts the loaded runtime is MonoGame >= 3.8.5 (v11-capable), not KNI/3.8.2.

## How to run

3.8.5 is **pre-release** — this is validation only, NOT the product baseline (still 3.8.2.1105 / v10). The
project is not in `ShadowDusk.slnx` and opts out of central package management so the preview pin stays local.

```pwsh
dotnet run --project validation/MonoGameV11           # MGFX v11 -> output/mgfx-v11
dotnet run --project validation/MonoGameV11 -- v10    # MGFX v10 -> output/mgfx-v10-385 (same runtime)
python validation/compare_mgfxv11.py
```

Format spec: [`plan/PHASE-35-appendix/mgfx-v11-format-spec.md`](../../plan/PHASE-35-appendix/mgfx-v11-format-spec.md).
