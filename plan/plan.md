# ShadowDusk — Implementation Plan

This document is the top-level index. Each phase is fleshed out in its own document.

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
| 21 | Planned | [PHASE-21-test-suite-performance.md](PHASE-21-test-suite-performance.md) | Investigate the 21m43s `ShadowDusk.Integration.Tests` run (same 108 tests ran in ~3s earlier same session) — likely AV-scan / CLI-spawn / a stalling test; diagnostic, correctness unaffected |
| 22 | Planned | [PHASE-22-wasm-shader-fiddle-sample.md](PHASE-22-wasm-shader-fiddle-sample.md) | KNI Blazor-WASM **sample app**: paste `.fx` → compile in-browser via `ShadowDusk.Wasm` → `new Effect` → cat image with shader applied + error UI. The user-facing XNA-Fiddle demo on top of Phase 19's compiler capability (boundary drawn to avoid overlap) |
| 25 | Planned | [PHASE-25-security-hardening.md](PHASE-25-security-hardening.md) | Security hardening for WASM/web path — path traversal, input validation, supply chain |
| 30 | Planned | [PHASE-30-cross-platform-ci.md](PHASE-30-cross-platform-ci.md) | RID matrix, native binary restore, GitHub Actions CI across Linux/macOS/Windows |
| 100 | Deferred | [PHASE-100-deferred-backlog.md](PHASE-100-deferred-backlog.md) | **Single deferred bucket** (merges the old Phase 20 backlog + the Phase 19 WASM browser-runtime tail), parked far-future: unchecked items from phases 2–9, *plus* real emscripten DXC + SPIRV-Cross WASM modules + an actual in-browser Mode 1/Mode 2 run. No prerequisites; review before 1.0 |

---

## Dependencies

```
Phase 1  (scaffold)
  └─ Phase 2  (FX9 parser)
  └─ Phase 3  (preprocessor)
       └─ Phase 4  (DXC integration)
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
                           │         └─ Phase 30 (CI)
                           └─ Phase 19 (WASM — ShadowDusk.Wasm JS interop impl)
```

> Phase 19 intentionally appears twice (a diamond): it depends on **Phase 8** (the `IShaderCompiler` abstraction it implements) *and* on **Phase 17** (the MonoGame-loadable `.mgfx` format + MojoShader-dialect GLSL a browser-produced effect must also carry). Phase 19 additionally depends on Phase 25 (security) and Phase 30 (CI), which the graph omits to keep the build-order spine readable — see the phase doc's "Depends on" line for the full set. Phase 19 supersedes the earlier "9W" placeholder.

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
