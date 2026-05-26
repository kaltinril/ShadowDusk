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
│  PHASE 2 — FX9 Pre-Parser               │
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
│  PHASE 3 — Preprocessor                 │
│                                         │
│  Flatten #includes                      │
│  Inject platform macros:                │
│    DirectX  → HLSL=1, SM4=1, MGFX=1    │
│    OpenGL   → GLSL=1, OPENGL=1, MGFX=1 │
└──────────────────┬──────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│  PHASE 4 — DXC Compiler (Vortice.Dxc)  │
│                                         │
│  Compiles each pass's VS + PS entry     │
│  point separately                       │
└──────────┬──────────────────────────────┘
           │
     ┌─────┴──────┐
     │            │
     ▼            ▼
 DirectX       OpenGL / WebGL
 target        target
     │            │
     │            │  -spirv flag
     ▼            ▼
  DXBC         SPIR-V
  bytecode      (binary IR)
     │            │
     │            ▼
     │   ┌─────────────────────────────┐
     │   │  PHASE 6 — SPIRV-Cross      │
     │   │  (P/Invoke on desktop,      │
     │   │   JS interop in WASM)       │
     │   │                             │
     │   │  Applies:                   │
     │   │  • Y-axis flip              │
     │   │  • Depth range remap        │
     │   │  • Combined image samplers  │
     │   │  • GLSL version targeting   │
     │   │    desktop  → #version 140  │
     │   │    WebGL    → #version 300 es│
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
│  PHASE 5 — Shader Reflection            │
│                                         │
│  Extracts from bytecode:                │
│  parameter names, types, sizes,         │
│  cbuffer layouts, sampler bindings      │
└──────────────────┬──────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│  PHASE 7 — .mgfx Binary Writer          │
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

- **FX9 blocks** (`technique`, `pass`, `sampler_state`) are a D3DX legacy format inherited by XNA and MonoGame. DXC cannot parse them — the pre-parser strips them before DXC ever sees the file.
- **DirectX path** is shorter: DXBC goes straight from DXC to the binary writer with no transpilation step.
- **OpenGL / WebGL path** has an extra hop through SPIRV-Cross to convert SPIR-V → GLSL. Desktop targets `#version 140`; WebGL (KNI browser) targets `#version 300 es`.
- **SPIRV-Cross** runs as native P/Invoke on desktop (CLI) and as a WASM JS interop call in the browser (KNI / XNA Fiddle).
- **Output** is always a `.mgfx` binary blob — written to disk by the CLI, returned as `byte[]` in-memory by the WASM library.
