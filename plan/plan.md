# ShadowDusk — Implementation Plan

This document is the top-level index. Each phase is fleshed out in its own document.

---

## Completed Phases

These phases are fully implemented. Their documents have been moved to `DONE/` with all checklist items ticked.

| Phase | File | Summary |
|-------|------|---------|
| 0 ✓ | [DONE/phase-0-setup.md](DONE/phase-0-setup.md) | Fixture corpus (39 .fx / 4 .fxh), golden .mgfx reference compilation, ShaderViewer sample — **DONE** |
| 1 ✓ | [DONE/PHASE-1-solution-scaffold.md](DONE/PHASE-1-solution-scaffold.md) | .NET solution structure, project references, NuGet dependencies, test framework — **DONE** |
| 2 ✓ | [DONE/PHASE-2-fx9-pre-parser.md](DONE/PHASE-2-fx9-pre-parser.md) | Custom parser: extract technique/pass/sampler_state/render-state blocks before DXC sees the file — **DONE** |
| 3 ✓ | [DONE/PHASE-3-preprocessor-macro-injection.md](DONE/PHASE-3-preprocessor-macro-injection.md) | #include flattening, platform macro injection (MGFX=1, GLSL=1, SM4=1, etc.) — **DONE** |

---

## Active Phases

| Phase | File | Summary |
|-------|------|---------|
| 4 | [PHASE-4-dxc-integration.md](PHASE-4-dxc-integration.md) | Vortice.Dxc wiring, per-platform DXC flags, HLSL → SPIR-V compilation |
| 5 | [PHASE-5-shader-reflection.md](PHASE-5-shader-reflection.md) | Cross-platform parameter metadata extraction via IDxcUtils::CreateReflection and SPIRV-Cross |
| 6 | [PHASE-6-spirv-cross-glsl-transpilation.md](PHASE-6-spirv-cross-glsl-transpilation.md) | SPIRV-Cross C API P/Invoke, SPIR-V → GLSL/MSL, Y-flip, depth range, combined samplers |
| 7 | [PHASE-7-mgfx-binary-writer.md](PHASE-7-mgfx-binary-writer.md) | .mgfx binary format serialization: header, constant buffers, shaders, parameters, techniques, passes |
| 8 | [PHASE-8-cli-entry-point.md](PHASE-8-cli-entry-point.md) | dotnet tool CLI, mgfxc-compatible flags, MGCB error format, stderr routing, exit codes |
| 9 | [PHASE-9-integration-tests.md](PHASE-9-integration-tests.md) | End-to-end .fx compilation tests, fixture shaders, per-platform test filters |
| 10 | [PHASE-10-cross-platform-ci.md](PHASE-10-cross-platform-ci.md) | RID matrix, native binary restore, GitHub Actions CI across Linux/macOS/Windows |

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
                      ├─ Phase 8  (CLI — ShadowDusk.Cli dotnet tool)
                      └─ Phase 8W (WASM — ShadowDusk.Wasm JS interop impl)
                           └─ Phase 9  (integration tests)
                                └─ Phase 10 (CI)
```

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

## ⚠️ Known Constraint: DXC Cannot Produce SM5 DXBC

Discovered during Phase 4 implementation. **DXC only produces SM6 DXIL** — it rejects `vs_5_0`/`ps_5_0` profiles with `error: invalid profile` for non-SPIRV targets. It has never supported DXBC (SM1–SM5) output.

**Impact by target:**

| Target | Status | Notes |
|--------|--------|-------|
| OpenGL (SPIR-V path) | ✅ Unaffected | DXC compiles `vs_5_0 -spirv` fine; SPIRV-Cross handles the rest |
| Vulkan (SPIR-V path) | ✅ Unaffected | Same pipeline |
| DirectX 11 | ⚠️ Degraded | DXC produces SM6 DXIL, not SM5 DXBC; MonoGame's D3D11 backend cannot load DXIL |
| DirectX 12 / KNI | ✅ Works | D3D12 natively accepts DXIL (SM6); `vs_6_0`/`ps_6_0` used in Phase 4 |

**SM6 DXIL on D3D11 is a hard no.** `ID3D11Device::CreateVertexShader` rejects DXIL unconditionally — even on Windows 10. DXIL is a D3D12-only format.

### Cross-Platform SM5 DXBC Options

The only viable cross-platform HLSL→DXBC compiler that does not require Wine:

**`vkd3d-shader`** (`gitlab.winehq.org/wine/vkd3d`)
- A standalone C library — **no Wine runtime required**. It is developed under the Wine project umbrella but ships and runs independently; linking against it is identical to linking SPIRV-Cross.
- Compiles HLSL to SM4/SM5 DXBC via a `vkd3d_shader_compile()` C API; P/Invoke pattern mirrors SPIRV-Cross
- Cross-platform: Linux, macOS, Windows; prebuilt shared libraries (`libvkd3d-shader.so` / `.dylib` / `.dll`)
- License: **LGPL-2.1+** — safe to use as a dynamically-linked native binary (same model as SPIRV-Cross)
- Active development; v2.0 released May 2026; adopted by SDL3's `SDL_shadercross` as its non-Windows DXBC backend (no Wine involved there either)
- Coverage for MonoGame-style effects (cbuffers, Texture2D samplers, VS/PS only): sufficient
- Known gaps: SM5 UAV buffers (RWBuffer) and tessellation stages are partial; irrelevant for basic MonoGame effects
- **Not byte-identical to `fxc.exe` output**, but semantically equivalent; on Windows `d3dcompiler_47.dll` (ships with Windows) can serve as a fidelity fallback

**Recommended path for DirectX SM5 support (Phase 4.1):**

No end-user installation required on any platform:

| Platform | Library | How it's delivered |
|----------|---------|-------------------|
| Windows | `d3dcompiler_47.dll` | Ships with Windows since Vista — always present, zero bundling needed |
| Linux | `libvkd3d-shader.so` | Downloaded by `tools/restore.sh`, bundled into `dotnet publish` output |
| macOS | `libvkd3d-shader.dylib` | Same as Linux |

This mirrors exactly how SPIRV-Cross is distributed today. Users install ShadowDusk and nothing else.

> **WASM delivery target: DirectX DXBC is an open problem.** Native P/Invoke is unavailable in the browser, so neither `d3dcompiler_47.dll` nor `libvkd3d-shader` can be called directly from .NET WASM. No prebuilt WASM artifact of vkd3d-shader currently exists. The path forward requires one of: (a) compiling vkd3d-shader to WASM via emscripten and calling it via `[JSImport]` (same pattern as WASM-compiled DXC), or (b) a server-side compilation relay for DXBC. This is unresolved — see [PHASE-4.1-SPIKE-wasm-directx-dxbc.md](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) for the full problem statement and candidate solutions.

Until Phase 4.1 lands, the `PlatformTarget.DirectX` path compiles to SM6 DXIL (usable by D3D12 / KNI). The OpenGL path (the primary cross-platform target) is fully functional.
