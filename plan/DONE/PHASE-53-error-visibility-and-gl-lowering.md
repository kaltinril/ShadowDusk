# Phase 53 — Error visibility & GL lowering completeness

**Status: ✅ Complete (opened 2026-07-21, closed 2026-07-23)**
**Track: Correctness / drop-in `mgfxc` fidelity + consumer UX.**

## 1. Why this phase exists (the field evidence)

Multiple community reports over June–July 2026 describe the same experience: *"shader
compilation for OpenGL failed with something like 'shader compilation failed' without
saying what's wrong exactly, while the same shader compiled successfully for DirectX"*,
and *"it was an error on the spritebatch call"*. The 2026-07-21 project review traced
both to **two stacked sources that produce the same generic sentence**:

1. **Compile time (ShadowDusk's own output).** Any DXC diagnostic line without a
   `file:line:col:` prefix (SPIR-V codegen/legalization errors — disproportionately the
   *OpenGL* leg) collapses into one fixed error `X0000: "Shader compilation failed"`
   (`DxcDiagnosticReformatter`), with the real compiler text captured in
   `ShaderError.RawDiagnostics` — **which no delivery surface ever prints**, and no
   verbose flag exists. The backend contract also keeps only `errors[0]` (which can even
   be a *warning* when DXC prints warnings before the error).
2. **Runtime (the engine's message, not ours).** ShadowDusk exits 0 but emits GLSL that
   strict drivers (Mesa, WebGL1/ANGLE, native GLES) reject. MonoGame 3.8.2's GL backend
   then writes the real driver log to `Debug.WriteLine` (invisible in a normal game) and
   throws `InvalidOperationException("Shader Compilation Failed")` — **lazily, at the
   first draw, i.e. "on the SpriteBatch call"**. KNI throws
   `"Shader Compilation Failed." + log`, but the log references our *generated* GLSL.
   The emission classes are the open issues **#137 #138 #139 #140 #141** (found by the
   issue-#136 adversarial sweep) plus the unguarded SpriteBatch varying mismatch
   (a PS-only pass reading interpolants beyond `COLOR0`+`TEXCOORD0` links against the
   built-in SpriteEffect VS, which only writes `vFrontColor`/`vTexCoord0`).

Constraint 5 (fail loudly, verbatim) was honored on *capture* but violated on *surface*.

## 2. Goals (owner directives, 2026-07-21)

1. **Fix the issues** — close the open GL emission classes that turn into runtime
   failures (#137, #139, #140; #138 as far as tractable; #141's missing diagnostic).
2. **Bubble the real errors up to users** — every surface (library, CLI, WASM, Fiddle)
   shows the underlying compiler's text **by default**; *"this isn't security software
   with passwords, it's just a shader error — show them."* No flags, no opt-in.
3. **A dead-simple `Validate` / `ValidateAsync` API** — one call that tries to show
   *all* issues with a shader (across targets, errors *and* warnings), for users
   debugging "works on DX, dies on GL".
4. **As simple as possible for the consumer** — nothing here may add a step, a flag, or
   a concept the consumer needs to get correct output (CLAUDE.md seamless directive).

Non-goals: changing MonoGame/KNI's own runtime exception text (not ours to fix — we
compensate by preventing the class at compile time and documenting the triage);
byte-identity with mgfxc (never a goal).

## 3. Design decisions

### D1 — Kill the generic message; promote raw text verbatim
`DxcDiagnosticReformatter` / `D3DCompilerDiagnosticReformatter`: when diagnostic lines
don't parse, the **raw text itself becomes `Message`** (trimmed, verbatim — constraint
5), never the fixed string `"Shader compilation failed"`. `X0000`'s registry meaning
becomes "diagnostic from the underlying compiler, passed through verbatim (no parseable
location)". Side benefit: ShadowDusk's compile-time text can no longer be confused with
MonoGame's runtime `"Shader Compilation Failed"` exception when triaging user reports.

### D2 — Primary-error selection prefers errors, and the primary carries the full raw text
The per-stage backend contract returns a single `ShaderError`; until that contract is
widened, the selected primary is the **first `Severity == Error`** entry (falling back
to `errors[0]`), and its `RawDiagnostics` is set to the **complete** native diagnostic
text so nothing is dropped even in single-error form. Applies to `DxcShaderCompiler`
(compile + preprocess), `D3DCompilerShaderCompiler`, and `Vkd3dCompileContract`.

### D3 — Surfaces print `RawDiagnostics` by default when it adds information
- **CLI**: the MGCB-parseable line prints first (unchanged format), then any
  `RawDiagnostics` content not already contained in the message prints as
  indented follow-on stderr lines. Extra stderr lines are safe for MGCB.
- **Fiddle sample**: shows the raw block under the diagnostic entry.
- No verbosity flag is added — visible-by-default is the point.

### D4 — Warnings channel (`CompiledShader.Warnings`) and the end of `-WX`-by-default
`CompiledShader` gains a non-positional `Warnings` init property
(`IReadOnlyList<ShaderError>`, default empty; `Severity == Warning`). Sources:
- **DXC**: the pipeline stops forcing `-WX` (`AllowWarnings = false → true` at the
  `DxcCompileOptions` construction in `CompilationPipeline.Run`). **mgfxc parity:**
  mgfxc's fxc front end does not pass `/WX`; our `-WX` default made the GL leg *stricter
  than the reference compiler* and was a confirmed "DX compiles, GL fails" divergence
  class. Warning text on successful compiles is captured (`PlatformBlob.Warnings`) and
  surfaced instead of being fatal or discarded.
- **GL portability lint** (D5) findings.
The CLI prints warnings in MGCB format (`warning SD04xx: …`) with exit code 0.

### D5 — GL portability lint: turn the silent runtime class into compile-time diagnostics
A product-side `GlslPortabilityAnalyzer` (ShadowDusk.GLSL; the issue-#136 test
analyzer ported + extended) runs over the emitted MonoGame-GL GLSL in the pipeline:
- **SD0400** (#141): a `dFdx`/`dFdy`/`fwidth` lexically inside a loop with a divergent
  exit (`break`/`discard`) — derivatives read 0.0 on ANGLE D3D11 (every Windows
  browser); fxc warns X3553/rejects X4532 on the same shape, so silence is a fidelity
  gap. Fires only when the *user's* shape keeps it (Rule 9a already unwraps ours).
- **SD0401**: a pass with **no vertex shader** whose pixel shader reads interpolants
  beyond `vFrontColor` (COLOR0) / `vTexCoord0` (TEXCOORD0) — with SpriteBatch, the
  built-in SpriteEffect VS never writes them, and strict GL drivers fail the program
  link **at the first draw**. Includes any `var_<SEM>` passthrough varying.
- **SD0402** (#138, diagnostic half): a loop shape outside GLSL ES 1.00 Appendix A
  (`for (;;)`, or an empty-increment `for` with the index advanced in the body) — the
  effect may fail to load on WebGL1 / KNI Reach.
All lint findings are **warnings, never errors** (they render fine on many targets and
must not reject working desktop shaders); the known-fatal shapes keep their existing
loud `SD0210` errors.

### D6 — `Validate` / `ValidateAsync`: one call, all issues
```csharp
ShaderValidationReport report = await compiler.ValidateAsync(fxSource);
Console.WriteLine(report);            // human-readable, per-target, everything
if (!report.IsValid) { /* report.Targets[i].Errors / .Warnings */ }
```
- Default target set: **OpenGL + DirectX** (the two mainstream MonoGame/KNI backends —
  exactly the "DX works, GL doesn't" reports). Overload takes explicit
  `PlatformTarget[]` (Vulkan/FNA opt-in — FNA's SM2/3 dialect would false-alarm
  MonoGame users if validated by default).
- Implemented via `CompileAsync` per target (one pipeline, never a fork); output bytes
  are discarded; errors + warnings are aggregated per target.
- `ShaderValidationReport.ToString()` renders the friendly multi-line report so "print
  one thing" is the whole consumer story. Sync `Validate` mirrors it (same
  WASM `InitializeAsync` precondition as `Compile`).
- **Correction (post-review, same phase):** the first cut implemented these as C# 8
  default interface methods on `IShaderCompiler`. Default interface methods are only
  reachable through an interface-TYPED reference — `var compiler = new
  EffectCompiler();` (the exact pattern the README/quickstarts use for `CompileAsync`)
  fails to compile against `compiler.ValidateAsync(...)` with `CS1061`, because the
  compile-time type is `EffectCompiler`, not `IShaderCompiler`. Verified by building the
  README's snippet as written. Fixed by moving both methods to
  `ShaderCompilerValidationExtensions`, a public extension-method class over
  `IShaderCompiler` in `ShadowDusk.Core` — extension methods resolve through any type
  that implements the extended interface, so the same call now works whether the
  variable is typed as `IShaderCompiler` or as the concrete `EffectCompiler` /
  `WasmShaderCompiler`, with nothing for either implementer to add. Permanent regression:
  `tests/ShadowDusk.Compiler.Tests/EffectCompilerValidateApiSurfaceTests.cs` calls
  `ValidateAsync`/`Validate` on a `var compiler = new EffectCompiler();` — if the API
  regresses to a default interface method, this file stops compiling.

### Post-review follow-ups (same phase, from the 2026-07-22 review)
- **Warnings survive a later hard failure.** `CompilationPipeline.Fail(error)` used to
  drop the warnings already accumulated in `runWarnings`/`fnaWarnings` when a LATER
  stage failed (e.g. technique 1's pass compiled and warned SD0401; technique 2's pass
  then failed). A `Fail(error, accumulatedWarnings)` overload appends them after the
  fatal error (error stays FIRST — the actionable line leads; zero-warning path
  allocates nothing extra). Note the reachable failure classes are POST-compile stages
  (render-state parse SD0011, reflection, writers, SD0012/SD0025/SD0026) — an
  HLSL-level error in another function can NOT exercise this, because DXC semantically
  checks the whole translation unit on every entry-point compile, so a bad body fails
  the FIRST compile before any warning exists. Severity-aware consumers updated:
  `ShaderCompilerValidationExtensions.ToTargetValidation` splits the failure array into
  Errors/Warnings; the ShaderFiddle summary counts only error-severity entries; the CLI
  formatter already prints per-severity. Regression:
  `Phase53ErrorVisibilityRegressionTests.EarlierTechniquesWarning_SurvivesALaterTechniquesHardFailure`.
- **Raw-diagnostic duplication suppressed on the common path.** The
  print-the-complete-raw-text logic (CLI indented block, report `| ` block, Fiddle
  `<pre>`) used `!Message.Contains(RawDiagnostics)`, which only suppressed the
  unparseable-verbatim case — an ordinary single located error printed its one
  diagnostic line TWICE (formatted + raw). Centralized as
  `ShaderError.HasAdditionalRawDiagnostics`: false when there is no raw text, when the
  message already contains it, or when the raw text is a single line ENDING with the
  message (the common single-diagnostic shape); true for anything multi-line
  (leading warnings, source-echo/caret context). All three surfaces now share it.
  Regression: `ShaderErrorHasAdditionalRawDiagnosticsTests`.
- **SD0400 deduped across nested loops.** A gradient call nested inside two divergent
  loops produced one finding PER enclosing loop; now one finding, attributed to the
  innermost qualifying loop. Regression:
  `Sd0400_GradientNestedInsideTwoDivergentLoops_FlaggedOnce`.

### D7 — Fix the emission classes themselves (the lint is the net, not the fix)
- **#137**: run the stage-agnostic body lowerings on the **vertex** stage too —
  Rule 8 (round), Rule 9b (one-shot do-while → for), Rule 10 (pow-square), Rule 11
  (reciprocal fold) — inside the `isVertex` branch **before** `InjectPosFixup`.
  Rule 9a (unwrap → early `return;`) stays pixel-only: an early `return` in a VS would
  skip the posFixup tail lines appended at end-of-main (exactly the issue's caution).
  Rule 9b is safe: single loop exit, fixup lines land after the loop.
- **#139**: fragment shaders whose emitted body contains `dFdx`/`dFdy`/`fwidth` get
  `#extension GL_OES_standard_derivatives : enable` prepended as the **first line**
  (mgfxc parity — `ShaderData.mojo.cs` does exactly this; scan includes `fwidth`,
  which SPIRV-Cross emits directly and mgfxc's two-token scan never had to handle).
- **#140**: `LowerRoundToFloorHalfUp` resumes the scan **inside** the replacement
  (`searchFrom = callStart`) so a `round()` nested in another's argument is visited;
  terminates because `floor((…))` can never re-match the function name. (Rule 10
  already does the equivalent.)
- **#138 (emission half)**: assessed in-phase; the empty-increment canonicalization
  (move the body-trailing `index++` into the `for` rest-statement when provably the
  only index write) lands only if it stays a contained, gate-green transform —
  otherwise it stays an open issue with SD0402 lint coverage and this doc records the
  deferral.

### D8 — Repo hygiene: the NUL byte in `CompilationPipeline.cs`
`ExpansionUnavailable` embeds a **raw NUL character** in its string literal, which makes
ripgrep classify the product's central file as *binary* and silently skip it in every
search (this phase's review hit it). Replaced with the `"\0"` escape — identical string
value, file becomes text again.

## 4. Work items

- [x] W1 — D1+D2 reformatters/primary selection (`DxcDiagnosticReformatter`,
      `D3DCompilerDiagnosticReformatter`, `DxcShaderCompiler`,
      `D3DCompilerShaderCompiler`, `Vkd3dCompileContract`) + unit tests. As-built:
      `SelectPrimary(text, file, noDiagnosticsFallback, fallbackCode)` +
      `ReformatAsWarnings` on both reformatters; the WASM `MapJsException` delegates to
      the same policy (SD1900 fires only on an empty exception message; SD0212 only on
      empty vkd3d text).
- [x] W2 — D4 warnings channel: `PlatformBlob.Warnings`, DXC success-path capture,
      `-WX` default flip, pipeline aggregation (dedupe by (File, Line, Column, Code,
      Message) across stages/passes), `CompiledShader.Warnings`. As-built extras: the
      vkd3d message buffer is captured on success too (previously discarded), and the
      FNA path carries warnings through `CompileFnaStage`. The GL success path keeps
      only the SPIR-V compile's warnings (the DXIL-for-reflection compile would
      duplicate them).
- [x] W3 — D3 surfacing: CLI raw-block (`MgcbErrorFormatter.FormatAll`, parseable line
      first, indented raw lines after) + success-path warning lines
      (`PipelineRunner`); Fiddle UI raw `<pre>` blocks + warnings-on-success in the
      diagnostics panel. `TestHelpers.CompileViaPipelineAsync` mirrors the CLI surface
      (warnings → stderr, exit 0) so both invocation modes assert identically.
- [x] W4 — D7 rewriter fixes #140, #139, #137 with regression fixtures
      (`examples/Issue140NestedRound.fx`, `Issue139DerivativeExtension.fx`,
      `Issue137VsRound.fx`, `Issue137VsEarlyReturn.fx`) + rewriter unit tests +
      `Phase53ErrorVisibilityRegressionTests` (GL structural + DX-still-compiles).
      Empirically confirmed: VS `floor((_34.xy * 8.0) + 0.5)`, the 9b
      `for (int _spvonce_0 …)` form with posFixup after the loop, the header as the
      first fragment line.
- [x] W5 — D5 `GlslPortabilityAnalyzer` (public, ShadowDusk.GLSL) + pipeline wiring +
      SD0400/0401/0402 registered. The for-header classification splits at top-level
      semicolons (paren-safe), so Rule 9b's own loop and `i < f(x)` conditions never
      false-positive.
- [x] W6 — D6 `Validate`/`ValidateAsync` (extension methods over `IShaderCompiler` —
      see the D6 correction above; default targets OpenGL+DirectX; a set
      `CompilerOptions.Profile` pins the single profile target) + `ShaderValidationReport`
      (`IsValid`/`IsClean`/`AllDiagnostics`/friendly `ToString`) + end-to-end tests (the
      int-uniform "DX works, GL doesn't" report shape, and warnings surfacing), plus the
      `EffectCompiler`-typed-variable API-surface regression test.
- [x] W7 — D8 NUL-byte fix (`"\0"` escape; value identical, file greps as text again).
- [x] W8 — Full `dotnet test ShadowDusk.slnx` green: **2038 tests** (was 1990;
      +40 unit, +15 integration, 2 reformatter pins updated to the new verbatim
      contract). The cross-host byte-identity manifest stayed green **without
      regeneration** — none of its fixtures contain the affected shapes, proving the
      output changes are confined to the intended classes (and that dropping `-WX`
      changes no bytes for warning-free shaders).
- [x] W9 — Windows render gates green (`./validation/run-windows-render-gates.ps1`) —
      required: #137/#139 change emitted GL bytes (VS lowering, extension header).
      **Run 2026-07-23 on a Windows box with an RTX 3080: 8/8 PASS.** The two gates that
      exercise this phase's GL emission changes are the KNI OpenGL desktop gate
      (`KniDesktopGL` + `compare_kni.py`) and the KNI OpenGL VS-driven gate
      (`KniVsDriven`, the issue-#70 matrix/POSITION rig, which is what actually renders a
      VERTEX stage through the newly-applied Rules 8/9b/10/11). `KniVsDriven` compares
      in-process at **maxd 0**; `KniDesktopGL` matches **within tolerance** — max Δ 1 on
      Scanlines and Dots against the mgfxc goldens, maxd 0 against the MonoGame render —
      in real KNI v4.2.9001.0. The ANGLE D3D11 derivative probe (the
      backend where the #139 extension header and the Rule-9a shape matter) also passed.
      The `[FAIL]` lines inside the Vulkan PS-corpus gate are mgfxc's OWN output failing to
      load (`[baseline-vulkan] 0/10 rendered`, the upstream MonoGame `SlotOffset` bug);
      that arm is best-effort by design and "All candidate renders succeeded".
      **Note the merge order slip:** this box went unticked when the phase merged (PR #144)
      and was only closed by the 2026-07-23 post-merge review. The gate is the pre-merge
      bar, not a post-merge formality.
- [x] W10 — Support-surface docs (same PR): CHANGELOG `[Unreleased]`,
      `docs/error-codes.md` (X0000/SD0212 meaning updates, SD0400–SD0402 + range row),
      `docs/glsl-uniform-naming.md` (Rule 8 nested-resume note, new Rule 12 derivatives
      header, VS-stage lowering paragraph with the 9a-stays-pixel-only rationale),
      `docs/validation-matrix.md` §7 (two new rows), `README.md` (the one-line
      ValidateAsync pitch), XML docs on all new public API. DocFX architecture pages
      pick the rewriter-rule changes up via the `docs/glsl-uniform-naming.md`
      transclusion.

### #138 disposition (D7, decided 2026-07-22)

The **emission-side canonicalization is deferred**: rewriting SPIRV-Cross's genuine
loop shapes (`for (;;)` data-dependent trips; empty-increment with in-body `index++`)
is a delicate string transform over real multi-iteration control flow, its blast
radius is WebGL1 / KNI Reach only, and it would need its own render-gate proof over
the loop-bearing corpus (GaussianBlur, apos EllipseSDF). SD0402 covers the class as a
compile-time warning today (verified live: compiling `apos-shapes-aa.fx` on GL prints
the `for (;;)` warning). Issue #138 stays open for the emission half, pointing here.

**Follow-on landed (2026-07-24): the empty-increment shape (shape 2, `GaussianBlur.fx`)
is now fixed, not just warned about.** `MonoGameGlslRewriter` Rule 12
(`LowerEmptyIncrementForLoop`) hoists the trailing `<index>++; continue;` (or
`+= k; continue;`) into the for-header's increment clause whenever it can prove the
rewrite safe (no other write to the index, no other `continue` in the body) —
semantically exact, pixels unchanged. Confirmed end-to-end: compiling the real
vendored `Nez/GaussianBlur.fx` through the CLI no longer emits `SD0402` at all.
**Shape 1 (a genuinely runtime-bounded trip count, e.g. Apos.Shapes' Newton-iteration
SDF) is NOT fixed and stays open** — by the time the GLSL reaches the rewriter,
SPIRV-Cross has already erased any compile-time bound into an opaque runtime SSA
value, so there is no provably-safe mechanical rewrite available at this stage; a real
fix would need a static-bound analysis threaded through from HLSL/DXC, a materially
bigger project. `apos-shapes.fx` continuing to warn `SD0402` through the real CLI is
pinned as the regression for this still-open half.

## 5. Validation

- Unit: reformatter promotion/selection; rewriter rules (VS lowering, nested round,
  extension header) pinned per shape; analyzer true/false positives (incl. Rule 9b's
  own `for (int _i = 0; _i < 1; _i++)` NOT flagged by SD0402).
- Regression fixtures: VS `round()`, VS early-return helper, nested `round(round(x))`,
  a `fwidth`-using PS (header present), a PS-only TEXCOORD1 reader (SD0401 fires), a
  gradient-in-divergent-loop PS (SD0400 fires).
- The full suite + the Windows render gates (KNI-GL, DX, ANGLE probe included
  default-ON) prove pixels unchanged where they must be, and the byte-identity /
  golden-structural tests prove the PS output changes are limited to the intended
  classes (#139 header line).

## 6. Issue mapping when this phase ships

| Issue | Disposition |
|---|---|
| #137 VS skips body lowerings | Fixed (Rule 8/9b/10/11 on VS; 9a deliberately PS-only, recorded) |
| #138 non-Appendix-A loops | SD0402 lint warning always; emission canonicalization only if contained (else stays open, deferral recorded here) |
| #139 missing derivatives #extension | Fixed (mgfxc-parity header incl. `fwidth`) |
| #140 nested round survives | Fixed (resume-inside scan) |
| #141 no diagnostic for user gradient-in-divergent-loop | Fixed as SD0400 warning (fxc-warns parity; we cannot force-unroll) |
| (unfiled) SpriteBatch varying mismatch invisible until draw | SD0401 warning |
| (unfiled) generic X0000 hides real DXC text; no surface prints RawDiagnostics; no warnings channel | Fixed (D1–D4) |
