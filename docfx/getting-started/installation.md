# Installation

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (≥ 8.0.100)

That's it. DXC binaries come from the `Vortice.Dxc` NuGet package automatically, and the SPIRV-Cross native binary ships transitively through the `Silk.NET.SPIRV.Cross.Native` NuGet — both are restored by `dotnet restore` into the package cache. There is **no separate native install** for the in-memory OpenGL/WebGL path.

## The library (the product)

Add the `ShadowDusk.Compiler` package to your project:

```sh
dotnet add package ShadowDusk.Compiler
```

Then call it from code — see the [In-Memory Quickstart](in-memory-quickstart.md):

```csharp
var compiler = new ShadowDusk.Compiler.EffectCompiler();
var result = await compiler.CompileAsync(hlslSource,
    new ShadowDusk.Core.CompilerOptions { Target = ShadowDusk.Core.PlatformTarget.OpenGL });
```

## The CLI tool (`ShadowDuskCLI`)

Install the drop-in `mgfxc` replacement as a global tool:

```sh
dotnet tool install --global ShadowDusk.Cli
```

This provides a `ShadowDuskCLI` command with the same flags and `.mgfx` output as MonoGame's tool. See the [CLI Reference](../cli/index.md) and the [Drop-in mgfxc](../guides/dropin-mgfxc.md) guide.

> **Default-target caveat:** the CLI's default `/Profile` is **`DirectX_11`**, while the library's <xref:ShadowDusk.Core.CompilerOptions.Target> default is **`OpenGL`**. Always pass the target you want explicitly. This is called out again on the [Quickstart](in-memory-quickstart.md) and [CLI Reference](../cli/index.md) pages.

## The in-browser (WASM) library

For in-browser runtime compilation (KNI / Blazor WebAssembly), add the `ShadowDusk.Wasm` package. It self-registers as Blazor static web assets — see [In-Browser (KNI/Blazor WASM)](../guides/in-browser-kni-blazor.md).

## Targeting FNA

[FNA](https://fna-xna.github.io/) uses the **same `ShadowDusk.Compiler` package** — there is nothing FNA-specific to install on ShadowDusk's side:

```sh
dotnet add package ShadowDusk.Compiler
```

Two things differ from the MonoGame/KNI path, both on FNA's side, not ShadowDusk's:

- **FNA is not a NuGet package.** Unlike MonoGame and KNI, FNA is consumed as a **project reference** — a git clone/submodule of [FNA-XNA/FNA](https://github.com/FNA-XNA/FNA) plus its native `fnalibs` (SDL3, FNA3D, FAudio) — per FNA's own setup docs. (Community/unofficial FNA NuGet builds exist, but the project reference is FNA's documented, supported path.) You add `ShadowDusk.Compiler` to that existing FNA project exactly as above.
- **Different output container.** For `PlatformTarget.Fna`, ShadowDusk emits a D3D9 **fx_2_0 `.fxb`** (Shader Model ≤ 3), not the `.mgfx` MonoGame/KNI load. FNA reads it through MojoShader at runtime via `new Effect(graphicsDevice, fxbBytes)`. See [Compiling for FNA](in-memory-quickstart.md#compiling-for-fna) in the quickstart.

The FNA path is cross-platform and uses the same `vkd3d-shader` native as the cross-platform DirectX backend — which **ships inside the NuGet package** for all four desktop RIDs, so there is nothing to restore or install (the [restore script](restore-native-tools.md) is only for building ShadowDusk itself from source).

## DirectX backend & native tools

For **DirectX (DX11)**, the **default** backend is `d3dcompiler_47` — a system DLL **already part of Windows** (you don't install it), so DX compilation works out of the box on Windows with the most `fxc`-faithful output. The **cross-platform** DirectX backend is `vkd3d-shader` (opt-in via `CompilerOptions.DxbcBackend = DxbcBackend.Vkd3d`); its native binaries for all four desktop RIDs (win-x64, linux-x64, osx-x64, osx-arm64) **ship inside the NuGet package** — nothing to install. (The [Restore Native Tools](restore-native-tools.md) script is only for building ShadowDusk itself from source.) See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md). The OpenGL/WebGL in-memory path needs no extra restore and is cross-platform out of the box.

## Building from source

```sh
git clone https://github.com/kaltinril/ShadowDusk.git
cd ShadowDusk
./tools/restore.sh        # Linux / macOS  (.\tools\restore.ps1 on Windows)
dotnet build ShadowDusk.slnx
```

See [Restore Native Tools](restore-native-tools.md) for what the restore script does.
