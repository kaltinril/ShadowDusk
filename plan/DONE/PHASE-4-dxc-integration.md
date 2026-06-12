# Phase 4 — DXC Integration via Vortice.Dxc

## Overview

This phase wires up `Vortice.Dxc` to compile preprocessed HLSL to SPIR-V (OpenGL/Vulkan) or DXBC (DirectX_11), one DXC invocation per shader stage per pass. It sits between the preprocessor/FX9 pre-parser output (Phases 2–3) and the downstream SPIRV-Cross transpilation step (Phase 5).

**Inputs:** Preprocessed HLSL source, per-pass entry points, platform target, include resolver, `-D` macros.
**Outputs:** `Result<PlatformBlob, ShaderError>` — SPIR-V bytes for OpenGL/Vulkan, DXBC bytes for DirectX_11.

---

## Scope and Non-Goals

**In scope:**
- `Vortice.Dxc` NuGet dependency wired into `ShadowDusk.HLSL`
- Per-platform DXC flag assembly (OpenGL, Vulkan, DirectX_11)
- One `IDxcCompiler3` invocation per shader stage per pass
- Error reformatting from DXC diagnostic format to FXC format
- `Result<PlatformBlob, ShaderError>` return type — no exceptions as control flow
- Unit tests for flag assembly and error reformatting
- Integration tests for a minimal HLSL round-trip to SPIR-V

**Out of scope:**
- FXC (Windows-only, not supported)
- Shader Model 3 targets — minimum is SM5 (vs_5_0/ps_5_0)
- SPIRV-Cross transpilation (Phase 5)
- Hull/domain/geometry/compute stages (Phase 6)
- Parallel pass compilation (deferred; thread-safety note documented below)

---

## 1. NuGet Dependency

1. Add `Vortice.Dxc` to `src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj`.
2. Pin to a specific stable release — record the version in `Directory.Packages.props` (Central Package Management).
3. Verify the package bundles native binaries for all three RIDs: `win-x64`, `linux-x64`, `osx-x64` / `osx-arm64`. Confirm no additional native restore script is required for DXC.
4. Remove any existing DXC binary download step from `tools/restore.sh` / `tools/restore.ps1` that would conflict.

```xml
<!-- src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj -->
<PackageReference Include="Vortice.Dxc" Version="x.y.z" />
```

---

## 2. Public API Surface

Define the following types in `src/ShadowDusk.HLSL/Dxc/`.

### 2.1 `PlatformBlob`

```csharp
// src/ShadowDusk.HLSL/Dxc/PlatformBlob.cs
#nullable enable
namespace ShadowDusk.HLSL.Dxc;

public enum BlobKind { Spirv, Dxbc }

public sealed class PlatformBlob
{
    public BlobKind Kind { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public PlatformBlob(BlobKind kind, ReadOnlyMemory<byte> bytes) { Kind = kind; Bytes = bytes; }
}
```

### 2.2 `DxcCompileRequest`

```csharp
// src/ShadowDusk.HLSL/Dxc/DxcCompileRequest.cs
#nullable enable
namespace ShadowDusk.HLSL.Dxc;

public sealed class DxcCompileRequest
{
    /// <summary>Preprocessed HLSL with technique blocks stripped.</summary>
    public required string HlslSource { get; init; }

    /// <summary>Source file name used in diagnostics (does not need to exist on disk).</summary>
    public required string SourceFileName { get; init; }

    /// <summary>Entry point function name from Phase 2/3 pre-parser output.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>Shader stage being compiled.</summary>
    public required ShaderStage Stage { get; init; }

    /// <summary>Target platform determining flag set and output format.</summary>
    public required PlatformTarget Platform { get; init; }

    /// <summary>Additional preprocessor macro definitions (-D flags).</summary>
    public IReadOnlyList<(string Name, string? Value)> Macros { get; init; } = [];

    /// <summary>Optional include handler forwarded from Phase 3 preprocessor.</summary>
    public IDxcIncludeHandler? IncludeHandler { get; init; }
}
```

### 2.3 `IDxcShaderCompiler` and `DxcShaderCompiler`

```csharp
// src/ShadowDusk.HLSL/Dxc/IDxcShaderCompiler.cs
#nullable enable
namespace ShadowDusk.HLSL.Dxc;

public interface IDxcShaderCompiler
{
    Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default);
}
```

`DxcShaderCompiler` is the concrete implementation. It holds one `IDxcCompiler3` instance; callers must not share a single `DxcShaderCompiler` across threads (see section 6).

---

## 3. Per-Platform DXC Flag Assembly

### 3.1 Flag Table

> **⚠️ Implementation deviation — DirectX profiles:** The original plan specified `vs_5_0`/`ps_5_0` for DirectX. DXC rejects SM5 profiles for non-SPIRV output (`error: invalid profile vs_5_0`). DXC only emits SM6 DXIL. The implemented profiles are `vs_6_0`/`ps_6_0`. See the [DXC SM5 constraint note in plan.md](../plan.md#-known-constraint-dxc-cannot-produce-sm5-dxbc) for the full analysis and the `vkd3d-shader` path to true SM5 DXBC support.

| Platform | Stage | Profile (planned) | Profile (implemented) | Additional flags |
|----------|-------|-------------------|-----------------------|-----------------|
| OpenGL | Vertex | `vs_5_0` | `vs_5_0` | `-spirv -fvk-use-dx-layout -fvk-use-dx-position-w` |
| OpenGL | Pixel | `ps_5_0` | `ps_5_0` | `-spirv -fvk-use-dx-layout -auto-binding-space 1` |
| Vulkan | Vertex | `vs_6_0` | `vs_6_0` | `-spirv -fvk-use-dx-layout -fvk-invert-y -fvk-use-dx-position-w -fspv-reflect` |
| Vulkan | Pixel | `ps_6_0` | `ps_6_0` | `-spirv -fvk-use-dx-layout -auto-binding-space 1 -fspv-reflect` |
| DirectX | Vertex | `vs_5_0` ❌ | `vs_6_0` (DXIL) | _(none beyond `-T` and `-E`)_ |
| DirectX | Pixel | `ps_5_0` ❌ | `ps_6_0` (DXIL) | _(none beyond `-T` and `-E`)_ |

### 3.2 Flag Rationale

| Flag | Purpose |
|------|---------|
| `-spirv` | Emit SPIR-V instead of DXBC |
| `-fvk-invert-y` | Flip NDC Y axis: DirectX is Y-up, OpenGL/Vulkan is Y-down; vertex stage only |
| `-fvk-use-dx-position-w` | Match DirectX W convention for homogeneous clip coordinates; vertex stage only |
| `-fvk-use-dx-layout` | Pack cbuffers with 16-byte DirectX alignment, not Vulkan std430 |
| `-auto-binding-space 1` | Offset pixel shader resource bindings to avoid descriptor set collisions with vertex resources |
| `-fspv-reflect` | Embed SPIR-V reflection data block (OpReflectionInstruction); required for Vulkan resource introspection |

### 3.3 Implementation

Create `src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs`.

```csharp
// Returns the ordered argument list for a single DXC invocation.
// Must be pure / side-effect free (unit-testable without DXC present).
internal static class DxcFlagBuilder
{
    public static IReadOnlyList<string> Build(
        PlatformTarget platform,
        ShaderStage stage,
        string entryPoint,
        IReadOnlyList<(string Name, string? Value)> macros);
}
```

Rules:
1. Always prepend `-E <entryPoint>`.
2. Always append `-T <profile>` derived from the table above.
3. Append platform+stage flags from the table above in the order listed.
4. Append one `-D <Name>=<Value>` (or `-D <Name>`) per macro, in declaration order.
5. Always append `-Zpr` (row-major matrix packing — matches MonoGame convention).
6. Always append `-WX` (treat warnings as errors) unless `DxcCompileOptions.AllowWarnings` is set.
7. Append `-Zi -Qembed_debug` only when `DxcCompileOptions.EmbedDebugInfo` is set.

---

## 4. `DxcShaderCompiler` Implementation

File: `src/ShadowDusk.HLSL/Dxc/DxcShaderCompiler.cs`

### 4.1 Compilation Sequence

1. Validate `request` fields (non-null source, non-empty entry point, valid stage/platform combination). Return `Result.Failure(ShaderError.InvalidRequest(...))` for invalid input — do not throw.
2. Build the argument list via `DxcFlagBuilder.Build(...)`.
3. Acquire the `IDxcCompiler3` instance (created once in constructor via `Dxc.CreateDxcCompiler3()`).
4. Create a `DxcBuffer` wrapping the UTF-8 encoded HLSL source bytes.
5. Call `IDxcCompiler3.Compile(ref sourceBuffer, arguments, includeHandler)`.
6. Retrieve `IDxcResult` and check `HRESULT`.
7. Call `IDxcResult.GetOutput(DXC_OUT_ERRORS)` first — always extract and reformat diagnostics regardless of success/failure.
8. On failure: parse diagnostics, return `Result.Failure(ShaderError)`.
9. On success: call `IDxcResult.GetOutput(DXC_OUT_OBJECT)` to retrieve the blob bytes.
10. Return `Result.Success(new PlatformBlob(kind, bytes))`.

### 4.2 Output Kind Selection

```csharp
BlobKind kind = platform == PlatformTarget.DirectX_11 ? BlobKind.Dxbc : BlobKind.Spirv;
```

### 4.3 Async Wrapper

DXC's managed bindings are synchronous. Wrap the synchronous call in `Task.Run` to keep the async contract and avoid blocking the calling thread.

```csharp
public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
    DxcCompileRequest request,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    return Task.Run(() => CompileCore(request), cancellationToken);
}
```

---

## 5. Error Reformatting

DXC emits diagnostics in Clang format:

```
<source>:<line>:<col>: error: <message>
```

MGCB expects FXC format:

```
<Filename>(line,col-col): error X####: <message>
```

### 5.1 Reformatter

File: `src/ShadowDusk.HLSL/Dxc/DxcDiagnosticReformatter.cs`

```csharp
internal static class DxcDiagnosticReformatter
{
    /// <summary>
    /// Parses DXC's IDxcBlobUtf8 error text and returns a list of ShaderError
    /// instances formatted in FXC style.
    /// </summary>
    public static IReadOnlyList<ShaderError> Reformat(string dxcErrorText, string sourceFileName);
}
```

Reformat rules:
1. Split on newlines; process each non-empty line.
2. Match the pattern `^(.+):(\d+):(\d+):\s+(error|warning|note):\s+(.+)$`.
3. Map to FXC format: `{sourceFileName}({line},{col}-{col}): error X0000: {message}`.
4. Lines that do not match the pattern are included verbatim in a fallback `ShaderError.Raw` field.
5. Preserve warning vs. error distinction in `ShaderError.Severity`.

### 5.2 `ShaderError` additions

Ensure `ShaderError` (in `ShadowDusk.Core`) has:
- `Severity` enum: `Error`, `Warning`, `Note`
- `SourceFile`, `Line`, `Column` properties
- `FxcFormattedMessage` computed property that renders the FXC string

---

## 6. Thread Safety

`IDxcCompiler3` is not thread-safe. The following rules apply:

- `DxcShaderCompiler` must not be shared across concurrent compilations.
- The orchestrator in `ShadowDusk.Core` must create one `DxcShaderCompiler` per parallel worker (or serialize via a lock / channel).
- Document this constraint in XML doc on `DxcShaderCompiler` and in the `IDxcShaderCompiler` interface.
- A future `DxcShaderCompilerPool` may manage a fixed-size pool of compiler instances; defer to Phase 7.

---

## 7. File Layout

```
src/ShadowDusk.HLSL/
└── Dxc/
    ├── BlobKind.cs
    ├── PlatformBlob.cs
    ├── DxcCompileRequest.cs
    ├── DxcCompileOptions.cs          # optional flags (AllowWarnings, EmbedDebugInfo)
    ├── IDxcShaderCompiler.cs
    ├── DxcShaderCompiler.cs
    ├── DxcFlagBuilder.cs             # pure static; no DXC dependency
    └── DxcDiagnosticReformatter.cs   # pure static; no DXC dependency

src/ShadowDusk.Core/
└── Errors/
    └── ShaderError.cs                # extend with Severity, SourceFile, Line, Column
```

---

## 8. Unit Tests

File: `tests/ShadowDusk.HLSL.Tests/Dxc/DxcFlagBuilderTests.cs`
File: `tests/ShadowDusk.HLSL.Tests/Dxc/DxcDiagnosticReformatterTests.cs`

These tests must be pure — no DXC binary, no disk, no process execution.

### 8.1 `DxcFlagBuilderTests` checklist

- [ ] OpenGL vertex stage produces `-spirv`, `-fvk-use-dx-position-w`, `-fvk-use-dx-layout`, profile `vs_5_0`; does NOT contain `-fvk-invert-y` (SPIRV-Cross handles Y-flip via `FlipVertexY` option to avoid double flip)
- [ ] OpenGL pixel stage produces `-spirv`, `-fvk-use-dx-layout`, `-auto-binding-space 1`, profile `ps_5_0`; does NOT contain `-fvk-invert-y`
- [ ] Vulkan vertex stage produces all OpenGL vertex flags plus `-fspv-reflect`, profile `vs_6_0`
- [ ] Vulkan pixel stage produces all OpenGL pixel flags plus `-fspv-reflect`, profile `ps_6_0`
- [ ] DirectX_11 vertex stage produces `vs_5_0`; does NOT contain `-spirv`
- [ ] DirectX_11 pixel stage produces `ps_5_0`; does NOT contain `-spirv`
- [ ] Macros are appended as `-D Name=Value` for keyed macros and `-D Name` for flag macros
- [ ] Entry point appears as `-E <entryPoint>` before the profile argument
- [ ] `-Zpr` always present
- [ ] `-WX` present by default; absent when `AllowWarnings = true`

### 8.2 `DxcDiagnosticReformatterTests` checklist

- [ ] Well-formed Clang diagnostic line parses to correct file/line/col/message
- [ ] FXC-formatted output matches `Filename.fx(line,col-col): error X0000: message`
- [ ] Warning severity mapped to `ShaderError.Severity.Warning`
- [ ] Unknown/non-matching lines preserved as `ShaderError.Raw`
- [ ] Empty input returns empty list
- [ ] Multi-line error block with note lines handled correctly

---

## 9. Integration Tests

File: `tests/ShadowDusk.Integration.Tests/Dxc/DxcShaderCompilerIntegrationTests.cs`

All integration tests must be tagged `[Trait("Category", "Integration")]`.

### 9.1 Minimal SPIR-V round-trip (acceptance criterion)

```csharp
[Fact]
[Trait("Category", "Integration")]
public async Task CompileMinimalVertex_OpenGL_ReturnsSpirvBlob()
{
    const string hlsl = """
        float4 VSMain(float4 pos : POSITION) : SV_Position { return pos; }
        """;

    var compiler = new DxcShaderCompiler();
    var request = new DxcCompileRequest
    {
        HlslSource     = hlsl,
        SourceFileName = "minimal.fx",
        EntryPoint     = "VSMain",
        Stage          = ShaderStage.Vertex,
        Platform       = PlatformTarget.OpenGL,
    };

    var result = await compiler.CompileAsync(request);

    result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
    result.Value.Kind.Should().Be(BlobKind.Spirv);
    result.Value.Bytes.Length.Should().BeGreaterThan(0);
}
```

### 9.2 Full integration test checklist

- [ ] Minimal vertex shader compiles to non-empty SPIR-V on OpenGL target
- [ ] Minimal pixel shader compiles to non-empty SPIR-V on OpenGL target
- [ ] Minimal vertex shader compiles to non-empty DXBC on DirectX_11 target
- [ ] Syntax error in HLSL returns `Result.Failure` with a `ShaderError` whose `FxcFormattedMessage` contains `(line,col`
- [ ] Undefined variable in pixel shader returns `Result.Failure` with line/col in FXC format
- [ ] Vulkan target vertex shader compiles to non-empty SPIR-V
- [ ] Compile with a valid `-D` macro (e.g., conditional `#ifdef`) succeeds and macro is visible to DXC
- [ ] Cancellation before invocation returns `OperationCanceledException` (not `ShaderError`)

---

## 10. Acceptance Criteria

| # | Criterion | Verified by |
|---|-----------|-------------|
| 1 | `Vortice.Dxc` added to `ShadowDusk.HLSL.csproj` and pinned in `Directory.Packages.props` | `/build` green on all three RIDs |
| 2 | All per-platform flag sets assembled correctly | Unit tests 8.1 |
| 3 | DXC errors reformatted to FXC format (`Filename(line,col-col): error X####: ...`) | Unit tests 8.2 |
| 4 | `Result<PlatformBlob, ShaderError>` returned; no exceptions thrown for shader errors | Unit + integration tests |
| 5 | Integration test compiles `VSMain` HLSL snippet as `vs_5_0 -spirv` and receives non-empty blob | Integration test 9.2 item 1 |
| 6 | Syntax error input returns `Result.Failure` with correctly formatted `ShaderError` | Integration test 9.2 item 4 |
| 7 | Thread-safety constraint documented on `DxcShaderCompiler` and `IDxcShaderCompiler` | Code review |
| 8 | `/build` produces no new warnings or errors | CI |
| 9 | `/test` passes all new unit and integration tests | CI |

---

## 11. Task Checklist

### Setup

- [x] 1. Add `Vortice.Dxc` NuGet reference to `ShadowDusk.HLSL.csproj`
- [x] 2. Pin version in `Directory.Packages.props` — v3.3.4
- [x] 3. Verify all three RIDs build successfully with `/build`
- [x] 4. Remove any conflicting DXC download steps from `tools/restore.sh` / `tools/restore.ps1` — no restore scripts existed; DXC ships via NuGet, nothing to conflict

### Core types

- [x] 5. Create `BlobKind.cs` and `PlatformBlob.cs`
- [x] 6. Create `DxcCompileRequest.cs`
- [x] 7. Create `DxcCompileOptions.cs` (AllowWarnings, EmbedDebugInfo booleans)
- [x] 8. Create `IDxcShaderCompiler.cs`
- [x] 9. Extend `ShaderError` in `ShadowDusk.Core` — added `Note` to `ShaderErrorSeverity`; added `FxcFormattedMessage` computed property (`File`, `Line`, `Column` were already present)

### Flag assembly

- [x] 10. Implement `DxcFlagBuilder.Build(...)` — note: DirectX profiles use `vs_6_0`/`ps_6_0` (not `vs_5_0`) because DXC rejects SM5 for non-SPIRV targets; see constraint note
- [x] 11. Write all `DxcFlagBuilderTests` from checklist 8.1 — 25 unit tests, all passing

### Compiler

- [x] 12. Implement `DxcDiagnosticReformatter.Reformat(...)` per section 5
- [x] 13. Write all `DxcDiagnosticReformatterTests` from checklist 8.2 — 8 unit tests, all passing
- [x] 14. Implement `DxcShaderCompiler.CompileCore(...)` synchronous inner method
- [x] 15. Implement `DxcShaderCompiler.CompileAsync(...)` async wrapper
- [x] 16. Add thread-safety XML doc comment to `DxcShaderCompiler` and `IDxcShaderCompiler`

### Integration tests

- [x] 17. Scaffold `DxcShaderCompilerIntegrationTests.cs` with `[Trait("Category","Integration")]`
- [x] 18. Implement all integration tests from checklist 9.2 — 9 integration tests, all passing
- [x] 19. Confirm integration tests are excluded from unit-only test runs via filter `Category!=Integration` — verified

### CI / verification

- [x] 20. Run `/build` — zero warnings, zero errors
- [x] 21. Run `/test` — 60 unit tests pass
- [x] 22. Run `/test --filter "Category=Integration"` — 9 integration tests pass
- [x] 23. Run `/platform-check` — no new platform-specific assumptions in Phase 4 code

---

## 12. Dependencies on Earlier Phases

| This phase needs | Provided by |
|-----------------|-------------|
| Preprocessed HLSL source string | Phase 3 (HlslPreprocessor) |
| Per-pass entry point names (vertex + pixel) | Phase 2 (FX9 pre-parser) |
| `IDxcIncludeHandler` forwarding | Phase 3 include resolver |
| Platform macro `-D` definitions | Phase 3 macro pipeline |
| `ShaderError` base type and `Result<T,E>` | Phase 1 (ShadowDusk.Core) |
| `PlatformTarget` and `ShaderStage` enums | Phase 1 (ShadowDusk.Core) |

> **Note:** `ShaderStage` and `PlatformTarget` enums are defined in `ShadowDusk.Core` (Phase 1) — do not redefine them here.

## 13. What Phase 5 Consumes from This Phase

| Artifact | Consumer |
|----------|----------|
| `PlatformBlob` with `BlobKind.Spirv` | Phase 5 SPIRV-Cross transpiler (GLSL/MSL emission) |
| `PlatformBlob` with `BlobKind.Dxbc` | Phase 5 DirectX_11 `.mgfx` packer |
| `ShaderError` with FXC-formatted message | Phase 5 diagnostic pass-through to MGCB |
