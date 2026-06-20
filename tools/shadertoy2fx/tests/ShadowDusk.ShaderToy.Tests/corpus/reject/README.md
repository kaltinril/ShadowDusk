# Reject corpus — ShaderToy → FX (Phase 46)

Shaders that **must be rejected** with a clear, located diagnostic and a non-zero exit (project
constraint 5: fail loudly, never emit silently-wrong HLSL). Each shader is otherwise valid v1
ShaderToy GLSL — the listed construct is the **only** out-of-scope thing in it, so the harness can
assert the tool rejects for that specific reason.

| File | Expected rejection reason |
|---|---|
| `user_struct.glsl` | Declares a user `struct` (`struct Ray { ... }`) — user structs are not in the v1 subset. |
| `user_array.glsl` | Declares a user array (`float weights[3];`) — arrays are not in the v1 subset. |
| `second_entry_cubemap.glsl` | Contains a second entry point (`mainCubemap`) — v1 supports a single `mainImage` image shader only. |
| `switch_statement.glsl` | Uses a `switch` statement — `switch` is not in the v1 subset. |
| `macro_paste.glsl` | Uses the token-paste operator `##` inside a `#define` body — `##`/`#` (stringize) are not implemented and are a loud reject rather than a mis-expansion. |
| `unknown_intrinsic.glsl` | Calls `texelFetch`, which has no entry in the intrinsic mapping table — unmapped intrinsics are a loud reject. |
| `unknown_global.glsl` | Uses a free identifier (`RENDERSIZE`, an ISF builtin) that is not a ShaderToy uniform/local/const/user-function — undeclared identifiers are a loud reject (L1), not a silent pass-through. |
| `custom_uniform_sampler3d.glsl` | A custom `uniform sampler3D` — only `sampler2D` is a supported uniform type; `sampler3D`/`samplerCube` are a loud reject. |
| `custom_uniform_bad_type.glsl` | A custom `uniform mat2x3` (non-square) — a custom uniform must be a supported scalar/vector/matrix (mat2/3/4) or `sampler2D`; everything else is a loud reject. |
| `global_unsupported_type.glsl` | **G1 boundary**: a top-level mutable global of an unsupported type (`double gBad;`) — supported-type mutable globals are now accepted (as `static`), but an unsupported-type one stays a loud reject. |
| `pp_include.glsl` | **G5 boundary**: `#include "common.glsl"` — there is no file resolver, so `#include` stays a loud reject (it cannot be silently dropped without losing code). |

Total: 11 reject shaders.

Note: function-like `#define NAME(...)` macros and the `#if`/`#ifdef`/`#ifndef`/`#elif`/`#else`/`#endif`
conditional family are now **supported** (Phase 46 preprocessor) and have moved to `authored/` (see
`macro_function.glsl`, `pp_*.glsl`). A custom `uniform` of a SUPPORTED type is now **accepted** and
emitted as an effect parameter, **including one with a default value** (`uniform float x = 1.0;`, G4);
a top-level non-`const` mutable global of a SUPPORTED type is **accepted** as a `static` global (G1);
and `#version`/`#extension`/`#pragma`/`#line` plus glslViewer/Bonzomatic `#i*` channel-metadata
directives are now silently **ignored** (G5). What stays a reject here: an *unsupported* uniform/global
type, a custom `varying`/`in`/`out`, a sampler with an initializer, the unimplemented `##`/`#`
operators, and `#include`.
