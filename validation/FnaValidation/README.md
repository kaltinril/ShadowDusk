# FnaValidation — Phase 39 rung-3/4 harness (real FNA, real MojoShader)

The proof that ShadowDusk's `PlatformTarget.Fna` output (`.fxb`, fx_2_0) **loads and
renders in REAL FNA identically to Microsoft's own fx_2_0 compiler**. This is the FNA
analog of the Phase 17 (GL) / Phase 18 (DX) validation hosts, covering the last two rungs
of the Phase 39 evidence ladder (`docs/the-purpose.md`, *FNA's bar*):

- **Rung 3 — real MojoShader parses + translates**: each arm's bytes go through FNA's
  `new Effect(GraphicsDevice, bytes)` → FNA3D → `MOJOSHADER_compileEffect`. A parse or
  translate failure surfaces here (the harness hooks `FNALoggerEXT.LogError` to capture
  the exact MojoShader error text — FNA3D logs, it does not throw).
- **Rung 4 — real FNA renders pixel-equivalent**: each effect renders in one of two scenes
  and the two arms' pixels are compared in-process. PS-only effects are applied to the same
  cat image via the normal `SpriteBatch` path (mirrors
  `validation/SharedDx/DxEffectImageRenderer.cs` exactly); VS-driven effects — which ship
  their own vertex shader SpriteBatch can never exercise — draw a custom-geometry
  clip-space quad (`POSITION0/COLOR0/TEXCOORD0`, `DrawUserIndexedPrimitives`) so the
  effect's own VS runs (mirrors `validation/SharedDx/VsDxEffectImageRenderer.cs`, the
  Phase-28 analog).

## The two arms

| Arm | Compiler | Role |
|---|---|---|
| **Candidate** | ShadowDusk in-memory: `EffectCompiler.CompileAsync` with `PlatformTarget.Fna` (vkd3d-shader SM ≤ 3 + `Fx2EffectWriter`) | the SHIPPING pipeline |
| **Reference** | system `d3dcompiler_47.dll` `D3DCompile(pTarget: "fx_2_0", pEntrypoint: NULL)` | **test oracle only** — byte-identical to `fxc.exe /T fx_2_0` (see `tests/fixtures/golden/FNA/README.md`); never ships, never drives the product |

Both arms run in **one process on one GraphicsDevice**, so the comparison is same-backend
by construction. The harness sets `FNA3D_FORCE_DRIVER=D3D11` (FNA3D's Windows default,
pinned explicitly for determinism).

**Profile parity:** ShadowDusk's FNA path compiles macro-profile passes
(`compile PS_SHADERMODEL …`) at ps_3_0/vs_3_0 and defines `FNA;HLSL;SM3` — never
`OPENGL`. The reference arm prepends `#define OPENGL 1` so the corpus template's
`#if OPENGL → #define PS_SHADERMODEL ps_3_0` branch fires (the `#else` is
`ps_4_0_level_9_1`, which fx_2_0 rejects). Before relying on that, the harness verifies
textually per shader that the **only** OPENGL-conditional content is the standard
SHADERMODEL/`SV_POSITION` define block; anything else marks the shader
*macro-parity-unsafe* and excludes it (compared programs must be the same program).

## Gate vs reported

- **GATE (exit code 0 requires all 14 to PASS):** the Phase 17 PS-only set — Grayscale,
  Invert, Sepia, Saturate, Pixelated, Scanlines, Fading, Dots, Dissolve, TintShader — plus
  the VS-driven set (custom-geometry quad scene, the 17-VS analog) —
  VsTransformColorTexture, PolygonLight, VertexAndPixel, FnaMultiPassStates (the last also
  proves multi-pass + in-pass render states empirically: its second pass alpha-blends over
  its first with the device blend state pinned Opaque — pinned by an automated flat-image
  content guard, since the arm-vs-arm compare alone cannot see an FNA-side honoring
  regression that degrades both arms identically).
- **Reported (not gating):** BasicShader, BlendShader, ClipShader, ClipShaderNew,
  ClipShaderSpriteTarget, MultiTexture, MultiTextureOverlay, SimpleLightShader,
  SpriteAlphaTest, Teleport, the fxc golden sources `minimal`/`textured` from
  `tests/fixtures/golden/FNA/`, and the 5 diagnostic probe rows (`FnaPreshaderProbe` +
  the `FnaProbe*` Appendix-G bisection ladder).

**PASS** = both arms compile + load + **render cleanly** (a render-stage error on either
arm fails the row, even when comparable pixels exist), and **zero** pixels have a
per-channel delta above **4/255** — the same tolerance `validation/compare_dx.py` applies
to the Phase 18 cross-compiler comparison (different compilers ⇒ tiny float divergence is
legitimate; byte-equality is a non-goal). Each row's scene flag is verified against the
candidate `.fxb` (a VS-bearing effect misfiled under the Sprite scene would pass
vacuously), and the VsQuad scene clears texture slots between arms so a missing
sampler→texture binding renders black instead of inheriting the other arm's binding.

## External dependencies (restored, never committed)

`./external/` is gitignored. `restore-fna.ps1` populates it idempotently:

1. **FNA** — pinned tag **26.06** (latest release, 2026-06), cloned with submodules from
   https://github.com/FNA-XNA/FNA. FNA ≥ 24.01 uses **SDL3**.
2. **fnalibs** (win-x64 natives: SDL3, FNA3D, FAudio, libtheorafile, D3D12 Agility SDK) —
   the `fnalibs` artifact of the **FNA-XNA/fnalibs-dailies** CI workflow (the old
   `fna.flibitijibibo.com/archive/fnalibs.tar.bz2` URL is gone — 404). Downloading
   needs an authenticated `gh` CLI. The Phase 39 evidence run used dailies run
   `27180614567` (2026-06, main, success).

The vkd3d native used by the candidate arm comes from the repo's own
`tools/restore.ps1` (same as every other validation host).

## How to run

```powershell
# once (or after deleting external/)
.\restore-fna.ps1            # from validation/FnaValidation
..\..\tools\restore.ps1      # repo natives (vkd3d), if not already restored

dotnet run -c Release        # from validation/FnaValidation
```

A small FNA window appears briefly (FNA3D needs a swap-chain host); everything renders
offscreen in the first Draw and the Game exits. Outputs land in
`validation/output-fna/{reference,candidate}/<name>.png` (plus both arms' raw `.fxb`
under `output-fna/fxb/`) regardless of outcome.

This project is intentionally **not** in `ShadowDusk.slnx` (it is Windows-only — the
*oracle* is Windows-only, not ShadowDusk's FNA target) and mirrors
`validation/CandidateVkd3d`'s structure.
