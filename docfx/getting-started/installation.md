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

## The CLI tool (`mgfxc`)

Install the drop-in `mgfxc` replacement as a global tool:

```sh
dotnet tool install --global ShadowDusk.Cli
```

This provides an `mgfxc` command with the same flags and `.mgfx` output as MonoGame's tool. See the [CLI Reference](../cli/index.md) and the [Drop-in mgfxc](../guides/dropin-mgfxc.md) guide.

> **Default-target caveat:** the CLI's default `/Profile` is **`DirectX_11`**, while the library's <xref:ShadowDusk.Core.CompilerOptions.Target> default is **`OpenGL`**. Always pass the target you want explicitly. This is called out again on the [Quickstart](in-memory-quickstart.md) and [CLI Reference](../cli/index.md) pages.

## The in-browser (WASM) library

For in-browser runtime compilation (KNI / Blazor WebAssembly), add the `ShadowDusk.Wasm` package. It self-registers as Blazor static web assets — see [In-Browser (KNI/Blazor WASM)](../guides/in-browser-kni-blazor.md).

## DirectX backend & native tools

For **DirectX (DX11)**, the **default** backend is `d3dcompiler_47` — a system DLL **already part of Windows** (you don't install it), so DX compilation works out of the box on Windows with the most `fxc`-faithful output. The **cross-platform** DirectX backend is `vkd3d-shader` (opt-in via `CompilerOptions.DxbcBackend = DxbcBackend.Vkd3d`), a restored (non-redistributed) native artifact for Linux/macOS — see [Restore Native Tools](restore-native-tools.md) and [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md). The OpenGL/WebGL in-memory path needs no extra restore and is cross-platform out of the box.

## Building from source

```sh
git clone https://github.com/kaltinril/ShadowDusk.git
cd ShadowDusk
./tools/restore.sh        # Linux / macOS  (.\tools\restore.ps1 on Windows)
dotnet build ShadowDusk.slnx
```

See [Restore Native Tools](restore-native-tools.md) for what the restore script does.
