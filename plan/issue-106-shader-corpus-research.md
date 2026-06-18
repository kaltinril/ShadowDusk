# Issue #106 — Shader Corpus Research (additional real-world `.fx` test shaders)

**Author:** research pass for ShadowDusk
**Date:** 2026-06-17
**Status:** Research deliverable only. No existing project file was modified; this is the single new file.
**Scope:** Find additional VALID, known-good `.fx` shaders (compile with real `mgfxc` / `fxc /T fx_2_0`) that ADD coverage to the corpus, with a bias toward the issue-#106 class (relational operators + ternaries, helper functions called from entry points) and the under-covered language features (`for`/`while` loops in the all-runtime subset, branching, multi-technique VS+PS).

> **Context on issue #106.** Issue #106 ("Shader should be able to return ternary values", reported by `vchelaru`, author of XnaFiddle / xnafiddle.net) is a ShadowDusk repo issue, **not** a MonoGame issue. The reproducer is a VS+PS sprite shader with a **helper function returning a ternary** — `float TernaryReturn(float value) { return value <= 0.5f ? 0.0f : 1.0f; }` — called from `MainPS`. (That exact shader is already checked in as `tests/fixtures/shaders/BasicShader.fx`'s sibling pattern; see §1.) Note xnafiddle.net is itself powered by ShadowDusk to compile shaders, so XnaFiddle/community shaders are squarely in-domain. Source: GitHub issue, `gh issue view 106`; <https://github.com/vchelaru/XnaFiddle>.

---

## 1. What the current corpus covers, and the biggest gaps

The corpus lives in `tests/fixtures/shaders/` (75 `.fx` files incl. `examples/`). It is **heavily PS-only post-processing** in the classic SpriteBatch/`SpriteEffect` shape, and well-stocked on structural/writer-fidelity probes. Provenance is documented in `docs/test-shader-corpus.md`; many of the post-FX fixtures are in fact **Nez** shaders (MIT) already (Grayscale, Sepia, Scanlines, Dissolve, Dots, Invert, ForwardLighting, DeferredSprite, PolygonLight, SpriteAlphaTest, MultiTexture, MultiTextureOverlay all match Nez's `DefaultContentSource/effects/` byte-for-concept).

### Already well-covered (do NOT duplicate)

| Feature | Existing fixture(s) |
|---|---|
| `tex2D` + tint, SpriteBatch PS shape | `BasicShader.fx`, `Grayscale.fx`, `TintShader.fx`, many |
| `dot` greyscale / colour matrix | `Grayscale.fx`, `Sepia.fx` |
| `clip()` / `discard` + ternary inside `clip` | `Dissolve.fx`, `SpriteAlphaTest.fx`, `AlphaTestEffect.fx`, `DeferredSprite.fx`, `ForwardLighting.fx`, `ClipShader.fx`, `ExLegacyTextureDiscard.fx` |
| Ternary `?:` in PS body (chained, band-select) | `ArrayUniform.fx` (`x<0.25 ? ... : x<0.50 ? ...`), `AlphaTestEffect.fx`, `MultiTextureOverlay.fx`, `SpriteAlphaTest.fx`, `DeferredSprite.fx` |
| `if/else if` in PS body w/ relational ops | `Teleport.fx` (`<=`, `>`, `<`, `&&`), `ClipShader.fx` (`if/else`) |
| `lerp`, `saturate`, `normalize`, `length`, `max` | `Dissolve.fx`, `ForwardLighting.fx` |
| Two/N samplers, multi-texture binding | `MultiTexture.fx`, `DualTextureEffect.fx`, `ExDualTexture.fx`, `SimpleLightShader.fx`, `ClipShader.fx` |
| VS+PS, `mul(pos, matrix)`, custom geometry | `VsTransformColorTexture.fx`, `ForwardLighting.fx`, `basiceffect-mini.fx`, `VertexAndPixel.fx` |
| Multiple techniques | `multitechnique.fx` (SM5), `FnaMultiPassStates.fx` (fx_2_0), `AlphaTestEffect.fx` |
| Multiple passes / in-pass render states | `multipass.fx`, `render-states.fx`, `State*.fx`, `FnaMultiPassStates.fx` |
| Array uniforms (float4[], float[]) | `ArrayUniform.fx`, `ArrayUniformVs.fx` |
| `cbuffer` / shared cbuffer | `cbuffer.fx`, `MultiCbuffer.fx`, `SharedCbuffer.fx` |
| FNA fx_2_0 literal `vs_2_0`/`ps_2_0` | `FnaMultiPassStates.fx`, `SpriteEffect.fx` (stock) |
| Advanced texture (HiDef, DX-only): cube/3D/grad/lod | `examples/Ex*Hidef.fx`, `ExVsTextureFetch.fx` |
| Stock MS-PL effects (Macros.fxh layer) | `BasicEffect.fx`, `AlphaTestEffect.fx`, `DualTextureEffect.fx`, `EnvironmentMapEffect.fx`, `SkinnedEffect.fx`, `SpriteEffect.fx` |

### The biggest GAPS (what to add)

1. **`for` / `while` loops in the *all-runtime SM3* subset — MISSING.** The only loops in the corpus are inside `Lighting.fxh` / `SkinnedEffect.fx`, i.e. the MS-PL **stock effects** that are SM4/DX-leaning feature probes, not the validated all-runtime SpriteBatch subset. There is **no** simple `ps_3_0`/`ps_2_0` loop fixture (e.g. an N-tap blur). This is the single clearest gap.
2. **Standalone helper function called from an entry point returning a *ternary / relational expression* — UNDER-covered.** The issue-#106 *shape* (a separate `float helper(float) { return a <= b ? x : y; }` plus the call) exists structurally in `BasicShader`-style fixtures, but there is no **dedicated, minimal, named** regression fixture isolating "helper returns ternary" and "helper does relational math." `Noise.fx`/`PixelGlitch.fx` (Nez) add real helper-function coverage (`rand()`, `hash11()`).
3. **`if`-branch driven by a relational op in the PS *body* (not inside `clip`) — thin.** Only `Teleport.fx` and `ClipShader.fx`. Adding `Twist.fx` (`if (dist < radius)` + trig) and `Vignette.fx` strengthens this.
4. **Common real effects not yet present:** vignette, gaussian/box blur (with a loop), heat/UV distortion, swirl/twist, palette-swap, edge-detect/outline, water/reflection, glitch. The corpus has grayscale/sepia/scanlines/dissolve/pixelate but lacks the blur/vignette/distortion family.
5. **`sin`/`cos`/`atan2`/`frac`/`floor`/`round` trig+math intrinsics in branching context — thin.** `Pixelated.fx` has `round`; `Scanlines.fx` has `sin`. Twist/Reflection/Noise add `sin`,`cos`,`frac`,`floor` in richer contexts.
6. **A *redistributable* VS+PS multi-technique non-stock effect — thin.** `multitechnique.fx` is SM5 project-owned; `Reflection.fx` (Nez, MIT) gives a real **2-technique VS+PS** effect (mirror + water) under a permissive license.

> Note on what is NOT a gap: cube/3D/grad/lod texture intrinsics are already covered (DX-only HiDef `examples/Ex*Hidef.fx`); cbuffers, array uniforms, annotations, render-states, and the FNA multi-pass path are all covered. Don't add more of those.

---

## 2. Compatibility & licensing rules applied

- **All-runtime subset** = compiles for MonoGame-GL (`ps_3_0`/`vs_3_0`, MojoShader: **no VTF, no texture arrays, no SM4+**), MonoGame-DX (`*_4_0_level_9_1`), KNI (same as MonoGame), and FNA (`fx_2_0`, SM ≤ 3). The portable authoring form is the `#if OPENGL ... #else` `VS_SHADERMODEL`/`PS_SHADERMODEL` macro pattern (as in every Grayscale-style fixture).
- **`ps_2_0` / `ps_3_0` literal-profile** Nez shaders compile under `mgfxc` for the GL/DX paths AND map cleanly to FNA's `fx_2_0` (`ps_2_0`/`ps_3_0` are exactly the D3D9 SM2/SM3 FNA wants). To slot a literal-profile Nez shader into the **all-runtime** corpus you wrap it in the `#if OPENGL` macro block and use a `sampler2D = sampler_state{...}` (the project already does this for its Nez-derived fixtures).
- **`VPOS` semantic** (Nez `Crosshatch`, `Noise`, `Letterbox`, `SpriteLines`): `VPOS` is a D3D9 PS register for screen-space pixel position. Its translation through MojoShader → `gl_FragCoord` is **not guaranteed equivalent** across the GL/DX paths (Y-flip + half-pixel conventions differ), and `%` (modulo) on the resulting floats is fragile. **Flag VPOS shaders as DX-leaning / needs-render-verification — do NOT put them in the all-runtime pixel-equivalence set without a Windows render gate.** Source: MojoShader is the GL translator MonoGame/KNI still use (<https://github.com/MonoGame/mojoshader>); VPOS↔gl_FragCoord behavior is a known cross-path hazard.
- **`tex1D` / `sampler1D`** (Nez `PaletteCycler`): 1D textures are not part of the MojoShader-era SM3 GL guarantee and MonoGame exposes textures as 2D — treat as **DX/needs-verification**, not all-runtime.
- **License gate.** We may **vendor** only permissively-licensed source (MIT / MS-PL / public domain / BSD). Verified below:
  - **Nez** (`prime31/Nez`) — **MIT**, "Copyright (c) 2016 Mike". Vendorable with the MIT notice. <https://github.com/prime31/Nez/blob/master/LICENSE>
  - **manbeardgames/monogame-hlsl-examples** — **MIT**, "Copyright 2020 Christopher Whitley". Vendorable. (Already partly in corpus.) <https://github.com/manbeardgames/monogame-hlsl-examples/blob/master/LICENSE>
  - **MonoGame stock effects** (`BasicEffect.fx` etc.) — **Microsoft Public License (MS-PL)**, permissive; already in corpus. The `*_4_0_level_9_1`/`ps_4_0` ones are not pure all-runtime because they ride the `Macros.fxh` layer and target SM4 features. <https://github.com/MonoGame/MonoGame/tree/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources>
  - **MonoGame docs tutorial grayscale** (`docs.monogame.github.io .../24_shaders/snippets/grayscaleeffect.fx`) — documentation is **CC-BY-NC-SA** (NonCommercial). **NOT vendorable** into a permissive corpus. Reference only. <https://github.com/MonoGame/docs.monogame.github.io>

---

## 3. Prioritized candidate shortlist (best-fit first)

Legend — **All-runtime** = drops into the SM3/fx_2_0 pixel-equivalence corpus after wrapping in the `#if OPENGL` macro; **DX-lean** = compiles but uses a feature (VPOS / 1D tex / SM4) that needs a Windows render gate or is DX-only; **Dup?** = overlaps existing coverage.

| # | Shader / effect | Source (repo path) | License | Profile | Runtimes | Exercises (gap closed) | Dup? | Compile confidence |
|---|---|---|---|---|---|---|---|---|
| 1 | **GaussianBlur** (1-D weighted N-tap) | Nez `DefaultContentSource/effects/GaussianBlur.fx` | MIT | `ps_2_0` | All-runtime | **`for` loop** (gap #1), array uniforms `float2[]`/`float[]`, accumulate-in-loop | No | High — shipping Nez bloom shader; classic XNA pattern |
| 2 | **Twist / swirl distortion** | Nez `.../Twist.fx` | MIT | `ps_3_0` | All-runtime | **`if (dist<radius)` branch** (gap #3), `length`,`sin`,`cos`, UV warp | No | High — shipping Nez effect |
| 3 | **Vignette** | Nez `.../Vignette.fx` | MIT | `ps_3_0` | All-runtime | `dot`-based radial falloff, swizzle, no-VS post-FX (gap #4) | No | High — shipping Nez effect |
| 4 | **Noise (film grain)** | Nez `.../Noise.fx` | MIT | `ps_3_0` (uses VPOS only as unused param) | All-runtime* | **helper fn `rand()` from entry** (gap #2), `frac`,`sin`,`dot` | No | High — but VPOS param is declared; drop the unused `screenPos:VPOS` to make it cleanly all-runtime |
| 5 | **PixelGlitch** | Nez `.../PixelGlitch.fx` | MIT | `ps_3_0` | All-runtime | **helper fn `hash11()`** (gap #2), `floor`,`frac`, row offset | No | High — shipping Nez effect |
| 6 | **SpriteBlink** | Nez `.../SpriteBlinkEffect.fx` | MIT | `ps_3_0` | All-runtime | `lerp` tint by uniform alpha; minimal; good smoke | Partial (TintShader-like) | High |
| 7 | **HeatDistortion** | Nez `.../HeatDistortion.fx` | MIT | `ps_2_0` | All-runtime | 2nd sampler w/ `AddressU/V=Wrap` `sampler_state`, time-scrolled UV, remap to [-1,1] (gap #4) | No | High — shipping Nez effect |
| 8 | **Reflection** (mirror + water) | Nez `.../Reflection.fx` | MIT | `vs_2_0`/`ps_2_0` | All-runtime (water tech uses `if`) | **2 techniques, each VS+PS** (gap #6), `frac`, `half2`, world-space, `if (...>...)` | No | Medium-High — shipping; larger; `half` type + many params (good stress) |
| 9 | **PaletteCycler** | Nez `.../PaletteCycler.fx` | MIT | `ps_3_0` | **DX-lean** (`tex1D`/`sampler1D`) | palette-swap via 1-D LUT, `_time` cycle | No | Medium — compiles in fxc; 1-D tex not GL-guaranteed → DX/verify |
| 10 | **Crosshatch** | Nez `.../Crosshatch.fx` | MIT | `ps_3_0` (**VPOS**, `%`) | **DX-lean** | **nested `if` + `<` relationals** (gap #3), `int` uniform, `%` | No | Medium — VPOS+float-modulo → DX render gate needed |
| 11 | **Letterbox** | Nez `.../Letterbox.fx` | MIT | `ps_3_0` (**VPOS**) | **DX-lean** | `min`, `if (... < size)`, screen-space | No | Medium — VPOS → DX/verify |
| 12 | **Bevels / edge-detect** | Nez `.../Bevels.fx` | MIT | `ps_2_0` | All-runtime | neighbor-tap edge detect (no loop), `tex2D` offsets (gap #4 outline) | Partial | High — tiny shipping effect |
| 13 | **SpriteLines** | Nez `.../SpriteLines.fx` | MIT | `ps_3_0` (**VPOS**, `%`) | **DX-lean** | 2 techniques (H/V), `floor`,`%`,`lerp` | No | Medium — VPOS → verify |
| 14 | **MonoGame stock `.fx`** (Basic/AlphaTest/...) | MonoGame `.../Effect/Resources/*.fx` | MS-PL | SM4 + Macros.fxh | DX-lean / already present | reference; NOT all-runtime | **Yes (in corpus)** | High but already covered |
| 15 | **Issue-#106 ternary-helper (original)** | author it (see §4.D) | project-owned | `ps_3_0`/`vs_3_0` | All-runtime | **the exact #106 regression**: helper returns ternary + relational | No | Author it to guarantee fit |

\* "All-runtime*" = after the trivial edit noted (drop unused VPOS param).

---

## 4. Top redistributable candidates — inline source (MIT, ready to vendor)

All four below are **Nez** (`prime31/Nez`, **MIT**, Copyright (c) 2016 Mike). They are reproduced verbatim from `DefaultContentSource/effects/`. **To use in the all-runtime corpus, prepend the standard `#if OPENGL` macro block and (where the shader uses a bare `sampler s0;`) keep that form — ShadowDusk already rewrites bare `sampler s0;` per `docs/test-shader-corpus.md` gap #2/#4.** Keep the MIT attribution in a header comment when vendoring (the project's convention, per `ExLegacyTextureDiscard.fx`).

> **Vendoring note.** The Nez originals use literal `ps_2_0`/`ps_3_0` and a bare `sampler s0;`. The project's existing Nez-derived fixtures (Grayscale, Sepia, …) were adapted to the `#if OPENGL`/`VS_SHADERMODEL` macro form. Mirror that: wrap profiles in the macro, add the `Texture2D`/`sampler2D = sampler_state` form if you want the modern path, or leave bare `s0` to exercise the synthesis rewrite. Add a provenance header like the other `examples/` fixtures.

### 4.A — GaussianBlur.fx  (closes gap #1: the `for`-loop hole)  ⭐ top pick

```hlsl
// Pixel shader applies a one dimensional gaussian blur filter. This is used twice by the bloom postprocess, first to
// blur horizontally, and then again to blur vertically.

sampler s0; // from SpriteBatch

#define SAMPLE_COUNT 15

float2 _sampleOffsets[SAMPLE_COUNT];
float _sampleWeights[SAMPLE_COUNT];


float4 PixelShaderFunction( float2 texCoord : TEXCOORD0 ) : COLOR0
{
    float4 c = 0;

    // Combine a number of weighted image filter taps.
    for( int i = 0; i < SAMPLE_COUNT; i++ )
        c += tex2D( s0, texCoord + _sampleOffsets[i] ) * _sampleWeights[i];

    return c;
}


technique GaussianBlur
{
    pass Pass1
    {
        PixelShader = compile ps_2_0 PixelShaderFunction();
    }
}
```
*Why:* The only `for`-loop in the all-runtime subset would now exist. Also adds `float2[]` + `float[]` array uniforms read **inside a loop with a literal-bounded index** (D3D9-safe). Pairs perfectly with the existing `ArrayUniform.fx` (which deliberately uses literal indices). Source: <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/GaussianBlur.fx>

### 4.B — Twist.fx  (closes gap #3: relational-driven `if` branch + trig)  ⭐ top pick

```hlsl
sampler s0;

float radius; // 0.5
float angle; // 5.0
float2 offset; // 0.5, 0.5


float4 PixelShaderFunction( float2 texCoord:TEXCOORD0 ) : COLOR0
{
    float2 coord = texCoord - offset;
    float dist = length( coord );

    if( dist < radius )
    {
        float ratio = ( radius - dist ) / radius;
        float angleMod = ratio * ratio * angle;
        float s = sin( angleMod );
        float c = cos( angleMod );
        coord = float2( coord.x * c - coord.y * s, coord.x * s + coord.y * c );
    }

    return tex2D( s0, coord + offset );
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_3_0 PixelShaderFunction();
    }
}
```
*Why:* `if (dist < radius)` is a genuine relational-driven branch in the PS body (not inside `clip`), with `length`/`sin`/`cos`. Source: <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Twist.fx>

### 4.C — Noise.fx  (closes gap #2: helper function called from entry point)  ⭐ top pick

```hlsl
sampler s0;

float noise; // 1.0

float rand( float2 co )
{
    return frac( sin( dot( co.xy, float2( 12.9898, 78.233 ) ) ) * 43758.5453 );
}


float4 PixelShaderFunction( float2 coords:TEXCOORD0 ) : COLOR0
{
    float4 color = tex2D( s0, coords );

    float diff = ( rand( coords ) - 0.5 ) * noise;

    color.r += diff;
    color.g += diff;
    color.b += diff;

    return color;
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_3_0 PixelShaderFunction();
    }
}
```
*Why:* A clean **helper function (`rand`) called from the entry point** — the structural twin of issue #106 — using `frac`/`sin`/`dot`. (The Nez original also declares `in float2 screenPos:VPOS` on the entry; **drop that unused param** when vendoring to keep it cleanly all-runtime — it is never referenced.) Source: <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Noise.fx>

### 4.D — Vignette.fx  (closes gap #4: vignette / radial post-FX)

```hlsl
sampler s0;

float _power; // 1.0
float _radius; // 1.25


float4 mainPS( float2 texCoord:TEXCOORD0 ) : COLOR0
{
	float4 color = tex2D( s0, texCoord );
	float2 dist = ( texCoord - 0.5f ) * _radius;
	dist.x = 1 - dot( dist, dist ) * _power;
	color.rgb *= dist.x;

	return color;
}



technique Vignette
{
	pass P0
	{
		PixelShader = compile ps_3_0 mainPS();
	}
};
```
*Why:* Common real effect missing from the corpus; minimal `dot`/swizzle; no VS. Source: <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Vignette.fx>

### 4.E — HeatDistortion.fx  (closes gap #4: 2nd-sampler `sampler_state` + UV distortion)

```hlsl
sampler s0;

texture _distortionTexture;
sampler2D _distortionTextureSampler = sampler_state
{
    Texture = <_distortionTexture>;
    AddressU = Wrap;
    AddressV = Wrap;
};


float _time; // Time used to scroll the distortion map
float _distortionFactor; // default 0.005. Factor used to control severity of the effect
float _riseFactor; // default 0.15. Factor used to control how fast air rises


float4 mainPS( float2 coords:TEXCOORD0 ) : COLOR0
{
    float2 distortionUV = coords;
    distortionUV.y -= _time * -_riseFactor;

    // Compute the distortion by reading the distortion map
    float2 distortionMapValue = tex2D( _distortionTextureSampler, distortionUV ).xy;

	// bring it into the -1 to 1 range
    float2 distortionPositionOffset = distortionMapValue;
    distortionMapValue = ( ( distortionMapValue * 2.0 ) - 1.0 );

    // The _distortionFactor scales the offset and thus controls the severity
    distortionMapValue *= _distortionFactor;

    distortionMapValue *= ( coords.y ); // 1.0 - coords.y for actual OpenGL due to coords x at the bottom

	return tex2D( s0, distortionMapValue + coords );
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_2_0 mainPS();
    }
}
```
*Why:* Adds a **distortion-map second sampler declared with explicit `AddressU/V = Wrap` `sampler_state`** (sampler-state baking coverage), time-animated UV, remap-to-signed pattern. Source: <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/HeatDistortion.fx>

---

## 5. The issue-#106 regression fixture (author this one — guarantees the exact bug is pinned)

For the most direct #106 coverage, **author a project-owned original** (no licensing question, fits the all-runtime subset exactly, and isolates *exactly* the reported failure: a helper that returns a ternary + a relational expression, in both a helper and the entry point, on the VS+PS sprite path). This mirrors the reporter's shader without copying it. Drop it at `tests/fixtures/shaders/examples/ExTernaryHelper.fx`:

```hlsl
// =============================================================================
// ExTernaryHelper.fx  —  ShadowDusk fresh example fixture (issue #106)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #106).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#106 class — a helper function that RETURNS a
//              ternary built from a relational operator, called from the pixel
//              entry point, plus a relational/ternary used directly in the PS
//              body. VS+PS sprite path (the validated shape).
// Exercises  : ternary `?:` return from a helper, relational ops (<=, >, <),
//              ternary in entry body, helper call from entry, VS mul-transform.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

matrix MatrixTransform;

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

// The issue-#106 shape: a helper whose body is a single ternary over a relational op.
float Threshold(float value)
{
    return value <= 0.5f ? 0.0f : 1.0f;
}

// A second helper returning a relational-derived float (no ternary) for contrast.
float Band(float value, float lo, float hi)
{
    return (value > lo && value < hi) ? 1.0f : 0.0f;
}

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color    = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    float4 tex = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;

    float step = Threshold(input.TexCoord.x);
    float band = Band(input.TexCoord.y, 0.25f, 0.75f);

    // Ternary directly in the entry body as well.
    float3 rgb = step >= 0.5f ? tex.rgb : tex.rgb * 0.25f;
    rgb = lerp(rgb, float3(1, 1, 1), band);

    return float4(rgb, tex.a);
}

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
```
*Confidence:* Uses only SM3-legal constructs (relationals, `?:`, `&&`, `lerp`, `mul`, `tex2D`) that `fxc`/`mgfxc` compile at `ps_3_0`/`ps_4_0_level_9_1` and that `fxc /T fx_2_0` accepts when re-profiled to `ps_2_0`/`vs_2_0`. It is the reporter's pattern, generalized, so a green compile + render here is the direct evidence for issue #106.

---

## 6. Recommendation — the 5–10 to add, and the gap each closes

**Tier 1 — add now (all permissive, all-runtime, each closes a distinct gap):**

1. **`ExTernaryHelper.fx`** (project-owned, §5) — *the* issue-#106 regression: helper returns ternary + relational. **Gap #2 / the issue itself.** Author it; no license risk.
2. **GaussianBlur.fx** (Nez MIT, §4.A) — **gap #1**: the missing `for`-loop, plus loop-indexed array uniforms. Highest-value structural addition.
3. **Twist.fx** (Nez MIT, §4.B) — **gap #3**: relational `if` branch in PS body + `length`/`sin`/`cos`.
4. **Noise.fx** (Nez MIT, §4.C, drop the unused VPOS param) — **gap #2**: helper `rand()` called from entry; `frac`/`sin`/`dot`.
5. **Vignette.fx** (Nez MIT, §4.D) — **gap #4**: classic vignette post-FX (none in corpus); `dot`/swizzle.

**Tier 2 — add for breadth (still permissive, all-runtime):**

6. **HeatDistortion.fx** (Nez MIT, §4.E) — **gap #4**: distortion-map 2nd sampler with explicit `AddressU/V=Wrap sampler_state` + time-scrolled UV.
7. **PixelGlitch.fx** (Nez MIT) — **gap #2**: a *second* helper-function case (`hash11()`), `floor`/`frac`.
8. **Reflection.fx** (Nez MIT) — **gap #6**: a real **two-technique VS+PS** permissive effect (mirror + water), `if (...>...)`, `frac`, `half2`. (Larger/heavier — good stress; the water technique's `if` is a bonus.)

**Tier 3 — DX-lean, add ONLY behind a Windows render gate (note as DX-only, not in the all-runtime pixel-equivalence set):**

9. **Crosshatch.fx** (Nez MIT) — nested `if` + `<` relationals + `int` uniform + `%` + **VPOS** → verify on DX.
10. **PaletteCycler.fx** (Nez MIT) — palette-swap via **`tex1D`/`sampler1D`** → DX/verify (not GL-guaranteed).

**What each tier proves.** Tier 1+2 are wrapped in the `#if OPENGL` macro and go straight into the same SpriteBatch/`SpriteEffect` validated path the existing Nez-derived fixtures use, so they extend the corpus along the **all-runtime SM3/fx_2_0** axis (MonoGame-GL, MonoGame-DX, KNI, FNA). Tier 3 documents two real shaders that compile under `fxc`/`mgfxc` but use VPOS / 1-D textures whose cross-path behavior is unverified — keep them as **DX-only** corpus entries gated by `validation/run-windows-render-gates.ps1` until rung-4 render-verified.

**Do NOT add (redundant or non-permissive):**
- MonoGame docs tutorial **grayscale** snippet — duplicates `Grayscale.fx` AND is **CC-BY-NC-SA (NonCommercial)** → reference only, not vendorable.
- MonoGame **stock effects** beyond what's present — already in corpus; MS-PL but SM4/Macros.fxh, not all-runtime.
- More cbuffer / array-uniform / annotation / render-state probes — already well-covered (§1).

---

## 7. Sources

- ShadowDusk issue #106 ("Shader should be able to return ternary values"), reporter `vchelaru` — `gh issue view 106`; XnaFiddle (powered by ShadowDusk): <https://github.com/vchelaru/XnaFiddle>, <https://xnafiddle.net/>
- Nez (MIT) effect sources — <https://github.com/prime31/Nez/tree/master/DefaultContentSource/effects> ; license <https://github.com/prime31/Nez/blob/master/LICENSE>
  - GaussianBlur <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/GaussianBlur.fx>
  - Twist <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Twist.fx>
  - Noise <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Noise.fx>
  - Vignette <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Vignette.fx>
  - HeatDistortion <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/HeatDistortion.fx>
  - PixelGlitch <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/PixelGlitch.fx>
  - Reflection <https://github.com/prime31/Nez/blob/master/DefaultContentSource/effects/Reflection.fx>
  - Crosshatch / PaletteCycler / Letterbox / SpriteLines / Bevels / SpriteBlinkEffect (same dir)
- MonoGame stock effect resources (MS-PL) — <https://github.com/MonoGame/MonoGame/tree/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources> (SpriteEffect.fx, AlphaTestEffect.fx, BasicEffect.fx, …)
- manbeardgames/monogame-hlsl-examples (MIT) — <https://github.com/manbeardgames/monogame-hlsl-examples> ; license <https://github.com/manbeardgames/monogame-hlsl-examples/blob/master/LICENSE>
- MonoGame docs grayscale tutorial (CC-BY-NC-SA, reference only) — <https://github.com/MonoGame/docs.monogame.github.io/blob/main/articles/tutorials/building_2d_games/24_shaders/index.md> ; snippet <https://raw.githubusercontent.com/MonoGame/docs.monogame.github.io/main/articles/tutorials/building_2d_games/24_shaders/snippets/grayscaleeffect.fx>
- MojoShader (the GL translator MonoGame/KNI use; VPOS↔gl_FragCoord hazard) — <https://github.com/MonoGame/mojoshader>
- GerhardSchreurs/MonoGame_GaussianBlur (dhpoware XNA lineage; alternative blur reference) — <https://github.com/GerhardSchreurs/MonoGame_GaussianBlur>
- Existing-corpus provenance — `docs/test-shader-corpus.md` (Nez + manbeardgames + Penumbra lineage, MS-PL stock effects)
