# MGCB Content Pipeline (Tier-1)

MonoGame's content build tool, **MGCB**, compiles `.fx` shaders by shelling out to **`mgfxc`**. Because ShadowDusk's CLI is a [drop-in `mgfxc` replacement](dropin-mgfxc.md), you can make MGCB use ShadowDusk **without changing your `.mgcb` file or any game code**.

## Tier-1: PATH override (the shipping path)

MGCB invokes whatever `mgfxc` it finds. Put ShadowDusk's `mgfxc` first on `PATH`:

```sh
dotnet tool install --global ShadowDusk.Cli
# ensure the dotnet global-tools dir is on PATH (it is by default for `dotnet tool`),
# and that ShadowDusk's mgfxc resolves before any MonoGame-provided mgfxc.
```

Then run your content build as usual:

```sh
dotnet mgcb /@:Content.mgcb
```

MGCB calls `mgfxc <SourceFile> <OutputFile> /Profile:<Platform> …` exactly as before; ShadowDusk answers with the same flags, the same `.mgfx` output, and the same exit codes. This is the **shipping** MGCB integration: it requires no plugin and no `.mgcb` edits.

## The `.mgcb` `Profile` ↔ ShadowDusk mapping

MGCB passes the platform via `/Profile:`. ShadowDusk understands the MonoGame profile names:

| MGCB `/Profile:` | ShadowDusk target |
|---|---|
| `DirectX_11` | DirectX (DXBC SM5) |
| `OpenGL` | OpenGL / DesktopGL (GLSL) |
| `Vulkan` | Vulkan (SM6 SPIR-V) |

Unsupported console profiles (`PlayStation4`, `XboxOne`, `Switch`) fail loudly with exit code 1, just as a portable tool should.

## A worked sample

The repository's [`samples/mgcb`](../samples/mgcb.md) sample shows a content project building its `.fx` through ShadowDusk via the PATH override.

## The MGCB plugin is a stub (future)

A dedicated `ShadowDusk.MgcbPlugin` content-processor NuGet is **scaffolded but not implemented** — it is a stub with no working processor today. **The Tier-1 PATH override above is the supported MGCB path.** Do not expect a shipping MGCB plugin package; track its status in the [Contributing Guide](../contributing/index.md).
