# shadertoy2fx — Coverage Stress Test (Phase 46)

A statistical coverage report for the `shadertoy2fx` GLSL → HLSL `.fx` converter, produced by
running a batch of **real, third-party single-pass ShaderToy / GLSL image shaders** through the
converter and then through the ShadowDusk OpenGL compiler.

> **Update 2026-06-19 (f) — both-entries (`mainImage` + standalone `void main()`) now CONVERTS.** The
> single biggest real-world coverage bug is fixed: a shader that defines BOTH a ShaderToy
> `void mainImage(out vec4, in vec2)` AND a standalone `void main(){ mainImage(gl_FragColor,
> gl_FragCoord.xy); }` wrapper (the glslViewer / Bonzomatic / desktop-runner shape, ~a third of all
> real-corpus failures) is **no longer the "ambiguous entry point" reject**. The converter now PREFERS
> ShaderToy mode (`mainImage` is canonical; our harness generates its own fullscreen VS/PS), **drops the
> `void main()` wrapper** (not translated/emitted, so no dangling `gl_FragColor`/`gl_FragCoord`), and
> emits a **Warning** that the wrapper was ignored in favor of `mainImage`. The dropped wrapper does not
> change the `mainImage` output (byte-identical to the wrapper-less shader). A `main()` doing
> substantive work beyond calling `mainImage` is still dropped (never merged). Single-entry shaders and
> the plain-GLSL `main`-only mode are UNCHANGED; "no entry point" stays a reject. **Scratch re-measure:
> conversion 44.4 % (71/160), up from 33.8 % (54/160) — a +17-shader gain** confirming the both-entries
> shape was a top failure cause. Unit suite **259 green (0 warn)**; golden compile-sweep **52/52 on
> OpenGL / DirectX_11 / FNA** (1 new both-entries golden, no regressions); render-proof **4/4 + multipass
> (exit 0)**. The former `reject/both_entry_points.glsl` was retired; the new
> `authored/mainimage_with_main_wrapper.glsl` covers the converting case.
>
> **Update 2026-06-19 (e) — G2 (plain-GLSL `void main()` entry mode) landed.** The converter now
> accepts a SECOND entry convention: a plain-GLSL `void main()` fragment shader (glslViewer /
> Bonzomatic / Shadertoy-export style) in addition to the ShaderToy `void mainImage(...)`. The
> convention is auto-detected by entry name (no flag): `mainImage`-only → ShaderToy mode, `main`-only →
> plain-GLSL mode, **both → ambiguous loud reject**, neither → no-entry reject. In `main()` mode the
> fragment output is the legacy `gl_FragColor` OR a single top-level user-declared `out vec4 <name>;`
> (incl. `layout(location=N) out vec4`), consumed (NOT emitted as a parameter/global) and returned as
> COLOR0; `gl_FragCoord` maps to the SAME bottom-left-Y pixel coord the ShaderToy harness uses
> (render-proven by the new `main_gradient` case, which asserts the same orientation as `gradient_uv`).
> A `main()` with no discoverable output is a loud reject; everything else (preprocessor, structs,
> arrays, traps, custom uniforms) works identically in both modes. **Scratch re-measure: unchanged at
> conversion 34.6 % / compile-of-converted 88.7 % / end-to-end 30.7 %** over the same 153-`mainImage`
> in-scope set — this corpus was sampled by a `mainImage` GitHub search and contains **0 pure
> plain-GLSL `main()` shaders**, so G2 adds no new conversions HERE (the 6 `main`-only files in the 160
> all genuinely reject: 3 define BOTH a `mainImage` and a `main` = ambiguous, 3 hit unsupported
> constructs). The value of G2 is broadened *reach* to the glslViewer/Bonzomatic class of shaders, with
> correctness held first (never silent-wrong). Unit suite **234 green (0 warn)**; golden compile-sweep
> **47/47 on OpenGL / DirectX_11 / FNA** (3 new main-mode goldens, no regressions); render-proof
> **4/4 (exit 0)** with the new main-mode orientation case.
>
> **Update 2026-06-19 (d) — G6 (structs) + G7 (arrays / intrinsics / parser tail) landed.** Two more
> backlog gaps closed. **G6:** a top-level user `struct` of supported member types is accepted and
> emitted as an HLSL `struct` plus a generated `make_Name(...)` factory (GLSL's `Name(...)` constructor
> is rewritten to call it); struct-typed locals/params/returns and member access `s.field` work, and a
> **matrix-typed member still hits the matrix-order trap** (`s.rot * v` -> `mul(v, s.rot)`); nested /
> inline-struct members and combined `struct{..}var;` forms stay loud rejects. **G7:** fixed-size arrays
> at const/mutable global and local scope (`const float k[3] = float[](...)` -> `static const float k[3]
> = { ... }`, `float arr[4];`, indexing), the added intrinsics `fwidth` (same-name) and `matrixCompMult`
> (componentwise `(a*b)`, NOT the mul-reordered product), and parser hardening for the GLSL comma
> (sequence) operator in `for` headers; unsized/runtime arrays, size/element mismatches, `roundEven`
> (no faithful HLSL map), and the mip-bias `texture(s,uv,bias)` form (its `tex2Dbias` does not compile
> on GL/DX) stay loud, located rejects. Re-measured over the same 160-shader gitignored scratch corpus
> (153 with a `void mainImage`): **conversion 34.6 %** (53/153, was 34.0 %), **compile-of-converted
> 88.7 %** (47/53, was 86.5 %), **end-to-end 30.7 %** (47/153, was 29.4 %). The compile-of-converted
> rate IMPROVED because the `texture(s,uv,bias)` reject removes a converted-but-fails case; the remaining
> 6 converted-but-not-compiling shaders are pre-existing §5 edge cases (B4 truncation, B5 modifier
> spacing, a `float(...)` shadow, a legacy `tex2Dlod` case), none a new silent-wrong path. Unit suite
> 214 green (0 warn); golden compile-sweep **44/44 on OpenGL / DirectX_11 / FNA** (the struct and array
> goldens compile on FNA fx_2_0 too — no SM3-limit case in this corpus); render-proof 3/3 (exit 0).
>
> **Update 2026-06-19 (c) — G1/G3/G4/G5 gap-closures landed.** Four backlog gaps closed together:
> **G1** top-level non-`const` mutable globals (emitted as HLSL `static` globals; unsupported-type
> globals still reject), **G3** more exact-type host aliases (`time`/`fGlobalTime`→`iTime`,
> `u_frame`/`iGlobalFrame`→`iFrame`; a type-mismatched alias becomes a custom uniform, not a wrong
> alias; genuine undeclared idents still reject), **G4** a custom `uniform` with a default initializer
> (`uniform float x = 1.0;`, valid GLSL 1.20+) is accepted and the default is preserved, and **G5**
> harmless preprocessor directives (`#version`/`#extension`/`#pragma`/`#line` + glslViewer/Bonzomatic
> `#iChannel0 "..."`/`#iKeyboard`/… metadata) are silently ignored (`#include` still a loud reject).
> Re-measured over the same 160-shader gitignored scratch corpus (153 with a `void mainImage`):
> **conversion rate 34.0 %** (52/153, was 26.0 %), **compile-of-converted 86.5 %** (45/52),
> **end-to-end 29.4 %** (45/153, was 22.1 %). The 7 converted-but-not-compiling shaders are all
> pre-existing §5 edge cases (B4 implicit-truncation, B5 modifier-spacing, a `float(...)` shadowing,
> a legacy `tex2Dlod`/texture-offset case) that the new shaders only now *reach* — none is a new
> silent-wrong path; each fails loudly at compile (non-zero exit). Unit suite 161→185 green (0 warn);
> golden compile-sweep 38/38 on OpenGL/DirectX_11/FNA; render-proof 3/3 (exit 0).
>
> **Update 2026-06-19 (b) — custom top-level uniforms landed.** A top-level `uniform <type> <name>;`
> of a non-built-in name is now ACCEPTED and emitted as an HLSL effect parameter the consumer drives
> (scalar/vector/matrix, plus `sampler2D` as the iChannelN-style texture+sampler pair). Unsupported
> uniform types (`sampler3D`/`samplerCube`/struct/array/`uint`/non-square matrix/unknown), a `uniform`
> with an initializer, and `varying`/`attribute`/`in`/`out` of a custom name stay loud rejects; bare
> never-declared identifiers (L1) still reject. The exact-type glslViewer alias `u_time` is folded onto
> `iTime`. Re-measured over the same scratch corpus (154 in-scope):
> **conversion rate 26.0 %** (40/154, was 23.4 %), **compile rate 85.0 %** (34/40, was 86.1 %),
> **end-to-end 22.1 %** (34/154, was 20.1 %). The compile-rate dip is not a regression in the new path:
> the 6 converted-but-not-compiling shaders hit pre-existing transpiler edge cases already catalogued
> in §5 (B4 implicit-truncation, B5 modifier-spacing) that they only now *reach* because the custom
> uniform that previously blocked them is accepted; none are caused by custom-uniform emission.
>
> **Update 2026-06-19 — C preprocessor support landed.** The converter now evaluates the full
> conditional-compilation family (`#if`/`#ifdef`/`#ifndef`/`#elif`/`#else`/`#endif`, correctly
> nested, with a C integer const-expression evaluator that understands `defined()` and macro
> expansion) and expands **function-like macros** `#define F(a,b) …` with argument substitution
> (plus `#undef`, and comment-stripping on directive lines). `##`/`#` (paste/stringize) and
> `#include` remain loud rejects. Re-measured over the same scratch corpus:
> **conversion rate 23.4 %** (36/154, was 20.1 %), **compile rate 86.1 %** (31/36, was 67.7 %),
> **end-to-end 20.1 %** (31/154, was 13.6 %). The §2/§3 tables below are the original baseline
> snapshot kept for the construct ranking; the headline numbers in this note supersede them.

**Licensing note:** the third-party shaders were fetched only transiently into the gitignored
`.scratch/` directory and are **not** committed. This report contains only aggregate statistics,
categorized failure causes, and short illustrative snippets **authored here by hand** to show the
shape of a gap. No third-party shader source is reproduced.

---

## 1. Method

- **Source corpus.** 160 `mainImage`-style GLSL/`.frag` files were sampled from **150 distinct
  public GitHub repositories** (found via GitHub code search for `mainImage extension:glsl|frag|fs`,
  capped at ~4 files per repo to maximize diversity). This is a broad real-world cross-section, not a
  curated "easy" set: it includes ports, ISF/openFrameworks-wrapped shaders, raytracers, fluid sims,
  fractals, demos, and tutorial shaders.
- **Pipeline per shader.**
  1. `shadertoy2fx <in.glsl> -o <out.fx>` — record exit code + first diagnostic.
  2. For converted `.fx`: `ShadowDuskCLI <out.fx> <out.mgfx> /Profile:OpenGL` — record compile
     success + first error.
- **Scope filter.** A file was counted **out-of-scope** only when it has no ShaderToy `mainImage`
  entry at all (a plain `void main()` GLSL fragment shader, `mainSound`/`mainVR`/`mainCubemap`, or a
  manifest file). Multipass shaders that still define a single `mainImage` were kept in-scope: the
  converter's job is the single image pass, so a shader that merely *references* `iChannelN` buffers
  is a legitimate converter input.

---

## 2. Totals

| Metric | Count |
|---|---:|
| Fetched | 160 |
| Skipped as out-of-scope (no `mainImage`) | 6 |
| **In-scope** (have `mainImage`) | **154** |
| Converted OK (exit 0) | 31 |
| Converted but **failed to compile** (transpiler bugs) | 10 |
| Rejected by converter (loud diagnostic) | 123 |
| Compiled OK to OpenGL `.mgfx` | 21 |

- **Conversion rate (of in-scope):** **20.1 %** (31 / 154)
- **Compile rate (of converted):** **67.7 %** (21 / 31)
- **End-to-end rate (in-scope → loadable `.mgfx`):** **13.6 %** (21 / 154)

Out-of-scope breakdown (6): all 6 were plain `void main()` GLSL fragment shaders, not ShaderToy
`mainImage` format. (No `mainSound`/`mainVR`/`mainCubemap` landed in this sample; one `mainVR` was
*in*-scope-classified because it also had a `mainImage`, and was correctly rejected by the converter.)

The conversion rate is the headline finding: **~80 % of real ShaderToy shaders are rejected today**,
and the rejections cluster on a small number of constructs. Closing the top three would more than
triple coverage.

---

## 3. Ranked rejection causes

Two views are given. The **first-failure** view counts the construct that the converter rejected
*first* per shader (it stops at the first error). The **blast-radius** view counts how many in-scope
shaders *contain* a given unsupported construct anywhere — this is the better signal for "what to
build next," because a shader that fails first on `#ifdef` may also need uniforms, arrays, etc.

### 3a. First-failure cause (what stopped each rejected shader), 123 total

| Rank | Cause | Shaders |
|---:|---|---:|
| 1 | Preprocessor conditionals / `#include` (`#if`/`#ifdef`/`#ifndef`/`#else`/`#endif`/`#include`) | 45 |
| 2 | Top-level `uniform` / `varying` / `attribute` / `in` / `out` declaration | 29 |
| 3 | Top-level non-`const` global variable | 14 |
| 4 | Function-like `#define NAME(...)` macro | 11 |
| 5 | Parser/syntax error (mostly array `[`, see §5) | 10 |
| 6 | User-defined `struct` | 3 |
| 7 | `texelFetch` | 2 |
| 7 | Unknown type name (`layout`, framework wrapper tokens) | 2 |
| 7 | Unknown function/intrinsic (`textureCube`, undefined helper) | 2 |
| 7 | `mainImage` signature/arity wrong, or duplicate `mainImage` | 2 |
| 11 | `switch` statement | 1 |
| 11 | User-declared array | 1 |
| 11 | `mainVR` entry point | 1 |

### 3b. Blast-radius (in-scope shaders that *contain* the construct anywhere), 154 total

| Construct | Shaders containing it |
|---|---:|
| Top-level `uniform` | 66 |
| Preprocessor conditionals (`#if`/`#ifdef`/…) | 40 |
| Function-like `#define NAME(...)` | 23 |
| User-defined `struct` | 14 |
| Array declaration | 14 |
| `texelFetch` | 6 |
| `#include` | 5 |
| `varying` / `attribute` | 5 |
| `samplerCube` / `textureCube` | 4 |
| `switch` | 4 |
| `sampler3D` | 3 |
| `layout` qualifier | 3 |
| `textureLod` | 3 |
| `uint` / `uvec` | 3 |
| `textureGrad` | 1 |

> **Top-level `uniform` is the single biggest blocker (66 / 154 shaders contain one).** Crucially,
> **19 of those 66 declare *only* ShaderToy built-in uniforms** (`uniform float iTime;`,
> `uniform vec2 iResolution;`, `uniform sampler2D iChannel0;`, …). The converter already emits those
> globals itself, so a redundant declaration is harmless — **recognizing and dropping a re-declaration
> of a known ShaderToy uniform would convert 19 more shaders with near-zero risk.** The other 47 have
> at least one genuinely-custom uniform that needs a host-supplied value (a harder problem).

---

## 4. Recommended next features, ordered by impact

Impact = approximate number of additional in-scope shaders unblocked (from §3b, accounting for
overlap — many shaders need more than one of these, so the increments are not additive). The cheap,
high-yield wins are at the top.

1. **Drop redundant ShaderToy-uniform re-declarations** (+19, very low risk). When a top-level
   `uniform` declares a *known* built-in (`iTime`, `iResolution`, `iChannel0..3`, `iMouse`, …),
   recognize it and skip it rather than rejecting. The converter already synthesizes these globals.
2. **Preprocessor conditionals + object-like `#if`/`#ifdef`/`#ifndef`/`#else`/`#elif`/`#endif`**
   (+40 contain them; unblocks the #1 *first-failure* cause). Many ShaderToy shaders use `#ifdef AA`
   toggles or `#if HW_PERFORMANCE` guards around otherwise-supported code. A real C-style conditional
   evaluator (with the already-supported `#define` table) is the single biggest coverage lever.
   `#include` (5) can be folded in as "inline if the file is provided, else reject."
3. **Function-like macros `#define NAME(a,b) ...`** (+23 contain them). Extremely common in
   ShaderToy (`#define rot(a) mat2(cos(a),...)`, `#define S(a,b,x) smoothstep(...)`,
   `#define saturate(x) clamp(x,0.,1.)`). Token-paste expansion of argumented macros would unblock a
   large slice and pairs naturally with the `#define` work in (2).
4. **User-defined `struct`** (+14). Raytracers/SDF shaders define `struct Ray { vec3 ro; vec3 rd; };`
   etc. HLSL has near-identical struct syntax, so this is mostly parser + emit-passthrough work.
5. **Arrays (local/const-global declarations + initializers, indexing)** (+14, and the cause of most
   of the 10 "parser/syntax error" first-failures — see §5). `float k[3] = float[](...)`, palette
   tables, kernel weights. The GLSL `T name[N]` form parses differently from HLSL but the target
   syntax is straightforward.
6. **`texelFetch` / `textureLod` / `textureGrad` for `iChannelN`** (+~9 combined). `texelFetch`
   maps to a `tex2D` with explicit integer-coordinate normalization; `textureLod` is already half-done.
7. **Custom top-level uniforms** (+47 potential). **DONE (2026-06-19).** A custom `uniform` of a
   supported type is now emitted as an HLSL effect parameter the consumer drives (reported in
   `UsedUniforms`); samplers use the iChannelN-style texture+sampler pair. The host contract is the
   `ShaderToyEffect.SetCustom(name, value)` runtime helper. Unsupported uniform types stay loud rejects.

`switch` (4), `samplerCube`/`sampler3D` (7 combined), `uint`/`uvec` (3), and `layout`/`varying`
(framework-wrapped, off-format) are long-tail and lower priority.

---

## 5. Converted-but-failed-to-compile — transpiler correctness bugs (10)

These are **more serious than honest rejects**: the converter accepted the shader and emitted `.fx`
that the HLSL compiler then refused. Grouped by root cause. (Snippets below are hand-authored
minimal repros illustrating the shape, not copied shader source.)

### Genuine transpilation bugs (emit wrong HLSL)

| # | Shaders | Bug | Illustrative shape (authored) |
|---:|---:|---|---|
| B1 | 1 (`s080`) | **Compound assignment with a matrix RHS is not transposed.** The matrix-multiply trap (`A*B → mul(B,A)`) is applied to binary `*` but **not** to `*=`. GLSL `v *= M;` is emitted verbatim as `v *= M;`, so HLSL sees `float2 *= float2x2` → `cannot convert 'float2x2' to 'vector<float,2>'`. | GLSL `p.xy *= rot(a);` (where `rot` returns `mat2`) is emitted unchanged instead of `p.xy = mul(p.xy, rot(a));` |
| B2 | 1 (`s052`) | **Spurious parentheses trip `-Werror,-Wparentheses-equality`.** The emitter wraps every binary expression in `(...)`, so `if (a == 0.0)` becomes `if ((a == 0.0))`, which fxc rejects as a warned-error. | `if ((fragColor.a == 0.0)) {…}` |
| B3 | 1 (`s027`) | **Vector `==` inside `if` not reduced to scalar.** GLSL `==` on vectors yields a `bool` (component-equal via context) but the converter passes it through, and HLSL `==` yields a `bool2`, so `if (a == b)` → "conditional must evaluate to a scalar." Needs `all(a == b)`. | `if (iMouse.xy == float2(0)) {…}` (emitted as-is; HLSL wants `all(...)`) |
| B4 | 2 (`s060`,`s144`) | **Implicit vector truncation under stricter HLSL conversion.** GLSL silently truncates in spots HLSL flags as `-Werror,-Wconversion` (e.g. assigning a wider vector result, or a splat that lands in a narrower slot). | `float2 m = iMouse / iResolution.xy;` where `iMouse` is `float4` → `float4/float2` truncation error |
| B5 | 1 (`s151`) | **Double space "modifiers must appear before type."** In a generated/copied declaration the emitter produced `float  y` style spacing that fxc parsed as `modifiers must appear before type` at a helper-function param. Indicates a token-spacing / declaration-emission edge case. | emitted helper param like `float  y` triggering a modifier-order diagnostic |

### Leniency leaks — the converter should have *rejected* these (undeclared identifiers)

| # | Shaders | Cause |
|---:|---:|---|
| L1 | 4 (`s004`,`s031`,`s091`,`s122`) | The converter let an **unknown global identifier** through to HLSL instead of rejecting it: `RENDERSIZE` (ISF builtin), `iGlobalTime` (a *deprecated ShaderToy alias for `iTime`* — could be auto-mapped!), `iBackgroundColor`, `iNbItems` (custom uniforms). These surface only at compile time as "use of undeclared identifier." Per the MAPPING.md "loud reject, never silently-wrong" rule, the converter should diagnose an unresolved free identifier at convert time — and `iGlobalTime`→`iTime` is a trivial, high-value alias to add. |

**Priority among the bugs:** **B1 (matrix `*=`) and B3 (vector `==` in `if`) are the most important**
— both are *common* ShaderToy idioms (`p *= rot(t)` and `if (v == vec2(0))`) and both currently
produce code that silently fails to compile. **B2 (over-parenthesized equality)** is also common and
trivially fixable (don't double-wrap, or strip the redundant outer parens around `==`/`!=`).
**L1** should be tightened so the converter rejects unresolved identifiers at convert time (and
auto-aliases `iGlobalTime`), restoring the "loud, never silently-wrong" guarantee.

---

## 6. Summary

- **Gathered:** 160 real shaders from **150 distinct public GitHub repos** (broad cross-section).
- **Conversion rate:** 20.1 % of in-scope; **compile rate:** 67.7 % of converted; **end-to-end:** 13.6 %.
- **Top 5 rejection causes (first-failure):** (1) preprocessor `#if`/`#include` (45), (2) top-level
  `uniform`/`varying` (29), (3) top-level non-`const` global (14), (4) function-like `#define` macro
  (11), (5) array-driven parser errors (10).
- **Highest-value next features:** drop redundant ShaderToy-uniform re-declarations (+19, trivial),
  preprocessor conditionals (+40), function-like macros (+23), structs (+14), arrays (+14).
- **Correctness bugs found (convert-but-not-compile):** 5 genuine transpiler bugs — most important are
  **matrix `*=` not transposed** and **vector `==` inside `if` not scalarized** — plus 4 leniency
  leaks where an unknown global slipped through to a compile error instead of a clean reject
  (including the trivially-fixable `iGlobalTime`→`iTime` alias).
