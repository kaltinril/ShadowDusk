# Phase 8 — `ShadowDusk.Compiler` Library NuGet Package

**Depends on:** Phases 1–7 (all complete)  
**Consumed by:** Phase 9 (CLI), Phase 10 (WASM), Phase 11 (integration tests), Phase 12 (CI)

---

## Goal

Create the deliverable NuGet library that downstream consumers reference to compile `.fx` shaders.
This phase introduces a new `ShadowDusk.Compiler` project that acts as the composition root for
phases 2–7 — wiring `FxPreParser`, `Preprocessor`, `DxcShaderCompiler`, `ReflectionPipeline`,
`SpirvCrossGlslTranspiler`, and `MgfxWriter` into a single coherent `IShaderCompiler` implementation.

After this phase:
- Any project can add a NuGet reference to `ShadowDusk.Compiler` and call one method to get
  `.mgfx` bytes back from `.fx` source.
- The CLI (Phase 9) and WASM library (Phase 10) both thin-wrap this package rather than
  reimplementing the pipeline themselves.
- `IShaderCompiler` is fully implemented for the native (non-WASM) target.

---

## New Project: `ShadowDusk.Compiler`

### Location

```
src/ShadowDusk.Compiler/
├── ShadowDusk.Compiler.csproj
├── EffectCompiler.cs              ← public IShaderCompiler implementation
└── Internal/
    ├── CompilationPipeline.cs     ← orchestrates phases 2–7
    ├── PassCompilationResult.cs   ← per-pass intermediate result
    └── ShaderIRBuilder.cs         ← assembles ShaderIR from pass results
```

### `ShadowDusk.Compiler.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Compiler</AssemblyName>
    <RootNamespace>ShadowDusk.Compiler</RootNamespace>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- NuGet package -->
    <IsPackable>true</IsPackable>
    <PackageId>ShadowDusk.Compiler</PackageId>
    <PackageVersion>0.1.0</PackageVersion>
    <Description>
      Cross-platform HLSL Effect compiler for MonoGame and KNI.
      Compiles .fx shaders to .mgfx binaries for DirectX, OpenGL, and Vulkan targets
      without requiring Wine, the Windows SDK, or fxc.exe.
    </Description>
    <PackageTags>monogame kni hlsl shader compiler mgfx cross-platform</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
    <ProjectReference Include="..\ShadowDusk.HLSL\ShadowDusk.HLSL.csproj" />
    <ProjectReference Include="..\ShadowDusk.GLSL\ShadowDusk.GLSL.csproj" />
  </ItemGroup>
</Project>
```

Metal (`ShadowDusk.Metal`) is intentionally excluded until the MSL transpilation path is complete.

---

## Public API Surface

The only types a consumer needs to import are in `ShadowDusk.Compiler` and `ShadowDusk.Core`.

### `EffectCompiler`

```csharp
// src/ShadowDusk.Compiler/EffectCompiler.cs
#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Compiler;

/// <summary>
/// Compiles .fx Effect shaders to .mgfx binaries using DXC and SPIRV-Cross.
/// Thread-safe: each call to CompileAsync creates its own DXC instance.
/// </summary>
public sealed class EffectCompiler : IShaderCompiler
{
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);
}
```

### Consumer Usage Pattern

```csharp
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

var compiler = new EffectCompiler();

var result = await compiler.CompileAsync(
    await File.ReadAllTextAsync("MyShader.fx"),
    new CompilerOptions
    {
        Target          = PlatformTarget.OpenGL,
        SourceFileName  = "MyShader.fx",
        IncludeResolver = new FileSystemIncludeResolver(["./shaders"]),
        MgfxVersion     = 10,
    });

if (result.IsSuccess)
    await File.WriteAllBytesAsync("MyShader.mgfx", result.Value.Data);
else
    foreach (var e in result.Error)
        Console.Error.WriteLine($"{e.File}({e.Line},{e.Column}): {e.Message}");
```

---

## Changes to `ShadowDusk.Core`

### `CompilerOptions` — new properties

Add three properties to `src/ShadowDusk.Core/CompilerOptions.cs`:

```csharp
/// <summary>
/// Display name used in error messages. Defaults to "&lt;source&gt;" when null.
/// For file-based compilation, set this to the source file's path or name.
/// </summary>
public string? SourceFileName { get; init; }

/// <summary>
/// Embed debug information in compiled shader bytecode.
/// Forwards -Zi -Qembed_debug to DXC. Default: false.
/// </summary>
public bool Debug { get; init; }

/// <summary>
/// MGFX binary format version. Must be 10 or 11. Default: 10 (MonoGame 3.8.2+).
/// Version 11 is opt-in for future MonoGame/KNI releases.
/// </summary>
public int MgfxVersion { get; init; } = 10;
```

No breaking changes — all three are init-only properties with defaults.

---

## Internal Implementation

### `PassCompilationResult` (internal)

Holds the per-pass per-stage blobs assembled before ShaderIR construction.

```csharp
// src/ShadowDusk.Compiler/Internal/PassCompilationResult.cs
#nullable enable

namespace ShadowDusk.Compiler.Internal;

internal sealed record PassCompilationResult
{
    public required string PassName           { get; init; }

    // Primary output blobs (SPIR-V for OpenGL/Vulkan/Metal; DXIL for DirectX)
    public required ReadOnlyMemory<byte> VertexPrimaryBlob  { get; init; }
    public required ReadOnlyMemory<byte> PixelPrimaryBlob   { get; init; }

    // DXIL blobs — always present; used for IDxcReflection parameter extraction.
    // For DirectX targets these are the same as the primary blobs above.
    // For OpenGL/Metal these are a second DXC compile pass targeting vs_6_0/ps_6_0.
    public required ReadOnlyMemory<byte> VertexDxilBlob     { get; init; }
    public required ReadOnlyMemory<byte> PixelDxilBlob      { get; init; }

    // SPIR-V blobs — populated for OpenGL/Vulkan/Metal, empty for DirectX.
    public required ReadOnlyMemory<byte> VertexSpirvBlob    { get; init; }
    public required ReadOnlyMemory<byte> PixelSpirvBlob     { get; init; }
}
```

### `CompilationPipeline` (internal)

The full pipeline in stage order:

```
Stage 1 — FX9 pre-parse
    FxPreParser.Parse(hlslSource, sourceFileName)
    → FxParseResult  (StrippedHlsl, Techniques, Samplers, ParameterAnnotations)

Stage 2 — Preprocess
    Preprocessor.Flatten(strippedHlsl, fileName, macros, includeResolver, additionalPaths)
    → PreprocessedSource  (flattened HLSL text, DXC macro flags, file path)
    Macros injected: PlatformMacros.For(options.Target)

Stage 3 — Per-pass DXC compilation
    For each technique → for each pass:

    IF platform is OpenGL or Metal or Vulkan:
        Compile VS to SPIR-V: DxcShaderCompiler.CompileAsync(platform, Vertex, entryPoint, ...)
        Compile PS to SPIR-V: DxcShaderCompiler.CompileAsync(platform, Pixel,  entryPoint, ...)
        Compile VS to DXIL:   DxcShaderCompiler.CompileAsync(DirectX,  Vertex, entryPoint, ...)
        Compile PS to DXIL:   DxcShaderCompiler.CompileAsync(DirectX,  Pixel,  entryPoint, ...)
        PrimaryBlob = SPIR-V blob

    IF platform is DirectX:
        Compile VS to DXIL:   DxcShaderCompiler.CompileAsync(DirectX,  Vertex, entryPoint, ...)
        Compile PS to DXIL:   DxcShaderCompiler.CompileAsync(DirectX,  Pixel,  entryPoint, ...)
        PrimaryBlob = DXIL blob (same object as DxilBlob)

    → List<PassCompilationResult>

Stage 4 — Reflection (per pass, vertex and pixel separately)
    ReflectionPipeline.ReflectAsync(
        new ReflectionInput
        {
            DxilBlob      = pass.VertexDxilBlob,
            SpirVBlob     = pass.VertexSpirvBlob,   // empty for DirectX
            FxAnnotations = fxParseResult.ParameterAnnotations
        })
    → ReflectedEffect  (parameters, constant buffers, semantics)

Stage 5 — GLSL transpilation (OpenGL / Metal only)
    SpirvCrossGlslTranspiler.Transpile(pass.VertexSpirvBlob)  → GlslSource
    SpirvCrossGlslTranspiler.Transpile(pass.PixelSpirvBlob)   → GlslSource
    Encode GLSL text as UTF-8 bytes → store as compiled shader blob

Stage 6 — ShaderIR assembly
    ShaderIRBuilder.Build(fxParseResult, passResults, reflectionResults, glslResults, options)
    → ShaderIR

Stage 7 — MGFX serialization
    MgfxWriter.Write(ir, new MgfxWriterOptions { MgfxVersion = options.MgfxVersion, ... })
    → byte[]
```

**Fail-fast rule:** if any stage returns `Result.Fail`, return immediately without executing later
stages. All errors from DXC are already structured `ShaderError` values via `DxcDiagnosticReformatter`.

### Profile remapping

The `.fx` source declares profiles like `vs_4_0`, `ps_3_0`, etc. DXC rejects these for SPIR-V
output — it requires at minimum `vs_5_0`/`ps_5_0`. `CompilationPipeline` must remap declared profiles
upward to the minimum DXC accepts for each target:

| Declared profile | OpenGL/Metal target | DirectX target |
|---|---|---|
| `vs_1_1` through `vs_4_1` | promote to `vs_5_0` | promote to `vs_6_0` |
| `vs_5_0` | use as-is (`vs_5_0`) | promote to `vs_6_0` |
| `vs_6_x` | promote to `vs_5_0` (SPIR-V uses SM5) | use as-is |
| `ps_*` | same logic | same logic |

This remapping is handled by `DxcFlagBuilder` which already encodes the correct profile per
`(PlatformTarget, ShaderStage)` pair — the declared profile from the `.fx` file is used only to
determine whether a profile was specified at all, not as a literal DXC argument.

### `ShaderIRBuilder`

Assembles `ShaderIR` from all compilation outputs. Converts:
- `PassCompilationResult` → `CompiledShaderBlob` (the primary bytecode)
- `ReflectedEffect` → `EffectParameterInfo[]`, `ConstantBufferInfo[]`
- `FxParseResult.Techniques` → `MgfxTechniqueInfo[]` with vertex/pixel shader indices

The mapping between `ShaderBlob` list indices and `MgfxPassInfo.VertexShaderIndex` /
`PixelShaderIndex` must maintain pass ordering exactly as declared in the `.fx` source.

---

## Solution File Update

Add to `ShadowDusk.slnx` under the `/src/` folder:

```xml
<Project Path="src/ShadowDusk.Compiler/ShadowDusk.Compiler.csproj" />
```

---

## Tests

### New test project: `tests/ShadowDusk.Compiler.Tests/`

```
tests/ShadowDusk.Compiler.Tests/
├── ShadowDusk.Compiler.Tests.csproj
├── EffectCompilerTests.cs          ← main integration tests
└── ShaderIRBuilderTests.cs         ← unit tests for IR assembly logic
```

`ShadowDusk.Compiler.Tests.csproj` references `ShadowDusk.Compiler` and the standard xUnit stack.

All tests that invoke DXC or SPIRV-Cross are tagged:
```csharp
[Trait("Category", "Integration")]
```

Pure unit tests (no native binaries) are untagged.

### Integration Test Cases (`EffectCompilerTests`)

| Test | Shader | Platform | Assertion |
|------|--------|----------|-----------|
| `Compile_Minimal_OpenGL_ReturnsBytes` | `minimal.fx` | OpenGL | `IsSuccess == true`, `Data.Length > 0` |
| `Compile_Minimal_DirectX_ReturnsBytes` | `minimal.fx` | DirectX | `IsSuccess == true`, `Data.Length > 0` |
| `Compile_Textured_OpenGL_ReturnsBytes` | `textured.fx` | OpenGL | `IsSuccess == true` |
| `Compile_Cbuffer_OpenGL_HasParameters` | `cbuffer.fx` | OpenGL | success; MGFX blob has constant buffer with correct parameter count |
| `Compile_MultiPass_OpenGL_HasTwoPasses` | `multipass.fx` | OpenGL | success; MGFX blob has 1 technique with 2 passes |
| `Compile_SyntaxError_ReturnsErrors` | invalid HLSL | OpenGL | `IsFailure == true`, `Error.Length > 0`, `Error[0].Line > 0` |
| `Compile_MissingInclude_ReturnsError` | source with `#include "Missing.fxh"` | OpenGL | `IsFailure == true`, error references missing file |
| `Compile_Deterministic_SameBytesOnRepeat` | `minimal.fx` | OpenGL | compile twice; `Data` is byte-identical both runs |
| `Compile_Debug_DoesNotFail` | `minimal.fx` | OpenGL | `Debug = true` options; `IsSuccess == true` |
| `Compile_InMemoryIncludes_Resolves` | source with `#include "Helpers.fxh"` | OpenGL | `InMemoryIncludeResolver` with embedded header content; `IsSuccess == true` |

### Unit Test Cases (`ShaderIRBuilderTests`)

| Test | Input | Assertion |
|------|-------|-----------|
| `Build_PreservesPassOrder` | FxParseResult with 3 passes | ShaderIR techniques[0].Passes are in source order |
| `Build_ShaderIndicesAreZeroBased` | 2-pass technique | Pass 0: VertexShaderIndex=0, Pass 1: VertexShaderIndex=2 (VS0, PS0, VS1, PS1) |
| `Build_EmptyAnnotationsAllowed` | Pass with no annotations | No exception; AnnotationInfo list is empty |

---

## Fixture Shaders Required

These fixture files must exist in `tests/fixtures/shaders/` before the integration tests run.
The `minimal.fx` file is also used by Phase 9 (CLI tests).

### `minimal.fx`

```hlsl
struct VertexInput  { float4 Position : POSITION; float4 Color : COLOR0; };
struct PixelInput   { float4 Position : SV_POSITION; float4 Color : COLOR0; };

PixelInput VS(VertexInput input)
{
    PixelInput o;
    o.Position = input.Position;
    o.Color    = input.Color;
    return o;
}

float4 PS(PixelInput input) : SV_TARGET { return input.Color; }

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS();
    }
}
```

### `textured.fx`

```hlsl
Texture2D Texture;
SamplerState TextureSampler;

struct VertexInput  { float4 Position : POSITION; float2 TexCoord : TEXCOORD0; };
struct PixelInput   { float4 Position : SV_POSITION; float2 TexCoord : TEXCOORD0; };

PixelInput VS(VertexInput input)
{
    PixelInput o;
    o.Position = input.Position;
    o.TexCoord = input.TexCoord;
    return o;
}

float4 PS(PixelInput input) : SV_TARGET
{
    return Texture.Sample(TextureSampler, input.TexCoord);
}

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS();
    }
}
```

### `cbuffer.fx`

```hlsl
cbuffer Transforms
{
    float4x4 WorldViewProj;
    float4   DiffuseColor;
};

struct VertexInput  { float4 Position : POSITION; };
struct PixelInput   { float4 Position : SV_POSITION; };

PixelInput VS(VertexInput input)
{
    PixelInput o;
    o.Position = mul(input.Position, WorldViewProj);
    return o;
}

float4 PS(PixelInput input) : SV_TARGET { return DiffuseColor; }

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS();
    }
}
```

### `multipass.fx`

```hlsl
float4 PS_Solid(float4 pos : SV_POSITION) : SV_TARGET { return float4(1, 0, 0, 1); }
float4 PS_Alpha(float4 pos : SV_POSITION) : SV_TARGET { return float4(1, 0, 0, 0.5); }

float4 VS(float4 pos : POSITION) : SV_POSITION { return pos; }

technique Technique1
{
    pass Opaque
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS_Solid();
    }
    pass Transparent
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS_Alpha();
    }
}
```

---

## Task Checklist

### 1. New project

- [ ] 1.1 Create `src/ShadowDusk.Compiler/` directory.
- [ ] 1.2 Write `ShadowDusk.Compiler.csproj` with all package metadata and project references.
- [ ] 1.3 Add to `ShadowDusk.slnx` under `/src/`.

### 2. `CompilerOptions` additions (`ShadowDusk.Core`)

- [ ] 2.1 Add `SourceFileName`, `Debug`, and `MgfxVersion` properties to `CompilerOptions`.
- [ ] 2.2 Verify no existing callers are broken (all properties have defaults).

### 3. Internal types

- [ ] 3.1 Write `PassCompilationResult` record.
- [ ] 3.2 Write `CompilationPipeline` class with `RunAsync` implementing the 7-stage pipeline.
- [ ] 3.3 Write `ShaderIRBuilder` static class with `Build` method.
- [ ] 3.4 Implement profile remapping in `CompilationPipeline` (see Profile Remapping section).

### 4. `EffectCompiler`

- [ ] 4.1 Write `EffectCompiler : IShaderCompiler`.
- [ ] 4.2 `CompileAsync` delegates to `CompilationPipeline.RunAsync` and maps its result to
         `Result<CompiledShader, ShaderError[]>`.
- [ ] 4.3 `EffectCompiler` is `IDisposable` if `DxcShaderCompiler` is held as a field;
         otherwise create-per-call (preferred for thread safety).
- [ ] 4.4 Confirm thread safety: two concurrent `CompileAsync` calls must not interfere.
         (Easiest guarantee: create a new `DxcShaderCompiler` per `CompileAsync` invocation,
         since `DxcShaderCompiler` is documented as NOT thread-safe.)

### 5. Fixture shaders

- [ ] 5.1 Create `tests/fixtures/shaders/minimal.fx`.
- [ ] 5.2 Create `tests/fixtures/shaders/textured.fx`.
- [ ] 5.3 Create `tests/fixtures/shaders/cbuffer.fx`.
- [ ] 5.4 Create `tests/fixtures/shaders/multipass.fx`.

### 6. Test project

- [ ] 6.1 Create `tests/ShadowDusk.Compiler.Tests/ShadowDusk.Compiler.Tests.csproj`.
- [ ] 6.2 Add to `ShadowDusk.slnx` under `/tests/`.
- [ ] 6.3 Write all integration test cases from `EffectCompilerTests`.
- [ ] 6.4 Write all unit test cases from `ShaderIRBuilderTests`.

### 7. Verification

- [ ] 7.1 `dotnet build ShadowDusk.slnx` — 0 errors, 0 warnings.
- [ ] 7.2 `dotnet test --filter "Category!=Integration"` — all unit tests pass.
- [ ] 7.3 `dotnet test --filter "Category=Integration"` — all integration tests pass.
- [ ] 7.4 `dotnet pack src/ShadowDusk.Compiler` — NuGet package produced.
- [ ] 7.5 Create a scratch `ConsoleApp` outside the solution; add a local NuGet reference to the
         packed `.nupkg`; call `EffectCompiler.CompileAsync` on `minimal.fx`; confirm bytes are
         written to disk successfully.

---

## Acceptance Criteria

| Criterion | How to verify |
|---|---|
| `EffectCompiler.CompileAsync` returns `.mgfx` bytes for `minimal.fx`/OpenGL | Integration test `Compile_Minimal_OpenGL_ReturnsBytes` passes |
| `EffectCompiler.CompileAsync` returns `.mgfx` bytes for `minimal.fx`/DirectX | Integration test `Compile_Minimal_DirectX_ReturnsBytes` passes |
| Shader errors are returned as structured `ShaderError[]`, not thrown | Integration test `Compile_SyntaxError_ReturnsErrors` passes |
| Two calls with identical inputs produce byte-identical output | Integration test `Compile_Deterministic_SameBytesOnRepeat` passes |
| `dotnet pack` produces a valid `.nupkg` with the correct `PackageId` | Task 7.4 |
| A fresh project can compile `.mgfx` using only the NuGet reference | Task 7.5 (scratch app smoke test) |
| `IShaderCompiler` is the only interface a consumer needs to type-reference | `EffectCompiler` assignable to `IShaderCompiler` variable |

---

## Out of Scope for This Phase

- Metal (MSL) transpilation — `PlatformTarget.Metal` must return a `ShaderError` with code `SD0200`
  and message `"Metal target not yet supported"` until Phase 10 Metal support is added.
- MGCB plugin integration (`ShadowDusk.MgcbPlugin`) — separate future phase.
- Publishing to NuGet.org — deferred to Phase 12 (CI).
- `ShadowDusk.Core` being separately packable as a consumer-facing NuGet — it is an implementation
  detail; consumers reference `ShadowDusk.Compiler` only.
