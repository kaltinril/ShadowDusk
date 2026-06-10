<p align="center">
  <img src="Brand/ShadowDuskBanner.png" alt="ShadowDusk" />
</p>

# ShadowDusk

A cross-platform HLSL shader compiler for [MonoGame](https://monogame.net/), [KNI](https://github.com/kniEngine/kni), and [FNA](https://fna-xna.github.io/). Compile `.fx` shaders on Linux, macOS, or Windows — no Wine, no Windows SDK, no DirectX install required.

## What it is

**The product is a self-contained, in-memory, cross-platform compiler library** (the `ShadowDusk.Compiler` NuGet package): a developer adds the package and calls `IShaderCompiler.CompileAsync(fx)` to get `.mgfx` bytes on Linux, macOS, or Windows — needing nothing else (no `fxc.exe`, no `mgfxc`, no Wine, no Windows SDK, no native toolchain to install separately; the native pieces ride inside the package). The **CLI** (`mgfxc` dotnet tool) and the **MGCB plugin** are *delivery shapes of the same library* for build-time use. The **in-browser shader fiddle is only a sample of reach — not a separate product.**

## What it does

MonoGame's stock content pipeline shells out to `mgfxc`, a Windows-only tool that depends on `fxc.exe` from the DirectX SDK. ShadowDusk replaces that step with one portable pipeline that produces output a real MonoGame/KNI `Effect` loads and renders like `mgfxc`'s:

```
OpenGL / WebGL:
  HLSL (.fx)
    → DXC (via Vortice.Dxc)  →  SPIR-V
    → SPIRV-Cross             →  GLSL (+ MojoShader-dialect rewrite)
    → .mgfx binary            →  MonoGame Effect loader

DirectX (DX11):
  HLSL (.fx)
    → vkd3d-shader            →  DXBC (SM5)
    → .mgfx binary            →  MonoGame Effect loader

FNA (fx_2_0):
  HLSL (.fx, D3D9-style)
    → vkd3d-shader            →  D3D9 bytecode (SM ≤ 3)
    → .fxb (fx_2_0) binary    →  FNA Effect loader (FNA3D / MojoShader)
```

**OpenGL / WebGL is fully cross-platform and self-contained** — DXC + SPIRV-Cross ride inside the package, so it compiles on Linux, macOS, and Windows with nothing to install.

**DirectX (DX11)** produces DXBC in-process (no `fxc.exe`/`mgfxc`) via two backends behind `IDxbcShaderCompiler`, chosen by `CompilerOptions.DxbcBackend`: the **default** is `d3dcompiler_47` — Microsoft's HLSL compiler, a system DLL **already present on Windows** (not a dependency you install), giving the most `fxc`-faithful output; `vkd3d-shader` is the **opt-in, cross-platform** backend (`DxbcBackend.Vkd3d`) for compiling DX shaders on Linux/macOS. DXC is **not** used for DX11 (it emits DXIL/SM6, not the DXBC/SM ≤ 5 the DX11 runtime loads); its `ps_6_0`/`vs_6_0` output is retained only for the DX12/KNI path.

Supported backends:

| Backend | Output | Status |
|---|---|---|
| OpenGL / DesktopGL | GLSL | Validated end-to-end (10/10 in real MonoGame DesktopGL) |
| DirectX (Windows, DX11) | DXBC (SM5) via vkd3d-shader | Validated end-to-end (10/10 in real MonoGame WindowsDX) |
| WebGL (XNA Fiddle / KNI browser) | GLSL ES | Validated end-to-end (10/10 in real headless KNI WebGL) |
| FNA (`/Profile:FNA` → `.fxb`) | D3D9 fx_2_0 via vkd3d-shader | Validated end-to-end (pixel-identical to `fxc /T fx_2_0` in real FNA — PS-only effects; custom-vertex-shader effects pending) |
| Metal (macOS / iOS) | MSL | Not yet implemented |
| Vulkan | SPIR-V | Future |

> **FNA note.** FNA's documented shader workflow is the deprecated Windows-only
> `fxc.exe /T fx_2_0` (run under Wine on Linux/macOS). `PlatformTarget.Fna` removes that
> entirely: ShadowDusk compiles D3D9-style `.fx` (SM ≤ 3 — `sampler_state`, `tex2D`,
> `COLOR0` semantics) to the legacy fx_2_0 effects binary FNA loads via
> `new Effect(gd, bytes)`, on every OS, with no Wine. One `.fxb` serves every FNA graphics
> backend (FNA translates at load time). Validated against real fxc output rendering in
> real FNA (`validation/FnaValidation`); shaders needing SM4+ features fail loudly with a
> clear diagnostic.

> **KNI HiDef / WebGL2 note.** A single ShadowDusk `.mgfx` loads in both KNI **Reach** (WebGL1) and **HiDef** (WebGL2 / GLSL ES 3.00) — no profile flag and no separate build. KNI converts the legacy GLSL to ES 3.00 at load time, and ShadowDusk emits the `#define`-aliased fragment output that converter expects (GitHub [#7](https://github.com/kaltinril/ShadowDusk/issues/7)). HiDef shader loading needs **KNI ≥ v3.14.9001** (the release that added KNI's runtime converter — any recent KNI qualifies); Reach and desktop GL have no version requirement. After upgrading ShadowDusk to pick up this fix, **recompile your `.fx`** — a `.mgfx` built by an older ShadowDusk keeps the old output and won't load under HiDef.

## Drop-in `mgfxc` replacement

ShadowDusk is a transparent substitute for MonoGame's `mgfxc`. Same CLI flags, same `.mgfx` output format, same exit codes, same MGCB-compatible error messages on stderr. Games using the MonoGame Content Pipeline require zero code changes to switch.

## Delivery shapes

**Library** (`ShadowDusk.Compiler`, type `EffectCompiler : IShaderCompiler`) — **the product.** Add the package, call `CompileAsync(fx)`, get `.mgfx` bytes in-memory:

```csharp
var compiler = new EffectCompiler();
Result<CompiledShader, ShaderError[]> result =
    await compiler.CompileAsync(hlslSource, new CompilerOptions(/* … */));
```

**CLI tool** (`dotnet tool` named `ShadowDuskCLI`) — the same library wrapped for build-time use from MGCB, scripts, or the terminal:

```sh
ShadowDuskCLI MyShader.fx MyShader.mgfx /Profile:OpenGL
```

**WASM library** (`ShadowDusk.Wasm`, type `WasmShaderCompiler : IShaderCompiler`) — the same pipeline running inside .NET WASM for in-browser runtime compilation (the faithful pinned-DXC→WASM + SPIRV-Cross-WASM frontend). Returns `.mgfx` bytes in-memory with no server roundtrip. The in-browser shader fiddle ([samples/ShaderFiddle.Web](samples/ShaderFiddle.Web)) is a **sample** of this reach, not a separate product. See [`docs/HOWTO-WASM-KNI.md`](docs/HOWTO-WASM-KNI.md) for the KNI/Blazor walkthrough.

Every shape shares the same `IShaderCompiler` interface. "Same `.mgfx` output" means behaviorally equivalent and `Effect`-loadable — byte-identity is ShadowDusk's *own* reproducibility (same version + source + target → same bytes), never byte-equality with `mgfxc`.

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
│   ├── ShadowDusk.Core/         # Core types: IShaderCompiler, Result<T,E>, ShaderError,
│   │                            #   CompilerOptions, CompiledShader, ShaderIR, DxbcBackend, SpirvReflector
│   ├── ShadowDusk.HLSL/         # FX9 pre-parser, preprocessor, DXC integration, reflection,
│   │                            #   vkd3d-shader + d3dcompiler DXBC backends
│   ├── ShadowDusk.GLSL/         # SPIR-V → GLSL via SPIRV-Cross + MonoGameGlslRewriter
│   ├── ShadowDusk.Metal/        # SPIR-V → MSL (stub — not yet implemented)
│   ├── ShadowDusk.Compiler/     # EffectCompiler : IShaderCompiler — the consumer-facing product NuGet
│   ├── ShadowDusk.Cli/          # dotnet tool entry point (mgfxc)
│   ├── ShadowDusk.MgcbPlugin/   # MGCB content processor plugin (scaffold)
│   └── ShadowDusk.Wasm/         # In-browser WASM compiler (WasmShaderCompiler), [JSImport] DXC + SPIRV-Cross
├── samples/
│   ├── ShaderFiddle.Web/        # KNI Blazor-WASM in-browser fiddle (sample of reach)
│   ├── ShaderViewer/            # Desktop shader viewer
│   └── mgcb/                    # MGCB content-pipeline sample
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.HLSL.Tests/
│   ├── ShadowDusk.GLSL.Tests/
│   ├── ShadowDusk.Integration.Tests/
│   └── fixtures/
│       ├── shaders/             # Canonical .fx test shaders
│       └── golden/              # Reference .mgfx outputs (DirectX_11/ and OpenGL/)
├── tools/                       # Native binary restore scripts
└── docs/                        # Architecture docs and research (incl. HOWTO-WASM-KNI.md)
```

## Tech stack

- C# 12 / .NET 8
- [Vortice.Dxc](https://github.com/amerkoleci/Vortice.Windows) — managed DXC wrapper (cross-platform, no Windows SDK required)
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) — SPIR-V → GLSL transpilation via P/Invoke
- [vkd3d-shader](https://gitlab.winehq.org/wine/vkd3d) — cross-platform HLSL → DXBC (SM5) for the DirectX backend
- xUnit + FluentAssertions

## Design principles

- **No Windows / Wine requirement.** Every native binary has Linux + macOS builds.
- **Drop-in replacement.** Same CLI flags, same `.mgfx` output, same exit codes and error format as MonoGame's `mgfxc`. Zero changes to existing content pipelines.
- **Deterministic output.** Same source + same target = byte-identical `.mgfx`, given the same compiler version.
- **Fail loudly.** Shader errors surface the source file, line, column, and message exactly as the underlying compiler emitted them.
- **Result-typed errors.** No exceptions for expected shader failures — the API returns `Result<CompiledShader, ShaderError[]>`.

## Acknowledgements

ShadowDusk stands on a lot of excellent prior work. The faithful compilation pipeline is built around — and ships pieces of — these projects:

- **[DirectX Shader Compiler (DXC)](https://github.com/microsoft/DirectXShaderCompiler)** (Microsoft) — the HLSL → SPIR-V frontend, used on desktop via Vortice and compiled to WebAssembly for the in-browser path. The single faithful frontend everywhere.
- **[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)** (Amer Koleci) — managed `Vortice.Dxc` / `Vortice.D3DCompiler` bindings that let us drive DXC and `d3dcompiler_47` without the Windows SDK.
- **[SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross)** (The Khronos Group) — SPIR-V → GLSL transpilation, via P/Invoke on desktop and WebAssembly in the browser; the native package is provided through **[Silk.NET](https://github.com/dotnet/Silk.NET)**.
- **[vkd3d / vkd3d-shader](https://gitlab.winehq.org/wine/vkd3d)** (the Wine project) — the cross-platform HLSL → DXBC backend that makes the DirectX path compilable where `mgfxc` can't run.
- **[MonoGame](https://github.com/MonoGame/MonoGame)** — the runtime we target and the `mgfxc`/`.mgfx` format we faithfully reproduce.
- **[KNI](https://github.com/kniEngine/kni)** (nkast) — the WebAssembly/WebGL-capable MonoGame fork the in-browser sample runs on.
- **[MojoShader](https://github.com/icculus/mojoshader)** (Ryan C. Gordon) — the OpenGL GLSL dialect / shader-bytecode heritage that MonoGame's `.mgfx` OpenGL effects use, which our GLSL rewrite matches.
- **[Emscripten](https://emscripten.org/)** — used to compile DXC and SPIRV-Cross to WebAssembly.
- **[Slang](https://github.com/shader-slang/slang)** (shader-slang) — used **only** in the in-browser sample as an early spike frontend; it is *not* part of the product pipeline (which uses faithful DXC everywhere).
- **[DocFX](https://github.com/dotnet/docfx)** (the .NET Foundation) — planned to generate the documentation site.
- **[xUnit](https://github.com/xunit/xunit)** and **[FluentAssertions](https://github.com/fluentassertions/fluentassertions)** — the test suite.

The test-shader corpus is derived from community MonoGame/HLSL examples, with thanks to:

- **[Penumbra](https://github.com/discosultan/penumbra)** by *discosultan* — several effect shaders.
- **[monogame-hlsl-examples](https://github.com/manbeardgames/monogame-hlsl-examples)** by *manbeardgames* — the tutorial shader set.

See [`docs/test-shader-corpus.md`](docs/test-shader-corpus.md) for per-shader provenance.

## License & contributing

See [`CLAUDE.md`](CLAUDE.md) for coding conventions and agent guidance.
