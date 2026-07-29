# MGCB Sample

A minimal MonoGame content-pipeline sample (`samples/mgcb`) that builds `.fx` shaders through ShadowDusk via the **MGCB content build**, demonstrating the [drop-in `mgfxc`](../guides/dropin-mgfxc.md) / [MGCB Content Pipeline (Tier-1)](../guides/mgcb-content-pipeline.md) integration.

## What it does

The sample's `Content/Content.mgcb` is a standard MonoGame content project targeting **DesktopGL / Reach** that builds a broad slice of the [test shader corpus](../contributing/test-shader-corpus.md) (`BasicEffect`, `AlphaTestEffect`, the `Penumbra*` effects, the tutorial `*Shader.fx` set, post-process effects like `Grayscale`/`Invert`/`Sepia`, and more) with the stock `EffectImporter` / `EffectProcessor`.

> [!IMPORTANT]
> This sample was written on the belief that MGCB shells out to an `mgfxc` on `PATH`, so exposing
> ShadowDusk's CLI under that name would route the build through it. **Measurement on 2026-07-28 disproved
> that** for `dotnet mgcb` 3.8.2.1105, 3.8.4.1, and 3.8.5: MGCB compiles `.fx` **in-process** and never
> invokes an external `mgfxc`. The sample still builds — but through **MonoGame's own compiler, not
> ShadowDusk.** See [MGCB Content Pipeline](../guides/mgcb-content-pipeline.md) for the measurement and for
> the two routes that do work today.

## Run it

```sh
cd samples/mgcb
dotnet mgcb /@:Content/Content.mgcb     # builds the .fx with MGCB's own in-process compiler
dotnet run                              # runs the host
```

To compile these same shaders **with ShadowDusk**, use the CLI directly:

```sh
dotnet tool install --global ShadowDusk.Cli   # installs the `ShadowDuskCLI` command
ShadowDuskCLI Content/Grayscale.fx Content/Grayscale.mgfx /Profile:OpenGL
```

See [MGCB Content Pipeline](../guides/mgcb-content-pipeline.md) for the `/Profile` ↔ target mapping and the
supported integration routes.

## Files

| File | Role |
|---|---|
| `MGCBSample.csproj` | the host project |
| `Program.cs` | MonoGame host entry point |
| `Content/Content.mgcb` | the content build that compiles the `.fx` corpus through `mgfxc` |
