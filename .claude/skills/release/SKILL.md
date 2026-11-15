---
name: release
description: "Cut a ShadowDusk release: bump the single centralized version, update CHANGELOG/RELEASING, audit docs, commit, push, PR, wait for CI, merge, then trigger the NuGet publish. Trigger on 'release', 'cut a release', 'bump version', 'new version', or /release."
argument-hint: "<version> (e.g., 0.2.0)"
---

# Release

Automate the full ShadowDusk release from version bump through PR merge to publish trigger.

A release publishes **all eleven** `ShadowDusk.*` packages (`Core`, `HLSL`, `GLSL`, `ShaderToy`,
`SkSL`, `Slang`, `Compiler`, `Cli`, `Wasm`, `MgcbPlugin`, `ContentPipeline`) plus the `ShadowDuskCLI` `dotnet tool` to nuget.org, and attaches
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
   actual product promise, and `release.yml` checks only the CI-run render workflow, not this GPU gate, so a divergence must stop the
   release before any version churn, commit, PR, or CI time is spent.

   **Run these from PowerShell 7 (`pwsh`), not Windows PowerShell 5.1.** The script shells out
   to `tools/restore.ps1`, which carries `#requires -Version 7.0`; under 5.1 the run dies at the
   restore step with a `ScriptRequiresUnmatchedPSVersion` error that looks nothing like a render
   divergence.

   ```powershell
   ./validation/run-windows-render-gates.ps1              # DX corpus (vkd3d + d3dcompiler arms) + DX-modern (VTF, legacy-register, half-globals, legacy texture-slot and legacy-lod arms) + DX Apos gallery + DX resource arrays (issues #339/#340: texture-array table + render, sampler-array SD0224 refusal) + DX ShaderToy route + DX12 corpus + DX12 managed-DXIL-signature (ManagedDxilDx12, #478) + DX12 VS-driven/Apos gallery + DX12 texture-array row (issue #324) + DX12 legacy-lod + KNI-DX (incl. legacy texture slots) + KNI-GL desktop + KNI-GL VS-driven + Skia vs KNI (SkSL, issue #369) + SkSL input vs GL (SkSLVsGl, Phase 71) + non-square matrix Set on WindowsDX and DesktopVK (issue #423) + GL vertex semantics + matrix interpolants (VertexSemanticsGl, VertexSemanticsKniGl) + GL Apos + GL Apos gallery + ANGLE-D3D11 derivative probe (issue #136) + MGCB plugin (real dotnet mgcb 3.8.4.1, 3.8.5 AND 3.8.6, decoy-PATH DXC guard) + XNB direct writer Content.Load on MonoGame WindowsDX, MonoGame DesktopGL and KNI 4.2.9001+4.3.9001 + MonoGame 3.8.5 Content Builder (ShadowDusk.ContentPipeline, real ContentBuilder + Content.Load<Effect>) + Slang corpus + Slang full corpus (ShadowDusk.Slang real-slangc route; DX11, DX12 and Vulkan real-Effect arms, issue #230) + Slang textured GL (issue #252) + the Vulkan block (CandidateVulkan, InitializerDefaultsVulkan, NonSquareMatrixVulkan, KniEffectTestVulkan, VsDrivenVulkan vs/apos/texarr/legacy-lod/regslots, SlangFullCorpusVulkan) + the six InitializerDefaults drivers (global initializer = default with no Set, every MGFX/KNIFX runtime) + the KNI EffectTest drivers (KNI's own EffectTest.cs on KNI 4.2.9001+4.3.9001 GL/DX and MonoGame GL/DX/DX12/Vulkan; gated + known-failing rows) + the newer-MonoGame block (every MonoGame gate again on 3.8.6 via -p:MonoGameVersion, plus the DesktopGL4 arm and the 3.8.6 Content Builder; -NewerMonoGameOnly runs just it), vs mgfxc/fxc
   ./validation/run-windows-render-gates.ps1 -IncludeFna  # add FNA fx_2_0 + its Slang arm + initializer defaults on D3D11 and OpenGL; include it when in doubt
   ./validation/run-windows-render-gates.ps1 -SkipVulkan  # ONLY on a box with no Vulkan-capable GPU
   ```

   Other switches the script accepts: `-IncludeVulkan` (accepted as a no-op; Vulkan is default-ON
   since issue #145), `-SkipRestore`, and `-NewerMonoGameOnly` (only the newer-MonoGame block).

   **`RELEASING.md` → Prerequisites item 5 is the authoritative description** of what these
   gates cover, which switches apply, and why CI cannot replace them — read it there rather
   than duplicating it here. This step is the procedure:
   - **Stop on a non-zero exit.** Report which gate diverged and hand back to the user. Do not
     proceed to step 3.
   - **Stop if the box is not Windows or has no GPU** (and, for the Vulkan gates, no Vulkan
     GPU). Say so plainly. Do not release on `dotnet test` alone.
   - Record which gates ran and their pass counts; put that in the PR body at step 9. It is
     the evidence CI structurally cannot produce.
3. **Branch.** `git checkout -b version/<version>` (main is already current from step 1).
4. **Bump version.** Run `tools/bump-version.sh <version>` (bash, repo root). It edits
   `Directory.Build.props` `<Version>` only (never the eleven `src/ShadowDusk.*/*.csproj` files, never
   the `<PackageVersion Include=... />` items in `Directory.Packages.props`), rewrites the
   `ShadowDusk.*` ranges in every `*packages*.lock.json`, moves `CHANGELOG.md` `[Unreleased]` into
   a dated `[<version>]` section with a fresh empty `[Unreleased]` and the link references (adds a
   "Version bump and documentation updates" entry if `[Unreleased]` was empty), updates the version
   examples in `RELEASING.md`, and runs `tools/check-lock-files.sh`. A non-zero exit stops the release.
   Review `git diff --stat`: only those files change.
5. **Docs audit, then FIX every finding before the release commit (owner directive,
   2026-10-29: "Always fix doc issues before the release.").** The audit itself can stay a
   separate read-only agent (Explore), auditing against the actual code, in the parts below;
   its findings are then fixed on the release branch, each claim verified against the code or
   CI, before step 8. The release does not proceed with known doc drift:
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
     presence gates), `Brand/README.md`, and every "nine packages" / package-count mention in
     `CLAUDE.md` and `RELEASING.md` — these encode a specific count and silently go stale
     otherwise.
   Fix every gap before step 8 (PR CI builds an XML doc comment or csproj change) and
   list the fixes in the PR body. Deferring a finding is not an option the release offers: if a
   fix truly cannot land now (it needs a measurement no one can run yet), stop and ask the user,
   and record the open item where the next person will find it (a `docs/validation-matrix.md`
   §7 row or an issue) rather than shipping the stale text.
8. **Commit.** Stage the release files (`Directory.Build.props`, the lock files, `CHANGELOG.md`,
   `RELEASING.md`, and the step 5 doc fixes). Use a conventional message such as `chore(release): <version>`.
   Per CLAUDE.md Git Commit Conventions, the commit carries **NO `Co-Authored-By` trailer of
   any kind** (not Claude/Anthropic/Opus, not the user) and **no "Generated with Claude Code" /
   tool-attribution line**. There is no `/commit` skill here — commit directly with
   `git commit`.
9. **Push + PR.** `git push -u origin version/<version>`; `gh pr create` with a
   summary-bullets body (what changed at this version). No test-plan section and no
   tool-attribution footer in the PR body.
10. **Wait for PR CI.** `gh pr checks <pr> --watch --fail-fast`. Do **not** merge on red. There
    is no local build or test: CI runs the full suite on the 3-OS matrix (`ci.yml`). If it fails
    `Phase41StructuralDivergenceMatrixTests` because `plan/PHASE-41-appendix/structural-divergence-matrix.md`
    is stale, regenerate it as `project_rules.md` says, review the diff, commit, and push.
11. **Merge.** `gh pr merge <pr> --merge`.
12. **Wait for post-merge main CI** (`ci.yml`, and `validation-render.yml` if it triggers; `release.yml`'s
    `verify-ci` job fails the release otherwise), then tell the user to trigger the publish via
    **Actions → Release → Run workflow** with version `<version>` (no leading `v`).

    **`release.yml` is dispatch-ONLY — there is no tag-push trigger.** Do not tell the user to
    `git tag && git push` instead: that publishes nothing while looking like it worked. The
    workflow creates and pushes the `v<version>` tag itself on a successful run; a tag is a
    marker, never a trigger. (`RELEASING.md` → "Triggering a release" is authoritative.)

    The `validate` job checks the dispatch input against `Directory.Build.props` `<Version>`; if
    they match, all eleven packages + the `ShadowDuskCLI` tool publish to nuget.org and a GitHub
    Release is cut. Point the user at `RELEASING.md` → "Verify after release" for the post-publish
    checks (`dotnet tool install -g ShadowDusk.Cli` → `ShadowDuskCLI --help`, and all eleven packages on
    nuget.org at `<version>`).

## ShadowDusk-specific notes

- **One file bumps the version** — `Directory.Build.props` `<Version>` only. Never the
  csprojs.
- **`release.yml` runs the pre-release workflows itself** (issue #483): `verify-ci` fails unless
  `ci.yml` and `validation-render.yml` are green on the release commit, and `pack-consume.yml`
  (`Pack & Consume Smoke`: the only cold-consumer proof, incl. slangc from an installed package),
  `win-arm64.yml` (issue #286) and `android-emulator.yml` (issue #304) are called with
  `workflow_call`; `nuget-push` needs all of them. Do not dispatch them by hand first. A new
  workflow that must gate a release gets a `workflow_call` trigger and a job in `release.yml`.
- **Commit directly, no `/commit` skill, no co-author / tool-attribution trailer of any
  kind** (CLAUDE.md Git Commit Conventions).
- **No local build or test in a release**: PR CI runs the full suite on three OSes. A stale
  `structural-divergence-matrix.md` fails `Phase41StructuralDivergenceMatrixTests` there (issue #361, step 10).
- **A new package/native/target in this release means checking `release.yml`,
  `pack-consume.yml`, `Brand/README.md`, and every hardcoded package-count mention too**
  (step 5's audit) — not just the usual docs list.
- **The Windows render gate (step 2) is not optional and CI cannot replace it.** The DX / FNA /
  KNI-DX rung-4 render proofs run only on a Windows+GPU box (`validation/run-windows-render-gates.ps1`);
  green CI does NOT cover them. Skipping it can ship a silently broken
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
