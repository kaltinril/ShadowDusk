# MonoGame 3.8.5 Content Builder

**MonoGame 3.8.5 made the code-centric Content Builder project the template default**: instead of a
`.mgcb` file, your content is described by ordinary C# you own — a console project referencing
`MonoGame.Framework.Content.Pipeline`, whose `ContentBuilder` subclass lists what to build. ShadowDusk
plugs into it as a **library**: the `ShadowDusk.ContentPipeline` package gives you
`ShadowDuskEffectImporter` and `ShadowDuskEffectProcessor` as types you `new` up, and your `.fx`
shaders compile to `.xnb` **through ShadowDusk, inside your Builder's own process** — no `mgfxc`, no
`fxc.exe`, no Wine, no Windows SDK, on Linux, macOS, and Windows alike.

It is the same importer and processor the [MGCB plugin](mgcb-content-pipeline.md) ships, compiled from
the same source files; only the packaging differs. The plugin is a tools-only package (MGCB needs
everything in one directory it can `/reference:`), which cannot be referenced from C# — so the
Content Builder gets its own, library-shaped package.

## Setup

In your Content Builder project (the `MonoGame.ContentBuilder.CSharp` template, short name `mgcb`):

```sh
dotnet add package ShadowDusk.ContentPipeline
```

Keep the template's own package list as it is. ShadowDusk's assemblies and its pinned DXC,
SPIRV-Cross, and vkd3d-shader natives flow into the Builder's `bin/` through normal NuGet asset flow;
nothing has to be installed separately.

Then pass the two instances where you include your effects:

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
        content.Include<WildcardRule>("Effects/*.fx",
            new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());
        content.Include<WildcardRule>("Font/*.spritefont");
        content.Include("splash-screen.png");
        return content;
    }
}
```

That is the whole setup. The **target follows the Builder's platform** (`-p` / `$(MonoGamePlatform)`
from the game's `BuildContent.targets`), and the output is the backwards-compatible MGFX v10 container
every MonoGame 3.8.1.263+ and KNI runtime loads. You never pick a version, a format, or a flag.

> [!IMPORTANT]
> **Pass the instances.** `content.Include<WildcardRule>("Effects/*.fx")` with no importer/processor
> lets the Builder auto-discover one by extension, and with both ShadowDusk's and MonoGame's pairs
> loaded it picks MonoGame's stock `EffectImporter`/`EffectProcessor` — silently, with no way for a
> second `.fx` importer to win (measured against the real 3.8.5 Builder). The explicit instances are the
> route.

## Platform → target mapping

| Builder platform (`-p`) | ShadowDusk target |
|---|---|
| `Windows` | DirectX 11 (DXBC SM5) |
| `DesktopGL`, `MacOSX`, `iOS`, `Android`, `RaspberryPi`, `Web`, `NativeClient` | OpenGL (GLSL) |
| `DesktopVK` | [Vulkan](../backends/vulkan.md) (SPIR-V) |
| `WindowsDX12` | [DirectX 12](../backends/directx12.md) (DXIL SM6) — build on Windows: DXIL signing needs the Windows-only `dxil.dll`, or the output is unsigned and retail D3D12 rejects it (`SD0214`) |
| `PlayStation4`, `PlayStation5`, `XboxOne`, `XboxSeries`, `Switch`, `Xbox360`, `Stadia` | not supported — the asset fails loudly with `SD0501` |

The map keys on the platform's **name** (what `-p` and `$(MonoGamePlatform)` carry), so it is correct
under MonoGame 3.8.5's renumbered `TargetPlatform` even though the package is compiled against the
3.8.2.1105 contract (the floor, so the same assembly also loads into older MGCBs).

## Processor properties

Processor parameters are **C# properties on the instance**, not a dictionary, and they take part in the
Builder's per-asset cache key, so changing one rebuilds the asset:

```csharp
new ShadowDuskEffectProcessor { Defines = "FOO=1;BAR", DebugMode = EffectProcessorDebugMode.Debug }
```

| Property | Default | What it does |
|---|---|---|
| `DebugMode` | `Auto` | On MGCB, `Auto` follows the content build configuration. **The Builder has no build-configuration argument**, so `Auto` optimizes there; set `Debug` explicitly for debug info. |
| `Defines` | *(empty)* | Preprocessor macros in `mgfxc`'s `/Defines:` spelling: `NAME=VALUE` entries separated by `;` or `,`; a bare `NAME` defines it as `1`. |
| `IncludeDirs` | *(empty)* | Extra `#include` search directories, `;`-separated. The including file's own directory is always searched first. `#include`d files are registered as dependencies, so editing an `.fxh` rebuilds. |
| `ShaderProfile` | *(empty)* | Escape hatch: `DirectX_11`, `DirectX_12`, `OpenGL`, `Vulkan` overrides the platform-derived target. Not needed on 3.8.5, whose `DesktopVK` / `WindowsDX12` platforms already name those targets. |
| `MgfxVersion` | `10` | `11` opts into the newer MGFX container (MonoGame 3.8.5+). |
| `DxbcBackend` | `vkd3d` | `d3dcompiler` opts into the Windows-only correctness oracle for the DirectX target. |

## Diagnostics

Shader errors reach the Builder's logger in the `file(line,col-col): error CODE: message` form
`fxc`/`mgfxc` use and MSBuild's `[E]`-prefixed error regex picks up, with the underlying compiler's own
words verbatim beneath — the same text the [ShadowDusk CLI](../cli/index.md) prints, from the same
formatter. The asset fails; nothing is silently swallowed.

## Is it really ShadowDusk, and does it really load?

Checked, not claimed. `validation/ContentBuilder` runs a **real** MonoGame 3.8.5 `ContentBuilder`
subclass over the fixture set with two content roots — MonoGame's stock pair and ShadowDusk's — and
asserts that the ShadowDusk `.xnb` payload is **byte-for-byte** the ShadowDusk CLI's, that the `.xnb`
envelope is byte-for-byte the stock build's, that the payloads differ, and then loads **both** through a
real `ContentManager.Load<Effect>` on MonoGame 3.8.5 and requires **pixel-identical** renders
(measured 2026-09-09: 7/7 assets, 4/4 renders at 1,230,720 px identical). `pack-consume.yml`
additionally consumes the packed package cold from a local feed in a scratch Builder on Linux, macOS,
and Windows. Details in [Validation](../contributing/validation.md).

The same run is also the proof that ShadowDusk's dependency graph survives the Builder's unguarded
`Assembly.GetTypes()` scan of every assembly a Builder project references — a scan that used to crash on
one type in `Vortice.Direct3D12`, which is why that package left ShadowDusk's graph.

## Known limits

- **Macro-defined techniques on the OpenGL target** fail with `SD0010`, identically through every
  route (a compiler-library gap, not a Builder one). It affects MonoGame's own `BasicEffect`-family
  stock effects.
- **Extension auto-discovery cannot prefer ShadowDusk** (see the note above): pass the instances.
- The Builder's `server` mode uses the same importer and processor and needs nothing extra, but it is
  untested here and not claimed.
