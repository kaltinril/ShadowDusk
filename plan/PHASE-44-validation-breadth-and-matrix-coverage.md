# Phase 44 — Validation breadth & matrix coverage

**Status:** ✅ **Effectively done (2026-06-15)** — every in-scope item is closed; the only
remaining work is **one externally-blocked** item documented below (per this phase's own
Definition of Done, a cell may be "blocked on a real external dependency"). **A done; B (VTF)
done; B (texture-array render) resolved as N/A-not-ours — our DXBC is correct + pinned, MonoGame
just has no public array-binding API to render through (a MonoGame limitation, closed from our
side); C: GL render gates wired into CI 2026-06-15, DX/FNA/KNI-DX render-in-CI need a Windows
software-driver/WARP story (external) but are now a baked-in local pre-release gate
(`validation/run-windows-render-gates.ps1`); D (KNI v4.02 render) done across all three KNI
paths: desktop GL 2026-06-14, DirectX 2026-06-15, WebGL Reach + HiDef refreshed 2026-06-15.**
Owns the living [validation matrix](../docs/validation-matrix.md).
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
- **`Texture2DArray`: ✅ resolved (render N/A, not our gap)** — ShadowDusk compiles the array shader to
  valid DXBC, pinned by item A. A non-vacuous render can't be set up because **MonoGame's public API exposes
  no `Texture2DArray` to bind** to the shader's array sampler. That is a MonoGame *runtime-API* limitation,
  not a ShadowDusk gap, so this is **closed from our side** (our part is done and proven). Revisit only if a
  MonoGame array-binding path lands (3.8.5+?) or via a non-MonoGame DX11 harness.

### C. CI-ify the real-engine render gates — ✅ GL in-process gates wired; DX/FNA/KNI-DX remain
Promote the manual `validation/*` render gates (MonoGame GL/DX, FNA) into CI jobs where a software/headless
driver exists.

**Done (2026-06-15):** the three in-process, **self-asserting GL** render gates — `validation/StateFidelity`
(Phase 43 render-states/annotations/sampler-states), `validation/CbufferModel` (Phase 43C cbuffer + array
model), and `validation/TextureBreadthValidation` (Phase 34 cube + 3D texture) — now run in CI on ubuntu
under **xvfb + Mesa llvmpipe** via the new **`.github/workflows/validation-render.yml`** (push-to-main +
manual dispatch + the `run-validation-render` PR label, mirroring the integration-tests cadence). They need
no Python compare and no baseline-generation step (each loads the committed mgfxc goldens itself and exits
non-zero on any over-tolerance row), and a GL-init failure is a natural red (MonoGame throws
`NoSuitableGraphicsDevice`), never a silent skip. This is the same DesktopGL-on-llvmpipe recipe `wasm.yml`'s
reference renderer already proves in CI. (The `ShadowDusk.ImageTests` GL render was already in CI; this adds
the heavier real-engine `validation/*` GL gates.) All three were re-confirmed passing locally on real GPU GL
(exit 0) before wiring.

**Remaining:** the **DX / FNA / KNI-DX** render gates (`validation/CandidateDx`, `VsDrivenDx`,
`DxModernFeatures`, `FnaValidation`, `KniWinFormsDX`) need a **Windows runner with a software D3D driver
(WARP)** — unverified, so deliberately not wired yet. The first push-to-main run of `validation-render.yml`
will confirm the GL lane green on a real GitHub runner.

### D. KNI v4.02 render validation — ✅ desktop done (2026-06-14); WebGL refresh + KNI DirectX remain
Add a KNI **desktop** (`SDL2.GL`) render check and refresh the Phase-24 browser harness against **KNI v4.02**,
so KNI stops being browser-only/dated in the matrix. This is Phase 35 Area B's **reproduce-first** step: prove
ShadowDusk's v10 output **loads + renders pixel-equivalent in real KNI v4.2.9001** to establish the baseline
rig. KNIFX is a **committed** additive deliverable (per the 2026-06-14 direction), so this harness is not a
"decide whether KNIFX is needed" gate, it is the **validation rig the faithful KNIFX writer will be checked
against** (v10 baseline first, then KNIFX output on the same rig).

**Done — desktop (`validation/KniDesktopGL`, 2026-06-14):** a new harness compiles the 10-shader SM3 PS corpus
with the unchanged `EffectCompiler` (default -> v10 GL) and loads those bytes into a **real KNI `Effect`
v4.2.9001 on SDL2.GL**. A runtime-integrity guard asserts the XNA assembly is KNI's (`Xna.Framework.*`
4.2.9001.x), not MonoGame's, so a render can't be mislabeled. Result: **10/10 load + render**; pixel-compared
(`compare_kni.py`, GL<->GL, tol 4/255) the KNI render is **maxd 0 vs the MonoGame render of the same bytes**
and **<= maxd 1 vs the mgfxc goldens** (Scanlines/Dots differ by 1, driver rounding). So v10 is **render-proven
on the current KNI v4.02 desktop runtime** -> matrix §1 KNI OpenGL cell promoted to ✅. The packages
(`nkast.Xna.Framework[.*]` + `nkast.Kni.Platform.SDL2.GL` @ 4.2.9001.\*) restore from nuget.org; the project is
not in `ShadowDusk.slnx` and opts out of central package management. README: `validation/KniDesktopGL/README.md`.

**Done — KNI DirectX (`validation/KniWinFormsDX`, 2026-06-15):** the DX analogue of `KniDesktopGL`. It
compiles the 10-shader SM5 PS-only corpus with the unchanged `EffectCompiler` (DirectX target -> DXBC SM5 in
an MGFX v10 container) and, in **one real KNI `Effect` v4.2.9001 on WinForms.DX11**, loads **both** those
bytes **and** the committed mgfxc DirectX goldens (`tests/fixtures/golden/DirectX_11/*.mgfx`, the control),
renders each through the identical SpriteBatch path, and pixel-compares the two arms **in process**
(self-asserting exit code). The same runtime-integrity guard asserts the XNA assembly is KNI's
(`Xna.Framework.*` 4.2.9001.x), not MonoGame's. Result: **10/10 loaded + rendered + matched mgfxc** in real
KNI DX11, maxd 0 for 9 shaders and maxd 1 on Dots (driver rounding) -> matrix §1 KNI/DirectX cell promoted
to ✅. `WinForms.DX11` is the only KNI DX platform published at 4.2.9001 (no SDL2.DX11), so the harness is
`net8.0-windows` + `UseWindowsForms` + an `[STAThread]` `Main`. README: `validation/KniWinFormsDX/README.md`.

**Done — KNI WebGL refresh (2026-06-15).** The sample's `nkast.Kni.Platform.Blazor.GL` pin was already at
`4.2.9001.*` (resolved `4.2.9001.2`), so the gap was that the last *run* pre-dated it. Re-ran the Phase-24
Playwright harness on the current pin: **mode-1 Reach (`--corpus=sd`) 10/10 load + render** of ShadowDusk's
own `.mgfx` in real KNI WebGL, and **HiDef/WebGL2 (`--corpus=sd-hidef`) 10/10 GREEN** with the issue-#7
`gl_FragColor` regression guard holding (no `RESULTS-SD-HIDEF-REPRO.md`), both within the Phase-17 §6.1
tolerance vs the DesktopGL render of the same bytes. To stop the dated-ness ambiguity from recurring, the
harness now **stamps the KNI pin into every generated `RESULTS`** (`run-harness.mjs` reads the version from
the sample csproj) — `RESULTS-SD.md` / `RESULTS-SD-HIDEF.md` now record `nkast.Kni.Platform.Blazor.GL
4.2.9001.*`. Matrix §1 KNI/OpenGL browser note + §2 HiDef row + §3 Web row updated to current-on-v4.02.

**Remaining (externally blocked, out of this phase's control):** the **DX/FNA/KNI-DX render-in-CI** gates
need a Windows runner + a software D3D driver (WARP) — see item C. Desktop + browser GL render-in-CI is
done.

## Gating
- A + B: doable now (no external blocker).
- C: GL is already CI'd; DX/FNA render-in-CI needs a driver story on the runners.
- D: needs a KNI v4.02 web/desktop harness.

## Definition of done
Each matrix cell is either backed by a passing programmatic test (compile or render) or documented as blocked
on a real external dependency (no runtime / preview-only engine), with the matrix doc updated to match.
