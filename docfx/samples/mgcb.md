# MGCB Sample

A minimal MonoGame content-pipeline sample (`samples/mgcb`) that builds `.fx` shaders through ShadowDusk via the **MGCB content build**, demonstrating the [drop-in `mgfxc`](../guides/dropin-mgfxc.md) / [MGCB Content Pipeline (Tier-1)](../guides/mgcb-content-pipeline.md) integration.

## What it does

The sample's `Content/Content.mgcb` is a standard MonoGame content project targeting **DesktopGL / Reach** that builds a broad slice of the [test shader corpus](../contributing/test-shader-corpus.md) (`BasicEffect`, `AlphaTestEffect`, the `Penumbra*` effects, the tutorial `*Shader.fx` set, post-process effects like `Grayscale`/`Invert`/`Sepia`, and more) with the stock `EffectImporter` / `EffectProcessor`.

When ShadowDusk's `mgfxc` is on `PATH` ahead of MonoGame's, MGCB shells out to it unchanged — same flags, same `.mgfx` output — so this exact `.mgcb` builds its shaders through ShadowDusk on Linux, macOS, or Windows.

## Run it

```sh
cd samples/mgcb
dotnet mgcb /@:Content/Content.mgcb     # builds the .fx through whatever mgfxc is on PATH
dotnet run                              # runs the host
```

Install ShadowDusk's CLI first and ensure it resolves before any MonoGame-provided `mgfxc`:

```sh
dotnet tool install --global ShadowDusk.Cli
```

See [MGCB Content Pipeline (Tier-1)](../guides/mgcb-content-pipeline.md) for the PATH-override details and the `/Profile` ↔ target mapping.

## Files

| File | Role |
|---|---|
| `MGCBSample.csproj` | the host project |
| `Program.cs` | MonoGame host entry point |
| `Content/Content.mgcb` | the content build that compiles the `.fx` corpus through `mgfxc` |
