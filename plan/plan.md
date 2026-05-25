# ShadowDusk — Implementation Plan

This document is the top-level index. Each phase is fleshed out in its own document.

---

## Phase Overview

| Phase | File | Summary |
|-------|------|---------|
| 1 | [PHASE-1-solution-scaffold.md](PHASE-1-solution-scaffold.md) | .NET solution structure, project references, NuGet dependencies, test framework |
| 2 | [PHASE-2-fx9-pre-parser.md](PHASE-2-fx9-pre-parser.md) | Custom parser: extract technique/pass/sampler_state/render-state blocks before DXC sees the file |
| 3 | [PHASE-3-preprocessor-macro-injection.md](PHASE-3-preprocessor-macro-injection.md) | #include flattening, platform macro injection (MGFX=1, GLSL=1, SM4=1, etc.) |
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
                      └─ Phase 8  (CLI)
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
