# shadertoy2fx — Coverage Stress Test (Phase 46)

A statistical coverage report for the `shadertoy2fx` GLSL → HLSL `.fx` converter, produced by
running a batch of **real, third-party single-pass ShaderToy / GLSL image shaders** through the
converter and then through the ShadowDusk OpenGL compiler.

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
7. **Custom top-level uniforms with a default-value convention** (+47 potential, but harder/lower
   confidence). Would need a host contract for supplying arbitrary uniform values; only worth it after
   1-5.

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
