# Reflection

To write a valid `.mgfx`, ShadowDusk must know each shader's **parameters** (names, types, sizes), **constant-buffer layouts**, and **sampler/texture bindings**. That information is recovered by *reflection* over the compiled bytecode. There are two faithful reflection sources, one per backend.

## Managed `SpirvReflector` (OpenGL / WebGL path)

For the OpenGL/WebGL path the shader is compiled to **SPIR-V** by DXC. ShadowDusk reflects that SPIR-V with a **pure-managed** reflector, `SpirvReflector` (`ShadowDusk.Core/Reflection/SpirvReflector.cs`, with the low-level SPIR-V parsing under `Reflection/Spirv/`). It extracts:

- parameter names, <xref:ShadowDusk.Core.Reflection.EffectParameterClass>/<xref:ShadowDusk.Core.Reflection.EffectParameterType>, array/vector/matrix sizes,
- constant-buffer (cbuffer) layouts and offsets,
- sampler/texture bindings and <xref:ShadowDusk.Core.Reflection.TextureDimension> (2D / Cube / 3D).

Being pure-managed, it runs anywhere .NET runs — including in the browser — with **no native dependency**, which is what makes the in-memory and WASM paths self-contained. It was validated as equivalent to a DXIL oracle across the corpus.

## DXBC reflection (DirectX path)

For the DirectX path the shader is compiled to **DXBC** by [`vkd3d-shader` or `d3dcompiler_47`](directx-dxbc-vkd3d.md). The same DXBC bytes are the reflection source, read via `ID3D11ShaderReflection`. A `DxbcReflectionExtractor` reflects the output of **both** DXBC backends so they produce the same parameter/cbuffer/sampler metadata.

## The shared contract

Both reflectors produce the same managed shape — a `ReflectedEffect` of `ParameterReflection`, `ConstantBufferReflection`, `SamplerReflection`, and `TextureReflection` — behind the `IShaderReflector` abstraction (`ShadowDusk.Core/Reflection/IShaderReflector.cs`). The [MGFX writer](mgfx-format.md) consumes that contract, so the writer is backend-agnostic: the same writer emits a structurally compatible `.mgfx` whether the blob inside is GLSL text (OpenGL) or DXBC bytecode (DirectX).
