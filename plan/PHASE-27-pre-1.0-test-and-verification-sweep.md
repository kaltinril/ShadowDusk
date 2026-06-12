# Phase 27 — Pre-1.0 Test & Verification Sweep

**Track:** Release (→ v1.0).
**Status:** Planned (written 2026-06-03). A consolidation pass that closes the deferred
unit/integration test-coverage and manual pack/CLI verification items parked across
Phases 2–9 and 15, so that **test coverage is not a 1.0 blind spot**. Many items are
"file exists — run it and check it off"; a handful need new tests written. The product is
a **drop-in `mgfxc` replacement** (`CLAUDE.md` → THE PURPOSE) — the coverage gaps that
matter here are the ones that would let a behavioral or diagnostic regression ship
silently against that promise.

**Depends on:**
- The existing test projects: `ShadowDusk.HLSL.Tests`, `ShadowDusk.Core.Tests`,
  `ShadowDusk.Compiler.Tests`, `ShadowDusk.Integration.Tests` (where the
  native-library-gated work lives). No production-code prerequisites.
- The SPIRV-Cross native library being restored (`tools/restore.ps1` / `tools/restore.sh`
  → `tools/spirv-cross/`) for the integration-gated items.

**Blocks:** Nothing in the product pipeline. This is a release-readiness gate, not a
feature. It feeds [Phase 30](DONE/PHASE-30-ci-and-nuget-release.md), which **runs** the resulting
suite across Linux/macOS/Windows (and owns the cross-platform run validation this phase
deliberately does *not* duplicate).

> Coverage is a **proxy**, not the bar (`CLAUDE.md` → *evidence ladder*). This phase
> raises the proxy floor before 1.0; it does not relitigate rung-4 in-engine equivalence
> (proven for the PS-only corpus in Phases 17/18/23/24).

---

## Overview

[Phase 100](PHASE-100-deferred-backlog.md) is the single deferred bucket. Items shouldn't
live there forever; the test/verification subset of it is being **promoted into this real
plan** so it gets done (or gets an explicit, recorded re-deferral) before 1.0. Verification
of the current tree (2026-06-03) found most of the referenced test *files* already exist
and are well-covered — so a large fraction of Phase 100's checklists are **already
satisfiable by simply running them and ticking the box**. The remainder are genuine gaps:
a CLI-process invocation theory, a few native-gated assertions, a golden-snapshot
reflection test, direct `ShaderIRBuilder` unit tests, and three manual pack/install/publish
steps.

---

## Scope & Non-Goals

**In scope:**
- Run every Phase-100 *"file exists — verify coverage"* item, confirm the assertion is
  present, and check it off (HLSL/DXC unit + reflection + DXC integration).
- Write the genuinely-missing tests: CLI-process `[Theory]` parity, the Y-flip VS
  transpile assertion, the SD0101 binding-mismatch assertion, the `MgfxParameterMatch`
  golden snapshot, a `FileSystemIncludeResolver` integration test, a `DxcIncludeHandler`
  smoke test, and direct `ShaderIRBuilder` index/annotation tests.
- The three manual CLI pack/install/publish verifications (Phase 9 §9.4–9.6).
- For every item not implemented: an **explicit re-deferral with a one-line reason**
  recorded in the originating `DONE/PHASE-X` doc and in Phase 100.

**Out of scope / Non-Goals:**
- **Cross-platform *runs*** of the suite on Linux/macOS — owned by
  [Phase 30](DONE/PHASE-30-ci-and-nuget-release.md) (Phase 15 §9 / the old "Phase 10 CI"
  criterion). Reference it; do not duplicate the matrix here.
- **Already-resolved** Phase-100 items: `11-6-A` (transpiler wired — Phase 8),
  `11-6-D` (uniform remap — Phase 17), Phase 8 packaging `7.4/7.5` (NuGet drop-in fix,
  branch `selfcontained-inmemory-nuget`). Do not re-open.
- **VS-driven GL effects** (`17-VS`) — a feature, not a test gap; stays in Phase 100.
- The browser-runtime tail (moved out of Phase 100 to Phases 23/24/30 §16).
- New security-hardening tests — those belong to [Phase 25](PHASE-25-security-hardening.md)
  (its Finding-1 `../`-escape test overlaps the include-resolver work; coordinate so the
  two don't write competing tests).

---

## Architecture & key decisions

Verified against the tree on 2026-06-03 (cite real files):

- **DXC flag/diagnostic unit tests already exist and are rich.**
  `tests/ShadowDusk.HLSL.Tests/Dxc/DxcFlagBuilderTests.cs` and
  `DxcDiagnosticReformatterTests.cs` cover essentially the full Phase-4 checklists
  (GL/Vulkan/DirectX profiles, `-spirv`/`-Zpr`/`-WX`/macros/`-E` ordering;
  empty/well-formed/warning/note/raw/multi-error reformatting). These are **verify-and-tick**,
  with one nuance to record: the macro flag format is `-DFOO=1` / `-DBAR` (single token),
  not Phase-100's `-D Name=Value` (two tokens) — update the checklist wording to match
  reality, don't "fix" the code.
- **DXC integration tests already exist:** `tests/ShadowDusk.Integration.Tests/Dxc/DxcShaderCompilerIntegrationTests.cs`
  covers minimal VS/PS → SPIR-V (OpenGL), VS → DXBC (DirectX), Vulkan VS → SPIR-V, two
  failure-with-`FxcFormattedMessage` cases, a `-D` macro round-trip, and
  pre-invocation cancellation. **Native-gated** (real DXC). Verify-and-tick.
- **Binding-slot verifier exists but the mismatch assertion does not.**
  `tests/ShadowDusk.Integration.Tests/Reflection/SpvBindingVerificationTests.cs` uses
  `SpvReflectionVerifier.GetBindings(...)` and ends with `// TODO Phase 6: add mismatch
  assertion`. The SD0101 (`"SD0101"`) DXIL-vs-SPIR-V slot-mismatch path (Phase 5 §7.3.3)
  is **unasserted** — needs a new negative test. **Native-gated.**
- **`MgfxParameterMatch` golden snapshot test does not exist** (no
  `tests/**/MgfxParameterMatch*.cs`). Phase 5 §9.3.1/§9.3.2 is a genuine gap; the
  `mgfxc` reference goldens live under `tests/fixtures/golden/` and the `mgfxc`
  reader is `tests/ShadowDusk.ImageTests/MgfxcMgfxReader.cs`.
- **GLSL transpiler tests exist but lack the Y-flip assertion.**
  `tests/ShadowDusk.Integration.Tests/Glsl/GlslTranspilerTests.cs` asserts `void main(`,
  `#version 140`, `sampler2D`, and invalid/empty-SPIR-V failures — but **no `gl_Position.y`
  sign-flip** check (Phase 6 `11-6-B`). Needs adding (`passthrough_vs.fx`). **Native-gated.**
- **CLI-process invocation mode is implemented but untested.**
  `tests/ShadowDusk.Integration.Tests/TestHelpers.cs` has `CompileViaCliAsync` and the
  `InvocationMode { CliProcess, DirectPipeline }` enum; `CompileFixtureTests.Compile_ProducesValidMgfxHeader`
  runs **only** `DirectPipeline` (the default param). Both `CliBinaryFixture` (reuse-built,
  Phase 21) and `Fixtures/CliFixture.cs` (skip-on-missing) **coexist** — Phase 15 asks
  whether to unify them. Needs a `[Theory]` over both modes + the fixture decision.
- **`ShaderIRBuilder` has no direct unit tests.**
  `tests/ShadowDusk.Compiler.Tests/ShaderIRBuilderTests.cs` exercises IR only through the
  public `EffectCompiler` (empty-source error + multipass order). `ShaderIRBuilder.Build`
  is internal and `src/ShadowDusk.Compiler/ShadowDusk.Compiler.csproj` has **no**
  `InternalsVisibleTo("ShadowDusk.Compiler.Tests")` — needed for the zero-based-index and
  empty-annotations tests (Phase 8).
- **CLI packaging is configured, never verified.**
  `src/ShadowDusk.Cli/ShadowDusk.Cli.csproj` sets `PackAsTool` + `ToolCommandName=mgfxc`
  + `PublishSingleFile`. Phase 9 §9.4–9.6 (pack / `tool install -g` / self-contained
  `publish`) are **manual** steps not yet run. Note Metal is a stub
  (`src/ShadowDusk.Metal/MslEmitter.cs` = `public sealed class MslEmitter { }`); the CLI
  rejects unknown profiles via `ArgumentParser.cs` (X0010 for console platforms, X0004 for
  unknown — Metal is not even a profile), so no Metal pipeline test is in scope.
- **Stripped-HLSL-compiles (Phase 2)** is implicitly covered: the FX9 pre-parser output is
  fed through real DXC by the integration suite; tick it once the DXC integration run is
  green, or add one explicit assertion if reviewers want it named.

---

## Tasks

### A. HLSL / DXC unit (pure — no native; verify-and-tick)
- [x] Confirm `DxcFlagBuilderTests` covers the Phase-4 flag matrix; reconcile the
      `-DName=Value` checklist wording to the real `-DName=Value` single-token format.
      *(Done 2026-06-12: Phase 4 §8.1 ticked with reconciled wording — single-token
      macros, DirectX `vs_6_0`/`ps_6_0` (not `vs_5_0`; DXC min is SM6), Vulkan-adds-
      `-fvk-invert-y`. One assertion added: `EntryPoint_PrecedesProfileArgument`
      (the "-E before profile" ordering was previously unasserted).)*
- [x] Confirm `DxcDiagnosticReformatterTests` covers empty/well-formed/warning/note/
      raw/multi-error; tick the Phase-4 reformatter checklist. *(Done 2026-06-12:
      Phase 4 §8.2 ticked; field shipped as `RawDiagnostics`, not `Raw` — wording noted.)*

### B. DXC + reflection integration (native-gated: SPIRV-Cross + DXC)
- [x] Verify-and-tick `DxcShaderCompilerIntegrationTests` against the Phase-4 integration
      checklist (SPIR-V/DXBC blobs, FXC-format failures, macro, cancellation).
      *(Done 2026-06-12: natives restored, class green, Phase 4 §9.2 ticked — with the
      nuance that the DirectX-target "DXBC" is SM6 DXIL in its DXBC container.)*
- [x] **New:** SD0101 mismatch test in `SpvBindingVerificationTests` (replace the TODO) —
      assert a `ShaderError` with code `"SD0101"` on a deliberately divergent
      DXIL-vs-SPIR-V binding layout (Phase 5 §7.3.3).
      *(Done 2026-06-12 in the SUPERSEDED form this doc's risk note allowed: the
      production §7.3.3 comparison never existed — `SpvReflectionVerifier` is a stub
      returning `BindingSlotMap.Empty` and `SpvcNative` exposes no
      `spvc_compiler_create_shader_resources` — and implementing it is a production
      change outside this phase. Added instead:
      `SpirvReflector_InvalidModule_ReturnsSD0101` (the SD0101 path that DOES exist in
      production) and `DivergentBindings_DxilVsSpirv_SlotMismatchIsDetectable` (negative
      control proving the `SpirvVsDxilReflectionTests` parity gate's slot comparison
      cannot pass vacuously — note both reflectors model slots as per-class register
      RANK, so the divergence is constructed by rank, not by raw register shift).
      Recorded as superseded in Phase 5 §7.3.2/§7.3.3.)*
- [x] **New:** `MgfxParameterMatchTests` golden-snapshot test — compile a reference shader,
      run reflection, compare exactly (name/class/type/rows/columns/elements) to the
      `mgfxc` golden under `tests/fixtures/golden/` (Phase 5 §9.3.1–§9.3.2).
      *(Done 2026-06-12: `Integration.Tests/Reflection/MgfxParameterMatchTests.cs`, 13
      corpus stems vs `golden/OpenGL/*.mgfx` parsed by the extended `MgfxBlobReader`.
      Exact on all value-class params; pins the only two render-proven object-class
      divergences — ShadowDusk's additive sampler params (§7.4.3) and the legacy-sampler
      `*_SDTexture` synthesis. Anything else fails. FINDING worth knowing: mgfxc's GL
      output does NOT emit sampler parameters and names the legacy-sampler texture param
      after the sampler — both verified harmless in-engine in Phases 17/28.)*
- [x] **New:** Y-flip assertion in `GlslTranspilerTests` — `passthrough_vs.fx` → SPIR-V →
      transpile, assert `gl_Position.y` is negated (Phase 6 `11-6-B`).
      *(Done 2026-06-12; mutation-checked — inverted assertion fails.)*
- [x] **New:** `FileSystemIncludeResolver` integration test — resolve a real `.fxh` from
      disk (Phase 3 §4.4); add to `Integration.Tests/Preprocessor/` (coordinate with the
      Phase 25 path-traversal test so they share one harness).
      *(Done 2026-06-12: 4 tests — sibling-dir + search-path resolution of
      `includes/TestHelper.fxh`, IncludeNotFound diagnostics, end-to-end `Flatten` of
      `MinimalWithInclude.fx`. Functional only; Phase 25 owns the `../`-escape security
      cases and can extend this class as its harness.)*
- [x] **New:** `DxcIncludeHandler` smoke test — construct with `InMemoryIncludeResolver`,
      assert `LoadSource` returns the correct blob bytes (Phase 3 §7.4).
      *(Done 2026-06-12: `HLSL.Tests/Dxc/DxcIncludeHandlerTests.cs` — placed in
      HLSL.Tests because `DxcIncludeHandler` is internal to ShadowDusk.HLSL (only
      HLSL.Tests has InternalsVisibleTo); Integration-tagged since `IDxcUtils` loads
      native DXC; macOS skips cleanly via a DXC gate. Also asserts the failure HRESULT +
      null blob for an unresolvable include.)*
- [x] Run `tools/restore.*` → rebuild → `dotnet test --filter "Category=Integration&Platform=OpenGL"`;
      tick Phase 6 `11-6-C` and run `/platform-check`.
      *(Done 2026-06-12 on win-x64: full suite 958/958 green (no skips), Phase 6 items
      0c/25/29/30 ticked, `/platform-check` over the Phase-6 surface + new tests: 0 BREAK,
      0 WARN.)*
- [ ] **Verify the global-param default-value fidelity gap (vs real mgfxc).** A **global**
      HLSL parameter with an initializer — e.g. `float FishEyeAmount = 0.35;` — compiles
      through ShadowDusk but the `.mgfx` stores its default as **`0.0`, not `0.35`** (the
      param data block is zeroed). Root cause: the global becomes a `$Globals` cbuffer
      member and **DXC drops the initializer** for cbuffer globals (HLSL treats them as
      advisory); **fxc** — which `mgfxc` uses — *does* capture it as the constant-table
      `DefaultValue`, so **mgfxc almost certainly bakes `0.35`**. If a consumer never calls
      `effect.Parameters["X"].SetValue(...)`, the uniform is `0` → any term multiplying it
      vanishes → the effect renders as an **identity transform** (looks like "no shader",
      though it compiled/loaded/applied fine). **Did NOT bite the SM3 corpus** (Phase 17 —
      its shaders use `SetValue` or *local* initializers, which DXC constant-folds
      correctly); only *global* params with defaults are affected. **Action:** compile a
      shader with a global default on a Windows box with real `mgfxc`; diff the reflected
      `DefaultValue`. **If mgfxc bakes it,** bake `$Globals` defaults into the MGFX constant
      data (source of truth = reflected cbuffer layout + the HLSL initializer parsed by the
      FX pre-parser, since DXC won't carry it). Found 2026-06-02 diagnosing a Fisheye shader
      in `ShaderFiddle.Web` (the sample's help note already warns users to inline the
      constant or `SetValue` it).

### C. CLI-process invocation parity (Phase 15)
- [ ] Add a `[Theory]` variant of `CompileFixtureTests.Compile_ProducesValidMgfxHeader`
      parameterised over both `InvocationMode` values (also exercises the built CLI binary).
- [ ] Wire `CliBinaryFixture` as the class fixture (publish/locate once per class).
- [ ] Assert exit codes, stderr format, and `.mgfx` bytes match across the two paths
      (drop-in equivalence).
- [ ] **Decide & unify:** keep one of `CliFixture` (skip-on-missing) / `CliBinaryFixture`
      (reuse-built); remove or document the other.

### D. `ShaderIRBuilder` direct unit tests (Phase 8)
- [x] Add `[assembly: InternalsVisibleTo("ShadowDusk.Compiler.Tests")]` to
      `ShadowDusk.Compiler.csproj`. *(Already present when Phase 27 ran (2026-06-12) —
      `ShadowDusk.Compiler.csproj` line 35 gained it after this doc's 2026-06-03
      verification; NO production change was needed at all for this phase.)*
- [x] `Build_ShaderIndicesAreZeroBased` — 2-pass technique; Pass 0 VS=0/PS=1,
      Pass 1 VS=2/PS=3. *(Done 2026-06-12 in `ShaderIRBuilderTests.cs` — pure direct
      test of the internal `ShaderIRBuilder.Build`; also asserts each index lands on a
      stage-correct blob in the zero-based shader list.)*
- [x] `Build_EmptyAnnotationsAllowed` — pass with no annotations; empty `AnnotationInfo`,
      no throw. *(Done 2026-06-12 — empty, never-null annotation lists round-trip.)*

### E. CLI pack / install / publish — manual (Phase 9 §9.4–9.6)
- [ ] `dotnet pack src/ShadowDusk.Cli` → confirm package with `ToolCommandName = mgfxc`.
- [ ] `dotnet tool install -g ShadowDusk.Cli --add-source ./nupkg` → run `mgfxc` with no
      args → usage on stderr, exit 1.
- [ ] `dotnet publish src/ShadowDusk.Cli -r win-x64 --self-contained` → single-file binary
      runs and bundles native DLLs. (Linux/macOS RIDs → Phase 30.)

### F. Bookkeeping
- [x] Tick the Phase 2 stripped-HLSL item once the DXC integration run is green (or add a
      named assertion). *(Done 2026-06-12: DXC integration run green; Phase 2 acceptance
      item ticked with the corpus-wide-coverage rationale.)*
- [x] For each item *not* done: record a one-line re-deferral reason in the originating
      `DONE/PHASE-X` doc and in Phase 100. *(Done for the core-coverage half, 2026-06-12 —
      Phase 100 is RETIRED (emptied 2026-06-03), so supersession/re-deferral notes live in
      the originating docs + this file only: Phase 5 §7.3.2/§7.3.3 + prerequisite line
      (SPIRV-Cross-based verifier superseded by the managed `SpirvReflector` + parity
      gate; stub `SpvReflectionVerifier` documented), Phase 3 §8.2–8.4 (closed-in-place
      per Phase 100, now ticked). CLI-parity/pack/review-input re-deferrals belong to the
      parallel CLI-half agent.)*

### Inputs from the 2026-06-12 Phase 4.1 QA + security reviews (added 2026-06-12)

The post-merge QA/security review of PRs #52–#56 (most findings fixed same-day in PR #59)
deferred these verification items here — they are exactly this phase's shape:

- [ ] **SD1902 end-to-end test** (vkd3d WASM module absent → SD1902 → the sample's
      per-target message): the path every consumer hits if the packed module ever goes
      missing; currently untested end-to-end.
- [ ] **SD1902 attribution** (`WasmVkd3dShaderCompiler` wraps `EnsureRegisteredAsync`,
      which loads all three modules — a spirv-cross load failure is mis-headlined as
      vkd3d; underlying error text is included so it fails loudly, just misleadingly).
      Deliberately deferred from PR #59 to avoid colliding with Phase 42's
      InitializeAsync design — now safe to fix.
- [ ] **Sample UI download path untested** (the G2 gate enters via `TestCompileExport`,
      bypassing `ExportAsync` → `sdDownloadBytes`; a broken blob-download wiring ships
      green). Sample-grade — decide test-or-re-defer explicitly.
- [ ] **Success-path compiler warnings are discarded on both hosts** (desktop + WASM read
      vkd3d messages then drop them when rc==0, despite `log_level=WARNING`); parity is
      preserved, but constraint 5 ("diagnostics surface verbatim") arguably wants them
      surfaced. Decide + record.
- [ ] **Shim empty-source pre-judge** (`shadowdusk-vkd3d.js` rejects empty source itself
      instead of letting vkd3d speak; unreachable through the real pipeline — cosmetic
      deviation, decide + record).
- [ ] **No source-size cap on the uncancellable in-browser compile** (self-DoS only;
      recorded as deliberate — re-confirm and document, or cap).

---

## Acceptance Criteria

- [ ] Every Phase-100 *"file exists — verify coverage"* item (Phase 2; Phase 4 unit +
      integration; Phase 6 `11-6-C`) is **either** ticked after a green run **or** has a
      recorded re-deferral reason.
- [ ] The genuinely-missing tests (SD0101 mismatch, `MgfxParameterMatch` golden, Y-flip,
      `FileSystemIncludeResolver` integration, `DxcIncludeHandler` smoke, CLI-process
      `[Theory]`, the two `ShaderIRBuilder` direct tests) are **implemented and passing**,
      or explicitly re-deferred with a reason.
- [ ] `InternalsVisibleTo("ShadowDusk.Compiler.Tests")` is present and the direct
      `ShaderIRBuilder` tests compile.
- [ ] The full suite is green (baseline 515/515) with the new tests added; native-gated
      tests pass with `tools/restore.*` run, and skip cleanly (not fail) without it.
- [ ] The CLI `pack`/`install`/`publish` manual steps (§9.4–9.6) are run and recorded; the
      `mgfxc` tool installs and shows usage/exit-1 on no args.
- [ ] The `CliFixture` vs `CliBinaryFixture` duplication is resolved (one kept, decision
      recorded).
- [ ] No coverage gap that matters for the drop-in-`mgfxc` promise (diagnostics format,
      reflection parameter match, CLI↔in-process parity) is left silently open.

## Definition of Done

The deferred test/verification items from Phases 2–9 and 15 are each **closed** — run and
ticked, newly implemented and passing, or explicitly re-deferred with a recorded reason —
so that entering 1.0 there is no parked-but-unknown coverage gap in the areas that protect
the drop-in-`mgfxc` contract (DXC flags/diagnostics, reflection parameter fidelity, GLSL
transpile, include resolution, IR construction, and CLI↔in-process equivalence). The
manual CLI pack/install/publish path is verified once on Windows; the cross-platform *run*
of the whole suite is handed to [Phase 30](DONE/PHASE-30-ci-and-nuget-release.md).

---

## Open questions / risks

- **Native-library gating.** Sections B and parts of C/E need SPIRV-Cross (and DXC)
  present. Locally these depend on `tools/restore.*`; in CI they are Phase 30's job. New
  native-gated tests must **skip cleanly** when the library is absent, never hard-fail, or
  they'll redden a fresh-clone build. (`CLAUDE.md` also flags AV-scan slowness on cold
  native binaries — keep the per-test `CancellationToken` timeouts and the
  `ShadowDusk.runsettings` 5-min session cap.)
- **SD0101 needs a genuinely-mismatched fixture.** The current shaders bind consistently
  across DXIL/SPIR-V; the negative test must *construct* a divergence (or assert the
  verifier's behavior on a hand-built mismatch) without becoming brittle to SPIRV-Cross
  version changes.
- **`MgfxParameterMatch` exactness vs `mgfxc` quirks.** The snapshot must compare
  *behaviorally significant* reflection fields, not byte-for-byte `mgfxc` output
  (byte-equality with `mgfxc` is never a goal — `CLAUDE.md`). Pin which fields are exact.
- **Overlap with [Phase 25](PHASE-25-security-hardening.md).** Both touch
  `FileSystemIncludeResolver` tests. Whichever lands first should own a shared harness so
  the path-traversal (Finding 1) and the §4.4 resolve-from-disk tests don't collide.
- **Re-deferral discipline.** "Explicitly re-defer with a reason" must be enforced — the
  failure mode is quietly leaving items unchecked again, which defeats the purpose of
  emptying Phase 100.
