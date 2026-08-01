# ShadowDusk ShaderToy viewer (sample)

A runnable MonoGame game that demonstrates, **end to end and at runtime**, the ShaderToy → FX
frontend together with ShadowDusk's in-memory runtime compile.

For the current shader it, **per cycle, with no build step and no `mgfxc`**:

1. converts ShaderToy GLSL to HLSL `.fx` with `ShaderToyConverter.Convert`,
2. compiles that `.fx` to `.mgfx` bytes **in memory** via `ShadowDusk.Compiler.EffectCompiler`
   (OpenGL target),
3. loads the bytes into a real `new Effect(GraphicsDevice, mgfxBytes)`, and
4. renders it animated and interactive over a fullscreen quad (wrapped in `ShaderToyEffect`).

There is **no content build, no `mgfxc`, no `fxc`, no Wine**: the entire shader pipeline runs at
runtime inside the game, which is exactly the ShadowDusk promise — "add the library, call the API."

> **This is a SAMPLE OF REACH, never the product.** It is not packaged as a NuGet, and like every
> other sample (`samples/ShaderViewer`, `samples/ShaderFiddle.Web`, `samples/mgcb`) it is **not** in
> `ShadowDusk.slnx`, so `dotnet test ShadowDusk.slnx` does not build it. Its `--smoke` mode below is
> its own gate, run manually.

## Where the MonoGame dependency lives (and why it is here)

The `ShaderToyEffect` runtime helper (`Runtime/ShaderToyEffect.cs`) drives the ShaderToy uniforms
(`iResolution`, `iTime`, `iTimeDelta`, `iFrame`, `iMouse`, `iChannel0..3`) and draws the fullscreen
quad. It is built entirely on `Microsoft.Xna.Framework.Graphics`, so it **cannot** be MonoGame-free
— which is precisely why it lives here in the sample tier and **never** in a shipped
`ShadowDusk.*` package. MonoGame is a *consumer's* runtime, not ours; pulling it into a product
package would bloat every consumer's graph and pin a MonoGame version into the product.
`NoMonoGameInProductLibrariesTests` (in `tests/ShadowDusk.Core.Tests`) is the standing guard that no
`src/*.csproj` ever gains a `MonoGame.Framework.*` reference.

A real consumer who wants to drive a converted effect copies this one file (or the ~20 lines of it
they need) into their own game. There is deliberately no ShadowDusk MonoGame package to add.

## Bundled shaders

All four are animated and/or interactive (they drive `iTime` and/or `iMouse`), copied from the
converter's authored / CC0 corpus into `shaders/`:

| File | What it shows |
|------|----------------|
| `time_animation.glsl`    | `iTime`-driven pulsing color (sin/cos). |
| `mouse_interaction.glsl` | `iMouse` glow — brightness tracks the cursor. |
| `atan_polar.glsl`        | Animated polar spiral (`atan2` + `iTime`). |
| `neon.glsl`              | CC0 "Neonwave style road, sun and city". |

`neon.glsl` is **CC0 1.0 (public-domain dedication)** — original ShaderToy
<https://www.shadertoy.com/view/WlByzy>, author `mrange`. Its `// License CC0` header is kept intact
in the bundled file. Full provenance:
[`tests/ShadowDusk.ShaderToy.Tests/corpus/cc0/LICENSES.md`](../../tests/ShadowDusk.ShaderToy.Tests/corpus/cc0/LICENSES.md).

## How to run

Interactive window (bundled catalog):

```bash
dotnet run --project samples/ShaderToyViewer
```

**Load ANY shader file** — point the viewer at your own ShaderToy / plain-GLSL file:

```bash
dotnet run --project samples/ShaderToyViewer -- path/to/your_shader.glsl
```

Accepted extensions: `.glsl`, `.frag`, `.fs`, `.txt`. The given file is added as the **first,
hot-reloadable** entry; the four bundled shaders remain cyclable behind it. The window title shows
the file name (tagged `EXTERNAL (hot-reload)`) and the uniforms it references.

**Hot-reload** — while a file is loaded the viewer polls its last-write-time (~4x/sec) and, when it
changes on disk, automatically **re-converts + recompiles + reloads** it live, flashing `reloaded`
(or `reload ERROR`) on screen. So you can edit the shader in your editor and watch it update without
restarting. Press **R** to force a reload of any entry.

**On a convert/compile error** the viewer does **not** crash: it dims the screen and draws the
diagnostic text (file, line/column, message) using a tiny built-in bitmap font (no content build /
`SpriteFont` asset needed), and keeps running so you can fix the file and let hot-reload pick it up.

Controls:

- **SPACE** or **RIGHT** — next shader (recompiled at runtime)
- **LEFT** — previous shader
- **R** — reload the current shader now
- **mouse** — drives `iMouse` (ShaderToy bottom-left-origin convention; click sets the `zw` slot)
- **ESC** — quit

Headless smoke (automated validation, no window loop):

```bash
# every bundled shader
dotnet run --project samples/ShaderToyViewer -- --smoke

# OR a single external file (testable without a window)
dotnet run --project samples/ShaderToyViewer -- --smoke path/to/your_shader.glsl
```

`--smoke` runs the full convert → in-memory compile → load → render-one-frame path (for **each**
bundled shader, or just the **given** file) to an offscreen `RenderTarget`, writes a PNG per shader
to `samples/ShaderToyViewer/output/`, asserts the frame is non-trivial (not all-black), and exits
`0` (non-zero on any failure). A bad path / unsupported extension exits `2` with a diagnostic and
never opens a context; a file that fails to convert/compile is reported and exits `1`. A few
representative PNGs are committed under `output/` as eyeball evidence; regenerable `*.fx` / `*.mgfx`
written there are gitignored.

## Copying this sample out of the repo

In-repo the viewer uses `ProjectReference`s. A consumer starts from the two published packages
instead — that is the whole setup:

```xml
<ItemGroup>
  <PackageReference Include="ShadowDusk.ShaderToy" Version="*" />
  <PackageReference Include="ShadowDusk.Compiler" Version="*" />
  <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.2.1105" />
</ItemGroup>
```

`ShadowDusk.Compiler` brings the native compile assets (DXC, SPIRV-Cross, vkd3d) transitively; there
is nothing else to install.

### Limitations

- The viewer renders **single-pass image shaders**. ShaderToy **multipass** shaders (a Buffer-A/B/…
  graph, typically shipped as a `.json` manifest) are **not** driven here — that is handled by the
  converter's multipass path and the out-of-band `shadertoy2fx` CLI, not this single-quad viewer.
  Point the viewer at the **image-tab GLSL** of such a shader.
- Custom textures / `iChannel` inputs are not bound, so a shader that samples a channel renders
  whatever the unbound sampler yields. `iTime`, `iTimeDelta`, `iFrame`, `iResolution`, and `iMouse`
  are all driven.

## What this proves

The product bar is "ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders." This sample
exercises exactly that path **at runtime** — no offline compile — for real ShaderToy shaders the
converter produced, on this machine's MonoGame DesktopGL runtime. It is a demonstration of reach,
not a rung-4 proof: the fidelity gate for the ShaderToy route is the out-of-band
`tools/shadertoy2fx/render-proof --fidelity` driver, and the render-proven `.glsl` route gate is
`validation/ShaderToyRouteGl` (see `docs/validation-matrix.md` §8).
