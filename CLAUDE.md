# ShadowDusk — Cross-Platform MonoGame Shader Compiler

## Project Overview

ShadowDusk is a cross-platform HLSL shader compiler for MonoGame and KNI. Its five core purposes are:

1. **OS-agnostic compilation** — compile `.fx` shaders on Linux, macOS, or Windows with no Wine or Windows SDK required.
2. **DirectX and OpenGL targets** — produce DXBC (DirectX 11) or GLSL (OpenGL/WebGL) output from a single HLSL source.
3. **Drop-in `mgfxc` replacement** — transparent substitute for MonoGame's Windows-only `mgfxc` tool; same CLI flags, same `.mgfx` output, same exit codes and error format so existing content pipelines require zero changes.
4. **CLI tool** — `dotnet tool` named `mgfxc`; usable standalone, from MGCB, or from any build script.
5. **Web / WASM / in-memory** — runs inside .NET WASM for browser-based tools (e.g. Vic's XNA Fiddle) so users can compile shaders at runtime without a server roundtrip; returns compiled `.mgfx` bytes in-memory.

MonoGame's stock content pipeline (`MGCB`) shells out to `mgfxc`, which depends on `fxc.exe` (DirectX SDK) on Windows. ShadowDusk replaces that pipeline step with a portable toolchain that transpiles and cross-compiles shaders for each supported MonoGame/KNI backend:

| MonoGame Backend | Shader Language | Compiler Target |
|---|---|---|
| DirectX (Windows) | HLSL | DXC → DXBC |
| OpenGL / DesktopGL | GLSL | DXC → SPIR-V → SPIRV-Cross → GLSL |
| Metal (macOS / iOS) | MSL | DXC → SPIR-V → SPIRV-Cross → MSL |
| Vulkan (future) | SPIR-V | DXC → SPIR-V (direct) |

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
3. **Deterministic output.** Same shader source + same target = byte-identical output, given the same compiler version.
4. **Two delivery targets.** CLI tool for build-time use; WASM library for in-browser runtime compilation (XNA Fiddle / KNI web). Output format is identical (.mgfx bytes); only the invocation mechanism differs. `IShaderCompiler` abstracts both.
5. **Fail loudly with diagnostics.** Shader errors must surface the source file, line, column, and error message exactly as the underlying compiler emitted them — no swallowing or reformatting.
6. **Content Pipeline compatible.** Output `.mgfx` binary that MonoGame's `Effect` class can load unchanged. Compatible with MGCB's `ExternalTool` config and PATH-based override.
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
