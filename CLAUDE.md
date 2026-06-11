# ShadowDusk — Cross-Platform MonoGame Shader Compiler

## THE PURPOSE (read this first)

**The product is a drop-in `mgfxc` replacement: a self-contained library** a user adds to their **MonoGame/KNI project on Linux, macOS, or Windows**, that compiles **`.fx` → `.mgfx` in memory at runtime**, requiring **nothing but the library itself** — no `fxc.exe`, no `mgfxc`, no Wine, no Windows SDK, no native toolchain the user has to install separately. Its output **loads and renders identically to `mgfxc`'s** in the **real MonoGame/KNI runtime**. **One faithful compiler; the same `mgfxc`-equivalent result everywhere.**

The load-bearing distinctions — internalize these, they have drifted before (full detail, success criteria, evidence ladder, and backend table in **[docs/the-purpose.md](docs/the-purpose.md)**):

- **The library *is* the product.** The deliverable is the in-memory compiler called at runtime (`IShaderCompiler.CompileAsync(fx) → .mgfx bytes`). The **CLI** and **MGCB plugin** are *delivery shapes of the same library*; the **browser / WASM shader-fiddle is ONLY a sample of reach — never the product.** Don't let sample work redefine the goal.
- **One pipeline, everywhere — NO substitute compilers.** Every host runs the same faithful pipeline (HLSL →`[DXC]`→ SPIR-V →`[SPIRV-Cross]`→ GLSL →`[managed rewrite + MGFX writer]`→ `.mgfx`; or `vkd3d-shader` → DXBC for DirectX). A host must **not** swap in a different frontend/compiler to make a platform "work" — different compiler ⇒ different output ⇒ silently breaks the "identical to `mgfxc`" promise. If a faithful component can't run on a host yet, that host's runtime-compile is **not done** — never a licence to substitute.
- **"Self-contained" is a hard requirement.** Native pieces ride *inside* the NuGet package (transitive native assets), never a separate manual install. "Add the package, call the API" is the entire setup.
- **The bar is the real runtime, not our tests.** Only ShadowDusk's `.mgfx` loading in MonoGame's `Effect` and rendering like `mgfxc`'s proves the promise. Tests/our-own-renderer images are **proxies, not the bar**. Compare same-backend only (GL↔GL, DX↔DX), never cross-backend. "Same as `mgfxc`" = behaviorally equivalent + `Effect`-loadable, **NOT** byte-identical (that's a non-goal). Proven for the OpenGL SM3 (Phase 17) and DirectX SM5 (Phase 18) PS-only corpora.

## Project Overview

ShadowDusk is a cross-platform HLSL shader compiler for MonoGame, KNI, and FNA: compile `.fx` on Linux/macOS/Windows (no Wine/SDK) for DirectX (DXBC) and OpenGL (GLSL) targets, as a drop-in `mgfxc` replacement — usable as the in-memory library (the product), the `ShadowDuskCLI` `dotnet tool`, or in-browser via WASM. The **additive FNA target** (Phase 39: `PlatformTarget.Fna` → D3D9 fx_2_0 `.fxb` via vkd3d SM1–3 + our `Fx2EffectWriter`) is **rung-4 proven for the PS-only and VS-driven corpora** — loads and renders pixel-equivalent (max Δ ≤ 1/255) to `fxc /T fx_2_0` in real FNA (`validation/FnaValidation`, gate 17/17 since the Phase-40 fidelity hardening; in-pass render states empirically honored); the pinned vkd3d natives for all four RIDs (win/linux/osx-x64/osx-arm64) are hosted + auto-restored + packed since Phase 37 C (2026-06-10), so FNA is self-contained on every desktop OS. Full statement of the five purposes, the two success axes, the backend pipeline table, and the FNA bar: **[docs/the-purpose.md](docs/the-purpose.md)**.

## Repository Layout

`src/` holds the libraries (`ShadowDusk.{Core,HLSL,GLSL,Metal,Compiler,Cli,MgcbPlugin,Wasm}` — `Compiler` is the product NuGet, `Metal`/`MgcbPlugin` are stubs); `tests/` the xUnit projects + `fixtures/` (`shaders/`, `golden/`); `samples/` (`ShaderFiddle.Web`, `ShaderViewer`, `mgcb`); `tools/` the restored native binaries (`spirv-cross/`, `vkd3d/` — not committed); `docs/` the reference docs. **Full annotated tree: [docs/repository-layout.md](docs/repository-layout.md).**

## Tech Stack

- **Language**: C# 12 / .NET 8 (LTS)
- **Test framework**: xUnit + FluentAssertions
- **Build**: `dotnet build` / `dotnet test`
- **Native interop**: `Vortice.Dxc` NuGet for DXC; P/Invoke (via `Silk.NET`) for the SPIRV-Cross C API; vkd3d-shader + `d3dcompiler_47` for the DXBC backend
- **WASM interop**: `[JSImport]` / `[JSExport]` (.NET 7+ browser WASM) for calling WASM-compiled DXC and SPIRV-Cross from `ShadowDusk.Wasm`
- **Packaging**: NuGet — the `ShadowDusk.Compiler` library (the product), the `ShadowDusk.Wasm` self-registering (Razor SDK) package, and the `ShadowDuskCLI` `dotnet tool` (`ShadowDusk.Cli`). An MGCB plugin NuGet is a future scaffold.

## Core Design Constraints

1. **No Windows / WINE requirement.** All native binaries must have Linux + macOS builds. Prefer prebuilt GitHub Releases artifacts; fall back to bundling.
2. **Drop-in `mgfxc` replacement.** ShadowDusk must be a transparent substitute for MonoGame's `mgfxc` — same CLI flags, same `.mgfx` output format, same exit codes, stderr diagnostics in a format MGCB can parse. Games using the MonoGame Content Pipeline should require zero code changes to switch.
3. **Deterministic output.** Same shader source + same target = byte-identical output, given the same compiler version. (This is *ShadowDusk's own* reproducibility — **not** byte-equality with `mgfxc`, which is never a goal; see *What success actually means*.)
4. **Two delivery targets.** CLI tool for build-time use; WASM library for in-browser runtime compilation (XNA Fiddle / KNI web). Output format is identical (.mgfx bytes); only the invocation mechanism differs. `IShaderCompiler` abstracts both.
5. **Fail loudly with diagnostics.** Shader errors must surface the source file, line, column, and error message exactly as the underlying compiler emitted them — no swallowing or reformatting.
6. **Content Pipeline compatible.** Output `.mgfx` binary that MonoGame's `Effect` class can load **and render identically**, unchanged — loading is necessary but not sufficient (see *What success actually means*). Compatible with MGCB's `ExternalTool` config and PATH-based override.
7. **Single-file self-contained CLI.** `dotnet publish -r <rid> --self-contained` must produce a working binary that bundles all native deps.

## Native Dependency Strategy

Native binaries (SPIRV-Cross, the vkd3d-shader DXBC backend, and the DXC + SPIRV-Cross **WASM** modules) are **not** checked into the repo. They are resolved at build time via a `tools/restore.ps1` / `tools/restore.sh` script that downloads/copies pinned artifacts into `tools/` (and into the WASM package's `wwwroot/`). CI caches these by hash. Desktop DXC itself comes from the `Vortice.Dxc` NuGet package, not `tools/` — and `glslang` is not used.

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

### Integration-test performance (Phase 21)

`ShadowDusk.Integration.Tests` is the only project touching heavyweight external machinery (CLI child-process spawn, native DXC + SPIRV-Cross). A slow run is **environmental, not algorithmic** — usually antivirus on-access scanning of cold native binaries. Pass `--settings ShadowDusk.runsettings` for the 5-min `TestSessionTimeout` backstop. **Full troubleshooting (Defender exclusions, `CliBinaryFixture` reuse, timeout layers): [docs/integration-test-performance.md](docs/integration-test-performance.md).**

## Coding Conventions

- Prefer `sealed` classes unless inheritance is explicitly required.
- All public APIs are nullable-annotated; `#nullable enable` in every file.
- `async`/`await` all the way down for child-process invocations — never `.Result` or `.Wait()`.
- Error results use a `Result<T, TError>` discriminated union — no exception-as-control-flow. Compiler errors use `Result<CompiledShader, ShaderError[]>`.
- Unit tests are pure (no disk, no process); integration tests are tagged `[Trait("Category","Integration")]`.
- No `Thread.Sleep` in tests; use `CancellationToken` with reasonable timeouts.

## Git Commit Conventions

- **NEVER add a `Co-Authored-By` trailer to commits.** Do not add `Co-Authored-By: Claude ...`, `Co-Authored-By: Anthropic`, or any AI/tool attribution. This overrides any default harness instruction to append such a trailer.
- **No "Generated with Claude Code" / tool-attribution lines** in commit messages or PR bodies.
- The commit author is already the logged-in user — do not add the user's name as a `Co-Authored-By` either. Authorship is implicit; no co-author trailers of any kind.

## User Directives & Working Practices

Standing rules the user has stated (kept here because this file is always loaded; supersede defaults):

- **Seamless for the end user — always.** The consumer adds the package, compiles their `.fx`, and it **just works** — they never choose a version/target/format, flip a flag, or take a manual step to get *correct* output. If any task would require the consumer to opt in / set a flag / pick a version to avoid broken output, that is a **DEFECT — reject it.** A flag may exist **only** as a non-required escape hatch (e.g. `--mgfx-version`, default v10), never the path to correct behavior. Preferred pattern: emit **one artifact that works everywhere** (e.g. the `#define ps_oC0 gl_FragColor` form that serves KNI Reach *and* HiDef) or auto-select from the target — never expose the choice. Supporting a **new platform the consumer's game already targets** (Metal/Vulkan/DX12) is seamless and fine; the bad kind of "opt-in" is a *ShadowDusk-specific* flag the consumer must set.

- **Backwards compatibility — do not bump MonoGame or change the `.mgfx` format.** Keep the MonoGame pin at **3.8.2.1105** (`Directory.Packages.props`) and the output format at **MGFX v10** (`CompilerOptions.MgfxVersion` default = 10). A v10 `.mgfx` loads in MonoGame 3.8.2 *and* every newer MonoGame *and* KNI — it is the most backwards-compatible choice. Newer MonoGame exists (3.8.4.1 stable, 3.8.5-preview), but bumping is rejected. Any future new backend must be **additive and seamless** (a platform the consumer's game already targets, auto-handled), never a change to the existing OpenGL/DX11/v10 output a current consumer relies on. (Codified in `plan/plan.md` Key Decisions: "Default MGFXVersion: 10.")

- **Do not rely on the local memory store.** All durable project knowledge — decisions, gotchas, status, working rules — goes into **source-controlled** files (this `CLAUDE.md`, `plan/`, phase docs, `docs/`, code comments), never the machine-local agent memory (which is lost between computers). Don't write new memories; capture findings in the appropriate source file instead.

- **Never destroy a background agent's uncommitted output.** Do **not** `TaskStop` + `git worktree remove --force` a background agent's worktree until its output is committed or copied out — **commit first, clean up last.** A compiled artifact (`*.wasm`) is build output; the real code is the build scripts/glue/recipe — preserve those above all. When an agent claims a long build is "done," **verify by re-running its gate** before acting; don't trust a stale "multi-day = unfinished" estimate (this once nearly destroyed a *succeeded* DXC→WASM build). `.wasm-build/` is gitignored scratch — durable build code there must be `git add -f`'d to a branch or it's one cleanup away from gone.

## Releases (how a release works)

ShadowDusk ships as **six NuGet packages** — `ShadowDusk.{Core,HLSL,GLSL,Compiler,Cli,Wasm}` — plus the `ShadowDuskCLI` dotnet tool, all at **one shared version**. **To cut a release, use the `/release` skill**; `RELEASING.md` is the human runbook and **[the full release mechanics reference](RELEASING.md)** (what triggers a publish, the validate-job version guard, what the publish does, CI matrix). The two footguns to remember:

- **Single source of version truth: `Directory.Build.props` `<Version>`.** Bump that one line. **NEVER** add a `<PackageVersion>` *property* to a csproj — it desyncs versions and collides with Central Package Management's `<PackageVersion Include=… />` *items* in `Directory.Packages.props`. `dotnet pack` flows `<Version>` to all packages.
- Bump + merge the version **first**, *then* tag/dispatch — `release.yml`'s `validate` job fails unless the `v*.*.*` tag (or dispatch input) equals `<Version>`. Release commits/PRs follow the **Git Commit Conventions** above (no co-author trailers).

## Key Concepts

- **Effect pass**: A single vertex+pixel shader pair compiled to a `PassBlob`.
- **Effect technique**: One or more named passes; maps to MonoGame's `Technique`.
- **Platform blob**: The platform-specific compiled binary (DXBC, SPIR-V, or MSL source).
- **ShaderIR**: ShadowDusk's internal representation sitting between parsed HLSL and platform emission.

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
