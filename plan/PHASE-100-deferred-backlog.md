# Phase 100 — Deferred Backlog (RETIRED / emptied)

**Status: ✅ Retired (2026-06-03).** This was the single far-future "deferred bucket" that
collected unchecked items from earlier phases. Per the project decision that *the deferred
bucket should not be a forever-parking-lot*, every item has been **promoted into a real,
planned phase** with its own task list, acceptance criteria, and Definition of Done. This
file is kept only as a **breadcrumb** so old links resolve and the provenance is traceable.

**Do not add new items here.** New deferred work goes into the relevant real phase (or a new
one). This document is closed.

---

## Where everything went

| Former Phase 100 section | Promoted to |
|---|---|
| **From Phase 2/3/4/5/6/8/9/15** — deferred unit/integration test coverage + manual pack/CLI verification (DXC flag/diagnostic tests, DXC integration, SPIRV-Cross binding-slot/`SD0101` verifier, `MgfxParameterMatch` golden snapshot, GLSL Y-flip `11-6-B`/`11-6-C`, `FileSystemIncludeResolver` + `DxcIncludeHandler` tests, direct `ShaderIRBuilder` tests + `InternalsVisibleTo`, CLI-process `[Theory]` parity, CLI pack/install/publish §9.4–9.6) | **[Phase 27 — Pre-1.0 Test & Verification Sweep](PHASE-27-pre-1.0-test-and-verification-sweep.md)** |
| **`17-VS`** — VS-driven MonoGame effects (symmetric `vs_uniforms_vec4` remap, VS-side attribute/varying I/O, PS/VS `mat4` expansion, validation) | **[Phase 28 — VS-Driven MonoGame Effects](PHASE-28-vs-driven-monogame-effects.md)** |
| **From Phase 9** — "Full MGCB content processor plugin (`ShadowDusk.MgcbPlugin`)" | **[Phase 29 — MGCB Content-Processor Plugin (Tier 2)](PHASE-29-mgcb-content-processor-plugin.md)** |
| **From Phase 9** — "Metal/MSL pipeline stage" (the empty `MslEmitter` stub) | **[Phase 31 — Metal / MSL Backend](PHASE-31-metal-msl-backend.md)** |
| **From Phase 4** — Vulkan compile-flag/SPIR-V items (most already covered by `DxcFlagBuilderTests` / `DxcShaderCompilerIntegrationTests`) + the Vulkan target wiring | **[Phase 32 — Vulkan Backend](PHASE-32-vulkan-backend.md)** |
| **From Phase 19** — WASM browser-runtime tail (emscripten modules + real in-browser run) | already moved out (2026-05-31) → **[Phase 23](DONE/PHASE-23-in-browser-compilation.md)** (faithful compile) + **[Phase 24](DONE/PHASE-24-browser-render-validation.md)** (render) + **[Phase 30 §16](PHASE-30-ci-and-nuget-release.md)** (CI) |
| **From Phase 15** — cross-platform *runs* of the suite (Linux/macOS) | **[Phase 30 — Cross-Platform CI](PHASE-30-ci-and-nuget-release.md)** |

### Already-resolved items (not promoted — closed in place)

- `11-6-A` — `SpirvCrossGlslTranspiler` wired into `CompilationPipeline` (resolved in Phase 8).
- `11-6-D` — uniform remapping for the MonoGame OpenGL runtime (resolved in Phase 17; see `docs/glsl-uniform-naming.md`).
- Phase 8 packaging `7.4`/`7.5` — NuGet drop-in fix (resolved 2026-05-31, branch `selfcontained-inmemory-nuget`).
- Phase 3 wiring checks `8.2`/`8.3`/`8.4` — verified against `CompilationPipeline.cs`.

> Cross-cutting `Status:` for the index lives in [plan.md](plan.md) → *Active & Planned Phases* (and the *Roadmap tracks* grouping below the table). The detailed task lists, acceptance criteria, and DoD that used to be sketched here now live in each promoted phase doc above.
