# ShadowDusk — Compilation Pipeline

High-level flow from `.fx` source to `.mgfx` output across all target platforms.

```
INPUT
─────
  shader.fx
  (HLSL + FX9 blocks)
       │
       ▼
┌─────────────────────────────────────────┐
│  FX9 Pre-Parser                         │
│                                         │
│  Strips technique/pass/sampler_state    │
│  blocks (not valid HLSL)                │
└────────────────┬────────────────────────┘
                 │
        ┌────────┴────────┐
        │                 │
        ▼                 ▼
  StrippedHLSL       Metadata
  (pure HLSL,        ┌──────────────┐
   DXC-safe)         │ TechniqueInfo│
        │            │ PassInfo     │
        │            │  - VS entry  │
        │            │  - PS entry  │
        │            │  - profile   │
        │            │ SamplerInfo  │
        │            │ RenderStates │
        │            └──────────────┘
        │
        ▼
┌─────────────────────────────────────────┐
│  Preprocessor                           │
│                                         │
│  Flatten #includes                      │
│  Inject platform macros:                │
│    DirectX  → HLSL=1, SM4=1, MGFX=1    │
│    OpenGL   → GLSL=1, OPENGL=1, MGFX=1 │
└──────────────────┬──────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│  HLSL → bytecode/IR                     │
│                                         │
│  Compiles each pass's VS + PS entry     │
│  point separately. The compiler depends │
│  on the target (see the two branches    │
│  below) — DXC is NOT used for DX11.     │
└──────────┬──────────────────────────────┘
           │
     ┌─────┴──────┐
     │            │
     ▼            ▼
 DirectX       OpenGL / WebGL
 target        target
     │            │
     │            │  DXC (Vortice.Dxc),
     │            │  -spirv flag
     ▼            ▼
 DXBC backend   SPIR-V
 (vkd3d-shader   (binary IR)
  or             │
  d3dcompiler_47)│
     │            │
     │            ▼
     │   ┌─────────────────────────────┐
     │   │  SPIRV-Cross                │
     │   │  (P/Invoke on desktop,      │
     │   │   JS interop in WASM)       │
     │   │                             │
     │   │  Applies:                   │
     │   │  • Y-axis flip              │
     │   │  • Depth range remap        │
     │   │  • Combined image samplers  │
     │   │  • Emits #version 140 GLSL  │
     │   └──────────────┬──────────────┘
     │                  │
     │                  ▼
     │   ┌─────────────────────────────┐
     │   │  MonoGameGlslRewriter       │
     │   │  (managed, MojoShader       │
     │   │   dialect)                  │
     │   │                             │
     │   │  • Strips #version          │
     │   │  • MojoShader uniform &     │
     │   │    sampler naming           │
     │   │  • gl_FragColor / varying   │
     │   └──────────────┬──────────────┘
     │                  │
     │                  ▼
     │              GLSL source
     │              (text string)
     │
     └───────┬──────────┘
             │
             ▼
┌─────────────────────────────────────────┐
│  Shader Reflection                      │
│                                         │
│  Extracts from bytecode:                │
│  parameter names, types, sizes,         │
│  cbuffer layouts, sampler bindings      │
└──────────────────┬──────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│  .mgfx Binary Writer                    │
│                                         │
│  Assembles everything:                  │
│  Header (MGFX signature + version)      │
│  + Constant buffers                     │
│  + Shader blobs (DXBC or GLSL text)     │
│  + Parameters (names, types, offsets)   │
│  + Techniques + Passes                  │
│  + Render states                        │
└──────────────────┬──────────────────────┘
                   │
        ┌──────────┴──────────┐
        ▼                     ▼
  File on disk          byte[] in memory
  (CLI path)            (WASM/KNI path)
  shader.mgfx           returned from
                        CompileAsync()
```

## Notes

- **FX9 blocks** (`technique`, `pass`, `sampler_state`) are a D3DX legacy format inherited by XNA and MonoGame. The HLSL compilers cannot parse them — the pre-parser strips them before any compiler sees the file.
- **DirectX path does NOT use DXC.** DXC only emits SM6 DXIL, but MonoGame 3.8's DX11 runtime loads **DXBC (SM ≤ 5)**. So the DirectX target routes HLSL → DXBC through a separate backend behind `IDxbcShaderCompiler`: the cross-platform **vkd3d-shader** library is the shipping backend (Linux/macOS/Windows, no Wine or Windows SDK), with Windows-only **`d3dcompiler_47`** as a correctness oracle. The backend is selected via `CompilerOptions.DxbcBackend` (`DxbcBackend.D3DCompiler` default, `DxbcBackend.Vkd3d` opt-in; see `src/ShadowDusk.Core/DxbcBackend.cs`). DXC's `ps_6_0`/`vs_6_0` (DXIL) path is retained only for the Vulkan / DX12-KNI SM6 profile. The DXBC bytes are also the reflection source (parsed by the pure-managed `RdefReader`, proven equal to the Windows `D3DReflect` oracle — so DX reflection runs on every OS and in the browser).
- **OpenGL / WebGL path** is the faithful HLSL →[DXC]→ SPIR-V →[SPIRV-Cross]→ GLSL pipeline, plus the managed `MonoGameGlslRewriter` (MojoShader-dialect rewrite). SPIRV-Cross emits `#version 140`; the rewrite then **strips the `#version` directive** and emits version-less legacy MojoShader-dialect GLSL — valid on both desktop GL and WebGL1. For WebGL2/HiDef, KNI's runtime converter up-converts that dialect to GLSL ES 3.00 at load, so a single `.mgfx` serves both profiles — ShadowDusk does **not** emit `#version 300 es`. See `docs/glsl-uniform-naming.md` for the GLSL dialect / uniform-naming contract the rewrite enforces.
- **SPIRV-Cross** runs as native P/Invoke on desktop (CLI) and as a WASM JS-interop call in the browser (KNI / XNA Fiddle).
- **DXC in the browser** is the *same* DirectXShaderCompiler compiled to WebAssembly (matching `Vortice.Dxc`'s pinned commit), so the in-browser frontend emits byte-identical SPIR-V to the desktop pipeline — one faithful compiler everywhere, no substitute frontend. The in-browser shader-fiddle is a *sample* of this reach, not the product.
- **vkd3d-shader in the browser**: the same pinned vkd3d 1.17 is also compiled to WebAssembly and ships in `ShadowDusk.Wasm`, so `PlatformTarget.DirectX` (DXBC `.mgfx`) and `PlatformTarget.Fna` (fx_2_0 `.fxb`) compile in-browser too, byte-identical to the desktop output. These are *export* targets — a browser cannot render DXBC/D3D9 bytecode; the artifacts render in the consumer's MonoGame WindowsDX / FNA game.
- **Output** is a `.mgfx` binary blob (or, for `PlatformTarget.Fna`, a fx_2_0 `.fxb`) — written to disk by the CLI, returned as `byte[]` in-memory by the library (the in-memory / WASM path).
