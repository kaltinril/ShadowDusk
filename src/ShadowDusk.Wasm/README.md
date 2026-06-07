# ShadowDusk.Wasm

**In-browser HLSL → `.mgfx` shader compiler for MonoGame / KNI**, for **Blazor WebAssembly** (`net8.0-browser`) apps. Compile `.fx` shaders to MonoGame `.mgfx` bytes **at runtime, in the browser** — no server round-trip, no `fxc.exe`, no `mgfxc`, no native install.

It runs the same faithful pipeline as the desktop compiler (pinned **DXC → SPIR-V → SPIRV-Cross → GLSL**), so the in-browser output matches the desktop/CLI output. The DXC + SPIRV-Cross WebAssembly modules ride **inside this package** as Blazor static web assets and self-register — **you wire nothing.**

## Install

```
dotnet add package ShadowDusk.Wasm
```

(or search **ShadowDusk** in Visual Studio's NuGet UI and install **ShadowDusk.Wasm**.)

The `dxcompiler.wasm` / `spirv-cross.wasm` modules ship in the package and are served automatically at `_content/ShadowDusk.Wasm/…` — there is nothing to copy or configure.

## Use (KNI / MonoGame Blazor WASM)

```csharp
using ShadowDusk.Core;
using ShadowDusk.Wasm;

// 1. Compile .fx -> .mgfx bytes, in the browser, at runtime.
IShaderCompiler compiler = new WasmShaderCompiler();
var result = await compiler.CompileAsync(fxSource, new CompilerOptions
{
    Target = PlatformTarget.OpenGL,   // WebGL / KNI
});

if (result.IsFailure)
{
    foreach (var e in result.Error)
        Console.Error.WriteLine($"{e.Code}: {e.Message}");
    return;
}

byte[] mgfx = result.Value.Data;

// 2. Load the bytes into a real KNI/MonoGame Effect and render.
var effect = new Effect(graphicsDevice, mgfx);
```

That's it: search → install → call `WasmShaderCompiler.CompileAsync` → feed the bytes to `new Effect(gd, bytes)`.

## Notes

- **Target framework:** `net8.0-browser` (Blazor WebAssembly / KNI web).
- **Self-contained:** the WASM native modules are inside the package; first use downloads them with your app's `_framework`/`_content` assets.
- **KNI HiDef / WebGL2:** a single `.mgfx` loads in both KNI Reach (WebGL1) and HiDef (WebGL2 / GLSL ES 3.00) — no flag, no separate build.
- **Output parity:** byte-identical to the desktop/CLI compiler for the same source + target.

See the [ShadowDusk repository](https://github.com/kaltinril/ShadowDusk) for the full pipeline, samples (`samples/ShaderFiddle.Web`), and the desktop library (`ShadowDusk.Compiler`) / `mgfxc` CLI tool.
