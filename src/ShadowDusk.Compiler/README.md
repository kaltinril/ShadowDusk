# ShadowDusk.Compiler

**An in-memory HLSL shader compiler for MonoGame, KNI, and FNA.** Add the package, call `CompileAsync(fx)`, and get back `.mgfx` bytes (or an FNA `.fxb`) you can load straight into an `Effect` — on Linux, macOS, or Windows, at build time or live at runtime.

It's a drop-in replacement for mgfxc: the output loads in a real MonoGame/KNI `Effect` and renders the same as mgfxc's, on every OS. Nothing extra to install (no fxc.exe, no mgfxc, no Wine, no Windows SDK) — every native piece ships inside the package.

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

### Output format

By default you get **MGFX v10** (`.mgfx`), which loads on MonoGame 3.8.2 and every newer MonoGame, plus KNI. You never set a flag to get correct output.

Targeting a newer runtime? Two optional formats load and render exactly like v10:

- MonoGame 3.8.5+ &rarr; `CompilerOptions.MgfxVersion = 11`
- KNI v4.02+ &rarr; `CompilerOptions.Container = EffectContainer.Knifx`

If you're not sure, keep the default.

Prefer to pick a whole target (backend **and** container) in one value? Set `CompilerOptions.Profile` to a `CapabilityProfile` (e.g. `CapabilityProfile.KniGL_4_02` for KNIFX on OpenGL); a profile fully specifies the output and overrides `Target` / `Container` / `MgfxVersion`. For an in-app compile, `RuntimeProfileDetector.Recommend(typeof(Game).Assembly, target)` returns the proven profile for the loaded framework.

## Links

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
- CLI flavor (`dotnet tool`): **ShadowDusk.Cli** · In-browser (Blazor WASM) flavor: **ShadowDusk.Wasm**
