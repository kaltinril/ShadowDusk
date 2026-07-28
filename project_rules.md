# Project Rules

How to work on this project. One short rule per line. These OVERRIDE default behavior.

> The rules that must fire **without anyone opening a file** stay in [CLAUDE.md](CLAUDE.md) and are deliberately NOT repeated here: the purpose and the evidence ladder, the seamless-for-the-consumer and backwards-compatibility directives, the pre-merge/pre-release gate commands, the support-surface docs checklist, the **C# coding conventions**, the git commit conventions, and the no-local-memory directive. Every rule has exactly one home.

## Never

- Never swap in a substitute compiler to make a host work; a different compiler means different output and silently breaks the mgfxc-equivalence promise.
- Never fork or own compiler internals; fail loudly, patch minimally and reversibly on our side of the boundary (the `D3d9BytecodePatcher` pattern), and record an upstream-fix follow-up.
- Never bump a pinned native version casually; pins exist because output byte-stability is a product promise, and a bump re-baselines every golden and re-runs rung 4.
- Never commit native binaries; they are restored, pinned, and hash-verified.
- Never destroy a background agent's uncommitted output; commit or copy out first, clean up last, and verify a "done" claim by re-running its gate rather than trusting an estimate. (operator)
- Never add a `PackageVersion` **property** to a csproj; the ShadowDusk version lives only in `Directory.Build.props`.
- Never swallow or reformat a compiler diagnostic; surface file, line, column, and the underlying compiler's own message text.
- Never require a native toolchain, Wine, or a Windows SDK on the consumer's machine; every native piece rides inside the NuGet package.

## Testing and validation

- A green `dotnet test` is necessary but NOT sufficient for any change to shader output, transpilation, the MGFX/KNIFX/FNA writers, render state, or matrix handling; the render gates in CLAUDE.md are the other half of the bar.
- Run the full `dotnet test ShadowDusk.slnx`, never a filtered subset, when touching the parser, pre-parser, writers, or render state; a whole class of valid HLSL can fail to compile while every test you happened to run stays green (this is how issue #106 escaped).
- Every fixed bug earns a permanent regression fixture or test so it can never silently return.
- Compare same-backend only (GL against GL, DX against DX); a green OpenGL result says nothing about DirectX.
- A test that cannot fail is worse than no test: a soft-skip reported as PASS is indistinguishable from real coverage. `SHADOWDUSK_REQUIRE_GL` exists because headless ImageTests soft-skipped as passes and masked three latent failures, including a GLFW registration race on real Windows hosts.
- A corpus that cannot see a bug class proves nothing about it: PS-only, matrix-free, modern-syntax-only corpora hid issue #70 and issue #145 entirely. Widening the corpus is part of the fix, not a follow-up.
- A green proxy is never the bar; unit tests, structural checks, and our own renderer can all pass while the real-runtime promise is unmet.
- Unit tests stay pure (no disk, no process); integration tests are tagged `[Trait("Category","Integration")]`.
- No `Thread.Sleep` in tests; use `CancellationToken` with reasonable timeouts.
- Treat a slow integration run as environmental (antivirus scanning cold natives) before treating it as algorithmic; see `docs/integration-test-performance.md`.

## Docs and phases

- Keep XML doc-comments on public API accurate; they render into the published API reference, so a stale "not yet implemented" ships to the site.

- Read `plan/plan.md` first for phase status; each phase's own doc is the detail.
- When a phase completes, move its doc and appendix to `plan/DONE/` and fix relative links in the moved doc and in every referrer.
- Update the phase index row in `plan/plan.md` in the same commit as the status change.
- Add the change to the `[Unreleased]` section of `CHANGELOG.md` in the same PR.
- Regenerate `docfx/images/pipeline-overview.svg` whenever `docs/pipeline-overview.puml` changes; the site embeds the SVG, so an un-regenerated SVG silently ships the old diagram.
- `docs/validation-matrix.md` is the living tracker: update the cell and its date whenever evidence changes, and give every new `validation/*` driver a §6 row with its exact run command.
- Keep `.claude/skills/` and `.claude/agents/` current when paths or conventions change.

## Working practice

- While chasing a stated backend or target-completion goal, fix bugs found along the way instead of stopping to ask; only a genuine scope or backwards-compatibility question warrants a check-in. (operator)
- Work on a branch off `main`; commit or push only when asked.
- Use `/release` to cut a release; `RELEASING.md` is the human runbook and the ground truth it follows.
- Bump the single `<Version>` line in `Directory.Build.props`, merge that PR to `main`, and only then dispatch `release.yml`; the workflow is dispatch-only and a pushed tag publishes nothing.
- Run the Windows render gate before bumping the version, not after: it is the longest and most likely step to fail, so a divergence should stop the release before any version churn.

## Maintaining the three source-of-truth files

- `project_facts.md`, `project_rules.md`, and `project_decisions.md` are the only home for durable facts, rules, and decisions. Do not create memory files. Exceptions that stay separate: phase docs, user-facing docs and readmes, reference docs, temporary files.
- Update them in the same commit as the change that alters them.
- Edit in place and delete entries that become false; never append changelogs or dated progress notes, since history lives in git.
- When a conversation surfaces a new durable fact, a correction to behavior, or a resolved choice, write it to the right file immediately without being asked.
- Facts state what is true; rules state how to work; decisions state what was chosen and why. An entry with no rejected alternative and no reason is a fact, not a decision.
- Record only what reading the code cannot tell you; folder layout, file names, diagnostic codes, and format constants are derivable and belong in the code or `docs/repository-layout.md`.
- Never record per-target **proof status** in these three files; `docs/validation-matrix.md` is its only home (CLAUDE.md carries a cold-start summary table that the support-surface rule keeps in sync). Status changes as evidence advances, so a second copy always drifts.
