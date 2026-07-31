# MGCB Sample

A minimal MonoGame content-pipeline sample (`samples/mgcb`) that builds a broad slice of the
[test shader corpus](../contributing/test-shader-corpus.md) **two ways over the same sources**, so the
[MGCB content-processor plugin](../guides/mgcb-content-pipeline.md) can be compared against
MonoGame's own compiler side by side.

## The two content projects

| File | Compiler | Output |
|---|---|---|
| `Content/Content.mgcb` | MonoGame's **own** in-process effect compiler (stock `EffectImporter` / `EffectProcessor`) | `Content/bin/DesktopGL/` |
| `Content/Content.ShadowDusk.mgcb` | **ShadowDusk**, via `/reference:` + `ShadowDuskEffectImporter` / `ShadowDuskEffectProcessor` | `Content/bin/shadowdusk/DesktopGL/` |

Both target **DesktopGL / Reach**. The two files differ in exactly three ways: the output
directories, the `/reference:` line, and the importer/processor names.

> [!NOTE]
> Ten of the 34 effects are deliberately absent from the ShadowDusk variant — `BasicEffect`,
> `AlphaTestEffect`, `DualTextureEffect`, `EnvironmentMapEffect`, `SkinnedEffect`, `SpriteEffect`,
> and the four `Penumbra*` effects. All ten declare their techniques through **preprocessor macros**,
> which ShadowDusk's OpenGL path cannot yet compile (`SD0010`). That is a **compiler-library** gap,
> identical through the ShadowDusk CLI — the plugin adds no compilation logic, so it inherits exactly
> what the library supports. They stay in `Content.mgcb` so the difference stays visible rather than
> hidden.

> [!IMPORTANT]
> This sample was originally written on the belief that MGCB shells out to an `mgfxc` on `PATH`, so
> exposing ShadowDusk's CLI under that name would route the build through it. **Measurement on
> 2026-07-28 disproved that** for `dotnet mgcb` 3.8.2.1105, 3.8.4.1, and 3.8.5: MGCB compiles `.fx`
> **in-process** and never invokes an external `mgfxc`. The `Content.ShadowDusk.mgcb` variant is the
> route that does work — a `/reference:`d content-processor plugin.

## Run it

```sh
cd samples/mgcb

# MonoGame's own compiler
dotnet mgcb /@:Content/Content.mgcb

# ShadowDusk, in MGCB's own process
dotnet build ../../src/ShadowDusk.MgcbPlugin/ShadowDusk.MgcbPlugin.csproj
cd Content && dotnet mgcb /@:Content.ShadowDusk.mgcb && cd ..

dotnet run                              # runs the host
```

The sample's `/reference:` points at the repo build output. A consumer of the published package points
at the package's `tools/net8.0/any/` directory instead — see
[MGCB Content Pipeline](../guides/mgcb-content-pipeline.md).

Compiling the same shaders with the CLI still works too, and is the route for a build script that
does not use MGCB at all:

```sh
dotnet tool install --global ShadowDusk.Cli   # installs the `ShadowDuskCLI` command
ShadowDuskCLI ../../tests/fixtures/shaders/Grayscale.fx Grayscale.mgfx /Profile:OpenGL
```

## Files

| File | Role |
|---|---|
| `MGCBSample.csproj` | the host project |
| `Program.cs` | MonoGame host entry point |
| `Content/Content.mgcb` | the content build that compiles the `.fx` corpus with MonoGame's own compiler |
| `Content/Content.ShadowDusk.mgcb` | the same corpus compiled by ShadowDusk through the MGCB plugin |
