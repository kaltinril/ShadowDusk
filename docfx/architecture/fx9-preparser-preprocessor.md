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
- **Injects platform macros** so a single source compiles correctly per target:

| Target | Macros injected |
|---|---|
| DirectX | `HLSL=1`, `SM4=1`, `MGFX=1` |
| OpenGL | `GLSL=1`, `OPENGL=1`, `MGFX=1` |

These mirror the macros `mgfxc` defines, so existing `#ifdef`-guarded shader source behaves identically.

## Where this sits in the pipeline

These two passes are the first stages of [The Faithful Pipeline](the-faithful-pipeline.md): `.fx` → **FX9 pre-parser** → **preprocessor** → compiler (DXC for SPIR-V, or `vkd3d-shader` for DXBC).
