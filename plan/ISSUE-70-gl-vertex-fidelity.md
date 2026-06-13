# Issue #70 — OpenGL vertex-shader fidelity (matrix transpose + legacy POSITION output)

**Status:** ✅ Both bugs fixed and rung-4 render-proven in real MonoGame (uncommitted). Driven by
GitHub issue [#70](https://github.com/kaltinril/ShadowDusk/issues/70) (squarebananas: a custom VS
rendered garbled vertex positions on DesktopGL/KNI).

## TL;DR

Two distinct, silently-wrong **OpenGL vertex-shader** fidelity bugs, both in ShadowDusk's
HLSL→GLSL pipeline (not the `.mgfx` container, so **KNIFX / MGFX-v11 would not have fixed
either** — see *Why not a version issue*). Both were invisible because the VS validation corpus
used **transpose-invariant inputs** (identity matrices) and **only true `SV_Position` fixtures**.
A new corpus-wide, varied-input render sweep (`MatrixConventionSweepTests`) found the second bug
while proving the first, and a mutation test proves the suites actually catch both.

| # | Bug | Symptom | Root cause | Fix |
|---|---|---|---|---|
| 1 | **Matrix transpose** | non-identity `mul(v, M)` renders transposed ("exploded cube") | `mat4(reg0..reg3)` rebuilt registers as COLUMNS, but the runtime uploads columns AND SPIRV-Cross swaps `mul(v,M)→M*v`, netting the transpose | `BuildUploadedMat4` reconstructs the matrix **transposed** (registers as rows), open-coded (no `transpose()` builtin — absent in GLSL ES 1.00/desktop 110) |
| 2 | **Legacy `: POSITION` output** | position written to a dead varying, `gl_Position` **unwritten** → broken geometry even at identity | DXC (SM6) treats a `: POSITION` VS output as a user varying; only `: SV_Position` is the builtin. The stock MonoGame GL template uses `#define SV_POSITION POSITION` | `IsPositionSemantic` remaps `out_var_POSITION{0}` → `gl_Position` (faithful to D3D9 SM3, what mgfxc does) |

Bug 2 is arguably the bigger one: it needs no special input to trigger and hits **any custom
vertex shader built from the default MonoGame OpenGL template**. It silently affected three corpus
fixtures (`VertexAndPixel`, `PolygonLight`, `FnaMultiPassStates`).

## Why these slipped through (the validation-gap lesson)

- **Identity matrices are transpose-invariant.** Every Phase-28 VS render check uploaded
  `Matrix.Identity`, so a transposed matrix produced an identical image. Bug 1 was undetectable.
- **The VS corpus used only true `SV_Position`.** `VsTransformColorTexture` deliberately used
  `: SV_Position` to dodge the "dead user-varying" the legacy form produces — so bug 2 was never
  rendered. The legacy form is the *common* shape (the default template), not the exotic one.

This is the concrete instance of the standing risk noted in
`plan/DONE/PHASE-28-…md` ("the transform is transposed/garbled at runtime — a classic silent
fidelity bug. Pin with an in-engine render check"): the render check existed but its inputs were
too weak to exercise the bug.

## Evidence (rung-4, real runtime, reference-compiler oracle)

- **Real MonoGame `validation/VsDriven`** (DesktopGL): with a non-identity asymmetric matrix,
  ShadowDusk == the mgfxc golden **maxd 0** (render-target + backbuffer); the legacy `: POSITION`
  variant **loads + renders == the true-SV_Position candidate maxd 0**.
- **Real MonoGame `validation/VsDrivenDx`**: both DX backends == golden maxd 0 (see below).
- **Automated GL↔GL sweep** `tests/ShadowDusk.ImageTests/Tests/MatrixConventionSweepTests.cs`:
  ShadowDusk's emitted GLSL vs the mgfxc golden across 5 matrix shapes (asymmetric scale+translate,
  shear, axis-flip/negative-determinant, general-asymmetric, rotation) × {single matrix @reg0,
  chained matrices @reg0/4/8, matrix array @reg0/4}, plus non-vacuity guards (identity≠matrix,
  matrix≠transpose). `Issue70MatrixTransposeRenderTests` is the focused #70 case.
- **Mutation test (suite-capability proof):** reverting either fix turns **12 tests red** (8 GLSL
  unit + 4 GL render); restoring them goes green. The suites genuinely detect both bug classes.

## Bonus find — DirectX d3dcompiler oracle had bug 1's twin

The non-identity DX render check surfaced that the Windows-only `d3dcompiler` oracle backend used
`ShaderFlags.PackMatrixRowMajor`, transposing the matrix vs the column-major convention mgfxc,
vkd3d (the shipping DX backend), and the runtime all use. The shipping vkd3d path was already
correct (maxd 0); the oracle rendered a sheared mesh. Fixed to `PackMatrixColumnMajor`
(`D3DCompilerShaderCompiler.cs`); reflection offsets are majorness-independent, so nothing else
moved (reflection suite green).

## Why not a version issue (KNIFX / MGFX-v11)

The bugs are in the **GLSL the rewriter emits**, written into the `.mgfx` body byte-for-byte
independent of `MgfxVersion` (`MgfxWriter` serializes body then header). So they are invariant
across v10/v11 and every KNI `GraphicsProfile` (Reach…FL11_1). A KNIFX container around the same
broken GLSL renders identically broken; KNI already loads our v10 via its MGFX migration path
(Phase 35 Area A). KNIFX buys KNI's *render-quality* fixes (a separate product-scope question,
Phase 35 Area B, deferred) — it does not fix ShadowDusk's own generation bugs. `--mgfx-version 11`
is a non-faithful stub and must not be advertised as KNI support.

## Scope / cross-backend status

- **OpenGL (MonoGame/KNI):** both bugs fixed, proven. WASM/browser GL inherits the fix (same shared
  managed `MonoGameGlslRewriter`).
- **DirectX:** shipping vkd3d already correct; d3dcompiler oracle fixed.
- **FNA:** unaffected (separate `Fx2EffectWriter`/vkd3d path; already proven with asymmetric matrices).
- **Byte-identity:** 12 OpenGL entries legitimately changed (11 matrix + `FnaMultiPassStates` for the
  POSITION fix); manifest regenerated; DX/FNA untouched.

## Open follow-ups

- **Widen the real-runtime sweep.** `MatrixConventionSweepTests` is automated but headless-GL; the
  real-MonoGame `VsDriven`/`VsDrivenDx` gates cover one fixture each. Extending the real-MonoGame
  gates to the full VS corpus × varied inputs is the highest-confidence next hardening step.
- **Edge of the POSITION remap.** A PS that *reads* a `: POSITION` input is not handled (it is also
  non-portable in D3D9). If a real shader needs it, decide support-or-reject-loudly.
- **KNI v4.02 render parity** (Phase 35 Area B) remains an open product-scope decision, unrelated to
  these correctness fixes.
