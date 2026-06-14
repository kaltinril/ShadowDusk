# validation/KniDesktopGL — KNI v4.02 desktop render validation (Phase 44 D)

The **non-browser KNI render proof.** This harness loads ShadowDusk's `.mgfx` output into a
**real KNI (nkast) runtime on the SDL2.GL desktop backend** and renders it, closing the matrix
gap where KNI was only ever proven in the browser (Phase 24) and on a pre-v4.02 build.

## What it proves

ShadowDusk's **unchanged** `EffectCompiler` (default options -> **MGFX v10 GL**) compiles the
10-shader SM3 PS-only corpus; those exact bytes are loaded into **KNI `Effect` v4.2.9001** (the
KNI v4.02 line) on SDL2.GL and rendered with the standard cat-corpus scene shared with the
MonoGame harnesses. The render is then pixel-compared, **same backend (GL <-> GL), the only
valid kind**, against:

- **the mgfxc goldens** (`validation/output/baseline`) — the reference compiler's output, MonoGame-rendered. This is the product bar: *does ShadowDusk's v10 render in KNI like the reference compiler's output renders?*
- **ShadowDusk -> MonoGame DesktopGL** (`validation/output/candidate`) — the *same bytes* on MonoGame. Arm-vs-arm: only the runtime differs, so any delta is KNI-vs-MonoGame, not compiler-vs-compiler.

## Result (2026-06-14, this machine, real KNI v4.2.9001 SDL2.GL)

**10/10 loaded + rendered.** Pixel comparison (`compare_kni.py`, tolerance 4/255):

| Comparison | Verdict | Max per-channel delta |
|---|---|---|
| KNI render vs **mgfxc golden** | 10/10 MATCH | 0 for 8 shaders; **1** for Scanlines + Dots (driver rounding, well inside 4/255) |
| KNI render vs **ShadowDusk @ MonoGame** (same bytes) | 10/10 MATCH | **0 for all 10** (KNI renders our v10 bytes pixel-identically to MonoGame) |

So ShadowDusk's v10 output is **render-proven on KNI v4.02 desktop**: it loads in real KNI and
renders pixel-equivalent to the reference compiler and pixel-identical to MonoGame. This is the
**reproduce-first** baseline that Phase 35 Area B's KNIFX writer will be validated against on the
same rig (v10 first, then KNIFX output).

## Why this is honest / non-vacuous

- A **runtime-integrity guard** (`Program.cs`) asserts the loaded XNA assembly is KNI's
  (`Xna.Framework.*`, version 4.2.9001.x), **not** MonoGame's (`MonoGame.Framework`), and aborts
  with exit 2 otherwise, so a stray MonoGame assembly can never be mislabeled as a KNI render.
- The render recipe is the **shared** `validation/Shared/*.cs` (compiled against KNI here, against
  MonoGame in `validation/Candidate`), so the scene is identical and any difference is attributable
  to the runtime, not the harness.
- KNI's WebGL render was already proven in Phase 24; this is the **desktop** analogue on the
  **current v4.02 line**, a distinct runtime + GL path.

## How to run

KNI SDL2.GL needs a real desktop OpenGL driver (works on a normal dev machine; CI desktop-GL is a
separate driver story, tracked as Phase 44 C). The harness is **not** in `ShadowDusk.slnx` and is
never packed; it opts out of central package management so the nkast pins stay local to it.

```pwsh
# 1. references (reference compiler + MonoGame render of our bytes)
dotnet run --project validation/Baseline       # mgfxc goldens   -> validation/output/baseline
dotnet run --project validation/Candidate      # ShadowDusk@MonoGame -> validation/output/candidate

# 2. the KNI desktop render (MGFX v10, the default)
dotnet run --project validation/KniDesktopGL   # ShadowDusk@KNI   -> validation/output/kni

# 2b. (optional) the KNIFX v11 render — ShadowDusk's additive KNIFX container in real KNI
dotnet run --project validation/KniDesktopGL -- knifx   # -> validation/output/kni-knifx

# 3. pixel compare (GL <-> GL, tolerance 4/255). If output/kni-knifx exists, also compares
#    KNIFX v11 vs MGFX v10 (both rendered in KNI).
python validation/compare_kni.py
```

## KNIFX v11 render proof (Phase 35 Area B)

Passing `knifx` makes the harness compile each shader with `CompilerOptions.Container = Knifx`
(`KnifxWriter`, signature `KNIF`) and render those bytes in the same real KNI runtime. Result
(2026-06-14): **10/10 KNIFX shaders load + render in real KNI v4.2.9001, maxd 0 vs the v10
render** (`compare_kni.py`). The GLSL is the same MojoShader-dialect code, so for this corpus
the KNIFX picture is identical to v10, the **smoke test** that ShadowDusk's KNIFX container is
valid and loadable. The KNIFX-specific fixes (optimized `Matrix4x4` via `columnsActual`,
sampler-without-texture) are **not** exercised by this corpus and must be validated against a
KNIFXC golden separately (see `plan/PHASE-35-appendix/knifx-format-spec.md`).

`compare_kni.py` exits non-zero if any image is missing or over tolerance, and writes magenta
diffs to `validation/output/kni-diff/` for any over-tolerance pixels.

## Pins

- `nkast.Xna.Framework[.*]` + `nkast.Kni.Platform.SDL2.GL` at **4.2.9001.\*** (KNI v4.02 line; same
  major/minor as the browser sample's `nkast.Kni.Platform.Blazor.GL`). `KniPlatform=DesktopGL` +
  `DESKTOPGL` define, per the KNI DesktopGL template.
- ShadowDusk via `ProjectReference` to `src/ShadowDusk.Compiler` (compiles in-process; deterministic
  bytes, identical to `validation/Candidate`'s).
