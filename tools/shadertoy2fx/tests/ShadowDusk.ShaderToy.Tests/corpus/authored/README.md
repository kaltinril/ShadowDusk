# Authored corpus — ShaderToy → FX (Phase 46)

Original single-pass ShaderToy *image* shaders written for this project (no copyright concerns).
Each uses `void mainImage(out vec4 fragColor, in vec2 fragCoord)` and the ShaderToy predefined
uniforms, stays inside the v1 supported subset, and deliberately exercises **one** feature or
translation trap so a failure points at a single cause.

| File | Feature / trap targeted |
|---|---|
| `gradient_uv.glsl` | Basic `fragCoord / iResolution.xy` UV normalization into a gradient. |
| `time_animation.glsl` | `iTime` animation driving `sin`/`cos` color. |
| `mouse_interaction.glsl` | `iMouse.xy` usage; distance-to-cursor glow. |
| `texture_channel0.glsl` | `texture(iChannel0, uv)` sampling (must become `Texture2D.Sample` / `tex2D`). |
| `mat2_rotation.glsl` | **Matrix trap**: `mat2 * vec2` rotation (column-major `M*v` must become `mul(v, M)`). |
| `mod_negative.glsl` | **Mod trap**: `mod()` on a possibly-negative centered coordinate (GLSL `mod` != HLSL `fmod`). |
| `helper_functions.glsl` | User-defined helper functions called from `mainImage`. |
| `for_loop_accumulate.glsl` | Bounded `for` loop with `+=` compound accumulation; `float(i)` cast. |
| `while_loop.glsl` | Bounded `while` loop (distinct from `for`) with `++`. |
| `if_else_branch.glsl` | `if` / `else if` / `else` control flow. |
| `ternary_select.glsl` | Ternary `?:` with relational operators (the issue #106 shape). |
| `swizzle_ops.glsl` | Read and write swizzles (`.xy`, `.yx`, `.rgb`, `.bgr`). |
| `mix_clamp_smoothstep.glsl` | `mix`(→`lerp`), `clamp`, `smoothstep`, `fract`(→`frac`) intrinsics. |
| `atan_polar.glsl` | Two-arg `atan(y, x)` (→`atan2`) for polar angle; `length` radius. |
| `define_constants.glsl` | Object-like `#define` constants (`PI`, `TAU`, `SCALE`). |
| `length_normalize_dot.glsl` | `length` / `normalize` / `dot` vector intrinsics (lambert-style dot). |
| `pow_gamma.glsl` | `pow`, `abs`, vec3 gamma curve, `*=` compound assignment. |

Total: 17 authored shaders.
