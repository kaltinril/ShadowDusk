# Phase 47 — ShaderToy Frontend Promotion (experiment → product library)

**Track:** Reach / additive frontend. Promotes the **Phase 46** ShaderToy→FX converter from an
out-of-band experiment (`tools/shadertoy2fx/`) into a first-class, in-solution **`ShadowDusk.*`
product library** — pure-managed, zero native dependency, strictly additive. The MAIN plan doc; it
owns the **overall architecture** and the **library-migration** plan, and links to two sibling
appendix docs that own the CLI-input and sample-migration slices.

**Status:** IMPLEMENTED (2026-06-20). The converter library + its 380-test suite were promoted to
`src/ShadowDusk.ShaderToy/` + `tests/ShadowDusk.ShaderToy.Tests/` (git mv, history preserved) and added
to `ShadowDusk.slnx`; `dotnet build/test ShadowDusk.slnx` is green (ShaderToy 380, full suite passing, 0
warnings). The out-of-band PoC CLI / render-proof / sample were repointed at the new `src/` path and
still build; `shadertoy2fx.slnx` references the promoted library in place. The real `ShadowDuskCLI` now
accepts ShaderToy/GLSL input (CLI appendix, implemented). A standing `NoMonoGameInProductLibrariesTests`
guard asserts no `src/*.csproj` depends on MonoGame. **Still owner-deferred:** NuGet publication of
`ShadowDusk.ShaderToy` (kept `IsPackable=false`), the sample/runtime migration to `samples/`
(sample-migration appendix), and the Windows DX/FNA + fidelity render gates (must be run on a Windows GPU
box before this merges — CI cannot run them).

**Depends on:**
- [Phase 46 — ShaderToy → FX Conversion Tool](DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md): the
  converter being promoted. Phase 46 is render-proven (fidelity gate **46/46 @ mean 0.00/255** vs
  the original GLSL; **371 tests green**; gallery 72/72) and was deliberately shaped for this lift —
  a clean `ShadowDusk.ShaderToy` library, one public `ShaderToyConverter.Convert(glsl)` entry, the
  `Multipass/` batch API, **no product coupling, no native dependency.** Promotion is a wire-in, not
  a rewrite (Phase 46 SESSION HANDOFF, owner-clarified direction 2026-06-20).
- [Phase 8 — Compiler Library](DONE/PHASE-8-compiler-library.md): the downstream `EffectCompiler :
  IShaderCompiler` the converter's `.fx` output feeds into. **Unchanged by this phase** — the
  converter remains upstream of the pipeline, emitting `.fx` *text*; the pipeline is not touched.

**Blocks / enables:**
- The CLI `.glsl`-input integration (the owner's stated end-goal: `ShadowDuskCLI`/`mgfxc` accepting a
  ShaderToy `.glsl` and routing it through the library) — planned in
  **[plan/PHASE-47-appendix/cli-shadertoy-input.md](PHASE-47-appendix/cli-shadertoy-input.md)**.
- The sample / runtime-helper migration (`ShadowDusk.ShaderToy.Runtime` + the interactive sample move
  to `samples/`, keeping a MonoGame dependency OUT of the shipped libraries) — planned in
  **[plan/PHASE-47-appendix/sample-migration.md](PHASE-47-appendix/sample-migration.md)**.

**Open converter-subset gap (one ACTIONABLE fix waiting to be picked up):** real-world ShaderToy
triage recorded in
**[plan/PHASE-47-appendix/converter-subset-gaps.md](PHASE-47-appendix/converter-subset-gaps.md)** —
**initializer-sized arrays** (`const float a[] = float[](0.9, 0.25);`, size statically knowable) are
currently rejected as "runtime-sized" but could be faithfully supported by inferring N from the
initializer (`Parser.cs` → `ParseArraySuffix`). The other three triaged shaders are correctly
out-of-subset (uint bit-hash, cubemap channel) or source UB (unsequenced side effect) and stay
rejected.

> This doc is the parent. The appendices are written concurrently by sibling agents; this doc
> references them by path and does not duplicate their content.

---

## Overview

Phase 46 proved the bet: a single-pass **ShaderToy / general-GLSL image shader** can be faithfully
transpiled to a self-contained HLSL **`.fx`**, which the **existing, unchanged** ShadowDusk pipeline
then compiles to every XNA-family backend (MonoGame GL/DX, KNI, FNA). The converter is pure managed
C# (preprocessor → lexer → parser → AST → type-inference → HLSL emitter → harness generator), builds
0-warning under the repo's `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`, and carries **no
native dependency at all** — the decisive property that made option 3 (managed transpiler) the chosen
shape over glslang+SPIRV-Cross (Phase 46 *Why this shape*).

It currently lives entirely under `tools/shadertoy2fx/`, in its **own** `shadertoy2fx.slnx`, **not**
in `ShadowDusk.slnx`, **not** run by `dotnet test ShadowDusk.slnx`, and **not** packed — exactly the
out-of-band posture the experiment demanded so it could never risk the `mgfxc`-equivalence promise.

Phase 47 promotes the **converter core + its tests** into the real product solution as a normal
`ShadowDusk.*` library, so it is built, tested, and maintained alongside the rest of the product —
while preserving every safety property that made the experiment safe:

- **It stays pure-managed with zero native dependency** ⇒ it is a clean additive library that
  *cannot* change any existing pipeline byte, output format, or native-packaging story.
- **It stays upstream of the pipeline** ⇒ it emits `.fx` text; `EffectCompiler`/`CompilationPipeline`
  are untouched, so the `mgfxc`-equivalence promise for `.fx`→`.mgfx` is structurally unaffected.
- **The MonoGame-dependent runtime helper + interactive sample do NOT become product libraries** —
  they move to `samples/` (appendix), so the shipped `ShadowDusk.*` libraries never gain a MonoGame
  dependency.
- **The out-of-band render/fidelity drivers** (`render-proof/`) stay out of `ShadowDusk.slnx`, exactly
  like `validation/*`.

What changes for the consumer: **nothing they rely on.** Existing `.fx` handling, the MGFX v10 output
format, and the MonoGame 3.8.2.1105 pin are all untouched. This is purely an additive frontend — the
"additive and seamless" / "the library is the product" directives in `CLAUDE.md` are honored: a new
input shape (ShaderToy GLSL) that produces the same one-true `.fx`→backend pipeline, never a flag the
consumer must flip to get correct output.

---

## Scope & Non-Goals

**In scope:**

- **Move the converter core to a product library** `src/ShadowDusk.ShaderToy/` (from
  `tools/shadertoy2fx/src/ShadowDusk.ShaderToy/`): the public `ShaderToyConverter.Convert(glsl) →
  ConvertResult` entry plus the `Multipass/` batch APIs (`ShaderToyProject`, `MultipassConverter`,
  `MultipassResult`, `MultipassManifest`). Pure-managed, `#nullable enable`, `sealed` where
  applicable, inheriting `Directory.Build.props` (warnings-as-errors, code-style). Add it to
  `ShadowDusk.slnx`.
- **Move the tests to `tests/ShadowDusk.ShaderToy.Tests/`** (from `tools/shadertoy2fx/tests/...`),
  add to `ShadowDusk.slnx`, and have them run under `dotnet test ShadowDusk.slnx` — the **371**
  unit/trap/golden/reject tests + the corpus + the goldens come along. (Phase 46 SESSION HANDOFF cites
  371; the "380" figure includes the multipass + integration theories — the live count is whatever the
  moved suite reports green, and the acceptance criterion is "same count green, 0 warnings".)
- **Resolve the deferred-packaging decision explicitly**: the library becomes a `ShadowDusk.*` library
  *in solution and tested now*, **packable later** — i.e. keep it OUT of the published NuGet set for
  now via an explicit, documented toggle, with the `IsPackable`/`PackageId` decision recorded so
  "publish it" is a one-line flip later (see *Architecture* → *Deferred packaging*).
- **Document the fidelity bar honestly** as a *distinct* evidence axis: the ShaderToy frontend's bar is
  **pixel-fidelity vs the original GLSL** (Phase 46 `render-proof --fidelity`, 46/46 @ 0.00), NOT
  `mgfxc`/`fxc`-equivalence — there is **no `mgfxc` oracle for ShaderToy input**.
- **Handle the validation-driver coupling** created once `ShadowDusk.ShaderToy` is itself a product
  lib: the `render-proof/` driver (and the sample) reference the converter and the *built CLI* — keep
  those drivers **out-of-band** (not in `ShadowDusk.slnx`), repointed at the new `src/` path.
- **Prove additivity**: a byte-diff of the existing `.fx`→`.mgfx`/`.fxb` golden corpus is **unchanged**
  before/after; **no MonoGame dependency** enters any shipped `ShadowDusk.*` library; the fidelity gate
  is still **46/46**.

**Out of scope / Non-Goals (owned elsewhere or deferred):**

- **CLI `.glsl` input wiring** — owned by
  [PHASE-47-appendix/cli-shadertoy-input.md](PHASE-47-appendix/cli-shadertoy-input.md). This doc only
  guarantees the library API that appendix consumes.
- **Sample + runtime-helper migration to `samples/`** — owned by
  [PHASE-47-appendix/sample-migration.md](PHASE-47-appendix/sample-migration.md). This doc only states
  the constraint they must honor ("no MonoGame dep in shipped libs") and that `render-proof/` stays
  out-of-band.
- **Publishing `ShadowDusk.ShaderToy` to NuGet** — deferred by the owner; planned-for, not done here.
- **Wiring the converter INTO `CompilationPipeline`/`EffectCompiler`** — explicitly rejected (Phase 46
  *Separate-tool discipline*). The converter is a frontend that emits `.fx`; it is never a frontend of
  the faithful pipeline. (The CLI appendix routes `.glsl` → converter → existing `.fx` path, NOT into
  the pipeline core.)
- **New converter coverage / new GLSL subset features** — Phase 47 is a *promotion*, not a feature
  phase. The ~61% single-pass ceiling, host-uniform passthrough, and the multipass orchestrator stay
  as Phase 46 left them (open items tracked there). Promotion must not change emitted `.fx` for any
  existing corpus shader (asserted by the moved goldens).
- **Any change to existing GL/DX/FNA output, the MGFX v10 format, or the MonoGame 3.8.2 pin.**

---

## Architecture & key decisions

### A. Target layout (locked)

```
src/ShadowDusk.ShaderToy/            # MOVED from tools/shadertoy2fx/src/ShadowDusk.ShaderToy/
    ShaderToyConverter.cs  (public Convert(glsl) -> ConvertResult)
    Multipass/             (ShaderToyProject, MultipassConverter, MultipassResult, MultipassManifest)
    <lexer/parser/AST/type-inference/HLSL emitter/harness generator>   # pure managed, ZERO native dep
tests/ShadowDusk.ShaderToy.Tests/    # MOVED from tools/shadertoy2fx/tests/ShadowDusk.ShaderToy.Tests/
    corpus/  (authored + reject + multipass JSON)   # CopyToOutputDirectory, comes along
    golden/  (per-fixture emitted .fx + manifest.json goldens)
    *Tests.cs  (trap tests, preprocessor, entry-mode, multipass, reject corpus, golden regression)
```

Both join `ShadowDusk.slnx` (`/src/` and `/tests/` folders). **Namespaces and `AssemblyName` are
unchanged** — they are already `ShadowDusk.ShaderToy` / `ShadowDusk.ShaderToy.Tests` (verified in the
existing csprojs), so **no namespace rename, no `using` churn, no `InternalsVisibleTo` change** is
needed; only the on-disk path and the `.csproj` `ProjectReference`/relative paths move.

**Stays in `tools/shadertoy2fx/` (out-of-band, NOT in `ShadowDusk.slnx`):**

- `src/ShadowDusk.ShaderToy.Cli/` (`shadertoy2fx` CLI) — superseded by the CLI appendix's
  `ShadowDuskCLI` `.glsl`-input route. The owner's end-goal is the *existing* CLI accepting `.glsl`;
  the standalone `shadertoy2fx` CLI becomes redundant. Decision deferred to the CLI appendix (keep as a
  thin out-of-band debug CLI, or retire once the main CLI accepts `.glsl`).
- `src/ShadowDusk.ShaderToy.Runtime/` (MonoGame helper) and `sample/` — **move to `samples/`** per the
  sample-migration appendix (they carry a `MonoGame.Framework.DesktopGL` dep that must NEVER enter the
  shipped libraries).
- `render-proof/` (fidelity + gallery + multipass render drivers) — **stay out-of-band**, like
  `validation/*`. They reference the converter `src/` lib + the **built CLI binary** (the driver
  *shells out* to `dotnet <ShadowDusk.Cli.dll>` to compile `.fx`→`.mgfx`; it does **not** carry a
  `ProjectReference` to `ShadowDusk.Compiler`).

### B. No native dependency enters the product (the key safety property — call it out)

The converter is pure managed C# end to end (no DXC, no SPIRV-Cross, no vkd3d, no RID matrix, no WASM
build). Promoting it therefore **cannot** change any native-packaging story and **cannot** alter any
existing pipeline output: its only product surface is a new managed assembly upstream of the pipeline
that produces `.fx` text. This is the same isolation argument that made the Phase 46 experiment safe,
preserved verbatim. A `/platform-check` of the promoted library must confirm zero new platform-specific
assumptions (it is build/CLI-time managed code, already cross-platform).

### C. The "circular-ish" validation coupling — how it is handled

The concern: once `ShadowDusk.ShaderToy` is a product lib, do the out-of-band drivers create a
reference cycle back into the product? **No — and the existing wiring already avoids it:**

- `render-proof/` converts `.glsl`→`.fx` by calling the `ShadowDusk.ShaderToy` library **directly**,
  then compiles `.fx`→`.mgfx` by **shelling out to the built `ShadowDusk.Cli` binary**
  (`Process.Start("dotnet", cliDll, fx, mgfx)` — confirmed in `render-proof/Program.cs` /
  `GalleryRunner.cs`). There is **no compile-time `ProjectReference` from render-proof to
  `ShadowDusk.Compiler`** — the dependency is a *runtime* invocation of an already-built artifact, the
  same loose coupling `validation/*` drivers use. Keeping render-proof out-of-band preserves this.
- The **sample** (`sample/`) *does* `ProjectReference` `ShadowDusk.Compiler` (for in-memory
  `EffectCompiler.Compile`). That is fine and not circular: `Compiler` does **not** reference
  `ShadowDusk.ShaderToy` (the converter is upstream, the compiler is downstream — a clean DAG). The
  sample sits at the bottom consuming both. This stays in `samples/` (appendix), out of the product
  graph.

So the dependency direction is strictly: `ShaderToy (frontend) → emits .fx → Compiler (pipeline)`.
No product project references back up into the converter; the drivers/sample are leaf consumers and
stay out-of-band. **No cycle is introduced.**

### D. Deferred packaging decision (explicit)

Owner has **deferred NuGet publication** of the ShaderToy frontend. Decision for this phase:

- The new `src/ShadowDusk.ShaderToy/` **keeps `IsPackable=false` for now** (carry the property forward
  from the experiment), but the *reason comment* changes from "standalone experiment, not in
  `ShadowDusk.slnx`" to "promoted to a product library + tested in-solution; **publishing deferred by
  owner** — flip `IsPackable` to `true` and add it to the release set to ship it."
- **Do NOT** add it to `ShadowDusk.Compiler`'s `ProjectReference` set, and **do NOT** add a
  `PackageId`/`Description`/release-set entry yet — that is the later toggle. Adding it to the published
  six-package set (`Directory.Build.props` `<Version>` already flows to all packed projects; the
  release-set membership is what gates publication) is a **deliberate future flip**, recorded here so it
  is a one-line change, not a redesign.
- **Single version source of truth is untouched**: do NOT add a `<PackageVersion>`/`<Version>` property
  to the new csproj (CLAUDE.md release footgun). When it is eventually published, it inherits
  `Directory.Build.props` `<Version>` like every other `ShadowDusk.*` package.

This keeps the seamless directive intact (no consumer-visible new package, no flag) while making the
library a real, tested, in-solution citizen now.

### E. Fidelity-bar documentation (a distinct, honest evidence axis)

The product's headline bar is **`mgfxc`/`fxc`-equivalence** for `.fx`→`.mgfx`/`.fxb` (the faithful
pipeline). **The ShaderToy frontend has NO such oracle** — a ShaderToy `.glsl` is not an `mgfxc` input,
so there is nothing to be byte/render-equivalent *to* on the input side. Its honest, distinct bar is:

> **Pixel-fidelity vs the original GLSL.** Render the ORIGINAL ShaderToy GLSL directly in a raw GL
> context (ground truth), render OUR converted `.fx`→`.mgfx` through MonoGame at the same uniforms, and
> diff per pixel. Phase 46's `render-proof --fidelity` gate: **46/46 deterministic shaders match the
> original at mean 0.00/255** (pixel-identical), including every matrix/precision trap and the 4 complex
> shaders.

This must be documented (validation-matrix + the phase doc) as a **separate rung from the `mgfxc`
bar**, so the two are never conflated. Crucially: the **downstream** half — once the converter has
emitted `.fx`, the `.fx`→`.mgfx` step is the SAME faithful, `mgfxc`-equivalent pipeline, still proven by
the existing corpus. Promotion changes neither bar. The fidelity driver stays out-of-band (like
`validation/*`), and the Windows render gate is not affected (it gates the `mgfxc`/`fxc` corpora, not
ShaderToy input).

### F. Test-suite integration (Phase 21 performance note)

The moved `ShadowDusk.ShaderToy.Tests` is a **pure managed** unit/golden suite (no child process, no
native binary) — it does NOT carry the heavyweight-machinery cost that made
`ShadowDusk.Integration.Tests` the slow outlier (Phase 21). The corpus + goldens are copied next to the
test assembly via `CopyToOutputDirectory` (already configured). Adding it to `ShadowDusk.slnx` adds a
fast, self-contained project. **Watch item:** the Phase 46 multipass tests include an *Integration
theory* that compiles each emitted per-pass `.fx` "on OpenGL via the real ShadowDusk CLI" — that one
exercises the CLI child-process path. Decide during the move whether to (a) keep it as a
`[Trait("Category","Integration")]`-tagged theory in the moved suite, or (b) relocate that single
cross-compile assertion to `ShadowDusk.Integration.Tests` so the new project stays pure (preferred, to
keep `dotnet test ShadowDusk.slnx` fast and the new suite child-process-free). Either way the suite must
stay green under the repo's `--settings ShadowDusk.runsettings` backstop.

### G. Seamless / additive / backwards-compatible (the directive check)

- **Zero change** to existing `.fx` handling, the MGFX v10 output format
  (`CompilerOptions.MgfxVersion` default 10), or the MonoGame 3.8.2.1105 pin. Promotion adds a managed
  assembly; it does not touch `Core`/`HLSL`/`GLSL`/`Compiler`.
- **No consumer-visible flag/version/opt-in** to get correct output (CLAUDE.md "seamless" directive).
  A consumer who wants ShaderToy input gets it through the CLI route (appendix); a consumer who does not
  is wholly unaffected and sees no new package (publishing deferred).
- **Additive frontend** = a new *input* shape that funnels into the existing one-true `.fx`→backend
  pipeline — the good kind of additive (CLAUDE.md), never a change to existing output a current consumer
  relies on.

---

## Tasks (sequenced)

> Each step keeps both solutions buildable; the move is reversible at every point (git mv + csproj path
> edits). Commit after the library move (B) and again after the test move (C) so a background-agent
> rollback never loses work (CLAUDE.md: commit first, clean up last).

1. **Baseline capture (additivity oracle).** Before any move: record/confirm the existing
   `.fx`→`.mgfx`/`.fxb` golden corpus state (the `byte-identity` manifest + `tests/fixtures/golden/`)
   so a post-move byte-diff can prove "existing output unchanged". Confirm `dotnet test
   ShadowDusk.slnx` green and Phase-46 `dotnet test tools/shadertoy2fx/shadertoy2fx.slnx` green +
   fidelity 46/46 (the before snapshot).
2. **Move the converter library** `tools/shadertoy2fx/src/ShadowDusk.ShaderToy/` →
   `src/ShadowDusk.ShaderToy/` (`git mv`). Keep `AssemblyName`/`RootNamespace`/`InternalsVisibleTo`.
   Update the `IsPackable=false` comment to the *deferred-publishing* rationale (Decision D). No
   `ProjectReference` changes needed (it has none — pure managed).
3. **Move the tests** `tools/shadertoy2fx/tests/ShadowDusk.ShaderToy.Tests/` →
   `tests/ShadowDusk.ShaderToy.Tests/` (`git mv`). Fix the `ProjectReference` relative path
   (`..\..\src\ShadowDusk.ShaderToy\...`). Keep the `corpus/**` `CopyToOutputDirectory` item and the
   golden set. Decide the multipass Integration-theory placement (Decision F).
4. **Add both projects to `ShadowDusk.slnx`** — `src/ShadowDusk.ShaderToy/ShadowDusk.ShaderToy.csproj`
   under `/src/`, `tests/ShadowDusk.ShaderToy.Tests/...` under `/tests/`.
5. **Repoint the out-of-band drivers/sample** that referenced the old `src/` path:
   `render-proof/*.csproj`, the standalone `shadertoy2fx` CLI csproj (if retained), and the
   sample/Runtime csprojs — update their `ProjectReference` paths to `..\..\..\src\ShadowDusk.ShaderToy\`
   (exact relative depth depends on the sample/Runtime final home — coordinate with the
   **sample-migration appendix**). These stay OUT of `ShadowDusk.slnx`.
6. **Update `tools/shadertoy2fx/shadertoy2fx.slnx`** (the out-of-band solution) so it still builds the
   remaining out-of-band projects (render-proof + whatever CLI/sample remain there until the appendices
   relocate them). It no longer "owns" the moved library/tests — it references them in place under
   `src/`/`tests/` OR the appendices fold the out-of-band drivers into `validation/`-style standalone
   builds. (Final disposition of `shadertoy2fx.slnx` is shared with the two appendices; this doc keeps
   it building the converter via the new path.)
7. **Build + test the product solution.** `dotnet build ShadowDusk.slnx` and `dotnet test
   ShadowDusk.slnx` green **including** the new library + test project, **0 warnings** (warnings-as-
   errors inherited). Same green test count as the moved suite reported pre-move.
8. **Additivity proof (byte-diff).** Re-run the existing `.fx`→`.mgfx`/`.fxb` golden/byte-identity
   verification; assert the existing-corpus output is **byte-identical** to the step-1 baseline (the
   converter promotion touched nothing downstream).
9. **No-MonoGame-dep audit.** Verify no shipped `ShadowDusk.*` library (`Core/HLSL/GLSL/Compiler/Cli/
   Wasm` + the new `ShaderToy`) has any `MonoGame.*` `PackageReference` (the Runtime helper + sample
   carry it and live in `samples/`, per the appendix). A grep/`dotnet list package` check is the gate.
10. **Fidelity gate re-run (out-of-band).** Run `render-proof --fidelity` against the new layout;
    confirm **46/46 @ 0.00** still holds (the converter bytes are unchanged by the move). Confirm the
    gallery 72/72 still renders.
11. **Documentation.** (a) Add the ShaderToy fidelity bar to `docs/validation-matrix.md` as a distinct
    rung (pixel-vs-original-GLSL, NO `mgfxc` oracle); (b) update `docs/repository-layout.md` to list
    `src/ShadowDusk.ShaderToy/` + `tests/ShadowDusk.ShaderToy.Tests/` and note the converter is an
    additive managed frontend, drivers/sample out-of-band; (c) update the Phase 46 doc's SESSION
    HANDOFF to point "promote core → library" at this phase.
12. **`/platform-check`** the promoted library (must stay cross-platform; it is managed-only — expected
    clean).
13. **Cross-link the appendices** and confirm the CLI-input + sample-migration plans consume the
    library API exactly as this doc specifies.

---

## Acceptance Criteria

- [ ] `src/ShadowDusk.ShaderToy/` exists (moved from `tools/`), pure-managed, `#nullable enable`,
      inherits `Directory.Build.props`, **zero native dependency**, public `ShaderToyConverter.Convert`
      + `Multipass/` API intact, and is in `ShadowDusk.slnx`.
- [ ] `tests/ShadowDusk.ShaderToy.Tests/` exists (moved from `tools/`), in `ShadowDusk.slnx`, and runs
      under `dotnet test ShadowDusk.slnx` — same green count as the pre-move suite (the 371-ish tests +
      corpus + goldens), **0 warnings**.
- [ ] `dotnet build ShadowDusk.slnx` **and** `dotnet test ShadowDusk.slnx` are **green including** the
      new library + test project.
- [ ] **Existing `.fx`→`.mgfx`/`.fxb` output is byte-identical** before vs after the move (byte-diff of
      the existing golden/byte-identity corpus = unchanged) — proving the promotion is purely additive.
- [ ] **No shipped `ShadowDusk.*` library has a MonoGame dependency** (audited); the Runtime helper +
      sample live in `samples/` (sample-migration appendix).
- [ ] **Fidelity gate still 46/46 @ mean 0.00/255** (`render-proof --fidelity`, out-of-band), and the
      gallery still renders 72/72.
- [ ] The deferred-packaging decision is implemented as documented: `ShadowDusk.ShaderToy` is
      `IsPackable=false` with the *deferred-publishing* rationale, NOT in the published release set, and
      no `<Version>`/`<PackageVersion>` property added to its csproj (single-source-of-truth intact).
- [ ] The out-of-band drivers (`render-proof/`) and the fidelity/gallery proofs stay **out of
      `ShadowDusk.slnx`** (like `validation/*`); no reference cycle into the product (verified: nothing
      in the product graph references `ShadowDusk.ShaderToy` upward; the converter→`.fx`→pipeline
      direction is a clean DAG).
- [ ] The ShaderToy fidelity bar is documented as a **distinct** evidence axis (pixel-vs-original-GLSL,
      explicitly NOT `mgfxc`-equivalence) in `docs/validation-matrix.md`; the existing `.fx`→`.mgfx`
      `mgfxc`-equivalence bar is unchanged.
- [ ] `/platform-check` of the promoted library is clean (no new platform-specific assumptions).
- [ ] The CLI-input and sample-migration appendices are cross-linked and consume the library API as
      specified.

## Definition of Done

`ShadowDusk.ShaderToy` is a first-class, in-solution `ShadowDusk.*` **product library** — pure-managed,
zero native dependency, built and tested by `dotnet build/test ShadowDusk.slnx` alongside the rest of
the product, with its full Phase-46 test corpus + goldens green. The converter remains an **additive
frontend upstream of the faithful pipeline**: it emits `.fx` text and changes **nothing** about
existing `.fx` handling, the MGFX v10 output format, the MonoGame 3.8.2 pin, or any existing output byte
(proven by a clean byte-diff). The shipped libraries gain **no MonoGame dependency** (the runtime helper
+ interactive sample moved to `samples/`, per the sample-migration appendix); the render/fidelity
drivers stay out-of-band like `validation/*`. NuGet publication is **deferred** by an explicit,
documented one-line toggle. The ShaderToy frontend's honest, distinct evidence bar
(pixel-fidelity-vs-original-GLSL, 46/46 @ 0.00 — NOT `mgfxc`-equivalence) is documented. The CLI `.glsl`
input and the sample/runtime migration are planned in the two linked appendices.

---

## Follow-up work — converter robustness + diagnostics — DONE (2026-06-21)

Two converter gaps surfaced while testing real third-party ShaderToy shaders through the new WASM-fiddle
ShaderToy path (the fiddle now accepts ShaderToy/GLSL input and renders it fullscreen; see
`samples/ShaderFiddle.Web`). Both were **converter** issues, NOT pipeline or WebGL-profile issues, and both
violated the project's "never silently wrong / fail loudly with a clear, located diagnostic" rule because
the converter emitted invalid HLSL and let DXC be the one to complain. **Both are now implemented:**

- **F1 (`src/ShadowDusk.ShaderToy/IdentifierSafety.cs`)** plans renames for a local that shadows a CALLED
  function (`mat3 rot = rot(...)` -> local `rot_sd`, the call + function stay `rot`) and for any identifier
  that is an HLSL reserved keyword (`matrix`/`sample`/`linear` -> `*_sd`), emitting a located **Warning**
  per rename. `HlslEmitter` applies the maps at value-refs, declarations, and call heads; type inference
  stays on the original names. It renames ONLY where HLSL genuinely breaks, so a shader that already
  compiled is byte-for-byte unchanged (the 376 existing goldens are untouched). Tests:
  `Phase47IdentifierSafetyTests` (5 cases incl. the no-false-positive uncalled-shadow), authored fixtures
  `identifier_shadow_rot.glsl` + `reserved_word_identifiers.glsl` (+ goldens), and CLI integration
  `Glsl_LocalShadowingFunction_AutoRenamed_Compiles_WithWarning`.
- **F2 (`src/ShadowDusk.Cli/PipelineRunner.cs`)** stops stamping the `.glsl` filename onto generated-HLSL
  line numbers: a pipeline (DXC) error on converted input is attributed to `<name>.generated.fx` and led
  by an `SD0003` **note** ("the diagnostics below refer to the GENERATED HLSL, not your original source
  lines"). Convert-stage diagnostics keep the real `.glsl` location. `SourceFileName` is diagnostics-only
  (does not affect output bytes). Test: `Glsl_PipelineError_IsAttributedToGeneratedHlsl_NotTheGlslSource`.

Validation: `dotnet test ShadowDusk.slnx` green (ShaderToy 385, Integration 496, 0 warnings); Windows
render gates 4/4 (DX, DX-modern VTF, KNI-DX, FNA) re-confirmed green (the pipeline is untouched). Deferred
tail (intrinsic-name local shadow like `vec3 min = ...; min(a,b)`, struct/custom-uniform reserved-word
renames, and full GLSL source-mapping of generated-HLSL lines) is rare and still fails loudly with the F2
attribution; revisit if a real shader needs it.

### F1 (as planned). Identifier-safety pass (reserved-word / name-collision protection) [the actual bug]

GLSL identifiers that are invalid as HLSL identifiers pass through the converter verbatim and produce
HLSL that DXC rejects with an opaque error. Two flavors:

- **A local variable that shadows a function name.** Legal GLSL, invalid HLSL. Reproduced with mrange's
  "Let's self reflect" ShaderToy: `mat3 rot = rot(normalize(r0), normalize(r1));` — the local `rot`
  shadows the `rot()` function, so DXC reports `error X0000: type 'float3x3' does not provide a call
  operator`. Renaming the local (`rot` -> `rotm`) compiles cleanly (verified via the CLI).
- **An identifier that collides with an HLSL keyword/intrinsic** (e.g. a GLSL variable named `min`,
  `sample`, `input`, `output`, `matrix`, `vector`, `linear`, `texture`, `row_major`, ...). Fine in GLSL,
  breaks in HLSL.

**Fix:** a converter identifier-safety pass that detects both cases and AUTO-RENAMES the offending
identifier (and its in-scope references) — e.g. `rot` -> `rot_sd` — emitting a **located Warning** (so it
is seamless AND transparent, honoring both directives). Genuinely unresolvable cases become a clear,
GLSL-located reject rather than an opaque DXC error. Needs scope-aware renaming in the
parser/type-inference/emitter, an HLSL reserved-word/intrinsic table, unit tests, and a regression
fixture (the `rot`/reserved-word shaders) per the repo's "every fixed bug earns a fixture" rule.

### F2. Better diagnostics for ShaderToy/GLSL input

When converted ShaderToy input fails at the **pipeline (DXC) stage**, the diagnostic is poor:

- **Wrong line attribution.** The error is stamped with the original `.glsl` filename but carries the
  *generated* `.fx` line numbers. A ~30-line `.glsl` reproduced an error reported at "line 51" (a line
  that exists only in the synthesized `.fx`), so a click-to-jump lands nowhere. Convert-stage diagnostics
  ARE correctly GLSL-located; only pipeline-stage ones leak generated-HLSL lines under the `.glsl` name.
- **Raw DXC message.** `type 'float3x3' does not provide a call operator` never explains the GLSL cause
  (a local shadowing a function).

**Fix:** (a) catch known translation traps (F1 and similar) at CONVERT time so they surface GLSL-located,
in plain English, and never reach the mislocated pipeline error; (b) for residual pipeline errors on
converted input, stop stamping the `.glsl` filename onto generated-HLSL line numbers — attribute them as
"generated HLSL" (full GLSL source-mapping through `.fx` generation is a larger effort, deferred). The
fiddle should also surface the panel detail more prominently than the "N compile error(s)" summary.

Suggested order: F1 first (it is the actual fix for the shaders that fail), then F2.

---

## Open questions / risks (with the owner)

- **Reversibility (low risk).** The whole phase is a `git mv` + csproj-path + `.slnx`-membership change
  on a pure-managed library with no downstream references into it. Rollback = move the two dirs back and
  drop the `.slnx` entries. No pipeline code, no native asset, no format change is touched. **Risk:
  low; rollback: trivial.**
- **Test count drift (cosmetic).** Phase 46 cites both "371" (HANDOFF) and "380" (this prompt) — the
  difference is the multipass + integration theories. The acceptance criterion is "same green count as
  the moved suite reports, 0 warnings", not a hard number; confirm the exact count at move time. The
  multipass *Integration theory* (compiles per-pass `.fx` via the real CLI) is the one child-process
  test — decide keep-in-place vs relocate-to-Integration.Tests (Decision F) to keep the new project
  fast and pure.
- **`shadertoy2fx.slnx` final disposition.** This doc keeps it building the out-of-band drivers via the
  new `src/` path, but the *final* home of the standalone `shadertoy2fx` CLI (retire vs keep as a debug
  CLI) and the render-proof drivers is **shared with the two appendices**. Flag for the owner: do we
  keep a `tools/shadertoy2fx/` out-of-band solution at all once the library+tests are in `ShadowDusk.
  slnx` and the sample/CLI are relocated, or fold render-proof into a `validation/`-style standalone?
  (Recommend: keep render-proof out-of-band under `tools/` or `validation/` — it needs a real GL driver
  + Silk.NET, exactly like the render gates that are deliberately not in the solution.)
- **Publishing-later guard.** When the owner flips `IsPackable=true` later, that is a release-affecting
  change (adds a 7th package). It must go through `/release` (single-version-source flip, release-set
  membership, validate-job guard) — record now so the future flip is not done ad-hoc. Until then the
  library is intentionally absent from the published set; consumers reach ShaderToy input via the CLI
  route (appendix), which needs no new package.
- **Fidelity-bar conflation risk.** The single biggest *documentation* risk is someone later reading the
  46/46 fidelity gate as an `mgfxc`-equivalence claim. Mitigated by documenting it as an explicitly
  separate rung in `docs/validation-matrix.md` and in this doc; the downstream `.fx`→`.mgfx` step
  retains the real `mgfxc` bar, untouched.
- **No-MonoGame-dep enforcement durability.** The shipped-libs-have-no-MonoGame-dep property is enforced
  only by audit at move time. Flag: consider a small `dotnet list package`/grep CI guard (or a test) so
  a future edit can't quietly add a MonoGame `PackageReference` to a shipped `ShadowDusk.*` lib. Owner
  to decide if that guard is worth adding here or deferred.
