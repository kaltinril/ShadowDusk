# ShadowDusk

A cross-platform HLSL shader compiler for [MonoGame](https://monogame.net/) and [KNI](https://github.com/kniEngine/kni). Compile `.fx` shaders on Linux, macOS, or Windows — no Wine, no Windows SDK, no DirectX install required.

## What it does

MonoGame's stock content pipeline shells out to `mgfxc`, a Windows-only tool that depends on `fxc.exe` from the DirectX SDK. ShadowDusk replaces that step with a portable pipeline:

```
HLSL (.fx)
  → DXC (via Vortice.Dxc)  →  SPIR-V
  → SPIRV-Cross             →  GLSL / MSL / DXBC
  → .mgfx binary            →  MonoGame Effect loader
```

Supported MonoGame backends:

| Backend | Output | Status |
|---|---|---|
| DirectX (Windows) | DXBC | Planned (Phase 4) |
| OpenGL / DesktopGL | GLSL | Planned (Phase 6) |
| Metal (macOS / iOS) | MSL | Planned (post-Phase 6) |
| Vulkan | SPIR-V | Future |

## Drop-in `mgfxc` replacement

ShadowDusk is a transparent substitute for MonoGame's `mgfxc`. Same CLI flags, same `.mgfx` output format, same exit codes, same MGCB-compatible error messages on stderr. Games using the MonoGame Content Pipeline require zero code changes to switch.

## Delivery targets

**CLI tool** (`dotnet tool` named `mgfxc`) — build-time use from MGCB, scripts, or the terminal:

```sh
mgfxc MyShader.fx /Profile:OpenGL /Output:MyShader.mgfx
```

**WASM library** (`ShadowDusk.Wasm`) — in-browser runtime compilation for [Vic's XNA Fiddle](https://github.com/vicot/xnafiddle) and similar KNI web tools. Runs inside .NET WASM; returns `.mgfx` bytes in-memory with no server roundtrip.

Both targets share the same `IShaderCompiler` interface and produce byte-identical output.

## Status

> **Active development — compilation pipeline through SPIRV-Cross transpilation is implemented.** The `.mgfx` binary writer (Phase 7) is the next milestone before an end-to-end compile is possible.

| Phase | Description | Status |
|---|---|---|
| 0 | Fixture corpus + golden reference outputs | Done |
| 1 | Solution scaffold, core types, project wiring | Done |
| 2 | FX9 pre-parser (technique/pass/sampler extraction) | Done |
| 3 | Preprocessor macro injection + `#include` flattening | Done |
| 4 | DXC integration (HLSL → SPIR-V / DXBC) | Done |
| 5 | Shader reflection (cbuffers, parameters, samplers) | Done |
| 6 | SPIRV-Cross transpilation (SPIR-V → GLSL) | Done |
| 7 | `.mgfx` binary writer | Up next |
| 8 | mgfxc-compatible CLI entry point | Planned |
| 9 | Integration tests | Planned |
| 10 | Cross-platform CI (Linux / macOS / Windows) | Planned |
| 11 | Deferred backlog (ShaderCompiler wiring, uniform remapping) | Backlog |
| 12 | Security hardening (path traversal, binary integrity) | Planned |

**Backend support** (compilation chain complete; `.mgfx` output pending Phase 7):

| Backend | Output | Chain status |
|---|---|---|
| DirectX (Windows) | DXBC | DXC → DXBC ✓ |
| OpenGL / DesktopGL | GLSL | DXC → SPIR-V → SPIRV-Cross → GLSL ✓ |
| Metal (macOS / iOS) | MSL | Stub — post-Phase 7 |
| Vulkan | SPIR-V | Future |
| WebGL (XNA Fiddle / KNI) | GLSL ES | WASM path — future phase |

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (≥ 8.0.100)

No native tool restore is required for build and test — DXC binaries come from the `Vortice.Dxc` NuGet package automatically. SPIRV-Cross native binaries are downloaded by `tools/restore.ps1` / `tools/restore.sh` (required for Phases 6+).

### Build

```sh
dotnet build ShadowDusk.slnx
```

### Test

```sh
dotnet test ShadowDusk.slnx
```

### Run integration tests only

```sh
dotnet test ShadowDusk.slnx --filter "Category=Integration"
```

## Repository layout

```
ShadowDusk/
├── src/
│   ├── ShadowDusk.Core/         # Compiler orchestration, IR, platform dispatch
│   ├── ShadowDusk.HLSL/         # HLSL parsing, DXC integration
│   ├── ShadowDusk.GLSL/         # SPIR-V → GLSL via SPIRV-Cross
│   ├── ShadowDusk.Metal/        # SPIR-V → MSL via SPIRV-Cross (stub)
│   ├── ShadowDusk.Cli/          # dotnet tool entry point
│   ├── ShadowDusk.MgcbPlugin/   # MGCB content processor plugin (stub)
│   └── ShadowDusk.Wasm/         # In-browser WASM compiler
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.HLSL.Tests/
│   ├── ShadowDusk.GLSL.Tests/
│   ├── ShadowDusk.Integration.Tests/
│   └── fixtures/
│       ├── shaders/             # 39 canonical .fx test shaders + 4 .fxh headers
│       └── golden/              # Reference .mgfx outputs (DirectX_11/ and OpenGL/)
├── tools/                       # Native binary restore scripts
├── plan/                        # Phase-by-phase implementation plan
└── docs/
```

## Tech stack

- C# 12 / .NET 8
- [Vortice.Dxc](https://github.com/amerkoleci/Vortice.Windows) — managed DXC wrapper (cross-platform, no Windows SDK required)
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) — SPIR-V → GLSL/MSL transpilation via P/Invoke
- xUnit + FluentAssertions

## Design principles

- **No Windows / Wine requirement.** Every native binary has Linux + macOS builds.
- **Deterministic output.** Same source + same target = byte-identical `.mgfx`, given the same compiler version.
- **Fail loudly.** Shader errors surface the source file, line, column, and message exactly as the underlying compiler emitted them.
- **Result-typed errors.** No exceptions for expected shader failures — the API returns `Result<CompiledShader, ShaderError[]>`.

## Contributing

The project is in active early development. See [`plan/`](plan/) for the implementation roadmap (active phases) and [`plan/DONE/`](plan/DONE/) for completed phases. [`CLAUDE.md`](CLAUDE.md) covers coding conventions and agent guidance.
