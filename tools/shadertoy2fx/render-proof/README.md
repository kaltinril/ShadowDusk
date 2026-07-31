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

The `ShaderToyEffect` helper (source-linked from `../../../samples/ShaderToyViewer/Runtime/`, its
only home since the Phase 51 A4 sample migration) draws the fullscreen
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

## `--fidelity` : the pixel-fidelity gate (does OUR render == the ORIGINAL GLSL?)

The analytic asserts above prove a handful of points are correct. `--fidelity` proves the WHOLE
frame matches the **original ShaderToy GLSL** - the difference between "renders something plausible"
and "renders what the original renders". It needs no shadertoy.com: it renders the original GLSL
itself as the ground truth.

```powershell
dotnet build src/ShadowDusk.Cli/ShadowDusk.Cli.csproj
dotnet run --project tools/shadertoy2fx/render-proof/ShadowDusk.ShaderToy.RenderProof.csproj -- --fidelity
```

For each authored corpus shader (`../tests/.../corpus/authored/*.glsl`), at a fixed
`320x240`, `iTime=1.5`, `iMouse=(160,120)`, `iFrame=90`:

1. **REFERENCE** (ground truth) = the ORIGINAL `mainImage` body wrapped in a plain `#version 330`
   ShaderToy fragment shader (`void main(){ mainImage(c, gl_FragCoord.xy); }`), rendered DIRECTLY in
   a raw Silk.NET GL offscreen FBO (`GlReferenceRenderer.cs`) - no ShadowDusk pipeline at all.
2. **OURS** (test) = our converted `.fx -> .mgfx (OpenGL) -> MonoGame Effect`, rendered at the SAME
   uniforms (`FidelityTestRenderGame.cs`). Each shader renders in its OWN MonoGame device - a shared
   device leaks pixel-shader state across effects in DesktopGL.
3. **DIFF** per pixel: mean absolute diff (/255), max channel delta, % of pixels within `12/255`.
4. **TOLERANCE (documented):** a shader MATCHES when mean abs diff `<= 6/255` AND `>= 95%` of pixels
   are within `12/255`. Both gates: the mean catches a uniform shift, the percentile catches localized
   breakage a mean would dilute. The faithful shaders hit `0.00/255` and `100%` - the tolerance is a
   margin, not a crutch.
5. **DIVERGENCES are classified honestly**, never hidden by loosening tolerance: a real CONVERSION
   BUG (broad structural difference, or a clean vertical MIRROR = matrix-order/handedness trap) vs
   legitimate FLOAT/DERIVATIVE chaos. Shaders the raw-GL harness can't fairly reproduce (custom
   uniforms, `gl_FragCoord.z/.w`, folded exact-type aliases like `time`->`iTime`, vertex-stage
   varyings, or GLSL that won't compile as plain `#version 330`) are SKIPPED with a reason, not faked.
6. **MONTAGE:** one committed `output/fidelity.png` (rows of `reference | ours | amplified-diff`); the
   per-shader `.fx/.mgfx` + dumped PNGs land in the gitignored `output/fidelity-work/`.

Set `FIDELITY_ONLY=name1,name2` to render a subset, `FIDELITY_DUMP_DIVERGENT=1` to write
ref/ours/diff PNGs for divergent shaders.

### Result (last run, real Windows GPU)

**45/46 shaders MATCH at `0.00/255` mean, `100%` within tolerance** - including every matrix /
precision-trap fixture (`atan_polar`, `kaleidoscope`, `mat2_rotation`, `matrix_comp_mult`,
`mod_negative`, `mix_clamp_smoothstep`, `intrinsic_fwidth`) and the four complex shaders
(`raymarch_sphere`, `fbm_clouds`, `kaleidoscope`, `domain_warp`). 26 skipped (reference-unfair, see
above).

**1 CONVERSION BUG flagged for a focused follow-up (NOT fixed here):** `mat_compound_assign` renders
the exact VERTICAL MIRROR of the reference (flipped image matches `0.0/255`). Root cause: GLSL
`p *= rot(a)` (where `p` is a `vec2`) means `p = p * M` (p as a ROW vector), which equals
`mul(rot(a), p)` in HLSL, but the converter lowered the matrix `*=` to `p = mul(p, rot(a))` - the
SAME order it (correctly) uses for the non-compound `M * v` form. With the row-major HLSL `float2x2`
constructor that yields `R(-a)` instead of `R(+a)`, i.e. the rotation's transpose, mirroring the
field. The non-compound `uv = spin * uv` cases convert correctly; only the compound `*=`-with-matrix
path picks the wrong multiply side. The fix belongs in the converter's compound-assignment lowering
(emit `p = mul(rot(a), p)` for a matrix RHS), out of scope for this render-gate task.

## Not in `ShadowDusk.slnx`

Out-of-band by design, like the rest of `tools/shadertoy2fx/` and the `validation/*` drivers.
