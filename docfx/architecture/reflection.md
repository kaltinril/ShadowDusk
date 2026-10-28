# Reflection

To write a valid `.mgfx`, ShadowDusk must know each shader's **parameters** (names, types, sizes), **constant-buffer layouts**, and **sampler/texture bindings**. That information is recovered by *reflection* over the compiled bytecode. There are two faithful reflection sources, one per backend.

## Managed `SpirvReflector` (OpenGL / WebGL path)

For the OpenGL/WebGL path the shader is compiled to **SPIR-V** by DXC. ShadowDusk reflects that SPIR-V with a **pure-managed** reflector, `SpirvReflector` (`ShadowDusk.Core/Reflection/SpirvReflector.cs`, with the low-level SPIR-V parsing under `Reflection/Spirv/`). It extracts:

- parameter names, <xref:ShadowDusk.Core.Reflection.EffectParameterClass>/<xref:ShadowDusk.Core.Reflection.EffectParameterType>, array/vector/matrix sizes,
- constant-buffer (cbuffer) layouts and offsets,
- sampler/texture bindings and <xref:ShadowDusk.Core.Reflection.TextureDimension> (2D / Cube / 3D).

Being pure-managed, it runs anywhere .NET runs — including in the browser — with **no native dependency**, which is what makes the in-memory and WASM paths self-contained. It was validated as equivalent to a DXIL oracle across the corpus.

## DXBC reflection (DirectX path)

For the DirectX path the shader is compiled to **DXBC** by [`vkd3d-shader` or `d3dcompiler_47`](directx-dxbc-vkd3d.md). The same DXBC bytes are the reflection source, parsed by the pure-managed `RdefReader` (the DXBC container's `RDEF`/`ISGN`/`OSGN` chunks) — platform-agnostic, and proven deeply equal to the Windows `D3DReflect` oracle for both backends' output. A `DxbcReflectionExtractor` wraps it so both DXBC backends produce the same parameter/cbuffer/sampler metadata on every OS.

## The shared contract

Both reflectors produce the same managed shape — a `ReflectedEffect` of `ParameterReflection`, `ConstantBufferReflection`, `SamplerReflection`, and `TextureReflection` — behind the `IShaderReflector` abstraction (`ShadowDusk.Core/Reflection/IShaderReflector.cs`). The [MGFX writer](mgfx-format.md) consumes that contract, so the writer is backend-agnostic: the same writer emits a structurally compatible `.mgfx` whether the blob inside is GLSL text (OpenGL) or DXBC bytecode (DirectX).

## Parameter default values

`float Scale = 2.0;` is a default a tool wants to show, but no compiler in the pipeline hands it to reflection. Measured: DXC's DXIL reflection reports a null default value and warns that the initializer is ignored, and DXC drops the initializer from the SPIR-V altogether. vkd3d does keep it in the DXBC constant-buffer table, but it fills a matrix default in source order into the packed register shape, which scrambles every non-square matrix and transposes a `row_major` one. The other DXBC backend, `d3dcompiler_47`, only runs on Windows and was not measured.

So the value comes from the source. `GlobalInitializerReader` evaluates each global's initializer from the preprocessed text and the pipeline writes it into the default blob of every MGFX and KNIFX parameter. <xref:ShadowDusk.Core.CompiledShader.Parameters> then reports that same blob as <xref:ShadowDusk.Core.Reflection.ParameterReflection.DefaultValue>, converted to logical row-major order (the blob holds a matrix column by column), array element after array element, so the reflection result and the bytes cannot disagree. A parameter whose blob is zeros the author never wrote (no initializer, or one that could not be evaluated, with an `SD0017` warning) has a `null` default, never zero. On FNA the same reader supplies the value when the shape agrees, and the compiled constant table supplies a scalar or vector it cannot read; the table is trimmed to the registers the shader reads, so a matrix or array is not taken from it.
