# ShadowDusk.Compiler

**Cross-platform, in-memory HLSL Effect compiler for MonoGame, KNI, and FNA** — a drop-in replacement for `mgfxc` that compiles `.fx` source to MonoGame/KNI `.mgfx` bytes (or FNA `.fxb`) **at runtime, on Linux, macOS, and Windows**, with no `fxc.exe`, no `mgfxc`, no Wine, and no Windows SDK. Add the package and call the API — every native piece rides inside the package set.

The output loads in a real MonoGame/KNI `Effect` and renders equivalently to `mgfxc`'s — one faithful pipeline (DXC → SPIR-V → SPIRV-Cross → GLSL, or vkd3d-shader → DXBC/D3D9 bytecode) on every OS, byte-identical output across hosts.

## Install

```
dotnet add package ShadowDusk.Compiler
```

## Use

```csharp
using ShadowDusk.Core;
using ShadowDusk.Compiler;

IShaderCompiler compiler = new EffectCompiler();
var result = await compiler.CompileAsync(fxSource, new CompilerOptions
{
    Target = PlatformTarget.OpenGL,   // or DirectX, or Fna
});

if (result.IsFailure)
{
    foreach (var e in result.Error)
        Console.Error.WriteLine($"{e.File}({e.Line},{e.Column}): {e.Code}: {e.Message}");
    return;
}

var effect = new Effect(graphicsDevice, result.Value.Data);
```

Need to compile from a **synchronous** call site (e.g. inside `Content.Load<Effect>`)? Await `compiler.InitializeAsync()` once at startup, then call the synchronous `compiler.Compile(...)` anywhere — same pipeline, byte-identical output.

## Targets

| `CompilerOptions.Target` | Output | Runtime |
|---|---|---|
| `OpenGL` | `.mgfx` (GLSL) | MonoGame DesktopGL, KNI (incl. WebGL) |
| `DirectX` | `.mgfx` (SM5 DXBC) | MonoGame WindowsDX, KNI | 
| `Fna` | `.fxb` (D3D9 fx_2_0) | FNA |

All three compile on every desktop OS and produce the same bytes on every OS. Errors come back as `ShaderError[]` with the file, line, column, and compiler message verbatim.

## Links

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
- CLI flavor (`dotnet tool`): **ShadowDusk.Cli** · In-browser (Blazor WASM) flavor: **ShadowDusk.Wasm**
