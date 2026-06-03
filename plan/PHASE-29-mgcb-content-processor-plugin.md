# Phase 29 — MGCB Content-Processor Plugin (Tier 2)

**Track:** Delivery shapes.
**Status:** Planned (written 2026-06-03). Promote `src/ShadowDusk.MgcbPlugin` from a stub to a
real MonoGame Content Builder **Tier-2** integration — an `EffectImporter`/`EffectProcessor`
(or equivalent `IContentProcessor`) that lets **MGCB invoke ShadowDusk natively in-process**,
without shelling out to a PATH-resolved `mgfxc` binary. This is a **convenience delivery shape**
layered on top of the already-working Tier-1 drop-in, not a new product surface and not required
for the core promise (see `CLAUDE.md` → THE PURPOSE: the **library is the product**; the CLI and
the MGCB plugin are *delivery shapes* of it).

**Depends on:**
- [Phase 8 — Compiler Library](DONE/PHASE-8-compiler-library.md): the `EffectCompiler : IShaderCompiler` entry point the plugin wraps (`src/ShadowDusk.Compiler/EffectCompiler.cs`). The plugin adds zero new compilation logic — it is an adapter onto `CompileAsync`.
- [Phase 9 — CLI Entry Point](DONE/PHASE-9-cli-entry-point.md): the `mgfxc` CLI is the **Tier-1 baseline** (`PackAsTool` + `ToolCommandName=mgfxc`, `src/ShadowDusk.Cli`). Tier 2 must produce the **same `.mgfx`** the CLI does and stays a strict superset, never a replacement.

**Blocks:** Nothing in the product pipeline. This is an ergonomics deliverable for MGCB users who prefer a `/reference`'d plugin over a PATH/`ExternalTool` override. The single combined-proof goal (compile where `mgfxc` can't + render like `mgfxc`) is fully met without it.

> Reference decision — `plan/plan.md` Key Decisions: *"MGCB integration: Tier 1 only (PATH-based drop-in binary named `mgfxc`). Tier 2 content processor plugin is a separate future undertaking."* This phase **is** that undertaking.

---

## Overview

MonoGame's stock content pipeline builds `.fx` via the built-in `EffectImporter` + `EffectProcessor`
(referenced in every `#begin` block of an `.mgcb` file as `/importer:EffectImporter` /
`/processor:EffectProcessor`). Those stock processors internally shell out to `mgfxc`, which on
Windows depends on `fxc.exe`. **Tier 1** (already working) sidesteps this by making ShadowDusk's
CLI *be* `mgfxc` on `PATH` (or via MGCB `ExternalTool`/`MGFXC_*` override) — MGCB calls it
unchanged, gets back a `.mgfx`, and never knows the difference.

**Tier 2** is a *native* MGCB plugin: a `.csproj`-built assembly the consumer adds via the
`.mgcb` `/reference:` directive, exposing a `[ContentImporter]`/`[ContentProcessor]` pair that MGCB
discovers by reflection and runs **in-process** — no child `mgfxc` process, no PATH plumbing. Its
`Process` method hands the `.fx` source to `EffectCompiler.CompileAsync` and wraps the resulting
`.mgfx` bytes in the `CompiledEffectContent` (or equivalent) the MGCB `ContentWriter` serializes
into the `.xnb` that MonoGame's `Effect` constructor consumes at load time. The output `.mgfx`
must be byte-for-byte what the Tier-1/CLI path emits for the same source + target.

---

## Scope & Non-Goals

**In scope:**
- Real `.cs` source in `src/ShadowDusk.MgcbPlugin` (currently **only** a `.csproj` + a `ProjectReference` to `ShadowDusk.Core` — **zero source files**, confirmed stub).
- A `[ContentImporter]`-attributed importer for `.fx` (or reuse the source-file passthrough) and a `[ContentProcessor]`-attributed processor that calls `EffectCompiler.CompileAsync` and emits `.mgfx`-bearing content MGCB can write to `.xnb`.
- Processor parameters mapping the user-relevant `CompilerOptions` (target platform from MGCB's `/platform`, `Debug`, include paths, MGFX version, DXBC backend selector for DirectX).
- Wiring the **existing `samples/mgcb`** project to exercise Tier 2 via `/reference:` (today it uses the *stock* `EffectImporter`/`EffectProcessor`, i.e. mgfxc — not ShadowDusk; switching it to ShadowDusk-Tier-2 is the sample's job).
- A NuGet package shape (`/reference:`-able assembly + transitive native assets) so the plugin is self-contained like the rest of the product.

**Out of scope / Non-Goals:**
- Replacing or deprecating Tier 1 — the PATH/`ExternalTool` drop-in stays the baseline and the only *required* MGCB integration.
- New compilation behavior. The plugin is a thin adapter; **all** HLSL→`.mgfx` logic stays in `EffectCompiler`/`CompilationPipeline`. No second pipeline, no substitute compiler (see THE PURPOSE).
- VS-driven effects on the GL path (backlog `17-VS`) and Metal (`MslEmitter` stub) — the plugin inherits whatever the library supports; it does not expand backend coverage.
- A bespoke `.mgcb` editor / GUI integration.

---

## Architecture & key decisions

- **Discovery contract.** MGCB loads plugin assemblies named by `/reference:<assembly-or-package>` in the `.mgcb` file, then reflects for `[ContentImporter(".fx")]` and `[ContentProcessor]` attributes (from `MonoGame.Framework.Content.Pipeline`). The user selects them per item with `/importer:` and `/processor:` (e.g. `/processor:ShadowDuskEffectProcessor`). This is the same discovery the stock `EffectProcessor` uses; ShadowDusk just provides an alternative processor type.
- **Package coupling.** The plugin must reference `MonoGame.Framework.Content.Pipeline` (the build-time content-pipeline contracts — **not** currently referenced anywhere in `src/`; the only MonoGame content-pipeline usage today is the stock-processor strings in `samples/mgcb/Content/Content.mgcb`). Pin the same MonoGame version the samples use (`3.8.2.1105`, per `samples/mgcb/MGCBSample.csproj`) to avoid contract drift.
- **Adapter, not pipeline.** The processor's `Process(input, context)` builds a `CompilerOptions` from the MGCB build context (`/platform:DesktopGL` → `PlatformTarget.OpenGL`, `WindowsDX` → `PlatformTarget.DirectX`), calls `EffectCompiler.CompileAsync` (`src/ShadowDusk.Compiler/EffectCompiler.cs`), unwraps the `Result<CompiledShader, ShaderError[]>`, and on failure surfaces each `ShaderError` through `context.Logger`/`PipelineException` in MGCB's expected file(line,col) format (constraint 5 — fail loudly). On success it wraps `CompiledShader.Data` (the `.mgfx` bytes) for the content writer.
- **Output identity.** `.mgfx` bytes from the plugin must equal the Tier-1/CLI bytes for the same source + target (constraint 3, ShadowDusk's own reproducibility) — because both call the *same* `EffectCompiler`. A test asserts plugin-output ≡ CLI-output per fixture.
- **Async boundary.** `IContentProcessor.Process` is synchronous; bridge to the async `CompileAsync` without `.Result`/`.Wait()` deadlock risk (constraint: `async` all the way down). Use `GetAwaiter().GetResult()` on a `ConfigureAwait(false)`-rooted call, or a small sync-over-async pump, and document why this one boundary is unavoidable (MGCB's contract is sync).
- **`MgfxProfile` vs `PlatformTarget` mismatch caution.** `src/ShadowDusk.Core/MgfxProfile.cs` warns these enums do **not** share ordinals (`PlatformTarget.OpenGL=1`, `MgfxProfile.OpenGL=0`). The MGCB `/platform` → `PlatformTarget` mapping must be explicit, never an ordinal cast.
- **Self-contained packaging.** Like the CLI, the plugin NuGet carries the native DXC/SPIRV-Cross (and, for DirectX, vkd3d/d3dcompiler) transitively so `/reference:`ing the package "just works" (constraints 1 & 7).

---

## Tasks

- [ ] Confirm the stub: `src/ShadowDusk.MgcbPlugin` has no `.cs` (only the `.csproj` + Core `ProjectReference`) — establish the starting line.
- [ ] Add the `MonoGame.Framework.Content.Pipeline` package reference (pinned to the samples' MonoGame version) and a `ProjectReference`/dependency to `ShadowDusk.Compiler`.
- [ ] Implement `ShadowDuskEffectImporter` (`[ContentImporter(".fx", ...)]`) — read source + record the source path for include resolution and diagnostics.
- [ ] Implement `ShadowDuskEffectProcessor` (`[ContentProcessor(DisplayName=...)]`): map MGCB build context → `CompilerOptions`; call `EffectCompiler.CompileAsync`; emit `.mgfx`-bearing content; route `ShaderError[]` to `context.Logger` in MGCB's parseable format.
- [ ] Expose processor parameters: target (from `/platform`), `Debug`, additional include paths, `MgfxVersion`, and `DxbcBackend` (DirectX).
- [ ] Decide content-write strategy: produce `CompiledEffectContent`-compatible output (reuse MonoGame's effect `ContentWriter`) **or** ship a tiny ShadowDusk writer — pick whichever yields the byte-identical `.xnb`/`.mgfx` MonoGame's `Effect` loads.
- [ ] Switch `samples/mgcb/Content/Content.mgcb` (or a parallel `.mgcb`) from stock `EffectImporter`/`EffectProcessor` to the ShadowDusk Tier-2 processor via `/reference:`; keep a Tier-1 variant for comparison.
- [ ] Add the plugin to `ShadowDusk.slnx` and a NuGet pack target with transitive native assets.
- [ ] Tests: plugin-emitted `.mgfx` ≡ CLI/Tier-1 `.mgfx` per fixture; error path surfaces file/line/col; an MGCB build of the sample produces loadable `.xnb`.
- [ ] Run `/platform-check` — no new platform-specific assumptions (the plugin runs at build time on Linux/macOS/Windows).

## Acceptance Criteria

- [ ] An MGCB project that `/reference:`s `ShadowDusk.MgcbPlugin` and selects its `[ContentProcessor]` builds `.fx → .xnb` with **no `mgfxc` child process** and **no PATH override**.
- [ ] The resulting effect **loads in MonoGame's `Effect` and renders like `mgfxc`'s** for the validated PS-only corpus — same-backend comparison, the real runtime (evidence-ladder rung 4; `CLAUDE.md` → *What success actually means*).
- [ ] The plugin's `.mgfx` bytes are **identical** to the Tier-1/CLI output for the same source + target.
- [ ] Shader errors surface through MGCB's logger with file/line/column in the format MGCB parses (constraint 5).
- [ ] `samples/mgcb` exercises Tier 2 end-to-end; `dotnet build` of the sample succeeds on a machine with no `mgfxc`/`fxc.exe`.
- [ ] The plugin NuGet is self-contained (native deps flow transitively); Tier 1 remains fully functional and documented as the baseline.

## Definition of Done

`src/ShadowDusk.MgcbPlugin` is a real, packaged MonoGame Tier-2 content-processor plugin: a
consumer adds the package, `/reference:`s it in their `.mgcb`, selects the ShadowDusk processor,
and MGCB compiles `.fx → .xnb` natively in-process — producing the **same `.mgfx`** as the
Tier-1 CLI and an effect that loads and renders like `mgfxc`'s in the real MonoGame/KNI runtime.
Tier 1 stays the baseline; Tier 2 is the documented convenience. The `samples/mgcb` sample
demonstrates it, and tests pin plugin-output ≡ CLI-output. The PHASE-100 carry-forward *"Full MGCB
content processor plugin (`ShadowDusk.MgcbPlugin`) — separate undertaking post-Phase 8"* is closed.

---

## Open questions / risks

- **MonoGame content-pipeline API/version coupling.** `MonoGame.Framework.Content.Pipeline` contracts (`ContentImporter`/`ContentProcessor`/`ContentWriter`, `CompiledEffectContent`) can shift across MonoGame versions and differ on KNI. Pin a version, document the supported range, and consider whether a KNI-targeted plugin variant is needed.
- **Reusing vs. reimplementing the effect `ContentWriter`.** Emitting `CompiledEffectContent` may drag in MonoGame's *own* `.mgfx` serialization (or even re-invoke `mgfxc`), defeating the purpose. Verify the write path actually serializes **ShadowDusk's** bytes unchanged; if not, ship a minimal ShadowDusk writer.
- **Sync/async bridge.** `IContentProcessor.Process` is synchronous over an async `CompileAsync` — must avoid deadlock and keep diagnostics intact; this is the one sanctioned sync-over-async boundary.
- **Native-asset loading inside MGCB's plugin host.** MGCB loads the plugin into its own process/AppDomain; confirm the transitive DXC/SPIRV-Cross (and DirectX vkd3d) natives resolve from the plugin's directory there, not just from a normal app's output (relates to [Phase 25](PHASE-25-security-hardening.md) Finding 6's `SpvcLoader` base-directory probing).
- **Scope drift.** This is a *convenience*, not the product. Time-box it; do not let Tier-2 work expand backend coverage or redefine the goal (`CLAUDE.md` → THE PURPOSE).
