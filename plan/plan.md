# ShadowDusk — Implementation Plan

This document is the top-level index. Each phase is fleshed out in its own document.

---

## THE PURPOSE (what every phase serves)

**The product is a drop-in `mgfxc` replacement: a self-contained library** a developer adds to a **MonoGame/KNI project on Linux, macOS, or Windows** that compiles **`.fx` → `.mgfx` in memory at runtime** with **nothing but the library** (no `fxc`, no Wine, no SDK), whose output **loads and renders identically to `mgfxc`'s in the real runtime**. **One faithful compiler — the same `mgfxc`-equivalent result everywhere.**

- The **library is the product**; the **CLI** and **MGCB plugin** are delivery shapes of it; the **browser / WASM shader-fiddle is only a sample of reach**, never the product.
- **No substitute compilers:** every host runs the *same* faithful pipeline (HLSL→DXC→SPIR-V→SPIRV-Cross→GLSL→MGFX, or vkd3d→DXBC). A host that can't yet run a faithful component is *not done* — never a licence to swap in a different compiler that diverges from `mgfxc`.
- Full statement + the success/evidence bar: see **`CLAUDE.md` → "THE PURPOSE" / "What success actually means"**.

Every phase below exists to serve that sentence. If a phase or sample starts redefining the goal, it has drifted — stop and re-anchor here.

---

## Reference Documents (background, not phase plans)

| Document | What it's good for | ⚠️ Read with |
|---|---|---|
| [`monogame_runtime_mgfx_compiler_research.md`](../monogame_runtime_mgfx_compiler_research.md) | Architecture survey of *how one builds a runtime MGFX compiler*: the `new Effect(gd, byte[])` loading path, `.xnb`-vs-raw-bytes, the DXC/SPIRV-Cross/MojoShader tool landscape, and a §0.1 map from its sections to these phases. | Its §0 alignment note — it was written greenfield and **understates** the MojoShader-GLSL-dialect blocker, which Phase 17 §3.6 proves is the real wall. Treat it as context, not a plan; Phases 7/8/17 supersede its roadmap. |

---

## Completed Phases

These phases are fully implemented. Their documents have been moved to `DONE/` with all checklist items ticked.

| Phase | File | Summary |
|-------|------|---------|
| 0 ✓ | [DONE/phase-0-setup.md](DONE/phase-0-setup.md) | Fixture corpus (39 .fx / 4 .fxh), golden .mgfx reference compilation, ShaderViewer sample — **DONE** |
| 1 ✓ | [DONE/PHASE-1-solution-scaffold.md](DONE/PHASE-1-solution-scaffold.md) | .NET solution structure, project references, NuGet dependencies, test framework — **DONE** |
| 2 ✓ | [DONE/PHASE-2-fx9-pre-parser.md](DONE/PHASE-2-fx9-pre-parser.md) | Custom parser: extract technique/pass/sampler_state/render-state blocks before DXC sees the file — **DONE** |
| 3 ✓ | [DONE/PHASE-3-preprocessor-macro-injection.md](DONE/PHASE-3-preprocessor-macro-injection.md) | #include flattening, platform macro injection (MGFX=1, GLSL=1, SM4=1, etc.) — **DONE** |
| 4 ✓ | [DONE/PHASE-4-dxc-integration.md](DONE/PHASE-4-dxc-integration.md) | Vortice.Dxc wiring, per-platform DXC flags, HLSL → SPIR-V compilation — **DONE** |
| 5 ✓ | [DONE/PHASE-5-shader-reflection.md](DONE/PHASE-5-shader-reflection.md) | Cross-platform parameter metadata extraction via IDxcUtils::CreateReflection and SPIRV-Cross — **DONE** |
| 6 ✓ | [DONE/PHASE-6-spirv-cross-glsl-transpilation.md](DONE/PHASE-6-spirv-cross-glsl-transpilation.md) | SPIRV-Cross C API P/Invoke, SPIR-V → GLSL/MSL, Y-flip, depth range, combined samplers — **DONE** |
| 7 ✓ | [DONE/PHASE-7-mgfx-binary-writer.md](DONE/PHASE-7-mgfx-binary-writer.md) | .mgfx binary format serialization: header, constant buffers, shaders, parameters, techniques, passes — **DONE** |
| 8 ✓ | [DONE/PHASE-8-compiler-library.md](DONE/PHASE-8-compiler-library.md) | `ShadowDusk.Compiler` NuGet library — `EffectCompiler : IShaderCompiler`, pipeline orchestration, the consumer-facing package — **DONE** |
| 9 ✓ | [DONE/PHASE-9-cli-entry-point.md](DONE/PHASE-9-cli-entry-point.md) | dotnet tool CLI, mgfxc-compatible flags, MGCB error format, stderr routing, exit codes — **DONE** |
| 15 ✓ | [DONE/PHASE-15-integration-tests.md](DONE/PHASE-15-integration-tests.md) | End-to-end .fx compilation tests — 9 fixtures × 3 platforms, determinism, error cases (103 tests, all passing) — **DONE** |
| 16 ✓ | [DONE/PHASE-16-image-regression-tests.md](DONE/PHASE-16-image-regression-tests.md) | Visual regression tests — offscreen OpenGL rendering of all 9 Phase 15 fixtures, 12 reference PNGs anchored on ShadowDusk's own output, 13 tests passing — **DONE** |

---

## Active & Planned Phases

*(Status per each phase doc's own header. "In progress" = currently being worked; "Planned" = written but not started.)*

| Phase | Status | File | Summary |
|-------|--------|------|---------|
| 17 | ✅ Done | [DONE/PHASE-17-monogame-runtime-validation.md](DONE/PHASE-17-monogame-runtime-validation.md) | In-engine equivalence **complete (2026-05-30)** for the full SM3 PS-only corpus (all 10/10, Dissolve incl.): ShadowDusk `.mgfx` loads in a real MonoGame `Effect` and renders pixel-equivalent to `mgfxc` (OpenGL) — the fidelity (Part 2) bar. Carried forward: DirectX → Phase 18, VS-driven effects → backlog 17-VS |
| 18 | ✅ Done | [DONE/PHASE-18-directx-dxbc.md](DONE/PHASE-18-directx-dxbc.md) | In-engine equivalence **complete (2026-05-30)** for the SM5 PS-only corpus (all 10/10): ShadowDusk's DX `.mgfx` loads in real MonoGame WindowsDX and renders pixel-equivalent to `mgfxc`, via the cross-platform **vkd3d-shader** backend (`d3dcompiler_47` oracle on Windows) — the DirectX half of fidelity. Carried forward: VS-driven → backlog 17-VS, Linux/macOS *run* validation → Phase 30 CI |
| 19 | ✅ Done | [DONE/PHASE-19-wasm-runtime-compilation.md](DONE/PHASE-19-wasm-runtime-compilation.md) | WASM compile **engine** (scope narrowed 2026-05-30; browser-runtime tail → Phase 100). Built & desktop-verified: injectable backend seams; a pure-managed `SpirvReflector` proven equivalent to the DXIL oracle (10/10), removing the Windows-only reflection blocker; the GL pipeline reflects SPIR-V and emits `.mgfx` **byte-identical** to the DXIL path (10/10); `WasmShaderCompiler` composes the pipeline with `[JSImport]` DXC/SPIRV-Cross backends and **compiles for `net8.0-browser`** |
| 21 | ✅ Done | [DONE/PHASE-21-test-suite-performance.md](DONE/PHASE-21-test-suite-performance.md) | Resolved the 21m43s `ShadowDusk.Integration.Tests` outlier. Root cause (structural fit): a per-construction `dotnet publish -c Release` in `CliBinaryFixture` — a cold Release build + AV-scan of freshly-copied native binaries. Fix: reuse the normal build's CLI binary (PR #2); suite back to seconds (128/128, ~6 s). Dev-time Defender exclusions documented in `CLAUDE.md`. Guardrail added 2026-05-31: `ShadowDusk.runsettings` 5-min `TestSessionTimeout` so a future hang fails fast |
| 22 | In progress | [PHASE-22-wasm-shader-fiddle-sample.md](PHASE-22-wasm-shader-fiddle-sample.md) | KNI Blazor-WASM **sample app** (`samples/ShaderFiddle.Web/`, net8.0-browser): paste `.fx` → compile in-browser via `ShadowDusk.Wasm` → `new Effect` → cat + shader + error UI. **Built & build/publish-verified**; mode-1 load+render of the 10 Phase-17 goldens wired. Mode-2 in-browser compile **runs in the sample** (Slang-wasm + SPIRV-Cross-wasm, each stage node-verified). ⚠️ The Slang frontend is **sample-only** — **not** the faithful DXC pipeline; the faithful DXC→WASM *product* path is **Phase 23**. The rendered-cat bar + the KNI MGFX-v10-vs-KNIFX-v11 load question are owned by **Phase 24** (Playwright) |
| 23 | Active | [PHASE-23-in-browser-compilation.md](PHASE-23-in-browser-compilation.md) | **Faithful in-browser mode-2 compile** for the *product*. **DECIDED (2026-06-01): Option A — faithful DXC→WASM is the only product frontend; Slang is sample-only** (no substitute compilers). SPIRV-Cross→WASM is built & node-verified byte-identical and ships as a `[JSImport]` static web asset; the **DXC→WASM build is the long pole** (recipe now in the doc). Emscripten pinned to **3.1.34**. DoD: a corpus shader compiles fully in-browser via the faithful pipeline, bytes identical to the CLI, render-proven on Phase 24's harness. **Prerequisite from Phase 24: resolve the Dissolve `discard`/threshold WebGL render divergence** (dialect-rewrite fix vs documented KNI-WebGL limitation) |
| 24 | ✅ Done | [DONE/PHASE-24-browser-render-validation.md](DONE/PHASE-24-browser-render-validation.md) | **Browser render validation (Playwright headless) — complete (2026-06-01).** First real-browser run of ShadowDusk output. Mode-1: **10/10 load** in real KNI WebGL (MGFX v10 parses in KNI's forked `MGFXReader10` — no v11 *container* blocker) + **9/10 render-equivalent** vs DesktopGL of the same bytes. **Dissolve diverges** (Δ198 / 1.68% px at its `discard`/threshold band → carried forward as a **Phase 23 prerequisite**). KNIFX-v11 question answered with evidence; harness (`tests/ShadowDusk.BrowserTests/`) handed to Phase 30 §16. Carried forward: mode-2 sample verification → Phase 23 Gate 3 (blocked here on restore-gated `slang-wasm.wasm`) |
| 25 | Planned | [PHASE-25-security-hardening.md](PHASE-25-security-hardening.md) | Security hardening for the **untrusted-`.fx` library API** (any consumer, not just the fiddle) — path traversal, input validation, supply chain (incl. the `.wasm` artifacts) |
| 30 | Planned | [PHASE-30-cross-platform-ci.md](PHASE-30-cross-platform-ci.md) | RID matrix (SPIRV-Cross **+ vkd3d-shader**), native binary restore, GitHub Actions CI across Linux/macOS/Windows, **+ the WASM build & headless-browser smoke (§16)** that Phases 22/23/24/100 defer to it |
| 4.1 | Spike (parked) | [PHASE-4.1-SPIKE-wasm-directx-dxbc.md](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) | Research spike: faithful **DirectX DXBC in WASM** (vkd3d-shader→emscripten). Far-future, after the OpenGL-in-WASM path (Phase 23); the DXBC analogue of Phase 23's DXC→WASM build. Server-relay option is **out of bounds** (no server roundtrip) |
| 100 | Deferred | [PHASE-100-deferred-backlog.md](PHASE-100-deferred-backlog.md) | **Single deferred bucket** (merges the old Phase 20 backlog + leftover test/verification items), parked far-future: unchecked items from phases 2–9. **The WASM browser-runtime tail was moved out to active Phases [23](PHASE-23-in-browser-compilation.md) (faithful compile) + [24](PHASE-24-browser-render-validation.md) (browser render) + 30 §16 (CI)** — no longer tracked in 100. No prerequisites; review before 1.0 |

---

## Dependencies

```
Phase 1  (scaffold)
  └─ Phase 2  (FX9 parser)
  └─ Phase 3  (preprocessor)
       └─ Phase 4  (DXC integration)
            │    └─ Phase 4.1 (SPIKE: WASM + DirectX DXBC — parked, far-future)
            └─ Phase 5  (reflection)
            └─ Phase 6  (SPIRV-Cross transpilation)
                 └─ Phase 7  (binary writer)
                      └─ Phase 8  (ShadowDusk.Compiler library — EffectCompiler NuGet)
                           ├─ Phase 9  (CLI — ShadowDusk.Cli dotnet tool)
                           │    └─ Phase 15 (integration tests)
                           │         ├─ Phase 16 (image regression)
                           │         │    └─ Phase 17 (MonoGame runtime equivalence — OpenGL)
                           │         │         ├─ Phase 18 (DirectX DXBC — WindowsDX fidelity)
                           │         │         └─ Phase 19 (WASM runtime compilation — was "9W")
                           │         └─ Phase 30 (CI — desktop matrix + WASM/browser §16)
                           └─ Phase 19 (WASM — ShadowDusk.Wasm JS interop impl)
                                └─ Phase 22 (KNI Blazor-WASM SAMPLE app)
                                     └─ Phase 24 (browser render validation — Playwright; run FIRST)
                                          └─ Phase 23 (FAITHFUL in-browser compile — DXC→WASM, Option A)
```

> **The WASM-KNI product spine (the goal "a user uses our library inside WASM KNI"):** Phase 19 (engine) → Phase 22 (sample proves the shape) → **Phase 24** (real-browser render proof — retires the KNI MGFXReader10/v11 load risk; deliberately *before* the build effort) → **Phase 23** (faithful DXC→WASM frontend — the product). Phase 23's Gate 3 reuses Phase 24's harness; **Phase 30 §16** wires both into CI.
>
> Phase 19 intentionally appears twice (a diamond): it depends on **Phase 8** (the `IShaderCompiler` abstraction it implements) *and* on **Phase 17** (the MonoGame-loadable `.mgfx` format + MojoShader-dialect GLSL a browser-produced effect must also carry). Phases **23/24** (not the already-done Phase 19) additionally depend on **Phase 25** (untrusted-input security) and **Phase 30** (CI), which the graph keeps off the spine for readability — see each phase doc's "Depends on" line for the full set. Phase 19 supersedes the earlier "9W" placeholder.

## Key Decisions Already Made

- **Option B pipeline:** DXC → SPIR-V → SPIRV-Cross → GLSL/MSL. Option A (FXC/MojoShader) rejected — requires Wine on Linux/macOS.
- **DXC wrapper:** `Vortice.Dxc` NuGet package (bundles prebuilt native binaries for all platforms).
- **SPIRV-Cross binding:** Raw P/Invoke against the SPIRV-Cross C API (not Veldrid.SPIRV).
- **Default MGFXVersion:** `10` (MonoGame 3.8.2 stable). Version `11` is opt-in via flag.
- **Metal scope:** Out of scope until the OpenGL path is working and validated.
- **MGCB integration:** Tier 1 only (PATH-based drop-in binary named `mgfxc`). Tier 2 content processor plugin is a separate future undertaking.
- **Dual delivery targets:** CLI (`ShadowDusk.Cli`) and WASM library (`ShadowDusk.Wasm`). Output is always a `.mgfx` blob; `IShaderCompiler.CompileAsync` abstracts the difference. WASM implementation uses JS interop to call WASM-compiled DXC and SPIRV-Cross.
- **KNI compatibility:** KNI uses the same `.mgfx` format as MonoGame. No special output path needed for KNI.

---

## ✅ Resolved Constraint (Phase 18): DXC Cannot Produce SM5 DXBC

Discovered during Phase 4 implementation. **DXC only produces SM6 DXIL** — it rejects `vs_5_0`/`ps_5_0` profiles with `error: invalid profile` for non-SPIRV targets. It has never supported DXBC (SM1–SM5) output. **Phase 18 (done 2026-05-30) resolved this** by routing the DX11 path through a DXBC backend behind `IDxbcShaderCompiler` — the cross-platform **vkd3d-shader** library (HLSL → DXBC_TPF), validated 10/10 in real MonoGame WindowsDX, with `d3dcompiler_47.dll` as a Windows-only correctness oracle. DXC's DXIL path is retained only for DX12/KNI. The historical analysis below is kept for context.

**Impact by target:**

| Target | Status | Notes |
|--------|--------|-------|
| OpenGL (SPIR-V path) | ✅ Unaffected | DXC compiles `vs_5_0 -spirv` fine; SPIRV-Cross handles the rest |
| Vulkan (SPIR-V path) | ✅ Unaffected | Same pipeline |
| DirectX 11 | ✅ Resolved (Phase 18) | DXC's SM6 DXIL won't load on D3D11; **Phase 18 routes DX11 through vkd3d-shader → SM5 DXBC** (`d3dcompiler_47` oracle on Windows). 10/10 load + render pixel-equivalent in real WindowsDX |
| DirectX 12 / KNI | ✅ Works | D3D12 natively accepts DXIL (SM6); `vs_6_0`/`ps_6_0` used in Phase 4 |

**SM6 DXIL on D3D11 is a hard no.** `ID3D11Device::CreateVertexShader` rejects DXIL unconditionally — even on Windows 10. DXIL is a D3D12-only format.

### Cross-Platform SM5 DXBC Options

The only viable cross-platform HLSL→DXBC compiler that does not require Wine:

**`vkd3d-shader`** (`gitlab.winehq.org/wine/vkd3d`)
- A standalone C library — **no Wine runtime required**. It is developed under the Wine project umbrella but ships and runs independently; linking against it is identical to linking SPIRV-Cross.
- Compiles HLSL to SM4/SM5 DXBC via a `vkd3d_shader_compile()` C API; P/Invoke pattern mirrors SPIRV-Cross
- Cross-platform: Linux, macOS, Windows. **No official prebuilt Windows DLL exists** (Phase 18 finding) — we build `libvkd3d-shader` from WineHQ source (vkd3d-1.17 via MSYS2/autotools, self-contained, zero non-system deps) and host per-RID; Linux/macOS have distro/source builds. `tools/restore.*` carries the recipe.
- License: **LGPL-2.1+** — safe to use as a dynamically-linked native binary (same model as SPIRV-Cross)
- Active development; v2.0 released May 2026; adopted by SDL3's `SDL_shadercross` as its non-Windows DXBC backend (no Wine involved there either)
- Coverage for MonoGame-style effects (cbuffers, Texture2D samplers, VS/PS only): sufficient
- Known gaps: SM5 UAV buffers (RWBuffer) and tessellation stages are partial; irrelevant for basic MonoGame effects
- **Not byte-identical to `fxc.exe` output**, but semantically equivalent; on Windows `d3dcompiler_47.dll` (ships with Windows) can serve as a fidelity fallback

**Recommended path for DirectX SM5 support (Phase 4.1):**

No end-user installation required on any platform:

| Platform | Library | How it's delivered |
|----------|---------|-------------------|
| Windows | `libvkd3d-shader.dll` (shipping) + `d3dcompiler_47.dll` (oracle) | vkd3d built/hosted per-RID (the cross-platform shipping backend on every OS); `d3dcompiler_47.dll` ships with Windows and is used only as the correctness oracle |
| Linux | `libvkd3d-shader.so` | Downloaded by `tools/restore.sh`, bundled into `dotnet publish` output |
| macOS | `libvkd3d-shader.dylib` | Same as Linux |

This mirrors exactly how SPIRV-Cross is distributed today. Users install ShadowDusk and nothing else.

> **WASM delivery target: DirectX DXBC is an open problem.** Native P/Invoke is unavailable in the browser, so neither `d3dcompiler_47.dll` nor `libvkd3d-shader` can be called directly from .NET WASM. No prebuilt WASM artifact of vkd3d-shader currently exists. The path forward requires one of: (a) compiling vkd3d-shader to WASM via emscripten and calling it via `[JSImport]` (same pattern as WASM-compiled DXC), or (b) a server-side compilation relay for DXBC. This is unresolved — see [PHASE-4.1-SPIKE-wasm-directx-dxbc.md](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) for the full problem statement and candidate solutions.

**As of Phase 18 (done 2026-05-30), the `PlatformTarget.DirectX` DX11 profile compiles to SM5 DXBC via vkd3d-shader** and loads/renders in real MonoGame WindowsDX (10/10; `d3dcompiler_47` oracle on Windows). DXC's SM6 DXIL path is retained only for DX12/KNI. The OpenGL path (Phase 17) is fully functional. **WASM + DXBC remains the open problem** (Phase 4.1) — no native P/Invoke in the browser.
