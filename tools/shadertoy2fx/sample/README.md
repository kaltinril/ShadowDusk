# ShadowDusk ShaderToy sample (Phase 46 capstone)

A runnable MonoGame game that demonstrates, **end to end and at runtime**, the ShaderToy -> FX
experiment together with ShadowDusk's in-memory runtime compile.

For the current shader it, **per cycle, with no build step and no `mgfxc`**:

1. converts ShaderToy GLSL to HLSL `.fx` with `ShaderToyConverter.Convert`,
2. compiles that `.fx` to `.mgfx` bytes **in memory** via `ShadowDusk.Compiler.EffectCompiler`
   (OpenGL target),
3. loads the bytes into a real `new Effect(GraphicsDevice, mgfxBytes)`, and
4. renders it animated and interactive over a fullscreen quad (wrapped in `ShaderToyEffect`).

There is **no content build, no `mgfxc`, no `fxc`, no Wine**: the entire shader pipeline runs at
runtime inside the game, which is exactly the ShadowDusk promise -- "add the library, call the API."

This is a sample only. It is **not** packaged as a NuGet and is **not** part of `ShadowDusk.slnx`.

## Bundled shaders

All four are animated and/or interactive (they drive `iTime` and/or `iMouse`), copied from the
converter's authored / CC0 corpus into `shaders/`:

| File | What it shows |
|------|----------------|
| `time_animation.glsl`    | `iTime`-driven pulsing color (sin/cos). |
| `mouse_interaction.glsl` | `iMouse` glow -- brightness tracks the cursor. |
| `atan_polar.glsl`        | Animated polar spiral (`atan2` + `iTime`). |
| `neon.glsl`              | CC0 "Neonwave style road, sun and city". |

`neon.glsl` is **CC0 1.0 (public-domain dedication)** -- original ShaderToy
<https://www.shadertoy.com/view/WlByzy>, author `mrange`. Its `// License CC0` header is kept intact
in the bundled file. Full provenance:
`tools/shadertoy2fx/tests/ShadowDusk.ShaderToy.Tests/corpus/cc0/LICENSES.md`.

## How to run

Interactive window:

```bash
dotnet run --project tools/shadertoy2fx/sample
```

Controls:

- **SPACE** or **RIGHT** -- next shader (recompiled at runtime)
- **LEFT** -- previous shader
- **mouse** -- drives `iMouse` (ShaderToy bottom-left-origin convention; click sets the `zw` slot)
- **ESC** -- quit

The window title shows the current shader name, the uniforms it references, and that it was compiled
in memory via ShadowDusk.

Headless smoke (automated validation, no window loop):

```bash
dotnet run --project tools/shadertoy2fx/sample -- --smoke
```

`--smoke` runs the full convert -> in-memory compile -> load -> render-one-frame path for **each**
bundled shader to an offscreen `RenderTarget`, writes a PNG per shader to `sample/output/`, asserts
the frame is non-trivial (not all-black), and exits `0` (non-zero on any failure). A few
representative PNGs are committed under `output/` as eyeball evidence; regenerable `*.fx` / `*.mgfx`
written there (if any) are gitignored.

## What this proves

The product bar is "ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders." This sample
exercises exactly that path **at runtime** -- no offline compile -- for real ShaderToy shaders the
converter produced, on this machine's MonoGame DesktopGL runtime.
