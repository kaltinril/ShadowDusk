# Phase 6 — SPIRV-Cross GLSL Transpilation

**Goal:** Take the SPIR-V bytecode produced by DXC (Phase 4) and transpile it to GLSL source text using the SPIRV-Cross C API via raw P/Invoke. The output `GlslSource` string is stored verbatim in the `.mgfx` OpenGL shader blob; MonoGame's OpenGL runtime passes it directly to `glCompileShader` at load time.

**Prerequisite phases:** Phase 1 (scaffold), Phase 4 (DXC → SPIR-V output available)

---

## 1. Prerequisites

| Requirement | Where it comes from | Check |
|---|---|---|
| SPIR-V `uint[]` word array per shader stage | Phase 4 `DxcCompiler` output | `PassBlob.VertexSpirV`, `PassBlob.PixelSpirV` |
| Native SPIRV-Cross C shared library | `tools/restore.sh` / `restore.ps1` (Phase 1) | `tools/spirv-cross/` |
| `ShadowDusk.GLSL` project exists | Phase 1 scaffold | `src/ShadowDusk.GLSL/` |
| `Result<T, ShaderError>` type | Phase 1 / Core | `ShadowDusk.Core.Result` |

---

## 2. Native Library Filenames and Placement

The SPIRV-Cross C shared library is a single `.dll` / `.so` / `.dylib`. The file is placed by `tools/restore.sh` at build time and copied into the output directory by an MSBuild target.

| Platform RID | File name | Copy target |
|---|---|---|
| `win-x64` | `spirv-cross-c-shared.dll` | `runtimes/win-x64/native/` |
| `linux-x64` | `libspirv-cross-c-shared.so` | `runtimes/linux-x64/native/` |
| `osx-x64` | `libspirv-cross-c-shared.dylib` | `runtimes/osx-x64/native/` |
| `osx-arm64` | `libspirv-cross-c-shared.dylib` | `runtimes/osx-arm64/native/` |

Add to `ShadowDusk.GLSL.csproj`:

```xml
<ItemGroup>
  <None Include="$(MSBuildThisFileDirectory)../../tools/spirv-cross/spirv-cross-c-shared.dll"
        Condition="Exists('...')"
        CopyToOutputDirectory="PreserveNewest"
        Link="runtimes/win-x64/native/spirv-cross-c-shared.dll" />
  <None Include="$(MSBuildThisFileDirectory)../../tools/spirv-cross/libspirv-cross-c-shared.so"
        Condition="Exists('...')"
        CopyToOutputDirectory="PreserveNewest"
        Link="runtimes/linux-x64/native/libspirv-cross-c-shared.so" />
  <None Include="$(MSBuildThisFileDirectory)../../tools/spirv-cross/libspirv-cross-c-shared.dylib"
        Condition="Exists('...')"
        CopyToOutputDirectory="PreserveNewest"
        Link="runtimes/osx-x64/native/libspirv-cross-c-shared.dylib" />
  <None Include="$(MSBuildThisFileDirectory)../../tools/spirv-cross/libspirv-cross-c-shared.dylib"
        Condition="Exists('...')"
        CopyToOutputDirectory="PreserveNewest"
        Link="runtimes/osx-arm64/native/libspirv-cross-c-shared.dylib" />
</ItemGroup>
```

Use `NativeLibrary.Load()` with a platform-conditional path resolver (see Section 4) rather than a hard-coded `[DllImport]` library name.

---

## 3. SPIRV-Cross C API — Types and Constants

All types and constants come from the public SPIRV-Cross C header (`spirv_cross_c.h`). The stable C API is C89-compatible and safe for direct P/Invoke.

### 3.1 Opaque handle types

```csharp
// All handles are opaque pointers — represent as IntPtr
// spvc_context    -> IntPtr
// spvc_parsed_ir  -> IntPtr
// spvc_compiler   -> IntPtr
// spvc_compiler_options -> IntPtr
```

### 3.2 Backend enum

```csharp
internal enum SpvcBackend : uint
{
    None   = 0,
    Glsl   = 1,
    Hlsl   = 2,
    Msl    = 3,
    Cpp    = 4,
    Json   = 5,
}
```

### 3.3 Capture mode enum

```csharp
internal enum SpvcCaptureMode : uint
{
    Copy             = 0,
    TakeOwnership    = 1,
}
```

### 3.4 Result enum

```csharp
internal enum SpvcResult : int
{
    Success             =  0,
    ErrorInvalidSpirv   = -1,
    ErrorUnsupportedSpirv = -2,
    ErrorOutOfMemory    = -3,
    ErrorInvalidArgument = -4,
}
```

### 3.5 Compiler option constants

All compiler option integers come from the header. Map them as `const uint` — do **not** use an enum here because option IDs are combined with a backend selector in the upper bits.

```csharp
internal static class SpvcCompilerOption
{
    // Common options (backend = 0 in upper bits)
    public const uint FlipVertexY            = 0x00000001u; // SPVC_COMPILER_OPTION_FLIP_VERTEX_Y
    public const uint GlslDepthZeroToOne     = 0x00000002u; // SPVC_COMPILER_OPTION_GLSL_DEPTH_ZERO_TO_ONE (check header — may be GLSL-specific offset)

    // GLSL-specific options (backend selector bits applied by the library)
    public const uint GlslVersion            = 0x10000001u; // SPVC_COMPILER_OPTION_GLSL_VERSION
    public const uint GlslEs                 = 0x10000002u; // SPVC_COMPILER_OPTION_GLSL_ES
    public const uint GlslVulkanSemantics    = 0x10000003u; // SPVC_COMPILER_OPTION_GLSL_VULKAN_SEMANTICS
}
```

> **Note:** The exact integer values for these constants MUST be read from the version of `spirv_cross_c.h` that ships with the pinned SPIRV-Cross release. The values above are illustrative. Add a task-time step (Section 5, Task 5) to extract real values from the header before implementing P/Invoke.

---

## 4. P/Invoke Bindings — `SpvcNative.cs`

Create `src/ShadowDusk.GLSL/Interop/SpvcNative.cs`.

### 4.1 Library loading

```csharp
#nullable enable
using System.Runtime.InteropServices;

internal static partial class SpvcNative
{
    private const string LibName = "spirv-cross-c-shared";

    // Resolved once at startup via NativeLibrary.Load() with RID-aware path.
    // All [DllImport] entries below use the same LibName — the custom resolver
    // registered in SpvcLoader.Register() makes the runtime find the correct file.
}
```

Create `src/ShadowDusk.GLSL/Interop/SpvcLoader.cs` to register the resolver:

```csharp
internal static class SpvcLoader
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;
        NativeLibrary.SetDllImportResolver(
            typeof(SpvcLoader).Assembly,
            (name, assembly, searchPath) =>
            {
                if (name != "spirv-cross-c-shared") return IntPtr.Zero;
                var rid = GetCurrentRid();
                var candidate = Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes", rid, "native",
                    GetLibFileName());
                if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
                // In single-file published executables, extracted native libraries go to a temp
                // directory rather than AppContext.BaseDirectory. Fall back to a bare TryLoad so
                // the OS loader can find the library if it was extracted to a directory already on
                // the library search path (e.g. the temp extraction directory added by the host).
                return NativeLibrary.TryLoad(libName, out handle) ? handle : IntPtr.Zero;
            });
    }

    private static string GetCurrentRid() =>
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
         RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
         RuntimeInformation.OSArchitecture) switch
        {
            (true,  _,    _)                  => "win-x64",
            (false, true, Architecture.Arm64) => "osx-arm64",
            (false, true, _)                  => "osx-x64",
            _                                 => "linux-x64",
        };

    private static string GetLibFileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "spirv-cross-c-shared.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "libspirv-cross-c-shared.dylib"
        :                                                      "libspirv-cross-c-shared.so";
}
```

### 4.2 Function signatures

Add to `SpvcNative.cs`:

```csharp
[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_context_create(out IntPtr context);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern void spvc_context_destroy(IntPtr context);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern IntPtr spvc_context_get_last_error_string(IntPtr context);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_context_parse_spirv(
    IntPtr context,
    [In] uint[] spirvWords,
    nuint wordCount,
    out IntPtr parsedIr);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_context_create_compiler(
    IntPtr context,
    SpvcBackend backend,
    IntPtr parsedIr,
    SpvcCaptureMode captureMode,
    out IntPtr compiler);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_compiler_create_compiler_options(
    IntPtr compiler,
    out IntPtr options);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_compiler_options_set_bool(
    IntPtr options,
    uint option,
    [MarshalAs(UnmanagedType.U1)] bool value);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_compiler_options_set_uint(
    IntPtr options,
    uint option,
    uint value);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_compiler_install_compiler_options(
    IntPtr compiler,
    IntPtr options);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_compiler_build_combined_image_samplers(
    IntPtr compiler);

[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
internal static extern SpvcResult spvc_compiler_compile(
    IntPtr compiler,
    out IntPtr glslSource);  // pointer to null-terminated UTF-8 string owned by context
```

---

## 5. Implementation Tasks

### Task 1 — Read the pinned SPIRV-Cross header

1. Identify the SPIRV-Cross release version pinned in `tools/restore.sh` (or `restore.ps1`).
2. Download the corresponding `spirv_cross_c.h` from the SPIRV-Cross GitHub release or source tree.
3. Extract the exact integer values for all option constants listed in Section 3.5.
4. Update `SpvcCompilerOption` in `SpvcNative.cs` with the real values.
5. Document the pinned version and header hash in a comment at the top of `SpvcNative.cs`.

### Task 2 — Create `SpvcNative.cs` and `SpvcLoader.cs`

1. Create `src/ShadowDusk.GLSL/Interop/` directory.
2. Write `SpvcLoader.cs` as shown in Section 4.1.
3. Write `SpvcNative.cs` with all P/Invoke signatures from Section 4.2.
4. Add `SpvcLoader.Register()` call in the `SpirvCrossGlslTranspiler` constructor (see Task 4).

### Task 3 — Investigate MonoGame uniform naming convention

**This task is a prerequisite for correctness — do not skip it.**

1. Clone or browse the MonoGame repository (`develop` branch) on GitHub.
2. Locate `MonoGame.Framework/Platform/Graphics/Effect/Effect.OpenGL.cs` (or equivalent path).
3. Search for `glGetUniformLocation` calls.
4. Determine which of the following naming paths is used:

   | Convention | Indicator in source | Action required |
   |---|---|---|
   | HLSL variable names (`WorldViewProj`, `Texture0`) | `param.Name` used directly as uniform name | None — SPIRV-Cross preserves HLSL names by default |
   | Register-based names (`vs_c0`, `ps_c0`) | `"vs_c" + register` style string building | Post-process GLSL output to rename uniforms (Task 3b) |

5. If register-based names are found, implement a post-processing step in `SpirvCrossGlslTranspiler` that:
   a. Parses uniform declarations out of the GLSL source with a regex (`uniform\s+\w+\s+(\w+)\s*;`).
   b. Queries SPIRV-Cross for each uniform's original HLSL register index via `spvc_compiler_get_decoration` (`SpvDecorationBinding` / `SpvDecorationLocation`).
   c. Replaces each uniform name with the corresponding `vs_c{N}` / `ps_c{N}` register name.
6. Document the finding and the decision in `docs/glsl-uniform-naming.md`.

### Task 4 — Implement `SpirvCrossGlslTranspiler`

Create `src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs`.

**Method signature:**

```csharp
#nullable enable
namespace ShadowDusk.GLSL;

public sealed class SpirvCrossGlslTranspiler
{
    /// <summary>
    /// Transpiles a SPIR-V word array to GLSL #version 140 source.
    /// </summary>
    /// <param name="spirvWords">SPIR-V bytecode as a uint array (little-endian words).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>GLSL source text, or a <see cref="ShaderError"/> on failure.</returns>
    public Result<GlslSource, ShaderError> Transpile(
        ReadOnlySpan<uint> spirvWords,
        CancellationToken cancellationToken = default);
}
```

**Call sequence inside `Transpile`:**

```
1. SpvcLoader.Register()
2. spvc_context_create(&ctx)                                         → check SpvcResult.Success
3. spvc_context_parse_spirv(ctx, words, wordCount, &ir)              → check result; on error call GetLastError() and return ShaderError
4. spvc_context_create_compiler(ctx, GLSL, ir, TakeOwnership, &cmp)  → check result
5. spvc_compiler_create_compiler_options(cmp, &opts)                 → check result
6. Set all 5 options (see Section 5 option table below)
7. spvc_compiler_install_compiler_options(cmp, opts)                 → check result
8. spvc_compiler_build_combined_image_samplers(cmp)                  → check result; MANDATORY
9. spvc_compiler_compile(cmp, &glslPtr)                              → check result
10. Marshal.PtrToStringUTF8(glslPtr)  → GlslSource string
11. spvc_context_destroy(ctx)   ← always in finally block
12. Return Result.Ok(new GlslSource(glsl))
```

**Option table for step 6:**

| Call | Option constant | Value |
|---|---|---|
| `spvc_compiler_options_set_bool` | `SpvcCompilerOption.FlipVertexY` | `true` |
| `spvc_compiler_options_set_bool` | `SpvcCompilerOption.GlslDepthZeroToOne` | `true` |
| `spvc_compiler_options_set_uint` | `SpvcCompilerOption.GlslVersion` | `140` |
| `spvc_compiler_options_set_bool` | `SpvcCompilerOption.GlslEs` | `false` |
| `spvc_compiler_options_set_bool` | `SpvcCompilerOption.GlslVulkanSemantics` | `false` |

**Error helper:**

```csharp
private static ShaderError GetLastError(IntPtr ctx, string stage)
{
    var ptr = SpvcNative.spvc_context_get_last_error_string(ctx);
    var msg = Marshal.PtrToStringUTF8(ptr) ?? "(no error string)";
    return new ShaderError($"SPIRV-Cross [{stage}]: {msg}");
}
```

**Notes:**
- `spvc_context_destroy` frees all memory including the `glslPtr` string — copy it to a managed string **before** calling destroy.
- Use `try/finally` to ensure `spvc_context_destroy` is always called even on partial failure.
- `ReadOnlySpan<uint>` cannot be pinned directly across an async boundary; `Transpile` must be synchronous (no `async`). Async wrapping is the caller's responsibility.
- The `uint[]` overload of `spvc_context_parse_spirv` requires a mutable array; copy the span to a temporary `uint[]` before the P/Invoke call.

### Task 5 — Add `GlslSource` value type

Create `src/ShadowDusk.Core/GlslSource.cs`:

```csharp
#nullable enable
namespace ShadowDusk.Core;

/// <summary>Desktop GLSL source text to be stored in the .mgfx OpenGL shader blob.</summary>
public readonly record struct GlslSource(string Text)
{
    public override string ToString() => Text;
}
```

### Task 6 — Wire transpiler into `ShaderCompiler` orchestrator

In `ShadowDusk.Core`'s top-level `ShaderCompiler` (or wherever the per-pass compilation is orchestrated):

1. After DXC emits SPIR-V for vertex and pixel stages (Phase 4 output), call `SpirvCrossGlslTranspiler.Transpile()` for each stage.
2. Store the resulting `GlslSource` on `PassBlob.VertexGlsl` and `PassBlob.PixelGlsl`.
3. Propagate `ShaderError` up — do not swallow it.

### Task 7 — MSBuild copy targets for native binaries

In `src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj`, add `<None>` items with `CopyToOutputDirectory="PreserveNewest"` for each RID × file combination (see Section 2). Conditionalize each item on `Exists(...)` so the project builds on CI before binaries are restored.

---

## 6. New Source Files

| File | Project | Purpose |
|---|---|---|
| `src/ShadowDusk.GLSL/Interop/SpvcNative.cs` | `ShadowDusk.GLSL` | P/Invoke signatures |
| `src/ShadowDusk.GLSL/Interop/SpvcLoader.cs` | `ShadowDusk.GLSL` | NativeLibrary resolver |
| `src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs` | `ShadowDusk.GLSL` | Public transpiler class |
| `src/ShadowDusk.Core/GlslSource.cs` | `ShadowDusk.Core` | Output value type |
| `docs/glsl-uniform-naming.md` | — | Research findings from Task 3 |

---

## 7. Tests

All tests live in `tests/ShadowDusk.Integration.Tests/` and are tagged `[Trait("Category","Integration")]`.

### 7.1 Fixture shaders

Add to `tests/fixtures/shaders/`:

| File | Contents |
|---|---|
| `minimal_vs_ps.fx` | Trivial vertex + pixel shader pair, no textures |
| `textured_vs_ps.fx` | VS passes UV; PS samples a `Texture2D` via a `SamplerState` |
| `passthrough_vs.fx` | Vertex shader that outputs `SV_Position` unchanged (for Y-flip test) |

### 7.2 Test cases

```
GlslTranspilerTests
├── Transpile_MinimalShader_OutputContainsVoidMain
├── Transpile_MinimalShader_OutputContainsVersion140
├── Transpile_TexturedShader_OutputContainsSampler2D
├── Transpile_TexturedShader_OutputDoesNotContainSeparateSampler
├── Transpile_PassthroughVertex_YFlipIsApplied
├── Transpile_InvalidSpirv_ReturnsShaderError
└── Transpile_EmptySpirv_ReturnsShaderError
```

**`Transpile_OutputContainsVoidMain`** — compile `minimal_vs_ps.fx` through DXC (Phase 4), pass resulting SPIR-V to `SpirvCrossGlslTranspiler.Transpile()`, assert `result.IsOk`, assert `result.Value.Text.Contains("void main(")`.

**`Transpile_OutputContainsVersion140`** — same setup, assert `result.Value.Text.StartsWith("#version 140")`.

**`Transpile_OutputContainsSampler2D`** — compile `textured_vs_ps.fx`, transpile pixel stage SPIR-V, assert `result.Value.Text.Contains("sampler2D")`.

**`Transpile_OutputDoesNotContainSeparateSampler`** — same as above, assert output does not contain the pattern `texture(` paired with a standalone `sampler` type (i.e., combined samplers were actually applied).

**`Transpile_PassthroughVertex_YFlipIsApplied`** — compile `passthrough_vs.fx`, transpile vertex stage, assert that `gl_Position.y` appears negated in the output (either literally `-gl_Position.y` or via a generated sign-flip expression). This confirms `FlipVertexY` option was applied.

**`Transpile_InvalidSpirv_ReturnsShaderError`** — pass `new uint[]{ 0xDEADBEEF, 0, 0, 0 }` as SPIR-V, assert `result.IsError`, assert error message is non-empty.

---

## 8. Numbered Task Checklist

### Setup

- [ ] 1. Identify the SPIRV-Cross release version pinned in `tools/restore.sh` / `restore.ps1`.
- [ ] 2. Download the corresponding `spirv_cross_c.h` header from the SPIRV-Cross GitHub release.
- [ ] 3. Extract exact integer values for all `SpvcCompilerOption` constants from the header.
- [ ] 4. Update `SpvcCompilerOption` in `SpvcNative.cs` with the real values; add version + hash comment.

### P/Invoke layer

- [ ] 5. Create `src/ShadowDusk.GLSL/Interop/` directory.
- [ ] 6. Write `SpvcLoader.cs` with `NativeLibrary.SetDllImportResolver` (RID-aware path logic, single-file fallback).
- [ ] 7. Write `SpvcNative.cs` with all 11 P/Invoke signatures from Section 4.2.

### MonoGame uniform naming research (required before Task 11)

- [ ] 8. Clone/browse MonoGame `develop` branch; locate `Effect.OpenGL.cs` (or equivalent).
- [ ] 9. Find `glGetUniformLocation` calls; determine whether HLSL variable names or register-based names (`vs_c0`) are used.
- [ ] 10. If register-based names are required, implement the uniform-rename post-processing step in `SpirvCrossGlslTranspiler` (regex parse + `spvc_compiler_set_name`).
- [ ] 11. Write findings to `docs/glsl-uniform-naming.md`.

### Core transpiler

- [ ] 12. Add `GlslSource` value type to `src/ShadowDusk.Core/GlslSource.cs`.
- [ ] 13. Implement `SpirvCrossGlslTranspiler.Transpile()` with the 12-step call sequence from Section 5 (Task 4).
- [ ] 14. Ensure `spvc_context_destroy` is called in a `try/finally` on every code path.
- [ ] 15. Ensure `spvc_compiler_build_combined_image_samplers()` is called before every `spvc_compiler_compile()`.
- [ ] 16. Set all 5 compiler options (`FlipVertexY`, `GlslDepthZeroToOne`, `GlslVersion=140`, `GlslEs=false`, `GlslVulkanSemantics=false`).

### Wiring

- [ ] 17. Add `SpvcLoader.Register()` call in the `SpirvCrossGlslTranspiler` constructor.
- [ ] 18. Wire `SpirvCrossGlslTranspiler` into the `ShaderCompiler` orchestrator in `ShadowDusk.Core`; store results as `PassBlob.VertexGlsl` and `PassBlob.PixelGlsl`.

### MSBuild integration

- [ ] 19. Add `<None>` / `<Content>` copy items for all 4 RID × file combinations to `ShadowDusk.GLSL.csproj` (Section 2).

### Integration tests

- [ ] 20. Add `tests/fixtures/shaders/minimal_vs_ps.fx`, `textured_vs_ps.fx`, and `passthrough_vs.fx`.
- [ ] 21. Implement `Transpile_MinimalShader_OutputContainsVoidMain` — assert GLSL output contains `void main(`.
- [ ] 22. Implement `Transpile_MinimalShader_OutputContainsVersion140` — assert output starts with `#version 140`.
- [ ] 23. Implement `Transpile_TexturedShader_OutputContainsSampler2D` — combined samplers applied.
- [ ] 24. Implement `Transpile_TexturedShader_OutputDoesNotContainSeparateSampler` — no separate `texture2D` + `sampler`.
- [ ] 25. Implement `Transpile_PassthroughVertex_YFlipIsApplied` — assert `gl_Position.y` is negated.
- [ ] 26. Implement `Transpile_InvalidSpirv_ReturnsShaderError` — assert error on garbage SPIR-V input.
- [ ] 27. Implement `Transpile_EmptySpirv_ReturnsShaderError` — assert error on empty word array.

### Verification

- [ ] 28. Run `/build` — zero warnings, zero errors on all three RIDs (requires native binary restore).
- [ ] 29. Run `/test --filter "Category=Integration"` — all 7 integration tests pass.
- [ ] 30. Run `/platform-check` — no new platform-specific assumptions introduced.

---

## 9. Acceptance Criteria

- [ ] P/Invoke bindings exist for all 11 SPIRV-Cross C API functions listed in Section 4.2
- [ ] `SpvcLoader` resolves the correct native library for the current RID at runtime
- [ ] All 5 compiler options are set before `compile()` is called
- [ ] `spvc_compiler_build_combined_image_samplers()` is called before every `spvc_compiler_compile()`
- [ ] `spvc_context_destroy()` is always called (verified by `try/finally`)
- [ ] GLSL output starts with `#version 140`
- [ ] Output is desktop GL (not GLES): `SpvcCompilerOption.GlslEs = false`
- [ ] Uniform naming convention researched (Task 3) and documented; post-processing step present if register names are required
- [ ] `GlslSource` stored on `PassBlob` and accessible to Phase 7 (binary writer)
- [ ] All 7 integration tests passing on Linux, macOS, and Windows CI

---

## 9. Known Risks and Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Uniform naming mismatch (register vs HLSL names) causes silent bind failures at MonoGame runtime | High — MojoShader path uses register names | Task 3 is mandatory before wiring into Phase 7 |
| SPIRV-Cross C API option integer values differ across versions | Medium — header is version-locked | Task 1 pins values to the exact header; comment documents source |
| `glCompileShader` rejects `#version 140` on some GLES-only targets | Low — MonoGame DesktopGL targets desktop OpenGL 3.1+ | Tracked; `GlslEs` flag available if GLES path added later |
| Y-flip interacts badly with MonoGame's own projection matrix conventions | Low — MonoGame's DX backend also flips; SPIRV-Cross flip matches expectation | Integration test with rendered output validates end-to-end (Phase 9) |
| `spvc_context_destroy` called while `glslPtr` string still in use | Medium — easy mistake | Step 10 (marshal to managed string) must precede step 11 in all code paths |
| Combined sampler names conflict with MonoGame's expected sampler uniform names | Medium — depends on Task 3 findings | If names conflict, use `spvc_compiler_set_name()` to override before compile |

---

## 10. Out of Scope for This Phase

- MSL transpilation (Metal backend) — deferred until Phase 6 is validated for OpenGL
- SPIR-V → HLSL re-emission (not needed for any current target)
- SPIR-V optimization passes (e.g., `spirv-opt`) — may be added in Phase 10
- GLES / WebGL output — `GlslEs` option present but not exercised; separate phase if needed
