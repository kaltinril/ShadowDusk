# Phase 31 — Metal / MSL Backend

**Status:** Future (Track: *Backend breadth, post-1.0*). Today the same `.fx` can target
OpenGL (GLSL) and DirectX (DXBC); Apple GPUs are unreached. This phase adds the **Metal
Shading Language (MSL)** emission target so one HLSL source can also produce Metal — the
GLSL branch's direct analogue, since SPIRV-Cross already speaks MSL
(`SpvcBackend.Msl = 3`, verified in `src/ShadowDusk.GLSL/Interop/SpvcNative.cs:11`).
The MSL *emission* is the easy half; the hard half — and the reason this is **Future**,
not Planned — is the **validation surface**: there is no MonoGame/KNI Metal runtime and no
Apple hardware in the dev loop, so this phase must define how MSL gets render-validated
against `mgfxc` (the bar) and must **not** declare "done" on unvalidated output.

**Depends on:**
- [Phase 6](DONE/PHASE-6-spirv-cross-glsl-transpilation.md) — SPIRV-Cross C-API P/Invoke (`SpvcLoader`/`SpvcNative`). MSL reuses the same context/parse/compile flow, swapping the backend enum.
- [Phase 17](DONE/PHASE-17-monogame-runtime-validation.md) — the OpenGL PS-only corpus is render-validated in the real MonoGame runtime; the *prerequisite gate in `plan.md` Key Decisions* ("Metal scope: out of scope until the OpenGL path is working and validated") is now satisfied, which is what unblocks starting this phase.

**Blocks:** nothing in the product pipeline. Metal is a *breadth* target, parallel to OpenGL/DirectX, not on the critical path to 1.0.

> The product is the in-memory `IShaderCompiler` library (see `CLAUDE.md` → THE PURPOSE). Metal widens *which GPU backends* that library can emit; it does not change what the product is. Per THE PURPOSE, **a host that cannot validate a faithful component is not done** — so an MSL emitter that no real runtime exercises is, at most, *partially* done.

---

## Overview

The Metal pipeline shape is the GLSL branch with a different SPIRV-Cross backend:

```
HLSL → DXC → SPIR-V → SPIRV-Cross(MSL) → [managed: reflect + MGFX writer] → .mgfx (Metal)
```

This is **one faithful pipeline, no substitute compiler** — identical to the OpenGL leg in
`src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs` (`CompileEntryPointAsync`, the
`PlatformTarget.OpenGL` branch) up to the SPIR-V blob; only the SPIRV-Cross compiler
backend differs (`SpvcBackend.Msl` instead of `SpvcBackend.Glsl`). DXIL-based reflection,
the `ReflectionPipeline`, and the `MgfxWriter` are shared.

What exists today (verified):
- `src/ShadowDusk.Metal/MslEmitter.cs` is an empty stub: `public sealed class MslEmitter { }`.
- `ShadowDusk.Metal.csproj` references only `ShadowDusk.Core` (no SPIRV-Cross reference yet).
- `PlatformTarget.Metal = 2` exists (`src/ShadowDusk.Core/PlatformTarget.cs:9`).
- `CompilationPipeline.RunAsync` **hard-rejects** Metal early with `SD0200` "Metal target not yet supported" (`CompilationPipeline.cs:41-49`).
- `PlatformMacros.For` has **no Metal case** — it throws `ArgumentOutOfRangeException` (mapped to `X0010`) for any target outside DirectX/OpenGL/Vulkan (`src/ShadowDusk.Core/Preprocessor/PlatformMacros.cs:7-13`).
- CLI `ArgumentParser.ParseProfile` has **no `Metal`/`MacOSX` profile** — it returns `X0004` "Unknown profile" (`src/ShadowDusk.Cli/ArgumentParser.cs:169-198`). *(Note: the `X0010` codes there are for PS4/XboxOne/Switch; `PipelineRunner.cs` has no Metal-specific code — it forwards `args.Platform` unchanged. The PHASE-100 backlog line "PipelineRunner currently returns X0010 for Metal" is therefore stale: the actual gate is `SD0200` in `CompilationPipeline`.)*

---

## Scope & Non-Goals

**In scope:**
- Implement `MslEmitter` to emit MSL from a SPIR-V blob via SPIRV-Cross (mirroring `SpirvCrossGlslTranspiler`), behind an `ISpirvToMslTranspiler` seam.
- Add the SPIRV-Cross project reference + `SpvcLoader.Register()` wiring to `ShadowDusk.Metal`.
- Wire the `PlatformTarget.Metal` branch end-to-end through `CompilationPipeline` (remove the `SD0200` early-return; add the Metal `CompileEntryPointAsync` branch), `PlatformMacros.For` (a Metal macro set), and CLI `ParseProfile` (a `MacOSX`/`Metal` profile mapping to `PlatformTarget.Metal`).
- Emit MSL for the PS-only SM3 corpus and confirm SPIRV-Cross succeeds for all 10.
- **Define the real-runtime render-equivalence story** (see *Architecture* below) even if execution is hardware-blocked.

**Out of scope / Non-Goals:**
- Claiming Metal "done" / production-ready without a real-runtime render proof (rung 4 of the evidence ladder). Until then Metal ships as **experimental / unvalidated** and is labelled so everywhere (matching the [Phase 26](PHASE-26-documentation-site.md) "future backend" treatment).
- VS-driven Metal effects — the OpenGL PS-only gate (`fxParsed.Techniques.All(... VertexEntryPoint is null)` in `CompilationPipeline.cs:112-113`) carries forward; VS-driven remains backlog `17-VS`.
- iOS/tvOS specifics, Metal argument buffers, the `.metallib` precompile step, or any Metal-version targeting beyond what MonoGame/KNI's loader actually consumes.
- Running the Metal validation on CI hardware — execution is owned by / coordinated with [Phase 30](PHASE-30-cross-platform-ci.md) (macOS CI), which is where any real Apple runner lives.

---

## Architecture & key decisions

- **Reuse the GLSL transpiler shape, not the code path.** Add `src/ShadowDusk.Metal/ISpirvToMslTranspiler.cs` + `SpirvCrossMslEmitter` (rename/implement `MslEmitter`) modelled on `src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs`: `spvc_context_create` → `parse_spirv` → `create_compiler(ctx, SpvcBackend.Msl, …)` → set MSL options → `compile`. The SPIR-V blob comes from the **same** DXC compile the OpenGL branch already produces.
- **MSL-specific SPIRV-Cross options.** Unlike GLSL, MSL has no `FlipVertexY`/`GlslVersion` knobs; it needs the MSL option family (platform = macOS vs iOS, MSL version, `pad_fragment_output`, resource-binding remap). These constants are **not yet declared** in `SpvcNative.cs` (only the GLSL option IDs exist) — this phase adds the MSL `SpvcCompilerOption` values and the `SpvcBackend.Msl` create-compiler call.
- **No MonoGame "MojoShader" rewrite for Metal.** The OpenGL path runs `MonoGameGlslRewriter` to match MonoGame's GL `SpriteEffect` dialect. Metal has no such legacy-dialect contract; the open question is what **MSL dialect / entry-point/binding convention** the target Metal runtime expects — which is undefined until a runtime is chosen (below).
- **`.mgfx` Metal profile.** `MgfxWriter` switches on `options.Target`; `MgfxProfile` currently maps DirectX/OpenGL/Vulkan only (`CompilationPipeline.cs:391-397`, default `OpenGL`). A Metal profile id must match whatever loader consumes it — i.e. it is **derived from the chosen runtime**, not invented here.
- **The validation surface is the deliverable's hard problem.** Per the evidence ladder in `CLAUDE.md`, only "loads in the real runtime and renders like `mgfxc`" (rung 4) proves the promise, and **comparison is same-backend only** (Metal↔Metal, never Metal↔GL). Concretely, MonoGame 3.8 on Apple platforms runs **OpenGL via MojoShader**, *not* a mature first-class Metal `Effect` loader, and `mgfxc` itself has **no MSL output target** — so there may be **no same-backend `mgfxc` oracle to compare against**. The phase must pick one of:
  1. A MonoGame/KNI Metal backend **if/when one exists** that loads an MSL-bearing `.mgfx` — then validate render-equivalence against `mgfxc`'s Metal output (only viable if `mgfxc` gains MSL).
  2. A **non-MonoGame Metal harness** (compile MSL with Apple's `metal`/`metallib` toolchain, render the corpus offscreen, compare to the OpenGL reference image of the *same shader intent* as a sanity proxy) — explicitly a **proxy, not the bar**, and labelled as such.
  3. **Document the gap**: MSL emission is verified to compile via SPIRV-Cross + Apple's `metal` front-end, but render-equivalence is *unproven* pending a real runtime — and ship Metal as experimental.
  This decision (which runtime, whether an `mgfxc` Metal oracle even exists) is the gating risk and must be resolved **before** any "Metal done" claim.

---

## Tasks

- [ ] Add the SPIRV-Cross dependency wiring to `ShadowDusk.Metal.csproj` (reference `ShadowDusk.GLSL`'s interop or factor the P/Invoke into a shared interop assembly; call `SpvcLoader.Register()`).
- [ ] Declare the MSL `SpvcCompilerOption` IDs (platform, MSL version, `pad_fragment_output`, etc.) in `src/ShadowDusk.GLSL/Interop/SpvcNative.cs`.
- [ ] Implement `ISpirvToMslTranspiler` + `SpirvCrossMslEmitter` (replace the empty `MslEmitter`), modelled on `SpirvCrossGlslTranspiler`, using `SpvcBackend.Msl`.
- [ ] Add a Metal macro set to `PlatformMacros.For` (e.g. `MGFX`, `MSL`, `METAL`).
- [ ] Wire the `PlatformTarget.Metal` branch in `CompilationPipeline`: remove the `SD0200` early-return, add a Metal arm in `CompileEntryPointAsync` (DXC→SPIR-V→MSL), and add a Metal `MgfxProfile` mapping.
- [ ] Add a `MacOSX`/`Metal` profile to CLI `ArgumentParser.ParseProfile` → `PlatformTarget.Metal`; confirm `PipelineRunner` forwards it unchanged.
- [ ] **Decide the validation strategy** (runtime #1 / proxy harness #2 / documented gap #3) and write it into this doc + the docs site's "Metal (future)" page.
- [ ] Emit MSL for all 10 PS-only corpus shaders; assert SPIRV-Cross succeeds and (if a `metal` front-end is available) that the MSL compiles.
- [ ] Add unit tests (`SpvcBackend.Msl` path produces non-empty MSL for a known SPIR-V blob) and an integration test gated/skipped when no Metal runtime is present.
- [ ] Update the PHASE-100 backlog reference (resolved here) and `CLAUDE.md`'s backend table (Metal: experimental/unvalidated, with the exact caveat) — done by the orchestrator, but tracked here.

---

## Acceptance Criteria

- [ ] `MslEmitter`/`SpirvCrossMslEmitter` emits non-empty MSL from each of the 10 PS-only corpus shaders' SPIR-V blobs (SPIRV-Cross `compile` succeeds 10/10).
- [ ] The `PlatformTarget.Metal` target compiles **end-to-end** through `CompilationPipeline` and the CLI (`mgfxc /Profile:MacOSX` or equivalent) to a `.mgfx` with no `SD0200`/`X0010`/`X0004` rejection.
- [ ] A **real-runtime render-equivalence story is defined and recorded** — naming the chosen Metal runtime (or explicitly the documented gap + proxy), and stating that until rung 4 is met Metal is **experimental, not validated** (same-backend comparison only; never compared against GL output).
- [ ] No doc/code claims Metal is "done"/production while render-validation is unmet; the docs site's Metal page and `CLAUDE.md` backend table reflect the true status.
- [ ] Existing OpenGL/DirectX behavior is byte-unchanged (the GLSL/DXBC paths are untouched; no shared option/enum regression).

## Definition of Done

The same `.fx` can be compiled to a Metal-targeted `.mgfx` through the **one faithful
pipeline** (DXC → SPIR-V → SPIRV-Cross MSL → MGFX), wired through `CompilationPipeline`,
`PlatformMacros`, the CLI, and the `MgfxWriter`, with MSL emitted for the full PS-only
corpus. **And** the phase has an explicit, honest answer to "how does this render like
`mgfxc` in a real Metal runtime" — either a passing rung-4 validation against a real
MonoGame/KNI Metal backend (full done), or a clearly-documented validation gap with Metal
shipped as **experimental** (partial done, per THE PURPOSE: an unvalidatable faithful
component is *not* finished). Metal is never silently presented as validated.

---

## Open questions / risks

- **No same-backend oracle (the central risk).** `mgfxc` emits no MSL and MonoGame 3.8 Apple targets run GL-via-MojoShader, so there may be **no `mgfxc` Metal output** to compare against. Without a same-backend reference, rung 4 is unreachable by the normal method — hence the experimental-until-runtime stance.
- **No Apple hardware / Metal runtime in the dev loop.** Even MSL *compilation* (Apple's `metal`/`metallib`) and offscreen render checks need macOS; this couples tightly to [Phase 30](PHASE-30-cross-platform-ci.md)'s macOS runner. Until then, validation is at best SPIRV-Cross-emits-without-error (rung 1–2).
- **Which Metal `.mgfx` shape does any consumer load?** The `MgfxProfile` id, MSL entry-point/binding convention, and whether a MojoShader-style rewrite is needed are all **undefined** until a concrete Metal runtime is chosen — do not invent a format with no loader.
- **Shared SPIRV-Cross interop ownership.** The P/Invoke currently lives in `ShadowDusk.GLSL/Interop`. Referencing it from `ShadowDusk.Metal` (vs. extracting a shared interop assembly) is a small structural decision to make before adding MSL options to it.
- **Scope creep into iOS/Vulkan.** Keep this phase to macOS-class MSL emission + the validation-story decision; iOS specifics and the Vulkan direct-SPIR-V target are separate future backends.
