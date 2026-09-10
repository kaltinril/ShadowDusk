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

Every code it reports is listed in the [Diagnostic Codes](https://kaltinril.github.io/ShadowDusk/diagnostics.html) registry ([`docs/error-codes.md`](docs/error-codes.md)).

Everything it needs ships inside the package. There's no separate install: no fxc.exe, no mgfxc, no Wine, no Windows SDK. The same library also ships as a **command-line tool** and as an **MGCB content-processor plugin** for build-time use, and runs in the browser via WebAssembly (the in-browser fiddle is a sample of that reach, not a separate product).

## Why it exists

MonoGame's stock content pipeline compiles shaders with the same engine as mgfxc, which needs fxc.exe from the DirectX SDK and only runs on Windows. ShadowDusk replaces that one step with a portable pipeline whose output a real MonoGame, KNI, or FNA `Effect` loads and renders the same as mgfxc's — so the same shader build works on any OS, with nothing to install.

## Supported targets

ShadowDusk works with **MonoGame, KNI, and FNA** across these graphics backends. Pick your framework and backend; ShadowDusk emits the right output.

| Backend             | Output         | Status       |
|---------------------|----------------|--------------|
| OpenGL / DesktopGL  | GLSL `.mgfx`   | Supported    |
| DirectX 11          | DXBC `.mgfx`   | Supported    |
| WebGL (KNI browser) | GLSL `.mgfx`   | Supported    |
| Android (on-device) | GLSL `.mgfx`   | Supported (byte-identical to the desktop build) |
| FNA                 | D3D9 `.fxb`    | Supported    |
| Metal (macOS / iOS) | MSL            | Not yet      |
| Vulkan (MonoGame)   | SPIR-V `.mgfx` | Supported    |
| DirectX 12 (MonoGame WindowsDX12) | SM6 DXIL `.mgfx` | Supported |

Supported targets are tested end-to-end against the reference compiler (on-device Android via byte-identity: its output is byte-identical to the desktop build, whose renders are proven — the on-device pixel diff is a tracked follow-up). For the exact per-version, per-OS proof status, see the [Validation Matrix](docs/validation-matrix.md). To choose a target (or build a shader-download feature), see the [Choosing a Target](https://kaltinril.github.io/ShadowDusk/guides/choosing-a-target.html) guide. Classic Microsoft XNA 4.0 is out of scope.

<details>
<summary><b>How the pipeline works</b> (you don't need this to use it)</summary>

ShadowDusk runs one faithful pipeline per backend:

```
OpenGL / WebGL / Android:
  HLSL (.fx)  ->  DXC  ->  SPIR-V  ->  SPIRV-Cross  ->  GLSL  ->  .mgfx
DirectX 11:
  HLSL (.fx)  ->  vkd3d-shader  ->  DXBC (SM5)  ->  .mgfx
DirectX 12 (MonoGame WindowsDX12):
  HLSL (.fx)  ->  DXC  ->  DXIL (SM6)  ->  .mgfx (profile 2)
Vulkan (MonoGame DesktopVK):
  HLSL (.fx)  ->  DXC  ->  SPIR-V  ->  .mgfx (profile 80)
FNA:
  HLSL (.fx, D3D9-style)  ->  vkd3d-shader  ->  D3D9 bytecode  ->  .fxb
```

For **DirectX 11**, the default compiler is the cross-platform **vkd3d-shader**, whose native ships inside the package for all four desktop RIDs, so a DirectX compile produces the same bytes on Linux, macOS, and Windows. On Windows you can opt into Microsoft's `d3dcompiler_47` (a system DLL already present) as a reference-faithful alternative via `CompilerOptions.DxbcBackend`. DXC is not used for DX11 — it emits a newer bytecode (DXIL/SM6) the DX11 runtime can't load — that DXIL output is instead what the DirectX 12 target ships directly (`PlatformTarget.DirectX12`, auto-selected for consumers targeting `WindowsDX12`).
</details>

<details>
<summary><b>Framework notes</b> (output format, FNA, KNI HiDef / WebGL)</summary>

**Output format.** ShadowDusk emits **MGFX v10** by default, the format that loads on MonoGame 3.8.1.263 (the measured floor) and every newer MonoGame, plus KNI. You never set a flag to get correct output. Targeting a newer runtime? Two optional formats load and render exactly like v10:

- MonoGame 3.8.5+ &rarr; `CompilerOptions.MgfxVersion = 11`
- KNI v4.02+ &rarr; `CompilerOptions.Container = EffectContainer.Knifx`

If you're not sure, keep the default. See [Parameters &amp; Caveats](https://kaltinril.github.io/ShadowDusk/guides/parameters-and-caveats.html).

**FNA.** FNA's documented workflow is the deprecated, Windows-only `fxc.exe /T fx_2_0` (run under Wine elsewhere). `PlatformTarget.Fna` removes that: ShadowDusk compiles D3D9-style `.fx` to the fx_2_0 binary FNA loads via `new Effect(gd, bytes)`, on every OS, with no Wine. One `.fxb` serves every FNA backend. Shaders that need SM4+ features fail with a clear diagnostic.

**KNI HiDef / WebGL2.** A single `.mgfx` loads in both KNI Reach (WebGL1) and HiDef (WebGL2) — no profile flag, no separate build. HiDef loading needs KNI v3.14.9001 or newer (any recent KNI qualifies). After upgrading ShadowDusk, recompile your `.fx`: a `.mgfx` built by an older ShadowDusk keeps the old output and won't load under HiDef.
</details>

## Drop-in mgfxc replacement

ShadowDusk is a transparent substitute for MonoGame's mgfxc: same CLI flags, same `.mgfx` output format, same exit codes, same MGCB-compatible error messages. A build step that shells out to `mgfxc` can call `ShadowDuskCLI` instead with nothing downstream changing.

> **A `PATH` override does not redirect MGCB.** MGCB compiles `.fx` **in-process** and launches no external effect compiler (measured against `dotnet mgcb` 3.8.2.1105, 3.8.4.1, and 3.8.5), so putting ShadowDusk on `PATH` as `mgfxc` changes nothing. For MGCB, use the content-processor plugin below.

## Delivery shapes

Every shape runs the same pipeline and produces the same `.mgfx` bytes; only how you invoke it differs.

**Library** (`ShadowDusk.Compiler`) — the product. Add the package, call `CompileAsync(fx)`, get `.mgfx` bytes in memory (see the example above).

**CLI tool** (`ShadowDuskCLI` dotnet tool) — the same library for build-time use from scripts or the terminal:

```sh
ShadowDuskCLI MyShader.fx MyShader.mgfx /Profile:OpenGL
```

**MGCB plugin** (`ShadowDusk.MgcbPlugin`) — the same library as a MonoGame Content Builder content processor, so `.mgcb` builds compile `.fx → .xnb` through ShadowDusk in MGCB's own process. Add the package, then one reference line plus the importer/processor names:

```
/reference:$(NuGetPackageRoot)shadowdusk.mgcbplugin/<version>/tools/net8.0/any/ShadowDusk.MgcbPlugin.dll

#begin MyShader.fx
/importer:ShadowDuskEffectImporter
/processor:ShadowDuskEffectProcessor
/build:MyShader.fx
```

The target comes from the content project's own `/platform:` line (`DesktopVK` and `WindowsDX12` included on MonoGame 3.8.5), and the `.mgfx` inside the `.xnb` is byte-for-byte what the CLI emits. See [MGCB Content Pipeline](https://kaltinril.github.io/ShadowDusk/guides/mgcb-content-pipeline.html).

**Content Builder library** (`ShadowDusk.ContentPipeline`) — the same importer and processor as a normal library, for MonoGame 3.8.5's code-centric **Content Builder project** (the template default since 3.8.5, where a C# `ContentBuilder` you own replaces the `.mgcb`). Add the package to the Builder project and pass the two instances:

```csharp
using ShadowDusk.ContentPipeline;

content.Include<WildcardRule>("Effects/*.fx", new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());
```

Pass the instances (auto-discovery by extension picks MonoGame's own pair); the target follows the Builder's `-p` platform. Proven at rung 4 in a real 3.8.5 `ContentBuilder`: payload byte-identical to the CLI, envelope byte-identical to the stock build, and pixel-identical through `Content.Load<Effect>` on MonoGame 3.8.5. See [MonoGame 3.8.5 Content Builder](https://kaltinril.github.io/ShadowDusk/guides/content-builder.html).

**Direct `.xnb` output** — replace your content pipeline without changing a line of your game's code. ShadowDusk writes the `.xnb` itself, so `Content.Load<Effect>("MyShader")` keeps working and MGCB is out of the picture entirely. On the CLI, just name an `.xnb` output:

```sh
ShadowDuskCLI MyShader.fx Content/MyShader.xnb /Profile:OpenGL
```

Or from the library:

```csharp
var result = await new EffectCompiler().CompileAsync(fx, new CompilerOptions { Target = PlatformTarget.OpenGL });
File.WriteAllBytes("Content/MyShader.xnb", result.Value.ToXnb());
```

The XNB platform byte is **derived** from the target you already picked, never something you select, and the payload inside is byte-for-byte the `.mgfx` the same call would emit:

| `/Profile:` (or `--target-runtime`) | XNB platform byte | Runtimes that accept it |
|---|---|---|
| `OpenGL` (`monogame-gl`, `monogame-gl-v11`, `kni-knifx`) | `'d'` (DesktopGL) | MonoGame DesktopGL / Android / iOS / macOS / Web, KNI (every GL platform) |
| `DirectX_11` (`monogame-dx`) — **the CLI default** | `'w'` (Windows) | MonoGame WindowsDX, KNI WinForms.DX11 |
| `DirectX_12` | `'G'` | MonoGame WindowsDX12 (3.8.5+) |
| `Vulkan` | `'V'` | MonoGame DesktopVK (3.8.5+) |
| `FNA` (`fna`) | `'w'` | FNA (the only byte in FNA's list that MonoGame also accepts; the payload is the `.fxb`) |

Every runtime checks the byte only for **membership in its whitelist**, never against the platform actually running (measured on MonoGame, KNI and FNA), so one `/Profile:OpenGL` `.xnb` serves DesktopGL, Android, iOS, macOS and Web alike — an mgcb Android build would say `'a'` where ShadowDusk says `'d'`, and it does not matter.

> **Name the profile.** With no `/Profile:` the CLI defaults to `DirectX_11` (mgfxc parity), so an `.xnb` written that way loads in a WindowsDX game and fails in a DesktopGL one with *"This MGFX effect was built for a different platform!"*. The CLI warns (`SD0029`) when an `.xnb` is written with the implicit default; passing any profile silences it.

Proven at rung 4 on **MonoGame** (WindowsDX and DesktopGL), **KNI** (4.2.9001 and 4.3.9001, MGFX v10 and KNIFX payloads) and **FNA** (26.06): a real `ContentManager.Load<Effect>` on the ShadowDusk-written file renders pixel-identical to the reference build on every one of them (`validation/XnbContentLoad`, `XnbContentLoadGl`, `KniXnbContentLoad`, and the `.xnb` arm of `FnaValidation`). ShadowDusk's `.xnb` also loads on **KNI 4.2.9001, where a stock MonoGame-mgcb `.xnb` does not** (KNI 4.2's reader-name resolver rejects mgcb's type-reader manifest; ShadowDusk writes the XNA-4.0 name every runtime accepts).

**SkiaSharp / SkSL converter** (`SkslConverter`) — converts an `.fx` pixel shader to an [SkSL runtime effect](https://skia.org/docs/user/sksl/) for `SKRuntimeEffect`, so the same shader source can serve a SkiaSharp render path:

```csharp
var result = SkslConverter.Convert(fxSource, new SkslConvertOptions());
using var effect = SKRuntimeEffect.CreateShader(result.Value.SkslText, out var errors);
```

Know the limits before reaching for it — they are Skia's, not ShadowDusk's, and the converter enforces them **loudly** rather than emitting something that renders wrong. SkSL runtime effects have **no vertex stage and no varyings at all**: a pixel shader gets its coordinate plus uniforms and nothing else. That means a shader that *reads an interpolated input* (a vertex color, a custom interpolant) does not convert **even though it is purely a pixel shader** — the converter refuses it by name, with an explicit opt-in (`TreatVaryingsAsUniforms`) if a per-draw constant is acceptable. The convertible set is fragment-only, coordinate-driven effects with uniform inputs: post-process, tint, gradient, SDF work. Evidence bar: rendered-image fidelity against the original HLSL's math in real Skia (there is no reference compiler for SkSL, so this is **not** an `mgfxc`-equivalence claim).

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
| `ShadowDusk.MgcbPlugin` | [![ShadowDusk.MgcbPlugin](https://img.shields.io/nuget/v/ShadowDusk.MgcbPlugin)](https://www.nuget.org/packages/ShadowDusk.MgcbPlugin) | The MGCB content-processor plugin for `.mgcb` files (tools-only: one `/reference:` line). |
| `ShadowDusk.ContentPipeline` | [![ShadowDusk.ContentPipeline](https://img.shields.io/nuget/v/ShadowDusk.ContentPipeline)](https://www.nuget.org/packages/ShadowDusk.ContentPipeline) | The same importer/processor as a library, for MonoGame 3.8.5's Content Builder project. |

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (≥ 8.0.100) **and** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) — the libraries multi-target `net8.0` and `net10.0`, so both are needed to build the solution. Consuming the packages needs only one of them.

**One command to get from a fresh clone to a verified build** (needs PowerShell 7+, which runs on Windows, Linux and macOS):

```sh
pwsh tools/setup-local-testing.ps1                    # prerequisites, restore, build, full test suite, smoke compile
pwsh tools/setup-local-testing.ps1 -WithRenderGates   # also the GPU render proofs (Windows + a real GPU)
```

It prints a PASS/WARN/FAIL line per step and never skips anything silently — a step that can't run says why and gives you the command that fixes it. The most common first-run failure is having only one .NET SDK: the libraries multi-target `net8.0` **and** `net10.0`, so you need both.

The individual steps, if you'd rather run them yourself: DXC binaries come from the `Vortice.Dxc` NuGet package automatically, and SPIRV-Cross native binaries are downloaded by `tools/restore.ps1` / `tools/restore.sh`:

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
│   ├── ShadowDusk.MgcbPlugin/   # MGCB content-processor plugin (ShadowDuskEffectImporter/Processor), tools-only
│   ├── ShadowDusk.ContentPipeline/ # The same importer/processor as a library, for the MonoGame 3.8.5 Content Builder
│   └── ShadowDusk.Wasm/         # In-browser WASM compiler (WasmShaderCompiler), [JSImport] DXC + SPIRV-Cross
├── samples/
│   ├── ShaderFiddle.Web/        # KNI Blazor-WASM in-browser fiddle (sample of reach)
│   ├── ShaderToyViewer/         # Interactive ShaderToy viewer: runtime convert → in-memory compile → render
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

- C# 12 / .NET 8 + .NET 10 (libraries multi-target `net8.0;net10.0`)
- [Vortice.Dxc](https://github.com/amerkoleci/Vortice.Windows) — managed DXC wrapper (cross-platform, no Windows SDK required)
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) — SPIR-V → GLSL transpilation via P/Invoke
- [vkd3d-shader](https://gitlab.winehq.org/wine/vkd3d) — cross-platform HLSL → DXBC (SM5) for the DirectX backend
- xUnit + Shouldly

## Design principles

- **No Windows / Wine requirement.** Every native binary has Linux + macOS builds.
- **Drop-in replacement.** Same CLI flags, same `.mgfx` output, same exit codes and error format as MonoGame's `mgfxc`. A build step that invokes `mgfxc` can invoke it instead. MGCB itself compiles in-process, so it is integrated with the `ShadowDusk.MgcbPlugin` content processor rather than a `PATH` override.
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
- **[Slang](https://github.com/shader-slang/slang)** (shader-slang) — two distinct roles, and the distinction is load-bearing. **As a compiler inside the pipeline: no, permanently** — Slang never replaces DXC for HLSL (measured: two DXC flags don't forward through Slang's API, making byte-identity unprovable; the pipeline uses faithful DXC everywhere; an early in-browser spike that used Slang as a substitute frontend is dead, sample-only reference). **As an input language: yes, the HLSL-compatible subset** — ShadowDusk accepts `.slang` source as a pure text transform: entry points marked with Slang's own `[shader("vertex")]` / `[shader("fragment")]` attributes, the technique block synthesized (Slang has no technique/pass concept), and the shader body compiled by the **same pipeline as every `.fx`**. Nothing extra to install on any platform, browser included — no Slang binary is shipped or invoked. Slang-*only* language features (`import` modules, generics, `extension`s) are rejected with a clear named error rather than approximated. No route through Slang is `mgfxc`-equivalent; `mgfxc` cannot read Slang at all.
- **[DocFX](https://github.com/dotnet/docfx)** (the .NET Foundation) — generates the published [documentation site](https://kaltinril.github.io/ShadowDusk/).
- **[xUnit](https://github.com/xunit/xunit)** and **[Shouldly](https://github.com/shouldly/shouldly)** — the test suite.

The test-shader corpus is derived from community MonoGame/HLSL examples, with thanks to:

- **[Penumbra](https://github.com/discosultan/penumbra)** by *discosultan* — several effect shaders.
- **[monogame-hlsl-examples](https://github.com/manbeardgames/monogame-hlsl-examples)** by *manbeardgames* — the tutorial shader set.
- **[Nez](https://github.com/prime31/Nez)** by *prime31* (MIT) — real shipping `.fx` effects vendored as compile-level regression inputs.

See [`docs/test-shader-corpus.md`](docs/test-shader-corpus.md) for per-shader provenance.

## License & contributing

See [`CLAUDE.md`](CLAUDE.md) for coding conventions and agent guidance.
