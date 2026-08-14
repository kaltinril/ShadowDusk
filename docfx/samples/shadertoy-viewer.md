# ShaderToyViewer

An interactive MonoGame sample (`samples/ShaderToyViewer`) that runs the whole ShaderToy route **at runtime, in one process, with no build step and no `mgfxc`**: ShaderToy GLSL → HLSL `.fx` → `.mgfx` bytes in memory → a live `Effect` on screen.

## What it does

Per shader, per cycle:

1. `ShaderToyConverter.Convert(glsl)` (the [`ShadowDusk.ShaderToy`](../architecture/the-faithful-pipeline.md) frontend) emits self-contained HLSL `.fx` text;
2. `EffectCompiler.Compile(fx, OpenGL)` compiles it to `.mgfx` bytes **in memory**;
3. `new Effect(GraphicsDevice, bytes)` loads them into real MonoGame DesktopGL;
4. a `ShaderToyEffect` helper drives `iResolution` / `iTime` / `iTimeDelta` / `iFrame` / `iMouse` and draws a fullscreen quad.

It ships a four-shader catalog, accepts **any** ShaderToy or plain-GLSL image-tab file as an argument, **hot-reloads** it while you edit (re-convert + recompile + reload, ~4x/sec), and draws convert/compile diagnostics on screen with a built-in bitmap font instead of crashing or going black.

## Run it

```sh
# interactive window over the bundled catalog
dotnet run --project samples/ShaderToyViewer

# point it at your own shader (.glsl / .frag / .fs / .txt), hot-reloadable
dotnet run --project samples/ShaderToyViewer -- path/to/your_shader.glsl

# headless self-test: one offscreen frame per bundled shader, PNG per shader, exit 0 on success
dotnet run --project samples/ShaderToyViewer -- --smoke
```

Controls: **SPACE**/**RIGHT** next, **LEFT** previous, **R** force reload, mouse drives `iMouse`, **ESC** quits.

## Where the MonoGame dependency lives, and why

`Runtime/ShaderToyEffect.cs` is built entirely on `Microsoft.Xna.Framework.Graphics`, so it cannot be MonoGame-free. It therefore lives **inside this sample** and never in a shipped `ShadowDusk.*` package: MonoGame is a *consumer's* runtime, and pulling it into a product package would bloat every consumer's graph and pin a MonoGame version into the product. `NoMonoGameInProductLibrariesTests` is the standing guard that no project under `src/` ever gains a `MonoGame.Framework.*` reference. A consumer who wants this glue copies the one file.

## What it proves, and what it does not

This is a **sample of reach**, not the product bar. It demonstrates the runtime compile-and-render path end to end, but it asserts nothing about fidelity. The ShaderToy route's actual evidence lives elsewhere on the [evidence ladder](../contributing/validation.md): pixel-fidelity against the original GLSL comes from the out-of-band `tools/shadertoy2fx/render-proof --fidelity` driver, and the rung-4 OpenGL proof of the converted `.fx` against `mgfxc`'s own build is `validation/ShaderToyRouteGl`.

## Files

| File | Role |
|---|---|
| `Program.cs` | argument parsing; interactive vs `--smoke` entry |
| `SampleGame.cs` | the interactive viewer: cycling, hot-reload, `iMouse`, error overlay |
| `SmokeGame.cs` | headless one-frame-per-shader render + non-trivial-frame assertion |
| `SampleCompiler.cs` | convert → in-memory compile → `new Effect` → `ShaderToyEffect` |
| `ShaderCatalog.cs`, `ShaderSource.cs` | the bundled catalog and external-file loading |
| `PixelFont.cs` | built-in bitmap font for the on-screen diagnostics (no content build) |
| `Runtime/ShaderToyEffect.cs` | the MonoGame helper: uniform driving + fullscreen quad |
| `shaders/*.glsl` | the four bundled shaders (`neon.glsl` is CC0) |
