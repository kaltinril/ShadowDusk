# Phase 8 — CLI Entry Point

**Depends on:** Phase 7 (`.mgfx` binary writer)
**Consumed by:** Phase 9 (integration tests), Phase 10 (CI matrix)

---

## Goal

Replace the Phase 1 `Program.cs` stub in `ShadowDusk.Cli` with a fully functional, mgfxc-compatible
entry point. After this phase, `mgfxc SomeShader.fx Output.mgfx /Profile:OpenGL` must run the
complete pipeline end-to-end, write a valid `.mgfx` file, exit `0` on success, and emit properly
formatted diagnostics to stderr on failure — matching the contract that MonoGame Content Builder
(MGCB) expects from its external shader compiler.

---

## Deliverables

| Artifact | Project | Notes |
|---|---|---|
| `Program.cs` | `ShadowDusk.Cli` | Top-level statements; orchestrates full pipeline |
| `CliArguments.cs` | `ShadowDusk.Cli` | Parsed, validated argument bag |
| `ArgumentParser.cs` | `ShadowDusk.Cli` | Parses `args[]` without external framework |
| `MgcbErrorFormatter.cs` | `ShadowDusk.Cli` | `ShaderError` → MGCB stderr format |
| `PipelineRunner.cs` | `ShadowDusk.Cli` | Wires Phase 2–7 in order; returns `Result` |
| `ShadowDusk.Cli.csproj` updates | `ShadowDusk.Cli` | `PackAsTool`, `ToolCommandName`, RID publish config |
| Unit tests | `ShadowDusk.Core.Tests` | Argument parsing: valid, invalid, missing |
| Integration test | `ShadowDusk.Integration.Tests` | Invoke CLI binary; assert exit 0 + output file |

---

## mgfxc CLI Contract

ShadowDusk must be a transparent drop-in for `mgfxc`. MGCB invokes it via PATH or an `ExternalTool`
config entry using this exact positional argument convention:

```
mgfxc <SourceFile> <OutputFile> [/Debug] [/Profile:<Platform>]
```

`<SourceFile>` and `<OutputFile>` are positional (order matters). All flags are optional and
may use either `/` (Windows convention) or `--` (cross-platform) prefix.

### Flag and Argument Specification

| Argument | Required | Format | Default | Notes |
|---|---|---|---|---|
| `<SourceFile>` | Yes | Positional arg 1 | — | Path to `.fx` file |
| `<OutputFile>` | Yes | Positional arg 2 | — | Path to write `.mgfx` |
| `/Profile:<Platform>` | No | `/Profile:OpenGL` or `--Profile:OpenGL` | `DirectX_11` | Target platform (see table below) |
| `/Debug` | No | Flag, no value | off | Forward `-Zi` to DXC |
| `/I <path>` | No | Repeatable | none | Additional include search path |
| `--mgfx-version <10\|11>` | No | `--mgfx-version 10` | `10` | `.mgfx` binary format version |

### Profile Values

| Profile string | `PlatformTarget` enum | In-scope |
|---|---|---|
| `DirectX_11` | `PlatformTarget.DirectX` | Yes |
| `OpenGL` | `PlatformTarget.OpenGL` | Yes |
| `PlayStation4` | — | No — emit error, exit 1 |
| `XboxOne` | — | No — emit error, exit 1 |
| `Switch` | — | No — emit error, exit 1 |

Unrecognised profile strings (not in this table) must also produce exit code 1 with a stderr
diagnostic identifying the unknown value.

---

## Process Contract

### Exit Codes

| Condition | Exit code | Output stream |
|---|---|---|
| Successful compile | `0` | Silent — nothing to stdout or stderr |
| Shader compile error | `1` | Formatted diagnostics to **stderr** |
| Bad arguments / usage error | `1` | Usage message to **stderr** |
| Unsupported platform | `1` | Error message to **stderr** |
| Unexpected exception | `1` | Exception message to **stderr**; no stack trace in release |

MGCB captures stderr to populate the IDE error list. **Nothing must be written to stdout under any
circumstances** — not even debug or informational output. All output goes to stderr.

### Silence on Success

When compilation succeeds, the process exits `0` with no output. MGCB treats any stderr output as a
warning or error. Informational messages that were useful during development must be guarded and
never appear in release builds.

---

## MGCB Error Format

MGCB's error list parser expects diagnostics in this exact format on stderr:

```
<Filename>(line,colStart-colEnd): error X####: <message text>
```

- `<Filename>` is the **base name only** (no directory path) of the source file.
- `(line,colStart-colEnd)` is 1-based; `colEnd` is inclusive.
- `error` is the literal word for errors; `warning` for non-fatal diagnostics.
- `X####` is the HLSL error code, zero-padded to 4 digits.
- The space before the code letter and after the colon are mandatory.

Example:

```
Problem.fx(11,44-55): error X4502: invalid vs_3_0 input semantic 'SV_VERTEXID'
```

When column range information is unavailable, use `colStart-colStart` (zero-width range):

```
Problem.fx(11,1-1): error X0000: unknown error
```

When line information is unavailable (e.g. argument validation errors), omit the location prefix:

```
error X0000: source file not found: 'Missing.fx'
```

### MgcbErrorFormatter

`MgcbErrorFormatter` converts a `ShaderError` (from `ShadowDusk.Core`) to the string above.

```csharp
// src/ShadowDusk.Cli/MgcbErrorFormatter.cs
#nullable enable

namespace ShadowDusk.Cli;

internal static class MgcbErrorFormatter
{
    /// <summary>
    /// Formats a ShaderError into the MGCB-compatible stderr line.
    /// Returns a string ready to write with Console.Error.WriteLine.
    /// </summary>
    public static string Format(ShaderError error);

    /// <summary>
    /// Formats a collection of errors, one per line.
    /// </summary>
    public static IEnumerable<string> FormatAll(IEnumerable<ShaderError> errors);
}
```

Rules:
1. Extract `Path.GetFileName(error.File)` for the filename segment.
2. If `error.Line > 0`: emit `filename(line,col-col): severity X####: message`. Note: `ShaderError` currently has a single `Column` field (no `ColumnEnd`); use `Column-Column` (zero-width range) for now. A future `ColumnEnd` field can be added to `ShaderError` to enable proper range output when DXC provides it.
3. If `error.Line == 0`: emit `severity X####: message` (no location).
4. `error.Severity == ShaderErrorSeverity.Warning` uses the word `warning` in place of `error`.
5. The error code must be formatted as `X` + four decimal digits, e.g. `X0001`, `X4502`. If
   `error.Code` is already in this format, pass it through unchanged. If it is a raw integer
   string, zero-pad it.

---

## Argument Parser

Do not add a framework dependency (no `System.CommandLine`, no `McMaster`, no `Cocona`). The mgfxc
argument surface is small enough to parse with a hand-written loop. This keeps the published binary
lean and avoids transitive dependency churn.

### Parsing Rules

1. Collect all tokens from `args[]`.
2. The **first non-flag token** is `SourceFile`; the **second non-flag token** is `OutputFile`.
   A token is a flag if it starts with `/` or `--`.
3. Flags are matched case-insensitively.
4. `/Profile:<value>` and `--Profile:<value>` — the value is the substring after the colon.
5. `/I <path>` and `--I <path>` — the path is the **next** token; consume it.
   `/I:<path>` and `--I:<path>` (colon form) are also accepted.
6. `/Debug` and `--Debug` set the debug flag; they take no value.
7. `--mgfx-version <value>` — the value is the next token; consume it. Only `10` and `11` are
   valid; other values produce a usage error.
8. Unknown flags are silently ignored to allow forward compatibility with future mgfxc flags that
   MGCB may pass. Document this choice — it is intentional.
9. If `SourceFile` or `OutputFile` are absent after scanning all tokens, return a usage error.

### CliArguments Record

```csharp
// src/ShadowDusk.Cli/CliArguments.cs
#nullable enable

namespace ShadowDusk.Cli;

internal sealed record CliArguments(
    string                   SourceFile,
    string                   OutputFile,
    PlatformTarget           Platform,
    bool                     Debug,
    IReadOnlyList<string>    IncludePaths,
    int                      MgfxVersion   // 10 or 11
);
```

### ArgumentParser

```csharp
// src/ShadowDusk.Cli/ArgumentParser.cs
#nullable enable

namespace ShadowDusk.Cli;

internal static class ArgumentParser
{
    /// <summary>
    /// Parses raw args[] into a CliArguments record.
    /// Returns a failure Result with a usage-error ShaderError if arguments are invalid.
    /// Never throws.
    /// </summary>
    public static Result<CliArguments, ShaderError> Parse(string[] args);

    /// <summary>Returns the usage string written to stderr on bad arguments.</summary>
    public static string UsageText { get; }
}
```

`UsageText` content:

```
Usage: mgfxc <SourceFile> <OutputFile> [options]

Options:
  /Profile:<Platform>       Target platform. Default: DirectX_11
                            Platforms: DirectX_11, OpenGL
  /Debug                    Include debug information in output
  /I <path>                 Additional include search path (repeatable)
  --mgfx-version <10|11>    Output format version. Default: 10

Unsupported platforms (exit 1): PlayStation4, XboxOne, Switch
```

---

## Pipeline Wiring

`PipelineRunner` is the single place that composes Phase 2–7. The CLI calls it and maps the result
to an exit code. This separation allows `PipelineRunner` to be tested in isolation without
subprocess overhead.

> **Note:** The interfaces (`IFxFileParser`, `IPreprocessor`, `IDxcCompiler`, `IShaderReflector`, `ISpirvCrossTranspiler`, `IMgfxWriter`) do not yet exist — only concrete implementations do. Creating these interfaces is an explicit task in this phase's checklist. If the team decides not to create interfaces (and inject concrete types instead), `PipelineRunner`'s constructor signature should be updated accordingly. The test benefit of interfaces is that `PipelineRunner`'s logic can be tested with fakes; if integration tests are the primary test mechanism, interfaces may not be worth the extra layer.

```csharp
// src/ShadowDusk.Cli/PipelineRunner.cs
#nullable enable

namespace ShadowDusk.Cli;

internal sealed class PipelineRunner
{
    public PipelineRunner(
        IIncludeResolver        includeResolver,
        IFxFileParser           fxParser,
        IPreprocessor           preprocessor,
        IDxcCompiler            dxcCompiler,
        IShaderReflector        reflector,
        ISpirvCrossTranspiler   transpiler,
        IMgfxWriter             mgfxWriter) { ... }

    /// <summary>
    /// Runs the complete compilation pipeline from source .fx to .mgfx bytes.
    /// Returns Ok(byte[]) on success, Fail(IReadOnlyList&lt;ShaderError&gt;) on any error.
    /// </summary>
    public Task<Result<byte[], IReadOnlyList<ShaderError>>> RunAsync(
        CliArguments args,
        CancellationToken ct = default);
}
```

### Pipeline Stage Order

1. Read source file from disk (`File.ReadAllTextAsync`). On `IOException`, return a `ShaderError`
   with code `X0001` and the exception message.
2. **Phase 2:** FX9 pre-parser — extract technique/pass/sampler blocks; return cleaned HLSL.
3. **Phase 3:** Preprocessor — inject platform macros, flatten `#include` directives.
4. **Phase 4:** DXC compilation — compile each pass's vertex and pixel shader entry points.
   Forward `/Debug` as `-Zi`. Forward include paths and macro flags.
5. **Phase 5:** Shader reflection — extract constant buffers, parameters, and semantics.
6. **Phase 6:** SPIRV-Cross transpilation — only when `Platform == PlatformTarget.OpenGL` or `PlatformTarget.Metal`. Skip for `PlatformTarget.DirectX` (DXC's DXIL output is used directly — note: this is SM6 DXIL, not SM5 DXBC; MonoGame's D3D11 backend cannot load DXIL; full D3D11 support requires Phase 4.1). Skip for `PlatformTarget.Vulkan` (raw SPIR-V from Phase 4 is used directly).
7. **Phase 7:** `.mgfx` binary writer — serialise to bytes using `MgfxVersion` from `CliArguments`.
8. Write bytes to `OutputFile` (`File.WriteAllBytesAsync`). On `IOException`, return a `ShaderError`
   with code `X0002`.

If any stage returns a `Result.Fail`, stop the pipeline immediately and propagate the errors. Do
not execute subsequent stages. This is a short-circuit sequential bind:

```csharp
var result = await ParseAsync(...)
    .BindAsync(cleaned => preprocessor.FlattenAsync(...))
    .BindAsync(preprocessed => dxcCompiler.CompileAllPassesAsync(...))
    // ... etc.
```

Errors returned from DXC (via `Vortice.Dxc` error blobs) must be parsed into `ShaderError` records
before they leave `PipelineRunner`. The formatter in `ShadowDusk.Cli` must never see raw DXC output
strings — only structured `ShaderError` values.

---

## Program.cs — Top-Level Entry Point

```csharp
// src/ShadowDusk.Cli/Program.cs
#nullable enable

using ShadowDusk.Cli;
using ShadowDusk.Core;

// 1. Parse arguments.
var parseResult = ArgumentParser.Parse(args);
if (parseResult.IsFailure)
{
    Console.Error.WriteLine(ArgumentParser.UsageText);
    Console.Error.WriteLine(MgcbErrorFormatter.Format(parseResult.Error));
    return 1;
}

var cliArgs = parseResult.Value;

// 2. Run pipeline.
// (Unsupported platforms like PS4/XboxOne/Switch are already handled by ArgumentParser.Parse
//  returning Result.Fail — there is no PlatformTarget.Unsupported enum value.)
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

var runner = PipelineRunnerFactory.Create(cliArgs);

var compileResult = await runner.RunAsync(cliArgs, cts.Token);
if (compileResult.IsFailure)
{
    foreach (var line in MgcbErrorFormatter.FormatAll(compileResult.Error))
        Console.Error.WriteLine(line);
    return 1;
}

// 3. Write output file.
// (WriteAllBytesAsync errors are already wrapped as ShaderError inside RunAsync.)
return 0;
```

`PipelineRunnerFactory.Create(CliArguments)` is a static factory in `ShadowDusk.Cli` that
constructs the production `PipelineRunner` with real `FileSystemIncludeResolver`, `DxcCompiler`,
etc. This keeps `Program.cs` free of `new` calls and makes the factory the single composition root.

```csharp
// src/ShadowDusk.Cli/PipelineRunnerFactory.cs
#nullable enable
namespace ShadowDusk.Cli;

internal static class PipelineRunnerFactory
{
    public static PipelineRunner Create(CliArguments args)
    {
        var includeResolver = new FileSystemIncludeResolver(args.IncludePaths);
        var fxParser        = new FxFileParser();
        var preprocessor    = new Preprocessor();
        var dxcCompiler     = new DxcShaderCompiler();
        var reflector       = new ShaderReflector();          // Phase 5
        var transpiler      = new SpirvCrossGlslTranspiler(); // Phase 6; no-op for DirectX
        var mgfxWriter      = new MgfxWriter();               // Phase 7
        return new PipelineRunner(includeResolver, fxParser, preprocessor,
                                  dxcCompiler, reflector, transpiler, mgfxWriter);
    }
}
```
> Adjust type names to match actual class names in each phase's implementation.

---

## dotnet Tool Packaging

### `ShadowDusk.Cli.csproj`

```xml
<!-- src/ShadowDusk.Cli/ShadowDusk.Cli.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.Cli</AssemblyName>
    <RootNamespace>ShadowDusk.Cli</RootNamespace>
    <OutputType>Exe</OutputType>

    <!-- dotnet tool packaging -->
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>mgfxc</ToolCommandName>
    <PackageId>ShadowDusk.Cli</PackageId>
    <PackageVersion>0.1.0</PackageVersion>
    <Description>Cross-platform drop-in replacement for MonoGame's mgfxc shader compiler.</Description>

    <!-- Self-contained publish support -->
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>false</SelfContained>  <!-- default; override at publish time -->
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShadowDusk.Core\ShadowDusk.Core.csproj" />
    <ProjectReference Include="..\ShadowDusk.HLSL\ShadowDusk.HLSL.csproj" />
    <ProjectReference Include="..\ShadowDusk.GLSL\ShadowDusk.GLSL.csproj" />
    <ProjectReference Include="..\ShadowDusk.Metal\ShadowDusk.Metal.csproj" />
  </ItemGroup>
</Project>
```

### Tool Installation

```bash
# Install globally from local pack
dotnet pack src/ShadowDusk.Cli -o ./nupkg
dotnet tool install -g ShadowDusk.Cli --add-source ./nupkg

# After installation, mgfxc is on PATH:
mgfxc Shader.fx Output.mgfx /Profile:OpenGL
```

```bash
# Install from NuGet feed (future — once published)
dotnet tool install -g ShadowDusk.Cli
```

### Self-Contained Publish

Produce a single-file executable per RID. All managed code and native DLLs/SOs are bundled:

```bash
# Windows x64
dotnet publish src/ShadowDusk.Cli -r win-x64 --self-contained -c Release \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o publish/win-x64

# Linux x64
dotnet publish src/ShadowDusk.Cli -r linux-x64 --self-contained -c Release \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o publish/linux-x64

# macOS x64 (Intel)
dotnet publish src/ShadowDusk.Cli -r osx-x64 --self-contained -c Release \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o publish/osx-x64

# macOS arm64 (Apple Silicon)
dotnet publish src/ShadowDusk.Cli -r osx-arm64 --self-contained -c Release \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o publish/osx-arm64
```

Supported RID matrix:

| RID | Platform | Architecture |
|---|---|---|
| `win-x64` | Windows 10 / 11 | x64 |
| `linux-x64` | Ubuntu 20.04 LTS+ | x64 |
| `osx-x64` | macOS 12+ (Monterey) | Intel |
| `osx-arm64` | macOS 12+ (Monterey) | Apple Silicon |

Native dependencies (`libspirvd.so`, `libdxcompiler.so`, etc.) bundled by `Vortice.Dxc` and the
Phase 6 SPIRV-Cross P/Invoke assembly are automatically included by the single-file publish via
`<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>`.

---

## Cross-Platform Flag Prefix Handling

MGCB on Windows passes flags with `/` prefix (e.g. `/Profile:OpenGL`). When running under Linux or
macOS, shell expansion can misinterpret `/` as a root-relative path. ShadowDusk accepts both
prefixes everywhere:

| Canonical form | Accepted alternatives |
|---|---|
| `/Profile:<value>` | `--Profile:<value>` |
| `/Debug` | `--Debug` |
| `/I <path>` | `--I <path>`, `/I:<path>`, `--I:<path>` |
| `--mgfx-version <value>` | `/mgfx-version:<value>` |

Matching is case-insensitive throughout so that MGCB's Windows-style `/debug` (lowercase) also
works.

---

## Unsupported Platform Handling

PS4, XboxOne, and Switch profiles are defined in mgfxc's grammar but out of scope for ShadowDusk.
When one of these is passed, the process must:

1. Write a single line to stderr using the no-location error format:

   ```
   error X0010: platform 'PlayStation4' is not supported by ShadowDusk
   ```

2. Exit with code `1`.

This satisfies MGCB's expectation of a non-zero exit on failure while surfacing a readable message
in the IDE error list.

---

## Error Code Registry

Reserve these codes for CLI-layer errors (`X00xx`). Phase-specific codes are defined in their
respective phases.

| Code | Meaning |
|---|---|
| `X0001` | Source file read failure |
| `X0002` | Output file write failure |
| `X0003` | Missing required argument (`SourceFile` or `OutputFile`) |
| `X0004` | Unknown profile string |
| `X0005` | Invalid `--mgfx-version` value |
| `X0010` | Unsupported platform (PS4, XboxOne, Switch) |
| `X0099` | Unexpected internal exception |

---

## Testing Requirements

### Unit Tests (`ShadowDusk.Core.Tests`)

All tests are pure — no disk I/O, no process spawning.

#### ArgumentParser Tests

| Test | Input | Expected |
|---|---|---|
| `Parse_ValidPositionalArgs` | `["Shader.fx", "Out.mgfx"]` | `Ok` with `SourceFile=Shader.fx`, `OutputFile=Out.mgfx`, defaults |
| `Parse_ProfileOpenGL_SlashPrefix` | `["S.fx", "O.mgfx", "/Profile:OpenGL"]` | `Platform == OpenGL` |
| `Parse_ProfileOpenGL_DashPrefix` | `["S.fx", "O.mgfx", "--Profile:OpenGL"]` | `Platform == OpenGL` |
| `Parse_ProfileDirectX_Default` | `["S.fx", "O.mgfx"]` | `Platform == DirectX` |
| `Parse_ProfileCaseInsensitive` | `[..., "/Profile:opengl"]` | `Platform == OpenGL` |
| `Parse_DebugFlag_SlashPrefix` | `[..., "/Debug"]` | `Debug == true` |
| `Parse_DebugFlag_DashPrefix` | `[..., "--Debug"]` | `Debug == true` |
| `Parse_IncludePath_SingleSlash` | `[..., "/I", "include/"]` | `IncludePaths = ["include/"]` |
| `Parse_IncludePath_ColonForm` | `[..., "/I:include/"]` | `IncludePaths = ["include/"]` |
| `Parse_IncludePath_Repeatable` | `[..., "/I", "a", "/I", "b"]` | `IncludePaths = ["a", "b"]` |
| `Parse_MgfxVersion10` | `[..., "--mgfx-version", "10"]` | `MgfxVersion == 10` |
| `Parse_MgfxVersion11` | `[..., "--mgfx-version", "11"]` | `MgfxVersion == 11` |
| `Parse_MgfxVersionInvalid` | `[..., "--mgfx-version", "99"]` | `Fail` with code `X0005` |
| `Parse_MissingSourceFile` | `["Out.mgfx"]` | `Fail` with code `X0003` |
| `Parse_MissingBothFiles` | `[]` | `Fail` with code `X0003` |
| `Parse_UnknownFlagIgnored` | `[..., "--future-flag", "value"]` | `Ok` (ignore unknown flag) |
| `Parse_UnsupportedPlatform_PS4` | `[..., "/Profile:PlayStation4"]` | `Fail` with code `X0010` |
| `Parse_UnsupportedPlatform_XboxOne` | `[..., "/Profile:XboxOne"]` | `Fail` with code `X0010` |
| `Parse_UnknownProfile` | `[..., "/Profile:DOS"]` | `Fail` with code `X0004` |

#### MgcbErrorFormatter Tests

| Test | Input | Expected output string |
|---|---|---|
| `Format_FullLocation` | `ShaderError("Foo.fx", line=11, col=44, "X4502", "bad semantic")` | `Foo.fx(11,44-44): error X4502: bad semantic` |
| `Format_NoLocation` | `ShaderError("", line=0, ...)` | `error X0003: message` |
| `Format_WarningLevel` | `Severity=Warning` | `Foo.fx(3,1-1): warning X1234: message` |
| `Format_PathStrippedToFilename` | `File="/abs/path/to/Foo.fx"` | filename segment is `Foo.fx` |
| `Format_CodeZeroPadded` | `Code="501"` | error code segment is `X0501` |
| `Format_CodeAlreadyFormatted` | `Code="X4502"` | error code segment is `X4502` unchanged |
| `FormatAll_EmptyList` | `[]` | empty enumerable |
| `FormatAll_MultipleErrors` | 3 errors | 3 formatted strings in input order |

### Integration Test (`ShadowDusk.Integration.Tests`)

Tag all integration tests with `[Trait("Category","Integration")]`.

#### `CliIntegrationTest` class

**Setup:** before any test in this class runs, publish the `ShadowDusk.Cli` project to a temp
directory and record the path to the produced executable. This can be done via a shared
`IClassFixture<CliBinaryFixture>`.

```csharp
// tests/ShadowDusk.Integration.Tests/CliBinaryFixture.cs
public sealed class CliBinaryFixture : IDisposable
{
    public string ExecutablePath { get; }  // absolute path to published mgfxc binary

    public CliBinaryFixture()
    {
        // dotnet publish src/ShadowDusk.Cli -o <tempdir> --no-self-contained
        // Record the exe path.
    }

    public void Dispose() { /* clean up temp dir */ }
}
```

**Test cases:**

1. - [ ] `Compile_MinimalFx_OpenGL_ExitCode0`
   - Fixture: write a minimal valid `.fx` file (see Section below) to a temp path.
   - Invoke: `mgfxc <source> <output> /Profile:OpenGL`
   - Assert: exit code is `0`.
   - Assert: output file exists and has non-zero length.
   - Assert: stdout is empty.
   - Assert: stderr is empty.

2. - [ ] `Compile_InvalidFx_ExitCode1_StderrContainsError`
   - Fixture: write a `.fx` file with a deliberate HLSL syntax error.
   - Invoke: `mgfxc <source> <output> /Profile:OpenGL`
   - Assert: exit code is `1`.
   - Assert: stderr is not empty.
   - Assert: stderr does NOT contain any output file path (error format validation).
   - Assert: stdout is empty.

3. - [ ] `Compile_MissingSourceFile_ExitCode1_UsageOnStderr`
   - Invoke: `mgfxc /Profile:OpenGL` (no positional args).
   - Assert: exit code is `1`.
   - Assert: stderr contains the usage text.
   - Assert: stdout is empty.

4. - [ ] `Compile_UnsupportedPlatform_PS4_ExitCode1`
   - Invoke: `mgfxc Shader.fx Out.mgfx /Profile:PlayStation4`
   - Assert: exit code is `1`.
   - Assert: stderr contains `X0010`.

5. - [ ] `Compile_DebugFlag_ExitCode0`
   - Fixture: minimal valid `.fx`.
   - Invoke: `mgfxc <source> <output> /Profile:OpenGL /Debug`
   - Assert: exit code is `0` (debug flag must not cause failure).

6. - [ ] `Compile_IncludePathFlag_ResolvesHeader`
   - Fixture: `.fx` that includes a header via `#include "Common.fxh"`; header lives in
     a separate subdirectory.
   - Invoke: `mgfxc <source> <output> /Profile:OpenGL /I <header-dir>`
   - Assert: exit code is `0`.

### Minimal Test Fixture Shader

The following minimal `.fx` is the canonical fixture for integration tests that need a valid shader.
It lives at `tests/fixtures/shaders/Minimal.fx`:

```hlsl
// Minimal.fx — used by Phase 8 + Phase 9 integration tests.
// Compiles for DirectX_11 and OpenGL without any textures or constant buffers.

struct VertexInput
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
};

PixelInput VS(VertexInput input)
{
    PixelInput output;
    output.Position = input.Position;
    output.Color    = input.Color;
    return output;
}

float4 PS(PixelInput input) : SV_TARGET
{
    return input.Color;
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

---

## Task Checklist

### 1. Project file updates

- [ ] 1.1 Add `<PackAsTool>true</PackAsTool>` and `<ToolCommandName>mgfxc</ToolCommandName>` to
         `ShadowDusk.Cli.csproj`.
- [ ] 1.2 Add `<PackageId>`, `<PackageVersion>`, `<Description>` properties.
- [ ] 1.3 Add `<PublishSingleFile>` and `<IncludeNativeLibrariesForSelfExtract>` properties.
- [ ] 1.4 Verify all project references are present: Core ✓ (Phase 1), HLSL ✓ (Phase 4), GLSL (add if missing — needed for SPIRV-Cross transpiler), Metal (add if missing — needed for MSL path).

### 2. Data types

- [ ] 2.1 Define `CliArguments` sealed record in `CliArguments.cs`.
- [ ] 2.2 For unsupported platforms (PS4, XboxOne, Switch), `ArgumentParser.Parse` returns `Result.Fail(new ShaderError(..., Code: "X0010", ...))` directly — **do NOT add `PlatformTarget.Unsupported` to the enum**. The enum should remain: DirectX, OpenGL, Metal, Vulkan. Unknown profile strings not in the supported/unsupported tables return `X0004`.

### 3. ArgumentParser

- [ ] 3.1 Implement `ArgumentParser.Parse(string[] args)` with all rules in the Argument Parser
         section.
- [ ] 3.2 Implement `UsageText` static property.
- [ ] 3.3 Verify case-insensitive flag matching.
- [ ] 3.4 Verify unknown flags are silently ignored.
- [ ] 3.5 Verify `/I` colon form and space-separated form both work.
- [ ] 3.6 Write all unit tests from the ArgumentParser Tests table.

### 4. MgcbErrorFormatter

- [ ] 4.1 Implement `MgcbErrorFormatter.Format(ShaderError)`.
- [ ] 4.2 Implement `MgcbErrorFormatter.FormatAll(IEnumerable<ShaderError>)`.
- [ ] 4.3 Strip directory component from `error.File` (use `Path.GetFileName`).
- [ ] 4.4 Implement code zero-padding: if `error.Code` is a pure integer string, prepend `X` and
         zero-pad to 4 digits; if it already matches `X\d{4}`, pass through unchanged.
- [ ] 4.5 Write all unit tests from the MgcbErrorFormatter Tests table.

### 5. PipelineRunner and factory

- [ ] 5.0 Define interfaces `IFxFileParser`, `IPreprocessor`, `IDxcShaderCompiler` (or reuse concrete types if interfaces are deemed unnecessary). At minimum, `IMgfxWriter` is needed since it's the Phase 7 output boundary.
- [ ] 5.1 Define `PipelineRunner` class with constructor dependencies.
- [ ] 5.2 Implement `RunAsync` following the Stage Order in the Pipeline Wiring section.
- [ ] 5.3 Implement short-circuit: first failing stage returns immediately without executing later
         stages.
- [ ] 5.4 Wrap `File.ReadAllTextAsync` and `File.WriteAllBytesAsync` exceptions as `ShaderError`
         with codes `X0001` and `X0002` respectively.
- [ ] 5.5 Implement `PipelineRunnerFactory.Create(CliArguments)` as the composition root.

### 6. Program.cs

- [ ] 6.1 Replace the Phase 1 stub with the top-level statements from the Program.cs section.
- [ ] 6.2 Wire argument parsing errors to stderr + exit 1.
- [ ] 6.3 Wire pipeline errors to `MgcbErrorFormatter.FormatAll` + stderr + exit 1.
- [ ] 6.4 Wrap the entire `RunAsync` call in a `try/catch(Exception)` that emits `X0099` to stderr
         and exits 1. This is the last-resort exception boundary; no stack traces in Release.
- [ ] 6.5 Confirm nothing writes to stdout under any code path.

### 7. Minimal fixture shader

- [ ] 7.1 Create `tests/fixtures/shaders/Minimal.fx` with the content from the Testing Requirements
         section.
- [ ] 7.2 Verify the file compiles with `dxc -T vs_4_0 -E VS Minimal.fx` locally (optional smoke
         check before integration tests run).

### 8. Integration tests

- [ ] 8.1 Create `CliBinaryFixture` class that publishes `ShadowDusk.Cli` to a temp directory.
- [ ] 8.2 Use `System.Diagnostics.Process` to invoke the binary, capturing `stdout` and `stderr`
         separately, with a 30-second `CancellationToken` timeout.
- [ ] 8.3 Implement all six integration test cases from the Testing Requirements section.
- [ ] 8.4 Tag all integration tests with `[Trait("Category","Integration")]`.
- [ ] 8.5 Ensure the `CliBinaryFixture` cleans up temp directories in `Dispose`.

### 9. Verification

- [ ] 9.1 Run `dotnet build` — 0 errors, 0 warnings.
- [ ] 9.2 Run `dotnet test --filter "Category!=Integration"` — all unit tests pass.
- [ ] 9.3 Run `dotnet test --filter "Category=Integration"` — all integration tests pass.
- [x] 9.4 Run `dotnet pack src/ShadowDusk.Cli` — NuGet package is produced with `ToolCommandName`
         set to `mgfxc`. *(Closed by Phase 27, 2026-06-12: `ShadowDusk.Cli.0.4.0.nupkg`
         produced; the command name is `ShadowDuskCLI` — the later CLI re-brand superseded
         this step's `mgfxc` wording. Scripted in `tools/verify-cli-packaging.ps1`.)*
- [x] 9.5 Install locally and verify: `dotnet tool install -g ShadowDusk.Cli --add-source ./nupkg`
         then `mgfxc --help` (no-args) shows usage on stderr with exit 1. *(Closed by
         Phase 27, 2026-06-12 — installed with `--tool-path <scratch>` instead of `-g`
         (no machine pollution); `ShadowDuskCLI` no-args → usage on stderr, exit 1, and a
         fixture compile through the installed tool is byte-identical to the built CLI.)*
- [x] 9.6 Run `dotnet publish src/ShadowDusk.Cli -r win-x64 --self-contained` (or the current
         platform's RID) and confirm the single-file binary executes. *(Closed by Phase 27,
         2026-06-12 — 45.4 MB single-file `ShadowDuskCLI.exe` with release.yml's flag set;
         runs, compiles `Minimal.fx` byte-identically. Linux/macOS RIDs → Phase 30.)*

---

## Acceptance Criteria

| Criterion | How to verify |
|---|---|
| `mgfxc Shader.fx Out.mgfx /Profile:OpenGL` succeeds end-to-end | Integration test `Compile_MinimalFx_OpenGL_ExitCode0` passes |
| Successful compile is completely silent | Assert stdout and stderr are both empty on exit 0 |
| Shader compile errors go to stderr, not stdout | Integration test `Compile_InvalidFx_ExitCode1_StderrContainsError` |
| Exit code 0 on success, 1 on any failure | All integration tests check exit code explicitly |
| Error format matches MGCB pattern `Filename.fx(line,col-col): error X####: message` | `MgcbErrorFormatter` unit tests |
| `dotnet tool install -g ShadowDusk.Cli` makes `mgfxc` available on PATH | Manual verification (Step 9.5) |
| Unsupported platforms produce exit 1 with `X0010` in stderr | Integration test `Compile_UnsupportedPlatform_PS4_ExitCode1` |
| `/` and `--` flag prefixes both accepted | `Parse_ProfileOpenGL_SlashPrefix` and `_DashPrefix` unit tests |
| Self-contained publish works for all four RIDs | Step 9.6; CI validates in Phase 10 |
| `dotnet build` produces 0 warnings | `dotnet build -warnaserror` exits 0 |

---

## Out of Scope for This Phase

- MGCB plugin (`ShadowDusk.MgcbPlugin`) — content processor integration is a separate future
  undertaking; see `plan.md`.
- Metal / MSL compilation — `PlatformTarget.Metal` is defined but `PipelineRunner` must return an
  `X0010`-class error if it is selected until Phase 6 Metal support is complete.
- `--help` flag producing a help page to stdout — mgfxc itself does not implement `--help`; any
  invocation without required positional args emits usage to stderr with exit 1. Matching that
  behaviour exactly is the requirement.
- Shell completion scripts.
- NuGet publishing to a public feed — out of scope until Phase 10 CI is established.

---

## Known Gaps (deferred to later phases)

| Gap | Phase |
|---|---|
| Real `.fx` fixture library beyond `Minimal.fx` | 9 |
| Per-platform integration test filter (`Platform=OpenGL`) | 9 |
| GitHub Actions publish job for RID matrix | 10 |
| NuGet feed publication | 10 |
| Metal/MSL pipeline stage in `PipelineRunner` | post-Phase 6 |
| Full MGCB content processor plugin | post-Phase 8 |
