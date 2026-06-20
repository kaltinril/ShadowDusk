# Authored corpus — ShaderToy → FX (Phase 46)

Original single-pass GLSL *image* shaders written for this project (no copyright concerns). Most use
the ShaderToy `void mainImage(out vec4 fragColor, in vec2 fragCoord)` entry; the `main_*` shaders use
the plain-GLSL `void main()` entry (G2). Each stays inside the v1 supported subset and deliberately
exercises **one** feature or translation trap so a failure points at a single cause.

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
| `global_mutable.glsl` | **G1**: top-level non-`const` mutable globals (bare, initialized, comma multi-declarator) emit as HLSL `static` globals; a helper mutates the global before `mainImage` reads it back. |
| `uniform_default.glsl` | **G4**: a custom `uniform` with a default value (`uniform float uGain = 1.5;`, `uniform vec3 uColor = vec3(...)`) preserves the initializer as the HLSL parameter default. |
| `pp_version_extension.glsl` | **G5**: `#version` / `#extension` / `#pragma` / glslViewer `#iChannel0 "..."` metadata directives are all silently dropped (never rejected). |
| `alias_u_time.glsl` | **G3**: a glslViewer exact-type alias `uniform float time;` folds onto the ShaderToy built-in `iTime`. |
| `struct_basic.glsl` | **G6**: a user `struct` (with a **matrix member**) + the GLSL constructor `Name(...)` (-> generated `make_Name(...)` factory) + member access; the struct-member matrix multiply `s.rot * s.pos` must still emit `mul(...)` (the trap survives member access). |
| `const_array.glsl` | **G7**: a `const` global array via the GLSL array constructor `float[](...)` and the sized `vec3[2](...)` form (-> HLSL brace list `{ ... }`), plus indexing in a loop. |
| `local_array.glsl` | **G7**: a local fixed-size array (`float samples[4];`) written/read by index, plus a local `const` array initialized by an array constructor. |
| `intrinsic_fwidth.glsl` | **G7**: the `fwidth` intrinsic maps to the same-named HLSL intrinsic. |
| `matrix_comp_mult.glsl` | **G7**: `matrixCompMult(a, b)` is COMPONENTWISE and must emit `(a * b)`, NOT the mul()-reordered linear product (the trap must NOT fire here). |
| `for_comma_increment.glsl` | **G7 parser**: the GLSL comma (sequence) operator in a `for` header (`for (int i=0, j=4; ...; i++, j--)`). |
| `main_glfragcolor.glsl` | **G2**: plain-GLSL `void main()` entry writing the legacy `gl_FragColor`, reading `gl_FragCoord` (a UV gradient; the gl_FragCoord -> harness-pixel-coord + gl_FragColor -> PS-return bridge). |
| `main_out_var.glsl` | **G2**: plain-GLSL `void main()` with a user-declared `out vec4 outColor;` (GLSL ES 3.00 / 330) consumed (not a parameter/global) and returned as COLOR0; plus a helper. |
| `main_custom_resolution.glsl` | **G2**: plain-GLSL `void main()` reading a declared custom `uniform vec2 resolution;` (exposed as a host-driven effect parameter, not folded onto vec3 iResolution) + `gl_FragCoord`. |
| `mainimage_with_main_wrapper.glsl` | **G2 both-entries**: a ShaderToy `mainImage` PLUS a standalone `void main(){ mainImage(gl_FragColor, gl_FragCoord.xy); }` wrapper. The converter PREFERS ShaderToy mode, DROPS the `void main()` wrapper with a Warning, and emits the same `.fx` as the wrapper-less shader (our harness replaces `main`). The common third-party desktop-runner shape. |
| `array_global_const.glsl` | **G7a**: a global `const` array with the size suffix AFTER the base type (`const float[N] name`) and a GLSL brace initializer list (`= { ... }`) -> `static const T name[N] = { ... };`. |
| `array_local.glsl` | **G7b**: a local array with the size suffix AFTER the base type (`vec2[4] c = {...};`) using a brace initializer list. |
| `array_param.glsl` | **G7c**: an array function **parameter** with the size after the type (`void f(inout float[N] k)`); HLSL spells the size on the declarator name (`inout float k[N]`). |
| `array_constructor.glsl` | **G7**: the GLSL array-constructor expression in both the unsized `T[](...)` and sized `T[N](...)` forms, used as a declaration initializer (-> HLSL brace list). |
| `bitwise_ops.glsl` | **Bitwise**: `& \| ^ << >>` and the compound forms `&= \|= ^= <<= >>=` pass straight through to HLSL (valid on int), distinct from logical `&&`/`\|\|`. |
| `glfragcoord_in_body.glsl` | **G3c**: `gl_FragCoord` referenced directly in a `mainImage` body (a `float4`: `.xy` = pixel coord, `.z` = 0, `.w` = 1); the harness publishes a `static float4 gl_FragCoord;` and sets it before calling `mainImage`. |
| `uint_type.glsl` | **uint mapping**: `uint` -> `int` and `uvec2/3/4` -> `int2/3/4` (treated as signed int; common hash idiom). |
| `redeclare_ichannel.glsl` | **Channel redeclare**: a redundant `uniform sampler2D iChannel0;` re-declaration is accepted-and-ignored (the built-in channel is already injected). |
| `uniform_multi_declarator.glsl` | **Multi-declarator uniform**: a comma list `uniform float uA, uB, uC;` (each becomes its own custom uniform) plus a redundant built-in WITH an initializer (`uniform vec3 iResolution = vec3(...);`) dropped. |

Total: 56 authored shaders.
