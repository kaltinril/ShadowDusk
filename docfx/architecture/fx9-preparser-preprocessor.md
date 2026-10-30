# FX9 Pre-Parser & Preprocessor

Before any HLSL compiler sees the source, ShadowDusk runs two managed front-end passes. They live in `ShadowDusk.HLSL` (`FxPreParser`, `FxFileParser`) and `ShadowDusk.Core/Preprocessor/`.

## FX9 Pre-Parser

`.fx` files contain **D3DX FX9 / effect-framework blocks** — `technique`, `pass`, `sampler_state` — that XNA and MonoGame inherited. These are **not valid HLSL**; DXC and `vkd3d-shader` cannot parse them. The pre-parser:

1. **Strips** the FX9 blocks out of the source, leaving pure, compiler-safe HLSL (`StrippedHLSL`).
2. **Extracts metadata** the compilers can't see: techniques, passes (with their VS/PS entry points and shader profile), sampler state, and render states.

It also performs targeted **texture-keyword rewrites** (e.g. legacy `texture T;` → `Texture2D T;`) so effect-syntax shaders compile faithfully on the modern toolchain.

### Legacy sampling intrinsics

On every target except FNA (whose `fx_2_0` compiler takes D3D9 syntax natively), the pre-parser rewrites a legacy sampler (`sampler2D S;`, `sampler S = sampler_state { Texture = <T>; … };`) to a `Texture2D` + `SamplerState` pair and its sampling calls to the texture's methods:

| Source | Becomes | Note |
|---|---|---|
| `tex2D(s, uv)` | `T.Sample(s, uv)` | name swap; the arguments line up |
| `tex2Dgrad(s, uv, dx, dy)` | `T.SampleGrad(s, uv, dx, dy)` | name swap |
| `tex2Dlod(s, t)` | `tex2Dlod_SD0(T, s, t)`, a helper returning `T.SampleLevel(s, t.xy, t.w)` | LOD in `t.w`; vertex or pixel stage |
| `tex2Dbias(s, t)` | `tex2Dbias_SD0(T, s, t)`, returning `T.SampleBias(s, t.xy, t.w)` | bias in `t.w`; pixel stage |
| `tex2Dproj(s, t)` | `tex2Dproj_SD0(T, s, t)`, returning `T.Sample(s, t.xy / t.w)` | projective divide; pixel stage |

The last three pack a second operand into the coordinate's `w`, so the call cannot be a plain name swap. Each becomes a call of a small helper the pre-parser defines at global scope before the declaration (usually the function) that calls it: on a blank or comment-only line directly above the declaration when there is one (and that line is not inside a multi-line initializer, nor spliced onto a `// comment \` above it by a trailing backslash), else right after the `;` or `}` that ended the previous declaration (when only whitespace and comments lie between), else at the start of the declaration itself. The first two leave every column of the author's declaration line where it was. The declaration starts at its first `[attribute]` (the lexer drops the brackets, so this is found character by character), and a `;` inside an FX annotation (`: COLOR0 < string s = "x"; >`) does not end it:

```hlsl
float4 tex2Dlod_SD0(Texture2D sdTexture, SamplerState sdSampler, float4 sdCoord) { return sdTexture.SampleLevel(sdSampler, sdCoord.xy, sdCoord.w); } float4 MainVS(...)
```

- **A helper, not an inline `T.SampleLevel(s, (t).xy, (t).w)`**, so the coordinate is evaluated once, as the intrinsic evaluates it: an expression with a side effect (an assignment, `++`, a call with an `inout` parameter) would otherwise run twice.
- **The arguments are copied verbatim.** Only the span from the intrinsic's name through its `(` is replaced, so nested calls, swizzles, macros and comments in the arguments are untouched, a nested `tex2D` inside them is still rewritten in place, and no line moves (an error inside the coordinate keeps its line number).
- **The helper takes the texture and the sampler as parameters** and references no global, so it is valid wherever it lands, even when the texture or the sampler is declared inside an `#if`. Its texture parameter takes the texture's declared type (`Texture2D<float>` stays `Texture2D<float>`). Each declaration gets its own helper, so two `#if` branches never define one name twice.
- **Refused loudly with `FX0012`**: a call with other than two arguments.
- **Left to the preprocessed re-parse**: a conditional directive that closes or switches the branch the calling declaration starts in before the call (one body under two `#if`-selected headers), and a texture declared with a different type in different branches. Both depend on which branch compiles, so the raw parse leaves the call verbatim and records it (`FxParseResult.DeferredLegacyIntrinsicCalls`); the legacy-sampler recovery below rewrites the preprocessed source, where every conditional is resolved. DirectX 11, whose compiler would otherwise take the unrewritten call (measured: an effect with no texture parameter), goes to that recovery up front; if the recovery does not apply (the call is in a branch this configuration compiles out, or an `#include` does not resolve) it compiles the normal way, so the real result or error comes back.
- **Diagnostics name what the author wrote.** A compiler error that mentions a helper (`no matching function for call to 'tex2Dproj_SD0'`) names the intrinsic (`'tex2Dproj'`) in `ShaderError.Message`; the compiler's own text is kept in `RawDiagnostics`. Columns after the call on the call's line still move by the rename (`tex2Dlod(` → `tex2Dlod_SD0(T, `), as `tex2D` → `T.Sample` always has.
- A sampler the raw parse cannot bind (a function parameter, or one an `#include` or macro declares) is left alone, exactly like `tex2D`'s; the recovery below re-parses the preprocessed source.
- On **Vulkan**, the helper call is also what pairs the texture with its sampler on one combined descriptor (`VulkanTextureSamplerBindingRewriter`), since the pair never appears in a `T.Sample(s, …)` call.

`mgfxc` 3.8.4.1 `/Profile:DirectX_11` compiles every legacy intrinsic, including the 1D, 3D and cube families (`tex1D…`, `tex3D…`, `texCUBE…`). ShadowDusk does not rewrite those yet (they need a `Texture1D` / `Texture3D` / `TextureCube` the sampler rewrite does not synthesize) and refuses them with `FX0012`, naming the intrinsic and the modern form to use.

The stripped HLSL feeds the compiler; the extracted metadata feeds the [reflection](reflection.md) and [MGFX writer](mgfx-format.md) stages so the emitted `.mgfx` reconstructs the original technique/pass structure.

## Preprocessor

The preprocessor (`ShadowDusk.Core/Preprocessor/Preprocessor.cs`) then:

- **Flattens `#include` directives**, resolving them through an <xref:ShadowDusk.Core.Preprocessor.IIncludeResolver> (file-system or in-memory). Missing includes fail loudly with `SD0001`; circular includes with `SD0002`.
- **Compares resolved include paths the way your storage spells them, not the way the operating system is assumed to.** Deciding whether `Shared/Common.fxh` and `shared/common.fxh` are one file or two matters for `#pragma once` and for cycle detection, and inferring it from the OS is wrong on real targets: **Android's file system is case-sensitive**, and **APFS can be formatted case-sensitive**. An <xref:ShadowDusk.Core.Preprocessor.IIncludePathCanonicalizer> is asked instead, so two spellings merge only when the storage confirms they name one file. An include that resolved *only* because your host ignores case gets the `SD0008` warning, because that same shader fails with `SD0001` on Android, Linux, or a case-sensitive Mac.
- **Injects platform macros** so a single source compiles correctly per target:

| Target | Macros injected |
|---|---|
| DirectX | `HLSL=1`, `SM4=1`, `MGFX=1` |
| OpenGL | `GLSL=1`, `OPENGL=1`, `MGFX=1` |

These mirror the macros `mgfxc` defines, so existing `#ifdef`-guarded shader source behaves identically.

## The preprocessed view (OpenGL sampler-register reservations)

The flattener above deliberately leaves `#if` and `#define` for the compiler to evaluate. One decision ShadowDusk makes *itself* has to see their result, though: on OpenGL, a modern `SamplerState X : register(sN)` reserves sampler register `N`, so the combined sampler fxc synthesizes for each texture is allocated around it. `mgfxc` decides that on the **preprocessed** source, so a register that exists only in an inactive `#if` branch reserves nothing, and one spelled through a macro (`#define SLOT(n) : register(n)`) or written in an `#include`d file does. The same holds for the other half of the rule, the texture unit a legacy `sampler X : register(sN)` **pins**: a register written only in the branch OpenGL does not compile pins nothing, and a register number spelled through a macro (`#define REG s1`, `register(REG)`) does pin.

So for an OpenGL compile (and the raylib converter) ShadowDusk builds a **preprocessed view** of the effect with a small managed C preprocessor in `ShadowDusk.HLSL` (`FxMacroPreprocessor`): conditionals evaluated, object-like and function-like macros expanded (`#`, `##`, `__VA_ARGS__`), with the same platform and user macros the compile uses. A comment is whitespace to it, as to any C preprocessor, so a `/* ... */` that spans lines inside a `#define` or `#if` belongs to that directive and does not end it (issue #426). For an effect that compiles, the view is never handed to a compiler (DXC still preprocesses the real source); it only answers those sampler-register questions. The reservation is made by every sampler type keyword with an explicit register (`sampler`, `sampler2D`, `SamplerComparisonState`, not only `SamplerState`), used or not, as fxc does. It is pure managed code, so the answer is identical on every host, the browser included, whose DXC build has no preprocess-only entry point. A directive or expression it cannot evaluate is the `SD0009` error, raised only after DXC has accepted the source.

The one exception is a **recovery**. The pre-parser's own rewrite of legacy samplers works on the raw main file, so a legacy `sampler` / `sampler2D` declared in an `#include`d file, or declared or read (`tex2D`) through a macro (MonoGame's `DECLARE_TEXTURE` / `SAMPLE_TEXTURE`), reaches DXC unrewritten and DXC rejects it. When that happens on OpenGL, Vulkan or DirectX 12, or in the raylib and SkSL converters (which compile through the same DXC; issue #327), and the text DXC was given still holds legacy sampler syntax once preprocessed, ShadowDusk repeats the pre-parse on the preprocessed source (the same managed preprocessor, in a compiler-input form that keeps one output line per source line and passes `#line` / `#pragma` through, so diagnostics still point at the author's file and line) and compiles that text instead, which is how `mgfxc` itself works. Because it runs only after the raw compile has failed, an effect that compiles from its raw source is compiled from exactly that text. A shape the rewrite still cannot model, or a conditional on a compiler-predefined macro the managed view cannot evaluate, is the `SD0016` error after the compiler's own diagnostics. DirectX 11 compiles the legacy syntax natively and never recovers.

## Techniques come from the preprocessed source

`mgfxc` preprocesses first and finds techniques second, so ShadowDusk reads the technique list off the same managed preprocessed view, with the compile's platform and user macros, on every target (FNA, raylib and SkSL included):

- a technique inside an inactive `#if` (or behind a `/Defines` value that is not set) does not exist;
- a technique in an `#include`d file is compiled, beside the main file's own; its block is blanked from the compiler's input so the compiler is never handed effect syntax;
- a `compile` target written through a macro (`compile VS_SHADERMODEL MainVS()`) is the profile the macro expands to. That is what a KNIFX container records as each shader's model: `#define VS_SHADERMODEL vs_4_0` records 4.0, the same as KNI's own content pipeline. On FNA an expansion to an SM 2–3 profile is honored as written; any other expansion (the `vs_4_0_level_9_1` arm of MonoGame's standard header) keeps the SM3 ceiling.

Techniques that a macro produces (MonoGame's `TECHNIQUE(name, vs, ps)` in its stock effects) keep the zero-technique handling they had: recovered on DirectX, Vulkan, DirectX 12 and FNA, `SD0010` on OpenGL. A view that cannot be built is `SD0009`, raised only after the compiler has accepted the source.

## Where this sits in the pipeline

These two passes are the first stages of [The Faithful Pipeline](the-faithful-pipeline.md): `.fx` → **FX9 pre-parser** → **preprocessor** → compiler (DXC for SPIR-V, or `vkd3d-shader` for DXBC).
