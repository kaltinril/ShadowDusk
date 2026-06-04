# Changelog

All notable changes to ShadowDusk are documented here. The project has not yet had a tagged release; entries accumulate under **Unreleased**.

## [Unreleased]

### Added

- **GL texture breadth — cube maps, 3D/volume textures, and explicit-LOD/gradient sampling (Phase 34).** These valid HLSL texture features previously failed to compile for the OpenGL target (`SD0210`, added in the #7 fix as a safe interim); they now compile to working GLSL on the platforms that support them:
  - **Cube maps** (`TextureCube` / `samplerCube`) — supported on **Desktop GL, KNI HiDef (WebGL2), and KNI Reach (WebGL1)**. ShadowDusk emits the legacy `samplerCube ps_s{k}` + `textureCube(…)` form (byte-matching `mgfxc`'s own cube output) and the correct MonoGame sampler-type byte. Cross-validated against the `mgfxc` `EnvironmentMapEffect` golden (same form + sampler-type byte).
  - **3D / volume textures** (`Texture3D` / `sampler3D`) — supported on **Desktop GL + KNI HiDef**. Not available on **KNI Reach / WebGL1**, which has no 3D textures at all (a platform limitation, not ShadowDusk's).
  - **Explicit-LOD / gradient sampling** (`Texture2D.SampleLevel` / `SampleGrad`) — supported on **Desktop GL + KNI HiDef** via the generic `textureLod` / `textureGrad` builtins (core in GLSL ES 3.00; KNI HiDef passes them through). On **KNI Reach / WebGL1** these are gated behind an optional, non-guaranteed extension — a platform limitation.
  - These walls (3D + explicit-LOD on Reach/WebGL1) are **documented limitations**, not compile errors: ShadowDusk emits one OpenGL blob and cannot know the consumer's KNI profile at compile time. Sampler kinds still unmodelled (`sampler2DArray`, shadow samplers) continue to fail loudly with `SD0210`.
  - **Action required:** **recompile your `.fx`** to pick up cube/3D/LOD/grad support.

### Fixed

- **KNI HiDef / WebGL2 shader loading ([#7](https://github.com/kaltinril/ShadowDusk/issues/7)).** A ShadowDusk-compiled `.mgfx` loaded in KNI **Reach** (WebGL1) but failed in KNI **HiDef** (WebGL2 / GLSL ES 3.00) with `'gl_FragColor' : undeclared identifier`. ShadowDusk now emits the pixel-shader colour output as the `#define ps_oC0 gl_FragColor` form (matching `mgfxc`), which KNI's runtime ES-3.00 converter rewrites correctly — so **one `.mgfx` now works in both Reach and HiDef** with no flag, no separate build, and no API change. Validated 10/10 in a real headless KNI HiDef/WebGL2 runtime.
  - **Action required:** **recompile your `.fx`** after upgrading — a `.mgfx` built by an older ShadowDusk keeps the old output and still fails under HiDef.
  - HiDef shader loading requires **KNI ≥ v3.14.9001** (the release that added KNI's runtime ES-3.00 converter; any recent KNI qualifies). Reach and desktop GL have **no** version requirement.
- **Single-output fragment semantics.** A shader whose pixel output was written `: COLOR0` / `SV_Target0` was emitting `gl_FragData[0]` instead of `gl_FragColor`; an uppercase `: SV_TARGET` semantic was silently producing a non-rendering effect. Both now emit the correct primary `gl_FragColor` output. (Surfaced while generalising the #7 fix.)

### Changed

- GL shaders using sampler kinds the MojoShader-dialect rewriter still doesn't model — **array / shadow samplers** (`sampler2DArray`, `sampler2DShadow`, `samplerCubeArray`, …) — **fail loudly at compile time** (`SD0210`) instead of silently emitting broken GLSL. (Originally, Phase 33 also guarded cube/3D/LOD/grad; Phase 34 lifts those — see **Added** above.) Plain `tex2D` / `Texture2D.Sample` on `sampler2D` is unaffected.
