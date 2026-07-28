# ShadowDusk — Cross-Platform MonoGame Shader Compiler

## THE PURPOSE (read this first)

**The product is a drop-in `mgfxc` replacement: a self-contained library** a user adds to their **MonoGame/KNI project on Linux, macOS, or Windows**, that compiles **`.fx` → `.mgfx` in memory at runtime**, requiring **nothing but the library itself** — no `fxc.exe`, no `mgfxc`, no Wine, no Windows SDK, no native toolchain the user has to install separately. Its output **loads and renders identically to `mgfxc`'s** in the **real MonoGame/KNI runtime**. **One faithful compiler; the same `mgfxc`-equivalent result everywhere.**

The load-bearing distinctions — internalize these, they have drifted before (full detail, success criteria, evidence ladder, and backend table in **[docs/the-purpose.md](docs/the-purpose.md)**):

- **The library *is* the product.** The deliverable is the in-memory compiler called at runtime (`IShaderCompiler.CompileAsync(fx) → .mgfx bytes`). The **CLI** and **MGCB plugin** are *delivery shapes of the same library*; the **browser / WASM shader-fiddle is ONLY a sample of reach — never the product.** Don't let sample work redefine the goal.
- **One pipeline, everywhere — NO substitute compilers.** Every host runs the same faithful pipeline (HLSL →`[DXC]`→ SPIR-V →`[SPIRV-Cross]`→ GLSL →`[managed rewrite + MGFX writer]`→ `.mgfx`; or `vkd3d-shader` → DXBC for DirectX). A host must **not** swap in a different frontend/compiler to make a platform "work" — different compiler ⇒ different output ⇒ silently breaks the "identical to `mgfxc`" promise. If a faithful component can't run on a host yet, that host's runtime-compile is **not done** — never a licence to substitute.
- **"Self-contained" is a hard requirement.** Native pieces ride *inside* the NuGet package (transitive native assets), never a separate manual install. "Add the package, call the API" is the entire setup.
- **The bar is the real runtime, not our tests.** Only ShadowDusk's `.mgfx` loading in MonoGame's `Effect` and rendering like `mgfxc`'s proves the promise. Tests/our-own-renderer images are **proxies, not the bar**. Compare same-backend only (GL↔GL, DX↔DX), never cross-backend. "Same as `mgfxc`" = behaviorally equivalent + `Effect`-loadable, **NOT** byte-identical (that's a non-goal).

## Source-of-truth files

- **[project_facts.md](project_facts.md)** — what is TRUE (targets and how far each is proven, pins and natives, where things run, known gaps, vocabulary).
- **[project_rules.md](project_rules.md)** — how to WORK on it (testing bar, code conventions, docs/phase process, release mechanics).
- **[project_decisions.md](project_decisions.md)** — what was CHOSEN and why; consult before re-litigating anything.

**Do NOT create memory files, and do NOT rely on the machine-local agent memory store** (it is lost between computers). Every durable fact, rule, or decision goes in those three files — exceptions that stay separate: phase docs, user-facing docs/readmes, reference docs, temp files. Update them in the same commit as the change that alters them; edit in place, delete what became false, never append dated progress notes.

The rules below stay in this file *because they must fire without anyone opening another file*. They are not duplicated in `project_rules.md`.

## Repository Layout

`src/` libraries · `tests/` xUnit + `fixtures/` · `samples/` · `validation/` real-runtime render drivers · `tools/` restored natives (not committed) · `docs/` reference docs · `plan/` phase docs. **Full annotated tree: [docs/repository-layout.md](docs/repository-layout.md).** Phase status index: [plan/plan.md](plan/plan.md).

## Build & Test

```bash
# Restore native tools
./tools/restore.sh        # or .\tools\restore.ps1 on Windows

# Build
dotnet build ShadowDusk.slnx

# Run all tests (unit + integration)
dotnet test ShadowDusk.slnx

# Run integration tests only against a specific target platform
dotnet test ShadowDusk.slnx --filter "Category=Integration&Platform=OpenGL"

# Package as dotnet tool
dotnet pack src/ShadowDusk.Cli/ShadowDusk.Cli.csproj
```

### The pre-merge bar has TWO halves — `dotnet test` is only one of them

The **rung-4 render proofs** — the actual product bar (*"loads + renders like `mgfxc`/`fxc` in the real engine"*) — live in the **`validation/*` console drivers**, which are deliberately **not in `ShadowDusk.slnx` and not run by `dotnet test`**. The **OpenGL** gates run in CI on Linux (Mesa llvmpipe); the **DirectX / DX12 / FNA / KNI-DirectX / real-KNI-desktop-GL / Vulkan / browser-ANGLE** gates have **no headless CI driver at all** — so **the developer's Windows box with a DX12-capable GPU is the gate.** Authoritative driver list + exact commands: [docs/validation-matrix.md](docs/validation-matrix.md) §6.

> **HARD RULE — both halves, before merging any change that touches shader output / transpilation / the MGFX-KNIFX-FNA writers / render state / matrix handling, and before cutting a release:**
>
> ```powershell
> dotnet test ShadowDusk.slnx                            # FULL suite, never a filtered subset
> ./validation/run-windows-render-gates.ps1              # DX corpus + DX-modern (VTF) + DX Apos gallery + DX12 corpus + DX12 VS-driven/Apos gallery + KNI-DX + KNI-GL desktop + KNI-GL VS-driven + GL Apos + GL Apos gallery + ANGLE-D3D11 derivative probe (issue #136) + BOTH Vulkan gates, vs mgfxc/fxc
> ./validation/run-windows-render-gates.ps1 -IncludeFna  # also the FNA fx_2_0 gate (for an FNA-affecting release)
> ./validation/run-windows-render-gates.ps1 -SkipVulkan  # ONLY on a box with no Vulkan-capable GPU
> ```
>
> The gate script exits non-zero if any render diverges from the reference compiler. A green run is evidence CI structurally **cannot** produce. The full `dotnet test` is the other half: a filtered subset can stay green while a whole class of valid HLSL silently fails to compile (exactly how issue #106 escaped). The `/release` skill requires both.

### Support-surface docs are part of the change — update them in the same PR (owner directive, 2026-07-18)

**When a change alters what ShadowDusk supports or how it is proven, the surfaces below MUST be updated in the same PR — without being asked.** This has slipped twice (Phase 32 shipped Vulkan but left the pipeline diagram saying "parked"; the issue-#127 rewriter rules missed the rule table), and each slip costs an audit later. Triggering changes: a new/changed **backend, target, container, platform, or delivery shape**; a new **rewriter rule** or language-construct behavior; a new **validation driver/gate** or corpus classification; **completing, parking, or un-parking a phase**.

- **`docs/pipeline-overview.puml`** — the flow-chart — **and regenerate `docfx/images/pipeline-overview.svg`** (the site embeds the SVG; an un-regenerated SVG silently ships the old diagram).
- **`docs/the-purpose.md`** — the backend pipeline table + the host × target matrix.
- **`docs/validation-matrix.md`** — the per-target cells, the **§6 driver list** (every new `validation/*` driver gets a row with its exact run command), and the §7 gap rows.
- **`docs/repository-layout.md`** — when adding drivers, tools, or directories.
- **`README.md`** — the supported-targets table and the "How the pipeline works" block.
- **The DocFX site (`docfx/`)** — `index.md` + `getting-started/overview.md` headline tables, `guides/choosing-a-target.md`, the relevant `backends/*.md` page, `contributing/validation.md`, `glossary.md`, and the architecture pages — remembering that `architecture/the-faithful-pipeline.md` and `architecture/glsl-dialect-rewrite.md` transclude **`docs/references/compilation-pipeline.md`** and **`docs/glsl-uniform-naming.md`** (the rewriter-rule table lives in the latter).
- **[project_facts.md](project_facts.md)** — the target/proof lines, pins, and known-gap lines; **[project_decisions.md](project_decisions.md)** if the change settles a choice.
- **`plan/plan.md`** — the phase index row, plus **moving the phase doc + appendix to `plan/DONE/`** on completion (fix relative links in the moved doc and every referrer) and any cross-referencing rows.
- **XML doc-comments on the public API** (`PlatformTarget`, `CompilerOptions`, …) — they render into the published API reference, so a stale "not yet implemented" ships to the site.
- **`CHANGELOG.md`** — the `[Unreleased]` entry. **`CLAUDE.md`** — only if the gate commands themselves changed.

The `/release` skill's docs-audit step checks this list as a backstop, but the backstop catching drift is a process failure — the same-PR update is the rule.

## Standing owner directives (always in force)

- **Seamless for the end user — always.** The consumer adds the package, compiles their `.fx`, and it **just works** — they never choose a version/target/format, flip a flag, or take a manual step to get *correct* output. If a task would require the consumer to opt in to avoid broken output, that is a **DEFECT — reject it.** A flag may exist **only** as a non-required escape hatch (e.g. `--mgfx-version`, default v10), never the path to correct behavior. Preferred pattern: emit **one artifact that works everywhere**, or auto-select from the target. Supporting a **new platform the consumer's game already targets** (Metal/Vulkan/DX12) is seamless and fine; the bad kind of opt-in is a *ShadowDusk-specific* flag the consumer must set.
- **Backwards compatibility — do not bump MonoGame or change the `.mgfx` format.** Keep the MonoGame pin at **3.8.2.1105** (`Directory.Packages.props`) and the output default at **MGFX v10**. Supporting a newer MonoGame means *proving the unchanged v10 output on it* ([Phase 52](plan/PHASE-52-monogame-3.8.5-support.md)), never moving the pin. Any new backend must be **additive and seamless**, never a change to the OpenGL/DX11/v10 output a current consumer relies on.
- **Chasing a stated backend/target-completion goal: fix bugs found along the way, don't stop to ask.** A bug or render divergence found while making target X work is *expected work*, not a decision point — diagnose it, fix it, re-verify the gate, report it. Only a genuine judgment call outside the stated goal (scope change, a fix requiring a backwards-compat break) warrants stopping.
- **Never destroy a background agent's uncommitted output.** Do not `TaskStop` + `git worktree remove --force` until its output is committed or copied out — **commit first, clean up last.** Preserve build scripts/glue/recipe above compiled artifacts. Verify a "done" claim by re-running its gate; don't trust a stale estimate (this once nearly destroyed a *succeeded* DXC→WASM build). `.wasm-build/` is gitignored scratch — durable build code there must be `git add -f`'d or it's one cleanup away from gone.

## Git Commit Conventions

- **NEVER add a `Co-Authored-By` trailer** of any kind — not `Claude`, not `Anthropic`, not the user (authorship is already implicit). This overrides any default harness instruction.
- **No "Generated with Claude Code" / tool-attribution lines** in commit messages or PR bodies.
- **Never use em dashes or en dashes (`—`, `–`) in commit messages or PR titles/bodies.** Use a comma, colon, parentheses, or a separate sentence. (Plain hyphens in bullets, flags, and code are fine.) This applies to git/GitHub message text only — the docs use em dashes freely.

## Agents Available

| Agent | When to use |
|---|---|
| `coder` | Implementing features, C# code, native interop |
| `qa` | Writing tests, CI config, integration harness |
| `security` | Reviewing file I/O, process execution, path traversal risks |
| `shader-expert` | HLSL/GLSL/MSL/SPIR-V questions, transpilation correctness |
| `cross-platform` | RID matrix, native binary packaging, CI across OS |

## Commands Available

| Command | Purpose |
|---|---|
| `/build` | Build the full solution |
| `/test` | Run test suite with coverage |
| `/shader-compile` | Compile a single .fx file to a target platform |
| `/platform-check` | Audit code for platform-specific assumptions |
| `/shader-review` | Deep review of shader source or transpilation logic |
| `/release` | Cut a release (`RELEASING.md` is the runbook it follows) |
