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
| `switch_fallthrough.glsl` | A `switch` with true **fall-through** (a non-empty `case` body with no terminating `break`/`return`) — a plain break-terminated `switch` is now supported (lowered to if/else), but fall-through cannot be lowered without changing control flow. |
| `stage_in_noncoord_referenced.glsl` | A top-level `in`/`varying` of a **non-coordinate** name is ignored (vertex-stage leftover), but here it is **referenced** — we have no per-vertex value for it, so the reference is a loud undeclared-identifier reject (a conventional coordinate-varying name like `vUv`/`uv` WOULD resolve to the harness screen UV instead). |
| `macro_paste.glsl` | Uses the token-paste operator `##` inside a `#define` body — `##`/`#` (stringize) are not implemented and are a loud reject rather than a mis-expansion. |
| `unknown_intrinsic.glsl` | Calls `texelFetch`, which has no entry in the intrinsic mapping table — unmapped intrinsics are a loud reject. |
| `unknown_global.glsl` | Uses a free identifier (`RENDERSIZE`, an ISF builtin) that is not a ShaderToy uniform/local/const/user-function — undeclared identifiers are a loud reject (L1), not a silent pass-through. |
| `custom_uniform_sampler3d.glsl` | A custom `uniform sampler3D` — only `sampler2D` is a supported uniform type; `sampler3D`/`samplerCube` are a loud reject. |
| `custom_uniform_bad_type.glsl` | A custom `uniform mat2x3` (non-square) — a custom uniform must be a supported scalar/vector/matrix (mat2/3/4) or `sampler2D`; everything else is a loud reject. |
| `global_unsupported_type.glsl` | **G1 boundary**: a top-level mutable global of an unsupported type (`double gBad;`) — supported-type mutable globals are now accepted (as `static`), but an unsupported-type one stays a loud reject. |
| `pp_include.glsl` | **G5 boundary**: `#include "common.glsl"` — there is no file resolver, so `#include` stays a loud reject (it cannot be silently dropped without losing code). |
| `main_no_output.glsl` | **G2 boundary**: a plain-GLSL `void main()` with no discoverable fragment output (no `out vec4 <name>;` and no `gl_FragColor` write) — there is nothing to return as COLOR0, so it is a loud reject. |
| `array_nonconst_size.glsl` | **G7 boundary**: an array sized by a non-constant expression (`float a[n];` where `n` is a variable) — a fixed-size array (`float k[3];`) is supported, but a non-constant / macro size has no compile-time length, so it stays a loud reject. |
| `sampler_param.glsl` | **Final wave boundary**: a `sampler2D` FUNCTION PARAMETER (`vec4 f(sampler2D tex, ...)`) — valid HLSL but uncompilable through the legacy-FX9 → GL/DX pipeline (a sampler cannot be a function argument there, the same class as the mip-bias reject), so it is a loud, named reject (inline the `tex2D` on the global sampler instead). |
| `intrinsic_texturecube.glsl` | **Final wave**: `textureCube` samples a CUBEMAP — no faithful 2D `sampler2D` map. Named reject. |
| `unsigned_int_literal.glsl` | An **unsigned-integer literal** (`374761393U`) — drives uint/uvec bit arithmetic (an integer hash) with no faithful float mapping. Rejected AT the literal, not as a stray-`U` parse error. |
| `texture_cubemap_coord.glsl` | `texture(iChannel0, vec3)` samples a **CUBEMAP** (a 3D direction lookup). The generic `texture` form with a 3D coord is named-rejected (it would otherwise truncate the coord to 2D and emit an opaque HLSL `-Wconversion` error). |
| `feedback_lastframe.glsl` | **Final wave**: `getLastFrameColor` reads the shader's own previous-frame output (feedback / multipass) — a single image pass cannot supply it. Named reject. |
| `gl_fragdepth_builtin.glsl` | **Final wave**: `gl_FragDepth` (per-fragment depth output) has no meaning for a 2D fullscreen pass — a known GL stage built-in, named-rejected rather than a generic undeclared-identifier message. |
| `host_specific_uniform.glsl` | **Final wave**: a host-specific global (`iCurrentCursor`, like a terminal-cursor uniform) the converter cannot invent a value for — stays a loud "undeclared identifier ... depends on a host-provided value" reject (we do NOT auto-expose arbitrary unknowns). |
| `host_template_placeholder.glsl` | **Final wave**: a host-template `$placeholder` token (`#define speed $speed`) the converter cannot resolve to a host-substituted value — a loud, named reject (NOT a runaway macro expansion; the C blue-paint rule is honored first). |

Total: 21 reject shaders.

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

Note (Phase 46 second batch): a top-level `in`/`varying`/`attribute` declaration is now **IGNORED**
(web/desktop-export vertex-stage leftover), not rejected; a conventional coordinate-varying name
(`vUv`/`texCoord`/`uv`/…) referenced as the UV resolves to the harness normalized screen UV (see
`authored/stage_in_varying_ignored.glsl`). The OpenFL `#pragma header` + `openfl_*` globals
(`authored/openfl_header.glsl`), the GdShaders/Godot 4-arg `mainImage`
(`authored/godot_4arg_mainimage.glsl`), the libretro VERTEX/FRAGMENT stage split
(`authored/libretro_vertex_fragment.glsl`), and a break-terminated `switch`
(`authored/switch_statement.glsl`) are all now **accepted**. The former `switch_statement.glsl` reject
was retired (now in-subset), replaced by `switch_fallthrough.glsl` (fall-through) and
`stage_in_noncoord_referenced.glsl` (a non-coordinate ignored varying that is referenced) for the
boundary cases that genuinely stay rejects.
