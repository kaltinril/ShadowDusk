# CC0 / public-domain corpus — provenance (Phase 46)

This folder holds **real, third-party** single-pass ShaderToy image shaders included ONLY because
their author **explicitly** dedicated them to the public domain (CC0). The bar (per the Phase 46
task) is an *explicit* license statement by the author that we can quote; anything we could not
verify was excluded rather than guessed at. Quality of provenance over quantity.

## Included shaders

### `neon.glsl`

| | |
|---|---|
| **File** | `neon.glsl` |
| **Original ShaderToy** | https://www.shadertoy.com/view/WlByzy |
| **Author** | mrange (Marten Range) — author of the "Neonwave style road, sun and city" shader. |
| **License** | **CC0 1.0 (public domain dedication)** — https://creativecommons.org/publicdomain/zero/1.0/ |
| **License statement (verbatim, line 2 of the source)** | `// License CC0: Neonwave style road, sun and city` |
| **Verification path** | The shader ships verbatim, with its `// License CC0` header intact, in the GTK4 `gtk-demo` shader set, fetched from a public mirror: `https://raw.githubusercontent.com/udevbe/greenfield/6c578f4db7ec027eb1d8a5f7ec6e09f7646dbb57/examples/sdk/gtk4/demos/gtk-demo/neon.glsl`. The GTK project bundles a number of these ShaderToy shaders specifically because each carries the author's explicit `// License CC0` header; that header is the license grant we are relying on. The top comment also records the original ShaderToy URL (`https://www.shadertoy.com/view/WlByzy`). |

**Verbatim license header as found in the source:**

```glsl
// Originally from: https://www.shadertoy.com/view/WlByzy
// License CC0: Neonwave style road, sun and city
//  The result of a bit of experimenting with neonwave style colors.
```

**Trimming applied (to fit the v1 ShaderToy->FX subset):** the original is a standard
`void mainImage(out vec4 fragColor, vec2 fragCoord)` image shader, already very close to the
subset. Two minimal, fully-documented changes were made (and are also annotated inline in the file):

1. `float mod1(inout float p, float size)` used an `inout` parameter (not in the v1 subset) and
   carried **two** outputs: it wrote the wrapped coordinate into `p` and *returned* the cell index.
   It was split into two pure helpers, `mod1p` (returns the wrapped coordinate) and `mod1cell`
   (returns the cell index), and each caller updated to use whichever output(s) the original used.
   Behaviorally identical.
2. The `groundEffect` antialias width used `length(dFdx(pg))*...` (screen-space derivatives, not in
   the v1 subset). It was replaced with a small screen-space constant. Visually near-identical; the
   grid edges are a hair softer.

No other logic was altered. The CC0 dedication permits modification and redistribution without
restriction, so the trim is licensed.

## Considered but EXCLUDED (and why)

To keep the bar honest, these were investigated and deliberately left out:

- **`glowingstars.glsl`, `mandelbrot.glsl`, `cogs.glsl`** (all CC0, same GTK demo set, explicit
  `// License CC0` headers, real ShaderToy origins). Excluded because each leans heavily on
  out-of-subset constructs (`inout` helpers used pervasively, and `dFdx`/`dFdy` for AA). Trimming
  them to the v1 subset would require rewriting core logic to the point of misrepresenting the
  original shader, so they were dropped in favor of the single cleanly-trimmable `neon.glsl`. Their
  provenance is just as solid if a later, broader subset wants them.
- **`radial.glsl`, `kaleidoscope.glsl`** (MIT, from gl-transitions.com, present in the same GTK
  set). Excluded because they are **not** standard ShaderToy image shaders: they use a non-standard
  `mainImage(out vec4, in vec2 fragCoord, in vec2 resolution, in vec2 uv)` signature and GTK's
  `GskTexture` / `progress` / `u_texture*` uniforms rather than the ShaderToy harness. Out of scope
  for a faithful "ShaderToy image shader" corpus.
- **The shadertoy.com pages themselves** could not be fetched directly to screenshot the license
  badge (the site returns HTTP 403 to automated fetches). The in-file `// License CC0` header on the
  GTK mirror is therefore the authoritative, quotable grant we relied on.

## Summary

**1 CC0 shader included** (`neon.glsl`), verified via an explicit in-file `// License CC0` header
(author mrange, ShaderToy `WlByzy`), sourced from the GTK4 gtk-demo mirror which preserves that
header verbatim. Trimmed minimally (one `inout` helper -> two pure helpers; one `dFdx` AA term ->
constant), both changes documented inline and above.
