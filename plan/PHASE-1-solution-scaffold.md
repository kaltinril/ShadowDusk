> **Status: COMPLETE** — Solution scaffolded, build green (0 warnings), 1 test passing.
> Notable deviation: `dotnet new sln` under SDK 10.0.300 created `ShadowDusk.slnx` (modern XML format) rather than the legacy `.sln` format. All `dotnet sln` commands work identically. `Vortice.Dxc` pinned to `3.3.4` (3.3.1 was no longer resolvable without a NU1601 warning-as-error). `ShadowDusk.Wasm` project added post-scaffold for WASM/KNI browser target.

# Phase 1 — Solution Scaffold

**Goal:** Create the full .NET 8 solution structure, wire up project references, add NuGet dependencies, configure the test framework, and verify `dotnet build` + `dotnet test` are green on every platform with zero warnings.

This phase produces no functional shader code. Every class, method, and type is a stub. The only acceptance gate is a clean build and a passing (0-failed) test run.

---

## 1. Prerequisites

| Requirement | Version | Check |
|---|---|---|
| .NET SDK | 8.0 LTS (≥ 8.0.100) | `dotnet --version` |
| Git | any | repo already cloned |
| OS | Linux, macOS, or Windows | all three must work |

No native tool restore is required in this phase. DXC binaries come from the `Vortice.Dxc` NuGet package automatically.

---

## 2. Directory Structure to Create

```
ShadowDusk/
├── ShadowDusk.sln
├── src/
│   ├── ShadowDusk.Core/
│   │   └── ShadowDusk.Core.csproj
│   ├── ShadowDusk.HLSL/
│   │   └── ShadowDusk.HLSL.csproj
│   ├── ShadowDusk.GLSL/
│   │   └── ShadowDusk.GLSL.csproj
│   ├── ShadowDusk.Metal/
│   │   └── ShadowDusk.Metal.csproj
│   ├── ShadowDusk.Cli/
│   │   └── ShadowDusk.Cli.csproj
│   └── ShadowDusk.MgcbPlugin/
│       └── ShadowDusk.MgcbPlugin.csproj
└── tests/
    ├── ShadowDusk.Core.Tests/
    │   └── ShadowDusk.Core.Tests.csproj
    ├── ShadowDusk.HLSL.Tests/
    │   └── ShadowDusk.HLSL.Tests.csproj
    ├── ShadowDusk.GLSL.Tests/
    │   └── ShadowDusk.GLSL.Tests.csproj
    └── ShadowDusk.Integration.Tests/
        └── ShadowDusk.Integration.Tests.csproj
```

---

## 3. Shared MSBuild Properties

Create `Directory.Build.props` at the repo root. Every project inherits these without repeating them.

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningLevel>9999</WarningLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Authors>ShadowDusk Contributors</Authors>
    <RepositoryUrl>https://github.com/shadowdusk/ShadowDusk</RepositoryUrl>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props` at the repo root to centralise NuGet version pins (Central Package Management):

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup Label="DXC">
    <PackageVersion Include="Vortice.Dxc" Version="3.3.1" />
    <PackageVersion Include="Vortice.Direct3D12" Version="3.5.0" />
  </ItemGroup>

  <ItemGroup Label="Test">
    <PackageVersion Include="xunit"                        Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio"    Version="2.8.2" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk"       Version="17.11.1" />
    <PackageVersion Include="FluentAssertions"             Version="6.12.2" />
    <PackageVersion Include="coverlet.collector"           Version="6.0.3" />
  </ItemGroup>
</Project>
```

> **Version pin rationale:** `Vortice.Dxc 3.3.x` is the latest stable line that bundles DXC 1.8 for win-x64, linux-x64, and osx-arm64/x64. Pin exact versions now; bumps are a deliberate act.

---

## 4. Project Files

### 4.1 ShadowDusk.Core

```xml
<!-- src/ShadowDusk.Core/ShadowDusk.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Core</AssemblyName>
    <RootNamespace>ShadowDusk.Core</RootNamespace>
  </PropertyGroup>
</Project>
```

**Stub types to create in this project (one file each):**

| File | Type | Notes |
|---|---|---|
| `Result.cs` | `Result<T, TError>` discriminated union | See Section 6 |
| `ShaderError.cs` | `sealed record ShaderError` | See Section 6 |
| `ShaderIR.cs` | `sealed class ShaderIR` | Empty shell |
| `CompilerOptions.cs` | `sealed class CompilerOptions` | Empty shell |
| `IShaderCompiler.cs` | `interface IShaderCompiler` | Empty shell |
| `PlatformTarget.cs` | `enum PlatformTarget` | `DirectX, OpenGL, Metal, Vulkan` |
| `ShaderStage.cs` | `enum ShaderStage` | `Vertex, Pixel` |

### 4.2 ShadowDusk.HLSL

```xml
<!-- src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.HLSL</AssemblyName>
    <RootNamespace>ShadowDusk.HLSL</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Vortice.Dxc" />
  </ItemGroup>
</Project>
```

**Stub types:**

| File | Type | Notes |
|---|---|---|
| `DxcCompiler.cs` | `sealed class DxcCompiler` | Empty shell; will wrap `Vortice.Dxc.DxcCompiler` in Phase 4 |
| `FxFileParser.cs` | `sealed class FxFileParser` | Empty shell; Phase 2 |

### 4.3 ShadowDusk.GLSL

```xml
<!-- src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.GLSL</AssemblyName>
    <RootNamespace>ShadowDusk.GLSL</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
  </ItemGroup>
</Project>
```

**Stub types:**

| File | Type | Notes |
|---|---|---|
| `SpirvCrossTranspiler.cs` | `sealed class SpirvCrossTranspiler` | Empty shell; P/Invoke in Phase 6 |
| `GlslEmitter.cs` | `sealed class GlslEmitter` | Empty shell |

### 4.4 ShadowDusk.Metal

**Scope note:** Metal support is out of scope until the OpenGL path is complete. This project exists only to reserve the namespace and keep the reference graph correct.

```xml
<!-- src/ShadowDusk.Metal/ShadowDusk.Metal.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Metal</AssemblyName>
    <RootNamespace>ShadowDusk.Metal</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
  </ItemGroup>
</Project>
```

**Stub types:**

| File | Type | Notes |
|---|---|---|
| `MslEmitter.cs` | `sealed class MslEmitter` | Stub only — no implementation until Phase 6+ |

### 4.5 ShadowDusk.Cli

```xml
<!-- src/ShadowDusk.Cli/ShadowDusk.Cli.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Cli</AssemblyName>
    <RootNamespace>ShadowDusk.Cli</RootNamespace>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>mgfxc</ToolCommandName>
    <PackageId>ShadowDusk.Cli</PackageId>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
    <ProjectReference Include="..\ShadowDusk.HLSL\ShadowDusk.HLSL.csproj" />
    <ProjectReference Include="..\ShadowDusk.GLSL\ShadowDusk.GLSL.csproj" />
    <ProjectReference Include="..\ShadowDusk.Metal\ShadowDusk.Metal.csproj" />
  </ItemGroup>
</Project>
```

**Stub entry point (`Program.cs`):**

```csharp
// src/ShadowDusk.Cli/Program.cs
#nullable enable

// Phase 8 will implement full mgfxc-compatible argument parsing.
Console.Error.WriteLine("ShadowDusk mgfxc — not yet implemented.");
return 1;
```

### 4.6 ShadowDusk.MgcbPlugin

**Scope note:** MGCB plugin integration is a stub. Full implementation is a separate future undertaking (see `plan.md`).

```xml
<!-- src/ShadowDusk.MgcbPlugin/ShadowDusk.MgcbPlugin.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.MgcbPlugin</AssemblyName>
    <RootNamespace>ShadowDusk.MgcbPlugin</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
  </ItemGroup>
</Project>
```

---

## 5. Test Project Files

All test projects share this common structure (shown once, repeated for each):

```xml
<ItemGroup Label="Test framework">
  <PackageReference Include="xunit" />
  <PackageReference Include="xunit.runner.visualstudio" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" />
  <PackageReference Include="FluentAssertions" />
  <PackageReference Include="coverlet.collector">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
</ItemGroup>
```

### 5.1 ShadowDusk.Core.Tests

```xml
<!-- tests/ShadowDusk.Core.Tests/ShadowDusk.Core.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Core.Tests</AssemblyName>
    <RootNamespace>ShadowDusk.Core.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ShadowDusk.Core\ShadowDusk.Core.csproj" />
  </ItemGroup>

  <!-- test framework block from above -->
</Project>
```

### 5.2 ShadowDusk.HLSL.Tests

```xml
<!-- tests/ShadowDusk.HLSL.Tests/ShadowDusk.HLSL.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.HLSL.Tests</AssemblyName>
    <RootNamespace>ShadowDusk.HLSL.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ShadowDusk.HLSL\ShadowDusk.HLSL.csproj" />
  </ItemGroup>

  <!-- test framework block from above -->
</Project>
```

### 5.3 ShadowDusk.GLSL.Tests

```xml
<!-- tests/ShadowDusk.GLSL.Tests/ShadowDusk.GLSL.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.GLSL.Tests</AssemblyName>
    <RootNamespace>ShadowDusk.GLSL.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ShadowDusk.GLSL\ShadowDusk.GLSL.csproj" />
  </ItemGroup>

  <!-- test framework block from above -->
</Project>
```

### 5.4 ShadowDusk.Integration.Tests

```xml
<!-- tests/ShadowDusk.Integration.Tests/ShadowDusk.Integration.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Integration.Tests</AssemblyName>
    <RootNamespace>ShadowDusk.Integration.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Integration tests drive the full pipeline via Core -->
    <ProjectReference Include="..\..\src\ShadowDusk.Core\ShadowDusk.Core.csproj" />
    <ProjectReference Include="..\..\src\ShadowDusk.HLSL\ShadowDusk.HLSL.csproj" />
    <ProjectReference Include="..\..\src\ShadowDusk.GLSL\ShadowDusk.GLSL.csproj" />
  </ItemGroup>

  <!-- test framework block from above -->
</Project>
```

**Placeholder integration test** — ensures `dotnet test` registers the project even with 0 real tests:

```csharp
// tests/ShadowDusk.Integration.Tests/PlaceholderTest.cs
#nullable enable
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Placeholder so the test runner registers this assembly.
/// Real integration tests are added in Phase 9.
/// All integration tests must carry [Trait("Category","Integration")].
/// </summary>
public sealed class PlaceholderTest
{
    [Fact]
    [Trait("Category", "Integration")]
    public void Placeholder_AlwaysPasses() { }
}
```

---

## 6. Core Stub Types

These stubs live in `ShadowDusk.Core` and are referenced by every subsequent phase.

### 6.1 Result discriminated union (`Result.cs`)

```csharp
// src/ShadowDusk.Core/Result.cs
#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Lightweight discriminated union. Never throw exceptions for expected shader errors;
/// return a Result instead.
/// </summary>
public readonly struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    private Result(T value)  { _value = value; _error = default; IsSuccess = true; }
    private Result(TError error) { _value = default; _error = error; IsSuccess = false; }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value  => IsSuccess  ? _value!  : throw new InvalidOperationException("Result is a failure.");
    public TError Error => IsFailure ? _error! : throw new InvalidOperationException("Result is a success.");

    public static Result<T, TError> Ok(T value)       => new(value);
    public static Result<T, TError> Fail(TError error) => new(error);

    public Result<TNext, TError> Bind<TNext>(Func<T, Result<TNext, TError>> f)
        => IsSuccess ? f(_value!) : Result<TNext, TError>.Fail(_error!);

    public Result<TNext, TError> Map<TNext>(Func<T, TNext> f)
        => IsSuccess ? Result<TNext, TError>.Ok(f(_value!)) : Result<TNext, TError>.Fail(_error!);
}
```

### 6.2 ShaderError (`ShaderError.cs`)

```csharp
// src/ShadowDusk.Core/ShaderError.cs
#nullable enable

namespace ShadowDusk.Core;

/// <summary>Structured compiler diagnostic. Maps to a single compiler error or warning.</summary>
public sealed record ShaderError(
    string File,
    int    Line,
    int    Column,
    string Code,
    string Message,
    ShaderErrorSeverity Severity = ShaderErrorSeverity.Error
);

public enum ShaderErrorSeverity { Warning, Error }
```

### 6.3 PlatformTarget (`PlatformTarget.cs`)

```csharp
// src/ShadowDusk.Core/PlatformTarget.cs
#nullable enable

namespace ShadowDusk.Core;

public enum PlatformTarget
{
    DirectX  = 0,   // DXBC via FXC (legacy) — not primary path
    OpenGL   = 1,   // SPIR-V → GLSL via SPIRV-Cross
    Metal    = 2,   // SPIR-V → MSL via SPIRV-Cross (out of scope until Phase 6+)
    Vulkan   = 3,   // SPIR-V direct (future)
}
```

### 6.4 ShaderStage (`ShaderStage.cs`)

```csharp
// src/ShadowDusk.Core/ShaderStage.cs
#nullable enable
namespace ShadowDusk.Core;
public enum ShaderStage { Vertex = 0, Pixel = 1 }
```

### 6.5 Remaining empty stubs

Each file needs only the namespace declaration and an empty class/interface to satisfy the compiler.

**`ShaderIR.cs`**
```csharp
// src/ShadowDusk.Core/ShaderIR.cs
#nullable enable
namespace ShadowDusk.Core;
/// <summary>Internal representation between parsed HLSL and platform emission. Populated in Phase 2+.</summary>
public sealed class ShaderIR { }
```

**`CompilerOptions.cs`**
```csharp
// src/ShadowDusk.Core/CompilerOptions.cs
#nullable enable
namespace ShadowDusk.Core;
/// <summary>Options passed to the compiler pipeline. Populated in Phase 3+.</summary>
public sealed class CompilerOptions { }
```

**`IShaderCompiler.cs`**
```csharp
// src/ShadowDusk.Core/IShaderCompiler.cs
#nullable enable
namespace ShadowDusk.Core;
/// <summary>Contract for platform-specific shader compilation. Implemented in Phase 4+.</summary>
public interface IShaderCompiler { }
```

---

## 7. Project Reference Graph

```
ShadowDusk.Core
    ▲
    ├── ShadowDusk.HLSL      (+ Vortice.Dxc)
    ├── ShadowDusk.GLSL
    ├── ShadowDusk.Metal
    └── ShadowDusk.MgcbPlugin

ShadowDusk.Cli
    references Core, HLSL, GLSL, Metal

ShadowDusk.Core.Tests          → Core
ShadowDusk.HLSL.Tests          → HLSL (transitive: Core)
ShadowDusk.GLSL.Tests          → GLSL (transitive: Core)
ShadowDusk.Integration.Tests   → Core, HLSL, GLSL
```

No circular references. `ShadowDusk.Metal` and `ShadowDusk.MgcbPlugin` do NOT reference each other or the Cli.

---

## 8. Solution File

Create `ShadowDusk.sln` at the repo root using `dotnet sln` commands:

```powershell
# Run from repo root
dotnet new sln -n ShadowDusk

# src projects
dotnet sln add src/ShadowDusk.Core/ShadowDusk.Core.csproj
dotnet sln add src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj
dotnet sln add src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj
dotnet sln add src/ShadowDusk.Metal/ShadowDusk.Metal.csproj
dotnet sln add src/ShadowDusk.Cli/ShadowDusk.Cli.csproj
dotnet sln add src/ShadowDusk.MgcbPlugin/ShadowDusk.MgcbPlugin.csproj

# test projects
dotnet sln add tests/ShadowDusk.Core.Tests/ShadowDusk.Core.Tests.csproj
dotnet sln add tests/ShadowDusk.HLSL.Tests/ShadowDusk.HLSL.Tests.csproj
dotnet sln add tests/ShadowDusk.GLSL.Tests/ShadowDusk.GLSL.Tests.csproj
dotnet sln add tests/ShadowDusk.Integration.Tests/ShadowDusk.Integration.Tests.csproj
```

> The `dotnet sln add` commands place projects in the solution root. If you want solution folders (optional), add `--solution-folder src` / `--solution-folder tests` flags.

---

## 9. `.editorconfig`

Create `.editorconfig` at the repo root. Enforces style uniformly so `TreatWarningsAsErrors` does not fire on trivial style drift.

```ini
root = true

[*.cs]
indent_style             = space
indent_size              = 4
end_of_line              = lf
charset                  = utf-8
trim_trailing_whitespace = true
insert_final_newline     = true

# Language preferences
csharp_style_namespace_declarations          = file_scoped:warning
csharp_prefer_simple_using_statement         = true:warning
csharp_style_prefer_primary_constructors     = false:suggestion
dotnet_sort_system_directives_first          = true
dotnet_separate_import_directive_groups      = false

# Nullable
dotnet_diagnostic.CS8600.severity = error
dotnet_diagnostic.CS8601.severity = error
dotnet_diagnostic.CS8602.severity = error
dotnet_diagnostic.CS8603.severity = error
dotnet_diagnostic.CS8604.severity = error
dotnet_diagnostic.CS8618.severity = error
dotnet_diagnostic.CS8625.severity = error
```

---

## 10. `.gitignore` Additions

Ensure the following patterns are present in `.gitignore` (create the file if absent):

```gitignore
# .NET build outputs
bin/
obj/
*.user
*.suo
.vs/
.idea/

# NuGet
*.nupkg
*.snupkg
packages/

# dotnet tool manifest lock
# (keep .config/dotnet-tools.json — that IS checked in)

# Native binary restore output (Phase 10)
tools/dxc/
tools/spirv-cross/

# Test results
TestResults/
coverage/
```

---

## 11. Numbered Task Checklist

Execute these steps in order. Each step is independently verifiable.

1. - [ ] Confirm `dotnet --version` reports `8.0.x`.
2. - [ ] Create `Directory.Build.props` at repo root (Section 3).
3. - [ ] Create `Directory.Packages.props` at repo root (Section 3).
4. - [ ] Create `.editorconfig` at repo root (Section 9).
5. - [ ] Update `.gitignore` (Section 10).
6. - [ ] Create `src/ShadowDusk.Core/` directory and `.csproj` (Section 4.1).
7. - [ ] Create stub source files in `ShadowDusk.Core`: `Result.cs`, `ShaderError.cs`, `PlatformTarget.cs`, `ShaderStage.cs`, `ShaderIR.cs`, `CompilerOptions.cs`, `IShaderCompiler.cs` (Section 6).
8. - [ ] Create `src/ShadowDusk.HLSL/` directory and `.csproj` (Section 4.2).
9. - [ ] Create `src/ShadowDusk.HLSL/DxcCompiler.cs` and `FxFileParser.cs` (empty stubs).
10. - [ ] Create `src/ShadowDusk.GLSL/` directory and `.csproj` (Section 4.3).
11. - [ ] Create `src/ShadowDusk.GLSL/SpirvCrossTranspiler.cs` and `GlslEmitter.cs` (empty stubs).
12. - [ ] Create `src/ShadowDusk.Metal/` directory and `.csproj` (Section 4.4).
13. - [ ] Create `src/ShadowDusk.Metal/MslEmitter.cs` (empty stub).
14. - [ ] Create `src/ShadowDusk.Cli/` directory and `.csproj` (Section 4.5).
15. - [ ] Create `src/ShadowDusk.Cli/Program.cs` with the not-implemented stub (Section 4.5).
16. - [ ] Create `src/ShadowDusk.MgcbPlugin/` directory and `.csproj` (Section 4.6).
17. - [ ] Create `tests/ShadowDusk.Core.Tests/` directory and `.csproj` (Section 5.1).
18. - [ ] Create `tests/ShadowDusk.HLSL.Tests/` directory and `.csproj` (Section 5.2).
19. - [ ] Create `tests/ShadowDusk.GLSL.Tests/` directory and `.csproj` (Section 5.3).
20. - [ ] Create `tests/ShadowDusk.Integration.Tests/` directory and `.csproj` (Section 5.4).
21. - [ ] Create `tests/ShadowDusk.Integration.Tests/PlaceholderTest.cs` (Section 5.4).
22. - [ ] Run `dotnet new sln -n ShadowDusk` then all `dotnet sln add` commands (Section 8).
23. - [ ] Run `dotnet restore` — verify no errors.
24. - [ ] Generate and commit `packages.lock.json` files by running `dotnet restore`, then `git add **/packages.lock.json` and commit.
25. - [ ] Run `dotnet build --configuration Release` — verify 0 errors, 0 warnings.
26. - [ ] Run `dotnet test` — verify 0 failed, at least 1 passed (the placeholder).
27. - [ ] Commit: `git add -A && git commit -m "Phase 1: solution scaffold"`.

---

## 12. Acceptance Criteria

| Criterion | How to verify |
|---|---|
| `dotnet build` green with 0 warnings | `dotnet build -warnaserror` exits 0 |
| `dotnet test` green with 0 failures | `dotnet test` output shows `Passed: N, Failed: 0` |
| Solution file at repo root | `ShadowDusk.sln` exists |
| All projects target `net8.0` | `Directory.Build.props` inheritance; spot-check with `dotnet msbuild -t:GetTargetFrameworks` |
| `<Nullable>enable</Nullable>` everywhere | Inherited from `Directory.Build.props`; no per-project override needed |
| No circular references | `dotnet build` would fail; also verify manually against Section 7 |
| Works on Linux, macOS, Windows | Run the build on each OS (CI in Phase 10; manual verification now) |

---

## 13. Known Gaps (deferred to later phases)

| Gap | Phase |
|---|---|
| FX9 technique/pass parser | 2 |
| `#include` flattening and macro injection | 3 |
| Actual DXC compilation via `Vortice.Dxc` | 4 |
| Shader reflection | 5 |
| SPIRV-Cross P/Invoke and GLSL/MSL emission | 6 |
| `.mgfx` binary serialization | 7 |
| mgfxc-compatible CLI argument parsing | 8 |
| Real integration tests with `.fx` fixtures | 9 |
| CI matrix (GitHub Actions, RID restore) | 10 |
| Metal/MSL implementation | post-Phase 6 |
| Full MGCB plugin | post-Phase 8 |
