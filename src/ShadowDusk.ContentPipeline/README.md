# ShadowDusk.ContentPipeline

**ShadowDusk's MonoGame content-pipeline importer and processor as a normal library, for MonoGame 3.8.5's Content Builder project.** Reference the package, pass `new ShadowDuskEffectImporter()` and `new ShadowDuskEffectProcessor()` in your `ContentBuilder`, and your `.fx` shaders compile to `.xnb` **through ShadowDusk, inside your Builder's own process** — no `mgfxc`, no `fxc.exe`, no Wine, no Windows SDK, on Linux, macOS, and Windows build agents alike.

This is a **delivery shape of the ShadowDusk compiler library**, not a second compiler. The processor builds a `CompilerOptions` from the build context and calls the same `EffectCompiler` the [ShadowDusk CLI](https://www.nuget.org/packages/ShadowDusk.Cli) and the runtime API call, so **the `.mgfx` bytes inside the `.xnb` are byte-for-byte what the CLI emits for the same source and target.** It compiles the exact same source files as [`ShadowDusk.MgcbPlugin`](https://www.nuget.org/packages/ShadowDusk.MgcbPlugin), which remains the package for `.mgcb` files and MGCB (a tools-only package that cannot be referenced from C# — that is why this one exists).

## Install

In your Content Builder project (the `MonoGame.ContentBuilder.CSharp` template, short name `mgcb`):

```
dotnet add package ShadowDusk.ContentPipeline
```

The template's own package list stays as it is (`MonoGame.Framework.Content.Pipeline`, `MonoGame.Framework.Native`, and the `MonoGame.Tool.*` / `MonoGame.Library.*` packages). ShadowDusk's assemblies and its pinned DXC, SPIRV-Cross, and vkd3d-shader natives flow into the Builder's `bin/` through normal NuGet asset flow; nothing has to be installed separately.

## Use

```csharp
using Microsoft.Xna.Framework.Content.Pipeline;
using MonoGame.Framework.Content.Pipeline.Builder;
using ShadowDusk.ContentPipeline;

var builder = new Builder();
builder.Run(args);
return builder.FailedToBuild > 0 ? -1 : 0;

public class Builder : ContentBuilder
{
    public override IContentCollection GetContentCollection()
    {
        var content = new ContentCollection();

        // Pass the INSTANCES. Leaving them off lets the Builder auto-discover an importer
        // for .fx, and with both ShadowDusk's and MonoGame's pairs loaded that picks
        // MonoGame's own EffectImporter/EffectProcessor, silently (measured).
        content.Include<WildcardRule>("Effects/*.fx",
            new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());

        content.Include<WildcardRule>("Font/*.spritefont");
        content.Include("splash-screen.png");
        return content;
    }
}
```

That is the whole setup. **The target follows the Builder's platform** (`-p` / `$(MonoGamePlatform)`), and the output is the backwards-compatible MGFX v10 container every MonoGame 3.8.1.263+ and KNI runtime loads. You never pick a version, a format, or a flag to get correct output.

| Builder platform (`-p`) | ShadowDusk target |
|---|---|
| `Windows` | DirectX 11 (DXBC SM5) |
| `DesktopGL`, `MacOSX`, `iOS`, `Android`, `RaspberryPi`, `Web`, `NativeClient` | OpenGL (GLSL) |
| `DesktopVK` | Vulkan (SPIR-V) |
| `WindowsDX12` | DirectX 12 (DXIL SM6) — build on Windows, where DXIL is signed |
| `PlayStation4`, `PlayStation5`, `XboxOne`, `XboxSeries`, `Switch`, `Xbox360`, `Stadia` | not supported — fails loudly (`SD0501`) |

Three things worth knowing, all measured against the real 3.8.5 Builder:

- **Pass the instances** (as above). Extension auto-discovery picks MonoGame's stock pair when both are loaded; there is no way for a second `.fx` importer to win it.
- **`DebugMode` defaults to optimized.** The Builder has no build-configuration argument, so `DebugMode = Auto` (which follows the configuration on MGCB) means optimized here. Want debug info: `new ShadowDuskEffectProcessor { DebugMode = EffectProcessorDebugMode.Debug }`.
- **Processor parameters are C# properties on the instance**, not a dictionary: `new ShadowDuskEffectProcessor { Defines = "FOO=1;BAR" }`. They take part in the Builder's per-asset cache key, so changing one rebuilds the asset; `#include`d files are registered as dependencies, so editing an `.fxh` rebuilds too.

## Processor properties

Every one is optional; the defaults are the correct path.

| Property | Default | What it does |
|---|---|---|
| `DebugMode` | `Auto` | `Auto` follows the content build configuration where there is one (MGCB); the Builder has none, so it optimizes. `Debug` / `Optimize` force it. |
| `Defines` | *(empty)* | Preprocessor macros, in `mgfxc`'s `/Defines:` spelling: `NAME=VALUE` entries separated by `;` or `,`; a bare `NAME` defines it as `1`. |
| `IncludeDirs` | *(empty)* | Extra `#include` search directories, `;`-separated. The including file's own directory is always searched first. |
| `ShaderProfile` | *(empty)* | Escape hatch. Overrides the target derived from the platform: `DirectX_11`, `DirectX_12`, `OpenGL`, `Vulkan`. Not needed on 3.8.5, whose platforms already name Vulkan and DirectX 12. |
| `MgfxVersion` | `10` | Escape hatch. `11` opts into the newer MGFX container (MonoGame 3.8.5+). |
| `DxbcBackend` | `vkd3d` | Escape hatch. `d3dcompiler` opts into the Windows-only correctness oracle for the DirectX target. |

## Diagnostics

Shader errors surface through the Builder's logger in the `file(line,col-col): error CODE: message` form `fxc`/`mgfxc` use and MSBuild and IDEs parse, with the underlying compiler's own words verbatim beneath. The asset fails to build; nothing is silently swallowed.

## Learn more

- [ShadowDusk documentation](https://kaltinril.github.io/ShadowDusk/)
- [MonoGame 3.8.5 Content Builder guide](https://kaltinril.github.io/ShadowDusk/guides/content-builder.html)
- [MGCB content pipeline guide](https://kaltinril.github.io/ShadowDusk/guides/mgcb-content-pipeline.html) (for `.mgcb` files, via `ShadowDusk.MgcbPlugin`)
