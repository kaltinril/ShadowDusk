---
name: release
description: "Cut a ShadowDusk release: bump the single centralized version, update CHANGELOG/RELEASING, audit docs, build+test, commit, push, PR, wait for CI, merge, then trigger the NuGet publish. Trigger on 'release', 'cut a release', 'bump version', 'new version', or /release."
argument-hint: "<version> (e.g., 0.2.0)"
---

# Release

Automate the full ShadowDusk release from version bump through PR merge to publish trigger.

A release publishes **all seven** `ShadowDusk.*` packages (`Core`, `HLSL`, `GLSL`, `ShaderToy`,
`Compiler`, `Cli`, `Wasm`) plus the `ShadowDuskCLI` `dotnet tool` to nuget.org, and attaches
self-contained CLI binaries to a GitHub Release. The human runbook this automates is `RELEASING.md`.

## Input
`$ARGUMENTS` is the version (e.g., `0.2.0`). If omitted, read the current `<Version>` from
`Directory.Build.props` and ask the user for the new one.

## Steps

1. **Validate clean tree.** `git status`; if dirty, warn and stop. Show current `<Version>`
   from `Directory.Build.props`. Then `git checkout main && git pull` so the gates in step 2
   run against exactly the code that is about to ship.
2. **RENDER GATES FIRST — DX / FNA / KNI / Vulkan (required; CI cannot do this).** Run BEFORE
   the version bump: it is the longest and likeliest step to fail, it is the only proof of the
   actual product promise, and `release.yml` does not check it, so a divergence must stop the
   release before any version churn, commit, PR, or CI time is spent.

   ```powershell
   ./validation/run-windows-render-gates.ps1              # DX + DX-modern + KNI-DX + KNI-GL x2 + ANGLE + both Vulkan gates
   ./validation/run-windows-render-gates.ps1 -IncludeFna  # add FNA fx_2_0; include it when in doubt
   ```

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
     rewriter-rule table); `CLAUDE.md` (Project Overview + HARD-RULE gate commands);
     **the render-gate commands in THIS skill's step 2** (they drift whenever a driver or a
     switch changes — that is exactly how `-IncludeVulkan` went stale); `plan/plan.md`
     phase-index rows vs each phase doc's own Status line (a Done phase must sit in
     `plan/DONE/` with its row flipped); public-API XML doc-comments that claim "not yet
     implemented" for shipped features.
   - **The packaging surfaces:** each packable csproj's `<Description>` / `<PackageTags>`,
     the CLI README, and the WASM HOWTO if present.
   Report gaps; ask whether to fix now or defer. Do not block the release on doc drift
   unless the user says so.
8. **Build + test.**
   `dotnet build ShadowDusk.slnx -c Release` then
   `dotnet test ShadowDusk.slnx -c Release --no-build --settings ShadowDusk.runsettings`
   (the runsettings carry the 5-min `TestSessionTimeout` — see CLAUDE.md Phase 21, matching
   the `/test` skill). Stop on failure.
9. **Commit.** Stage the release files only (`Directory.Build.props`, `CHANGELOG.md`,
   `RELEASING.md`, and any doc fixes the user approved). Use a conventional message such as
   `chore(release): <version>`. Per CLAUDE.md Git Commit Conventions, the commit carries
   **NO `Co-Authored-By` trailer of any kind** (not Claude/Anthropic/Opus, not the user) and
   **no "Generated with Claude Code" / tool-attribution line**. There is no `/commit` skill
   here — commit directly with `git commit`.
10. **Push + PR.** `git push -u origin version/<version>`; `gh pr create` with a
   summary-bullets body (what changed at this version). No test-plan section and no
   tool-attribution footer in the PR body.
11. **Wait for PR CI.** `gh pr checks <pr> --watch`. Do **not** merge on red. Local green is
    not enough — CI runs the 3-OS matrix (`ci.yml`).
12. **Merge.** `gh pr merge <pr> --merge`.
13. **Wait for post-merge main CI**, then trigger publish. Tell the user to either:
    - run **Actions → Release → Run workflow** with version `<version>`, **or**
    - `git tag v<version> && git push origin v<version>`.

    The `validate` job checks the tag/input against `Directory.Build.props` `<Version>`; if
    they match, all seven packages + the `mgfxc` tool publish to nuget.org and a GitHub Release
    is cut. Point the user at `RELEASING.md` → "Verify after release" for the post-publish
    checks (`dotnet tool install -g ShadowDusk.Cli` → `ShadowDuskCLI --help`, and all seven packages on
    nuget.org at `<version>`).

## ShadowDusk-specific notes

- **One file bumps the version** — `Directory.Build.props` `<Version>` only. Never the six
  csprojs.
- **Commit directly, no `/commit` skill, no co-author / tool-attribution trailer of any
  kind** (CLAUDE.md Git Commit Conventions).
- **Tests pass `--settings ShadowDusk.runsettings`** (the Phase 21 suite-timeout guardrail),
  matching the `/test` skill.
- **The Windows render gate (step 2) is not optional and CI cannot replace it.** The DX / FNA /
  KNI-DX rung-4 render proofs run only on a Windows+GPU box (`validation/run-windows-render-gates.ps1`);
  a green `dotnet test` + green CI does NOT cover them. Skipping it can ship a silently broken
  render against the "renders like `mgfxc`/`fxc`" promise.
- **The publish trigger is the tag-or-dispatch `release.yml` workflow** whose `validate` job
  guards the tag against the centralized `<Version>`.

## Edge cases

- **Dirty tree** → stop; ask the user to commit or stash first.
- **Branch already exists** → ask before reusing or recreating it.
- **Empty `[Unreleased]`** → add a minimal "Version bump and documentation updates" entry.
- **Merge conflict** → stop; do not force-push or `--force` anything.
- **CI red** → stop at the wait step; do not merge.
