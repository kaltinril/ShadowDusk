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
- [ ] Confirm `DxcFlagBuilderTests` covers the Phase-4 flag matrix; reconcile the
      `-DName=Value` checklist wording to the real `-DName=Value` single-token format.
- [ ] Confirm `DxcDiagnosticReformatterTests` covers empty/well-formed/warning/note/
      raw/multi-error; tick the Phase-4 reformatter checklist.

### B. DXC + reflection integration (native-gated: SPIRV-Cross + DXC)
- [ ] Verify-and-tick `DxcShaderCompilerIntegrationTests` against the Phase-4 integration
      checklist (SPIR-V/DXBC blobs, FXC-format failures, macro, cancellation).
- [ ] **New:** SD0101 mismatch test in `SpvBindingVerificationTests` (replace the TODO) —
      assert a `ShaderError` with code `"SD0101"` on a deliberately divergent
      DXIL-vs-SPIR-V binding layout (Phase 5 §7.3.3).
- [ ] **New:** `MgfxParameterMatchTests` golden-snapshot test — compile a reference shader,
      run reflection, compare exactly (name/class/type/rows/columns/elements) to the
      `mgfxc` golden under `tests/fixtures/golden/` (Phase 5 §9.3.1–§9.3.2).
- [ ] **New:** Y-flip assertion in `GlslTranspilerTests` — `passthrough_vs.fx` → SPIR-V →
      transpile, assert `gl_Position.y` is negated (Phase 6 `11-6-B`).
- [ ] **New:** `FileSystemIncludeResolver` integration test — resolve a real `.fxh` from
      disk (Phase 3 §4.4); add to `Integration.Tests/Preprocessor/` (coordinate with the
      Phase 25 path-traversal test so they share one harness).
- [ ] **New:** `DxcIncludeHandler` smoke test — construct with `InMemoryIncludeResolver`,
      assert `LoadSource` returns the correct blob bytes (Phase 3 §7.4).
- [ ] Run `tools/restore.*` → rebuild → `dotnet test --filter "Category=Integration&Platform=OpenGL"`;
      tick Phase 6 `11-6-C` and run `/platform-check`.
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

### C. CLI-process invocation parity (Phase 15) — DONE 2026-06-12
- [x] Add a `[Theory]` variant of `CompileFixtureTests.Compile_ProducesValidMgfxHeader`
      parameterised over both `InvocationMode` values (also exercises the built CLI binary).
      *(9 fixtures × 3 platforms × 2 modes; the `CliProcess`+`DirectX_11` cells are
      Windows-only because the CLI uses the library's default DirectX backend — the
      d3dcompiler_47 oracle, SD0210 off-Windows — while the DirectPipeline helper opts
      into vkd3d there; see `CellRunsHere`.)*
- [x] Wire `CliBinaryFixture` as the class fixture (publish/locate once per class).
      *(Plus a per-(fixture, profile, mode) compile memo so the two theories share
      results instead of tripling the process-spawn count — Phase 21 discipline.)*
- [x] Assert exit codes, stderr format, and `.mgfx` bytes match across the two paths
      (drop-in equivalence). *(`CliProcess_And_DirectPipeline_ProduceByteIdenticalMgfx`:
      exit 0 both, CLI stderr empty, output bytes `.Should().Equal(...)` — asserted, not
      assumed; 27 pairs green on Windows 2026-06-12.)*
- [x] **Decide & unify:** keep one of `CliFixture` (skip-on-missing) / `CliBinaryFixture`
      (reuse-built); remove or document the other. **Decision: kept `CliBinaryFixture`,
      deleted `Fixtures/CliFixture.cs`.** Rationale: `CliBinaryFixture` is the Phase-21
      performance-blessed one (reuses the build output that the test project's
      `ReferenceOutputAssembly=false` ProjectReference guarantees exists, publish only as
      fallback) and was the only one any test actually used; `CliFixture` was dead code,
      and its skip-on-missing behavior is the wrong default now that the build guarantees
      the binary — a missing CLI is a build regression that must fail loudly, not skip.

### D. `ShaderIRBuilder` direct unit tests (Phase 8)
- [ ] Add `[assembly: InternalsVisibleTo("ShadowDusk.Compiler.Tests")]` to
      `ShadowDusk.Compiler.csproj`.
- [ ] `Build_ShaderIndicesAreZeroBased` — 2-pass technique; Pass 0 VS=0/PS=1,
      Pass 1 VS=2/PS=3.
- [ ] `Build_EmptyAnnotationsAllowed` — pass with no annotations; empty `AnnotationInfo`,
      no throw.

### E. CLI pack / install / publish — scripted + run (Phase 9 §9.4–9.6) — DONE 2026-06-12

Scripted as **`tools/verify-cli-packaging.ps1`** so "manual" never means "unrepeatable":
it packs, installs into a scratch `--tool-path` (**never `-g`** — no machine pollution;
the install is hermetic via a scratch `nuget.config` because machine-level
`packageSourceMapping` rejects `--add-source`), publishes win-x64 self-contained
single-file with release.yml's exact flag set, and runs each binary through the no-args
usage check + a real `Minimal.fx /Profile:OpenGL` compile, SHA-256-compared against the
normal built CLI. Results (Windows, 2026-06-12, v0.4.0):

- [x] `dotnet pack src/ShadowDusk.Cli` → `ShadowDusk.Cli.0.4.0.nupkg` (61.77 MB);
      `DotnetToolSettings.xml` command name = **`ShadowDuskCLI`**. *(Phase 9 wrote
      "`ToolCommandName = mgfxc`" — superseded by the CLI re-brand: the tool ships under
      its own name, `mgfxc` compatibility is flags/format/exit-code behavior, not the
      command name.)*
- [x] `dotnet tool install ShadowDusk.Cli --tool-path <scratch>` → installs; no-args →
      usage on stderr + exit 1; `Minimal.fx /Profile:OpenGL` → exit 0, 571 bytes,
      `sha256 28fc06e2…99` — **byte-identical** to the built CLI's output.
- [x] `dotnet publish -r win-x64 --self-contained /p:PublishSingleFile=true
      /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true`
      → `ShadowDuskCLI.exe` 45.4 MB (12 files in the publish dir — apphost + the
      non-embedded native companions, the same shape release.yml archives per RID); runs
      the usage check and the fixture compile with **byte-identical** output.
      (Linux/macOS RIDs → Phase 30, unchanged.)

### F. Bookkeeping
- [ ] Tick the Phase 2 stripped-HLSL item once the DXC integration run is green (or add a
      named assertion).
- [ ] For each item *not* done: record a one-line re-deferral reason in the originating
      `DONE/PHASE-X` doc and in Phase 100.

### Inputs from the 2026-06-12 Phase 4.1 QA + security reviews (added 2026-06-12)

The post-merge QA/security review of PRs #52–#56 (most findings fixed same-day in PR #59)
deferred these verification items here — they are exactly this phase's shape:

- [x] **SD1902 end-to-end test** — DONE 2026-06-12, at the two honest levels:
      *node gate* (`node-test-vkd3d-wasm.mjs` case 0, runs even without the restored
      module): the product shim imported with no `./vkd3d/` module → `ensureReady()`
      REJECTS (the exact rejection .NET maps to SD1902) and a stray `compile()` after the
      failed load throws the sticky "failed to initialize" error; *real-browser gate*
      (`browser-vkd3d-gate.mjs` Phase 27 scenario 1, fresh session with
      `vkd3d/vkd3d-shader.{js,wasm}` route-aborted): cold sync `Compile()` → **SD1903**
      (post-Phase-42 behavior) and async `CompileAsync` → **SD1902 with the helpful
      restore pointer** (`RESTORE.md` / `tools/restore`) — the path every consumer hits
      if the packed module ever goes missing. (Unit-level was NOT possible honestly:
      `WasmVkd3dShaderCompiler` is `[SupportedOSPlatform("browser")]` with `[JSImport]`
      seams that do not exist off-browser.)
- [x] **SD1902 attribution** — FIXED 2026-06-12. Registration is now **per compile
      path**: `WasmModuleRegistration.EnsureVkd3dRegisteredAsync` registers ONLY
      `shadowdusk-vkd3d` (the DirectX/FNA path makes no DXC/SPIRV-Cross `[JSImport]`
      calls), `EnsureDxcChainRegisteredAsync` registers `shadowdusk-dxc` +
      `shadowdusk-spirv-cross` (preserving the eager-SPIRV-Cross-instantiation ordering),
      and every individual import failure is re-wrapped to NAME the failing module + its
      asset URL. So a SPIRV-Cross load failure can no longer surface under the vkd3d
      SD1902 headline (or vice versa). Phase 42 `InitializeAsync` semantics unchanged —
      it still warms everything via both `Ensure*ReadyAsync` gates. Proven in the browser
      gate's Phase 27 scenario 2: with the DXC + SPIRV-Cross assets route-aborted, a
      DirectX export compile SUCCEEDS, SHA-256 == the committed manifest.
- [x] **Sample UI download path** — **decision: re-defer (recorded).** Everything beneath
      the export button (`WasmShaderCompiler.CompileAsync` for DirectX/FNA) is already
      gate-proven byte-identical via `TestCompileExport`; the untested remainder is the
      sample's `ExportAsync` status-line handling plus the ~10-line `sdDownloadBytes` JS
      blob helper. A Playwright click-through with a browser-download listener would add
      a flaky download dependency to the gate for sample-only plumbing — disproportionate
      (the sample is never the product, CLAUDE.md). Revisit only if the export station is
      ever promoted beyond sample status.
- [x] **Success-path compiler warnings** — **decision: keep discarding on rc==0
      (recorded), revisit post-1.0 as an additive API if asked for.** Reasons: (1) the
      CLI's mgfxc contract — a successful compile is SILENT on stdout+stderr — is
      asserted by `CliIntegrationTest` and now by the CLI↔pipeline parity theory;
      printing rc==0 warnings to stderr would break it and risk MGCB mis-parsing noise
      as failure. (2) Host parity is currently exact (desktop and WASM drop them
      identically); surfacing requires a `CompiledShader` diagnostics channel plumbed
      through both hosts — additive, not a pre-1.0 must. (3) vkd3d's non-fatal stream
      carries version-dependent internal noise (e.g. `fixme:preproc_yyparse #line
      directive`) — coupling consumer-visible output to it would churn. Constraint 5 is
      fully honored where it bites: FAILURE diagnostics surface verbatim
      (`Vkd3dCompileContract.MapCompileFailure`, both hosts).
- [x] **Shim empty-source pre-judge** — **decision: fix (done 2026-06-12).** The
      pre-judge (`throw 'compile: empty HLSL source.'`) was removed; the shim now mirrors
      the desktop backend exactly — empty source goes to vkd3d as pointer + length 0
      (allocating `max(1, len)` like the desktop's `AllocHGlobal(1)` pattern, since
      `_malloc(0)` may legally return null) and vkd3d speaks for itself. Node-gate case
      added: empty source through the shim → vkd3d's verbatim
      `empty.fx: E5005: Entry point "main" is not defined.` — host parity, no shim
      editorializing.
- [x] **Source-size cap** — **decision: re-confirmed deliberate NO cap (recorded).** The
      desktop backend has no cap either, so a browser-only cap would be a host-parity
      deviation (a shader legal on desktop failing in the browser — the bad kind of
      ShadowDusk-specific behavior). The uncancellable in-browser compile can only hurt
      the user's own tab (self-DoS, no server involved), and the sample's upload entry
      point already caps file reads at 2 MB at the UI edge. No product-side cap.

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
- [x] The CLI `pack`/`install`/`publish` manual steps (§9.4–9.6) are run and recorded; the
      tool (command `ShadowDuskCLI` — the re-brand superseded the doc's `mgfxc` name)
      installs and shows usage/exit-1 on no args. *(Scripted:
      `tools/verify-cli-packaging.ps1`; see Task E.)*
- [x] The `CliFixture` vs `CliBinaryFixture` duplication is resolved (one kept, decision
      recorded). *(Kept `CliBinaryFixture`; see Task C.)*
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
