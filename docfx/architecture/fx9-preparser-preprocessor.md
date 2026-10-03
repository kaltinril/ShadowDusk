# FX9 Pre-Parser & Preprocessor

Before any HLSL compiler sees the source, ShadowDusk runs two managed front-end passes. They live in `ShadowDusk.HLSL` (`FxPreParser`, `FxFileParser`) and `ShadowDusk.Core/Preprocessor/`.

## FX9 Pre-Parser

`.fx` files contain **D3DX FX9 / effect-framework blocks** — `technique`, `pass`, `sampler_state` — that XNA and MonoGame inherited. These are **not valid HLSL**; DXC and `vkd3d-shader` cannot parse them. The pre-parser:

1. **Strips** the FX9 blocks out of the source, leaving pure, compiler-safe HLSL (`StrippedHLSL`).
2. **Extracts metadata** the compilers can't see: techniques, passes (with their VS/PS entry points and shader profile), sampler state, and render states.

It also performs targeted **texture-keyword rewrites** (e.g. legacy `texture T;` → `Texture2D T;`) so effect-syntax shaders compile faithfully on the modern toolchain.

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

So for an OpenGL compile (and the raylib converter) ShadowDusk builds a **preprocessed view** of the effect with a small managed C preprocessor in `ShadowDusk.HLSL` (`FxMacroPreprocessor`): conditionals evaluated, object-like and function-like macros expanded (`#`, `##`, `__VA_ARGS__`), with the same platform and user macros the compile uses. For an effect that compiles, the view is never handed to a compiler (DXC still preprocesses the real source); it only answers those sampler-register questions. The reservation is made by every sampler type keyword with an explicit register (`sampler`, `sampler2D`, `SamplerComparisonState`, not only `SamplerState`), used or not, as fxc does. It is pure managed code, so the answer is identical on every host, the browser included, whose DXC build has no preprocess-only entry point. A directive or expression it cannot evaluate is the `SD0009` error, raised only after DXC has accepted the source.

The one exception is a **recovery**. The pre-parser's own rewrite of legacy samplers works on the raw main file, so a legacy `sampler` / `sampler2D` declared in an `#include`d file, or declared or read (`tex2D`) through a macro (MonoGame's `DECLARE_TEXTURE` / `SAMPLE_TEXTURE`), reaches DXC unrewritten and DXC rejects it. When that happens on OpenGL, Vulkan or DirectX 12, and the text DXC was given still holds legacy sampler syntax once preprocessed, ShadowDusk repeats the pre-parse on the preprocessed source (the same managed preprocessor, in a compiler-input form that keeps one output line per source line and passes `#line` / `#pragma` through, so diagnostics still point at the author's file and line) and compiles that text instead, which is how `mgfxc` itself works. Because it runs only after the raw compile has failed, an effect that compiles from its raw source is compiled from exactly that text. A shape the rewrite still cannot model, or a conditional on a compiler-predefined macro the managed view cannot evaluate, is the `SD0016` error after the compiler's own diagnostics. DirectX 11 compiles the legacy syntax natively and never recovers.

## Where this sits in the pipeline

These two passes are the first stages of [The Faithful Pipeline](the-faithful-pipeline.md): `.fx` → **FX9 pre-parser** → **preprocessor** → compiler (DXC for SPIR-V, or `vkd3d-shader` for DXBC).
