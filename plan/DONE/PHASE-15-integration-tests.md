# Phase 15 — End-to-End Integration Tests

**Status:** ✅ Done. 103 integration tests pass. Two items deferred to Phase 20 backlog:
CLI-process invocation mode tests (infrastructure built, tests not yet wired up), and
cross-platform Linux/macOS validation (explicitly scoped to Phase 30 CI).

## Overview

This phase adds a comprehensive integration test suite that compiles real `.fx` fixture shaders through the full ShadowDusk pipeline and validates the resulting `.mgfx` blobs. Unlike unit tests, these tests exercise every layer — FX9 pre-parser, preprocessor macro injection, DXC compilation, SPIRV-Cross transpilation, and MGFX serialization — against real HLSL source files and real native tooling.

**Inputs:** `.fx` fixture files under `tests/fixtures/shaders/`, a built CLI binary (Phase 8) or direct pipeline invocation.  
**Outputs:** Pass/fail assertions on exit code, MGFX blob structure, shader counts, parameter reflection, and stderr diagnostics.

**Project:** `tests/ShadowDusk.Integration.Tests/`

---

## Scope and Non-Goals

**In scope:**
- Nine canonical fixture shaders covering the full feature surface
- Per-fixture structural assertions on the MGFX blob (header, technique count, pass count, shader blobs, parameter reflection)
- Platform-tagged test variants for OpenGL, DirectX_11, and Vulkan
- A determinism test (compile-twice, byte-compare)
- Error-case tests (syntax error, missing include, unknown profile)
- A shared `CompileFixture` test helper that can run end-to-end via CLI process or inline pipeline
- CI-ready test filtering by `Category` and `Platform` traits

**Out of scope:**
- Metal (MSL) target — deferred to Phase 11 (macOS CI runner not yet configured)
- GPU execution of compiled shaders — output validation is structural, not runtime
- Load testing / large shader stress tests
- Parallel compilation benchmarks

---

## 1. Fixture Shaders

Create each file under `tests/fixtures/shaders/`. Files must be valid HLSL Effect (FX) syntax. No MGCB or MonoGame SDK is required to author them — they are plain text.

> **Note:** The fixture corpus already contains 37 real MonoGame shaders (BasicEffect.fx, SpriteEffect.fx, etc.) from Phase 0. The 9 shaders below are **new, purpose-built** fixtures designed to exercise specific pipeline features. They coexist with the existing corpus and should be authored as additional files.

### 1.1 Fixture Table

| File | Purpose | Key Features Exercised |
|------|---------|------------------------|
| `minimal.fx` | Passthrough VS + solid color PS, single technique, no textures | Simplest valid shader; baseline for all other tests |
| `textured.fx` | VS + PS with `Texture2D` + `SamplerState` | Combined sampler path; separate-object sampler binding |
| `cbuffer.fx` | Explicit `cbuffer` with `float4x4` matrix + `float4` color | cbuffer reflection; parameter size/offset correctness |
| `multipass.fx` | Single technique with two passes | Pass ordering; two distinct shader blobs per technique |
| `multitechnique.fx` | Three techniques in a single file | Technique ordering preservation; technique-by-name lookup |
| `render-states.fx` | Pass with `CullMode`, `AlphaBlendEnable`, `DepthBufferEnable` | Render state round-trip through MGFX serialization |
| `annotations.fx` | Parameter with `< string UIName = "MyParam"; >` annotation | Annotation round-trip; UIName preserved in MGFX blob |
| `platform-macros.fx` | `#if GLSL` / `#if SM4` guarded code paths | Macro injection correctness per target platform |
| `basiceffect-mini.fx` | Four techniques selected by index, minimal per-technique VS/PS pair | Technique-by-index pattern; indices 0–3 are distinct and ordered |

### 1.2 Fixture Source Listings

#### `tests/fixtures/shaders/minimal.fx`

```hlsl
float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : SV_TARGET
{
    return float4(1, 0, 1, 1);
}

technique Technique0
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
```

#### `tests/fixtures/shaders/textured.fx`

```hlsl
float4x4 WorldViewProj;

Texture2D DiffuseTexture;
SamplerState DiffuseSampler
{
    Filter   = MIN_MAG_MIP_LINEAR;
    AddressU = Wrap;
    AddressV = Wrap;
};

struct VSInput  { float4 Position : POSITION; float2 TexCoord : TEXCOORD0; };
struct VSOutput { float4 Position : SV_POSITION; float2 TexCoord : TEXCOORD0; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 PS(VSOutput input) : SV_TARGET
{
    return DiffuseTexture.Sample(DiffuseSampler, input.TexCoord);
}

technique Textured
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
```

#### `tests/fixtures/shaders/cbuffer.fx`

```hlsl
cbuffer TransformBuffer : register(b0)
{
    float4x4 WorldViewProj;
    float4   Color;
};

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : SV_TARGET
{
    return Color;
}

technique CBuffer
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
```

#### `tests/fixtures/shaders/multipass.fx`

```hlsl
float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PSRed(VSOutput input)   : SV_TARGET { return float4(1, 0, 0, 1); }
float4 PSGreen(VSOutput input) : SV_TARGET { return float4(0, 1, 0, 1); }

technique MultiPass
{
    pass PassRed
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PSRed();
    }
    pass PassGreen
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PSGreen();
    }
}
```

#### `tests/fixtures/shaders/multitechnique.fx`

```hlsl
float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PSA(VSOutput i) : SV_TARGET { return float4(1, 0, 0, 1); }
float4 PSB(VSOutput i) : SV_TARGET { return float4(0, 1, 0, 1); }
float4 PSC(VSOutput i) : SV_TARGET { return float4(0, 0, 1, 1); }

technique TechA
{
    pass Pass0 { VertexShader = compile vs_5_0 VS(); PixelShader = compile ps_5_0 PSA(); }
}
technique TechB
{
    pass Pass0 { VertexShader = compile vs_5_0 VS(); PixelShader = compile ps_5_0 PSB(); }
}
technique TechC
{
    pass Pass0 { VertexShader = compile vs_5_0 VS(); PixelShader = compile ps_5_0 PSC(); }
}
```

#### `tests/fixtures/shaders/render-states.fx`

```hlsl
float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : SV_TARGET { return float4(1, 1, 1, 0.5); }

technique RenderStates
{
    pass Pass0
    {
        VertexShader        = compile vs_5_0 VS();
        PixelShader         = compile ps_5_0 PS();
        CullMode            = None;
        AlphaBlendEnable    = True;
        DepthBufferEnable   = False;
    }
}
```

#### `tests/fixtures/shaders/annotations.fx`

```hlsl
float4 TintColor < string UIName = "Tint Color"; float4 UIMin = float4(0,0,0,0); >;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = input.Position;
    return output;
}

float4 PS(VSOutput input) : SV_TARGET { return TintColor; }

technique Annotated
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
```

#### `tests/fixtures/shaders/platform-macros.fx`

```hlsl
float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : SV_TARGET
{
#if GLSL
    // Coordinate-flip for OpenGL NDC
    return float4(0.0, 1.0, 0.0, 1.0);
#elif SM4
    return float4(1.0, 0.0, 0.0, 1.0);
#else
    return float4(0.5, 0.5, 0.5, 1.0);
#endif
}

technique PlatformMacros
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
```

#### `tests/fixtures/shaders/basiceffect-mini.fx`

```hlsl
float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; float4 Color : COLOR0; };
struct VSOutput { float4 Position : SV_POSITION; float4 Color : COLOR0; };

VSOutput VS_NoTex(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    output.Color    = input.Color;
    return output;
}

float4 PS_Vertex(VSOutput input) : SV_TARGET { return input.Color; }
float4 PS_White(VSOutput input)  : SV_TARGET { return float4(1,1,1,1); }
float4 PS_Flat(VSOutput input)   : SV_TARGET { return float4(0.5,0.5,0.5,1); }
float4 PS_Debug(VSOutput input)  : SV_TARGET { return float4(1,0,1,1); }

// Technique indices 0-3 must be distinct and ordered
technique Tech0 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_Vertex(); } }
technique Tech1 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_White();  } }
technique Tech2 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_Flat();   } }
technique Tech3 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_Debug();  } }
```

---

## 2. Project Setup

> **Note:** `tests/ShadowDusk.Integration.Tests/` already exists and already contains integration tests from Phases 4–6: `DxcShaderCompilerIntegrationTests.cs`, `GlslTranspilerTests.cs`, and several reflection tests. This phase **adds** to the existing project rather than creating it from scratch. Also remove `PlaceholderTest.cs` (a stub from Phase 1) as part of this phase's setup.

### 2.1 Create the test project

1. The project `tests/ShadowDusk.Integration.Tests/ShadowDusk.Integration.Tests.csproj` already exists. Confirm it targets `net8.0`.
2. Add project references:
   - `src/ShadowDusk.Core` (for direct pipeline invocation path)
   - `src/ShadowDusk.Cli` (for CLI process invocation path, referenced as a project-level tool)
3. Add NuGet references:
   - `xunit` (pinned, matching the unit test projects)
   - `xunit.runner.visualstudio`
   - `FluentAssertions`
   - `Microsoft.NET.Test.Sdk`
4. Add the project to `ShadowDusk.slnx` under the `tests/` solution folder. (The solution file is `ShadowDusk.slnx`, not `ShadowDusk.sln`.)
5. In `Directory.Build.props`, confirm `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` applies to this project.

### 2.2 Fixture file embedding

1. In the `.csproj`, add:
   ```xml
   <ItemGroup>
     <None Include="..\..\tests\fixtures\shaders\**\*" CopyToOutputDirectory="PreserveNewest" Link="fixtures\shaders\%(RecursiveDir)%(Filename)%(Extension)" />
   </ItemGroup>
   ```
2. Add a static helper `FixturePath(string fileName)` in `TestHelpers.cs` that resolves the path relative to `AppContext.BaseDirectory` so tests work regardless of working directory.

### 2.3 Directory structure

```
tests/
├── fixtures/
│   └── shaders/
│       ├── minimal.fx
│       ├── textured.fx
│       ├── cbuffer.fx
│       ├── multipass.fx
│       ├── multitechnique.fx
│       ├── render-states.fx
│       ├── annotations.fx
│       ├── platform-macros.fx
│       └── basiceffect-mini.fx
└── ShadowDusk.Integration.Tests/
    ├── ShadowDusk.Integration.Tests.csproj
    ├── TestHelpers.cs
    ├── MgfxBlobReader.cs
    ├── Fixtures/
    │   └── CliFixture.cs
    └── Tests/
        ├── CompileFixtureTests.cs
        ├── DeterminismTests.cs
        └── ErrorCaseTests.cs
```

---

## 3. Test Infrastructure

### 3.1 `CompileFixture` helper

Create `tests/ShadowDusk.Integration.Tests/TestHelpers.cs`.

```csharp
#nullable enable
namespace ShadowDusk.Integration.Tests;

public enum InvocationMode { CliProcess, DirectPipeline }

public sealed record CompileResult(int ExitCode, byte[] Mgfx, string Stderr);

public static class TestHelpers
{
    public static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", fileName);

    /// <summary>
    /// Compiles a fixture shader to a temp directory, returns exit code, raw .mgfx bytes,
    /// and captured stderr. Uses <paramref name="mode"/> to select CLI process vs. direct call.
    /// </summary>
    public static async Task<CompileResult> CompileFixtureAsync(
        string fx,
        string profile,
        InvocationMode mode = InvocationMode.DirectPipeline,
        CancellationToken ct = default)
    {
        var inputPath  = FixturePath(fx);
        var outputDir  = Path.Combine(Path.GetTempPath(), $"shadowdusk_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, Path.ChangeExtension(fx, ".mgfx"));

        try
        {
            return mode switch
            {
                InvocationMode.CliProcess      => await CompileViaCliAsync(inputPath, outputPath, profile, ct),
                InvocationMode.DirectPipeline  => await CompileViaPipelineAsync(inputPath, outputPath, profile, ct),
                _                              => throw new ArgumentOutOfRangeException(nameof(mode))
            };
        }
        finally
        {
            // Best-effort cleanup; failures are non-fatal.
            try { Directory.Delete(outputDir, recursive: true); } catch { /* ignored */ }
        }
    }
}
```

Implementation notes:
- `CompileViaCliAsync`: launch the `mgfxc` CLI binary (named per `<ToolCommandName>mgfxc</ToolCommandName>`) found at `Path.Combine(AppContext.BaseDirectory, "mgfxc")` (or `"mgfxc.exe"` on Windows), capture stdout/stderr, return when process exits. Do not search for `"shadowdusk"` — the binary is named `"mgfxc"`.
- `CompileViaPipelineAsync`: call `ShadowDusk.Cli.PipelineRunner` (Phase 8) directly via `PipelineRunnerFactory.Create(...)` and `RunAsync(...)`. No `ShadowDusk.Core.CompilerPipeline` class exists — the pipeline runner lives in `ShadowDusk.Cli`. Serialize the result to a temp file path, then read the bytes back. Stderr is populated from `ShaderError` diagnostics on failure, empty on success.
- Both paths write output to `outputPath`, then read the bytes. If the file does not exist (compile failure), return an empty byte array.
- Never `.Wait()` or `.Result` — all I/O is `async`/`await`.

### 3.2 `MgfxBlobReader`

Create `tests/ShadowDusk.Integration.Tests/MgfxBlobReader.cs`. This is a minimal structural reader — it does not need to reproduce the full MGFX parser, only extract the fields that integration tests assert on.

Fields to expose:
- `Signature`: first 4 bytes as `string` (ASCII)
- `MgfxVersion`: byte at offset 4
- `ProfileId`: byte at offset 5
- `TechniqueCount`: parsed from the technique table
- `Techniques`: list of `(string Name, int PassCount)` tuples
- `TotalShaderBlobCount`: sum of shader blob entries across all passes
- `ParameterNames`: list of parameter name strings from the constant table
- `RenderStateFlags`: bitmask or list of state names present in any pass

```csharp
#nullable enable
namespace ShadowDusk.Integration.Tests;

public sealed class MgfxBlobReader
{
    public string   Signature         { get; }
    public byte     MgfxVersion       { get; }
    public byte     ProfileId         { get; }
    public int      TechniqueCount    { get; }
    public IReadOnlyList<TechniqueInfo> Techniques { get; }
    public int      TotalShaderBlobCount { get; }
    public IReadOnlyList<string> ParameterNames { get; }

    public static MgfxBlobReader Parse(byte[] blob) { /* ... */ }
}

public sealed record TechniqueInfo(string Name, int PassCount);
```

Implementation note: parse using `BinaryReader` over a `MemoryStream`. Reference the MGFX binary format documented in `src/ShadowDusk.Core/MgfxWriter.cs` (Phase 7) for field offsets and encoding — `MgfxWriter` is in the root `ShadowDusk.Core` namespace, not a `Serialization/` subfolder. If the blob is malformed, throw `InvalidDataException` with the byte offset.

### 3.3 `CliFixture`

Create `tests/ShadowDusk.Integration.Tests/Fixtures/CliFixture.cs` as an `IAsyncLifetime` xUnit class fixture. Its responsibility is locating the CLI binary once per test class and exposing its path.

```csharp
#nullable enable
namespace ShadowDusk.Integration.Tests.Fixtures;

public sealed class CliFixture : IAsyncLifetime
{
    public string CliPath { get; private set; } = string.Empty;

    public Task InitializeAsync()
    {
        // Search: AppContext.BaseDirectory, then PATH, then published output directory.
        // Throw SkipException (Xunit.SkipException or custom) if the binary is absent,
        // so CLI-mode tests are skipped rather than failing when the CLI has not been built yet.
        CliPath = LocateCliBinary();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string LocateCliBinary() { /* ... */ }
}
```

---

## 4. Compile Fixture Tests

Create `tests/ShadowDusk.Integration.Tests/Tests/CompileFixtureTests.cs`.

All tests in this file carry:
```csharp
[Trait("Category", "Integration")]
```

Platform-specific tests also carry one of:
```csharp
[Trait("Platform", "OpenGL")]
[Trait("Platform", "DirectX_11")]
[Trait("Platform", "Vulkan")]
```

### 4.1 Universal header assertions (parameterized)

Use `[Theory]` + `[MemberData]` to drive one test method over all 9 fixtures × 3 platforms.

```csharp
[Theory]
[Trait("Category", "Integration")]
[MemberData(nameof(AllFixturesAndPlatforms))]
public async Task Compile_ProducesValidMgfxHeader(string fx, string profile, string platformTrait)
```

For each combination assert:

- [x] Exit code is `0`
- [x] Output byte array is non-empty
- [x] `MgfxBlobReader.Parse(mgfx).Signature == "MGFX"`
- [x] `MgfxBlobReader.Parse(mgfx).MgfxVersion == 10`
- [x] `MgfxBlobReader.Parse(mgfx).ProfileId` matches the expected value for the given platform:

| Profile string | Expected `ProfileId` |
|----------------|----------------------|
| `OpenGL`       | `0x00` |
| `DirectX_11`   | `0x01` |
| `Vulkan`       | `0x03` |

> **Known limitation for `DirectX_11` fixture tests:** DXC produces SM6 DXIL (not SM5 DXBC) for DirectX targets. The `.mgfx` blob will compile and the header/structure assertions will pass, but the shader blobs contain DXIL — MonoGame's D3D11 backend **cannot load** DXIL at runtime. The MGFX binary format is valid; only the blob content is wrong for D3D11. Full D3D11 support (SM5 DXBC) requires Phase 4.1 (vkd3d-shader integration). Tests should assert structural correctness, not runtime loadability for DirectX_11.

### 4.2 Per-fixture structural assertions

Each assertion is its own `[Fact]` or `[Theory]`. Annotate each with the relevant `[Trait("Platform", ...)]`.

#### Task list

1. **`minimal.fx` — 1 technique, 1 pass, 2 shader blobs**
   - [x] `TechniqueCount == 1`
   - [x] `Techniques[0].PassCount == 1`
   - [x] `TotalShaderBlobCount == 2` (one VS + one PS)

2. **`textured.fx` — combined sampler present in GLSL output**
   - [x] Compile for `OpenGL` target
   - [x] `TotalShaderBlobCount == 2`
   - [x] Disassemble the GLSL blob (embedded as UTF-8 source in the MGFX) and assert it contains `sampler2D` (combined texture-sampler, not separate `texture2D` + `sampler`)

3. **`cbuffer.fx` — parameter reflection**
   - [x] `ParameterNames` contains `"WorldViewProj"`
   - [x] `ParameterNames` contains `"Color"`
   - [x] The size recorded for `WorldViewProj` in the constant table is `64` bytes (4×4 float matrix)

4. **`multipass.fx` — 2 passes, 2 shader blobs each**
   - [x] `TechniqueCount == 1`
   - [x] `Techniques[0].PassCount == 2`
   - [x] `TotalShaderBlobCount == 4` (VS+PS per pass)

5. **`multitechnique.fx` — technique ordering**
   - [x] `TechniqueCount == 3`
   - [x] `Techniques[0].Name == "TechA"`
   - [x] `Techniques[1].Name == "TechB"`
   - [x] `Techniques[2].Name == "TechC"`
   - [x] Order in blob matches declaration order in source (not alphabetical, not hash-ordered)

6. **`render-states.fx` — render state round-trip**
   - [x] `TechniqueCount == 1`
   - [x] `Techniques[0].PassCount == 1`
   - [x] `MgfxBlobReader` reports `CullMode = None` in the pass render-state block
   - [x] `MgfxBlobReader` reports `AlphaBlendEnable = true`
   - [x] `MgfxBlobReader` reports `DepthBufferEnable = false`

7. **`annotations.fx` — annotation round-trip**
   - [x] `ParameterNames` contains `"TintColor"`
   - [x] The annotation `UIName` for `TintColor` equals `"Tint Color"` in the serialized annotation table

8. **`platform-macros.fx` — macro injection per platform**
   - [x] Compile for `OpenGL`: compile succeeds; disassembled GLSL output contains a reference to the green-channel value (the GLSL branch)
   - [x] Compile for `DirectX_11`: compile succeeds; the SM4 branch is selected (pipeline does not error on missing `GLSL` branch)
   - [x] Verify that injecting an unknown macro does not suppress the `#else` fallback

9. **`basiceffect-mini.fx` — 4 distinct techniques by index**
   - [x] `TechniqueCount == 4`
   - [x] `Techniques[0].Name == "Tech0"` through `Techniques[3].Name == "Tech3"`
   - [x] All four techniques are distinct (no deduplication of identical VS sources)
   - [x] `Techniques[i].PassCount == 1` for all `i` in `0..3`

---

## 5. Determinism Tests

Create `tests/ShadowDusk.Integration.Tests/Tests/DeterminismTests.cs`.

```csharp
[Trait("Category", "Integration")]
[Trait("Category", "Determinism")]
```

### 5.1 Task list

1. **Compile `minimal.fx` twice, same profile, same tool versions**
   - [x] Call `CompileFixtureAsync("minimal.fx", "OpenGL")` twice in sequence (not parallel, to eliminate race conditions)
   - [x] Assert `result1.Mgfx.SequenceEqual(result2.Mgfx)` — byte-identical output
   - [x] Repeat for `DirectX_11` profile

2. **Compile `cbuffer.fx` twice for OpenGL**
   - [x] Assert byte-identical output
   - [x] This exercises SPIR-V emission order determinism (constant buffer offsets must not vary between runs)

3. **Compile `multitechnique.fx` twice for OpenGL**
   - [x] Assert byte-identical output
   - [x] Exercises that technique/pass ordering in the blob is not influenced by dictionary or hash iteration order

4. **Document tool-version caveat**
   - [x] Add an XML doc comment on the test class noting that determinism is guaranteed only for the same DXC + SPIRV-Cross binary versions. The test is valid within a single CI run; it does not assert cross-version stability.

---

## 6. Error Case Tests

Create `tests/ShadowDusk.Integration.Tests/Tests/ErrorCaseTests.cs`.

```csharp
[Trait("Category", "Integration")]
```

These tests use inline HLSL strings rather than fixture files. Write to a temp file before calling `CompileFixtureAsync`.

Add a helper:
```csharp
static Task<CompileResult> CompileSourceAsync(string hlslSource, string profile, CancellationToken ct = default)
```
It writes `hlslSource` to a temp `.fx` file, calls `CompileFixtureAsync`, then deletes the temp file.

### 6.1 Task list

1. **Syntax error in HLSL**
   - [x] Source: `float4 PS() : SV_TARGET { return SYNTAX ERROR; }`
   - [x] Assert `ExitCode == 1`
   - [x] Assert `Stderr` matches regex `\(\d+,\d+\)` — the `(line,col)` diagnostic format
   - [x] Assert `Stderr` does not contain stack traces or internal exception messages (no swallowed errors)

2. **Undeclared identifier**
   - [x] Source: valid VS + PS that references `UndeclaredVar` in the PS body
   - [x] Assert `ExitCode == 1`
   - [x] Assert `Stderr` contains the identifier name `"UndeclaredVar"`
   - [x] Assert `Stderr` contains a line number

3. **Missing `#include`**
   - [x] Source: `#include "nonexistent_header.fxh"` at the top of an otherwise-valid shader
   - [x] Assert `ExitCode == 1`
   - [x] Assert `Stderr` references the source file name (not just the include path)
   - [x] Assert `Stderr` contains the line number of the `#include` directive (line 1)

4. **Unknown profile string**
   - [x] Call `CompileFixtureAsync("minimal.fx", "PS5_NotAReal_Target")`
   - [x] Assert `ExitCode == 1`
   - [x] Assert `Stderr` contains the unrecognized profile string

5. **Empty source file**
   - [x] Source: empty string
   - [x] Assert `ExitCode == 1`
   - [x] Assert `Stderr` contains a human-readable message (e.g., "no techniques found" or "empty source")

6. **No techniques declared**
   - [x] Source: a valid HLSL function but no `technique` block
   - [x] Assert `ExitCode == 1`
   - [x] Assert `Stderr` references missing technique

---

## 7. Test Filtering Reference

| `dotnet test` filter | Tests included |
|---|---|
| `--filter "Category=Integration"` | All integration tests |
| `--filter "Category=Integration&Platform=OpenGL"` | OpenGL-targeted tests only |
| `--filter "Category=Integration&Platform=DirectX_11"` | DirectX 11-targeted tests only |
| `--filter "Category=Integration&Platform=Vulkan"` | Vulkan-targeted tests only |
| `--filter "Category=Determinism"` | Determinism tests only |
| `--filter "Category=Integration&Category!=Determinism"` | Integration, excluding determinism |

---

## 8. CI Integration Notes

> Full CI matrix is specified in Phase 10. The following notes apply to how these tests behave within it.

1. **Native tool availability gate**: Before running tests, CI must run `tools/restore.sh` (Linux/macOS) or `tools/restore.ps1` (Windows). If a native binary is missing, `CliFixture.InitializeAsync` must throw `SkipException` so tests appear as "Skipped" rather than "Failed".

2. **Temp directory hygiene**: Each `CompileFixtureAsync` call creates and deletes its own temp directory. Tests must not assume a particular working directory and must not share temp directories across test cases.

3. **Parallelism**: xUnit test parallelism is enabled at the collection level. `CompileFixtureTests` and `DeterminismTests` must be in separate `[Collection]` classes if they share any static state. Determinism tests should explicitly disable parallel execution within their collection to avoid false failures from concurrent DXC invocations writing to shared temp paths.

4. **Timeout**: Every `CompileFixtureAsync` call is given a `CancellationToken` with a 30-second timeout. Tests that exceed this are treated as failures, not hangs.

5. **Output verbosity**: Run with `--logger "console;verbosity=normal"` in CI so fixture names appear in failed test output without requiring `--logger trx`.

---

## 9. Acceptance Criteria

- [x] All 9 fixture `.fx` files are authored and present under `tests/fixtures/shaders/`
- [x] All 9 fixtures compile successfully for the `OpenGL` target (exit code 0, valid MGFX header)
- [x] All 9 fixtures compile successfully for the `DirectX_11` target
- [x] All 9 fixtures compile successfully for the `Vulkan` target
- [x] Per-fixture structural assertions pass for each fixture (technique count, pass count, shader blob count, parameter names)
- [x] Determinism test passes for `minimal.fx`, `cbuffer.fx`, and `multitechnique.fx` on OpenGL
- [x] All 6 error-case tests produce `ExitCode == 1` with correctly formatted `Stderr`
- [x] `dotnet test --filter "Category=Integration&Platform=OpenGL"` runs only OpenGL-tagged tests
- [ ] Tests run without modification on Windows, Linux, and macOS — **deferred to Phase 30 CI** (see [PHASE-20-deferred-backlog.md](PHASE-20-deferred-backlog.md))
- [x] No test uses `Thread.Sleep`, `.Result`, or `.Wait()`
- [x] No test writes outside of `Path.GetTempPath()` or the test output directory

---

## 10. Implementation Order

- [x] 0. Delete `tests/ShadowDusk.Integration.Tests/PlaceholderTest.cs` (stub from Phase 1, no longer needed).
- [x] 1. Author all 9 `.fx` fixture files from Section 1.2 under `tests/fixtures/shaders/`. (The directory and project already exist from Phases 4–6.)
- [x] 2. Confirm `tests/ShadowDusk.Integration.Tests/ShadowDusk.Integration.Tests.csproj` is registered in `ShadowDusk.slnx` (Section 2.1).
- [x] 3. Add fixture file embedding `<None Include>` items to the `.csproj` (Section 2.2).
- [x] 4. Implement `TestHelpers.cs` — `FixturePath`, `CompileFixtureAsync`, `CompileViaCliAsync`, `CompileViaPipelineAsync` (Section 3.1).
- [x] 5. Implement `MgfxBlobReader.cs` — structural parser reading signature, version, profile, technique table, shader blobs, and parameter names (Section 3.2).
- [x] 6. Implement `CliFixture.cs` — `IAsyncLifetime`, binary location logic, skip-on-missing (Section 3.3).
- [x] 7. Implement `CompileFixtureTests.cs` — universal header `[Theory]` first (Section 4.1), then per-fixture `[Fact]` assertions (Section 4.2).
- [x] 8. Implement `DeterminismTests.cs` — compile-twice byte-compare for `minimal.fx`, `cbuffer.fx`, `multitechnique.fx` (Section 5).
- [x] 9. Implement `ErrorCaseTests.cs` — 6 error-case tests using `CompileSourceAsync` helper for inline source (Section 6).
- [x] 10. Run `dotnet test --filter "Category=Integration"` locally; confirm all 9 fixtures pass for OpenGL.
- [x] 11. Run `dotnet test --filter "Category=Integration&Platform=DirectX_11"` — confirm all pass.
- [x] 12. Run `dotnet test --filter "Category=Determinism"` — confirm passes independently.
- [x] 13. Confirm all 6 error-case tests produce `ExitCode == 1` with correctly formatted `Stderr`.
