# Reject corpus — ShaderToy → FX (Phase 46)

Shaders that **must be rejected** with a clear, located diagnostic and a non-zero exit (project
constraint 5: fail loudly, never emit silently-wrong HLSL). Each shader is otherwise valid v1
ShaderToy GLSL — the listed construct is the **only** out-of-scope thing in it, so the harness can
assert the tool rejects for that specific reason.

| File | Expected rejection reason |
|---|---|
| `nested_struct.glsl` | A struct with a **nested / inline** struct member (`struct { ... } inner;`) — a flat user struct is now supported (G6), but an inline nested struct is not. |
| `unsized_array.glsl` | An **unsized / runtime-sized** array (`float data[];`) — a fixed-size array (`float k[3];`) is now supported (G7), but an unsized one has no fixed length. |
| `unmappable_intrinsic.glsl` | Calls `roundEven` (round-half-to-even / banker's rounding), which has no faithful HLSL equivalent — an unmappable intrinsic is a loud reject (the mapping table stays authoritative). |
| `second_entry_cubemap.glsl` | Contains a second entry point (`mainCubemap`) — v1 supports a single `mainImage` image shader only. |
| `switch_statement.glsl` | Uses a `switch` statement — `switch` is not in the v1 subset. |
| `macro_paste.glsl` | Uses the token-paste operator `##` inside a `#define` body — `##`/`#` (stringize) are not implemented and are a loud reject rather than a mis-expansion. |
| `unknown_intrinsic.glsl` | Calls `texelFetch`, which has no entry in the intrinsic mapping table — unmapped intrinsics are a loud reject. |
| `unknown_global.glsl` | Uses a free identifier (`RENDERSIZE`, an ISF builtin) that is not a ShaderToy uniform/local/const/user-function — undeclared identifiers are a loud reject (L1), not a silent pass-through. |
| `custom_uniform_sampler3d.glsl` | A custom `uniform sampler3D` — only `sampler2D` is a supported uniform type; `sampler3D`/`samplerCube` are a loud reject. |
| `custom_uniform_bad_type.glsl` | A custom `uniform mat2x3` (non-square) — a custom uniform must be a supported scalar/vector/matrix (mat2/3/4) or `sampler2D`; everything else is a loud reject. |
| `global_unsupported_type.glsl` | **G1 boundary**: a top-level mutable global of an unsupported type (`double gBad;`) — supported-type mutable globals are now accepted (as `static`), but an unsupported-type one stays a loud reject. |
| `pp_include.glsl` | **G5 boundary**: `#include "common.glsl"` — there is no file resolver, so `#include` stays a loud reject (it cannot be silently dropped without losing code). |
| `main_no_output.glsl` | **G2 boundary**: a plain-GLSL `void main()` with no discoverable fragment output (no `out vec4 <name>;` and no `gl_FragColor` write) — there is nothing to return as COLOR0, so it is a loud reject. |

Total: 12 reject shaders.

Note: a shader that defines **BOTH** a ShaderToy `mainImage` AND a standalone `void main()` wrapper is
**no longer a reject** (the former `both_entry_points.glsl`, now retired). It PREFERS ShaderToy mode,
drops the `void main()` wrapper with a Warning, and converts — see
`authored/mainimage_with_main_wrapper.glsl`. This is the common third-party shape (a desktop-runner shim
around `mainImage`); ~a third of failures in the real-shader corpus hit it.

Note: function-like `#define NAME(...)` macros and the `#if`/`#ifdef`/`#ifndef`/`#elif`/`#else`/`#endif`
conditional family are now **supported** (Phase 46 preprocessor) and have moved to `authored/` (see
`macro_function.glsl`, `pp_*.glsl`). A custom `uniform` of a SUPPORTED type is now **accepted** and
emitted as an effect parameter, **including one with a default value** (`uniform float x = 1.0;`, G4);
a top-level non-`const` mutable global of a SUPPORTED type is **accepted** as a `static` global (G1);
`#version`/`#extension`/`#pragma`/`#line` plus glslViewer/Bonzomatic `#i*` channel-metadata directives
are now silently **ignored** (G5); a flat user **`struct`** is **accepted** (G6, see `struct_basic.glsl`),
and a fixed-size **array** is **accepted** (G7, see `const_array.glsl`/`local_array.glsl`). The former
`user_struct.glsl` / `user_array.glsl` rejects were therefore retired (now in-subset), replaced by
`nested_struct.glsl` / `unsized_array.glsl` for the boundary cases that genuinely stay rejected. What
stays a reject here: an *unsupported* uniform/global type, a custom `varying`/`in`/`out`, a sampler with
an initializer, the unimplemented `##`/`#` operators, `#include`, a nested/inline struct, an unsized
array, and an unmappable intrinsic (`roundEven`).
