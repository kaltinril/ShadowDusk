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
| `mat_compound_assign.glsl` | **B1**: matrix compound assignment `v *= M` (must become `v = mul(v, M)`; scalar `*=` stays component-wise). |
| `equality_parens.glsl` | **B2**: scalar equality `if (a == 0.0)` must not be double-parenthesized. |
| `vector_equality.glsl` | **B3**: vector `==`/`!=` in a bool context must be scalarized with `all(...)`/`any(...)` (incl. a `&&` chain). |
| `vector_truncation.glsl` | **B4**: implicit vector truncation on assign/init must emit an explicit `.xy`/`.xyz` swizzle. |
| `decl_modifier_spacing.glsl` | **B5**: a stray storage/precision modifier after the type must be dropped (no "modifiers must appear before type"). |
| `builtin_redeclare.glsl` | **L1(a)**: redundant `uniform iTime/iResolution/iMouse` re-declarations are dropped, not rejected. |
| `iglobaltime_alias.glsl` | **L1(b)**: deprecated `iGlobalTime` alias maps to `iTime`. |
| `macro_function.glsl` | Function-like `#define SQR(x) ...` / `#define MIXC(a,b,t) ...` macros expand with argument substitution at the call site. |
| `pp_ifdef.glsl` | `#ifdef` / `#ifndef` / `#else` gating: the inactive branch bodies are dropped (blanked), never emitted. |
| `pp_if_arith.glsl` | `#if` with an integer constant expression (arithmetic, comparison, shift, `&&`, macro expansion). |
| `pp_nested.glsl` | Nested `#if` / `#elif` / `#else` with correct nesting (only the one selected branch survives). |
| `pp_defined.glsl` | `defined(NAME)` and bare `defined NAME` inside an `#if` expression. |
| `pp_undef.glsl` | `#undef` followed by a redefinition; the use before/after sees the right value, and `#ifdef` sees the redefine. |
| `custom_uniform_scalar.glsl` | **Custom uniforms**: a top-level `uniform float` + `uniform vec3` emitted as effect-parameter globals the consumer drives. |
| `custom_sampler.glsl` | **Custom sampler**: a top-level `uniform sampler2D` emitted as the iChannelN-style texture + sampler_state pair; `texture(...)` → `tex2D`. |
| `custom_uniform_alias.glsl` | **Alias nicety**: the exact-type glslViewer alias `uniform float u_time;` folds onto `iTime`, while a genuinely-custom `uniform float uSpeed;` is exposed verbatim. |

Total: 33 authored shaders.
