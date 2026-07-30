# ShadowDusk — Cross-Platform MonoGame Shader Compiler

## THE PURPOSE (read this first)

**The product is a drop-in `mgfxc` replacement: a self-contained library** a user adds to their **MonoGame/KNI project on Linux, macOS, or Windows**, that compiles **`.fx` → `.mgfx` in memory at runtime**, requiring **nothing but the library itself** — no `fxc.exe`, no `mgfxc`, no Wine, no Windows SDK, no native toolchain the user has to install separately. Its output **loads and renders identically to `mgfxc`'s** in the **real MonoGame/KNI runtime**. **One faithful compiler; the same `mgfxc`-equivalent result everywhere.**

The distinctions that carry the most weight — internalize these, they have drifted before (full detail, success criteria, and the backend table in **[docs/the-purpose.md](docs/the-purpose.md)**):

- **The library *is* the product.** The deliverable is the in-memory compiler called at runtime (`IShaderCompiler.CompileAsync(fx) → .mgfx bytes`). The **CLI** and **MGCB plugin** are *delivery shapes of the same library*; the **browser / WASM shader-fiddle is ONLY a sample of reach — never the product.** Don't let sample work redefine the goal.
- **One pipeline, everywhere — NO substitute compilers.** Every host runs the same faithful pipeline (HLSL →`[DXC]`→ SPIR-V →`[SPIRV-Cross]`→ GLSL →`[managed rewrite + MGFX writer]`→ `.mgfx`; or `vkd3d-shader` → DXBC for DirectX). A host must **not** swap in a different frontend/compiler to make a platform "work" — different compiler ⇒ different output ⇒ silently breaks the "identical to `mgfxc`" promise. If a faithful component can't run on a host yet, that host's runtime-compile is **not done** — never a licence to substitute.
- **"Self-contained" is a hard requirement.** Native pieces ride *inside* the NuGet package (transitive native assets), never a separate manual install. "Add the package, call the API" is the entire setup.
- **The bar is the real runtime, not our tests.** Only ShadowDusk's `.mgfx` loading in MonoGame's `Effect` and rendering like `mgfxc`'s proves the promise. Tests/our-own-renderer images are **proxies, not the bar**. Compare same-backend only (GL↔GL, DX↔DX), never cross-backend. "Same as `mgfxc`" = behaviorally equivalent + `Effect`-loadable, **NOT** byte-identical (that's a non-goal).

### The evidence ladder — what "rung 4" means

Docs, phase notes, and commit messages say **"rung 4"** constantly. It is the four-step scale of how strongly a target is proven, weakest to strongest:

| Rung | What it proves |
|---|---|
| **1** | The shader **compiles** without error. |
| **2** | The output file is **structurally well-formed** (a real reader can parse it). |
| **3** | Our output **matches the reference compiler's** (`mgfxc`/`fxc`) when rendered in **our own** test renderer. |
| **4** | The output **loads in the real engine** (MonoGame/KNI `Effect`, or FNA) and **renders the same image** as the reference compiler's build. |

**Only rung 4 proves the promise.** Rungs 1-3 are proxies: every one of them can be green while the product is broken for a real player. "Rung-4 proven" and "render-proven" mean the same thing.

## Source-of-truth files

- **[project_facts.md](project_facts.md)** — what is TRUE (targets and how far each is proven, pins and natives, where things run, known gaps, vocabulary).
- **[project_rules.md](project_rules.md)** — how to WORK on it (testing bar, code conventions, docs/phase process, release mechanics).
- **[project_decisions.md](project_decisions.md)** — what was CHOSEN and why; consult before re-litigating anything.

**Do NOT create memory files, and do NOT rely on the machine-local agent memory store** (it is lost between computers). Every durable fact, rule, or decision goes in those three files — exceptions that stay separate: phase docs, user-facing docs/readmes, reference docs, temp files. Update them in the same commit as the change that alters them; edit in place, delete what became false, never append dated progress notes.

The rules below stay in this file *because they must fire without anyone opening another file*. They are not duplicated in `project_rules.md`.

### Handoff — there is no verbal handoff on this project

Work arrives from a previous session you did not see. Two obligations, both non-optional:

- **Picking up:** before assuming the state of anything, read `git log --oneline -20`, `CHANGELOG.md`'s `[Unreleased]` section, and `plan/plan.md`'s status rows.
- **Handing off:** anything durable you learned goes into the three files above *before the session ends*, and any **new obligation** you created gets registered where the next person will actually find it — a check that can't run under `dotnet test` needs a `docs/validation-matrix.md` §6 row (and a slot in the render-gate script if it can run there); a release-time chore needs a line in `RELEASING.md` and the `/release` skill. **A check nobody remembers to run does not exist.** Full rules: [project_rules.md](project_rules.md) → *Handoff*.

## Repository Layout

`src/` libraries · `tests/` xUnit + `fixtures/` · `samples/` · `validation/` real-runtime render drivers · `tools/` restored natives (not committed) · `docs/` reference docs · `plan/` phase docs. **Full annotated tree: [docs/repository-layout.md](docs/repository-layout.md).** Phase status index: [plan/plan.md](plan/plan.md).

**Stack:** C# 12 / .NET 8 (LTS), xUnit + Shouldly (**FluentAssertions is banned — issue #171, licence; see [project_facts.md](project_facts.md)**), warnings-as-errors. Native interop: `Vortice.Dxc` (DXC), `Silk.NET` P/Invoke (SPIRV-Cross), `vkd3d-shader` (DXBC). Ships as seven `ShadowDusk.*` NuGet packages at one shared version plus the `ShadowDuskCLI` dotnet tool.

## What ShadowDusk compiles for today

| Target | Where it stands |
|---|---|
| **OpenGL** (MonoGame/KNI DesktopGL, WebGL) | rung 4 |
| **DirectX 11** (WindowsDX) | rung 4 |
| **DirectX 12** (WindowsDX12) | rung 4 — MonoGame only, KNI ships no DX12 |
| **Vulkan** (DesktopVK) | rung 4 — MonoGame only, KNI ships no Vulkan |
| **FNA** (`fx_2_0` `.fxb`) | rung 4 — reference compiler is `fxc /T fx_2_0`, not `mgfxc` |
| **Metal** | not implemented, parked (no consumer runtime to validate against) |
| **Android** (compile on-device) | proven on an emulator; still needs production hardening |

This table is a **cold-start summary**. The authority on how far each cell is proven is **[docs/validation-matrix.md](docs/validation-matrix.md)** — update it there first. What *constrains* each target (and why), plus pins and known gaps: **[project_facts.md](project_facts.md)**.

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
- **`CHANGELOG.md`** — the `[Unreleased]` entry. **`CLAUDE.md`** — the target summary table, and the gate commands if those changed.

The `/release` skill's docs-audit step checks this list as a backstop, but the backstop catching drift is a process failure — the same-PR update is the rule.

## Standing owner directives (always in force)

- **Seamless for the end user — always.** The consumer adds the package, compiles their `.fx`, and it **just works** — they never choose a version/target/format, flip a flag, or take a manual step to get *correct* output. If a task would require the consumer to opt in to avoid broken output, that is a **DEFECT — reject it.** A flag may exist **only** as a non-required escape hatch (e.g. `--mgfx-version`, default v10), never the path to correct behavior. Preferred pattern: emit **one artifact that works everywhere**, or auto-select from the target. Supporting a **new platform the consumer's game already targets** (Metal/Vulkan/DX12) is seamless and fine; the bad kind of opt-in is a *ShadowDusk-specific* flag the consumer must set.
- **Backwards compatibility — the commitment is the OUTPUT FORMAT, not a MonoGame version.** Keep the output default at **MGFX v10**. Supporting a newer MonoGame means *proving the unchanged v10 output on it* and adding it to the matrix ([Phase 52](plan/DONE/PHASE-52-monogame-3.8.5-support.md)), never changing what we emit. Any new backend must be **additive and seamless**, never a change to the OpenGL/DX11/v10 output a current consumer relies on.
  - **Do not treat 3.8.2.1105 as "the" version** (owner directive, 2026-07-28). No shipped `ShadowDusk.*` package depends on MonoGame, and no project referencing the `Directory.Packages.props` MonoGame version is in `ShadowDusk.slnx` — that number is just the default the GL validation harnesses render against. The real, measured claim is that **one v10 build renders pixel-identically on every MonoGame from 3.8.1.263 to 3.8.5** (`validation/ForwardCompat`, 7 releases). **3.8.1.263 is the measured floor** — 3.8.0.1641's loader predates MGFX v10 and rejects our output. **Validate before *and* after any new release**: append it to the matrix and re-sweep, rather than re-anchoring on it.
- **Chasing a stated backend/target-completion goal: fix bugs found along the way, don't stop to ask.** A bug or render divergence found while making target X work is *expected work*, not a decision point — diagnose it, fix it, re-verify the gate, report it. Only a genuine judgment call outside the stated goal (scope change, a fix requiring a backwards-compat break) warrants stopping.
- **Never destroy a background agent's uncommitted output.** Do not `TaskStop` + `git worktree remove --force` until its output is committed or copied out — **commit first, clean up last.** Preserve build scripts/glue/recipe above compiled artifacts. Verify a "done" claim by re-running its gate; don't trust a stale estimate (this once nearly destroyed a *succeeded* DXC→WASM build). `.wasm-build/` is gitignored scratch — durable build code there must be `git add -f`'d or it's one cleanup away from gone.

## Coding Conventions

- `sealed` by default unless inheritance is explicitly required.
- `#nullable enable` in every file; all public APIs nullable-annotated.
- `async`/`await` all the way down for child-process work — never `.Result` or `.Wait()`.
- Errors use a `Result<T, TError>` union, never exception-as-control-flow. Compiler errors use `Result<CompiledShader, ShaderError[]>`.
- **Fail loudly.** An input shape we don't model gets a registered diagnostic code, never a silent pass-through. Never swallow or reformat a compiler's own message — keep its file, line, column, and text verbatim.
- Unit tests are pure (no disk, no process); integration tests are tagged `[Trait("Category","Integration")]`; no `Thread.Sleep` in tests.
- **Assertions use Shouldly; never FluentAssertions** — banned (issue #171), a licence obligation, not a preference. Never add the package back and never write `.Should()`; Shouldly's assertions hang off the value itself. On string receivers pass `Case.Sensitive` to `ShouldContain`/`ShouldNotContain`: Shouldly's default is case-*insensitive*, which silently weakens assertions over generated shader source.

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
