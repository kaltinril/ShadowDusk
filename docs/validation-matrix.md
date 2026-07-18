# ShadowDusk Validation Matrix

**Purpose:** one place to *definitively* track what ShadowDusk is proven to do, across every runtime
library, shader format/version, graphics target, and OS, and to mark cells off as they advance. This is a
**living checklist**: update a cell's status (and the date) whenever its evidence changes.

**Last updated:** 2026-07-18 (**Phase 32 Vulkan render-PROVEN** — real MonoGame 3.8.5 DesktopVK, §1 / §7 updated; ShadowDusk's own Vulkan output renders correctly 10/10, but a pixel-diff vs `mgfxc`'s own Vulkan output is blocked by a confirmed, separate MonoGame `SlotOffset` bug — see `plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md`). Prior: 2026-06-28 (**Phase 50 Android on-device compile PROVEN** — §3 / §7 updated) · 2026-06-27 (Phase 49 + Phase 41 GAP-1 FNA + issue #107 + Phase 35 `__KNIFX__`). **Phase 50 (PROVEN 2026-06-28):** **Android** added to the OS list — a real `pixel_7` API-34 emulator compiled an HLSL string to a `.mgfx` and loaded it into a live MonoGame `Effect` **in memory, at runtime, on the device** via seamless `new EffectCompiler()` (`validation/AndroidGl`, green-screen). **DXC was cross-compiled for Android** (arm64-v8a + x86_64) by porting the `.wasm-build` recipe to the NDK (`.wasm-build/build-dxc-android.ps1`); SPIRV-Cross is a stock NDK build. The three green-making fixes: APK-`lib/<abi>/` bare-SONAME loader branches; `ExcludeAssets="native"` dropping the desktop NuGets' wrong-platform linux `.so`; and `EffectCompiler` auto-selecting the managed `SpirvReflector` on Android (the default DXIL-oracle reflection throws there; byte-identical, so `new EffectCompiler()` stays seamless). Desktop unchanged, full `dotnet test` green (1951). The `android-arm64` natives are now **hosted + SHA-pinned in `tools/restore.*` + packed into `ShadowDusk.HLSL`/`.GLSL` (shipping in 0.11.0)**, so a NuGet consumer is self-contained and the release / `pack-consume` CI hard-gates their presence; open follow-ups: the automated `dxc-android-build.yml` CI build, on-device pixel-vs-`mgfxc` render diff. See [`plan/PHASE-50-android-runtime-support.md`](../plan/PHASE-50-android-runtime-support.md) §6.2. **Phase 35 `__KNIFX__` (Vic / Gum):** KNI's compiler always defines `__KNIFX__=1` and real KNI shaders (Apos.Shapes/Gum) branch on it; ShadowDusk now defines `__KNIFX__` when targeting the **KNIFX** container (only), so a KNIFX compile takes the same branch KNI's compiler does, while the universal MGFX default stays `__KNIFX__`-free (seamless). Byte-identity safe; pinned by `PlatformMacrosTests` + `CapabilityProfileByteIdentityTests` + `examples/ExKnifxMacro.fx`. **(1)** Vendored the **Gum / Apos.Shapes** shaders the Gum ecosystem actually ships as compile-level regression fixtures (vchelaru's request): `apos-shapes.fx` (Gum's SDF renderer) compiles on **GL + DX**; classified by an actual probe (`ThirdPartyShaderCorpusTests`, §6). **(2)** **Phase 41 GAP-1 closed on the FNA path** (macro-defined `TECHNIQUE()` techniques are now recovered in `RunFna`): **7 MonoGame stock effects now compile on FNA** (SpriteEffect, AlphaTestEffect, DualTextureEffect, Penumbra*); byte-identity preserved, Windows render gates green (DX/DX-modern/KNI-DX/FNA 17/17). **(3)** **Issue [#107](https://github.com/kaltinril/ShadowDusk/issues/107) now CLOSED + WebGL render-proven (2026-06-27):** the `do{}while(false)`-from-nested-`if`-early-return that WebGL rejected at load is lowered to a bounded for-loop (Rule 9) and the repro now **loads + renders `maxDelta=0` in real KNI WebGL on both Reach and HiDef** via the browser harness (CI-covered). **Gaps recorded in §7:** **GAP-2** (DeferredSprite MRT `: COLOR0/1` output, now closed at compile + structural-match; MRT render proof is a desktop-only rung), **GAP-1 on GL** (macro-defined techniques; faithfulness-blocked, not just hard, see §7), and **KNIFX-on-KNI-WebGL** (now CLOSED 2026-06-27: `KnifxWriter` emits a multi-backend GL-family directory so one `.knifx` loads on KNI desktop GL + mobile + WebGL, the WebGL body using KNI's proven runtime ES converter; render-proven both hosts, §7). Prior: 2026-06-18 (Phase 45 B10 reserved-word free-uniform render-proven through MGFX v11 / KNI v10 / KNIFX v11); 2026-06-17 (B10 GL render gate; issue #106 parser regression); 2026-06-15 (Phase 44: KNI DirectX render-proven; GL render gates wired into CI).

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
| **MonoGame** | ✅ DXBC SM5 (MGFX v10) — rendered on Windows vs `mgfxc` + `fxc` oracle (PS corpus + VS-matrix) | ✅ GLSL (MGFX v10) — rendered on Linux (Mesa) + Windows vs `mgfxc` (PS corpus + VS-driven) | ✅ SPIR-V (MGFX v11, profile 80) — **render-proven on real MonoGame 3.8.5 DesktopVK**: ShadowDusk's own Vulkan output loads + renders correctly (10/10 corpus, visually confirmed) via `validation/CandidateVulkan` (Phase 32, 2026-07-18). **No pixel-diff vs `mgfxc`'s own Vulkan output exists**: real mgfxc's compiled output crashes on all 10 shaders in real DesktopVK due to a confirmed, independent MonoGame bug (`SlotOffset` arithmetic wraparound) — the oracle is unavailable, not a ShadowDusk gap; see `plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md` | ⬛ MSL — ShadowDusk Metal target is a **stub** |
| **KNI** | ✅ DXBC SM5 (MGFX v10) — **render-proven on KNI v4.2.9001 (WinForms.DX11)**: ShadowDusk's DX output loads + renders **pixel-equivalent to the mgfxc DX golden (maxd <= 1, driver rounding on Dots only)** across the 10-shader corpus in real KNI DX11 (`validation/KniWinFormsDX`, 2026-06-15) | ✅ GLSL (MGFX v10) — **render-proven on KNI v4.2.9001 desktop (SDL2.GL)**: v10 loads + renders **pixel-identical to MonoGame (maxd 0)** and **within maxd 1 of the mgfxc goldens** across the 10-shader corpus (`validation/KniDesktopGL`, 2026-06-14). Also **browser-proven on the current KNI v4.2.9001.2 WebGL/Blazor runtime** (refreshed 2026-06-15: 10/10 of ShadowDusk's own `.mgfx` load + render in real KNI WebGL Reach **and** HiDef/WebGL2, within the Phase-17 §6.1 tolerance vs the DesktopGL render of the same bytes; `tests/ShadowDusk.BrowserTests`) | — KNI ships **no Vulkan** platform | — KNI iOS uses **GL**, no Metal |
| **FNA** | ✅ — *one* fx_2_0 `.fxb` serves **all** FNA3D backends (D3D11 / Vulkan / OpenGL / Metal) via FNA3D + MojoShader; rendered vs `fxc /T fx_2_0` (PS + VS-driven) | ✅ (same `.fxb`) | ✅ (same `.fxb`) | ✅ (same `.fxb`) |

**Reading it:** the rock-solid, render-proven cells today are **MonoGame OpenGL, MonoGame DirectX, FNA
(all backends), KNI OpenGL on the current v4.02 desktop runtime** (`validation/KniDesktopGL`, 2026-06-14),
**and now KNI DirectX on the v4.02 WinForms.DX11 runtime** (`validation/KniWinFormsDX`, 2026-06-15). The
remaining honest gap is the **modern DirectX features** (next section). The KNI WebGL proof was
refreshed on the current v4.02 runtime on 2026-06-15 (Reach + HiDef, 10/10). **MonoGame Vulkan
(DesktopVK)** joined the render-proven set on 2026-07-18 (Phase 32) with one asterisk: it's
ShadowDusk's own output proven correct against real DesktopVK, not a pixel-diff against `mgfxc`'s
Vulkan output — that oracle currently can't render at all on this corpus (a separate, confirmed
MonoGame bug), so the comparison itself is blocked upstream.

## 2. Shader format / version / graphics profile

> **Format roadmap (committed):** ShadowDusk will emit **v10 *and* v11 *and* KNIFX**, each a real, faithful
> output a consumer can select and use, not just v10 with a promise that newer runtimes still load it. v10
> stays the **default** because it loads on every MGFX-lineage runtime (the seamless baseline), but v11 and
> KNIFX are **first-class additive outputs** so consumers can use the newer containers' features (and take the
> bytes for their own runtime if they like). Forward-compatibility of v10 is a nice-to-have, **not** a reason
> to stop short of v11/KNIFX. The newer formats are opt-in or auto-selected from the target, **never a flag a
> consumer must set to get correct output** (the seamless rule still holds). **All three are now BUILT and
> render-proven (2026-06-14):** v10 (default, everywhere), **MGFX v11** (`MgfxVersion = 11`, render-proven in
> MonoGame 3.8.5), and **KNIFX v11** (`Container = Knifx`, render-proven in KNI 4.02). **The
> auto-select/override seam is now built too:** a closed `CapabilityProfile` set names each
> (runtime, format) contract, `CompilerOptions.Profile` (and CLI `--target-runtime`) selects one (a
> profile implies its backend), and `RuntimeProfileDetector` recommends a proven profile from the
> loaded framework assembly, all byte-identical when unset. A `ShaderFeatures` axis
> (`ShaderFeatureSupport.Validate`) **rejects loudly with `SD0201`** any shader that needs a capability
> no shipping runtime supports yet (e.g. vertex texture fetch / texture arrays on the GL/MojoShader
> path), so an unsupported feature fails at compile rather than silently mis-rendering. KNIFX
> `columnsActual` is now **validated
> against a KNIFXC golden** (full matrices match exactly; the partially-used-matrix divergence is
> render-safe, a storage-only difference, see the KNIFX spec).

| Format / profile | Applies to | Status | Notes |
|---|---|---|---|
| **MGFX v10** | MonoGame, KNI | ✅ | The default and the basis of every ✅ above. The one container every MGFX-lineage runtime loads. |
| **MGFX v11** (MonoGame) | MonoGame 3.8.5+ | ✅ **render-proven (MonoGame 3.8.5)** | ShadowDusk emits a **faithful MGFX v11** via `CompilerOptions.MgfxVersion = 11`: the v10 body plus the two per-shader diagnostic strings (`SourceFile`, `Entrypoint`) MonoGame PR #8813 added. **Render-proven 2026-06-14**: loads + renders **10/10 in real MonoGame 3.8.5.0** (`validation/MonoGameV11`), **maxd 0 vs v10**, <= 1 vs the mgfxc goldens. **Opt-in only** (3.8.5 is pre-release; the default stays v10). The old header-byte `--mgfx-version 11` was actually **corrupt** (v10 body + version 11 desyncs a v11 reader); now fixed. **The Phase 45 B10 reserved-word free-uniform binding is also render-proven through v11 (2026-06-18):** `float noise;` (a GLSL reserved word, SPIRV-Cross-renamed `_noise` on the GL path) stays exposed under its original name and exactly drives the output (`noise=0.25 -> grey 64`, `0.75 -> 191`, both `dExpected 0`; differentiation 127) in real MonoGame 3.8.5.0 via `validation/MonoGameV11 -- reservedword` (the B10 fix lives in the shared GL container path, so it flows into v11). Spec: [`DONE/PHASE-35-appendix/mgfx-v11-format-spec.md`](../plan/DONE/PHASE-35-appendix/mgfx-v11-format-spec.md). |
| **KNIFX v11** (KNI) | KNI 4.02+ | ✅ **render-proven (corpus); feature parity pending** | ShadowDusk **emits KNIFX v11** today (`CompilerOptions.Container = Knifx`, `KnifxWriter`): signature `KNIF`, multi-backend directory, packed-int body, GL GLSL-version directory. **Render-proven 2026-06-14**: the KNIFX corpus **loads + renders 10/10 in real KNI v4.2.9001** (`validation/KniDesktopGL knifx`), **maxd 0 vs the v10 render**. **Also render-proven on KNI WebGL (2026-06-27):** `KnifxWriter` now emits a **multi-backend GL-family directory** (OpenGL + GLES + WebGL) so one `.knifx` loads on KNI desktop GL + mobile + browser; the GLES/WebGL body uses `ShaderVersion (0,0)` + raw GLSL so KNI's runtime ES converter (the proven v10 path) handles the browser dialect. The KNIFX container now loads + renders the `__KNIFX__` branch in real KNI WebGL (`tests/ShadowDusk.BrowserTests --corpus=sd`, `ExKnifxMacro` red 196096 / green 0); desktop OpenGL body byte-identical (no regression), §7. **The Phase 45 B10 reserved-word free-uniform binding is also render-proven through KNIFX, and through MGFX v10 in KNI (2026-06-18):** `float noise;` stays exposed under its original name and exactly drives the output (`noise=0.25 -> grey 64`, `0.75 -> 191`, both `dExpected 0`) in real KNI v4.2.9001 via `validation/KniDesktopGL -- reservedword` (KNI MGFX v10) and `... -- reservedword knifx` (KNIFX). Still **opt-in / experimental** (not auto-selected yet). The optimized-`Matrix4x4` `columnsActual` fix is now **validated against a KNIFXC golden** (2026-06-14, KNIFXC built from source `kni/Tools/EffectCompiler`): a full `float4x4` golden writes `columnsActual=4`, **identical** to ShadowDusk; a partially-used `float4x4` (`(float3x3)World`) golden writes `columnsActual=3` while ShadowDusk writes `4`, but that is **render-safe** (ShadowDusk's GLSL and `columnsActual` come from the same reflection, so they stay consistent, a storage-efficiency divergence, not wrong pixels). Closing the partial-matrix case exactly is a non-goal; the sampler-without-texture fix is not golden-checked yet. KNIFX = a new container + those parity fixes over a still-MojoShader body. **Compile-macro fidelity (2026-06-27):** a KNIFX compile now defines **`__KNIFX__=1`** (only for the KNIFX container), matching KNI's compiler, so KNI shaders that branch on it (KNI's `Macros.fxh`; Apos.Shapes/Gum) take the correct branch; the universal MGFX default stays `__KNIFX__`-free. Spec: [`DONE/PHASE-35-appendix/knifx-format-spec.md`](../plan/DONE/PHASE-35-appendix/knifx-format-spec.md). |
| **Reach** (WebGL1 / GL ES 1.00) | MonoGame, KNI GL | ✅ | The default GL output dialect. |
| **HiDef** (WebGL2 / GL ES 3.00) | KNI GL | 🌐 (current) | The `#version 300`-guarded output; **browser-proven on KNI v4.2.9001.2** (refreshed 2026-06-15, `--corpus=sd-hidef`): 10/10 of ShadowDusk's own `.mgfx` load + render in a real KNI HiDef/WebGL2 context, and the issue-#7 `gl_FragColor` regression guard holds GREEN. |

## 3. OS coverage (where the proof actually ran)

ShadowDusk's **output bytes are OS-independent** (proven byte-identical on Windows/Linux/macOS by
`CrossHostByteIdentityTests`), so compiling is cross-OS-solved. What varies by OS is where a **render** was
actually run.

| OS | Compile | Render proof that ran here |
|---|---|---|
| **Windows** | ✅ (CI) | ✅ DirectX (validation harnesses) + ✅ FNA (`fxc` oracle) + ✅ **KNI OpenGL desktop** (`validation/KniDesktopGL`, real SDL2.GL driver). MonoGame GL render **soft-skips** in CI (runners expose only GDI Generic GL), but renders on a real desktop driver. |
| **Linux** | ✅ (CI, byte-identical) | ✅ OpenGL (Mesa software GL, the `ShadowDusk.ImageTests` suite runs in CI here). |
| **macOS** | ✅ (CI, byte-identical) | (no separate render run — transferred via byte-identity: the output equals the Windows/Linux bytes, so their render proofs carry over). |
| **Web (WASM / Blazor)** | ✅ (in-browser DXC+SPIRV-Cross frontend, Phase 23) | 🌐 (current) KNI WebGL (Phase 24 Playwright harness) **refreshed on KNI v4.2.9001.2, 2026-06-15** (Reach + HiDef, 10/10). |
| **Android** (on-device compile) | 🟢 **compile-proven on-device (Phase 50, 2026-06-28)** — NDK-built DXC + SPIRV-Cross (`arm64-v8a` + `x86_64`), seamless `new EffectCompiler()` | 🟦 **load-proven on a real `pixel_7` API-34 emulator**: an HLSL string compiled **in memory, on the device** → 410-byte `.mgfx` → loaded into a live MonoGame `Effect` (`validation/AndroidGl`, green-screen success). DXC cross-compiled for Android via `.wasm-build/build-dxc-android.ps1`; `EffectCompiler` auto-selects the managed `SpirvReflector` on Android (byte-identical to DXIL). Remaining rung: on-device **pixel-vs-`mgfxc` render diff** (harness today proves compile + `Effect` load, not a pixel compare). Build-time precompile also works (OS-independent bytes). |

## 4. The modern-DirectX-features sub-checklist (called out because it is a live gap)

The DirectX target is **not** MojoShader-limited (that limit is OpenGL-only). It already *compiles* the SM4/5
features the GL path rejects, but their **render** is not yet proven.

| Feature | Compiles (DirectX) | Rejected on OpenGL | Rendered + matched on DirectX |
|---|---|---|---|
| Vertex texture fetch (`SampleLevel` in VS) | ✅ (exit 0) | ✅ `SD0210` (correct) | ✅ **render-proven** — vkd3d == `fxc` oracle at **maxd 0** in real MonoGame WindowsDX, VTF actually deforms the mesh (`validation/DxModernFeatures`, 2026-06-14) |
| `Texture2DArray` | ✅ (exit 0) | ✅ `SD0210` (correct) | ✅ **our part done** / render **N/A (not our gap)** — ShadowDusk compiles it to valid DXBC (pinned by `ValidationMatrixCoverageTests`); a render can't be set up because **MonoGame exposes no public `Texture2DArray` binding API**. A MonoGame runtime limitation, **closed from ShadowDusk's side**; revisit only if MonoGame adds array binding. |

VTF is closed (render-proven). The texture-array render is **N/A from ShadowDusk's side** — our compile is
correct and pinned; the render simply can't be exercised because MonoGame has no public array-binding API, a
MonoGame limitation rather than a ShadowDusk gap. The compile rung for both is pinned by
`ValidationMatrixCoverageTests`; VTF render by `validation/DxModernFeatures`.

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
| **FX language-construct parse regression** (relational operators, ternaries, shifts, helper functions in shader bodies; issue #106 `FX0001` false positive) | `tests/ShadowDusk.HLSL.Tests` (`FxPreParser` unit cases) + the regression `.fx` fixture corpus compile | ✅ | `dotnet test tests/ShadowDusk.HLSL.Tests` (full corpus: `dotnet test ShadowDusk.slnx`) |
| **Matrix compile/reject claims** (this doc's compile rung: GL rejects VTF/arrays `SD0210`, DX compiles them, FNA rejects SM4 `SD0300`) | `ValidationMatrixCoverageTests` | ✅ | `dotnet test ...Integration.Tests --filter ValidationMatrixCoverage` |
| **Third-party vendored corpus** (real shipping shaders compile to a well-formed container on their classified targets): Nez (MIT) + **Gum / Apos.Shapes (MIT, Phase 49)** — incl. `apos-shapes.fx` Gum SDF renderer on GL+DX. Compile-level, not pixel-equivalence (no goldens for these). | `ThirdPartyShaderCorpusTests` (+ `FnaCompileFixtureTests.Sm3Corpus`, `Phase41StructuralDivergenceMatrixTests` auto-glob) | ✅ | `dotnet test ...Integration.Tests --filter ThirdPartyShaderCorpus` |
| **Macro-defined techniques (`TECHNIQUE()` idiom) recovered** — DX (existing) + **FNA (Phase 41 GAP-1, 2026-06-27)**: the SM2-fitting MonoGame stock effects compile on FNA to valid fx_2_0; BasicEffect/SkinnedEffect → honest `SD0305` (SM2 register pressure), Gum FnaSample → `SD0300` (sub-SM2 `vs_1_1`). Compile-level (the 7 new FNA effects are not render-proven vs `fxc` goldens). | `Phase41MacroTechniqueTests` (DX + FNA cases) | ✅ | `dotnet test ...Integration.Tests --filter Phase41MacroTechnique` |
| **Cross-OS byte-identical** output (Win/Linux/Mac; GL/DX/FNA) | `CrossHostByteIdentityTests` | ✅ (all 3 OSes) | `dotnet test ...Integration.Tests --filter CrossHostByteIdentity` |
| OpenGL render vs golden (software GL) | `tests/ShadowDusk.ImageTests` (incl. `MatrixConventionSweepTests`, `Issue70MatrixTransposeRenderTests`) | ✅ (Linux Mesa) | `dotnet test tests/ShadowDusk.ImageTests` |
| **Real MonoGame OpenGL** render vs `mgfxc` | `validation/VsDriven`, `validation/Candidate` + `validation/compare.py` | manual | `dotnet run --project validation/VsDriven` |
| **Real MonoGame OpenGL** cube + 3D/volume texture render (rung-4; cube full, 3D single-voxel) | `validation/TextureBreadthValidation` (in-process assert) | ✅ (`validation-render.yml`, ubuntu/llvmpipe) | `dotnet run --project validation/TextureBreadthValidation` |
| **Real MonoGame OpenGL** pass render-states / annotations / baked sampler-states load + render vs `mgfxc` golden (Phase 43) | `validation/StateFidelity` (in-process compare) | ✅ (`validation-render.yml`, ubuntu/llvmpipe) | `dotnet run -c Release --project validation/StateFidelity` |
| **Real MonoGame OpenGL** cbuffer + array-parameter-by-name model load + render vs `mgfxc` golden (Phase 43C) | `validation/CbufferModel` (in-process compare) | ✅ (`validation-render.yml`, ubuntu/llvmpipe) | `dotnet run -c Release --project validation/CbufferModel` |
| **Real MonoGame OpenGL** reserved-word free uniform (`float noise;`) binds by name + drives output on the B10 offset-bridge path (Phase 45 B10; exact expected pixels, plus pixel-equiv vs `mgfxc` when present) | `validation/ReservedWordGl` (in-process assert) | ✅ (`validation-render.yml`, ubuntu/llvmpipe) | `dotnet run -c Release --project validation/ReservedWordGl` |
| **Real MonoGame DirectX** render vs `mgfxc`/`fxc` | `validation/VsDrivenDx`, `validation/Candidate{Dx,Vkd3d}` + `compare_dx.py` | manual | `dotnet run --project validation/VsDrivenDx` |
| **DirectX modern features render** (vertex texture fetch; vkd3d vs `fxc`) | `validation/DxModernFeatures` | manual | `dotnet run --project validation/DxModernFeatures` |
| **Real FNA** render vs `fxc /T fx_2_0` | `validation/FnaValidation` | manual | `dotnet run --project validation/FnaValidation` |
| Forward-compat (newer MonoGame loads our v10) | `validation/ForwardCompat` | manual | `validation/ForwardCompat/run-forwardcompat.ps1` |
| **KNI WebGL** render (browser; 11-shader SD corpus = the 10 PS-only + the #107 do-while repro, Reach + HiDef) | `tests/ShadowDusk.BrowserTests` (Playwright) | manual / CI (`browser-smoke`) | see `tests/ShadowDusk.BrowserTests/README.md`; `node run-harness.mjs --corpus=sd` (Reach) / `--corpus=sd-hidef` (WebGL2) |
| **Real KNI OpenGL desktop** render vs mgfxc + MonoGame (KNI v4.02, SDL2.GL) | `validation/KniDesktopGL` + `compare_kni.py` | manual | `dotnet run --project validation/KniDesktopGL` then `python validation/compare_kni.py` |
| **Real KNI DirectX** render vs mgfxc (KNI v4.02, WinForms.DX11; in-process compare) | `validation/KniWinFormsDX` | manual (Windows DX11) | `dotnet run --project validation/KniWinFormsDX -c Release` |
| **Real KNI OpenGL VS-driven** render vs mgfxc (issue #70: matrix transpose + legacy `: POSITION`, KNI v4.02 SDL2.GL) | `validation/KniVsDriven` (in-process compare) | manual | `dotnet run --project validation/KniVsDriven` |
| **KNIFX v11** render in real KNI (vs v10) | `validation/KniDesktopGL knifx` + `compare_kni.py` | manual | `dotnet run --project validation/KniDesktopGL -- knifx` |
| **MGFX v11** render in real MonoGame 3.8.5 (vs v10 + goldens) | `validation/MonoGameV11` + `compare_mgfxv11.py` | manual | `dotnet run --project validation/MonoGameV11` then `... -- v10`, then `python validation/compare_mgfxv11.py` |
| **Phase 45 B10 reserved-word free uniform (`float noise;`) binds by name + drives output through MGFX v11 / KNI MGFX v10 / KNIFX** (in-process assert; same non-vacuous bar as `ReservedWordGl`: `noise=0.25 -> grey 64`, `0.75 -> 191`, differentiation 127). Re-proves B10 in the other containers the shared GL container path writes (v10 GL alone is `ReservedWordGl` above). Shared probe `validation/Shared/ReservedWord/ReservedWordProbe.cs`. | `validation/MonoGameV11 -- reservedword` (MGFX v11, real MonoGame 3.8.5); `validation/KniDesktopGL -- reservedword` (MGFX v10, real KNI v4.02); `validation/KniDesktopGL -- reservedword knifx` (KNIFX v11, real KNI v4.02) | manual | `dotnet run --project validation/MonoGameV11 -- reservedword`; `dotnet run --project validation/KniDesktopGL -- reservedword`; `dotnet run --project validation/KniDesktopGL -- reservedword knifx` |

**The "test programmatically" goal:** the path to a fully self-checking matrix is (a) promote the
`validation/*` render gates into CI jobs (where a software/headless driver exists), and (b) back this matrix
with a machine-readable coverage manifest a test asserts against (so a cell cannot be marked ✅ without a
passing test). **(a) is now partly done (Phase 44 C):** the three in-process, self-asserting **GL** render
gates (`StateFidelity`, `CbufferModel`, `TextureBreadthValidation`) run in CI on ubuntu under
xvfb + Mesa llvmpipe via **`validation-render.yml`** (push-to-main + the `run-validation-render` PR label),
so a GL render regression turns the lane red. The **DX / FNA / KNI-DX** render gates stay manual — they
need a Windows runner with a software D3D driver (WARP), which is unverified, so they are deliberately not
wired yet (the remaining Phase 44 C tail).

## 7. Gaps & next targets (ordered)

| Gap | Achievable here? | Notes |
|---|---|---|
| **DirectX modern features render** | **done** | **VTF ✅** (`validation/DxModernFeatures`, vkd3d == `fxc` maxd 0). Texture-array render is **N/A from our side** (closed): our DXBC is correct + pinned, but MonoGame has no public `Texture2DArray` binding API to render through — a MonoGame limitation, not a ShadowDusk gap. |
| **KNI v4.02 render** (desktop `SDL2.GL` + DirectX + WebGL) | **done** | ✅ **Desktop SDL2.GL** (`validation/KniDesktopGL`, 2026-06-14): v10 maxd 0 vs MonoGame, ≤1 vs mgfxc goldens, 10/10. ✅ **DirectX WinForms.DX11** (`validation/KniWinFormsDX`, 2026-06-15): ShadowDusk DX vs mgfxc DX golden, maxd ≤1, 10/10. ✅ **WebGL Reach + HiDef** (`tests/ShadowDusk.BrowserTests`, refreshed 2026-06-15 on KNI v4.2.9001.2; corpus extended 2026-06-27): 11/11 of ShadowDusk's own `.mgfx` (the 10 PS-only + the **#107 do-while repro**) load + render in real KNI WebGL, issue-#7 HiDef guard GREEN, **#107 `maxDelta=0` on both profiles**. All three KNI render paths now proven on the current v4.02 line. The desktop rig is also Phase 35 Area B's reproduce-first baseline for the KNIFX writer. |
| **Promote `validation/*` render gates into CI** | partly **done** | **GL in-process gates wired into CI** (`validation-render.yml`: `StateFidelity` / `CbufferModel` / `TextureBreadthValidation` on ubuntu/llvmpipe, push-to-main + label). The `ShadowDusk.ImageTests` GL render already runs in CI. **DX / FNA / KNI-DX** render gates remain manual — they need a Windows runner + a software D3D driver (WARP), unverified. |
| **Machine-readable coverage** backing this matrix | **compile rung done** | `ValidationMatrixCoverageTests` pins the compile/reject cells as a `[Theory]`. Extending it to assert the render cells against the `validation/*` gates is the remaining step. |
| **MGFX v11 / KNIFX writers** | **committed, in progress** | Additive outputs we **will** emit so consumers can *use* the new-container features (KNIFX's XNA-compat/quality fixes; MonoGame v11's body), not a "won't do." v10 staying forward-compatible is a convenience, **not** a reason to skip these. Default stays v10 for universal load; v11/KNIFX are opt-in / auto-selected from the target, never required (seamless rule preserved). Path: reproduce-first render against KNI v4.02 (Phase 44 D) -> build the faithful writers (Phase 35 Area B). See [`DONE/PHASE-35-appendix/`](../plan/DONE/PHASE-35-appendix/). |
| **Vulkan render** | **done (2026-07-18, Phase 32)** | MonoGame 3.8.5 shipped `DesktopVK` stable; ShadowDusk's own Vulkan `.mgfx` (real container, profile 80) render-proven 10/10 on real DesktopVK, visually confirmed. **Pixel-diff vs `mgfxc`'s own Vulkan output remains blocked**: that output crashes on all 10 shaders in real DesktopVK due to a confirmed, independent MonoGame bug (`SlotOffset` arithmetic) — an external dependency on MonoGame fixing it, not a ShadowDusk gap. See `plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md`. |
| **DX12 render** | **ext-blocked** | The DXIL plumbing exists (Vulkan's SM6 sibling); render-validation is blocked on MonoGame's DX12 runtime going stable — untouched by the Vulkan work above. |
| **Metal target** | ⬛ | ShadowDusk Metal is a stub; FNA's Metal backend is already covered by the one `.fxb`. |
| **Issue [#107](https://github.com/kaltinril/ShadowDusk/issues/107) — `do{}while(false)` rejected by WebGL** | **CLOSED 2026-06-27 (now WebGL render-proven, both profiles)** | A helper with a nested `if` + early `return` (Vic's `TestEarlyReturn`) made SPIRV-Cross emit a one-shot `do { … break; … } while(false);` in the GL GLSL (the early `return` becomes `break`). The effect compiled + loaded on desktop GL but **WebGL/KNI Reach rejected the `do`-loop at load** (GLSL ES 1.00 Appendix A doesn't guarantee do-while). **Fixed** by `MonoGameGlslRewriter` **Rule 9** (the sibling of the Rule 8 `roundEven` lowering): each one-shot do-while is lowered to the WebGL1-safe `for (int _i = 0; _i < 1; _i++) { … }` — semantically identical (one iteration, `break`/`continue`/fall-through preserved → pixels unchanged) and the Appendix-A-allowed form (which `mgfxc`/MojoShader effectively use too). Proven do-while-free end-to-end on all 4 affected corpus shaders (ClipShader, ClipShaderSpriteTarget, apos-shapes, the #107 repro); byte-identity manifest regenerated for the 2 affected GL entries (DX/FNA bytes unchanged); rewriter unit + `HidefGeneralityFixtureTests` pipeline tests pin it; full suite green. **Remaining rung now CLOSED (2026-06-27):** the `#107` repro (`examples/Issue107DoWhile.fx`) is wired into the KNI WebGL browser harness (`tests/ShadowDusk.BrowserTests`, `--corpus=sd` + `--corpus=sd-hidef`) and **loads + renders `maxDelta=0` in real KNI WebGL on BOTH profiles** — Reach (WebGL1 / GLSL ES 1.00, the exact context that rejected the `do`-loop) and HiDef (WebGL2 / GLSL ES 3.00). ShadowDusk's OWN bytes, vs the desktop-GL render of the same bytes; the do-while-free GLSL is confirmed in the emitted `.mgfx`. CI auto-covers it (the `browser-smoke` job runs the same harness). Distinct from #106 (CLOSED, Phase 45 parser fix). |
| **Phase 41 GAP-2 — DeferredSprite MRT `: COLOR0/1` output on GL** | **closed at compile + structural-match (2026-06-27); MRT render proof = remaining rung** | The multi-render-target PS returns a struct with `: COLOR0/1` outputs; DXC's GL SPIR-V path rejected `COLOR` as a PS output (vkd3d/DX accepts it). **Fixed** by a **GL-only**, struct/entry-aware rewrite (`GlStructOutputColorRewriter`): it retargets only the PS-RETURN struct's members to `: SV_Target<n>` for the OpenGL DXC compiles (DX/vkd3d output **byte-identical**, verified by md5), never the PS-input interpolant `Color : COLOR0`. Plus a render-correctness fix in `MonoGameGlslRewriter`: true MRT (2+ outputs) now emits `gl_FragData[0]` for slot 0 (not `gl_FragColor`, which broadcasts to all attachments) — matching the mgfxc golden's `#define ps_oC0 gl_FragData[0]`/`[1]`. DeferredSprite now **compiles on GL and structural-matches its golden** (Phase 41 census `OpenGL = OK`). Pinned by `GlStructOutputColorRewriterTests` (unit) + `HidefGeneralityFixtureTests.DeferredSprite_Mrt_*` (end-to-end). **Remaining rung:** a true 2-attachment MRT render proof (bind 2 RTs, read back both, compare to mgfxc) needs a new render driver — the existing GL render gates are single-target only. A desktop MonoGame/DX driver could do it; **KNI WebGL cannot** (its Blazor backend exposes no public multi-render-target binding API to a consumer, the same shape as the MonoGame `Texture2DArray` limitation above), so the MRT render proof is a **desktop-only** rung. Tracked. |
| **Phase 41 GAP-1 — macro-defined techniques on GL** | **open (GL only; DX + FNA done) — faithfulness-blocked, not just hard** | DX (existing fallback) and **FNA (2026-06-27)** recover macro-defined `TECHNIQUE()` techniques. **GL stays gated**: its macro set lacks SM4/SM6, so the stock effects expand to a legacy DX9/SM2 branch that crashes DXC's SPIR-V codegen (uncatchable AV) — kept as a loud `SD0010` rather than a crash. **Why the obvious shortcut is wrong (analysis 2026-06-27):** the tempting fix — add SM4 to the GL macro recovery so the effect takes the modern branch DXC can compile — would **violate faithfulness**. `mgfxc`'s own OpenGL target also defines only `{MGFX, GLSL, OPENGL}` (no SM4; mirrored in `PlatformMacros.For(OpenGL)`), so `mgfxc` compiles these stock effects from the **legacy SM2 branch** (via `fxc` + MojoShader). Forcing ShadowDusk onto the modern branch would produce a *different* effect than `mgfxc` emits on GL — a silent divergence from the "identical to `mgfxc`" promise. A genuinely faithful GL fix would need DXC to not crash on the legacy SM2 HLSL (or a managed legacy→modern transcription that is provably behavior-preserving), neither of which is a small change. Pinned by `GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap`. Not a blocker for Vic: these effects compile on **DX and FNA** (the backends Gum targets), and macro-technique recovery is proven there. |
| **Android — compile `.fx` on-device** | 🟢 **PROVEN on a real emulator (Phase 50, 2026-06-28); productionization follow-ups open** | **On-device, in-memory, runtime compile works**: a `pixel_7` API-34 emulator ran `validation/AndroidGl` — plain `new EffectCompiler().CompileAsync(hlslText, OpenGL)` compiled an HLSL string → 410-byte `.mgfx` → live MonoGame `Effect`, all on the device (green-screen). **DXC was cross-compiled for Android** (`arm64-v8a` + `x86_64`) by porting the `.wasm-build` recipe to the NDK (`.wasm-build/build-dxc-android.ps1`; the one new fix is `LLVM_INFERRED_HOST_TRIPLE` to bypass `config.guess`); SPIRV-Cross is a stock NDK CMake build. Three fixes took it to green: the APK-`lib/<abi>/` bare-SONAME loader branches; `ExcludeAssets="native"` to drop the desktop NuGets' wrong-platform linux `.so`; and `EffectCompiler` auto-selecting the managed `SpirvReflector` on Android (the default DXIL-oracle reflection throws there) — byte-identical, so `new EffectCompiler()` stays seamless. Desktop unchanged; full `dotnet test` green (1951). The `android-arm64` natives are now **hosted on the `native-dxc-1.7.2212.40` tag, SHA-pinned in `tools/restore.*`, and packed into `ShadowDusk.HLSL` / `ShadowDusk.GLSL` under `runtimes/android-arm64/native/` (shipping in 0.11.0)** — so a NuGet consumer is self-contained and the release / `pack-consume` CI hard-gates their presence. **Remaining (not blockers):** author `dxc-android-build.yml` to rebuild the natives in CI, host the `x86_64` emulator natives (a local-only dev convenience today), and an on-device **pixel-vs-`mgfxc` render diff** (today proves compile + `Effect` load). Build-time precompile also works. Tracked: [`plan/PHASE-50-android-runtime-support.md`](../plan/PHASE-50-android-runtime-support.md) §6.2. |
| **KNIFX container on KNI WebGL** | **CLOSED 2026-06-27 (render-proven in real KNI WebGL)** | ShadowDusk's `--target-runtime kni-knifx` used to emit a KNIFX whose backend directory carried only the **OpenGL** backend (`0x0011`); KNI **WebGL** reports a distinct backend (`KnifxBackend.WebGL = 0x0014`), found no match, and **rejected it** (*"Effect profile 'DirectX_11' is not compatible with the graphics backend 'WebGL'"*). **Fixed** by making `KnifxWriter` emit, for a GL target, a **multi-backend directory advertising the whole GL family** (OpenGL + GLES + WebGL) so one `.knifx` loads on every KNI GL host: the **OpenGL (desktop)** entry keeps the faithful version-directory body (byte-identical, desktop render unchanged), while the **GLES + WebGL** entries share a body whose per-shader `ShaderVersion` is **(0,0)** + raw GLSL — which routes KNI's **runtime** GL-ES converter, the EXACT mechanism that loads MGFX v10 on KNI WebGL (proven, Phase 33). **Render-proven both hosts:** KNI **desktop** SDL2.GL still 10/10 (`validation/KniDesktopGL knifx`); KNI **WebGL** now loads + renders the KNIFX container (`tests/ShadowDusk.BrowserTests` `--corpus=sd`: `ExKnifxMacro` __KNIFX__ red branch, 196096 red px / 0 green, Mode-1 12/12). Pinned by `KnifxWriterTests` (multi-backend dir + raw web body, 20/20) + the WebGL harness probe. Full suite 1940 green; DX/FNA/KNI-DX render gates unaffected (KNIFX-isolated change). |

---

## 8. ShaderToy / GLSL frontend — a DISTINCT evidence axis (NOT `mgfxc`-equivalence)

`src/ShadowDusk.ShaderToy` (Phase 47) converts a single-pass ShaderToy / GLSL image shader to a
self-contained `.fx`, which the **existing, unchanged** pipeline then compiles. Its evidence bar is a
**separate rung from the headline `mgfxc`/`fxc`-equivalence bar** and must never be conflated with it:

- **There is NO `mgfxc` oracle for ShaderToy input** — a `.glsl` is not an `mgfxc` input, so there is
  nothing to be byte/render-equivalent *to* on the input side. The frontend's honest bar is
  **pixel-fidelity vs the ORIGINAL GLSL**: render the original ShaderToy GLSL in a raw GL context
  (ground truth) vs OUR converted `.fx`→`.mgfx` through MonoGame, diff per pixel. Phase 46's
  out-of-band `render-proof --fidelity` gate: **46/46 deterministic shaders match the original at mean
  0.00/255** (gallery 72/72). This driver stays out-of-band like `validation/*`.
- **The DOWNSTREAM half is unchanged.** Once the converter emits `.fx`, the `.fx`→`.mgfx`/`.fxb` step is
  the SAME faithful, `mgfxc`-equivalent pipeline proven by the existing corpus — the promotion changes
  neither bar (proven additive: existing `.fx` output byte-identical; converter is pure-managed, no
  native/MonoGame dep — guarded by `NoMonoGameInProductLibrariesTests`).
- **CLI `.glsl` route** (`ShadowDuskCLI shader.glsl out.mgfx /Profile:...`): backed by
  `CliShaderToyInputTest` (compile OpenGL/DX, located-reject error path, byte-identity CLI ≡
  Convert+pipeline). The Windows DX/FNA + GL render-gate fixtures for the `.glsl` route are a tracked
  follow-up; run the Windows render gates on a GPU box before a release that ships this route.

---

## Sources / cross-references

- Engine state (June 2026, primary-source): [`DONE/PHASE-35-appendix/shader-pipeline-landscape-2026-06.md`](../plan/DONE/PHASE-35-appendix/shader-pipeline-landscape-2026-06.md).
- Evidence-ladder definition + backend pipeline table: [`the-purpose.md`](the-purpose.md).
- Forward-version (v11/KNIFX/Vulkan/DX12) status: [`PHASE-35-forward-version-support.md`](../plan/DONE/PHASE-35-forward-version-support.md).
- KNI WebGL render harness: `tests/ShadowDusk.BrowserTests/README.md` (Phase 24).
- Cross-OS byte-identity rationale: `tests/fixtures/golden/byte-identity/README.md`.
