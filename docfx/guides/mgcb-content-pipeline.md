# MGCB Content Pipeline

**ShadowDusk plugs into the MonoGame Content Builder as a content-processor plugin.** Add the
`ShadowDusk.MgcbPlugin` package, point one `/reference:` line at it, and MGCB compiles your `.fx`
to `.xnb` **through ShadowDusk, inside its own process** — no `mgfxc`, no `fxc.exe`, no Wine, no
Windows SDK, on Linux, macOS, and Windows alike.

> [!IMPORTANT]
> **Do not try to route MGCB through a `PATH` override.** This page used to document one, and it
> does not work. Measured on 2026-07-28 against `dotnet mgcb` **3.8.2.1105, 3.8.4.1, and 3.8.5**:
> with a real `mgfxc` executable placed first on `PATH`, a `.mgcb` content build **never invoked it
> once** and still produced a valid `.xnb`. **MGCB compiles `.fx` in-process** — there is nothing
> for a `PATH` alias to intercept. MonoGame 3.8.5's code-centric Content Builder is a C# project
> over `MonoGame.Framework.Content.Pipeline` and has no external-tool seam either. The plugin below
> is the real integration.

## Setup

```sh
dotnet add package ShadowDusk.MgcbPlugin
```

It is a build-time (`DevelopmentDependency`) package: nothing from it reaches your shipped game
assembly.

Then in your `.mgcb`, add the reference once and select the ShadowDusk importer and processor on
each effect:

```
/reference:$(NuGetPackageRoot)shadowdusk.mgcbplugin/<version>/tools/net8.0/any/ShadowDusk.MgcbPlugin.dll

#begin MyEffect.fx
/importer:ShadowDuskEffectImporter
/processor:ShadowDuskEffectProcessor
/build:MyEffect.fx
```

> MGCB does **not** expand MSBuild properties inside a `.mgcb`, so spell the path out (or copy the
> package's `tools/net8.0/any` directory somewhere stable in your repo and point at that). Everything
> the plugin needs — the ShadowDusk assemblies and the pinned DXC, SPIRV-Cross, and vkd3d-shader
> natives for every RID — lives in that one directory, because MGCB resolves a referenced plugin's
> dependencies from the plugin's own folder.

That is the whole setup. The **target comes from the content project's own `/platform:` line**, and
the output is the backwards-compatible MGFX v10 container every MonoGame 3.8.1.263+ and KNI runtime
loads. You never pick a version, a format, or a flag to get correct output.

## Platform → target mapping

| `.mgcb` `/platform:` | ShadowDusk target |
|---|---|
| `Windows` | DirectX 11 (DXBC SM5) |
| `DesktopGL`, `MacOSX`, `iOS`, `Android`, `RaspberryPi`, `Web`, `NativeClient` | OpenGL (GLSL) |
| `PlayStation4`, `PlayStation5`, `XboxOne`, `Switch`, `Xbox360`, `Stadia` | not supported — the build fails loudly with `SD0501` rather than emitting an artifact those runtimes cannot load |

MonoGame's `WindowsDX12` and `DesktopVK` runtimes have no member in MGCB's platform list at all, so
they are reached through the `ShaderProfile` processor parameter below.

## Processor parameters

Every one is optional. The defaults are the correct path — a parameter here is an escape hatch, never
a step you must take to get working output.

| `/processorParam:` | Default | What it does |
|---|---|---|
| `DebugMode` | `Auto` | `Auto` follows the content build configuration, exactly like MonoGame's stock `EffectProcessor`. `Debug` / `Optimize` force it. |
| `Defines` | *(empty)* | Preprocessor macros in `mgfxc`'s `/Defines:` spelling: `NAME=VALUE` entries separated by `;` or `,`; a bare `NAME` defines it as `1`. Same property name and format as the stock processor, so an existing `/processorParam:Defines=…` carries over unchanged. |
| `IncludeDirs` | *(empty)* | Extra `#include` search directories, `;`-separated. The including file's own directory is always searched first and needs no entry. Equivalent to the CLI's `/I`. |
| `ShaderProfile` | *(empty)* | Overrides the target derived from `/platform:`. `DirectX_11`, `DirectX_12`, `OpenGL`, `Vulkan` — the only way to reach [DirectX 12](../backends/directx12.md) and [Vulkan](../backends/vulkan.md), which MGCB's platform list cannot name. Build DX12 content **on Windows**: DXIL signing needs the Windows-only `dxil.dll`, or the output is unsigned and retail D3D12 rejects it (`SD0214`). |
| `MgfxVersion` | `10` | `11` opts into the newer MGFX container (MonoGame 3.8.5+). |
| `DxbcBackend` | `vkd3d` | `d3dcompiler` opts into the Windows-only correctness oracle for the DirectX target. |

## Diagnostics

Shader errors surface through MGCB in the `file(line,col-col): error CODE: message` form
`fxc`/`mgfxc` use and MSBuild and IDEs parse, with the underlying compiler's own words verbatim
beneath — the same text the [ShadowDusk CLI](../cli/index.md) prints, from the same formatter. The
build fails; nothing is silently swallowed.

```
C:\game\Content\Broken.fx(49,32-32): error X0000: use of undeclared identifier 'notADeclaredThing'
    C:\game\Content\Broken.fx:49:32: error: use of undeclared identifier 'notADeclaredThing'
        col.rgb = (col.r + col.g + notADeclaredThing) / 3.0f;
                                   ^
```

`#include`d files are registered as build dependencies, so editing an `.fxh` rebuilds the effects
that use it.

## Is it really ShadowDusk?

Yes, and it is checked rather than claimed. The `.mgfx` bytes inside the `.xnb` are **byte-for-byte**
what the ShadowDusk CLI writes for the same source and target — because the plugin is an adapter onto
the same `EffectCompiler`, adding no compilation logic of its own. Two gates hold that:
`MgcbPluginByteIdentityTests` drives the processor and compares against the real CLI binary on every
`dotnet test`, and `validation/MgcbPlugin` runs an actual `dotnet mgcb` build and additionally checks
that the `.xnb` envelope matches MGCB's own stock output while the payload *differs* from it. Verified
green on `dotnet mgcb` 3.8.2.1105, 3.8.3, 3.8.4, 3.8.4.1, and 3.8.5.

## The other routes (still supported)

The plugin is the native integration for teams who *want* MGCB in their build, but none of these went away:

**0. Skip MGCB entirely and let ShadowDusk write the `.xnb` itself.** If effects are the only reason
MGCB is in your build, you no longer need it there:

```sh
ShadowDuskCLI Content/MyEffect.fx Content/MyEffect.xnb /Profile:OpenGL
```

Drop the file where the pipeline-built one used to go and `Content.Load<Effect>("MyEffect")` keeps
working unchanged — the container matches what MGCB writes (verified against a real `dotnet-mgcb`
build, loaded through a real `ContentManager`, rendered pixel-identically). From the library it is
`result.Value.ToXnb()`. See [Drop-in `mgfxc`](dropin-mgfxc.md).

**1. Compile with the CLI and let MGCB copy the result.** Build `.fx → .mgfx` with the
[ShadowDusk CLI](dropin-mgfxc.md) as a pre-build step, then `/copy:` it:

```sh
ShadowDuskCLI Content/MyEffect.fx Content/MyEffect.mgfx /Profile:OpenGL
```

```
#begin MyEffect.mgfx
/copy:MyEffect.mgfx
```

**2. Skip the content pipeline for shaders entirely** — compile at runtime and hand the bytes to
`Effect`, which is [what the library is for](../getting-started/overview.md):

```csharp
var result = await new EffectCompiler().CompileAsync(File.ReadAllText("MyEffect.fx"),
    new CompilerOptions { Target = PlatformTarget.OpenGL });
var effect = new Effect(GraphicsDevice, result.Value.Data);
```

The `/Profile:` names in route 1 are the same names the `ShaderProfile` processor parameter takes.

## A worked sample

The repository's [`samples/mgcb`](../samples/mgcb.md) sample carries two content projects over the
same shader corpus: `Content.mgcb` (MonoGame's own compiler) and `Content.ShadowDusk.mgcb` (this
plugin), so you can build both and compare.

## Known limits

- **Macro-defined techniques on the OpenGL target** fail with `SD0010 "Effect source contains no
  techniques"`. That is a compiler-library gap, not a plugin one — the same shaders fail identically
  through the CLI. It affects MonoGame's own `BasicEffect`-family stock effects and Penumbra's.
- **The package is large (~81 MB)** because it carries the pinned DXC for every RID. That is what
  "add the package and point at it" costs; nothing has to be installed separately.
