<p align="center">
  <img src="Brand/ShadowDuskBanner.png" alt="ShadowDusk" />
</p>

# ShadowDusk

<p align="center">
  <a href="https://www.nuget.org/packages/ShadowDusk.Compiler"><img src="https://img.shields.io/nuget/v/ShadowDusk.Compiler?label=ShadowDusk.Compiler" alt="ShadowDusk.Compiler on NuGet" /></a>
  <a href="https://www.nuget.org/packages/ShadowDusk.Cli"><img src="https://img.shields.io/nuget/v/ShadowDusk.Cli?label=ShadowDuskCLI%20(dotnet%20tool)" alt="ShadowDuskCLI dotnet tool on NuGet" /></a>
  <a href="https://www.nuget.org/packages/ShadowDusk.Wasm"><img src="https://img.shields.io/nuget/v/ShadowDusk.Wasm?label=ShadowDusk.Wasm" alt="ShadowDusk.Wasm on NuGet" /></a>
</p>

A cross-platform HLSL shader compiler for [MonoGame](https://monogame.net/), [KNI](https://github.com/kniEngine/kni), and [FNA](https://fna-xna.github.io/). Compile `.fx` shaders on Linux, macOS, or Windows — no Wine, no Windows SDK, no DirectX install required.

## What it is

ShadowDusk is an in-memory shader compiler library for MonoGame, KNI, and FNA. Add the package to your game, call `CompileAsync(fx)`, and get back `.mgfx` bytes you can load straight into an `Effect` — on Linux, macOS, or Windows, at build time or live at runtime.

```csharp
var compiler = new EffectCompiler();
var result = await compiler.CompileAsync(
    hlslSource, new CompilerOptions { Target = PlatformTarget.OpenGL });

// result.Value.Data is the .mgfx — hand it straight to MonoGame:
var effect = new Effect(graphicsDevice, result.Value.Data);
```

Shader not working? One call shows everything wrong with it — every error and every
warning, for OpenGL and DirectX side by side, with the underlying compiler's full text:

```csharp
Console.WriteLine(await compiler.ValidateAsync(hlslSource));
```

Everything it needs ships inside the package. There's no separate install: no fxc.exe, no mgfxc, no Wine, no Windows SDK. The same library also ships as a **command-line tool** for build-time use — including from MGCB's external-tool hook — and runs in the browser via WebAssembly (the in-browser fiddle is a sample of that reach, not a separate product).

## Why it exists

MonoGame's stock content pipeline shells out to mgfxc, a Windows-only tool that needs fxc.exe from the DirectX SDK. ShadowDusk replaces that one step with a portable pipeline whose output a real MonoGame, KNI, or FNA `Effect` loads and renders the same as mgfxc's — so the same shader build works on any OS, with nothing to install.

## Supported targets

ShadowDusk works with **MonoGame, KNI, and FNA** across these graphics backends. Pick your framework and backend; ShadowDusk emits the right output.

| Backend             | Output         | Status       |
|---------------------|----------------|--------------|
| OpenGL / DesktopGL  | GLSL `.mgfx`   | Supported    |
| DirectX 11          | DXBC `.mgfx`   | Supported    |
| WebGL (KNI browser) | GLSL `.mgfx`   | Supported    |
| Android (on-device) | GLSL `.mgfx`   | Supported    |
| FNA                 | D3D9 `.fxb`    | Supported    |
| Metal (macOS / iOS) | MSL            | Not yet      |
| Vulkan (MonoGame)   | SPIR-V `.mgfx` | Supported    |
| DirectX 12 (MonoGame WindowsDX12) | — | Not yet |

Supported targets are tested end-to-end against the reference compiler. For the exact per-version, per-OS proof status, see the [Validation Matrix](docs/validation-matrix.md). To choose a target (or build a shader-download feature), see the [Choosing a Target](https://kaltinril.github.io/ShadowDusk/guides/choosing-a-target.html) guide. Classic Microsoft XNA 4.0 is out of scope.

<details>
<summary><b>How the pipeline works</b> (you don't need this to use it)</summary>

ShadowDusk runs one faithful pipeline per backend:

```
OpenGL / WebGL / Android:
  HLSL (.fx)  ->  DXC  ->  SPIR-V  ->  SPIRV-Cross  ->  GLSL  ->  .mgfx
DirectX 11:
  HLSL (.fx)  ->  vkd3d-shader  ->  DXBC (SM5)  ->  .mgfx
Vulkan (MonoGame DesktopVK):
  HLSL (.fx)  ->  DXC  ->  SPIR-V  ->  .mgfx (profile 80)
FNA:
  HLSL (.fx, D3D9-style)  ->  vkd3d-shader  ->  D3D9 bytecode  ->  .fxb
```

For **DirectX 11**, the default compiler is the cross-platform **vkd3d-shader**, whose native ships inside the package for all four desktop RIDs, so a DirectX compile produces the same bytes on Linux, macOS, and Windows. On Windows you can opt into Microsoft's `d3dcompiler_47` (a system DLL already present) as a reference-faithful alternative via `CompilerOptions.DxbcBackend`. DXC is not used for DX11 — it emits a newer bytecode (DXIL/SM6) the DX11 runtime can't load — and is reserved for a future DirectX 12 path.
</details>

<details>
<summary><b>Framework notes</b> (output format, FNA, KNI HiDef / WebGL)</summary>

**Output format.** ShadowDusk emits **MGFX v10** by default, the format that loads on MonoGame 3.8.2 and every newer MonoGame, plus KNI. You never set a flag to get correct output. Targeting a newer runtime? Two optional formats load and render exactly like v10:

- MonoGame 3.8.5+ &rarr; `CompilerOptions.MgfxVersion = 11`
- KNI v4.02+ &rarr; `CompilerOptions.Container = EffectContainer.Knifx`

If you're not sure, keep the default. See [Parameters &amp; Caveats](https://kaltinril.github.io/ShadowDusk/guides/parameters-and-caveats.html).

**FNA.** FNA's documented workflow is the deprecated, Windows-only `fxc.exe /T fx_2_0` (run under Wine elsewhere). `PlatformTarget.Fna` removes that: ShadowDusk compiles D3D9-style `.fx` to the fx_2_0 binary FNA loads via `new Effect(gd, bytes)`, on every OS, with no Wine. One `.fxb` serves every FNA backend. Shaders that need SM4+ features fail with a clear diagnostic.

**KNI HiDef / WebGL2.** A single `.mgfx` loads in both KNI Reach (WebGL1) and HiDef (WebGL2) — no profile flag, no separate build. HiDef loading needs KNI v3.14.9001 or newer (any recent KNI qualifies). After upgrading ShadowDusk, recompile your `.fx`: a `.mgfx` built by an older ShadowDusk keeps the old output and won't load under HiDef.
</details>

## Drop-in mgfxc replacement

ShadowDusk is a transparent substitute for MonoGame's mgfxc: same CLI flags, same `.mgfx` output format, same exit codes, same MGCB-compatible error messages. Games using the MonoGame Content Pipeline need zero code changes to switch.

## Delivery shapes

All three shapes share the same `IShaderCompiler` interface and produce the same `.mgfx` bytes; only how you invoke them differs.

**Library** (`ShadowDusk.Compiler`) — the product. Add the package, call `CompileAsync(fx)`, get `.mgfx` bytes in memory (see the example above).

**CLI tool** (`ShadowDuskCLI` dotnet tool) — the same library for build-time use from MGCB, scripts, or the terminal:

```sh
ShadowDuskCLI MyShader.fx MyShader.mgfx /Profile:OpenGL
```

**WASM library** (`ShadowDusk.Wasm`) — the same pipeline running in the browser via WebAssembly, for live in-browser compilation with no server roundtrip. OpenGL output renders live in KNI WebGL; DirectX and FNA output come back as downloads to run in your desktop game. The [in-browser fiddle](samples/ShaderFiddle.Web) is a sample of this. See [`docs/HOWTO-WASM-KNI.md`](docs/HOWTO-WASM-KNI.md) for the KNI/Blazor walkthrough.

> "Same `.mgfx` output" means it loads and renders like mgfxc's, not that the bytes are identical. ShadowDusk's output is deterministic in its own right: the same version, source, and target always give the same bytes.

## Packages

All packages ship together at one shared version. Most projects only need one of the first three.

| Package | NuGet | What it's for |
|---|---|---|
| `ShadowDusk.Compiler` | [![ShadowDusk.Compiler](https://img.shields.io/nuget/v/ShadowDusk.Compiler)](https://www.nuget.org/packages/ShadowDusk.Compiler) | **The product.** The in-memory `.fx` → `.mgfx` compiler library. This is the one to add to your game or tool. |
| `ShadowDusk.Cli` | [![ShadowDusk.Cli](https://img.shields.io/nuget/v/ShadowDusk.Cli)](https://www.nuget.org/packages/ShadowDusk.Cli) | The `ShadowDuskCLI` dotnet tool — the same compiler as a command-line mgfxc replacement: `dotnet tool install -g ShadowDusk.Cli` |
| `ShadowDusk.Wasm` | [![ShadowDusk.Wasm](https://img.shields.io/nuget/v/ShadowDusk.Wasm)](https://www.nuget.org/packages/ShadowDusk.Wasm) | The same pipeline compiled to WebAssembly, for in-browser compilation from Blazor / KNI web apps. |
| `ShadowDusk.ShaderToy` | [![ShadowDusk.ShaderToy](https://img.shields.io/nuget/v/ShadowDusk.ShaderToy)](https://www.nuget.org/packages/ShadowDusk.ShaderToy) | Optional, standalone ShaderToy / GLSL → `.fx` front-end (pure managed, no native deps). |
| `ShadowDusk.Core` | [![ShadowDusk.Core](https://img.shields.io/nuget/v/ShadowDusk.Core)](https://www.nuget.org/packages/ShadowDusk.Core) | Shared types (`IShaderCompiler`, `CompilerOptions`, `Result<T,E>`). Pulled in automatically as a dependency. |
| `ShadowDusk.HLSL` | [![ShadowDusk.HLSL](https://img.shields.io/nuget/v/ShadowDusk.HLSL)](https://www.nuget.org/packages/ShadowDusk.HLSL) | HLSL front-end (FX pre-parser, DXC, DXBC backends). Pulled in automatically as a dependency. |
| `ShadowDusk.GLSL` | [![ShadowDusk.GLSL](https://img.shields.io/nuget/v/ShadowDusk.GLSL)](https://www.nuget.org/packages/ShadowDusk.GLSL) | SPIR-V → GLSL transpilation and the MonoGame GLSL rewrite. Pulled in automatically as a dependency. |

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
│   ├── ShadowDusk.ShaderToy/    # ShaderToy / GLSL → .fx front-end (optional, pure managed)
│   ├── ShadowDusk.Metal/        # SPIR-V → MSL (stub — not yet implemented)
│   ├── ShadowDusk.Compiler/     # EffectCompiler : IShaderCompiler — the consumer-facing product NuGet
│   ├── ShadowDusk.Cli/          # dotnet tool entry point (mgfxc)
│   ├── ShadowDusk.MgcbPlugin/   # MGCB content processor plugin (scaffold, not published)
│   └── ShadowDusk.Wasm/         # In-browser WASM compiler (WasmShaderCompiler), [JSImport] DXC + SPIRV-Cross
├── samples/
│   ├── ShaderFiddle.Web/        # KNI Blazor-WASM in-browser fiddle (sample of reach)
│   ├── ShaderViewer/            # Desktop shader viewer
│   └── mgcb/                    # MGCB content-pipeline sample
├── tests/
│   ├── ShadowDusk.*.Tests/      # Unit tests per library (Core, HLSL, GLSL, Compiler, ShaderToy)
│   ├── ShadowDusk.Integration.Tests/  # End-to-end compiles (CLI, native DXC + SPIRV-Cross)
│   ├── ShadowDusk.ImageTests/   # Offscreen-GL render comparisons
│   ├── ShadowDusk.BrowserTests/ # Playwright KNI WebGL harness
│   └── fixtures/
│       ├── shaders/             # Canonical .fx test shaders
│       └── golden/              # Reference .mgfx outputs (DirectX_11/ and OpenGL/)
├── validation/                  # In-engine render-proof drivers (real MonoGame / KNI / FNA)
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
- **[DocFX](https://github.com/dotnet/docfx)** (the .NET Foundation) — generates the published [documentation site](https://kaltinril.github.io/ShadowDusk/).
- **[xUnit](https://github.com/xunit/xunit)** and **[FluentAssertions](https://github.com/fluentassertions/fluentassertions)** — the test suite.

The test-shader corpus is derived from community MonoGame/HLSL examples, with thanks to:

- **[Penumbra](https://github.com/discosultan/penumbra)** by *discosultan* — several effect shaders.
- **[monogame-hlsl-examples](https://github.com/manbeardgames/monogame-hlsl-examples)** by *manbeardgames* — the tutorial shader set.
- **[Nez](https://github.com/prime31/Nez)** by *prime31* (MIT) — real shipping `.fx` effects vendored as compile-level regression inputs.

See [`docs/test-shader-corpus.md`](docs/test-shader-corpus.md) for per-shader provenance.

## License & contributing

See [`CLAUDE.md`](CLAUDE.md) for coding conventions and agent guidance.
