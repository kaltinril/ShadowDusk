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

The flattener above deliberately leaves `#if` and `#define` for the compiler to evaluate. One decision ShadowDusk makes *itself* has to see their result, though: on OpenGL, a modern `SamplerState X : register(sN)` reserves sampler register `N`, so the combined sampler fxc synthesizes for each texture is allocated around it. `mgfxc` decides that on the **preprocessed** source, so a register that exists only in an inactive `#if` branch reserves nothing, and one spelled through a macro (`#define SLOT(n) : register(n)`) or written in an `#include`d file does.

So for an OpenGL compile (and the raylib converter) ShadowDusk builds a **preprocessed view** of the effect with a small managed C preprocessor in `ShadowDusk.HLSL` (`FxMacroPreprocessor`): conditionals evaluated, object-like and function-like macros expanded (`#`, `##`, `__VA_ARGS__`), with the same platform and user macros the compile uses. The view is never handed to a compiler (DXC still preprocesses the real source); it only answers the reservation question. It is pure managed code, so the answer is identical on every host, the browser included, whose DXC build has no preprocess-only entry point. A directive or expression it cannot evaluate is the `SD0009` error, raised only after DXC has accepted the source.

## Where this sits in the pipeline

These two passes are the first stages of [The Faithful Pipeline](the-faithful-pipeline.md): `.fx` → **FX9 pre-parser** → **preprocessor** → compiler (DXC for SPIR-V, or `vkd3d-shader` for DXBC).
