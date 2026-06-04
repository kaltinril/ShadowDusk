# Changelog

All notable changes to ShadowDusk are documented here. The project has not yet had a tagged release; entries accumulate under **Unreleased**.

## [Unreleased]

### Fixed

- **KNI HiDef / WebGL2 shader loading ([#7](https://github.com/kaltinril/ShadowDusk/issues/7)).** A ShadowDusk-compiled `.mgfx` loaded in KNI **Reach** (WebGL1) but failed in KNI **HiDef** (WebGL2 / GLSL ES 3.00) with `'gl_FragColor' : undeclared identifier`. ShadowDusk now emits the pixel-shader colour output as the `#define ps_oC0 gl_FragColor` form (matching `mgfxc`), which KNI's runtime ES-3.00 converter rewrites correctly — so **one `.mgfx` now works in both Reach and HiDef** with no flag, no separate build, and no API change. Validated 10/10 in a real headless KNI HiDef/WebGL2 runtime.
  - **Action required:** **recompile your `.fx`** after upgrading — a `.mgfx` built by an older ShadowDusk keeps the old output and still fails under HiDef.
  - HiDef shader loading requires **KNI ≥ v3.14.9001** (the release that added KNI's runtime ES-3.00 converter; any recent KNI qualifies). Reach and desktop GL have **no** version requirement.
- **Single-output fragment semantics.** A shader whose pixel output was written `: COLOR0` / `SV_Target0` was emitting `gl_FragData[0]` instead of `gl_FragColor`; an uppercase `: SV_TARGET` semantic was silently producing a non-rendering effect. Both now emit the correct primary `gl_FragColor` output. (Surfaced while generalising the #7 fix.)

### Changed

- GL shaders using texture **LOD / projected / gradient** sampling (`Texture2D.SampleLevel` / `SampleGrad`, `tex2Dproj`) or **non-2D samplers** (`samplerCube` / `sampler3D`) now **fail loudly at compile time** (`SD0210`) instead of silently emitting GLSL that breaks under WebGL. These were not correctly supported before; full HiDef-safe support is tracked as a follow-up (Phase 34). Plain `tex2D` / `Texture2D.Sample` on `sampler2D` is unaffected.
