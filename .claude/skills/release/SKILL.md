---
name: release
description: "Cut a ShadowDusk release: bump the single centralized version, update CHANGELOG/RELEASING, audit docs, build+test, commit, push, PR, wait for CI, merge, then trigger the NuGet publish. Trigger on 'release', 'cut a release', 'bump version', 'new version', or /release."
argument-hint: "<version> (e.g., 0.2.0)"
---

# Release

Automate the full ShadowDusk release from version bump through PR merge to publish trigger.

A release publishes **all six** `ShadowDusk.*` packages (`Core`, `HLSL`, `GLSL`, `Compiler`,
`Cli`, `Wasm`) plus the `ShadowDuskCLI` `dotnet tool` to nuget.org, and attaches self-contained CLI
binaries to a GitHub Release. The human runbook this automates is `RELEASING.md`.

## Input
`$ARGUMENTS` is the version (e.g., `0.2.0`). If omitted, read the current `<Version>` from
`Directory.Build.props` and ask the user for the new one.

## Steps

1. **Validate clean tree.** `git status`; if dirty, warn and stop. Show current `<Version>`
   from `Directory.Build.props`.
2. **Branch from latest main.** `git checkout main && git pull && git checkout -b version/<version>`.
3. **Bump version.** Edit `Directory.Build.props` `<Version>` only — the single source of
   truth. Do **NOT** touch the six `src/ShadowDusk.*/*.csproj` files; they no longer carry a
   version (the `<Version>` flows to all of them). Do **NOT** touch the
   `<PackageVersion Include=… />` items in `Directory.Packages.props` — those are unrelated
   Central Package Management dependency pins.
4. **Update CHANGELOG.md.** Move `[Unreleased]` → `## [<version>] - <today YYYY-MM-DD>`;
   leave a fresh empty `[Unreleased]` (with empty `### Added` / `### Changed` / `### Fixed`).
   If Unreleased is empty, add "- Version bump and documentation updates". Update the
   bottom-of-file compare/release link references.
5. **Update RELEASING.md** version examples to `<version>`.
6. **Docs audit (Explore agent, report-only — do NOT auto-fix).** Check the `CLAUDE.md`
   inventory (repo layout, the backend table, "What success actually means"), the root and
   CLI READMEs, the DocFX site (Phase 26) and WASM HOWTO if present, and each packable
   csproj's `<Description>` / `<PackageTags>` against the actual code. Report gaps; ask
   whether to fix now or defer. Do not block the release on doc drift unless the user says so.
7. **Build + test.**
   `dotnet build ShadowDusk.slnx -c Release` then
   `dotnet test ShadowDusk.slnx -c Release --no-build --settings ShadowDusk.runsettings`
   (the runsettings carry the 5-min `TestSessionTimeout` — see CLAUDE.md Phase 21, matching
   the `/test` skill). Stop on failure.
8. **Commit.** Stage the release files only (`Directory.Build.props`, `CHANGELOG.md`,
   `RELEASING.md`, and any doc fixes the user approved). Use a conventional message such as
   `chore(release): <version>`. Per CLAUDE.md Git Commit Conventions, the commit carries
   **NO `Co-Authored-By` trailer of any kind** (not Claude/Anthropic/Opus, not the user) and
   **no "Generated with Claude Code" / tool-attribution line**. There is no `/commit` skill
   here — commit directly with `git commit`.
9. **Push + PR.** `git push -u origin version/<version>`; `gh pr create` with a
   summary-bullets body (what changed at this version). No test-plan section and no
   tool-attribution footer in the PR body.
10. **Wait for PR CI.** `gh pr checks <pr> --watch`. Do **not** merge on red. Local green is
    not enough — CI runs the 3-OS matrix (`ci.yml`).
11. **Merge.** `gh pr merge <pr> --merge`.
12. **Wait for post-merge main CI**, then trigger publish. Tell the user to either:
    - run **Actions → Release → Run workflow** with version `<version>`, **or**
    - `git tag v<version> && git push origin v<version>`.

    The `validate` job checks the tag/input against `Directory.Build.props` `<Version>`; if
    they match, all six packages + the `mgfxc` tool publish to nuget.org and a GitHub Release
    is cut. Point the user at `RELEASING.md` → "Verify after release" for the post-publish
    checks (`dotnet tool install -g ShadowDusk.Cli` → `ShadowDuskCLI --help`, and all six packages on
    nuget.org at `<version>`).

## ShadowDusk-specific notes

- **One file bumps the version** — `Directory.Build.props` `<Version>` only. Never the six
  csprojs.
- **Commit directly, no `/commit` skill, no co-author / tool-attribution trailer of any
  kind** (CLAUDE.md Git Commit Conventions).
- **Tests pass `--settings ShadowDusk.runsettings`** (the Phase 21 suite-timeout guardrail),
  matching the `/test` skill.
- **The publish trigger is the tag-or-dispatch `release.yml` workflow** whose `validate` job
  guards the tag against the centralized `<Version>`.

## Edge cases

- **Dirty tree** → stop; ask the user to commit or stash first.
- **Branch already exists** → ask before reusing or recreating it.
- **Empty `[Unreleased]`** → add a minimal "Version bump and documentation updates" entry.
- **Merge conflict** → stop; do not force-push or `--force` anything.
- **CI red** → stop at the wait step; do not merge.
