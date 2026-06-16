# validation/KniWinFormsDX — KNI v4.02 DirectX render validation (Phase 44 D)

The **KNI DirectX render proof.** This harness loads ShadowDusk's `.mgfx` DirectX output into a
**real KNI (nkast) runtime on the WinForms.DX11 backend** and renders it, closing the matrix's
last honest KNI gap: KNI DirectX was previously only "likely loads" (the bytes equal MonoGame's,
but had never been load/render-tested in KNI itself).

## What it proves

ShadowDusk's **unchanged** `EffectCompiler` (`PlatformTarget.DirectX` -> **DXBC SM5 in an MGFX v10
container**) compiles the 10-shader SM5 PS-only corpus; in **one real KNI `Effect` v4.2.9001** (the
KNI v4.02 line) on WinForms.DX11, the harness loads **both** those bytes (candidate) **and** the
committed mgfxc DirectX goldens (`tests/fixtures/golden/DirectX_11/*.mgfx`, the control), renders
each through the **identical** SpriteBatch path, and pixel-compares the two arms **in process**.
Same backend (DX11 <-> DX11), same scene, only the compiler that produced the bytes differs.

## Result (2026-06-15, this machine, real KNI v4.2.9001 WinForms.DX11)

**10/10 loaded + rendered + matched mgfxc.** Per-channel delta vs the mgfxc golden render:

| Shader | Verdict | Max per-channel delta |
|---|---|---|
| Grayscale, Invert, TintShader, Sepia, Saturate, Pixelated, Scanlines, Fading, Dissolve | PASS | **0** |
| Dots | PASS | **1** (384 px, driver rounding, well inside the 4/255 bar) |

So ShadowDusk's shipping DirectX output is **render-proven on KNI v4.02**: it loads in a real KNI
DX11 `Effect` and renders pixel-equivalent to the reference compiler's DX output on the same
runtime. Matrix §1 KNI/DirectX cell -> ✅.

## Why this is honest / non-vacuous

- A **runtime-integrity guard** (`Program.cs`) asserts the loaded XNA assembly is KNI's
  (`Xna.Framework.*`, version 4.2.9001.x), **not** MonoGame's (`MonoGame.Framework`), and aborts
  with exit 2 otherwise, so a stray MonoGame assembly can never be mislabeled as a KNI render.
- The **golden arm is a control**: if the mgfxc golden failed to load in KNI DX11, the row fails as
  a "control failure", so a green run means KNI genuinely loaded *both* compilers' bytes.
- The render inputs are the **shared** `validation/SharedDx/DxShaderInputs.cs` (the same shader
  list + by-name params used by the MonoGame DX harnesses), so the scene is identical and any
  difference is attributable to the compiler, not the harness.

## How to run

KNI WinForms.DX11 needs a real DX11 desktop (works on a normal Windows dev machine; DX-render-in-CI
is a separate driver story, tracked as Phase 44 C). WinForms.DX11 is the **only** KNI DX platform
published at 4.2.9001 (no SDL2.DX11), so this is `net8.0-windows` + `UseWindowsForms` + an
`[STAThread]` `Main`. The harness is **not** in `ShadowDusk.slnx` and is never packed; it opts out
of central package management so the nkast pins stay local to it.

```pwsh
# Self-contained: compiles, loads both arms, renders, and pixel-compares in process.
# Exit 0 iff every row loads on both arms and matches within tolerance (default 4).
dotnet run --project validation/KniWinFormsDX -c Release

# Stricter bar (e.g. exact match except documented driver rounding):
dotnet run --project validation/KniWinFormsDX -c Release -- --tolerance 1
```

PNGs for both arms land in `validation/output-dx/kni/` (gitignored) for offline inspection.

## Pins

- `nkast.Xna.Framework[.*]` + `nkast.Kni.Platform.WinForms.DX11` at **4.2.9001.\*** (KNI v4.02 line;
  same major/minor as the desktop-GL harness's SDL2.GL and the browser sample's Blazor.GL).
  `KniPlatform=WindowsDX` + `WINDOWSDX` define, per the KNI WindowsDX template.
- ShadowDusk via `ProjectReference` to `src/ShadowDusk.Compiler` (compiles in-process; deterministic
  bytes, identical to `validation/CandidateDx`'s). DX compilation uses the pinned vkd3d-shader
  native restored by `tools/restore.*`.
