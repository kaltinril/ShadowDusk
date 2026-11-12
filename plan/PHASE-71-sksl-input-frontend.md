# Phase 71 — SkSL input frontend (`ShadowDusk.SkSL`)

**Track:** Input frontends.
**Status:** 🚧 **In progress (2026-11-12).** Slice 1 (color shaders and `uniform shader` children)
is implemented and render-proven against real SkiaSharp; the package is not published yet.

## Goal

A Skia user's SkSL runtime-effect shader compiles to an `.fx` that renders in MonoGame/KNI the way
it renders in Skia. This is the input direction; [Phase 62](PHASE-62-skiasharp-sksl-target.md) is
the output direction (`.fx` to SkSL). Skia has no reference compiler for SkSL-to-HLSL, so the
evidence is image fidelity against real SkiaSharp (`validation/SkSLVsGl`), never rung 4.

## Design rule (owner's principles, in priority order)

1. **Correct:** a given SkSL shader behaves identically on Skia+SkSL and on MonoGame+HLSL. Anything
   the frontend cannot convert faithfully fails with an SkSL-worded diagnostic that says what to
   change; it never converts to something that renders differently.
2. **Fast:** the generated shader is not slower than it needs to be, and it loads quickly. Prefer a
   multiply by a host-set reciprocal over a per-pixel divide where results do not change, hoist
   per-pixel work to the host where possible, and keep the conversion itself cheap.
3. **Readable:** the generated shader is idiomatic, expressive and properly formatted (no redundant
   parentheses such as `((c + float2(1, 0)) * imgTexelSize)`).

Apply them in that order to every decision in this phase.

## Slice 1 (done): color shaders and children

- `SkSLToFxConverter.Convert(sksl, options)`: comments blanked in place, an SkSL pre-scan, then a
  source rewrite to the ShaderToy GLSL subset and `ShaderToyConverter` in an internal SkSL mode.
- Entry `half4 main(float2 coord)` (also `float4`/`vec4`, `half2`/`vec2`) becomes `mainImage`; the
  harness passes `input.UV * iResolution.xy`, which is SkSL's top-left, pixel-center coordinate
  (no double Y flip).
- `uniform shader img;` becomes a sampler plus a host-set `imgTexelSize` (1 / size in pixels), and
  `img.eval(p)` becomes `tex2D(img, p * imgTexelSize)`.
- Alpha: one `.fx`, two techniques. `SkSL` (default): premultiplied textures, `BlendState.AlphaBlend`.
  `SkSLStraightAlpha` (Gum's default): straight-alpha textures, `BlendState.NonPremultiplied`; child
  samples are premultiplied after filtering and the result un-premultiplied. The choice is a
  literal-set `static bool` that folds away, so there is no runtime branch. RGB matches Skia on
  both; `NonPremultiplied`'s destination alpha (`a*a + d*(1-a)`) differs from `SrcOver`'s.
- Rejected with SkSL diagnostics: `sk_FragCoord`, `layout(color)`, color filter and blender
  entries and children, `shader` parameters, `unpremul`, `toLinearSrgb`/`fromLinearSrgb`,
  non-square matrices, `gl_*`/`sk_*` names, the names the ShaderToy stage owns (`iResolution`,
  `iTime`, …, `mainImage`) and `<child>TexelSize`.
- Gate: `validation/SkSLVsGl`, 64x48 target, children of other non-square sizes, six premultiplied
  cases, straight-alpha arms of the three translucent ones, and seven positive controls (Y flip,
  eval offset, swapped resolution, non-premultiplied blend, straight texture on the premultiplied
  path, straight setup with the default technique). In the ubuntu GL CI job and the Windows
  render-gate script.

## Readable output (done)

- The shared ShaderToy emitter writes only the parentheses that precedence, associativity or a
  reader needs (`HlslEmitter`'s parenthesization policy), so SkSL and ShaderToy output both read
  `tex2D(img, (c + float2(1, 0)) * imgTexelSize)`. All 84 re-pinned converter goldens compile to
  byte-identical `.mgfx` before and after on OpenGL and DirectX 11 (vkd3d and `d3dcompiler`), and
  `mgfxc` rebuilt the `GradientToy` goldens byte-identical.

## Remaining slices

1. **`sk_FragCoord`**: needs the device coordinate, which equals the local coordinate only when the
   shader is drawn untransformed; decide the host contract, then implement and gate it.
2. **`layout(color)` uniforms**: Skia converts them from sRGB to the working color space; define
   what MonoGame's equivalent is (usually none) and prove it.
3. **Color filter and blender children** (`uniform colorFilter`, `uniform blender`) and their
   `eval(half4)` / `eval(half4, half4)` calls, plus `shader` as a function parameter (inline the
   child at the call site).
4. **Color-space built-ins**: `unpremul` is a straight rewrite; `toLinearSrgb`/`fromLinearSrgb`
   depend on the working color space and need a host contract first.
5. **Matrix uniforms**: square matrices convert (`mul(p, m)`), but the host-side upload order is
   not render-proven against Skia yet.
6. **DirectX and FNA arms**: the gate proves OpenGL only; add a DX11-on-WARP arm.
7. **Packaging**: a `PackageId`, `README.md`, `release.yml` entry, the support-surface docs
   (`README.md` targets table, `docs/the-purpose.md`, `docs/pipeline-overview.puml` and its SVG,
   the DocFX pages) and a CLI/`ShaderFormatDetector` route. It must ship at the same version as
   `ShadowDusk.ShaderToy`, whose internals it uses.

## Done when

Every slice above is implemented or explicitly refused with a decision recorded, the gate covers
each implemented construct with a positive control, and the package ships.
