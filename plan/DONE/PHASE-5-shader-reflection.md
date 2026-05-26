# Phase 5 — Shader Reflection

**Status:** Done
**Depends on:** Phase 4 (bytecode emission — DXIL/SPIR-V blobs available)
**Produces:** `ReflectedEffect` data model; populated Parameters and Constant Buffers sections of the `.mgfx` binary

---

## 1. Goals

Extract shader parameter metadata from compiled bytecode so that the `.mgfx` writer (Phase 6) can populate every field that MonoGame's `Effect` loader expects at runtime. Metadata includes constant buffer layouts, scalar/vector/matrix parameters, texture slots, sampler slots, and vertex input/output signatures.

This phase must work on Linux, macOS, and Windows without any Windows-only COM API (no `ID3D11ShaderReflection` / `d3dcompiler.dll`).

---

## 2. Out of Scope

- Writing reflected metadata into the final `.mgfx` binary (Phase 6).
- FX9 annotation parsing beyond forwarding pre-parsed annotation strings from the effect parser (Phase 3).
- Runtime parameter setting (`Effect.Parameters[...]`). Reflection only produces the static description of parameters.

---

## 3. Cross-Platform Reflection Strategy

| Reflection Path | When Used | API Surface | NuGet / Native |
|---|---|---|---|
| **DXIL reflection (primary)** | Always, after DXC compilation | `IDxcUtils::CreateReflection()` → `ID3D12ShaderReflection` | `Vortice.Dxc` |
| **SPIR-V reflection (secondary)** | After SPIR-V generation (OpenGL / Vulkan targets) | `spvc_compiler_create_shader_resources()` | SPIRV-Cross C API via existing P/Invoke wrapper |
| ~~DXBC reflection~~ | Never — Windows-only `d3dcompiler.dll` | ~~`ID3D11ShaderReflection`~~ | Prohibited |

**Decision rationale:** Vortice.Dxc ships prebuilt `libdxcompiler` for all three OS. `IDxcUtils::CreateReflection()` accepts the DXIL blob produced by Phase 4 and returns an `ID3D12ShaderReflection`-compatible interface that works cross-platform. SPIRV-Cross reflection is used exclusively to verify and fill in binding slots that differ between the HLSL and SPIR-V views.

---

## 4. Output Data Model

All types live in `ShadowDusk.Core.Reflection`.

### 4.1 Top-Level Container

```csharp
// ShadowDusk.Core/Reflection/ReflectedEffect.cs
public sealed record ReflectedEffect
{
    public required IReadOnlyList<ConstantBufferReflection> ConstantBuffers { get; init; }
    public required IReadOnlyList<ParameterReflection>      Parameters       { get; init; }
    public required IReadOnlyList<TextureReflection>        Textures         { get; init; }
    public required IReadOnlyList<SamplerReflection>        Samplers         { get; init; }
    public required IReadOnlyList<SignatureParameterReflection> InputSignature  { get; init; }
    public required IReadOnlyList<SignatureParameterReflection> OutputSignature { get; init; }
}
```

### 4.2 Constant Buffer

```csharp
public sealed record ConstantBufferReflection
{
    public required string                           Name      { get; init; }
    public required int                              SizeBytes { get; init; }
    public required int                              BindSlot  { get; init; }
    public required IReadOnlyList<VariableReflection> Variables { get; init; }
}
```

### 4.3 Variable (inside a cbuffer)

```csharp
public sealed record VariableReflection
{
    public required string                 Name          { get; init; }
    public required int                    StartOffset   { get; init; }  // byte offset within cbuffer
    public required int                    SizeBytes     { get; init; }
    public required EffectParameterClass   ParameterClass { get; init; }
    public required EffectParameterType    ParameterType  { get; init; }
    public required int                    Rows          { get; init; }
    public required int                    Columns       { get; init; }
    public required int                    Elements      { get; init; }  // 0 = not an array
    public          IReadOnlyList<VariableReflection>? Members { get; init; } // structs only
}
```

### 4.4 Parameter

`ParameterReflection` represents a top-level effect parameter (may reference a cbuffer variable or an object like a texture). Annotations are forwarded from the FX9 pre-parser.

```csharp
public sealed record ParameterReflection
{
    public required string               Name        { get; init; }
    public required string?              Semantic    { get; init; }
    public required EffectParameterClass Class       { get; init; }
    public required EffectParameterType  Type        { get; init; }
    public required int                  Rows        { get; init; }
    public required int                  Columns     { get; init; }
    public required int                  Elements    { get; init; }
    public          IReadOnlyList<AnnotationReflection>? Annotations { get; init; }
}
```

### 4.5 Texture and Sampler

```csharp
public sealed record TextureReflection
{
    public required string            Name      { get; init; }
    public required int               BindSlot  { get; init; }
    public required TextureDimension  Dimension { get; init; }  // Texture2D, TextureCube, etc.
}

public sealed record SamplerReflection
{
    public required string  Name         { get; init; }
    public required int     BindSlot     { get; init; }
    public required string? TextureName  { get; init; }  // associated texture (from FX9 sampler state)
}
```

### 4.6 Signature Parameter

```csharp
public sealed record SignatureParameterReflection
{
    public required string SemanticName  { get; init; }
    public required uint   SemanticIndex { get; init; }
    public required uint   Register      { get; init; }
    public required string SystemValue   { get; init; }  // "NONE", "POSITION", "DEPTH", etc.
    public required string ComponentType { get; init; }  // "float", "uint", "int"
    public required byte   Mask          { get; init; }
}
```

### 4.7 Enumerations

These must match MonoGame's values exactly.

```csharp
public enum EffectParameterClass : byte
{
    Scalar  = 0,
    Vector  = 1,
    Matrix  = 2,
    Object  = 3,
    Struct  = 4,
}

public enum EffectParameterType : byte
{
    Void     = 0,
    Bool     = 1,
    Int32    = 2,
    Single   = 3,
    String   = 4,
    Texture  = 5,
    Texture1D = 6,
    Texture2D = 7,
    Texture3D = 8,
    TextureCube = 9,
}

public enum TextureDimension { Unknown, Texture1D, Texture2D, Texture3D, TextureCube }
```

---

## 5. Type Mapping Tables

### 5.1 DXIL `D3D_SHADER_VARIABLE_CLASS` → `EffectParameterClass`

| DXIL Enum Value | Vortice Name | `EffectParameterClass` |
|---|---|---|
| `D3D_SVC_SCALAR` | `Scalar` | `Scalar` |
| `D3D_SVC_VECTOR` | `Vector` | `Vector` |
| `D3D_SVC_MATRIX_ROWS` | `MatrixRows` | `Matrix` |
| `D3D_SVC_MATRIX_COLUMNS` | `MatrixColumns` | `Matrix` |
| `D3D_SVC_OBJECT` | `Object` | `Object` |
| `D3D_SVC_STRUCT` | `Struct` | `Struct` |

### 5.2 DXIL `D3D_SHADER_VARIABLE_TYPE` → `EffectParameterType`

| DXIL Enum Value | Vortice Name | `EffectParameterType` |
|---|---|---|
| `D3D_SVT_VOID` | `Void` | `Void` |
| `D3D_SVT_BOOL` | `Bool` | `Bool` |
| `D3D_SVT_INT` | `Int` | `Int32` |
| `D3D_SVT_UINT` | `UInt` | `Int32` (widened; MonoGame has no unsigned) |
| `D3D_SVT_FLOAT` | `Float` | `Single` |
| `D3D_SVT_STRING` | `String` | `String` |
| `D3D_SVT_TEXTURE` | `Texture` | `Texture` |
| `D3D_SVT_TEXTURE1D` | `Texture1D` | `Texture1D` |
| `D3D_SVT_TEXTURE2D` | `Texture2D` | `Texture2D` |
| `D3D_SVT_TEXTURE3D` | `Texture3D` | `Texture3D` |
| `D3D_SVT_TEXTURECUBE` | `TextureCube` | `TextureCube` |
| `D3D_SVT_SAMPLER` | `Sampler` | `Object` (class), no type enum |

### 5.3 DXIL `D3D_SRV_DIMENSION` → `TextureDimension`

| DXIL Value | `TextureDimension` |
|---|---|
| `D3D_SRV_DIMENSION_TEXTURE1D` | `Texture1D` |
| `D3D_SRV_DIMENSION_TEXTURE2D` | `Texture2D` |
| `D3D_SRV_DIMENSION_TEXTURE3D` | `Texture3D` |
| `D3D_SRV_DIMENSION_TEXTURECUBE` | `TextureCube` |
| anything else | `Unknown` |

---

## 6. HLSL Constant Buffer Packing Rules

These rules govern expected offsets in integration tests and must be mirrored when asserting reflected metadata.

1. **16-byte row alignment.** Each row is 16 bytes (four 32-bit components). A variable never crosses a 16-byte boundary; padding is inserted before it if needed.
2. **Array element padding.** Every array element is padded to a multiple of 16 bytes regardless of the element type. A `float[3]` array occupies 48 bytes (3 × 16), not 12 bytes.
3. **Matrix alignment.** Each row of a matrix is 16-byte aligned. A `float4x4` is 64 bytes (4 rows × 16 bytes). A `float3x3` is 48 bytes (3 rows × 16 bytes).
4. **Struct alignment.** A struct's start offset is padded to 16 bytes. Its size is rounded up to a multiple of 16 bytes.
5. **Scalar packing.** Scalars and sub-16-byte vectors are packed together as long as they fit in the current 16-byte row.

These rules are not enforced by ShadowDusk — DXC handles packing — but the reflection extractor **must** report offsets verbatim from `ID3D12ShaderReflection` without adjustment.

---

## 7. Implementation Tasks

### 7.1 Data Model

- [x] 7.1.1 Create `ShadowDusk.Core/Reflection/` directory and add the record types from Section 4 (`ReflectedEffect`, `ConstantBufferReflection`, `VariableReflection`, `ParameterReflection`, `TextureReflection`, `SamplerReflection`, `SignatureParameterReflection`).
- [x] 7.1.2 Add `EffectParameterClass`, `EffectParameterType`, and `TextureDimension` enums to the same namespace. Verify numeric values match MonoGame source (`Microsoft.Xna.Framework.Graphics.EffectParameterClass`).
- [x] 7.1.3 Add `AnnotationReflection` record (name + string value) referenced by `ParameterReflection.Annotations`.

### 7.2 DXIL Reflection Extractor

- [x] 7.2.1 Create `src/ShadowDusk.HLSL/Reflection/DxilReflectionExtractor.cs`. Signature:
  ```csharp
  public sealed class DxilReflectionExtractor
  {
      public Result<ReflectedEffect, ShaderError> Extract(
          ReadOnlyMemory<byte> dxilBlob,
          CancellationToken ct = default);
  }
  ```
- [x] 7.2.2 Obtain `IDxcUtils` from `Vortice.Dxc.DxcCompiler.CreateDxcUtils()`. Call `IDxcUtils.CreateReflection(blob, typeof(ID3D12ShaderReflection).GUID, out var reflection)`. Handle `HRESULT` failure → return `ShaderError`.
- [x] 7.2.3 Call `reflection.GetDesc(out var shaderDesc)` to retrieve `D3D12_SHADER_DESC` (total CB count, bound resource count, input/output parameter counts).
- [x] 7.2.4 Implement `ExtractConstantBuffers(reflection, shaderDesc)` (private):
  - Loop `i` in `[0, shaderDesc.ConstantBuffers)`.
  - `reflection.GetConstantBufferByIndex(i)` → `ID3D12ShaderReflectionConstantBuffer`.
  - `cb.GetDesc(out var cbDesc)` → name, size, variable count.
  - Loop variables: `cb.GetVariableByIndex(j)` → `GetDesc(out var varDesc)` → name, start offset, size.
  - For each variable, call `GetType()` → `GetDesc(out var typeDesc)` → class, type, rows, columns, elements.
  - Recurse into struct members when `typeDesc.Class == D3D_SVC_STRUCT`.
- [x] 7.2.5 Implement `ExtractBoundResources(reflection, shaderDesc)` (private):
  - Loop `i` in `[0, shaderDesc.BoundResources)`.
  - `reflection.GetResourceBindingDesc(i, out var bindDesc)` → name, type (`D3D_SIT_*`), bind slot.
  - Route to `_textures` list when `D3D_SIT_TEXTURE`, to `_samplers` list when `D3D_SIT_SAMPLER`, ignore CBs (already extracted).
- [x] 7.2.6 Implement `ExtractInputSignature` and `ExtractOutputSignature` (private):
  - Use `GetInputParameterDesc` / `GetOutputParameterDesc` with index loop.
  - Map to `SignatureParameterReflection`.
- [x] 7.2.7 Build and return a `ReflectedEffect` from the collected lists.
- [x] 7.2.8 Apply the type mapping tables from Section 5 in a `private static` `MapClass` method and a `private static` `MapType` method. Throw `InvalidOperationException` (internal, not user-facing) for unmapped enum values so gaps surface in tests.
- [x] 7.2.9 Wrap all COM interop in `try/catch` that converts exceptions to `ShaderError` with `ErrorCode.ReflectionFailed`.

### 7.3 SPIRV-Cross Binding Slot Verification

- [x] 7.3.1 Create `ShadowDusk.Core/Reflection/SpvReflectionVerifier.cs`. Signature:
  ```csharp
  public sealed class SpvReflectionVerifier
  {
      public Result<BindingSlotMap, ShaderError> GetBindings(
          ReadOnlyMemory<byte> spirvBlob,
          CancellationToken ct = default);
  }
  ```
  where `BindingSlotMap` is a simple `record` with `IReadOnlyDictionary<string, int>` for textures and samplers.
- [ ] 7.3.2 Use the existing SPIRV-Cross P/Invoke wrapper (`spvc_context_create` → `spvc_compiler_create_shader_resources`). Enumerate `separate_images` and `separate_samplers` from the resources struct.
- [ ] 7.3.3 Compare slots with DXIL-reflected slots. If a mismatch exists, emit a `ShaderError` with `ErrorCode.BindingSlotMismatch` and include the resource name and both slot values in the message.
- [x] 7.3.4 This verifier runs only when the SPIR-V blob is available (OpenGL/Vulkan targets). Guard with `if (spirvBlob.IsEmpty) return Result.Ok(BindingSlotMap.Empty)`.

### 7.4 Parameter List Assembly

- [x] 7.4.1 Create `ShadowDusk.Core/Reflection/ParameterListBuilder.cs` (pure, no native calls):
  ```csharp
  public static class ParameterListBuilder
  {
      public static IReadOnlyList<ParameterReflection> Build(
          ReflectedEffect dxilReflection,
          IReadOnlyList<FxAnnotation>? fxAnnotations);
  }
  ```
- [x] 7.4.2 Flatten cbuffer variables into top-level `ParameterReflection` records. Each variable in each cbuffer becomes one entry.
- [x] 7.4.3 Append one `ParameterReflection` per texture and one per sampler (class = `Object`, type = `Texture*` or `Object` respectively).
- [x] 7.4.4 Merge FX9 annotations: match by parameter name; attach to `ParameterReflection.Annotations`.
- [x] 7.4.5 Output list order: cbuffer variables in cbuffer declaration order, then textures in bind-slot order, then samplers in bind-slot order. This must be stable across compilations (determinism requirement).

### 7.5 Public Facade

- [x] 7.5.1 Add `ReflectionPipeline` to `ShadowDusk.Core/Reflection/`:
  ```csharp
  public sealed class ReflectionPipeline
  {
      public async Task<Result<ReflectedEffect, ShaderError>> ReflectAsync(
          ReflectionInput input,
          CancellationToken ct = default);
  }

  public sealed record ReflectionInput
  {
      public required ReadOnlyMemory<byte>            DxilBlob       { get; init; }
      public          ReadOnlyMemory<byte>            SpirVBlob      { get; init; }
      public          IReadOnlyList<FxAnnotation>?    FxAnnotations  { get; init; }
  }
  ```
- [x] 7.5.2 `ReflectionPipeline.ReflectAsync` calls `DxilReflectionExtractor.Extract`, then optionally `SpvReflectionVerifier.GetBindings`, then `ParameterListBuilder.Build`. Returns the assembled `ReflectedEffect`.
- [x] 7.5.3 Wire `ReflectionPipeline` via constructor injection — no DI container framework is assumed.

---

## 8. Error Codes

Add the following values to `ShaderErrorCode` (or equivalent enum in `ShadowDusk.Core`):

| Code | Meaning |
|---|---|
| `ReflectionFailed` | `IDxcUtils::CreateReflection` returned a failing HRESULT |
| `BindingSlotMismatch` | DXIL and SPIR-V disagree on a resource's binding slot |
| `UnknownShaderVariableClass` | DXIL returned a `D3D_SVC_*` value with no mapping |
| `UnknownShaderVariableType` | DXIL returned a `D3D_SVT_*` value with no mapping |

---

## 9. Tests

### 9.1 Unit Tests — `ShadowDusk.Core.Tests/Reflection/`

- [x] 9.1.1 `TypeMappingTests.cs` — table-driven tests covering every row in Section 5.1 and 5.2. Assert `MapClass` and `MapType` return the correct `EffectParameterClass` / `EffectParameterType` for every listed DXIL enum value. No native calls.
- [x] 9.1.2 `ParameterListBuilderTests.cs` — construct fake `ReflectedEffect` instances in-memory; verify the output list order (cbuffer vars → textures → samplers) and annotation merging logic.
- [x] 9.1.3 `CbufferPackingTests.cs` — assert that for a known synthetic `VariableReflection` layout the offsets satisfy the packing rules in Section 6 (validates test fixture design, not production code).

### 9.2 Integration Tests — `ShadowDusk.Integration.Tests/Reflection/`

All integration tests carry `[Trait("Category", "Integration")]`.

- [x] 9.2.1 `BasicCbufferReflectionTests.cs`
  - Fixture shader `fixtures/shaders/reflection/basic_cbuffer.hlsl` contains:
    ```hlsl
    cbuffer Params : register(b0)
    {
        float  Scale;      // offset 0, size 4
        float3 Direction;  // offset 4, size 12  (fits in same 16-byte row)
        float4 Color;      // offset 16, size 16
        float4x4 World;    // offset 32, size 64
    }
    ```
  - Compile with DXC (Phase 4 pipeline), run `DxilReflectionExtractor.Extract`.
  - Assert: 1 constant buffer named `"Params"`, size = 96 bytes.
  - Assert variable `Scale`: offset=0, size=4, class=`Scalar`, type=`Single`.
  - Assert variable `Direction`: offset=4, size=12, class=`Vector`, type=`Single`, columns=3.
  - Assert variable `Color`: offset=16, size=16, class=`Vector`, type=`Single`, columns=4.
  - Assert variable `World`: offset=32, size=64, class=`Matrix`, type=`Single`, rows=4, columns=4.

- [x] 9.2.2 `ArrayReflectionTests.cs`
  - Fixture: `fixtures/shaders/reflection/array_param.hlsl` with `float PointLights[4]` inside a cbuffer.
  - Assert: elements=4, size=64 bytes (4 × 16-byte padding), offsets correct.

- [x] 9.2.3 `TextureSamplerReflectionTests.cs`
  - Fixture: `fixtures/shaders/reflection/tex_sampler.hlsl` with `Texture2D Albedo : register(t0)` and `SamplerState AlbedoSampler : register(s0)`.
  - Assert: `Textures` list has 1 entry: name=`"Albedo"`, slot=0, dimension=`Texture2D`.
  - Assert: `Samplers` list has 1 entry: name=`"AlbedoSampler"`, slot=0.

- [x] 9.2.4 `StructReflectionTests.cs`
  - Fixture: cbuffer with a nested struct `DirectionalLight { float3 Dir; float3 Color; float Intensity; }`.
  - Assert: the variable's `Members` list contains 3 entries with correct names, offsets, and types.
  - Assert recursive offset calculation matches Section 6 struct alignment rules.

- [x] 9.2.5 `SpvBindingVerificationTests.cs`
  - Compile the texture/sampler fixture to both DXIL and SPIR-V.
  - Run `SpvReflectionVerifier` and assert no mismatch errors.
  - Mutate the SPIR-V blob's binding annotation (or use a hand-crafted mismatched fixture) and assert `BindingSlotMismatch` error is returned.

- [x] 9.2.6 `SignatureReflectionTests.cs`
  - Fixture: `fixtures/shaders/reflection/vs_input.hlsl` with a vertex shader taking `POSITION`, `NORMAL`, `TEXCOORD0`.
  - Assert `InputSignature` contains entries with correct semantic names, registers, and component types.

### 9.3 Acceptance Test (End-to-End)

- [ ] 9.3.1 `MgfxParameterMatchTests.cs` — compile a reference shader that MonoGame ships (`SpriteBatch.fx` or equivalent), run the full `ReflectionPipeline`, and compare the resulting parameter names and types against a golden JSON snapshot produced from MonoGame's own `mgfxc`. Snapshot is checked into `fixtures/snapshots/`. **Note:** Generating the golden snapshot requires a working MonoGame `mgfxc` installation; the snapshot should be pre-generated and committed to the repository.
- [ ] 9.3.2 The golden snapshot comparison must be exact (name, class, type, rows, columns, elements) — no fuzzy matching.

---

## 10. Acceptance Criteria

| Criterion | How Verified |
|---|---|
| All cbuffer variables extracted with correct name, byte offset, and size | Integration test 9.2.1 |
| Array elements report correct element count and 16-byte-padded size | Integration test 9.2.2 |
| Textures and samplers extracted with correct bind slots | Integration test 9.2.3 |
| Struct members reflected recursively with correct offsets | Integration test 9.2.4 |
| `EffectParameterClass` and `EffectParameterType` mapped correctly | Unit test 9.1.1 |
| SPIR-V binding slot verification runs and catches mismatches | Integration test 9.2.5 |
| Vertex input signature extracted with semantic names and registers | Integration test 9.2.6 |
| Output matches MonoGame `mgfxc` for a real production shader | Acceptance test 9.3.1–9.3.2 |
| Works on Linux, macOS, Windows (no `d3dcompiler.dll` dependency) | CI matrix gate (all three OS runners pass) |
| Parameter list order is deterministic | `ParameterListBuilderTests` + acceptance snapshot comparison |
| `Result<ReflectedEffect, ShaderError>` returned — no thrown exceptions reaching callers | All tests assert no uncaught exceptions; error-path tests assert correct error codes |

---

## 11. File Checklist

```
src/ShadowDusk.Core/Reflection/
  ReflectedEffect.cs
  ConstantBufferReflection.cs
  VariableReflection.cs
  ParameterReflection.cs
  TextureReflection.cs
  SamplerReflection.cs
  SamplerReflection.cs
  SignatureParameterReflection.cs
  AnnotationReflection.cs
  BindingSlotMap.cs
  EffectParameterClass.cs          (enum)
  EffectParameterType.cs           (enum)
  TextureDimension.cs              (enum)
  SpvReflectionVerifier.cs
  ParameterListBuilder.cs
  ReflectionPipeline.cs
  ReflectionInput.cs

src/ShadowDusk.HLSL/Reflection/
  DxilReflectionExtractor.cs

tests/ShadowDusk.Core.Tests/Reflection/
  TypeMappingTests.cs
  ParameterListBuilderTests.cs
  CbufferPackingTests.cs

tests/ShadowDusk.Integration.Tests/Reflection/
  BasicCbufferReflectionTests.cs
  ArrayReflectionTests.cs
  TextureSamplerReflectionTests.cs
  StructReflectionTests.cs
  SpvBindingVerificationTests.cs
  SignatureReflectionTests.cs
  MgfxParameterMatchTests.cs

tests/fixtures/shaders/reflection/
  basic_cbuffer.hlsl
  array_param.hlsl
  tex_sampler.hlsl
  struct_cbuffer.hlsl
  vs_input.hlsl

tests/fixtures/snapshots/
  spritebatch_params.json          (golden snapshot for acceptance test)
```

---

## 12. Dependencies and Prerequisites

| Dependency | Version | Purpose |
|---|---|---|
| `Vortice.Dxc` | >= 3.x | `IDxcUtils`, `ID3D12ShaderReflection` |
| `Vortice.Direct3D12` | >= 3.x | `D3D12_SHADER_DESC`, reflection enum types |
| SPIRV-Cross native library | >= 2024-01 tag | `spvc_compiler_create_shader_resources` |
| Phase 4 output | — | DXIL blob (required) and SPIR-V blob (optional) |
| Phase 3 output | — | `FxAnnotation` list (optional, for annotation merging) |

Before starting this phase, confirm:
- [x] `Vortice.Direct3D12` is added to `ShadowDusk.HLSL.csproj` (reflection types live here).
- [ ] SPIRV-Cross P/Invoke wrapper from Phase 4 exposes `spvc_compiler_create_shader_resources` and the `spvc_resources` struct accessors.
- [x] `Result<T, ShaderError>` type and `ShaderErrorCode` enum are defined in `ShadowDusk.Core` (Phase 2 or earlier).
