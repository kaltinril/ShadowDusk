# Phase 7 — MGFX Binary Writer

**Goal:** Serialize the compiled and reflected effect data into the `.mgfx` binary format that MonoGame's `Effect` class can load. This phase consumes the `ShaderIR` produced by Phases 2–6 and emits a `byte[]` blob that is byte-compatible with MonoGame's stock `mgfxc` output.

This phase produces the `MgfxWriter` class in `ShadowDusk.Core`. No CLI wiring happens here — that is Phase 8.

---

## 1. Prerequisites

| Requirement | Provided by |
|---|---|
| `ShaderIR` populated with techniques, passes, parameters, constant buffers, and compiled shader blobs | Phase 2 (parser), Phase 5 (reflection) |
| Per-pass shader blobs (GLSL bytes for OpenGL, DXBC for DX11, SPIR-V for Vulkan) | Phase 4 (DXC), Phase 6 (SPIRV-Cross) |
| Per-pass render state key-value pairs | Phase 2 (FX9 pre-parser) |
| `Result<T, ShaderError>` discriminated union | Phase 1 |

---

## 2. Binary Format Reference

### 2.1 Overall Layout (in byte order)

| # | Section | Notes |
|---|---|---|
| 1 | Header | 6 bytes: 4-byte signature + 1-byte version + 1-byte profile ID |
| 2 | Constant Buffers | int32 count, then N buffer records |
| 3 | Shaders | int32 count, then N shader blob records |
| 4 | Parameters | int32 count, then N parameter records |
| 5 | Techniques | int32 count, then N technique records (each embeds its passes inline) |

### 2.2 Header (6 bytes)

| Offset | Size | Field | Value |
|--------|------|-------|-------|
| 0 | 4 bytes | Signature | `0x4D474658` (little-endian; ASCII "MGFX") |
| 4 | 1 byte | Version | `10` (default) or `11` (opt-in) |
| 5 | 1 byte | Profile ID | see table below |

**Profile ID values:**

| Profile | Byte |
|---------|------|
| OpenGL | `0` |
| DirectX_11 | `1` |
| Vulkan | `3` |

> **Version compatibility:** A `.mgfx` file compiled with version `11` is **rejected** at load time by a MonoGame 3.8.2 (`version 10`) runtime. Always emit `10` unless the caller explicitly requests `11` via `--mgfx-version 11` (wired in Phase 8).

### 2.3 Constant Buffer Record

Per constant buffer, in order:

| Field | Type | Notes |
|---|---|---|
| Name | string (7-bit length prefix) | See Section 3 |
| Size | int16 | Buffer size in bytes |
| Parameter index count | int32 | Number of parameters that belong to this buffer |
| Parameter indices | int32[] | One int32 per parameter, referencing the global parameter table |
| Parameter offsets | uint16[] | Byte offset of each parameter within the buffer; parallel to indices |

### 2.4 Shader Blob Record

Per shader blob, in order:

| Field | Type | Notes |
|---|---|---|
| Byte length | int32 | Length of the raw blob that follows |
| Raw bytes | byte[] | GLSL text (OpenGL), DXBC (DX11), SPIR-V bytes (Vulkan) |

> For OpenGL, the GLSL source is written as raw UTF-8 bytes (no null terminator). MonoGame's GL backend expects text, not a compiled binary.

### 2.5 Parameter Record

Per parameter, in order:

| Field | Type | Notes |
|---|---|---|
| Class | byte | `EffectParameterClass` enum value (Scalar=0, Vector=1, Matrix=2, Object=3, Struct=4) |
| Type | byte | `EffectParameterType` enum value (Void=0, Bool=1, Int32=2, Single=3, String=4, Texture=5, Texture1D=6, Texture2D=7, Texture3D=8, TextureCube=9) |
| Name | string (7-bit) | |
| Semantic | string (7-bit) | Empty string if none |
| Annotations | annotation list | See Section 2.7 |
| Rows | byte | Matrix row count; 0 for scalars/vectors/objects |
| Columns | byte | Vector/matrix column count; 0 for scalars/objects |
| Member indices | int32[] | Count (int32) followed by one int32 per struct member; empty list for non-structs |
| Element indices | int32[] | Count (int32) followed by one int32 per array element; empty list for non-arrays |

### 2.6 Technique and Pass Records

Techniques are written in source-file order (see Section 5). Each technique record:

| Field | Type | Notes |
|---|---|---|
| Name | string (7-bit) | |
| Annotations | annotation list | See Section 2.7 |
| Pass count | int32 | |
| Passes | pass records[] | Written inline, immediately following pass count |

Each pass record:

| Field | Type | Notes |
|---|---|---|
| Name | string (7-bit) | |
| Annotations | annotation list | See Section 2.7 |
| Vertex shader index | int16 | Index into the shader blob table; `-1` if none |
| Pixel shader index | int16 | Index into the shader blob table; `-1` if none |
| Render states | render state block | See Section 2.8 |

### 2.7 Annotation List Encoding

An annotation list is written as:

1. `int32` count of annotations
2. Per annotation:
   - Name: `string` (7-bit)
   - Type: `byte` (`EffectParameterType` enum)
   - Value: type-dependent — `string` (7-bit) for string type, `float` for Single, `int32` for Int32/Bool

Empty annotation lists are written as a single `int32` zero.

### 2.8 Render State Block (per pass)

Render states are serialized as three optional sub-blocks, one per state object type. Each sub-block is gated by a presence flag:

| Field | Type | Notes |
|---|---|---|
| BlendState present | byte | `1` if pass has explicit blend state, else `0` |
| BlendState fields | key-value pairs | Only if present byte is `1`; see Section 6 |
| DepthStencilState present | byte | `1` if present, else `0` |
| DepthStencilState fields | key-value pairs | Only if present |
| RasterizerState present | byte | `1` if present, else `0` |
| RasterizerState fields | key-value pairs | Only if present |

Each key-value pair is written as:
- Key: `byte` (field ID from the MonoGame render-state field enum)
- Value: `int32` (enum ordinal, bool as 0/1, or bit-packed float)

---

## 3. String Encoding (Critical)

**All strings in the `.mgfx` format use `BinaryWriter.Write(string)`**, which writes a 7-bit variable-length encoded length prefix followed by UTF-8 bytes. This is the standard `System.IO.BinaryWriter` behavior on .NET.

**Do NOT write a fixed `int32` length prefix.** Getting this wrong silently corrupts every subsequent field in the file because all following reads will be misaligned.

Round-trip contract:
```
BinaryWriter.Write("hello")   →   BinaryReader.ReadString()  ==  "hello"
```

Encoding details:
- Length 0–127: 1 byte prefix
- Length 128–16383: 2 byte prefix (high bit of first byte set, continuation)
- Null/empty string: write `""` (1-byte `0x00` prefix); never write a null reference

Semantic strings that are absent in the HLSL source must be written as empty string `""`, not skipped.

---

## 4. D3D9 Render State Token Mapping

The FX9 pre-parser (Phase 2) emits render state key-value pairs using D3D9 token names. These must be mapped to MonoGame state fields before serialization.

### 4.1 RasterizerState

| D3D9 Token | MonoGame Field | Value mapping |
|---|---|---|
| `CullMode = None` | `RasterizerState.CullMode` | `CullMode.None` |
| `CullMode = CW` | `RasterizerState.CullMode` | `CullMode.CullClockwiseFace` |
| `CullMode = CCW` | `RasterizerState.CullMode` | `CullMode.CullCounterClockwiseFace` |
| `FillMode = Solid` | `RasterizerState.FillMode` | `FillMode.Solid` |
| `FillMode = Wireframe` | `RasterizerState.FillMode` | `FillMode.WireFrame` |
| `ScissorTestEnable` | `RasterizerState.ScissorTestEnable` | bool |
| `MultiSampleAntiAlias` | `RasterizerState.MultiSampleAntiAlias` | bool |
| `DepthBias` | `RasterizerState.DepthBias` | float (bit-cast to int32) |
| `SlopeScaleDepthBias` | `RasterizerState.SlopeScaleDepthBias` | float (bit-cast to int32) |

### 4.2 BlendState

| D3D9 Token | MonoGame Field | Value mapping |
|---|---|---|
| `AlphaBlendEnable = True` | BlendState enabled | enables non-opaque blend |
| `SrcBlend` | `BlendState.ColorSourceBlend` | `Blend` enum |
| `DestBlend` | `BlendState.ColorDestinationBlend` | `Blend` enum |
| `BlendOp` | `BlendState.ColorBlendFunction` | `BlendFunction` enum |
| `SrcBlendAlpha` | `BlendState.AlphaSourceBlend` | `Blend` enum |
| `DestBlendAlpha` | `BlendState.AlphaDestinationBlend` | `Blend` enum |
| `BlendOpAlpha` | `BlendState.AlphaBlendFunction` | `BlendFunction` enum |
| `ColorWriteEnable` | `BlendState.ColorWriteChannels` | `ColorWriteChannels` flags |

D3D9 `Blend` token-to-`Blend` enum mapping:

| D3D9 | MonoGame `Blend` |
|---|---|
| `Zero` | `Zero` |
| `One` | `One` |
| `SrcColor` | `SourceColor` |
| `InvSrcColor` | `InverseSourceColor` |
| `SrcAlpha` | `SourceAlpha` |
| `InvSrcAlpha` | `InverseSourceAlpha` |
| `DestAlpha` | `DestinationAlpha` |
| `InvDestAlpha` | `InverseDestinationAlpha` |
| `DestColor` | `DestinationColor` |
| `InvDestColor` | `InverseDestinationColor` |
| `SrcAlphaSat` | `SourceAlphaSaturation` |
| `BlendFactor` | `BlendFactor` |
| `InvBlendFactor` | `InverseBlendFactor` |

### 4.3 DepthStencilState

| D3D9 Token | MonoGame Field | Value mapping |
|---|---|---|
| `ZEnable = True` | `DepthStencilState.DepthBufferEnable` | `true` |
| `ZEnable = False` | `DepthStencilState.DepthBufferEnable` | `false` |
| `ZWriteEnable` | `DepthStencilState.DepthBufferWriteEnable` | bool |
| `ZFunc` | `DepthStencilState.DepthBufferFunction` | `CompareFunction` enum |
| `StencilEnable` | `DepthStencilState.StencilEnable` | bool |
| `StencilRef` | `DepthStencilState.ReferenceStencil` | int32 |
| `StencilMask` | `DepthStencilState.StencilMask` | int32 |
| `StencilWriteMask` | `DepthStencilState.StencilWriteMask` | int32 |
| `StencilFail` | `DepthStencilState.StencilFail` | `StencilOperation` enum |
| `StencilZFail` | `DepthStencilState.StencilDepthBufferFail` | `StencilOperation` enum |
| `StencilPass` | `DepthStencilState.StencilPass` | `StencilOperation` enum |
| `StencilFunc` | `DepthStencilState.StencilFunction` | `CompareFunction` enum |

D3D9 `ZFunc` token-to-`CompareFunction` mapping:

| D3D9 | MonoGame `CompareFunction` |
|---|---|
| `Never` | `Never` |
| `Less` | `Less` |
| `Equal` | `Equal` |
| `LessEqual` | `LessEqual` |
| `Greater` | `Greater` |
| `NotEqual` | `NotEqual` |
| `GreaterEqual` | `GreaterEqual` |
| `Always` | `Always` |

---

## 5. Technique Ordering (Critical)

`BasicEffect.fx` and other built-in MonoGame effects select techniques by **index** from C# code, not by name. The technique ordering in the source `.fx` file must be preserved exactly in the serialized output.

- Do **not** sort techniques alphabetically.
- Do **not** deduplicate or reorder techniques based on any reflection output.
- The writer takes technique order directly from `ShaderIR.Techniques`, which is already ordered by Phase 2's parser in source-file order.
- Tests must assert that the first technique written is the one that appeared first in the source file (see Section 9).

---

## 6. New Types and Files

All new types live in `ShadowDusk.Core` unless noted.

### 6.1 `MgfxProfile` enum (`MgfxProfile.cs`)

```csharp
// src/ShadowDusk.Core/MgfxProfile.cs
#nullable enable
namespace ShadowDusk.Core;

// IMPORTANT: MgfxProfile byte values are NOT the same as PlatformTarget ordinals.
// PlatformTarget.DirectX=0, PlatformTarget.OpenGL=1
// MgfxProfile.OpenGL=0,     MgfxProfile.DirectX=1
// Always use MgfxProfile values when writing the binary format profile byte.
public enum MgfxProfile : byte
{
    OpenGL     = 0,
    DirectX11  = 1,
    Vulkan     = 3,
}
```

### 6.2 `MgfxWriterOptions` record (`MgfxWriterOptions.cs`)

```csharp
// src/ShadowDusk.Core/MgfxWriterOptions.cs
#nullable enable
namespace ShadowDusk.Core;

public sealed record MgfxWriterOptions(
    MgfxProfile Profile,
    byte        MgfxVersion = 10
);
```

### 6.3 `RenderStateBlock` record (`RenderStateBlock.cs`)

```csharp
// src/ShadowDusk.Core/RenderStateBlock.cs
#nullable enable
namespace ShadowDusk.Core;

/// <summary>
/// Parsed render state for a single effect pass.
/// All fields are nullable; null means "not specified in source" (runtime uses its default).
/// </summary>
public sealed record RenderStateBlock
{
    // Rasterizer
    public CullModeValue?        CullMode                  { get; init; }
    public FillModeValue?        FillMode                  { get; init; }
    public bool?                 ScissorTestEnable         { get; init; }
    public bool?                 MultiSampleAntiAlias      { get; init; }
    public float?                DepthBias                 { get; init; }
    public float?                SlopeScaleDepthBias       { get; init; }

    // Blend
    public bool?                 AlphaBlendEnable          { get; init; }
    public BlendValue?           ColorSourceBlend          { get; init; }
    public BlendValue?           ColorDestinationBlend     { get; init; }
    public BlendFunctionValue?   ColorBlendFunction        { get; init; }
    public BlendValue?           AlphaSourceBlend          { get; init; }
    public BlendValue?           AlphaDestinationBlend     { get; init; }
    public BlendFunctionValue?   AlphaBlendFunction        { get; init; }
    public int?                  ColorWriteChannels        { get; init; }

    // Depth/Stencil
    public bool?                 DepthBufferEnable         { get; init; }
    public bool?                 DepthBufferWriteEnable    { get; init; }
    public CompareFunctionValue? DepthBufferFunction       { get; init; }
    public bool?                 StencilEnable             { get; init; }
    public int?                  ReferenceStencil          { get; init; }
    public int?                  StencilMask               { get; init; }
    public int?                  StencilWriteMask          { get; init; }
    public StencilOperationValue? StencilFail              { get; init; }
    public StencilOperationValue? StencilDepthBufferFail   { get; init; }
    public StencilOperationValue? StencilPass              { get; init; }
    public CompareFunctionValue? StencilFunction           { get; init; }
}

// Mirroring MonoGame enum values as value-type wrappers to avoid a hard MonoGame assembly reference in Core.
public enum CullModeValue      : int { None = 1, CullClockwiseFace = 2, CullCounterClockwiseFace = 3 }
public enum FillModeValue      : int { Solid = 0, WireFrame = 1 }
public enum BlendValue         : int { One = 1, Zero = 0, SourceColor = 2, InverseSourceColor = 3, SourceAlpha = 4,
                                       InverseSourceAlpha = 5, DestinationAlpha = 6, InverseDestinationAlpha = 7,
                                       DestinationColor = 8, InverseDestinationColor = 9, SourceAlphaSaturation = 10,
                                       BlendFactor = 11, InverseBlendFactor = 12 }
public enum BlendFunctionValue : int { Add = 0, Subtract = 1, ReverseSubtract = 2, Min = 3, Max = 4 }
public enum CompareFunctionValue : int { Always = 0, Never = 1, Less = 2, LessEqual = 3, Equal = 4,
                                          GreaterEqual = 5, Greater = 6, NotEqual = 7 }
public enum StencilOperationValue : int { Keep = 0, Zero = 1, Replace = 2, Increment = 3, Decrement = 4,
                                           IncrementSaturation = 5, DecrementSaturation = 6, Invert = 7 }
```

> **IMPORTANT: MonoGame 3.8.2 enum ordinal verification required.** The integer values in `CullModeValue`, `BlendValue`, etc. MUST match what MonoGame's `BinaryReader`-based effect loader expects in the MGFX binary. Verify each enum value against `MonoGame.Framework/Graphics/Effect/EffectPass.cs` and related files in the MonoGame 3.8.2 source tree. Key values to verify: `CullMode.None` (MonoGame uses 1, not 0, because it mirrors D3D9), `FillMode.Solid = 0`, blend factors. Getting these wrong causes silent rendering corruption.

### 6.4 Extend `ShaderIR` with writer-facing fields

Add the following properties to `ShaderIR` (Phase 1 stub) — leave as stubs now if `ShaderIR` is still a skeleton; populate them from Phase 2/5 outputs:

| Property | Type | Notes |
|---|---|---|
| `ConstantBuffers` | `IReadOnlyList<ConstantBufferInfo>` | Phase 5 provides these |
| `Shaders` | `IReadOnlyList<CompiledShaderBlob>` | Phase 4/6 provide blobs |
| `Parameters` | `IReadOnlyList<EffectParameterInfo>` | Phase 5 provides these |
| `Techniques` | `IReadOnlyList<MgfxTechniqueInfo>` | Phase 2 provides these, in source order |

### 6.5 `MgfxWriter` class (`MgfxWriter.cs`)

```csharp
// src/ShadowDusk.Core/MgfxWriter.cs
#nullable enable
using System.IO;
namespace ShadowDusk.Core;

public sealed class MgfxWriter
{
    private const uint   MgfxSignature = 0x4D474658u;

    public Result<byte[], ShaderError> Write(ShaderIR ir, MgfxWriterOptions options)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        WriteHeader(bw, options);
        WriteConstantBuffers(bw, ir);
        WriteShaders(bw, ir);
        WriteParameters(bw, ir);
        WriteTechniques(bw, ir);

        bw.Flush();
        return Result<byte[], ShaderError>.Ok(ms.ToArray());
    }

    private static void WriteHeader(BinaryWriter bw, MgfxWriterOptions options)
    {
        bw.Write(MgfxSignature);             // 4 bytes, little-endian
        bw.Write(options.MgfxVersion);       // 1 byte
        bw.Write((byte)options.Profile);     // 1 byte
    }

    private static void WriteConstantBuffers(BinaryWriter bw, ShaderIR ir) { /* Section 7.1 */ }
    private static void WriteShaders(BinaryWriter bw, ShaderIR ir)          { /* Section 7.2 */ }
    private static void WriteParameters(BinaryWriter bw, ShaderIR ir)       { /* Section 7.3 */ }
    private static void WriteTechniques(BinaryWriter bw, ShaderIR ir)       { /* Section 7.4 */ }
}
```

> **Namespace note:** `MgfxWriter` lives directly in `ShadowDusk.Core` (no `Serialization` subdirectory). Phase 9's `MgfxBlobReader` must reference `src/ShadowDusk.Core/MgfxWriter.cs` — any reference to a `Serialization/` path is incorrect.

> **Prerequisite for Phase 8:** `ShaderError` has a single `Column` field (no `ColumnEnd`). Phase 8's `MgcbErrorFormatter` will emit `Column-Column` (zero-width range) for the MGCB error format. Adding a `ColumnEnd` field to `ShaderError` is a Phase 8 decision.

### 6.6 `RenderStateParser` class (`RenderStateParser.cs`)

```csharp
// src/ShadowDusk.Core/RenderStateParser.cs
#nullable enable
using System.Collections.Generic;
namespace ShadowDusk.Core;

/// <summary>
/// Maps D3D9-style render state key-value pairs (produced by the FX9 pre-parser)
/// into a typed <see cref="RenderStateBlock"/>.
/// </summary>
public sealed class RenderStateParser
{
    /// <summary>
    /// Parse a flat dictionary of D3D9 token names → string values into a
    /// <see cref="RenderStateBlock"/>. Unknown keys are silently ignored.
    /// </summary>
    public RenderStateBlock Parse(IReadOnlyDictionary<string, string> kvp)
    {
        // Implementation: Section 7.5
        throw new NotImplementedException();
    }
}
```

---

## 7. Implementation Tasks

### 7.1 WriteConstantBuffers

```
bw.Write((int) ir.ConstantBuffers.Count);
foreach cb in ir.ConstantBuffers:
    bw.Write(cb.Name);                           // 7-bit encoded string
    bw.Write((short) cb.SizeInBytes);
    bw.Write((int) cb.ParameterIndices.Count);
    foreach idx in cb.ParameterIndices:
        bw.Write((int) idx);
    foreach offset in cb.ParameterOffsets:
        bw.Write((ushort) offset);
```

### 7.2 WriteShaders

```
bw.Write((int) ir.Shaders.Count);
foreach blob in ir.Shaders:
    bw.Write((int) blob.Bytes.Length);
    bw.Write(blob.Bytes);
```

### 7.3 WriteParameters

```
bw.Write((int) ir.Parameters.Count);
foreach p in ir.Parameters:
    bw.Write((byte) p.Class);
    bw.Write((byte) p.Type);
    bw.Write(p.Name);                            // 7-bit encoded
    bw.Write(p.Semantic ?? "");                  // 7-bit encoded; never null
    WriteAnnotations(bw, p.Annotations);
    bw.Write((byte) p.RowCount);
    bw.Write((byte) p.ColumnCount);
    WriteInt32List(bw, p.MemberIndices);
    WriteInt32List(bw, p.ElementIndices);
```

`WriteInt32List`: write `int32` count, then each `int32` value.

### 7.4 WriteTechniques

```
bw.Write((int) ir.Techniques.Count);       // preserve source order — DO NOT sort
foreach tech in ir.Techniques:
    bw.Write(tech.Name);                   // 7-bit encoded
    WriteAnnotations(bw, tech.Annotations);
    bw.Write((int) tech.Passes.Count);
    foreach pass in tech.Passes:
        bw.Write(pass.Name);               // 7-bit encoded
        WriteAnnotations(bw, pass.Annotations);
        bw.Write((short) pass.VertexShaderIndex);  // -1 if absent
        bw.Write((short) pass.PixelShaderIndex);   // -1 if absent
        WriteRenderStateBlock(bw, pass.RenderState);
```

### 7.5 WriteRenderStateBlock

```
WriteOptionalStateObject(bw, block.HasBlendState,         () => WriteBlendState(bw, block));
WriteOptionalStateObject(bw, block.HasDepthStencilState,  () => WriteDepthStencilState(bw, block));
WriteOptionalStateObject(bw, block.HasRasterizerState,    () => WriteRasterizerState(bw, block));
```

`WriteOptionalStateObject`: write `byte 1` + delegate, or `byte 0` if absent.

Each state object writes individual fields as `(byte fieldId, int32 value)` key-value pairs, followed by a sentinel `byte 0xFF` to mark end-of-object. Only write fields whose value is non-null in `RenderStateBlock`.

### 7.6 WriteAnnotations

```
bw.Write((int) annotations.Count);
foreach ann in annotations:
    bw.Write(ann.Name);          // 7-bit encoded
    bw.Write((byte) ann.Type);
    switch ann.Type:
        Single:  bw.Write((float) ann.FloatValue);
        Int32:   bw.Write((int) ann.IntValue);
        Bool:    bw.Write((int)(ann.BoolValue ? 1 : 0));
        String:  bw.Write(ann.StringValue ?? "");
```

### 7.7 RenderStateParser implementation

1. Iterate the input `kvp` dictionary.
2. For each key, match case-insensitively against the D3D9 token table (Section 4).
3. Parse the string value to the corresponding enum or primitive.
4. Return an immutable `RenderStateBlock` built via `with` initializers.
5. On unrecognised key: ignore silently (log at `Trace` level if a logger is wired).
6. On unrecognised value for a known key: return `Result.Fail(new ShaderError(...))` with a descriptive message.

---

## 8. ShaderIR Extensions

The following supporting types are needed to hold the data `MgfxWriter` consumes. Add them to `ShadowDusk.Core`. Use stubs if Phase 5 is not yet complete.

> **Note:** Phase 5 produced `ConstantBufferReflection`, `ParameterReflection`, and `AnnotationReflection` in `ShadowDusk.Core.Reflection`. The writer-facing types below are **separate** from those reflection types — they contain the additional indexing data needed only for MGFX serialization (parameter indices, member indices, etc.). A mapping layer will translate `ReflectedEffect` → the writer types in Phase 8's `PipelineRunner`.
>
> Relationship summary:
> - `ConstantBufferInfo` maps from `ConstantBufferReflection` but adds `ParameterIndices` and `ParameterOffsets` (MGFX-specific indexing not present in the reflection type).
> - `EffectParameterInfo` maps from `ParameterReflection` but adds `MemberIndices` and `ElementIndices` arrays required for serialization.
> - `AnnotationInfo` maps from `AnnotationReflection`; keep both — one is for reflection metadata, one is for MGFX serialization.

### 8.1 `ConstantBufferInfo`

```csharp
public sealed record ConstantBufferInfo(
    string            Name,
    int               SizeInBytes,
    IReadOnlyList<int>    ParameterIndices,
    IReadOnlyList<ushort> ParameterOffsets
);
```

### 8.2 `CompiledShaderBlob`

> `ShaderStage` is defined in `ShadowDusk.Core` (Phase 1) — use `ShadowDusk.Core.ShaderStage` here.

```csharp
public sealed record CompiledShaderBlob(
    byte[]        Bytes,
    ShaderStage   Stage    // Vertex or Pixel — ShadowDusk.Core.ShaderStage from Phase 1
);
```

> `CompiledShaderBlob` is intentionally separate from `PlatformBlob` in `ShadowDusk.HLSL.Dxc`. `PlatformBlob` is DXC-specific and includes metadata needed for Phase 6 transpilation. `CompiledShaderBlob` is the writer-facing type that holds the final bytes after all transpilation is complete. `PipelineRunner` (Phase 8) is responsible for converting `PlatformBlob` → `CompiledShaderBlob`.

### 8.3 `EffectParameterInfo`

```csharp
public sealed record EffectParameterInfo(
    byte                          Class,
    byte                          Type,
    string                        Name,
    string?                       Semantic,
    IReadOnlyList<AnnotationInfo> Annotations,
    byte                          RowCount,
    byte                          ColumnCount,
    IReadOnlyList<int>            MemberIndices,
    IReadOnlyList<int>            ElementIndices
);
```

### 8.4 `AnnotationInfo`

```csharp
public sealed record AnnotationInfo(
    string  Name,
    byte    Type,           // EffectParameterType byte value
    string? StringValue,
    float?  FloatValue,
    int?    IntValue,
    bool?   BoolValue
);
```

### 8.5 `MgfxTechniqueInfo` and `MgfxPassInfo`

```csharp
public sealed record MgfxTechniqueInfo(
    string                        Name,
    IReadOnlyList<AnnotationInfo> Annotations,
    IReadOnlyList<MgfxPassInfo>   Passes          // source-file order
);

public sealed record MgfxPassInfo(
    string                        Name,
    IReadOnlyList<AnnotationInfo> Annotations,
    int                           VertexShaderIndex,   // -1 if absent
    int                           PixelShaderIndex,    // -1 if absent
    RenderStateBlock              RenderState
);
```

---

## 9. Unit Tests

All tests live in `tests/ShadowDusk.Core.Tests/MgfxWriterTests.cs`. All tests are pure (no disk, no process). Use the `BinaryReader` round-trip approach: write to `MemoryStream`, read back with `BinaryReader`, assert field values.

### 9.1 Header tests

- [ ] `Header_SignatureIsCorrect` — read bytes 0–3, assert `0x4D`, `0x47`, `0x46`, `0x58` (little-endian "MGFX").
- [ ] `Header_DefaultVersionIs10` — byte 4 == `10`.
- [ ] `Header_Version11WhenRequested` — pass `MgfxWriterOptions` with `MgfxVersion = 11`, assert byte 4 == `11`.
- [ ] `Header_OpenGlProfileId` — byte 5 == `0` for `MgfxProfile.OpenGL`.
- [ ] `Header_DirectX11ProfileId` — byte 5 == `1` for `MgfxProfile.DirectX11`.
- [ ] `Header_VulkanProfileId` — byte 5 == `3` for `MgfxProfile.Vulkan`.

### 9.2 String encoding tests

- [ ] `StringEncoding_EmptyString` — write a parameter with `Name = ""`, read back with `BinaryReader.ReadString()`, assert empty.
- [ ] `StringEncoding_ShortString` — name `"World"` (5 chars), assert length prefix is single byte `0x05`.
- [ ] `StringEncoding_LongString` — name of 200 chars, assert first prefix byte has high bit set (two-byte encoding).
- [ ] `StringEncoding_Roundtrip` — write 20 distinct strings, read all back, assert all equal originals.
- [ ] `StringEncoding_SemanticAbsent` — parameter with `Semantic = null`, assert read-back string is `""`.

### 9.3 Technique ordering tests

- [ ] `TechniqueOrder_PreservedFromIR` — IR with 3 techniques named `"C"`, `"A"`, `"B"` (in that order); written file reads back `"C"`, `"A"`, `"B"` in order.
- [ ] `TechniqueOrder_SingleTechnique` — IR with 1 technique; reads back correctly.
- [ ] `TechniqueOrder_PassCountPerTechnique` — 2 techniques with 3 and 1 pass respectively; assert counts.

### 9.4 Shader blob tests

- [ ] `ShaderBlob_LengthPrefixCorrect` — blob of 64 bytes; reader reads int32 == `64`, then 64 bytes.
- [ ] `ShaderBlob_ZeroBlobs` — IR with 0 shaders; reads back int32 == `0`.
- [ ] `ShaderBlob_MultipleBlobs` — 3 blobs of distinct sizes; all lengths and content correct.

### 9.5 Constant buffer tests

- [ ] `ConstantBuffer_ZeroBuffers` — reads back int32 == `0`.
- [ ] `ConstantBuffer_SingleBuffer` — name, size, two parameter indices, two offsets; all fields round-trip.
- [ ] `ConstantBuffer_ParameterOffsets_AreUInt16` — offset value `65000` (> `int16` max) round-trips correctly.

### 9.6 Render state tests

- [ ] `RenderState_NotPresent_WritesFlagZero` — pass with no render state; all three presence bytes read as `0`.
- [ ] `RenderState_CullModeNone` — D3D9 `CullMode = None` maps to `CullModeValue.None`; presence byte is `1`.
- [ ] `RenderState_AlphaBlend_SourceDestFactors` — `SrcBlend = SrcAlpha`, `DestBlend = InvSrcAlpha` map correctly.
- [ ] `RenderState_DepthBufferEnable` — `ZEnable = True` maps to `DepthBufferEnable = true`.
- [ ] `RenderState_DepthFunction_LessEqual` — `ZFunc = LessEqual` maps to `CompareFunctionValue.LessEqual`.

### 9.7 Full minimal effect round-trip

- [ ] `MinimalEffect_SingleTechniqueSinglePass` — build a minimal `ShaderIR` with 1 technique, 1 pass, 1 vertex shader blob (8 bytes), 1 pixel shader blob (8 bytes), 1 parameter, 0 constant buffers; write it; read it back section by section and assert every field.

---

## 10. Numbered Task Checklist

Execute these steps in order. Each step is independently verifiable.

1. - [ ] Add `MgfxProfile.cs` to `ShadowDusk.Core` (Section 6.1).
2. - [ ] Add `MgfxWriterOptions.cs` to `ShadowDusk.Core` (Section 6.2).
3. - [ ] Add `RenderStateBlock.cs` with all render-state enum types to `ShadowDusk.Core` (Section 6.3). Verify enum int values against MonoGame source.
4. - [ ] Add `ConstantBufferInfo.cs`, `CompiledShaderBlob.cs`, `EffectParameterInfo.cs`, `AnnotationInfo.cs`, `MgfxTechniqueInfo.cs`, `MgfxPassInfo.cs` to `ShadowDusk.Core` (Section 8).
5. - [ ] Extend `ShaderIR.cs` with `ConstantBuffers`, `Shaders`, `Parameters`, `Techniques` properties (Section 6.4).
6. - [ ] Add `MgfxWriter.cs` skeleton with all `Write*` methods as `NotImplementedException` stubs (Section 6.5).
6b. - [ ] Add `ShaderIRBuilder` static class (or mapper method) that accepts `FxParseResult`, `ReflectedEffect`, and `IReadOnlyList<CompiledShaderBlob>` and returns a populated `ShaderIR`. Can be a stub that Phase 8's `PipelineRunner` calls. This is the bridge between Phase 5/6 outputs and Phase 7 writer inputs.
7. - [ ] Add `RenderStateParser.cs` skeleton (Section 6.6).
8. - [ ] Implement `WriteHeader` in `MgfxWriter` (Section 7). Run `Header_*` tests — all should pass.
9. - [ ] Implement `WriteConstantBuffers` (Section 7.1). Run `ConstantBuffer_*` tests.
10. - [ ] Implement `WriteShaders` (Section 7.2). Run `ShaderBlob_*` tests.
11. - [ ] Implement `WriteAnnotations` helper (Section 7.6).
12. - [ ] Implement `WriteParameters` (Section 7.3). Run `StringEncoding_*` tests.
13. - [ ] Implement `WriteTechniques` and `WriteRenderStateBlock` (Sections 7.4, 7.5). Run `TechniqueOrder_*` and `RenderState_*` tests.
14. - [ ] Implement `RenderStateParser.Parse` with the full D3D9 token mapping table (Sections 4, 7.7). Run `RenderState_*` tests.
15. - [ ] Write and pass `MinimalEffect_SingleTechniqueSinglePass` round-trip test (Section 9.7).
16. - [ ] Run `dotnet build --configuration Release` — 0 errors, 0 warnings.
17. - [ ] Run `dotnet test --filter "FullyQualifiedName~MgfxWriterTests"` — all pass.
18. - [ ] Commit: `git commit -m "Phase 7: MgfxWriter — MGFX binary serializer"`.

---

## 11. Acceptance Criteria

| Criterion | How to verify |
|---|---|
| `.mgfx` output loadable by MonoGame | `new Effect(graphicsDevice, File.ReadAllBytes(path))` does not throw; covered in Phase 9 integration tests |
| Default `MGFXVersion` is `10` | `Header_DefaultVersionIs10` unit test |
| `MGFXVersion 11` is opt-in | `Header_Version11WhenRequested` unit test |
| Technique order from source preserved | `TechniqueOrder_PreservedFromIR` unit test |
| All strings use 7-bit variable-length encoding | `StringEncoding_Roundtrip` + `StringEncoding_LongString` unit tests |
| Render states serialized per pass | `RenderState_*` unit tests |
| D3D9 token mapping complete for common tokens | `RenderStateParser` tests; cross-check against MonoGame's own FX parser |
| `Result<byte[], ShaderError>` returned (no exceptions) | Writer returns `Result.Ok(bytes)` on success; `Result.Fail(error)` on bad render-state token |
| `dotnet build` green with 0 warnings | `dotnet build -warnaserror` exits 0 |
| All unit tests green | `dotnet test --filter "FullyQualifiedName~MgfxWriterTests"` shows 0 failures |

---

## 12. Known Gaps (deferred to later phases)

| Gap | Phase |
|---|---|
| CLI flag `--mgfx-version` wired to `MgfxWriterOptions.MgfxVersion` | 8 |
| End-to-end `.fx` → `.mgfx` round-trip loaded in a real MonoGame `GraphicsDevice` | 9 |
| Metal profile byte and MSL blob format | Post-Phase 6 |
| Geometry / compute shader stages | Post-Phase 7 |
| Effect parameter default value serialization | Post-Phase 7 |
| Full MGCB plugin integration | Post-Phase 8 |
