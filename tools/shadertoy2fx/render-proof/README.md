# ShaderToy render proof (Phase 46)

Closes the credibility gap left by the `.fx` *compile* proof: this driver proves a
converted ShaderToy actually **renders the mathematically-correct pixels** in a **real
MonoGame DesktopGL `Effect`**, in **ShaderToy's bottom-left `fragCoord` orientation**.

## What it does

For each deterministic shader under `shaders/`:

1. converts `.glsl` -> `.fx` by calling the `ShadowDusk.ShaderToy` library directly;
2. compiles `.fx` -> `.mgfx` for OpenGL by shelling the built ShadowDusk CLI
   (`dotnet ShadowDuskCLI.dll <in.fx> <out.mgfx> /Profile:OpenGL`);
3. loads the `.mgfx` into a real MonoGame `Effect`;
4. drives the ShaderToy uniforms through `ShaderToyEffect` (fixed `iResolution` 256x256,
   `iTime`=0) and renders a **fullscreen pass** to an offscreen `RenderTarget2D`;
5. reads back the pixels and **asserts analytic expected values** (the real gate);
6. saves the rendered PNG under `output/` for human eyeball.

The `ShaderToyEffect` helper (in `../src/ShadowDusk.ShaderToy.Runtime/`) draws the fullscreen
quad with the **effect's own** vertex+pixel shaders (NOT `SpriteBatch`, which would override the
converted vertex shader), and best-effort pushes `iResolution`, `iTime`, `iTimeDelta`, `iFrame`,
`iMouse`, and `iChannel0..3` to whichever effect parameters exist.

## Run it (Windows, real GPU)

```powershell
dotnet build src/ShadowDusk.Cli/ShadowDusk.Cli.csproj            # ensure the CLI exists
dotnet run --project tools/shadertoy2fx/render-proof/ShadowDusk.ShaderToy.RenderProof.csproj
```

Exit code 0 = every shader rendered + asserted correctly. Non-zero = a real failure
(convert, compile, GL-init, or a pixel assert) is reported honestly; there is no soft-skip.

## The Y-orientation oracle

`gradient_uv` outputs `(uv.x, uv.y, 0.5)`. With ShaderToy's bottom-left `fragCoord` origin the
displayed image must be: bottom-left `(R,G)=(0,0)`, top-right `(R,G)=(1,1)`. The asserts check
exactly that, so an upside-down render (a harness Y-flip bug) fails the gate. As of this writing
the render is **right-side-up vs ShaderToy** and the `HarnessGenerator` Y-orientation needed no
fix.

## Not in `ShadowDusk.slnx`

Out-of-band by design, like the rest of `tools/shadertoy2fx/` and the `validation/*` drivers.
