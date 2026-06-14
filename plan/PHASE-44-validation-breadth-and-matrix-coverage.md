# Phase 44 — Validation breadth & matrix coverage

**Status:** 🟡 In progress (started 2026-06-14). **A done; B (VTF) done, B (texture-array render) blocked on a MonoGame API gap; C/D remain.** Owns the living [validation matrix](../docs/validation-matrix.md).
**Track:** Validation / fidelity.

## Goal

Make ShadowDusk's cross-runtime validation **programmatically enforceable and broader**, so the
[validation matrix](../docs/validation-matrix.md) cells are backed by real tests rather than prose, and so
the known proof gaps get closed where a runtime exists to close them. Zero product behavior change, this is
test/validation infrastructure plus documentation.

## Why this phase exists

The validation matrix (added 2026-06-14) is the first single tracker of what ShadowDusk is proven to do
across runtime (MonoGame / KNI / FNA), format/version (MGFX v10/v11, KNIFX, fx_2_0, Reach/HiDef), target
(DirectX / OpenGL / Vulkan / Metal), and OS. It surfaced gaps that had no phase home:
- The matrix is a **document** — nothing stops it drifting from reality.
- The **modern DirectX features** (vertex texture fetch, texture arrays) **compile** but were never
  render-checked (the GL path rejects them with `SD0210`; the DX path emits them, unverified at render).
- The real-engine `validation/*` render gates are **manual**, not wired into CI.
- KNI render proof is **browser-only and predates v4.02** (also Phase 35 Area B's reproduce-first gate).

## Work items

### A. Matrix-coverage harness — ✅ (this phase)
A programmatic test that asserts the matrix's **compile-level** claims for each cell: a representative shader
either **compiles** for a target or is **rejected with the documented `SD` code**. This makes the
compile/reject rows of the matrix self-checking, so the doc cannot silently drift on them.
- Cells pinned: OpenGL + DirectX compile a standard effect; **OpenGL rejects vertex texture fetch and
  `Texture2DArray` with `SD0210`**; **DirectX compiles both**; **FNA rejects SM4 with `SD0300`** and compiles
  SM2/3. (Render-level cells stay backed by the harnesses in §C.)
- Home: `tests/ShadowDusk.Integration.Tests` (real `EffectCompiler`, native machinery present in CI).

### B. DirectX modern-features render test — ✅ VTF done; texture-array blocked
`validation/DxModernFeatures` renders in **real MonoGame WindowsDX** and confirms ShadowDusk's shipping
**vkd3d** output draws the same picture as the **`fxc`/`d3dcompiler` oracle** (Microsoft's own compiler),
arm-vs-arm, same scene, only the compiler differs (the `VsDrivenDx` pattern).
- **Vertex texture fetch: ✅ render-proven** (2026-06-14) — vkd3d == `fxc` at **maxd 0**, and the VTF
  genuinely deforms the mesh (gradient-height vs flat-height differ, so the pixel-match is non-vacuous).
  Matrix §4 VTF cell -> ✅.
- **`Texture2DArray`: 🚫 render blocked** — MonoGame's public API exposes no `Texture2DArray` to bind to the
  shader's array sampler, so a non-vacuous render can't be set up. This is a MonoGame *runtime-API* gap, not
  a ShadowDusk one (ShadowDusk compiles the array shader to valid DXBC, pinned by item A). Revisit if a
  MonoGame array-binding path lands (3.8.5+?) or via a non-MonoGame DX11 harness.

### C. CI-ify the real-engine render gates — (gap, partly this phase)
Promote the manual `validation/*` render gates (MonoGame GL/DX, FNA) into CI jobs where a software/headless
driver exists (GL already renders in CI on Linux via Mesa; DX/FNA need the Windows runner + a driver story).

### D. KNI v4.02 render validation — (gap, shared with Phase 35 Area B)
Refresh the Phase-24 browser harness against **KNI v4.02** and add a KNI **desktop** (`SDL2.GL`) render
check, so KNI stops being browser-only/dated in the matrix. This is also Phase 35 Area B's reproduce-first
gate ("does our v10 render correctly on current KNI, or is KNIFX actually needed?"). Needs a current KNI
runtime/harness.

## Gating
- A + B: doable now (no external blocker).
- C: GL is already CI'd; DX/FNA render-in-CI needs a driver story on the runners.
- D: needs a KNI v4.02 web/desktop harness.

## Definition of done
Each matrix cell is either backed by a passing programmatic test (compile or render) or documented as blocked
on a real external dependency (no runtime / preview-only engine), with the matrix doc updated to match.
