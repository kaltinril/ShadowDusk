# ShadowDusk.MgcbPlugin

**A MonoGame Content Builder (MGCB) content-processor plugin that compiles your `.fx` shaders with ShadowDusk, inside MGCB's own process** — no `mgfxc`, no `fxc.exe`, no Wine, no Windows SDK. It works on Linux, macOS, and Windows build agents alike.

This is a **delivery shape of the ShadowDusk compiler library**, not a second compiler. The processor builds a `CompilerOptions` from MGCB's build context and calls the same `EffectCompiler` the [ShadowDusk CLI](https://www.nuget.org/packages/ShadowDusk.Cli) and the runtime API call, so **the `.mgfx` bytes inside the `.xnb` are byte-for-byte what the CLI emits for the same source and target.**

> MGCB compiles `.fx` **in-process** and never launches an external effect compiler, so putting a drop-in `mgfxc` on `PATH` does not route an `.mgcb` build through ShadowDusk. This plugin is the route.

## Install

```
dotnet add package ShadowDusk.MgcbPlugin
```

It is a build-time (`DevelopmentDependency`) package: nothing from it ends up in your shipped game assembly.

## Use

Add a `/reference:` line to your `.mgcb`, then select the ShadowDusk importer and processor on each effect:

```
/reference:$(NuGetPackageRoot)shadowdusk.mgcbplugin/<version>/tools/net8.0/any/ShadowDusk.MgcbPlugin.dll

#begin MyEffect.fx
/importer:ShadowDuskEffectImporter
/processor:ShadowDuskEffectProcessor
/build:MyEffect.fx
```

MGCB's `/reference:` needs a real path, so spell out the package-cache path (or copy the `tools/net8.0/any` directory somewhere stable in your repo and point at that). Everything the plugin needs — the ShadowDusk assemblies and the pinned DXC, SPIRV-Cross, and vkd3d-shader natives — ships in that one directory.

Nothing else is required. The **target is derived from the content project's own `/platform:` line**, and the output is the backwards-compatible MGFX v10 container that every MonoGame 3.8.1.263+ and KNI runtime loads.

| `.mgcb` `/platform:` | ShadowDusk target |
|---|---|
| `Windows` | DirectX 11 (DXBC SM5) |
| `DesktopGL`, `MacOSX`, `iOS`, `Android`, `RaspberryPi`, `Web`, `NativeClient` | OpenGL (GLSL) |
| `PlayStation4`, `PlayStation5`, `XboxOne`, `Switch`, `Xbox360`, `Stadia` | not supported — fails loudly |

## Processor parameters

Every one is optional; the defaults are the correct path.

| `/processorParam:` | Default | What it does |
|---|---|---|
| `DebugMode` | `Auto` | `Auto` follows the content build configuration, exactly like MonoGame's stock `EffectProcessor`. `Debug` / `Optimize` force it. |
| `Defines` | *(empty)* | Preprocessor macros, in `mgfxc`'s `/Defines:` spelling: `NAME=VALUE` entries separated by `;` or `,`; a bare `NAME` defines it as `1`. |
| `IncludeDirs` | *(empty)* | Extra `#include` search directories, `;`-separated. The including file's own directory is always searched first. |
| `ShaderProfile` | *(empty)* | Escape hatch. Overrides the target derived from `/platform:`. `DirectX_11`, `DirectX_12`, `OpenGL`, `Vulkan` — needed only for MonoGame's `WindowsDX12` / `DesktopVK` runtimes, which MGCB's platform list cannot name. |
| `MgfxVersion` | `10` | Escape hatch. `11` opts into the newer MGFX container (MonoGame 3.8.5+). |
| `DxbcBackend` | `vkd3d` | Escape hatch. `d3dcompiler` opts into the Windows-only correctness oracle for the DirectX target. |

## Diagnostics

Shader errors surface through MGCB in the `file(line,col-col): error CODE: message` form `fxc`/`mgfxc` use and MSBuild and IDEs parse, with the underlying compiler's own words verbatim beneath. The build fails; nothing is silently swallowed.

## Learn more

- [ShadowDusk documentation](https://kaltinril.github.io/ShadowDusk/)
- [MGCB content pipeline guide](https://kaltinril.github.io/ShadowDusk/guides/mgcb-content-pipeline.html)
