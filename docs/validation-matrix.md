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
| **KNI** | 🟡 DXBC SM5 (MGFX v10) — same bytes as MonoGame DX (likely loads), **not load/render-tested in KNI** (`WinForms.DX11`/`UAP.DX11`) | ✅ GLSL (MGFX v10) — **render-proven on KNI v4.2.9001 desktop (SDL2.GL)**: v10 loads + renders **pixel-identical to MonoGame (maxd 0)** and **within maxd 1 of the mgfxc goldens** across the 10-shader corpus (`validation/KniDesktopGL`, 2026-06-14). Also browser-proven in KNI WebGL/Blazor (Phase 24; that run pre-dates v4.02, refresh pending) | — KNI ships **no Vulkan** platform | — KNI iOS uses **GL**, no Metal |
| **FNA** | ✅ — *one* fx_2_0 `.fxb` serves **all** FNA3D backends (D3D11 / Vulkan / OpenGL / Metal) via FNA3D + MojoShader; rendered vs `fxc /T fx_2_0` (PS + VS-driven) | ✅ (same `.fxb`) | ✅ (same `.fxb`) | ✅ (same `.fxb`) |

**Reading it:** the rock-solid, render-proven cells today are **MonoGame OpenGL, MonoGame DirectX, FNA
(all backends), and now KNI OpenGL on the current v4.02 desktop runtime** (`validation/KniDesktopGL`,
2026-06-14). The remaining honest gaps are **KNI DirectX** (untested), a **browser refresh** of the KNI
WebGL proof on v4.02 (the Phase-24 run pre-dates it), and the **modern DirectX features** (next section).

## 2. Shader format / version / graphics profile

> **Format roadmap (committed):** ShadowDusk will emit **v10 *and* v11 *and* KNIFX**, each a real, faithful
> output a consumer can select and use, not just v10 with a promise that newer runtimes still load it. v10
> stays the **default** because it loads on every MGFX-lineage runtime (the seamless baseline), but v11 and
> KNIFX are **first-class additive outputs** so consumers can use the newer containers' features (and take the
> bytes for their own runtime if they like). Forward-compatibility of v10 is a nice-to-have, **not** a reason
> to stop short of v11/KNIFX. The newer formats are opt-in or auto-selected from the target, **never a flag a
> consumer must set to get correct output** (the seamless rule still holds). **All three are now BUILT and
> render-proven (2026-06-14):** v10 (default, everywhere), **MGFX v11** (`MgfxVersion = 11`, render-proven in
> MonoGame 3.8.5), and **KNIFX v11** (`Container = Knifx`, render-proven in KNI 4.02). Remaining: the
> auto-select/override seam, and KNIFX feature-parity for optimized matrices.

| Format / profile | Applies to | Status | Notes |
|---|---|---|---|
| **MGFX v10** | MonoGame, KNI | ✅ | The default and the basis of every ✅ above. The one container every MGFX-lineage runtime loads. |
| **MGFX v11** (MonoGame) | MonoGame 3.8.5+ | ✅ **render-proven (MonoGame 3.8.5)** | ShadowDusk emits a **faithful MGFX v11** via `CompilerOptions.MgfxVersion = 11`: the v10 body plus the two per-shader diagnostic strings (`SourceFile`, `Entrypoint`) MonoGame PR #8813 added. **Render-proven 2026-06-14**: loads + renders **10/10 in real MonoGame 3.8.5.0** (`validation/MonoGameV11`), **maxd 0 vs v10**, <= 1 vs the mgfxc goldens. **Opt-in only** (3.8.5 is pre-release; the default stays v10). The old header-byte `--mgfx-version 11` was actually **corrupt** (v10 body + version 11 desyncs a v11 reader); now fixed. Spec: [`PHASE-35-appendix/mgfx-v11-format-spec.md`](../plan/PHASE-35-appendix/mgfx-v11-format-spec.md). |
| **KNIFX v11** (KNI) | KNI 4.02+ | ✅ **render-proven (corpus); feature parity pending** | ShadowDusk **emits KNIFX v11** today (`CompilerOptions.Container = Knifx`, `KnifxWriter`): signature `KNIF`, multi-backend directory, packed-int body, GL GLSL-version directory. **Render-proven 2026-06-14**: the KNIFX corpus **loads + renders 10/10 in real KNI v4.2.9001** (`validation/KniDesktopGL knifx`), **maxd 0 vs the v10 render**. Still **opt-in / experimental** (not auto-selected yet). The optimized-`Matrix4x4` `columnsActual` fix is now **validated against a KNIFXC golden** (2026-06-14, KNIFXC built from source `kni/Tools/EffectCompiler`): a full `float4x4` golden writes `columnsActual=4`, **identical** to ShadowDusk; a partially-used `float4x4` (`(float3x3)World`) golden writes `columnsActual=3` while ShadowDusk writes `4`, but that is **render-safe** (ShadowDusk's GLSL and `columnsActual` come from the same reflection, so they stay consistent, a storage-efficiency divergence, not wrong pixels). Closing the partial-matrix case exactly is a non-goal; the sampler-without-texture fix is not golden-checked yet. KNIFX = a new container + those parity fixes over a still-MojoShader body. Spec: [`PHASE-35-appendix/knifx-format-spec.md`](../plan/PHASE-35-appendix/knifx-format-spec.md). |
| **Reach** (WebGL1 / GL ES 1.00) | MonoGame, KNI GL | ✅ | The default GL output dialect. |
| **HiDef** (WebGL2 / GL ES 3.00) | KNI GL | 🌐 | The `#version 300`-guarded output; browser-proven (Phase 24 SD-HIDEF), pre-v4.02. |

## 3. OS coverage (where the proof actually ran)

ShadowDusk's **output bytes are OS-independent** (proven byte-identical on Windows/Linux/macOS by
`CrossHostByteIdentityTests`), so compiling is cross-OS-solved. What varies by OS is where a **render** was
actually run.

| OS | Compile | Render proof that ran here |
|---|---|---|
| **Windows** | ✅ (CI) | ✅ DirectX (validation harnesses) + ✅ FNA (`fxc` oracle) + ✅ **KNI OpenGL desktop** (`validation/KniDesktopGL`, real SDL2.GL driver). MonoGame GL render **soft-skips** in CI (runners expose only GDI Generic GL), but renders on a real desktop driver. |
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
| **Real KNI OpenGL desktop** render vs mgfxc + MonoGame (KNI v4.02, SDL2.GL) | `validation/KniDesktopGL` + `compare_kni.py` | manual | `dotnet run --project validation/KniDesktopGL` then `python validation/compare_kni.py` |
| **Real KNI OpenGL VS-driven** render vs mgfxc (issue #70: matrix transpose + legacy `: POSITION`, KNI v4.02 SDL2.GL) | `validation/KniVsDriven` (in-process compare) | manual | `dotnet run --project validation/KniVsDriven` |
| **KNIFX v11** render in real KNI (vs v10) | `validation/KniDesktopGL knifx` + `compare_kni.py` | manual | `dotnet run --project validation/KniDesktopGL -- knifx` |
| **MGFX v11** render in real MonoGame 3.8.5 (vs v10 + goldens) | `validation/MonoGameV11` + `compare_mgfxv11.py` | manual | `dotnet run --project validation/MonoGameV11` then `... -- v10`, then `python validation/compare_mgfxv11.py` |

**The "test programmatically" goal:** the manual `validation/*` harnesses are the render-proof for the
strongest cells but are not yet wired into CI. The path to a fully self-checking matrix is (a) promote the
`validation/*` render gates into CI jobs (where a software/headless driver exists), and (b) back this matrix
with a machine-readable coverage manifest a test asserts against (so a cell cannot be marked ✅ without a
passing test). Tracked as a gap below.

## 7. Gaps & next targets (ordered)

| Gap | Achievable here? | Notes |
|---|---|---|
| **DirectX modern features render** | partly **done** | **VTF ✅** (`validation/DxModernFeatures`, vkd3d == `fxc` maxd 0). Texture-array render is **blocked** on MonoGame's missing public `Texture2DArray` binding (a runtime-API gap, not ours). |
| **KNI v4.02 render** (desktop `SDL2.GL` + a fresh WebGL run) | **desktop done** | ✅ **Desktop SDL2.GL render-proven on KNI v4.2.9001** (`validation/KniDesktopGL`, 2026-06-14): v10 renders maxd 0 vs MonoGame, ≤1 vs mgfxc goldens, 10/10. Remaining: a **fresh WebGL run** on v4.02 (refresh the Phase-24 browser harness) and **KNI DirectX**. This desktop rig is also Phase 35 Area B's reproduce-first baseline for the KNIFX writer. |
| **Promote `validation/*` render gates into CI** | partly | GL render runs in CI on Linux today; DX/FNA render are Windows-runner + (for DX) a software driver question. |
| **Machine-readable coverage** backing this matrix | **compile rung done** | `ValidationMatrixCoverageTests` pins the compile/reject cells as a `[Theory]`. Extending it to assert the render cells against the `validation/*` gates is the remaining step. |
| **MGFX v11 / KNIFX writers** | **committed, in progress** | Additive outputs we **will** emit so consumers can *use* the new-container features (KNIFX's XNA-compat/quality fixes; MonoGame v11's body), not a "won't do." v10 staying forward-compatible is a convenience, **not** a reason to skip these. Default stays v10 for universal load; v11/KNIFX are opt-in / auto-selected from the target, never required (seamless rule preserved). Path: reproduce-first render against KNI v4.02 (Phase 44 D) -> build the faithful writers (Phase 35 Area B). See [`PHASE-35-appendix/`](../plan/PHASE-35-appendix/). |
| **Vulkan / DX12 render** | **ready, ext-blocked** | The DXC->SPIR-V / DXIL plumbing is **already built**; only the render-validation is blocked, and solely on the **external** dependency of MonoGame 3.8.5 (Vulkan + DX12 runtimes) going **stable** (it is preview.6 today). The moment 3.8.5 ships stable this validates (Areas C/D). Not a "won't do", a wait on someone else's release. |
| **Metal target** | ⬛ | ShadowDusk Metal is a stub; FNA's Metal backend is already covered by the one `.fxb`. |

---

## Sources / cross-references

- Engine state (June 2026, primary-source): [`PHASE-35-appendix/shader-pipeline-landscape-2026-06.md`](../plan/PHASE-35-appendix/shader-pipeline-landscape-2026-06.md).
- Evidence-ladder definition + backend pipeline table: [`the-purpose.md`](the-purpose.md).
- Forward-version (v11/KNIFX/Vulkan/DX12) status: [`PHASE-35-forward-version-support.md`](../plan/PHASE-35-forward-version-support.md).
- KNI WebGL render harness: `tests/ShadowDusk.BrowserTests/README.md` (Phase 24).
- Cross-OS byte-identity rationale: `tests/fixtures/golden/byte-identity/README.md`.
