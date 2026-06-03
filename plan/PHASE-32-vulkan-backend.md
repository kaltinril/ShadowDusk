# Phase 32 — Vulkan Backend (SPIR-V target)

**Track:** Backend breadth (post-1.0).
**Status:** Future / **Parked** (written 2026-06-03). The compile *path* for the Vulkan
SPIR-V target already exists — DXC emits SPIR-V (it is the OpenGL branch's intermediate),
`PlatformTarget.Vulkan` is wired through the CLI, the DXC flag builder, the platform macros,
and the MGFX profile map. What is missing is (a) the Vulkan reflection/`.mgfx` write wiring
in the full `EffectCompiler` pipeline and (b) **a runtime to validate against**. This phase
is **parked like [Phase 4.1](PHASE-4.1-SPIKE-wasm-directx-dxbc.md)**: MonoGame 3.8 ships **no
Vulkan backend**, and KNI's Vulkan story is unclear — so there is **no `mgfxc`-Vulkan baseline
and no MonoGame Vulkan runtime** to render-validate against. The "renders like `mgfxc`" bar
(CLAUDE.md → *What success actually means*, evidence-ladder rung 4) **cannot be met today**.
The phase exists to (1) keep the SPIR-V-direct path honest and tested, and (2) be ready
if/when a MonoGame/KNI Vulkan runtime ships.

**Depends on:**
- **[Phase 4](DONE/PHASE-4-dxc-integration.md)** (DXC integration) — the Vulkan target is a single DXC compile to SPIR-V (`vs_6_0`/`ps_6_0` + `-spirv`); the whole frontend is shared with the OpenGL SPIR-V branch.
- **[Phase 30](PHASE-30-cross-platform-ci.md)** (cross-platform CI) — the SPIR-V-validity test must run on the Linux/macOS/Windows matrix; it is the only honest gate available without a render runtime.

**Blocks:** nothing on the critical path. This is post-1.0 backend breadth.

**Sibling parked backend:** [Phase 4.1 — WASM + DirectX DXBC spike](PHASE-4.1-SPIKE-wasm-directx-dxbc.md)
is the analogous "backend wired in code, blocked on a runtime/toolchain reality" item. Both are
parked for honesty, not abandoned.

> The product is the in-memory `IShaderCompiler` library (CLAUDE.md → THE PURPOSE). Vulkan is a
> *fourth output target* of that one faithful pipeline — **no substitute compiler**, same
> HLSL→DXC→SPIR-V front the OpenGL path uses. This phase must not introduce a different frontend.

---

## Overview

DXC already produces SPIR-V for every OpenGL compile — it is the OpenGL branch's intermediate
before SPIRV-Cross transpiles it to GLSL. The Vulkan target is simpler than OpenGL: it stops at
SPIR-V (no GLSL transpile, no MojoShader dialect rewrite) and writes that SPIR-V into the `.mgfx`
under the `Vulkan` profile byte. Most of the path is therefore already in the tree:

- `PlatformTarget.Vulkan = 3` (`src/ShadowDusk.Core/PlatformTarget.cs`).
- `MgfxProfile.Vulkan = 3` (`src/ShadowDusk.Core/MgfxProfile.cs`).
- CLI `/Profile:Vulkan` → `PlatformTarget.Vulkan` and the usage text lists `Vulkan` (`src/ShadowDusk.Cli/ArgumentParser.cs:177`, usage at `:16`).
- `PlatformMacros.For(Vulkan)` → `MGFX/HLSL/VULKAN/SM6` (`src/ShadowDusk.Core/Preprocessor/PlatformMacros.cs:11`).
- `DxcFlagBuilder` builds Vulkan VS (`vs_6_0`, `-spirv`, `-fvk-use-dx-layout`, `-fvk-invert-y`, `-fvk-use-dx-position-w`, `-fspv-reflect`) and Vulkan PS (`ps_6_0`, `-spirv`, `-fvk-use-dx-layout`, `-auto-binding-space 1`, `-fspv-reflect`) — `src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs:38-46`.
- `CompilationPipeline` maps `Vulkan → MgfxProfile.Vulkan` (`Internal/CompilationPipeline.cs:395`) and routes Vulkan through the generic `else` branch of `CompileEntryPointAsync` (`:517-540`), which does a single DXC compile and returns the SPIR-V in `spirvBlob`.

The remaining work is the **reflection + write wiring** (see *Architecture* — there is a real gap)
and validation, which is the honesty-gated part.

---

## Scope & Non-Goals

**In scope:**
- Make `EffectCompiler.CompileAsync` produce a structurally valid Vulkan-profile `.mgfx` whose shader blobs are valid SPIR-V, for the same PS-only (and ideally simple VS/PS) corpus already used elsewhere.
- Fill the **reflection gap**: the Vulkan branch currently feeds an *empty* DXIL blob to the DXIL-reflection oracle (see *Architecture*); route Vulkan reflection through the pure-managed SPIR-V reflector instead (the same `SpirvReflector` the WASM/OpenGL path already uses).
- A test gate proving the Vulkan target emits **valid SPIR-V** (magic word `0x07230203`, parseable module) and a structurally well-formed `.mgfx` with profile byte `3`.
- Documentation: a clear "parked pending a validation runtime" statement in the docs site backend page ([Phase 26](PHASE-26-documentation-site.md) → *Alternative Backends / Vulkan (future)*).

**Out of scope / Non-Goals (the honesty boundary):**
- **Any "renders like `mgfxc`" claim.** There is no `mgfxc` Vulkan output and no MonoGame Vulkan runtime — rung 4 of the evidence ladder is unreachable. Do **not** assert in-engine equivalence; the strongest honest claim is "valid SPIR-V + well-formed `.mgfx`."
- The **exact KNI/MonoGame Vulkan `.mgfx` container shape** (SPIR-V layout, push-constant vs UBO binding model, sampler/descriptor-set convention). Unknown until a runtime exists; the writer uses the existing v10 record shape as a placeholder, explicitly provisional.
- A SPIR-V → MSL/Metal path (that is [Phase 31 — Metal/MSL backend](PHASE-31-metal-msl-backend.md)).
- Vulkan-in-WASM specifics (the WASM DXC already emits SPIR-V; no separate spike needed — unlike DX DXBC's [Phase 4.1](PHASE-4.1-SPIKE-wasm-directx-dxbc.md)).

---

## Architecture & key decisions

- **Reuse the OpenGL frontend verbatim.** Vulkan is `HLSL →[DXC]→ SPIR-V`, full stop — the same DXC invocation the OpenGL branch makes for its intermediate. No GLSL transpile, no `MonoGameGlslRewriter`. This keeps the "one faithful pipeline" invariant.
- **The real wiring gap is reflection.** In `CompilationPipeline.RunAsync`, `reflectFromSpirv` is set **only for OpenGL** (`:120`) and `directX` only for DirectX (`:134`). A Vulkan compile therefore falls into the **DXIL `else` branch** (`:278-288`) and calls `ReflectionPipeline.ReflectAsync` with `DxilBlob = default` (empty — the Vulkan compile path at `:536-537` only fills `spirvBlob`). `ReflectionPipeline` then runs `DxilReflectionExtractor.Extract(<empty>)` (`Reflection/ReflectionPipeline.cs:24`), which has no DXIL to read — so any Vulkan shader with reflectable parameters fails reflection today. **Fix:** broaden the SPIR-V-reflection gate from `Target == OpenGL` to `Target is OpenGL or Vulkan` (or add an explicit Vulkan arm) so Vulkan reflects from its own SPIR-V via the injectable `_reflectorFactory` (`SpirvReflector`, already proven byte-equivalent to the DXIL oracle in `SpirvReflectionByteIdentityTests`). This removes the dead DXIL compile for Vulkan and is WASM-safe.
- **Profile byte is already correct** (`MgfxProfile.Vulkan = 3`, distinct from the `PlatformTarget` ordinal — note the warning comment in `MgfxProfile.cs`). The `.mgfx` writer needs no structural branch for Vulkan beyond emitting that byte and the SPIR-V blob as the shader payload.
- **`.mgfx` Vulkan container is provisional.** Without a runtime we cannot know the binding model a MonoGame/KNI Vulkan loader would expect (descriptor sets, push constants, sampler tables). The writer reuses the v10 record shape as a placeholder; this is explicitly marked "subject to change when a runtime defines the contract" so we don't bake in a guess as if validated.
- **Validation ceiling is rung 2–3, not 4.** `valid SPIR-V` + `well-formed .mgfx` + (optionally) cross-checking the SPIR-V is internally consistent with `spirv-val`/`spirv-dis` if available. That is the honest ceiling until a runtime exists.

---

## Tasks

- [ ] Broaden the SPIR-V-reflection gate in `CompilationPipeline` so `PlatformTarget.Vulkan` reflects from its own SPIR-V blob (via `_reflectorFactory` / `SpirvReflector`) instead of the empty-DXIL DXIL-oracle path; drop the dead DXIL compile for Vulkan.
- [ ] Confirm the generic `else` compile branch (`CompileEntryPointAsync`) returns SPIR-V in `spirvBlob` for Vulkan and that the reflection loop gates on `spirvBlob` (not `dxilBlob`) for the Vulkan case.
- [ ] Verify (and document) the `.mgfx` writer emits profile byte `MgfxProfile.Vulkan (3)` and the SPIR-V blob as the shader payload; mark the container shape provisional.
- [ ] Add an `EffectCompiler` integration test: compile a PS-only fixture (and one simple VS/PS) with `Target = Vulkan`; assert success, profile byte `3`, and that each shader blob begins with the SPIR-V magic word `0x07230203` and parses as a SPIR-V module.
- [ ] (If `spirv-val` is restorable via `tools/restore.*`) add an optional, skip-on-missing validity check of the emitted SPIR-V.
- [ ] Document the Vulkan target as **parked pending a validation runtime** on the docs-site backend page ([Phase 26](PHASE-26-documentation-site.md)); state plainly that no in-engine render validation exists.
- [ ] Ensure the existing/added Vulkan tests run on the [Phase 30](PHASE-30-cross-platform-ci.md) Linux/macOS/Windows matrix.

### Already in place (verified — do not re-do)

- [x] `DxcFlagBuilder` Vulkan VS flags: `-spirv`, `-fvk-use-dx-layout`, `-fvk-use-dx-position-w`, `-fvk-invert-y`, `-fspv-reflect`, `vs_6_0` — covered by `DxcFlagBuilderTests` (`Vulkan_Vertex_HasProfile_vs6_0`, `_HasInvertY`, `_HasFspvReflect`, `_HasSpirvFlag`).
- [x] `DxcFlagBuilder` Vulkan PS flags: `-spirv`, `-fvk-use-dx-layout`, `-auto-binding-space 1`, `-fspv-reflect`, `ps_6_0` — covered by `Vulkan_Pixel_HasProfile_ps6_0`, `_HasFspvReflect`.
- [x] "Vulkan target vertex shader → non-empty SPIR-V" — exists as `DxcShaderCompilerIntegrationTests.CompileVulkanVertex_ReturnsSpirvBlob` (`:74`, asserts `BlobKind.Spirv` + non-empty bytes).
- [x] CLI `/Profile:Vulkan`, `PlatformMacros.For(Vulkan)`, `PlatformTarget.Vulkan`, `MgfxProfile.Vulkan` all wired.

---

## Acceptance Criteria

- [ ] `EffectCompiler.CompileAsync` with `Target = PlatformTarget.Vulkan` succeeds for a PS-only fixture (and one simple VS/PS), without falling into the empty-DXIL reflection failure.
- [ ] The emitted `.mgfx` has profile byte `3` (`MgfxProfile.Vulkan`) and each shader payload is **valid SPIR-V** (magic `0x07230203`, parseable; `spirv-val`-clean if the tool is available) — tested on the Phase 30 matrix.
- [ ] The DXC flag-builder Vulkan VS/PS cases (`vs_6_0`/`ps_6_0` + OpenGL flags + `-fspv-reflect`) remain covered (already green).
- [ ] No claim of `mgfxc`/runtime render parity is made anywhere; the docs and this plan state the target is **parked pending a MonoGame/KNI Vulkan runtime**.
- [ ] The OpenGL and DirectX paths are unchanged (no regression in the byte-identity / image / cross-validation suites).

## Definition of Done

The Vulkan target compiles end-to-end through `EffectCompiler` to a structurally well-formed,
`Vulkan`-profile `.mgfx` whose shader blobs are **valid SPIR-V**, with a CI-gated test proving
that on all three OSes — and the path is **documented as parked**, with the explicit, honest
statement that *no MonoGame/KNI Vulkan runtime exists to validate "renders like `mgfxc`"
against*, so the evidence ladder tops out at rung 2–3 until such a runtime ships. When a
runtime arrives, this phase reopens to (a) pin down the real `.mgfx` Vulkan container/binding
model and (b) add the rung-4 render-equivalence validation that finishes the backend.

---

## Open questions / risks

- **No baseline, no bar (the central risk).** Without an `mgfxc` Vulkan output and a MonoGame/KNI Vulkan runtime, "valid SPIR-V" is the strongest honest claim. The danger is mistaking a green SPIR-V-validity test for the product bar — it is **rung 2–3, not rung 4**. Guard against the proxy-as-bar trap CLAUDE.md warns about.
- **Unknown `.mgfx` Vulkan container.** The descriptor/binding model (push constants vs UBO, sampler/descriptor-set layout, SPIR-V placement in the record) is undefined until a runtime exists. Anything we write now is provisional and will likely change.
- **KNI Vulkan story unclear.** Whether KNI ever ships a Vulkan backend (desktop or WASM via WebGPU) is unknown. Coordinate with the KNI runtime question raised in [Phase 4.1](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) Option D before investing further.
- **SM6/DXIL vs SPIR-V symmetry.** Vulkan uses `vs_6_0`/`ps_6_0` like the DX12/KNI DXIL path, but emits SPIR-V (`-spirv`), not DXIL — keep the two SM6 targets distinct in the flag builder and pipeline so a future DX12 path and Vulkan don't get conflated.
- **`SpirvReflector` coverage.** The managed reflector is proven byte-equivalent to the DXIL oracle for the OpenGL corpus; confirm it handles the Vulkan `-fspv-reflect`-decorated SPIR-V (the reflect builtins) without surprises before relying on it for the Vulkan gate.
