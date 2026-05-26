# ShadowDusk

A cross-platform HLSL shader compiler for [MonoGame](https://monogame.net/) and [KNI](https://github.com/kniEngine/kni). Compile `.fx` shaders on Linux, macOS, or Windows — no Wine, no Windows SDK, no DirectX install required.

## What it does

MonoGame's stock content pipeline shells out to `mgfxc`, a Windows-only tool that depends on `fxc.exe` from the DirectX SDK. ShadowDusk replaces that step with a portable pipeline:

```
HLSL (.fx)
  → DXC (via Vortice.Dxc)  →  SPIR-V
  → SPIRV-Cross             →  GLSL / MSL
  → .mgfx binary            →  MonoGame Effect loader
```

Supported MonoGame backends:

| Backend | Output |
|---|---|
| DirectX (Windows) | DXBC |
| OpenGL / DesktopGL | GLSL |
| Metal (macOS / iOS) | MSL |
| Vulkan | SPIR-V |
| WebGL (XNA Fiddle / KNI browser) | GLSL ES |

## Drop-in `mgfxc` replacement

ShadowDusk is a transparent substitute for MonoGame's `mgfxc`. Same CLI flags, same `.mgfx` output format, same exit codes, same MGCB-compatible error messages on stderr. Games using the MonoGame Content Pipeline require zero code changes to switch.

## Delivery targets

**CLI tool** (`dotnet tool` named `mgfxc`) — build-time use from MGCB, scripts, or the terminal:

```sh
mgfxc MyShader.fx /Profile:OpenGL /Output:MyShader.mgfx
```

**WASM library** (`ShadowDusk.Wasm`) — in-browser runtime compilation for [XNA Fiddle](https://xnafiddle.net/) and similar KNI web tools. Runs inside .NET WASM; returns `.mgfx` bytes in-memory with no server roundtrip.

Both targets share the same `IShaderCompiler` interface and produce byte-identical output.

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (≥ 8.0.100)

DXC binaries come from the `Vortice.Dxc` NuGet package automatically. SPIRV-Cross native binaries are downloaded by `tools/restore.ps1` / `tools/restore.sh`:

```sh
./tools/restore.sh        # Linux / macOS
.\tools\restore.ps1       # Windows
```

### Build

```sh
dotnet build ShadowDusk.slnx
```

### Test

```sh
# Unit tests
dotnet test ShadowDusk.slnx --filter "Category!=Integration"

# Integration tests (requires native library restore first)
dotnet test ShadowDusk.slnx --filter "Category=Integration"
```

## Repository layout

```
ShadowDusk/
├── src/
│   ├── ShadowDusk.Core/         # Core types, Result<T>, ShaderError, IShaderCompiler
│   ├── ShadowDusk.HLSL/         # FX9 pre-parser, preprocessor, DXC integration, reflection
│   ├── ShadowDusk.GLSL/         # SPIR-V → GLSL via SPIRV-Cross
│   ├── ShadowDusk.Metal/        # SPIR-V → MSL via SPIRV-Cross
│   ├── ShadowDusk.Cli/          # dotnet tool entry point (mgfxc)
│   ├── ShadowDusk.MgcbPlugin/   # MGCB content processor plugin
│   └── ShadowDusk.Wasm/         # In-browser WASM compiler for XNA Fiddle / KNI
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.HLSL.Tests/
│   ├── ShadowDusk.GLSL.Tests/
│   ├── ShadowDusk.Integration.Tests/
│   └── fixtures/
│       ├── shaders/             # Canonical .fx test shaders
│       └── golden/              # Reference .mgfx outputs (DirectX_11/ and OpenGL/)
├── tools/                       # Native binary restore scripts
└── docs/                        # Architecture docs and research
```

## Tech stack

- C# 12 / .NET 8
- [Vortice.Dxc](https://github.com/amerkoleci/Vortice.Windows) — managed DXC wrapper (cross-platform, no Windows SDK required)
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) — SPIR-V → GLSL/MSL transpilation via P/Invoke
- xUnit + FluentAssertions

## Design principles

- **No Windows / Wine requirement.** Every native binary has Linux + macOS builds.
- **Drop-in replacement.** Same CLI flags, same `.mgfx` output, same exit codes and error format as MonoGame's `mgfxc`. Zero changes to existing content pipelines.
- **Deterministic output.** Same source + same target = byte-identical `.mgfx`, given the same compiler version.
- **Fail loudly.** Shader errors surface the source file, line, column, and message exactly as the underlying compiler emitted them.
- **Result-typed errors.** No exceptions for expected shader failures — the API returns `Result<CompiledShader, ShaderError[]>`.

## Contributing

See [`CLAUDE.md`](CLAUDE.md) for coding conventions and agent guidance.
