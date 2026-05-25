# ShadowDusk — Cross-Platform MonoGame Shader Compiler

## Project Overview

ShadowDusk is a system-agnostic shader compilation tool for MonoGame. The goal is to compile MonoGame-compatible shaders (HLSL Effect files) on **any OS** — Linux, macOS, or Windows — without requiring a Windows install or WINE.

MonoGame's stock content pipeline (`MGCB`) shells out to `mgfxc`, which depends on `fxc.exe` (DirectX SDK) on Windows. ShadowDusk replaces that pipeline step with a portable toolchain that transpiles and cross-compiles shaders for each supported MonoGame backend:

| MonoGame Backend | Shader Language | Compiler Target |
|---|---|---|
| DirectX (Windows) | HLSL | DXC → DXBC |
| OpenGL / DesktopGL | GLSL | DXC → SPIR-V → SPIRV-Cross → GLSL |
| Metal (macOS / iOS) | MSL | DXC → SPIR-V → SPIRV-Cross → MSL |
| Vulkan (future) | SPIR-V | DXC → SPIR-V (direct) |

## Repository Layout (planned)

```
ShadowDusk/
├── src/
│   ├── ShadowDusk.Core/          # Compiler orchestration, IR, platform dispatch
│   ├── ShadowDusk.HLSL/          # HLSL → FX parsing, DXC/FXC integration
│   ├── ShadowDusk.GLSL/          # HLSL → GLSL transpilation (via SPIRV-Cross)
│   ├── ShadowDusk.Metal/         # HLSL → MSL transpilation (via SPIRV-Cross)
│   ├── ShadowDusk.Cli/           # CLI entry-point (dotnet tool)
│   └── ShadowDusk.MgcbPlugin/    # MonoGame Content Builder plugin
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.Integration.Tests/   # Compile real .fx files end-to-end
│   └── fixtures/shaders/              # Canonical .fx test shaders
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
- **Packaging**: NuGet `dotnet tool` + optional MGCB plugin NuGet

## Core Design Constraints

1. **No Windows / WINE requirement.** All native binaries must have Linux + macOS builds. Prefer prebuilt GitHub Releases artifacts; fall back to bundling.
2. **Drop-in `mgfxc` replacement.** ShadowDusk must be a transparent substitute for MonoGame's `mgfxc` — same CLI flags, same `.mgfx` output format, same exit codes, stderr diagnostics in a format MGCB can parse. Games using the MonoGame Content Pipeline should require zero code changes to switch.
3. **Deterministic output.** Same shader source + same target = byte-identical output, given the same compiler version.
4. **Fail loudly with diagnostics.** Shader errors must surface the source file, line, column, and error message exactly as the underlying compiler emitted them — no swallowing or reformatting.
5. **Content Pipeline compatible.** Output `.mgfx` binary that MonoGame's `Effect` class can load unchanged. Compatible with MGCB's `ExternalTool` config and PATH-based override.
6. **Single-file self-contained CLI.** `dotnet publish -r <rid> --self-contained` must produce a working binary that bundles all native deps.

## Native Dependency Strategy

Native binaries (DXC, glslang, SPIRV-Cross) are **not** checked into the repo. They are resolved at build time via a `tools/restore.ps1` / `tools/restore.sh` script that downloads pinned GitHub Releases artifacts and places them in `tools/`. CI caches these by hash.

## Build & Test

```bash
# Restore native tools
./tools/restore.sh        # or .\tools\restore.ps1 on Windows

# Build
dotnet build

# Run all tests (unit + integration)
dotnet test

# Run integration tests only against a specific target platform
dotnet test --filter "Category=Integration&Platform=OpenGL"

# Package as dotnet tool
dotnet pack src/ShadowDusk.Cli
```

## Coding Conventions

- Prefer `sealed` classes unless inheritance is explicitly required.
- All public APIs are nullable-annotated; `#nullable enable` in every file.
- `async`/`await` all the way down for child-process invocations — never `.Result` or `.Wait()`.
- Error results use a `Result<T, ShaderError>` discriminated union — no exception-as-control-flow.
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
