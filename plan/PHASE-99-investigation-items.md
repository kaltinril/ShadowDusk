# Phase 99 — Investigation Items (post-review backlog)

**Status: 🔬 Open (investigation items).** Created 2026-06-13. This phase holds **real
items that need their own scoped, validated effort** rather than a quick fix — surfaced by
the 2026-06-13 multi-agent project review. The safe/mechanical review findings were already
applied (PR #80: docs, dead-code removal, the D3D reflection-table dedup, doc-comment
backfill). The two items below were **deliberately deferred** from that batch because each
either changes emitted output (so it is a fidelity decision, not a fix) or is large-enough
core surgery that the byte-identity guard alone would not catch every regression.

These are **post-1.0, non-blocking.** Each item starts with an INVESTIGATION step that must
conclude before any code changes; the conclusion decides whether (and how) to implement.

> Distinct from [Phase 100](PHASE-100-deferred-backlog.md), which is the *retired* historical
> deferral bucket (closed; do not add there). Phase 99 is the live home for new
> investigation-grade backlog items.

---

## INV-1 — Per-pass shader memoization (investigate whether mgfxc dedups shared entry points)

**Track:** efficiency / fidelity. **Source:** project review (efficiency dimension), finding #5.

**Observation.** `CompilationPipeline.Run` compiles each pass's vertex/pixel entry points
independently, with no `(entryPoint, stage)` cache. An effect that reuses the same VS or PS
across many techniques/passes pays the full compile cost (DXC + SPIRV-Cross + rewrite on GL,
or vkd3d + `RdefReader` on DX) once **per reference**. This became materially relevant after
Phase 41's GAP-1 fix: `BasicEffect.fx` (now compilable on DirectX) declares **32 techniques**
that share a small set of VS/PS functions, so it recompiles the same entry points dozens of
times. The review proposed memoizing compiled blobs in a `Dictionary<(string,ShaderStage),int>`
and reusing the blob index on a hit.

**Why this is an INVESTIGATION, not a fix.** The review claimed "mgfxc dedups this way, so it
also tightens fidelity." That premise is **unverified and may be false**, and it matters
because deduping the shader table **changes the emitted `.mgfx`** (fewer shader entries,
re-pointed pass indices). Counter-evidence: the byte-identity manifest already matches the
real mgfxc goldens for the multi-pass / multi-technique fixtures **without** deduping, which
strongly suggests mgfxc does **not** dedup the shader table either. If so, deduping the
*output* would make ShadowDusk **diverge** from mgfxc, i.e. a fidelity regression, not a win.

**Investigation step (do first).** Determine empirically whether `mgfxc` shares one shader-
table entry across passes that reference the same entry point, or emits a separate entry per
reference. Concretely: compile a known shared-entry effect with the real `mgfxc` (e.g. the
now-compilable `BasicEffect.fx`, or `multipass.fx` / `multitechnique.fx` if they share
entries) and inspect the golden's shader table with `MgfxBlobReader`; compare to ShadowDusk's
current output. Record the finding in this doc.

**Decision tree.**
- **If mgfxc DEDUPS** -> memoization is both a fidelity and a perf win. Implement the blob
  cache so the shader table is deduped, regenerate the affected goldens/manifest **only after**
  confirming the new output matches mgfxc, and validate at the full ladder (golden structural
  match + byte-identity manifest + render where a runtime exists).
- **If mgfxc does NOT dedup** -> ShadowDusk's current per-reference output is the faithful one.
  A cache may still reuse the compiled *blob* internally for speed, but it **must still emit
  the duplicate shader-table entries** so output stays byte-identical (perf-only, zero output
  change), or the item is closed as not-worth-it. Byte-identity manifest must show **zero
  churn** in this branch.

**Acceptance.** A recorded conclusion on mgfxc's behavior, and either (a) a validated
output-changing dedup with regenerated-and-justified goldens, or (b) an output-identical
internal cache proven by zero manifest churn, or (c) closed with rationale.

---

## INV-2 — Decompose the two giant pipeline methods

**Track:** maintainability / readability. **Source:** project review (readability dimension), finding #7.

**Observation.** Two methods at the heart of the compiler are very large and hard to follow:
- `CompilationPipeline.Run` (`src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs`) — ~600
  lines, 4-5 nesting levels, one ~540-line `try/finally`.
- `MonoGameGlslRewriter.Rewrite` (`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`) — ~540 lines
  of inline "Rule 1..8" blocks with a mid-method `return` (~line 583) splitting the VS and PS
  handling. (Note: Phase 41's GAP-1 macro-technique work added to this method, so it has grown.)

**Goal.** Behavior-preserving extraction: pull named stages out of `Run` (e.g.
`PreParseAndPreprocess`, `CompileAndReflectTechniques`, `BuildGlConstantBufferRecords`,
`BakeSamplerStates`) and promote each "Rule N" block in `Rewrite` to a named method, so the
top level reads as a pipeline. **No behavior or output change.**

**Why this is an INVESTIGATION/scoped-effort, not a quick fix.** This is 1,100+ lines of
core-pipeline surgery. The byte-identity manifest would catch *output* changes, but **not
every subtle logic regression** (variable scoping, the early-return VS/PS split semantics,
shared mutable state across the extracted stages) if a code path is not exercised by the
corpus. It needs to be done incrementally with validation between steps.

**Approach.** (1) First assess branch/path coverage of the corpus over both methods; add
targeted fixtures/tests for any unexercised path before refactoring it. (2) Extract one
method at a time, running the **full suite + byte-identity manifest (zero churn)** after each
extraction. (3) Keep each "why" comment with its block as it moves. Stop and reassess if any
extraction is not provably behavior-neutral.

**Acceptance.** Both methods decomposed into readable named stages; full suite green;
byte-identity manifest zero churn; no `.mgfx` output change for any fixture.

---

## Definition of Done (phase)

Each item above is either implemented behind its own validated change (with the stated
acceptance met) or closed in-place with a recorded rationale. Neither blocks v1.0.
