---
name: release
description: "Cut a ShadowDusk release: bump the single centralized version, update CHANGELOG/RELEASING, audit docs, build+test, commit, push, PR, wait for CI, merge, then trigger the NuGet publish. Trigger on 'release', 'cut a release', 'bump version', 'new version', or /release."
argument-hint: "<version> (e.g., 0.2.0)"
---

# Release

Automate the full ShadowDusk release from version bump through PR merge to publish trigger.

A release publishes **all eight** `ShadowDusk.*` packages (`Core`, `HLSL`, `GLSL`, `ShaderToy`,
`Compiler`, `Cli`, `Wasm`, `MgcbPlugin`) plus the `ShadowDuskCLI` `dotnet tool` to nuget.org, and attaches
self-contained CLI binaries to a GitHub Release. The human runbook this automates is `RELEASING.md`.

## Input
`$ARGUMENTS` is the version (e.g., `0.2.0`). If omitted, read the current `<Version>` from
`Directory.Build.props` and ask the user for the new one.

**Choosing the version bump** (0.y.z, pre-1.0 — verified against the actual CHANGELOG.md
history, not just SemVer's letter):
- **MINOR** (`0.X.0`) — the release ships a genuine new capability: a new backend/target,
  a new frontend, a new consumer-facing feature.
- **PATCH** (`0.X.Y`) — fixes/maintenance only: bug fixes, doc corrections, CI/tooling
  reliability. A patch release can still carry a small accompanying addition (a README, a
  regression fixture) as long as the headline is a fix, not a feature.
- **MAJOR** (1.0.0) is a deliberate decision the owner makes, not a numbering consequence of
  accumulated features — see `plan/plan.md`'s named v1.0-gate phases for what "ready" means.
- If a release doesn't cleanly fit one bucket, ask the user rather than guessing.

## Steps

1. **Validate clean tree.** `git status`; if dirty, warn and stop. Show current `<Version>`
   from `Directory.Build.props`. Then `git checkout main && git pull` so the gates in step 2
   run against exactly the code that is about to ship.
2. **RENDER GATES FIRST — DX / FNA / KNI / Vulkan (required; CI cannot do this).** Run BEFORE
   the version bump: it is the longest and likeliest step to fail, it is the only proof of the
   actual product promise, and `release.yml` does not check it, so a divergence must stop the
   release before any version churn, commit, PR, or CI time is spent.

   **Run these from PowerShell 7 (`pwsh`), not Windows PowerShell 5.1.** The script shells out
   to `tools/restore.ps1`, which carries `#requires -Version 7.0`; under 5.1 the run dies at the
   restore step with a `ScriptRequiresUnmatchedPSVersion` error that looks nothing like a render
   divergence.

   ```powershell
   ./validation/run-windows-render-gates.ps1              # DX corpus + DX-modern (VTF) + DX Apos gallery + DX12 corpus + DX12 VS-driven/Apos gallery + KNI-DX + KNI-GL desktop + KNI-GL VS-driven + GL Apos + GL Apos gallery + ANGLE derivative probe + MGCB plugin (real dotnet mgcb build) + BOTH Vulkan gates
   ./validation/run-windows-render-gates.ps1 -IncludeFna  # add FNA fx_2_0; include it when in doubt
   ./validation/run-windows-render-gates.ps1 -SkipVulkan  # ONLY on a box with no Vulkan-capable GPU
   ```

   Other switches the script accepts: `-IncludeVulkan` (accepted as a no-op; Vulkan is default-ON
   since issue #145) and `-SkipRestore`.

   **`RELEASING.md` → Prerequisites item 4 is the authoritative description** of what these
   gates cover, which switches apply, and why CI cannot replace them — read it there rather
   than duplicating it here. This step is the procedure:
   - **Stop on a non-zero exit.** Report which gate diverged and hand back to the user. Do not
     proceed to step 3.
   - **Stop if the box is not Windows or has no GPU** (and, for the Vulkan gates, no Vulkan
     GPU). Say so plainly. Do not release on `dotnet test` alone.
   - Record which gates ran and their pass counts; put that in the PR body at step 10. It is
     the evidence CI structurally cannot produce.
3. **Branch.** `git checkout -b version/<version>` (main is already current from step 1).
4. **Bump version.** Edit `Directory.Build.props` `<Version>` only — the single source of
   truth. Do **NOT** touch the six `src/ShadowDusk.*/*.csproj` files; they no longer carry a
   version (the `<Version>` flows to all of them). Do **NOT** touch the
   `<PackageVersion Include=… />` items in `Directory.Packages.props` — those are unrelated
   Central Package Management dependency pins.
5. **Update CHANGELOG.md.** Move `[Unreleased]` → `## [<version>] - <today YYYY-MM-DD>`;
   leave a fresh empty `[Unreleased]` (with empty `### Added` / `### Changed` / `### Fixed`).
   If Unreleased is empty, add "- Version bump and documentation updates". Update the
   bottom-of-file compare/release link references.
6. **Update RELEASING.md** version examples to `<version>`.
7. **Docs audit (Explore agent, report-only — do NOT auto-fix).** Audit against the actual
   code, in two parts:
   - **The support-surface checklist (CLAUDE.md → "Support-surface docs are part of the
     change") — check EVERY item by name; this is the backstop for the same-PR rule:**
     `docs/pipeline-overview.puml` **+ whether `docfx/images/pipeline-overview.svg` was
     regenerated after the last `.puml` edit**; `docs/the-purpose.md` (backend table +
     host × target matrix); `docs/validation-matrix.md` (cells, §6 driver list — one row
     per `validation/*` driver on disk — and §7 gap rows); `docs/repository-layout.md`;
     `README.md` (supported-targets table + pipeline block); the DocFX site
     (`docfx/index.md`, `getting-started/overview.md`, `guides/choosing-a-target.md`,
     `backends/*.md`, `contributing/validation.md` rung-4 list, `glossary.md`, and the
     transcluded `docs/references/compilation-pipeline.md` + `docs/glsl-uniform-naming.md`
     rewriter-rule table + `docs/error-codes.md`, which `docfx/diagnostics.md` transcludes —
     every code raised in `src/` must have a row, and every documented code must still be
     raised); `docs/test-shader-corpus.md` (fixture/corpus counts and the
     last-updated date — touched in 0.8.0 and 0.12.1 for exactly this); `CLAUDE.md` (Project
     Overview + HARD-RULE gate commands); **the render-gate commands in THIS skill's step 2**
     (they drift whenever a driver or a switch changes — that is exactly how `-IncludeVulkan`
     went stale); `plan/plan.md` phase-index rows vs each phase doc's own Status line (a Done
     phase must sit in `plan/DONE/` with its row flipped) — AND whether the phase doc's own
     body needs a tracker refresh reflecting what actually shipped, not just its Status line
     (0.11.0 refreshed Phase 50's own trackers, not only its plan.md row); public-API XML
     doc-comments that claim "not yet implemented" for shipped features.
   - **The packaging surfaces:** each packable csproj's `<Description>` / `<PackageTags>`,
     the CLI README, and the WASM HOWTO if present.
   - **If this release adds a new published package, a new required native dependency, or a
     new platform/target** (the class of change 0.9.0's ShaderToy promotion and 0.11.0's
     Android natives both were): also check `.github/workflows/release.yml` (the pack-job
     list and any package-count validation gate), `.github/workflows/pack-consume.yml` (native-
     presence gates), `Brand/README.md`, and every "eight packages" / package-count mention in
     `CLAUDE.md` and `RELEASING.md` — these encode a specific count and silently go stale
     otherwise.
   Report gaps; ask whether to fix now or defer. Do not block the release on doc drift
   unless the user says so.
8. **Build + test.**
   `dotnet build ShadowDusk.slnx -c Release` then
   `dotnet test ShadowDusk.slnx -c Release --no-build --settings ShadowDusk.runsettings`
   (the runsettings carry the 5-min `TestSessionTimeout` — see CLAUDE.md Phase 21, matching
   the `/test` skill). Stop on failure. **This run regenerates
   `plan/PHASE-41-appendix/structural-divergence-matrix.md`'s "ShadowDusk version:" stamp to
   the new version** (`Phase41StructuralDivergenceMatrixTests` reads it off the built
   assembly) — check `git status` after this step and carry that file into step 9. Two of the
   last eight releases (0.12.0, 0.13.0) missed this and needed a separate follow-up commit;
   don't repeat it.
9. **Commit.** Stage the release files (`Directory.Build.props`, `CHANGELOG.md`,
   `RELEASING.md`, the regenerated `structural-divergence-matrix.md` from step 8, and any doc
   fixes the user approved). Use a conventional message such as `chore(release): <version>`.
   Per CLAUDE.md Git Commit Conventions, the commit carries **NO `Co-Authored-By` trailer of
   any kind** (not Claude/Anthropic/Opus, not the user) and **no "Generated with Claude Code" /
   tool-attribution line**. There is no `/commit` skill here — commit directly with
   `git commit`.
10. **Push + PR.** `git push -u origin version/<version>`; `gh pr create` with a
   summary-bullets body (what changed at this version). No test-plan section and no
   tool-attribution footer in the PR body.
11. **Wait for PR CI.** `gh pr checks <pr> --watch`. Do **not** merge on red. Local green is
    not enough — CI runs the 3-OS matrix (`ci.yml`).
12. **Merge.** `gh pr merge <pr> --merge`.
13. **Wait for post-merge main CI**, then tell the user to trigger the publish via
    **Actions → Release → Run workflow** with version `<version>` (no leading `v`).

    **`release.yml` is dispatch-ONLY — there is no tag-push trigger.** Do not tell the user to
    `git tag && git push` instead: that publishes nothing while looking like it worked. The
    workflow creates and pushes the `v<version>` tag itself on a successful run; a tag is a
    marker, never a trigger. (`RELEASING.md` → "Triggering a release" is authoritative.)

    The `validate` job checks the dispatch input against `Directory.Build.props` `<Version>`; if
    they match, all eight packages + the `ShadowDuskCLI` tool publish to nuget.org and a GitHub
    Release is cut. Point the user at `RELEASING.md` → "Verify after release" for the post-publish
    checks (`dotnet tool install -g ShadowDusk.Cli` → `ShadowDuskCLI --help`, and all eight packages on
    nuget.org at `<version>`).

## ShadowDusk-specific notes

- **One file bumps the version** — `Directory.Build.props` `<Version>` only. Never the eight
  csprojs.
- **Commit directly, no `/commit` skill, no co-author / tool-attribution trailer of any
  kind** (CLAUDE.md Git Commit Conventions).
- **Tests pass `--settings ShadowDusk.runsettings`** (the Phase 21 suite-timeout guardrail),
  matching the `/test` skill.
- **The release-build test run regenerates `structural-divergence-matrix.md`'s version
  stamp — it must ride in the same commit as the version bump**, not a follow-up (step 8).
- **A new package/native/target in this release means checking `release.yml`,
  `pack-consume.yml`, `Brand/README.md`, and every hardcoded package-count mention too**
  (step 7's audit) — not just the usual docs list.
- **The Windows render gate (step 2) is not optional and CI cannot replace it.** The DX / FNA /
  KNI-DX rung-4 render proofs run only on a Windows+GPU box (`validation/run-windows-render-gates.ps1`);
  a green `dotnet test` + green CI does NOT cover them. Skipping it can ship a silently broken
  render against the "renders like `mgfxc`/`fxc`" promise.
- **The publish trigger is the DISPATCH-ONLY `release.yml` workflow** whose `validate` job
  guards the dispatch input against the centralized `<Version>`. Pushing a `v<version>` tag
  triggers nothing; the workflow pushes that tag itself on success.
- **Run the render gates under `pwsh` (PowerShell 7)**, not Windows PowerShell 5.1 — the
  `#requires -Version 7.0` in `tools/restore.ps1` aborts the run with an error that does not
  resemble a render failure.

## Edge cases

- **Dirty tree** → stop; ask the user to commit or stash first.
- **Branch already exists** → ask before reusing or recreating it.
- **Empty `[Unreleased]`** → add a minimal "Version bump and documentation updates" entry.
- **Merge conflict** → stop; do not force-push or `--force` anything.
- **CI red** → stop at the wait step; do not merge.
