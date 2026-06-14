# ShadowDusk Validation Matrix

**Purpose:** one place to *definitively* track what ShadowDusk is proven to do, across every runtime
library, shader format/version, graphics target, and OS, and to mark cells off as they advance. This is a
**living checklist**: update a cell's status (and the date) whenever its evidence changes.

**Last updated:** 2026-06-14.

---

## How to read a cell (the evidence levels, in plain English)

Every cell is at one of these levels. "Proven" always means **compared to the official reference compiler
(`mgfxc` for MonoGame/KNI, `fxc /T fx_2_0` for FNA), same graphics backend, same scene.**

| Mark | Level | Plain meaning |
|---|---|---|
| ✅ | **Render-proven** | Actually rendered in the **real engine** and the picture matches the reference compiler, pixel-for-pixel. The strongest proof. |
| 🟦 | **Load-proven** | The real engine **loads** the output without error, but the rendered picture has not yet been compared. |
| 🟡 | **Compile-only** | ShadowDusk **produces well-formed output**, but no real engine has loaded or rendered it. |
| 🌐 | **Browser-proven (dated)** | Rendered in a real **browser** runtime (KNI WebGL / Blazor) and compared, but the proof predates a current engine version, treat as stale until re-run. |
| 🚫 | **Blocked** | No shipping runtime exists to validate against yet (e.g. KNI has no Vulkan; "modern GLSL" has no GL runtime). Not a defect, a dependency. |
| ⬛ | **Not implemented** | ShadowDusk does not target this yet. |
| — | **N/A** | The combination does not exist (e.g. FNA is not MGFX-versioned). |

> **Why most of the naive 3 x 4 x 4 x 4 cross-product is not real:** FNA uses one fx_2_0 `.fxb` for *all* its
> backends (not MGFX, not v10/v11). KNI ships no Vulkan or Metal. ShadowDusk has no Metal target yet. The
> tables below show only the **valid** combinations.

---

## 1. Primary matrix, library x graphics target

What ShadowDusk emits for each runtime/target, and the best proof to date. (Format in parentheses.)

| Runtime | DirectX (DX11) | OpenGL / GLES | Vulkan | Metal |
|---|---|---|---|---|
| **MonoGame** | ✅ DXBC SM5 (MGFX v10) — rendered on Windows vs `mgfxc` + `fxc` oracle (PS corpus + VS-matrix) | ✅ GLSL (MGFX v10) — rendered on Linux (Mesa) + Windows vs `mgfxc` (PS corpus + VS-driven) | 🚫 SPIR-V — MonoGame 3.8.5 Vulkan still **preview**; ShadowDusk DXC->SPIR-V path **parked** (Phase 32) | ⬛ MSL — ShadowDusk Metal target is a **stub** |
| **KNI** | 🟡 DXBC SM5 (MGFX v10) — same bytes as MonoGame DX (likely loads), **not load/render-tested in KNI** (`WinForms.DX11`/`UAP.DX11`) | 🌐 GLSL (MGFX v10) — rendered in **KNI WebGL/Blazor (Phase 24)** but **pre-v4.02**; KNI **desktop** (`SDL2.GL`) not separately tested | — KNI ships **no Vulkan** platform | — KNI iOS uses **GL**, no Metal |
| **FNA** | ✅ — *one* fx_2_0 `.fxb` serves **all** FNA3D backends (D3D11 / Vulkan / OpenGL / Metal) via FNA3D + MojoShader; rendered vs `fxc /T fx_2_0` (PS + VS-driven) | ✅ (same `.fxb`) | ✅ (same `.fxb`) | ✅ (same `.fxb`) |

**Reading it:** the rock-solid, render-proven cells today are **MonoGame OpenGL, MonoGame DirectX, and FNA
(all backends)**. The biggest honest gaps are **KNI render validation on a current (v4.02) runtime** and the
**modern DirectX features** (next section).

## 2. Shader format / version / graphics profile

| Format / profile | Applies to | Status | Notes |
|---|---|---|---|
| **MGFX v10** | MonoGame, KNI | ✅ | The default and the basis of every ✅ above. The one container every MGFX-lineage runtime loads. |
| **MGFX v11** (MonoGame) | MonoGame 3.8.5+ | 🟡 | `--mgfx-version 11` is a header-byte **stub**, not a faithful v11 body. v10 loads forward in 3.8.5's `[10,11]` range (source-verified). 3.8.5 still **preview**. |
| **KNIFX v11** (KNI) | KNI 4.02+ | ⬛ | No KNIFX writer. Our v10 loads in KNI via its MGFX-v10 migration path. KNIFX = container + parity fixes over a still-MojoShader body. See [`PHASE-35-appendix/knifx-vs-mgfx-v11-research.md`](../plan/PHASE-35-appendix/knifx-vs-mgfx-v11-research.md). |
| **Reach** (WebGL1 / GL ES 1.00) | MonoGame, KNI GL | ✅ | The default GL output dialect. |
| **HiDef** (WebGL2 / GL ES 3.00) | KNI GL | 🌐 | The `#version 300`-guarded output; browser-proven (Phase 24 SD-HIDEF), pre-v4.02. |

## 3. OS coverage (where the proof actually ran)

ShadowDusk's **output bytes are OS-independent** (proven byte-identical on Windows/Linux/macOS by
`CrossHostByteIdentityTests`), so compiling is cross-OS-solved. What varies by OS is where a **render** was
actually run.

| OS | Compile | Render proof that ran here |
|---|---|---|
| **Windows** | ✅ (CI) | ✅ DirectX (validation harnesses) + ✅ FNA (`fxc` oracle). GL render **soft-skips** (runners expose only GDI Generic GL). |
| **Linux** | ✅ (CI, byte-identical) | ✅ OpenGL (Mesa software GL, the `ShadowDusk.ImageTests` suite runs in CI here). |
| **macOS** | ✅ (CI, byte-identical) | (no separate render run — transferred via byte-identity: the output equals the Windows/Linux bytes, so their render proofs carry over). |
| **Web (WASM / Blazor)** | ✅ (in-browser DXC+SPIRV-Cross frontend, Phase 23) | 🌐 KNI WebGL (Phase 24 Playwright harness), pre-v4.02. |

## 4. The modern-DirectX-features sub-checklist (called out because it is a live gap)

The DirectX target is **not** MojoShader-limited (that limit is OpenGL-only). It already *compiles* the SM4/5
features the GL path rejects, but their **render** is not yet proven.

| Feature | Compiles (DirectX) | Rejected on OpenGL | Rendered + matched on DirectX |
|---|---|---|---|
| Vertex texture fetch (`SampleLevel` in VS) | ✅ (exit 0) | ✅ `SD0210` (correct) | ✅ **render-proven** — vkd3d == `fxc` oracle at **maxd 0** in real MonoGame WindowsDX, VTF actually deforms the mesh (`validation/DxModernFeatures`, 2026-06-14) |
| `Texture2DArray` | ✅ (exit 0) | ✅ `SD0210` (correct) | 🚫 **blocked** — MonoGame's public API has no `Texture2DArray` to bind, so a non-vacuous render can't be set up yet (Phase 44 item D-adjacent) |

VTF is closed; the texture-array render is blocked on a MonoGame runtime-API gap, not a ShadowDusk one. The
compile rung for both is pinned by `ValidationMatrixCoverageTests`; VTF render by `validation/DxModernFeatures`.

---

## 5. How a cell advances (the workflow to turn 🟡/🟦 into ✅)

1. **Compile** the fixture to the target (already automated, see below).
2. **Load** it in the real engine (`new Effect(gd, bytes)` for MonoGame/KNI; FNA `Effect`).
3. **Render** a frame and pixel-compare to the reference compiler's output for the *same backend + scene*
   (tolerance is the established bar: max per-channel delta <= 1 for dyadic inputs).
4. Mark the cell ✅ with the date and the test/harness that proves it.

## 6. Programmatic tests that back each cell (what exists today)

| Coverage | Test / harness | Automated in CI? | How to run locally |
|---|---|---|---|
| Compile every fixture to DX + GL (+ census) | `tests/ShadowDusk.Integration.Tests` `Phase41StructuralDivergenceMatrixTests` | ✅ | `dotnet test ...Integration.Tests --filter Phase41StructuralDivergence` |
| **Matrix compile/reject claims** (this doc's compile rung: GL rejects VTF/arrays `SD0210`, DX compiles them, FNA rejects SM4 `SD0300`) | `ValidationMatrixCoverageTests` | ✅ | `dotnet test ...Integration.Tests --filter ValidationMatrixCoverage` |
| **Cross-OS byte-identical** output (Win/Linux/Mac; GL/DX/FNA) | `CrossHostByteIdentityTests` | ✅ (all 3 OSes) | `dotnet test ...Integration.Tests --filter CrossHostByteIdentity` |
| OpenGL render vs golden (software GL) | `tests/ShadowDusk.ImageTests` (incl. `MatrixConventionSweepTests`, `Issue70MatrixTransposeRenderTests`) | ✅ (Linux Mesa) | `dotnet test tests/ShadowDusk.ImageTests` |
| **Real MonoGame OpenGL** render vs `mgfxc` | `validation/VsDriven`, `validation/Candidate` + `validation/compare.py` | manual | `dotnet run --project validation/VsDriven` |
| **Real MonoGame DirectX** render vs `mgfxc`/`fxc` | `validation/VsDrivenDx`, `validation/Candidate{Dx,Vkd3d}` + `compare_dx.py` | manual | `dotnet run --project validation/VsDrivenDx` |
| **DirectX modern features render** (vertex texture fetch; vkd3d vs `fxc`) | `validation/DxModernFeatures` | manual | `dotnet run --project validation/DxModernFeatures` |
| **Real FNA** render vs `fxc /T fx_2_0` | `validation/FnaValidation` | manual | `dotnet run --project validation/FnaValidation` |
| Forward-compat (newer MonoGame loads our v10) | `validation/ForwardCompat` | manual | `validation/ForwardCompat/run-forwardcompat.ps1` |
| **KNI WebGL** render (browser) | `tests/ShadowDusk.BrowserTests` (Playwright) | manual | see `tests/ShadowDusk.BrowserTests/README.md` |

**The "test programmatically" goal:** the manual `validation/*` harnesses are the render-proof for the
strongest cells but are not yet wired into CI. The path to a fully self-checking matrix is (a) promote the
`validation/*` render gates into CI jobs (where a software/headless driver exists), and (b) back this matrix
with a machine-readable coverage manifest a test asserts against (so a cell cannot be marked ✅ without a
passing test). Tracked as a gap below.

## 7. Gaps & next targets (ordered)

| Gap | Achievable here? | Notes |
|---|---|---|
| **DirectX modern features render** | partly **done** | **VTF ✅** (`validation/DxModernFeatures`, vkd3d == `fxc` maxd 0). Texture-array render is **blocked** on MonoGame's missing public `Texture2DArray` binding (a runtime-API gap, not ours). |
| **KNI v4.02 render** (desktop `SDL2.GL` + a fresh WebGL run) | partly | Needs a current KNI runtime; browser path can refresh the Phase-24 harness. Closes the biggest 🌐/🟡 cells. |
| **Promote `validation/*` render gates into CI** | partly | GL render runs in CI on Linux today; DX/FNA render are Windows-runner + (for DX) a software driver question. |
| **Machine-readable coverage** backing this matrix | **compile rung done** | `ValidationMatrixCoverageTests` pins the compile/reject cells as a `[Theory]`. Extending it to assert the render cells against the `validation/*` gates is the remaining step. |
| **MGFX v11 / KNIFX writers** | gated | Only if a runtime proves v10 deficient (KNIFX) or 3.8.5 goes stable (v11). See [`PHASE-35-appendix/`](../plan/PHASE-35-appendix/). |
| **Vulkan / DX12 render** | gated | Blocked on MonoGame 3.8.5 going **stable** (Areas C/D). ShadowDusk's DXC->SPIR-V/DXIL plumbing is built. |
| **Metal target** | ⬛ | ShadowDusk Metal is a stub; FNA's Metal backend is already covered by the one `.fxb`. |

---

## Sources / cross-references

- Engine state (June 2026, primary-source): [`PHASE-35-appendix/shader-pipeline-landscape-2026-06.md`](../plan/PHASE-35-appendix/shader-pipeline-landscape-2026-06.md).
- Evidence-ladder definition + backend pipeline table: [`the-purpose.md`](the-purpose.md).
- Forward-version (v11/KNIFX/Vulkan/DX12) status: [`PHASE-35-forward-version-support.md`](../plan/PHASE-35-forward-version-support.md).
- KNI WebGL render harness: `tests/ShadowDusk.BrowserTests/README.md` (Phase 24).
- Cross-OS byte-identity rationale: `tests/fixtures/golden/byte-identity/README.md`.
