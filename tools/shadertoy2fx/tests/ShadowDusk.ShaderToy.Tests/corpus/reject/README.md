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

Total: 7 reject shaders.

Note: function-like `#define NAME(...)` macros and the `#if`/`#ifdef`/`#ifndef`/`#elif`/`#else`/`#endif`
conditional family are now **supported** (Phase 46 preprocessor) and have moved to `authored/` (see
`macro_function.glsl`, `pp_*.glsl`). Only the unimplemented `##`/`#` operators and `#include` remain
loud rejects.
