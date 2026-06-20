# shadertoy2fx — GLSL → HLSL `.fx` Mapping (Phase 46)

This document describes exactly what the `ShadowDusk.ShaderToy` converter implements: the supported
ShaderToy/GLSL subset, the type / intrinsic / operator mapping tables, and the explicit reject-list.
It is derived from the actual implementation (`src/ShadowDusk.ShaderToy/`), not an aspiration.

The converter is **out-of-band**: it references nothing in the ShadowDusk compiler pipeline. Its only
job is to emit a self-contained legacy-FX9 `.fx` text that the existing pipeline then compiles to
OpenGL / DirectX / FNA. Anything outside the subset below is a **loud `Error` diagnostic** (with the
original GLSL line/column and the offending construct) — never silently-wrong output.

---

## Entry point

- Required: `void mainImage(out vec4 fragColor, in vec2 fragCoord)`. Missing → reject.
- The signature is validated: first param must be `out vec4`, second `vec2`. Wrong return type,
  wrong arity, or wrong param types/qualifiers → reject.
- Multiple `mainImage` definitions → reject.

## ShaderToy uniforms (declared in the `.fx` only when referenced)

| GLSL uniform | HLSL global emitted |
|---|---|
| `iResolution` (vec3) | `float3 iResolution;` (always emitted — the harness PS needs it for fragCoord) |
| `iTime` (float) | `float iTime;` |
| `iTimeDelta` (float) | `float iTimeDelta;` |
| `iFrame` (int) | `int iFrame;` |
| `iFrameRate` (float) | `float iFrameRate;` |
| `iMouse` (vec4) | `float4 iMouse;` |
| `iDate` (vec4) | `float4 iDate;` |
| `iSampleRate` (float) | `float iSampleRate;` |
| `iChannelTime[4]` (float) | `float iChannelTime[4];` |
| `iChannelResolution[4]` (vec3) | `float3 iChannelResolution[4];` |
| `iChannel0..3` (sampler2D) | `texture iChannelNTexture;` + `sampler2D iChannelN = sampler_state { Texture = <iChannelNTexture>; };` |

`texture(iChannelN, uv)` → `tex2D(iChannelN, uv)`.

**Deprecated aliases (auto-mapped).** The original ShaderToy spellings are rewritten to the canonical
names at the token level before parsing, so they resolve cleanly: `iGlobalTime` → `iTime`,
`iGlobalFrame` → `iFrame`.

**Redundant built-in re-declaration (dropped).** A top-level declaration that merely re-declares a
known ShaderToy built-in uniform (e.g. `uniform float iTime;`, `uniform vec2 iResolution;`) is
silently dropped, not rejected: the harness already injects that global, so the declaration is
harmless.

## Custom uniforms (consumer-driven effect parameters)

A top-level `uniform <type> <name>;` of a **non**-built-in name is now ACCEPTED and emitted as its own
HLSL global, i.e. an **effect parameter the consumer drives** each frame. This broadens the tool from
pure-ShaderToy toward general single-pass GLSL image shaders (glslViewer / KodeLife / ShaderToy-derived).
Every accepted custom uniform/sampler is reported in `ConvertResult.UsedUniforms` (the drivable-parameter
list).

| GLSL custom uniform | HLSL emitted |
|---|---|
| `uniform float/int/bool <name>;` | `float/int/bool <name>;` |
| `uniform vecN/ivecN/bvecN <name>;` | `floatN/intN/boolN <name>;` |
| `uniform matN <name>;` (mat2/3/4) | `floatNxN <name>;` |
| `uniform sampler2D <name>;` | `texture <name>Texture;` + `sampler2D <name> = sampler_state { Texture = <<name>Texture>; };` |

`texture(<name>, uv)` on a custom `sampler2D` → `tex2D(<name>, uv)`, exactly as for `iChannelN`.

**Restrictions (still loud rejects).** A custom uniform whose type is **not** in the supported set
(`struct`, array, `sampler3D`/`samplerCube`, `double`/`uint`/`uvec`/`dvec`, non-square / explicit
`matAxB`, or any unknown type) is a loud, located reject. A `uniform` with an **initializer**
(illegal GLSL — its value is host-supplied) is a loud reject. A `varying`/`attribute`/`in`/`out`
declaration of a custom name is **still** a loud reject (only `uniform` is host-drivable). The L1 rule
is unchanged: a **bare, never-declared** identifier (e.g. ISF's `RENDERSIZE`) is still a loud reject —
declare it as a top-level `uniform` to expose it.

**Common alias nicety.** A declared `uniform float u_time;` (the glslViewer alias) whose type matches
the ShaderToy built-in **exactly** is folded onto `iTime`, so its references Just Work and no separate
parameter is exposed. Only the zero-risk exact-type alias is mapped; type-mismatched glslViewer aliases
(`vec2 u_resolution` vs `vec3 iResolution`, `vec2 u_mouse` vs `vec4 iMouse`) are **not** aliased — they
are exposed verbatim as custom uniforms instead, so the consumer drives them directly.

## Harness synthesized into the `.fx`

- A fullscreen-quad **vertex shader** `VSMain` taking `float4 Position : POSITION` (assumed already in
  NDC), passing it through and deriving a `[0,1]` uv (`uv = (pos.x*0.5+0.5, 0.5-pos.y*0.5)`).
- A **pixel shader** `PSMain : COLOR0` that computes `fragCoord` and calls `mainImage`.
- `technique <TechniqueName> { pass P0 { VertexShader = compile vs_3_0 VSMain(); PixelShader = compile ps_3_0 PSMain(); } }`.

### fragCoord Y orientation (the origin trap)

ShaderToy `fragCoord` has a **bottom-left** origin (y grows upward). The synthesized uv has a
**top-left** origin (uv.y = 0 at the top of the screen, the D3D convention). The PS therefore flips Y
back: `fragCoord = float2(uv.x, 1.0 - uv.y) * iResolution.xy`, so the rendered image matches the
ShaderToy reference orientation. (Documented inline in `HarnessGenerator.EmitPixelShader`.)

---

## Type mapping (trap 1)

| GLSL | HLSL | | GLSL | HLSL |
|---|---|---|---|---|
| `void` | `void` | | `ivec2/3/4` | `int2/3/4` |
| `bool` | `bool` | | `bvec2/3/4` | `bool2/3/4` |
| `int` | `int` | | `mat2` | `float2x2` |
| `float` | `float` | | `mat3` | `float3x3` |
| `vec2/3/4` | `float2/3/4` | | `mat4` | `float4x4` |

**Vector splat (GLSL-only, expanded):** GLSL `vecN(scalar)` splats the scalar to all N components.
HLSL has no single-scalar vector constructor, so `vecN(s)` → `((floatN)(s))`. A single **vector**
argument `vecN(vM)` likewise emits a truncating cast `((floatN)(vM))` (matches GLSL truncation).

## Operator mapping

Operators pass through unchanged **except** `*` when an operand is a matrix (trap 2, below):
`+ - * / %`, `== != < > <= >=`, `&& ||`, `& | ^ << >>`, ternary `?:`, assignment `= += -= *= /= %=`,
unary `- ! + ++ --` (prefix and postfix). Float literals without a decimal point are normalized to
`x.0` so HLSL types them as float.

Four correctness rules layer on top of that pass-through:

- **Matrix compound assignment.** A `*=` whose right-hand side is a matrix is desugared the same way
  as a binary `*` (trap 2): GLSL `v *= M` (`M` a `matN`) means `v = M*v`, which under the
  `A*B → mul(B,A)` rule emits `v = mul(v, M)`. A plain `v *= M` would be invalid HLSL
  (`float2 *= float2x2`). Scalar/vector `*=` (and every other compound op) stays component-wise.
- **No double-parenthesized conditions.** A relational/equality expression used directly as an
  `if`/`while`/`do…while`/ternary condition is NOT wrapped in its own extra parentheses (the
  condition site already supplies them), so `if (a == 0.0)` is emitted rather than `if ((a == 0.0))`
  (the latter trips fxc's `-Werror,-Wparentheses-equality`).
- **Vector equality scalarized.** A vector `==`/`!=` used in a boolean context (an `if`/`while`/
  ternary condition, or under `&&`/`||`/`!`) is reduced with `all(a == b)` / `any(a != b)`, since
  HLSL `==` on vectors yields a bool-vector that is not a valid scalar condition.
- **Explicit vector truncation.** When an initializer/assignment narrows a wider vector into a
  narrower slot (e.g. a `vec4` into a `vec2`), an explicit truncating swizzle (`.xy`/`.xyz`) is
  inserted, because GLSL truncates implicitly but stricter HLSL errors (`-Werror,-Wconversion`).

### Matrix multiply order (trap 2 — the highest-risk trap)

GLSL is column-major and evaluates `M * v` as matrix·column-vector. Two facts combine:

1. Feeding the **same scalar list** to an HLSL `floatNxN(...)` constructor that a GLSL `matN(...)`
   constructor received yields the **transpose** Mᵀ (GLSL fills column-major, HLSL row-major).
2. HLSL `mul(rowVector, M)` computes the row-major product.

So the converter emits matrix constructors with the identical scalar list (producing Mᵀ) and
translates GLSL `A * B` → HLSL `mul(B, A)`. The two transposes cancel: `mul(v, Mᵀ) == M·v`. A scalar
operand of `*` is **not** a matrix multiply — it stays `*` (componentwise scale).

**Proof (mat2 rotation):** GLSL `mat2(c,-s, s,c) * v` (rotate by +θ) → emitted
`mul(v, float2x2(c,-s, s,c))` = `(c*vx + s*vy, -s*vx + c*vy)`, the same +θ rotation. Verified against
the column-major GLSL result by hand and exercised by a regenerated smoke shader that compiles on GL
and DX.

### `mod` sign (trap 3)

GLSL `mod(x,y) = x - y*floor(x/y)` (sign follows `y`); HLSL `fmod` truncates toward zero, so they
differ for negative operands. `mod(x,y)` is emitted as a call to a generated `glsl_mod` helper
(`x - y*floor(x/y)`), overloaded for `floatN` and `floatN`/`float` operand shapes. The helper block is
emitted only when `mod` was used.

## Intrinsic mapping (trap 4 — explicit table; anything else calling-but-unmapped is rejected)

**Renamed:** `mix`→`lerp`, `fract`→`frac`, `inversesqrt`→`rsqrt`, `dFdx`→`ddx`, `dFdy`→`ddy`,
`texture`/`texture2D`→`tex2D`, `textureLod`→`tex2Dlod` (uv packed into `float4(uv,0,lod)`),
`textureGrad`→`tex2Dgrad`.

**Special-cased:** `atan(y,x)`→`atan2(y,x)`, `atan(x)`→`atan(x)`; `mod`→`glsl_mod` (see trap 3).

**Same name (carried over):** `clamp, min, max, abs, floor, ceil, round, trunc, sign, sqrt, exp, log,
exp2, log2, pow, sin, cos, tan, asin, acos, sinh, cosh, tanh, step, smoothstep, length, distance, dot,
cross, normalize, reflect, refract, radians, degrees, saturate`.

## Swizzles

`.xyzw`, `.rgba` pass through; `.stpq` is normalized to `.xyzw` (HLSL has no `stpq` set).

## Precision qualifiers (trap 5)

`highp` / `mediump` / `lowp` tokens and bare `precision …;` statements are stripped. A stray storage
or precision modifier that appears **after** the type in a copied/generated declaration
(e.g. `float const k`, `vec2 mediump uv`) is also dropped, so the emitted HLSL is a clean
`type name` — never `type modifier name`, which the stricter HLSL compilers (fxc / FNA) reject as
"modifiers must appear before type".

## Preprocessor (C-style, runs before lex/parse)

A real line-oriented C preprocessor pass handles:

- **Conditional compilation:** `#if` / `#ifdef` / `#ifndef` / `#elif` / `#else` / `#endif`, correctly
  nested. The `#if`/`#elif` expression is a C **integer constant expression**: integer literals
  (decimal and `0x…` hex, with `u`/`l` suffixes tolerated), the operators
  `! ~ * / % + - << >> < <= > >= == != & ^ | && ||`, ternary `?:`, parentheses, and
  `defined(NAME)` / `defined NAME`. Macros are expanded inside the expression first; an identifier
  that is **not** a defined macro evaluates to `0` (the standard C rule). Inactive branches are
  dropped but each source line is preserved as a blank line, so downstream diagnostics keep pointing
  at the original line.
- **Object-like macros:** `#define NAME value` — whole-word token substitution (bounded multi-pass so
  a define-of-a-define resolves).
- **Function-like macros:** `#define F(a, b) body` — argument substitution at each call site (no-arg
  `F()` and multi-token / nested-comma arguments handled; a multi-token argument is hygienically
  wrapped in parentheses to preserve call-site precedence). A function-like macro name **not** followed
  by `(` is left as a plain identifier (C rule).
- **`#undef`** is honored in source order (a use before the `#undef` sees the old value, after sees the
  redefinition).
- **Comments on directive lines** (`#define X 0 // note`, `#if A /* x */`) are stripped before the
  body/expression is taken, so they never leak into a macro body or an `#if` expression.
- **Line continuations** (`\` at end of a physical line) are folded into one logical directive.
- `#version` / `#extension` / `#pragma` are ignored.

**Rejected (loud, located):** the token-paste `##` and stringize `#` operators inside a macro body
(rare in shaders; rejected rather than mis-expanded), variadic macros (`...`), and `#include` (inline
the included source instead). A recursion/expansion-depth guard turns a pathological self-referential
macro into a loud reject rather than an infinite loop.

## Control flow & statements supported

block `{}`, local var decl + init (incl. comma lists `float a=…, b=…;` kept as siblings, not a new
scope), expression statement, compound assignment, `if/else`, `for`, `while`, `do…while`, `return`,
`break`, `continue`, `discard`. User-defined functions with `in`/`out`/`inout` params and `const`
globals are supported (a `const` global emits as `static const`). Function prototypes are accepted and
the later definition is emitted.

---

## Reject-list (loud `Error` + line/column + construct)

Each of the following produces a fatal diagnostic, never silently-wrong HLSL:

- **Entry points / multipass:** missing or duplicate `mainImage`; `mainSound`, `mainVR`,
  `mainCubemap` (Buffer A–D multipass is implied out of scope — only a single `mainImage` is emitted).
- **Types:** `double`, `dvecN`, `uint`, `uvecN`, explicit `matAxB` spellings (use `mat2/3/4`),
  non-square matrices, `sampler3D` / `samplerCube`, and any unknown type name.
- **Declarations:** user `struct` (top-level or local); user-declared arrays (locals, params, globals);
  top-level non-`const` globals; a custom `uniform` of an **unsupported type** (struct/array/
  `sampler3D`/`samplerCube`/`uint`/`double`/non-square matrix/unknown) or **with an initializer**; a
  top-level `varying`/`attribute`/`in`/`out` declaration of a **custom** name (only `uniform` is
  host-drivable). A redundant re-declaration of a known ShaderToy built-in is dropped, and a custom
  `uniform` of a SUPPORTED type is now **accepted** as an effect parameter — see *Custom uniforms* above.
- **Undeclared identifiers:** a free identifier used in an expression that is not a local/parameter, a
  `const` global, a user function, a declared custom uniform, or a predefined ShaderToy uniform is
  rejected at convert time (with line/column) rather than leaked to a downstream "use of undeclared
  identifier" compile error. This covers non-ShaderToy builtins (e.g. ISF's `RENDERSIZE`) that were
  never declared. The deprecated `iGlobalTime`/`iGlobalFrame` aliases are auto-mapped before this
  check, so they are accepted.
- **Preprocessor:** the token-paste `##` / stringize `#` operators in a macro body, variadic macros
  (`...`), `#include`, and any other non-ignored directive. (Conditional compilation and function-like
  macros are now **supported** — see *Preprocessor* above — and are no longer rejected.)
- **Statements/expressions:** `switch`; unknown function/intrinsic that is neither a user function nor
  in the mapping table; single-argument matrix constructor `matN(x)` (HLSL has no diagonal
  `floatNxN(scalar)` form); `texelFetch`, `textureProj`, `textureSize`, `fwidth`, fine/coarse
  derivatives, and bit-packing/bitfield intrinsics.

## Notes / known limits

- **FNA / SM3 ceiling.** The emitted `.fx` is `vs_3_0`/`ps_3_0`. Complex ShaderToy shaders that
  compile fine on GL/DX may legitimately exceed fx_2_0 / SM3 instruction or loop limits on FNA — an
  inherent fx_2_0 limit the pipeline already surfaces loudly, not a converter bug.
- **Oracle.** The reference for correctness is ShaderToy's own WebGL output, not `mgfxc`/`fxc`. GL is
  the closest match; DX/FNA may differ a hair due to D3D conventions.
- The converter only emits `.fx` text. A runtime helper (`ShaderToyEffect`) that drives the uniforms
  each frame and draws a fullscreen quad is a separate deliverable; it exposes `SetCustom(name, value)`
  so the consumer drives the custom uniforms reported in `UsedUniforms`.
