# Third-party shaders - XnaFiddle

These `.fx` files are the example shaders from **XnaFiddle** (`vchelaru/XnaFiddle`,
Victor Chelaru's browser MonoGame/KNI playground). They are the shaders Gum authors
reuse on both KNI and Skia, so they are the fixtures for the SkSL converter's default
`COLOR0` conversion (issue #368, Phase 62 Area D): each reads `input.Color` (SpriteBatch's
vertex color).

## Upstream project

- **Project:** XnaFiddle
- **Author / copyright:** Copyright (c) 2026 Victor Chelaru
- **Repository:** <https://github.com/vchelaru/XnaFiddle>
- **License:** MIT (verbatim text in `./LICENSE`)
- **Commit fetched (pinned for reproducibility):** `0a6edd690db3a8bd42b92676c8e5b31cf13969da`
- **Fetched:** 2026-10-03

## Modifications

The shader code is UNMODIFIED, byte-for-byte identical to upstream. The ONLY change to
each file is a provenance comment block prepended at the top. Local filenames are prefixed
`XnaFiddle-` so a stem lookup cannot collide with the MonoGame fixtures of the same name.

## Files

| Local file | Upstream path (under `XnaFiddle.BlazorGL/wwwroot/examples/`) | SkSL converter |
|---|---|---|
| `XnaFiddle-Fading.fx` | `Fading/Fading.fx` | converts |
| `XnaFiddle-Grayscale.fx` | `Grayscale/Grayscale.fx` | converts |
| `XnaFiddle-Invert.fx` | `Invert/Invert.fx` | converts |
| `XnaFiddle-Pixelated.fx` | `Pixelated/Pixelated.fx` | refused SD0612 (computed-UV sampling), pinned |
| `XnaFiddle-Tint.fx` | `Tint/TintShader.fx` | converts |
| `XnaFiddle-Mask.fx` | `Masking/Mask.fx` | converts |

Used only by `tests/ShadowDusk.Compiler.Tests/Sksl`. A compile or render of these is not
an `mgfxc`-equivalence claim.
