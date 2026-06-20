# Phase 47 Appendix — CLI ShaderToy / GLSL input

**Track:** Delivery shapes (CLI).
**Status:** Planned (written 2026-06-20). **Appendix** to the main Phase 47 plan
(`plan/PHASE-47-shadertoy-frontend-promotion.md`, owned by a sibling agent). This appendix covers
**only** the `ShadowDuskCLI` (`src/ShadowDusk.Cli`) change that lets the drop-in `mgfxc` accept
**ShaderToy / GLSL** input in addition to `.fx`, routing it through the promoted
`ShadowDusk.ShaderToy` converter into the **existing** compile pipeline. It does **not** plan the
converter promotion itself (the main plan does); it consumes the promoted library's frozen API.

> **Anchor (from the main plan).** The converter is promoted from `tools/shadertoy2fx/src/ShadowDusk.ShaderToy`
> to `src/ShadowDusk.ShaderToy`: pure-managed, **no native dependency**, public surface
> `ShaderToyConverter.Convert(string glsl, ConvertOptions?) → ConvertResult { Success, Fx, Diagnostics, UsedUniforms }`
> plus the `Multipass/*` APIs. The CLI takes a `ProjectReference` to it and, for a ShaderToy/GLSL
> input, converts `glsl → .fx` **text** then feeds the **EXISTING** `EffectCompiler`/`CompilationPipeline`
> to emit `.mgfx`/`.fxb` for the requested `/Profile`. **`.fx` handling stays 100% unchanged.**
> Purely additive.

**Depends on:**
- **Main Phase 47** — the converter must be promoted to `src/ShadowDusk.ShaderToy`, added to
  `ShadowDusk.slnx`, and packaged. This appendix is blocked on that `ProjectReference` existing.
- The `EffectCompiler : IShaderCompiler` entry point (`src/ShadowDusk.Compiler/EffectCompiler.cs`):
  the produced `.fx` text is fed to `CompileAsync(hlslSource, options)` exactly as a real `.fx` is
  today (`src/ShadowDusk.Cli/PipelineRunner.cs`). The CLI adds **zero** new compilation logic.

**Blocks:** Nothing in the product pipeline. This is an ergonomics surface: ShaderToy authors can run
`ShadowDuskCLI shader.glsl shader.mgfx /Profile:OpenGL` and get a loadable effect without first
hand-running the converter. The single combined-proof goal (compile where `mgfxc` can't + render like
`mgfxc`) is met without it; this just removes a manual step.

---

## Overview

Today `ShadowDuskCLI <src> <out> [flags]` reads `<src>` as HLSL `.fx`, builds `CompilerOptions`, and
calls `EffectCompiler.CompileAsync` (`PipelineRunner.RunAsync`, stages 1-3). The output container/target
is chosen by `/Profile` + `--mgfx-version` (or a `--target-runtime` profile). This appendix inserts a
**single new stage 1.5** *only when the input is detected as ShaderToy/GLSL*: run
`ShaderToyConverter.Convert(glsl)` to produce `.fx` text, then hand that text to the **unchanged** stage
2/3. A `.fx` input never touches the converter.

The mental model: the converter is a **front-end text transform**, `glsl → .fx`. Everything downstream
(`/Profile`, `/Debug`, `/I`, `/DxbcBackend`, `--mgfx-version`, `--target-runtime`, exit codes,
diagnostic format, output writing) is identical to the `.fx` path because it **is** the `.fx` path,
operating on the converter's emitted text.

```
                         ┌─ .fx  ──────────────────────────────────────────────┐
  <src> ── detect ──────►│                                                     ├─► EffectCompiler ─► .mgfx/.fxb
                         └─ .glsl ─► ShaderToyConverter.Convert(glsl) ─► .fx ──┘      (UNCHANGED)
```

---

## Scope & Non-Goals

**In scope:**
- A `ProjectReference` from `src/ShadowDusk.Cli` to the promoted `src/ShadowDusk.ShaderToy` (pure
  managed — no new native asset in the CLI package).
- **Input detection** (seamless, no required flag): decide `.fx` vs ShaderToy/GLSL from the input,
  with an **optional, non-required** `--input-format auto|fx|glsl` escape hatch (default `auto`).
- **Routing** the single-file ShaderToy/GLSL path through `ShaderToyConverter.Convert` then the
  existing pipeline.
- **Diagnostics**: surfacing each `ConvertDiagnostic` (error *and* warning) in the **same**
  MGCB-parseable `file(line,col-col): severity CODE: message` form the CLI already emits
  (`MgcbErrorFormatter`), with line/column mapping back to the **original `.glsl`**.
- **UsedUniforms**: printing the drivable-parameter list to **stderr** (informational), so the user
  knows what to drive at runtime.
- A single new CLI flag (`--input-format`) documented as a non-required escape hatch, plus an
  **optional** follow-up multipass batch mode (`--multipass`, see *Multipass* — likely deferred).
- CLI integration tests (`.glsl → .mgfx`, error-path file/line/col, byte-identity vs `Convert`+pipeline,
  `.fx` regression unaffected) and a render-gate entry.
- README + user-doc updates.

**Out of scope / Non-Goals:**
- **The converter promotion itself** (main plan). This appendix assumes the frozen API exists.
- **New compilation behavior.** The CLI is a thin front-end transform + the existing pipeline; no
  second pipeline, no substitute compiler (THE PURPOSE: one faithful pipeline everywhere).
- **A ShaderToy *runtime* / render-graph orchestrator.** Multipass (if exposed at all) only *emits*
  per-pass `.fx`/`.mgfx` + the existing `manifest.json`/`WIRING.md`; the consumer writes the Draw loop
  (the library already documents this).
- **Bundling the `ShaderToyEffect` runtime helper** with the CLI (that is the consumer's runtime
  concern; the CLI only compiles).
- Changing the `.fx` invocation, the `.mgfx` v10 format, or any existing flag's meaning.

---

## Architecture & key decisions

### 1. Input detection (seamless, no required flag)

The hard requirement (CLAUDE.md → *Seamless for the end user*): the user must **never** be required to
set a flag to get correct output. So detection is **automatic** by default, with an **optional**
override that is only ever an escape hatch.

**Chosen strategy: extension-first, content-sniff as the tie-breaker, explicit flag as an override.**

Resolution order for `<src>` (when `--input-format` is `auto`, the default):

1. **`--input-format` override (escape hatch).** If the user passed `--input-format fx` or
   `--input-format glsl`, honor it verbatim and skip 2-4. (Never required; only forces a decision.)
2. **Extension signal.**
   - `.fx` → **FX path** (unchanged; no sniff, no converter — preserves the exact current behavior for
     the overwhelmingly common case).
   - `.glsl` / `.frag` / `.fs` / `.glslf` → **GLSL path** (route to converter).
   - Any other / unknown / no extension (e.g. `.txt`, a bare name) → fall to the content sniff (3).
3. **Content sniff (the tie-breaker / unknown-extension resolver).** Read the source text and classify:
   - Contains a ShaderToy `mainImage` **definition** OR a fragment-style `void main()` AND contains
     **no** top-level `technique`/`pass` block → **GLSL path**.
   - Contains a `technique` (and/or `pass`) block → **FX path** (HLSL effect).
   - Neither signal, or **both** an `.fx`-looking `technique` AND a `mainImage` (genuinely ambiguous) →
     **fail loudly** (see *Ambiguity*).
4. **Default for a `.fx` extension is FX, full stop** — even if the sniff would be ambiguous — so no
   `.fx` file ever silently changes behavior (backwards-compat guarantee). The sniff only runs for
   non-`.fx` extensions.

**Why extension-first.** (a) Zero risk to the existing `.fx` corpus: a `.fx` is always FX, never
sniffed, so this change cannot regress a single current invocation. (b) MGCB calls `mgfxc` with `.fx`
sources, so the MGCB drop-in path is untouched. (c) `.glsl`/`.frag`/`.fs` are the de-facto ShaderToy /
glslViewer / KodeLife export extensions, so the common ShaderToy author's file Just Works with no flag.
(d) The sniff is the safety net for the off-convention case (a ShaderToy shader saved as `.txt`, or a
copy-pasted snippet) without forcing the user to rename or flag.

**Why a content sniff at all (and why narrow).** Some users save ShaderToy code as `.txt` or pipe it in
with an arbitrary name; the sniff catches that. It is deliberately a **cheap structural check** (token
scan for `mainImage`/`void main`/`technique`/`pass` outside comments+strings), **not** a parse — the
real parse/validation is the converter's job (which fails loudly if the sniff guessed wrong). The sniff
errs toward FX whenever a `technique` is present, because an HLSL effect with a `technique` is
unambiguously an `.fx`.

**The sniff must ignore comments and strings.** `mainImage`/`technique` inside a `//`/`/* */` comment or
a string literal must not trigger a misclassification. A minimal comment/string-stripping pre-pass
(reuse or mirror the converter's preprocessor comment handling if cheaply available; otherwise a small
local scanner) feeds the token check. (Risk noted in *Open questions*.)

**Alternatives considered:**
- *Sniff-only (ignore extension).* Rejected: a `.fx` that happens to contain a `void main()` helper
  (legal HLSL) could be misrouted to the converter — a backwards-compat regression. Extension-first
  removes that risk for the `.fx` case entirely.
- *Extension-only (no sniff).* Rejected: forces ShaderToy users who saved as `.txt`/no-extension to
  rename or flag — not seamless for that slice. The sniff is a cheap, conservative safety net.
- *Always require `--input-format`.* **Rejected outright** — it is exactly the "required flag for
  correct output" the seamless directive forbids.

### 2. The `--input-format` flag (optional escape hatch only)

- Form: `--input-format <auto|fx|glsl>` (GNU long-option; also accept `--input-format:<value>` and
  `--input-format=<value>` to match the existing parser's both-form handling for `/Profile:`,
  `--mgfx-version`, etc.). Default `auto`.
- It is **never required for correct output** — `auto` produces correct output for every conventional
  case. It exists purely to (a) force `glsl` on an oddly-named file the sniff can't see, or (b) force
  `fx` to defeat the sniff in a pathological ambiguous file. This matches the existing precedent of
  `/DxbcBackend` and `--mgfx-version` as documented non-required escape hatches.
- An invalid value is a loud parse error (new code, e.g. `X0011`), consistent with the existing
  `X0004`/`X0005`/`X0006`/`X0008` invalid-value errors in `ArgumentParser`.
- Register `input-format` in `KnownSlashFlagNames`? No — it is a `--` long option only (the parser's
  `IsFlag` already treats `--`-prefixed tokens as flags unambiguously). It will **not** use the `/`
  prefix, avoiding any POSIX-path collision concern.

### 3. Routing the GLSL path

Implemented as a new stage between stage 1 (read source) and stage 2 (build options + compile) in
`PipelineRunner.RunAsync`, gated on the detection result carried on `CliArguments` (a new
`InputFormat`/`ResolvedInputKind` field set by `ArgumentParser` + detection):

```
read <src> text  (stage 1, unchanged)
   │
   ├─ FX  ────────────────────────────────► hlslSource = <src> text         (unchanged)
   │
   └─ GLSL ─► ShaderToyConverter.Convert(text, ConvertOptions{ EffectName = <fileNameNoExt> })
                 │ Success=false ─► map each ConvertDiagnostic → ShaderError → return Fail (exit 1)
                 │ Success=true  ─► (print warnings + UsedUniforms to stderr); hlslSource = result.Fx
                 ▼
   build CompilerOptions (Target = /Profile, Debug, IncludePaths, MgfxVersion, DxbcBackend, Profile)
   compiler.CompileAsync(hlslSource, options)   (stage 2, UNCHANGED)
   write <out>                                  (stage 3, UNCHANGED)
```

Key points:
- The converter is **target-agnostic**: it emits one legacy-FX9 `vs_3_0`/`ps_3_0` `.fx` text, and the
  *existing pipeline* compiles that to OpenGL / DirectX_11 / FNA per `/Profile`. So **all existing
  flags apply unchanged** to the produced `.fx` (this is the whole point of routing through the real
  pipeline rather than a parallel path). `--target-runtime`, `--mgfx-version`, `/DxbcBackend`,
  `/Debug`, `/I` all behave exactly as for a hand-written `.fx`.
- `ConvertOptions.EffectName`/`TechniqueName`: default to the source file name (without extension) so
  the emitted technique has a stable, meaningful name. Not user-configurable in this phase (no flag);
  a future `--effect-name` could be additive if requested.
- `ConvertOptions.CommonSource`: not exposed for the single-file path (a single `.glsl` has no separate
  Common tab). It is only relevant to multipass (deferred).
- `StopOnFirstError`: leave default `false` so the user sees **all** convert diagnostics at once (matches
  how the existing compile path reports all `ShaderError`s via `FormatAll`).
- `/I` include paths and `FileSystemIncludeResolver` apply to the **produced `.fx`**, not the `.glsl`
  (the converter rejects `#include` itself — MAPPING.md — so GLSL-side includes are a loud convert
  error, which is correct). No behavior change.

### 4. Diagnostics (fail loudly, MGCB-parseable, original-`.glsl` line/col)

The converter already produces `ConvertDiagnostic(Severity, Message, Line, Column, Construct)` where
`Line`/`Column` are **1-based positions in the original ShaderToy GLSL** (ConvertApi.cs doc + the
converter's `ComputeLineCol`). The CLI maps each to the existing `ShaderError` and routes it through
the **unchanged** `MgcbErrorFormatter`, so the stderr shape is identical to a shader compile error:

```
shader.glsl(12,5-5): error SD0042: undeclared identifier 'RENDERSIZE' (not a ShaderToy built-in ...)
shader.glsl(3,1-1): warning SD0001: The shader defines BOTH a ShaderToy 'mainImage' and a standalone ...
```

Mapping rules:
- `ConvertDiagnostic.File` = the **original `<src>` path** (so `Path.GetFileName` yields `shader.glsl`,
  pointing the user at their real source, not the synthetic `.fx`). **Never** the converter's internal
  `.fx` text — line numbers in the emitted `.fx` are meaningless to the author.
- `Severity`: `DiagnosticSeverity.Error → ShaderErrorSeverity.Error`,
  `DiagnosticSeverity.Warning → ShaderErrorSeverity.Warning`. `MgcbErrorFormatter` already prints
  `warning`/`error` by `ShaderError.Severity`.
- `Code`: pick a dedicated convert error-code prefix so convert errors are distinguishable from `fxc`/
  pipeline errors. Two options:
  - **(chosen) `SD####`** — `MgcbErrorFormatter.FormatCode` passes through any `SDxxxx`-shaped code
    as-is (its final `return code;` branch), and the comment explicitly cites `"SD0001"` as a supported
    shape, so `SDxxxx` already prints correctly. Use a small, stable mapping (e.g. `SD0001` generic
    warning, `SD0010` reject/error) — or map every convert diagnostic to one error/one warning code in
    v1 and refine later. The `Construct` string is appended into the message (or kept in the message the
    converter already wrote, which names the construct).
  - *Alternative:* reuse the `X####` space. Rejected — convert errors are not pipeline/`fxc` errors and
    sharing the space muddies triage; `SD` cleanly namespaces them and is already supported by the
    formatter.
- **Line == 0** diagnostics (e.g. "Source GLSL was null", or a project-level multipass warning):
  `MgcbErrorFormatter` already drops the `file(line,col)` prefix when `Line <= 0`, printing just
  `severity CODE: message`. No special handling needed.
- **Exit code:** if `ConvertResult.Success == false`, return `1` (consistent with every other compile
  failure; `Program.cs` already maps a `Fail` to exit 1). A successful convert that then fails the
  *pipeline* compile (e.g. an FNA/SM3 instruction-limit overflow on a complex shader — an inherent
  fx_2_0 ceiling, MAPPING.md) surfaces the **pipeline's** error normally and exits 1 — also correct and
  loud.
- **Warnings do not fail the build.** Like `mgfxc`, a warning is printed to stderr and the compile
  proceeds (exit 0 if the pipeline succeeds). This matches the converter's own model (warnings are
  non-fatal; `Success` is still true).

### 5. UsedUniforms (so the user knows what to drive at runtime)

On a **successful** convert, print `ConvertResult.UsedUniforms` to **stderr** as an informational note
(never stdout — the CLI keeps stdout empty for the MGCB contract, per the existing tests). Form:

```
shader.glsl: note: drivable effect parameters: iResolution, iTime, iChannel0, u_gain
```

Rationale: the consumer must bind/drive these each frame at runtime (the `ShaderToyEffect` helper's job);
telling them what the shader references closes the loop. Use a `note:` severity-style line (not
`error`/`warning`) so MGCB doesn't treat it as a diagnostic. **Open question:** whether even this note
should be suppressed by default to keep stderr clean for MGCB (a successful `.fx` compile currently
produces **empty** stderr — see the test `stderr.Should().BeEmpty()`). **Recommended:** gate the note
behind a `--print-uniforms` opt-in OR only print it when stderr is a TTY / when at least one warning is
already being emitted, so the default `.glsl → .mgfx` success path stays stderr-clean and MGCB-safe.
(See *Open questions* — owner decides; default to **opt-in** to preserve the empty-stderr contract.)

### 6. Multipass (scope decision for this phase)

**Decision: DEFER the CLI multipass batch mode to a follow-up; keep the single-file path primary.**

Rationale:
- The seamless single-file path (`shader.glsl → shader.mgfx`) is the high-value, low-risk 90% case and
  fully satisfies the appendix's goal. Multipass adds a *different* CLI shape (batch `-o <dir>` emitting
  N artifacts + a manifest), new output semantics (a directory, not one file), and a render-graph the
  CLI does **not** orchestrate — more surface, more tests, more docs, for a smaller audience.
- The converter already exposes the full multipass API (`ShaderToyProject.Parse`,
  `MultipassConverter.Convert`, `MultipassManifest.ToJson`/`ToWiringMarkdown`) and the
  `tools/shadertoy2fx` repo already has a `render-proof/chain2` example. A consumer who needs multipass
  today can call the library directly; the CLI batch mode is pure convenience.

**When it lands (follow-up sketch, documented here so the shape is pre-agreed):**
- New mode: `ShadowDuskCLI --multipass <export.json> -o <outDir> /Profile:OpenGL`.
- `<export.json>` is the ShaderToy multi-tab API export. `-o <outDir>` is a **directory**.
- For each rendered pass: convert via `MultipassConverter.Convert`, then **compile each pass's `.fx` to
  `.mgfx`/`.fxb`** through the existing pipeline (so the user gets ready-to-load blobs, not just `.fx`),
  named `BufferA.mgfx`, `Image.mgfx`, etc. (normalized pass names).
- Also write the existing `manifest.json` + `WIRING.md` (verbatim from `MultipassManifest`) into
  `<outDir>` so the consumer gets the channel wiring + the ~15-line Draw-loop example.
- Per-pass convert/compile diagnostics route through the **same** `MgcbErrorFormatter` (prefixed with
  the pass name in the message). `sound`/`cubemap` passes warn-and-skip (the library already does this).
- Exit 1 if **any** rendered pass fails to convert or compile.
- **Alternative for the follow-up:** emit per-pass `.fx` only (not `.mgfx`) + manifest, matching the
  library's "we hand you the `.fx`" framing. **Preferred:** compile to `.mgfx` too, since the CLI's job
  is to produce loadable blobs and the user already chose a `/Profile`. (Owner to confirm at follow-up
  time.)

This appendix's tasks below implement **only the single-file path**; multipass is a tracked follow-up.

### 7. Flags / coexistence / exit codes

- **Every existing flag still applies** to the produced `.fx`: `/Profile`, `/Debug`, `/I`,
  `/DxbcBackend`, `--mgfx-version`, `--target-runtime`. No change to their parsing or meaning.
- **One new flag:** `--input-format auto|fx|glsl` (default `auto`, non-required escape hatch). Optional
  future: `--print-uniforms`, `--multipass`/`-o` (deferred), `--effect-name` (deferred).
- **Unknown flags** remain silently ignored (the existing forward-compat rule for future mgfxc flags).
- **Exit codes** unchanged: `0` success, `1` any failure (parse error, convert error, pipeline error,
  I/O error, timeout). The new convert-error path returns `1` via the existing `Fail` plumbing.
- **stdout stays empty** on success (MGCB contract); all diagnostics + notes go to **stderr**.

---

## Tasks

1. [ ] **(blocked on main P47)** Add a `ProjectReference` from `src/ShadowDusk.Cli/ShadowDusk.Cli.csproj`
   to the promoted `src/ShadowDusk.ShaderToy/ShadowDusk.ShaderToy.csproj`. Confirm no new native asset
   enters the CLI package (the converter is pure-managed).
2. [ ] **Detection module.** Add an `InputFormatDetector` (or static method) that, given `<src>` path +
   text + the `--input-format` value, returns `InputKind { Fx, Glsl }` or a loud `ShaderError`
   (ambiguous). Extension table (`.fx`→Fx; `.glsl`/`.frag`/`.fs`/`.glslf`→Glsl; else sniff). Sniff
   ignores comments/strings; classifies by `mainImage`/`void main` vs `technique`/`pass`; ambiguous →
   error.
3. [ ] **Argument parsing.** Add `--input-format` (and `:`/`=` forms) to `ArgumentParser`; new
   `InputFormat` field on `CliArguments` (enum `Auto|Fx|Glsl`). Invalid value → new `Xxxxx`/`SDxxxx`
   loud error. Update `UsageText`.
4. [ ] **Routing.** In `PipelineRunner.RunAsync`, after stage 1, resolve detection; if Glsl, call
   `ShaderToyConverter.Convert(text, new ConvertOptions { EffectName = <fileNoExt>, TechniqueName = ... })`.
   On `Success=false`, map each `ConvertDiagnostic` → `ShaderError(File=<src>, Line, Column, Code=SD..,
   Message)` and return `Fail(...)`. On success, set `hlslSource = result.Fx` and continue to the
   **unchanged** stage 2/3.
5. [ ] **Diagnostic mapping.** Implement `ConvertDiagnostic → ShaderError` (severity map, `SD####` code,
   message incl. `Construct`). Confirm `MgcbErrorFormatter` prints `SD####` as-is and drops the
   `file(line,col)` prefix for `Line == 0`.
6. [ ] **Warnings + UsedUniforms to stderr.** On success, print convert `Warning`s via
   `MgcbErrorFormatter` (stderr), and print `UsedUniforms` as a `note:` line — **gated** so the default
   success path keeps stderr empty (opt-in `--print-uniforms`, or only-with-warnings). Confirm the
   existing "successful compile → empty stderr" test still passes for the default `.fx` and default
   `.glsl` success cases.
7. [ ] **Fixtures.** Add `.glsl` fixtures under `tests/.../fixtures/shaders/shadertoy/` (a minimal
   gradient `mainImage`, a plain `void main()`, one with a custom uniform, one reject case). Reuse the
   converter's existing render-proof fixtures where shapes match.
8. [ ] **CLI integration tests** (`tests/ShadowDusk.Integration.Tests/Cli/`, `[Trait("Category","Integration")]`):
   - `.glsl → .mgfx` exit 0, output non-empty (OpenGL + DirectX_11).
   - **Byte-identity:** CLI `.glsl → .mgfx` bytes == `ShaderToyConverter.Convert(glsl).Fx` fed through
     `EffectCompiler.CompileAsync` with the same options (proves the CLI adds no behavior).
   - **Error path:** a reject `.glsl` exits 1; stderr matches
     `\.glsl\(\d+,\d+(-\d+)?\): error [A-Z]+\d+:` with the **original `.glsl`** filename + a real
     line/col (mirror the existing `.fx` error-format test).
   - **Detection:** a `.txt` ShaderToy file is sniffed to GLSL; a `.fx` is never sniffed; an ambiguous
     file (technique + mainImage) fails loudly; `--input-format glsl/fx` overrides.
   - **Regression:** every existing `.fx` CLI test still passes unchanged (the `.fx` path is untouched).
9. [ ] **Render gate.** Add a `.glsl → .mgfx` case to the OpenGL render gate (CI Mesa) and the Windows
   DX render gate (`validation/run-windows-render-gates.ps1`) so a converted ShaderToy effect is proven
   to **load + render** in the real runtime, not just compile. (Reference oracle = ShaderToy's own WebGL
   output per MAPPING.md; same-backend GL↔GL comparison.) Confirm `dotnet test ShadowDusk.slnx` green
   (regression half of the pre-merge bar) before merge.
10. [ ] **README + docs.** Update `src/ShadowDusk.Cli/README.md` (a `.glsl` usage example +
    `--input-format` note) and the user-facing "choosing a target" / usage docs (DocFX site via the
    `docs-maintenance` skill). State explicitly: ShaderToy/GLSL input is auto-detected; no flag required;
    `.fx` behavior unchanged.
11. [ ] **`/platform-check`** on the new CLI code — no platform-specific assumptions (detection,
    routing, and the converter are all pure-managed).

---

## Acceptance Criteria

- [ ] `ShadowDuskCLI shader.glsl shader.mgfx /Profile:OpenGL` (and `/Profile:DirectX_11`,
  `/Profile:FNA`) compiles a ShaderToy/GLSL `mainImage`/`void main()` shader to a loadable
  `.mgfx`/`.fxb` with **no flag required** and exit 0.
- [ ] The CLI's `.glsl → .mgfx` bytes are **identical** to `ShaderToyConverter.Convert` + the existing
  pipeline for the same source + target (the CLI adds no behavior).
- [ ] The resulting effect **loads in MonoGame's `Effect` and renders** like the ShaderToy WebGL
  reference for the gated fixture (same-backend GL↔GL; evidence-ladder rung 4).
- [ ] A ShaderToy source saved as `.txt`/no-extension is auto-detected via the content sniff; a `.fx`
  is **never** sniffed and behaves exactly as today.
- [ ] A genuinely ambiguous input (a `technique` block **and** a `mainImage`) **fails loudly** with a
  located, MGCB-parseable diagnostic — never a silent wrong route.
- [ ] Convert errors surface on stderr in `shader.glsl(line,col-col): error SD####: message` form, with
  the **original `.glsl`** filename and a real line/col that points at the offending GLSL construct;
  exit code 1.
- [ ] Convert **warnings** (dropped `void main()` wrapper, etc.) surface as `warning` lines; the build
  still succeeds (exit 0 if the pipeline succeeds).
- [ ] `--input-format auto|fx|glsl` exists as a **non-required** escape hatch (default `auto`); the
  default path needs no flag for correct output.
- [ ] **No existing `.fx` invocation changes**: every prior CLI test passes unchanged and the default
  `.fx` success path still produces empty stderr.
- [ ] `dotnet test ShadowDusk.slnx` is green **and** the Windows render gate (`-IncludeFna` for the FNA
  case) is green before merge (the combined pre-merge bar for shader-output-affecting changes).

---

## Definition of Done

`ShadowDuskCLI` accepts ShaderToy / GLSL input (`.glsl`/`.frag`/`.fs`/`.glslf`, or any source the
content sniff recognizes) **in addition to** `.fx`, with **no required flag**: it auto-detects the
input, converts ShaderToy/GLSL `glsl → .fx` via `ShaderToyConverter.Convert`, and feeds the produced
`.fx` to the **existing** `EffectCompiler` pipeline to emit `.mgfx`/`.fxb` for the chosen `/Profile`.
`.fx` handling is byte-for-byte unchanged. Convert diagnostics surface in the same MGCB-parseable
`file(line,col): severity CODE: message` form with original-`.glsl` line/col, drivable uniforms are
reported, the `.glsl` path is render-proven in the real runtime, and tests pin CLI output ≡
library-`Convert`+pipeline output. Multipass batch mode is a tracked follow-up; the single-file path is
complete and primary.

---

## Open questions / risks

- **UsedUniforms / warning noise vs the empty-stderr MGCB contract.** The current "successful compile →
  empty stderr" test is load-bearing for MGCB. Printing UsedUniforms (or even informational notes) on
  every `.glsl` success would break that expectation if MGCB ever feeds `.glsl`. **Recommendation:**
  gate the uniforms note behind `--print-uniforms` (default off) so the success path stays stderr-clean;
  warnings always print (they're real diagnostics MGCB tolerates). **Owner decision needed.**
- **Sniff false-positives / false-negatives.** The content sniff must ignore comments/strings and is a
  structural heuristic, not a parser. A `.fx` is never sniffed (extension wins), so the blast radius is
  limited to non-`.fx` extensions; still, an HLSL snippet saved as `.txt` with a `void main()` helper
  and no `technique` would be (correctly, per our rule) routed to the converter and then loudly rejected
  there — acceptable (fail loud), but worth documenting. **Mitigation:** keep the sniff conservative
  (require absence of `technique` to call something GLSL) and lean on the converter's loud rejects.
- **Error-code scheme (`SD####`).** Need a small, stable convert-diagnostic → `Code` mapping. Simplest
  v1: one `SD0010` for all convert errors, one `SD0001` for all warnings (message carries the detail the
  converter already wrote). Refinable later. Confirm `MgcbErrorFormatter.FormatCode` passes `SD####`
  through unchanged (it does — the final `return code;` branch).
- **Line/col fidelity through the preprocessor.** The converter runs a real C preprocessor before
  lex/parse; MAPPING.md notes inactive branches are preserved as blank lines so diagnostics keep
  pointing at the original line. Verify a reject **inside** a macro / conditional still reports a sane
  original-`.glsl` line in the CLI output (add a fixture).
- **`EffectName` collisions / identifier safety.** Deriving `EffectName`/`TechniqueName` from the file
  name must yield a valid identifier (a file like `2d-noise.glsl` → leading digit / hyphen). Reuse the
  multipass `NormalizePassName` sanitizer (already handles leading-digit + non-alnum) or equivalent.
- **Multipass deferral.** If the owner wants multipass in this phase rather than as a follow-up, the
  *output contract* (one file vs a directory) and the *compile-each-pass vs emit-`.fx`-only* choice must
  be settled first (see §6). Pre-agreeing the `--multipass <json> -o <dir>` shape here de-risks that.
- **Converter API stability.** This appendix binds to the **frozen** `ConvertApi.cs` contract
  (`Convert`/`ConvertResult`/`ConvertDiagnostic`/`ConvertOptions`) and the `Multipass/*` types. If the
  main P47 promotion changes any shape (it should not — the contract is explicitly frozen), reconcile
  here.
```
