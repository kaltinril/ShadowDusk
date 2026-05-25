# Phase 3 — Preprocessor & Macro Injection

**Depends on:** Phase 1 (solution scaffold), Phase 2 (FX9 pre-parser)
**Consumed by:** Phase 4 (DXC integration)

---

## Goal

Produce a `PreprocessedSource` value from Phase 2's cleaned HLSL — with all `#include` directives
resolved and inlined, platform macros prepended as text, and `#line` directives emitted so that
DXC diagnostic messages still reference the original source file and line. At the same time,
build the matching `-D` flag list that will be forwarded to DXC so macros are also active during
DXC's own include expansion.

---

## Deliverables

| Artifact | Project | Notes |
|---|---|---|
| `IIncludeResolver` | `ShadowDusk.Core` | Interface for include file lookup |
| `FileSystemIncludeResolver` | `ShadowDusk.Core` | Production: real `System.IO` paths |
| `InMemoryIncludeResolver` | `ShadowDusk.Core` | Test helper; no disk access |
| `MacroSet` | `ShadowDusk.Core` | Immutable set of name/value pairs for one target |
| `PlatformMacros` | `ShadowDusk.Core` | Static factory: returns `MacroSet` per `PlatformTarget` |
| `Preprocessor` | `ShadowDusk.Core` | Orchestrates macro inject + include flatten |
| `PreprocessedSource` | `ShadowDusk.Core` | Output record — text + `-D` list |
| `DxcIncludeHandler` | `ShadowDusk.HLSL` | IDxcIncludeHandler wrapper delegating to `IIncludeResolver` |
| Unit tests | `ShadowDusk.Core.Tests` | Pure, in-memory; no disk, no process |

---

## Platform Macro Table

The following macros are injected for each `PlatformTarget` enum value.

| PlatformTarget | MGFX | HLSL | GLSL | OPENGL | VULKAN | SM4 | SM6 |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| `DirectX` | 1 | 1 | — | — | — | 1 | — |
| `OpenGL` | 1 | — | 1 | 1 | — | — | — |
| `Vulkan` | 1 | 1 | — | — | 1 | — | 1 |

Rules:
- `MGFX=1` is **always** present — it is the universal MonoGame shader guard.
- `SM4=1` is critical for DirectX. MonoGame's built-in effects (e.g. `BasicEffect.fx`) gate
  `cbuffer` syntax on `#if SM4`. Omitting it causes the shader to compile against legacy D3D9
  constant syntax even when targeting DX11, producing either compile errors or incorrect binaries.
- `SM6=1` is set for Vulkan because ShadowDusk targets SPIR-V via DXC `-T *_6_0` profiles when
  compiling for the Vulkan backend.
- Macros are **not** defined with a value of `0`; they are simply absent when not applicable.

### Text Prepend Format

Macros are prepended to the cleaned HLSL source returned by Phase 2. The block is preceded by a
comment and followed by a `#line` directive that resets the compiler's view of the file back to
line 1 of the original source:

```hlsl
// ShadowDusk platform macros — DO NOT EDIT (generated)
#define MGFX 1
#define HLSL 1
#define SM4 1
#line 1 "Shaders/BasicEffect.fx"
<Phase 2 output begins here>
```

The `#line` directive is critical so that DXC error messages reference the user's file and original
line numbers rather than the macro-prepend block.

---

## Two Injection Points

Both points must be consistent — the same macro names and values applied to both:

1. **Text prepend (primary):** Macro `#define` lines inserted at the very top of the source string
   before it is handed to `Preprocessor.Flatten()` or DXC.

2. **DXC `-D` flags (backup):** Each macro is also added as a `-D NAME=VALUE` argument in
   `DxcCompileArgs`. This ensures macros are active during DXC's own preprocessor pass when it
   expands `#include` files that were not visited by the Phase 3 include flattener (e.g. includes
   inside HLSL standard library headers).

If a macro is present in both the text prepend and the `-D` list, DXC treats them as identical and
does not error. The duplication is harmless and intentional.

---

## Data Structures

```csharp
// ShadowDusk.Core/Preprocessor/MacroSet.cs
public sealed record MacroSet(IReadOnlyList<MacroDefinition> Macros)
{
    public string ToTextPrepend(string originalFilePath) { ... }
    public IReadOnlyList<string> ToDxcFlags() { ... }   // returns ["-D", "MGFX=1", "-D", "SM4=1", ...]
}

public sealed record MacroDefinition(string Name, int Value = 1);

// ShadowDusk.Core/Preprocessor/PreprocessedSource.cs
public sealed record PreprocessedSource(
    string Text,                         // fully flattened HLSL with macros prepended
    IReadOnlyList<string> DxcMacroFlags, // forwarded to DXC CompileArgs
    string OriginalFilePath              // for error attribution
);

// ShadowDusk.Core/Preprocessor/IncludeResolvedFile.cs
public sealed record IncludeResolvedFile(string FilePath, string Text);
```

---

## IIncludeResolver Interface

```csharp
// ShadowDusk.Core/Preprocessor/IIncludeResolver.cs
public interface IIncludeResolver
{
    /// <summary>
    /// Resolves an include path relative to the including file.
    /// Returns the resolved absolute path and file text, or an error.
    /// </summary>
    Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,   // null for the root file
        IReadOnlyList<string> additionalSearchPaths);
}
```

`FileSystemIncludeResolver` search order:
1. Directory of `includingFilePath` (the file that contains the `#include` directive).
2. Each path in `additionalSearchPaths` in order (populated from CLI `-I <path>` flags, Phase 8).
3. If not found in any location, return `Result.Failure` with a `ShaderError` that records
   `includingFilePath` and the line number of the failing `#include`.

`InMemoryIncludeResolver` accepts a `Dictionary<string, string>` mapping virtual paths to source
text. It must apply the same search-order logic, but against the dictionary keys rather than the
filesystem.

---

## Preprocessor.Flatten() Algorithm

```
Input:
  cleanedHlsl       : string        (Phase 2 output)
  originalFilePath  : string
  macros            : MacroSet
  includeResolver   : IIncludeResolver
  additionalPaths   : IReadOnlyList<string>

Output:
  Result<PreprocessedSource, ShaderError>

Algorithm:
  1. Prepend macro block (ToTextPrepend) to cleanedHlsl.
  2. Recursively process the source string:
     a. Scan line-by-line.
     b. For lines matching #include "..." or #include <...>:
        i.  Resolve the path via includeResolver.
        ii. If already in the visitedFiles set (circular include), return error.
        iii. Add the resolved path to visitedFiles.
        iv.  If #pragma once was seen for this file, skip (emit empty replacement).
        v.   Emit: #line 1 "<resolved-path>"
        vi.  Recursively flatten the included file.
        vii. Emit: #line <next-line> "<current-file>"   (resume line numbering)
     c. For lines matching #pragma once:
        - Add current file to pragmaOnceSet.
        - Emit nothing (consume the directive).
     d. For lines matching #pragma warning or any other #pragma:
        - Pass through unchanged.
     e. All other lines: pass through unchanged.
  3. Return PreprocessedSource with the flattened text and DxcMacroFlags.
```

Key constraint: the `visitedFiles` and `pragmaOnceSet` sets must be shared across the entire
recursive call tree for a single top-level file — pass them as parameters or hold them in a
`PreprocessorContext` struct.

---

## #line Directive Format

Use the HLSL/GLSL standard form:

```hlsl
#line <1-based-line-number> "<file-path>"
```

Example sequence for a two-level include:

```
(macro block — lines 1..N)
#line 1 "Shaders/Main.fx"
(lines 1..14 of Main.fx)
#line 1 "Shaders/Common.fxh"
(full text of Common.fxh)
#line 15 "Shaders/Main.fx"
(lines 15.. of Main.fx)
```

DXC respects `#line` for diagnostic source locations when compiled with standard HLSL mode.
The `originalFilePath` stored in `PreprocessedSource` must be an absolute path on disk (or the
virtual path key used for the in-memory resolver) so that MGCB-style error messages contain a
path the IDE can navigate to.

---

## DxcIncludeHandler

`DxcIncludeHandler` is defined in `ShadowDusk.HLSL` (not Core) because it takes a COM-style
dependency on `IDxcIncludeHandler` from `Vortice.Dxc`.

```csharp
// ShadowDusk.HLSL/DxcIncludeHandler.cs
internal sealed class DxcIncludeHandler : CallbackBase, IDxcIncludeHandler
{
    private readonly IIncludeResolver _resolver;
    private readonly string _rootFilePath;
    private readonly IReadOnlyList<string> _additionalPaths;

    public DxcIncludeHandler(
        IIncludeResolver resolver,
        string rootFilePath,
        IReadOnlyList<string> additionalPaths) { ... }

    public Result LoadSource(string fileName, out IDxcBlob? includeSource)
    {
        var result = _resolver.Resolve(fileName, _rootFilePath, _additionalPaths);
        if (!result.IsSuccess)
        {
            includeSource = null;
            return Result.Fail;
        }
        // Encode the text as a DXC blob
        includeSource = DxcUtils.CreateBlob(result.Value.Text, DXC_CP.UTF8);
        return Result.Ok;
    }
}
```

The `DxcIncludeHandler` instance is passed to `IDxcCompiler3.Compile()` in Phase 4.
This gives DXC's own preprocessor the same file-resolution logic used by the Phase 3 flattener,
so nested includes encountered during DXC's compilation (not just during Flatten) are handled
correctly.

---

## Error Model

All errors use `Result<T, ShaderError>` (no exceptions as control flow). Relevant new error kinds:

| ShaderError.Kind | When |
|---|---|
| `IncludeNotFound` | Resolver cannot locate the file in any search path |
| `CircularInclude` | A file includes itself directly or transitively |
| `PragmaOnceConflict` | Reserved; not currently raised — logged as warning only |

`ShaderError` must carry:
- `IncludingFilePath` — the file that contained the `#include` directive
- `IncludingLineNumber` — 1-based line in that file
- `RequestedPath` — the raw string inside `#include "..."` or `#include <...>`
- `SearchedPaths` — list of absolute paths that were tried (for diagnostics)

---

## PlatformMacros Static Factory

```csharp
// ShadowDusk.Core/Preprocessor/PlatformMacros.cs
public static class PlatformMacros
{
    public static MacroSet For(PlatformTarget platform) => platform switch
    {
        PlatformTarget.DirectX => new MacroSet([
            new("MGFX"), new("HLSL"), new("SM4")]),
        PlatformTarget.OpenGL => new MacroSet([
            new("MGFX"), new("GLSL"), new("OPENGL")]),
        PlatformTarget.Vulkan => new MacroSet([
            new("MGFX"), new("HLSL"), new("VULKAN"), new("SM6")]),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };
}
```

`MacroSet.ToDxcFlags()` produces alternating `-D` entries:

```
["-D", "MGFX=1", "-D", "HLSL=1", "-D", "SM4=1"]
```

`MacroSet.ToTextPrepend(filePath)` produces the `#define` block followed by `#line 1 "..."`.

---

## Task Checklist

### 1. Data types and interfaces

- [ ] 1.1 Define `MacroDefinition` record in `ShadowDusk.Core/Preprocessor/MacroDefinition.cs`.
- [ ] 1.2 Define `MacroSet` record with `ToTextPrepend(string)` and `ToDxcFlags()` methods.
- [ ] 1.3 Define `IIncludeResolver` interface.
- [ ] 1.4 Define `IncludeResolvedFile` record.
- [ ] 1.5 Define `PreprocessedSource` record.
- [ ] 1.6 Extend `ShaderError` with `IncludeNotFound` and `CircularInclude` kinds and the extra
         fields listed in the Error Model section.

### 2. PlatformMacros factory

- [ ] 2.1 Implement `PlatformMacros.For(PlatformTarget)` with the macro table from this document.
- [ ] 2.2 Unit test: assert exact macro names (no extras, no missing) for each platform.
- [ ] 2.3 Unit test: `ToDxcFlags()` produces correctly interleaved `-D NAME=VALUE` strings.
- [ ] 2.4 Unit test: `ToTextPrepend("foo.fx")` contains the `#line 1 "foo.fx"` footer and all
         `#define` lines in order.

### 3. InMemoryIncludeResolver

- [ ] 3.1 Implement `InMemoryIncludeResolver` backed by `Dictionary<string, string>`.
- [ ] 3.2 Apply the same search-order rules as `FileSystemIncludeResolver` (including-file
         directory first, then additional paths).
- [ ] 3.3 Unit test: resolves a file present in the dictionary.
- [ ] 3.4 Unit test: returns `IncludeNotFound` error with correct `SearchedPaths` when absent.

### 4. FileSystemIncludeResolver

- [ ] 4.1 Implement `FileSystemIncludeResolver` using `System.IO.File.ReadAllTextAsync`.
- [ ] 4.2 Respect search order: sibling directory of including file, then `additionalSearchPaths`.
- [ ] 4.3 Canonicalize resolved paths with `Path.GetFullPath` to ensure OS-independent comparison
         in the `visitedFiles` set.
- [ ] 4.4 Integration test (tagged `[Trait("Category","Integration")]`): resolve a real `.fxh`
         file from disk.

### 5. Preprocessor.Flatten()

- [ ] 5.1 Create `PreprocessorContext` internal class to hold `visitedFiles` (HashSet) and
         `pragmaOnceSet` (HashSet) — shared across the recursive call tree.
- [ ] 5.2 Implement recursive `FlattenFile()` method using the algorithm in this document.
- [ ] 5.3 Emit `#line` directives before each included file and after returning to the including
         file.
- [ ] 5.4 Handle `#pragma once`: consume the directive and add the resolved file path to
         `pragmaOnceSet`; subsequent re-includes of the same file emit nothing.
- [ ] 5.5 Handle `#pragma warning` and unknown `#pragma`: pass through unchanged.
- [ ] 5.6 Detect circular includes: if `FlattenFile()` is called for a path already in
         `visitedFiles`, return a `CircularInclude` error with file and line attribution.
- [ ] 5.7 Compose final `PreprocessedSource`: macro prepend + flattened body + `DxcMacroFlags`
         from `MacroSet.ToDxcFlags()`.

### 6. Preprocessor unit tests

- [ ] 6.1 **Basic macro injection:** verify `PreprocessedSource.Text` starts with the expected
         `#define` block and the `#line 1 "..."` reset for each `PlatformTarget`.
- [ ] 6.2 **Single #include:** root file includes one header; output contains header text inline
         with correct `#line` directives wrapping it.
- [ ] 6.3 **Nested #include:** header A includes header B; output flattens both in correct order
         with correct `#line` directives.
- [ ] 6.4 **Circular include direct:** file A includes file A; expect `CircularInclude` error
         naming file A and the `#include` line.
- [ ] 6.5 **Circular include transitive:** A includes B, B includes A; expect `CircularInclude`
         naming B's `#include` line.
- [ ] 6.6 **#pragma once:** file included twice; second occurrence produces no duplicate output.
- [ ] 6.7 **#pragma warning pass-through:** `#pragma warning(disable: 3571)` appears unchanged in
         output.
- [ ] 6.8 **Unknown #pragma pass-through:** `#pragma custom_thing` appears unchanged.
- [ ] 6.9 **Missing include error:** include path not in resolver; `IncludeNotFound` error carries
         `IncludingFilePath`, `IncludingLineNumber`, `RequestedPath`, and non-empty
         `SearchedPaths`.
- [ ] 6.10 **DxcMacroFlags round-trip:** `PreprocessedSource.DxcMacroFlags` equals the output of
          `PlatformMacros.For(platform).ToDxcFlags()`.
- [ ] 6.11 **Line number preservation:** after include expansion, lines in the root file that
          follow an include have a `#line` directive showing the correct line number and the root
          file path.

### 7. DxcIncludeHandler (ShadowDusk.HLSL)

- [ ] 7.1 Implement `DxcIncludeHandler` delegating `LoadSource` to `IIncludeResolver`.
- [ ] 7.2 Map `Result.Failure` from the resolver to a DXC `E_FAIL` HRESULT return, setting
         `includeSource` to `null`.
- [ ] 7.3 Encode included text as UTF-8 via `DxcUtils.CreateBlob` (or equivalent Vortice API).
- [ ] 7.4 Unit/smoke test: construct a `DxcIncludeHandler` with an `InMemoryIncludeResolver`
         containing a known file; verify `LoadSource` returns success and the correct blob bytes.

### 8. Wiring into the compilation pipeline

- [ ] 8.1 Add `IIncludeResolver` and `IReadOnlyList<string> AdditionalIncludePaths` parameters to
         `CompileRequest` (or equivalent input record used in Phase 4).
- [ ] 8.2 `Preprocessor.Flatten()` is called after Phase 2 and before DXC invocation; its output
         replaces the raw cleaned HLSL as the text sent to DXC.
- [ ] 8.3 `PreprocessedSource.DxcMacroFlags` is merged into the DXC compile arguments list in
         Phase 4 — no duplication guard needed, but document that duplication is harmless.
- [ ] 8.4 `DxcIncludeHandler` instance is constructed from the same `IIncludeResolver` and
         forwarded to `IDxcCompiler3.Compile()` in Phase 4.

---

## Acceptance Criteria

- [ ] `PlatformMacros.For(DirectX).Macros` contains exactly `MGFX`, `HLSL`, `SM4` — no extras.
- [ ] `PlatformMacros.For(OpenGL).Macros` contains exactly `MGFX`, `GLSL`, `OPENGL`.
- [ ] `PlatformMacros.For(Vulkan).Macros` contains exactly `MGFX`, `HLSL`, `VULKAN`, `SM6`.
- [ ] `PreprocessedSource.Text` begins with the macro `#define` block and is followed by
      `#line 1 "<original-file-path>"` before any shader source.
- [ ] `PreprocessedSource.DxcMacroFlags` contains a `-D NAME=VALUE` pair for every macro in the
      `MacroSet`.
- [ ] All `#include` directives in the root and any transitively included files are resolved and
      inlined; no `#include` lines appear in `PreprocessedSource.Text`.
- [ ] `#line` directives in the flattened output correctly attribute every line back to its source
      file and 1-based line number.
- [ ] Circular includes (direct and transitive) are detected before any infinite recursion and
      returned as `CircularInclude` errors with file-and-line attribution.
- [ ] `#pragma once` prevents duplicate inlining of a file.
- [ ] Unknown pragmas are preserved verbatim without error.
- [ ] Missing include errors include `IncludingFilePath`, `IncludingLineNumber`, `RequestedPath`,
      and `SearchedPaths`.
- [ ] All unit tests in `ShadowDusk.Core.Tests` covering this phase pass with no disk I/O and no
      child-process invocations.

---

## Out of Scope for This Phase

- Parsing or evaluating `#if` / `#ifdef` / `#elif` / `#else` / `#endif` — these are passed
  through verbatim and left for DXC's preprocessor to evaluate.
- Macro expansion of `#define`d symbols — ShadowDusk only injects platform macros; it does not
  implement a C preprocessor expression evaluator.
- System include paths (`#include <...>`) beyond the additional search paths — DXC handles standard
  HLSL intrinsics natively without needing to resolve them through `IIncludeResolver`.
- Metal / MSL macro variants — out of scope until Phase 6 (SPIRV-Cross transpilation) is complete.
