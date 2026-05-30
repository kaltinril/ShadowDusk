# ShadowDusk — Cross-Platform MonoGame Shader Compiler

## Project Overview

ShadowDusk is a cross-platform HLSL shader compiler for MonoGame and KNI. Its five core purposes are:

1. **OS-agnostic compilation** — compile `.fx` shaders on Linux, macOS, or Windows with no Wine or Windows SDK required.
2. **DirectX and OpenGL targets** — produce DXBC (DirectX 11) or GLSL (OpenGL/WebGL) output from a single HLSL source.
3. **Drop-in `mgfxc` replacement** — transparent substitute for MonoGame's Windows-only `mgfxc` tool; same CLI flags, same `.mgfx` output, same exit codes and error format so existing content pipelines require zero changes.
4. **CLI tool** — `dotnet tool` named `mgfxc`; usable standalone, from MGCB, or from any build script.
5. **Web / WASM / in-memory** — runs inside .NET WASM for browser-based tools (e.g. Vic's XNA Fiddle) so users can compile shaders at runtime without a server roundtrip; returns compiled `.mgfx` bytes in-memory.

### What success actually means (read this first)

ShadowDusk earns its existence on **two** axes, and needs **both** — either one alone is worthless:

1. **Reach `mgfxc` can't (the differentiator).** Compile `.fx` where MonoGame's own toolchain cannot: on **Linux/macOS** (no Wine, no Windows SDK, no `fxc.exe`) and **at runtime, in-browser / in-memory** via WASM (e.g. XNA Fiddle). `mgfxc` already covers Windows-at-build-time, so matching it *only* there would be pointless — the reach is the reason to exist.
2. **Output `mgfxc` would (the fidelity).** The compiled `.mgfx`, loaded into the **real MonoGame/KNI runtime**, renders **the same image** as the `mgfxc`-compiled version — zero code or content-pipeline changes.

The product is the **combination**: *the same result `mgfxc` gives, produced where `mgfxc` can't run.* The two parts are validated differently — Part 1 (reach) by cross-platform CI (Phase 30) + the WASM/in-memory path, with all hosts producing byte-identical output to each other; Part 2 (fidelity) by the bar below. The ultimate combined proof is a shader compiled by ShadowDusk *on Linux or in a browser* rendering the same in-game image as `mgfxc`'s Windows build.

**Part 2's bar is in-engine behavioral equivalence:** a game whose `.fx` shaders are compiled with ShadowDusk instead of `mgfxc`, loaded into the **real MonoGame/KNI runtime** (`Effect`), renders **the same pixels** as the `mgfxc`-compiled version.

- The measure is *what the player sees in a real MonoGame game*, **not "ShadowDusk's own tests pass."** Unit tests, `.mgfx` structural tests, and images rendered by **ShadowDusk's own** renderer are necessary **proxies, not the bar** — a proxy can be green while the real goal is unmet. (This has happened: a GLSL cross-validation passed only because the test renderer was taught to bind uniform names the real runtime doesn't use.)
- **Evidence ladder, weakest → strongest:** (1) compiles without error → (2) `.mgfx` is structurally well-formed → (3) ShadowDusk's GLSL matches `mgfxc`'s GLSL *in our own renderer* → (4) **ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders like `mgfxc`'s in the real runtime.** Only (4) proves the promise. (4) is **proven for the OpenGL SM3 PS-only corpus** (Phase 17, done 2026-05-30 — `plan/DONE/PHASE-17-monogame-runtime-validation.md`: all 10/10 shaders render pixel-equivalent in real MonoGame DesktopGL); **DirectX (4) is still pending** (Phase 18), and VS-driven effects remain (backlog 17-VS).
- **Compare same-backend, never cross-backend.** Validation always compares ShadowDusk vs `mgfxc` on the *same* target (GL↔GL, DX↔DX) — never OpenGL output against DirectX output. The shader's *intent* (e.g. a 9-tap blur) is backend-agnostic, but each backend is a **separate emitted artifact** (OpenGL = GLSL text; DirectX = GPU bytecode) loaded by a **different** MonoGame runtime path. So a green OpenGL result says nothing about DirectX: a shipped game runs exactly one backend, and each must be produced correctly and validated on its own. "The blur is correct" ≠ "ShadowDusk emitted a valid, loadable artifact for *this* backend."
- **"Same `.mgfx` output" means behaviorally equivalent and `Effect`-loadable — NOT byte-identical to `mgfxc`.** Different compilers; byte-equality with `mgfxc` is neither expected nor a goal. "Byte-identical / deterministic" (constraint 3) refers only to *ShadowDusk's own* reproducibility: same ShadowDusk version + same source + same target → same bytes.

MonoGame's stock content pipeline (`MGCB`) shells out to `mgfxc`, which depends on `fxc.exe` (DirectX SDK) on Windows. ShadowDusk replaces that pipeline step with a portable toolchain that transpiles and cross-compiles shaders for each supported MonoGame/KNI backend:

| MonoGame Backend | Shader Language | Compiler Target |
|---|---|---|
| DirectX (Windows) | HLSL | DXC → DXBC |
| OpenGL / DesktopGL | GLSL | DXC → SPIR-V → SPIRV-Cross → GLSL |
| Metal (macOS / iOS) | MSL | DXC → SPIR-V → SPIRV-Cross → MSL |
| Vulkan (future) | SPIR-V | DXC → SPIR-V (direct) |

> **DirectX row is the *intended* pipeline, not the current reality.** DXC compiles to **DXIL (Shader Model 6)**, not DXBC — `DxcFlagBuilder` uses `ps_6_0`/`vs_6_0` and notes DXC cannot emit SM5 DXBC. MonoGame 3.8's DX11 runtime loads **DXBC (SM ≤ 5)**, so the DirectX `.mgfx` likely won't load there yet. Reconciling this is deferred (see `plan/DONE/PHASE-17-monogame-runtime-validation.md` §8 / Phase 18). **OpenGL is the first target validated end-to-end** (Phase 17, done — 10/10 SM3 shaders render equivalently in real MonoGame DesktopGL).

## Repository Layout

```
ShadowDusk/
├── src/
│   ├── ShadowDusk.Core/          # Compiler orchestration, IR, platform dispatch
│   ├── ShadowDusk.HLSL/          # HLSL → FX parsing, DXC/FXC integration
│   ├── ShadowDusk.GLSL/          # HLSL → GLSL transpilation (via SPIRV-Cross)
│   ├── ShadowDusk.Metal/         # HLSL → MSL transpilation (via SPIRV-Cross)
│   ├── ShadowDusk.Cli/           # CLI entry-point (dotnet tool)
│   ├── ShadowDusk.MgcbPlugin/    # MonoGame Content Builder plugin
│   └── ShadowDusk.Wasm/          # WASM-safe IShaderCompiler impl for browser (JS interop to WASM-compiled DXC + SPIRV-Cross)
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.HLSL.Tests/
│   ├── ShadowDusk.GLSL.Tests/
│   ├── ShadowDusk.Integration.Tests/   # Compile real .fx files end-to-end
│   └── fixtures/
│       ├── shaders/                    # Canonical .fx test shaders (39 files + 4 .fxh headers)
│       └── golden/                     # Reference .mgfx outputs (DirectX_11/ and OpenGL/)
├── tools/                         # Vendored / downloaded native binaries
│   ├── dxc/                       # unused — DXC comes from Vortice.Dxc NuGet
│   └── spirv-cross/               # libspirv-cross-c-shared (.dll/.so/.dylib)
├── docs/
└── CLAUDE.md
```

## Tech Stack

- **Language**: C# 12 / .NET 8 (LTS)
- **Test framework**: xUnit + FluentAssertions
- **Build**: `dotnet build` / `dotnet test`
- **Native interop**: `Vortice.Dxc` NuGet for DXC; raw P/Invoke for SPIRV-Cross C API
- **WASM interop**: `[JSImport]` / `[JSExport]` (.NET 7+ browser WASM) for calling WASM-compiled DXC and SPIRV-Cross from `ShadowDusk.Wasm`
- **Packaging**: NuGet `dotnet tool` + optional MGCB plugin NuGet

## Core Design Constraints

1. **No Windows / WINE requirement.** All native binaries must have Linux + macOS builds. Prefer prebuilt GitHub Releases artifacts; fall back to bundling.
2. **Drop-in `mgfxc` replacement.** ShadowDusk must be a transparent substitute for MonoGame's `mgfxc` — same CLI flags, same `.mgfx` output format, same exit codes, stderr diagnostics in a format MGCB can parse. Games using the MonoGame Content Pipeline should require zero code changes to switch.
3. **Deterministic output.** Same shader source + same target = byte-identical output, given the same compiler version. (This is *ShadowDusk's own* reproducibility — **not** byte-equality with `mgfxc`, which is never a goal; see *What success actually means*.)
4. **Two delivery targets.** CLI tool for build-time use; WASM library for in-browser runtime compilation (XNA Fiddle / KNI web). Output format is identical (.mgfx bytes); only the invocation mechanism differs. `IShaderCompiler` abstracts both.
5. **Fail loudly with diagnostics.** Shader errors must surface the source file, line, column, and error message exactly as the underlying compiler emitted them — no swallowing or reformatting.
6. **Content Pipeline compatible.** Output `.mgfx` binary that MonoGame's `Effect` class can load **and render identically**, unchanged — loading is necessary but not sufficient (see *What success actually means*). Compatible with MGCB's `ExternalTool` config and PATH-based override.
7. **Single-file self-contained CLI.** `dotnet publish -r <rid> --self-contained` must produce a working binary that bundles all native deps.

## Native Dependency Strategy

Native binaries (DXC, glslang, SPIRV-Cross) are **not** checked into the repo. They are resolved at build time via a `tools/restore.ps1` / `tools/restore.sh` script that downloads pinned GitHub Releases artifacts and places them in `tools/`. CI caches these by hash.

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

## Coding Conventions

- Prefer `sealed` classes unless inheritance is explicitly required.
- All public APIs are nullable-annotated; `#nullable enable` in every file.
- `async`/`await` all the way down for child-process invocations — never `.Result` or `.Wait()`.
- Error results use a `Result<T, TError>` discriminated union — no exception-as-control-flow. Compiler errors use `Result<CompiledShader, ShaderError[]>`.
- Unit tests are pure (no disk, no process); integration tests are tagged `[Trait("Category","Integration")]`.
- No `Thread.Sleep` in tests; use `CancellationToken` with reasonable timeouts.

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
