# Phase 2: FX9 Pre-Parser (`ShadowDusk.HLSL`)

> **Naming note:** `FxParseResult.StrippedHlsl` is misleading — the FX9 blocks are extracted into structured data before being removed from the text; nothing is discarded. Consider renaming to `CleanedHlsl` or `DxcReadyHlsl` in a future pass.

## Purpose

DXC rejects complete `.fx` files because `technique`, `pass`, and `sampler_state` blocks are not valid HLSL. The FX9 pre-parser runs *before* any DXC invocation. It extracts all FX9-specific metadata, removes those blocks from the source text, and returns clean HLSL that DXC can accept alongside structured data the rest of the pipeline needs.

This phase lives entirely in `src/ShadowDusk.HLSL/`.

---

## Deliverables

| # | Artifact | Path |
|---|---|---|
| 1 | Parser entry point | `src/ShadowDusk.HLSL/FxPreParser.cs` |
| 2 | Token / lexer types | `src/ShadowDusk.HLSL/Lexer/FxLexer.cs` |
| 3 | AST / data model | `src/ShadowDusk.HLSL/Ast/*.cs` |
| 4 | Render-state mapper | `src/ShadowDusk.HLSL/RenderStateMapper.cs` |
| 5 | Error types | `src/ShadowDusk.HLSL/FxParseError.cs` |
| 6 | Unit tests | `tests/ShadowDusk.HLSL.Tests/FxPreParserTests.cs` |

---

## Data Model

### Top-level result

```csharp
// ShadowDusk.HLSL/Ast/FxParseResult.cs
#nullable enable
public sealed record FxParseResult
{
    /// <summary>HLSL source with all FX9 blocks stripped.</summary>
    public required string StrippedHlsl { get; init; }

    public required IReadOnlyList<TechniqueInfo> Techniques { get; init; }

    /// <summary>Sampler objects declared with sampler_state {}.</summary>
    public required IReadOnlyList<SamplerInfo> Samplers { get; init; }

    /// <summary>Global parameter annotations extracted from < ... > blocks.</summary>
    public required IReadOnlyList<ParameterAnnotation> ParameterAnnotations { get; init; }
}
```

### Technique / Pass

```csharp
// ShadowDusk.HLSL/Ast/TechniqueInfo.cs
public sealed record TechniqueInfo
{
    public required string Name { get; init; }
    public required SourceSpan Span { get; init; }
    public required IReadOnlyList<PassInfo> Passes { get; init; }
    public required IReadOnlyList<AnnotationEntry> Annotations { get; init; }
}

// ShadowDusk.HLSL/Ast/PassInfo.cs
public sealed record PassInfo
{
    public required string Name { get; init; }
    public required SourceSpan Span { get; init; }

    /// <summary>e.g. "VSMain"</summary>
    public required string? VertexEntryPoint { get; init; }

    /// <summary>e.g. "PSMain"</summary>
    public required string? PixelEntryPoint { get; init; }

    /// <summary>e.g. "vs_3_0", "vs_5_0"</summary>
    public required string? VertexProfile { get; init; }

    /// <summary>e.g. "ps_3_0", "ps_5_0"</summary>
    public required string? PixelProfile { get; init; }

    /// <summary>Raw render-state key/value pairs as written in source.</summary>
    public required IReadOnlyList<RenderStateEntry> RenderStates { get; init; }

    public required IReadOnlyList<AnnotationEntry> Annotations { get; init; }
}
```

### Sampler

```csharp
// ShadowDusk.HLSL/Ast/SamplerInfo.cs
public sealed record SamplerInfo
{
    public required string Name { get; init; }

    /// <summary>"sampler", "sampler2D", "sampler3D", "samplerCUBE", "SamplerState", etc.</summary>
    public required string SamplerType { get; init; }

    /// <summary>The identifier inside Texture = &lt;X&gt; or Texture = X.</summary>
    public required string? TextureReference { get; init; }

    /// <summary>Filter, address, LOD state entries.</summary>
    public required IReadOnlyList<SamplerStateEntry> StateEntries { get; init; }

    public required SourceSpan Span { get; init; }
}

public sealed record SamplerStateEntry(string Key, string Value, SourceSpan Span);
```

### Render state

```csharp
// ShadowDusk.HLSL/Ast/RenderStateEntry.cs
public sealed record RenderStateEntry(string Key, string Value, SourceSpan Span);
```

### Annotations

```csharp
// ShadowDusk.HLSL/Ast/AnnotationEntry.cs
/// <summary>A single entry inside an annotation block: TypeName Key = Value;</summary>
public sealed record AnnotationEntry(string Type, string Name, string Value, SourceSpan Span);

// ShadowDusk.HLSL/Ast/ParameterAnnotation.cs
/// <summary>Annotation block attached to a global parameter declaration.</summary>
public sealed record ParameterAnnotation
{
    public required string ParameterName { get; init; }
    public required IReadOnlyList<AnnotationEntry> Entries { get; init; }
    public required SourceSpan Span { get; init; }
}
```

### Source span

```csharp
// ShadowDusk.HLSL/Ast/SourceSpan.cs
public readonly record struct SourceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public static readonly SourceSpan Unknown = new(0, 0, 0, 0);
    public override string ToString() => $"({StartLine},{StartColumn})-({EndLine},{EndColumn})";
}
```

### Error

```csharp
// ShadowDusk.HLSL/FxParseError.cs
public sealed record FxParseError
{
    public required string SourceFile { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required string Message { get; init; }

    public override string ToString() =>
        $"{SourceFile}({Line},{Column}): error FX{Code:D4}: {Message}";

    public required int Code { get; init; }
}
```

Error code table:

| Code | Meaning |
|---|---|
| FX0001 | Unexpected token (expected `{`) |
| FX0002 | Unexpected end of file inside block |
| FX0003 | Malformed `compile` expression (missing profile or entry point) |
| FX0004 | Shader profile unrecognized (e.g. `vs_99_0`) |
| FX0005 | Duplicate technique name |
| FX0006 | Duplicate pass name within technique |
| FX0007 | Unmatched `<` in annotation block |
| FX0008 | Missing `;` after statement |
| FX0009 | Sampler `sampler_state` block missing closing `}` |
| FX0010 | Unrecognized render-state key (warning-level; stored, not fatal) |

### Result carrier

```csharp
// ShadowDusk.Core/Result.cs  (already planned; included for reference)
public readonly struct Result<T, E>
{
    public bool IsOk { get; }
    public T Value { get; }   // valid when IsOk
    public E Error { get; }   // valid when !IsOk

    public static Result<T, E> Ok(T value) => ...;
    public static Result<T, E> Fail(E error) => ...;
}
```

Parser returns `Result<FxParseResult, FxParseError>`.

---

## Render State Mapping Table

The mapper in `RenderStateMapper.cs` translates FX9 render-state names and values to MonoGame equivalents. Keys are case-insensitive.

| FX9 Key | MonoGame Target | Notes |
|---|---|---|
| `CullMode` | `RasterizerState.CullMode` | None / CW / CCW |
| `AlphaBlendEnable` | `BlendState` activation | True enables blending |
| `SrcBlend` | `BlendState.ColorSourceBlend` | FX9 enum → `Blend` enum |
| `DestBlend` | `BlendState.ColorDestinationBlend` | |
| `BlendOp` | `BlendState.ColorBlendFunction` | Add/Subtract/RevSubtract/Min/Max |
| `AlphaBlendOp` | `BlendState.AlphaBlendFunction` | |
| `SrcBlendAlpha` | `BlendState.AlphaSourceBlend` | |
| `DestBlendAlpha` | `BlendState.AlphaDestinationBlend` | |
| `ColorWriteEnable` | `BlendState.ColorWriteChannels` | Bitmask |
| `DepthBufferEnable` | `DepthStencilState.DepthBufferEnable` | bool |
| `DepthBufferWriteEnable` | `DepthStencilState.DepthBufferWriteEnable` | bool |
| `DepthBufferFunction` | `DepthStencilState.DepthBufferFunction` | CompareFunction enum |
| `ZEnable` | `DepthStencilState.DepthBufferEnable` | alias for DepthBufferEnable |
| `ZWriteEnable` | `DepthStencilState.DepthBufferWriteEnable` | alias |
| `ZFunc` | `DepthStencilState.DepthBufferFunction` | alias |
| `StencilEnable` | `DepthStencilState.StencilEnable` | bool |
| `FillMode` | `RasterizerState.FillMode` | Solid / WireFrame |
| `MultiSampleAntiAlias` | `RasterizerState.MultiSampleAntiAlias` | bool |
| `ScissorTestEnable` | `RasterizerState.ScissorTestEnable` | bool |

Value normalization for booleans: `True`, `TRUE`, `true`, `1` → `true`; `False`, `FALSE`, `false`, `0` → `false`.

The mapper returns `MappedRenderState` (a simple value object). Unrecognized keys produce error code FX0010 stored as a non-fatal diagnostic, not a hard failure.

---

## Parser Architecture

### Lexer (`FxLexer`)

A single-pass character scanner. No regex. Produces a flat `Token[]` array consumed by the parser.

Token kinds:

| Kind | Description |
|---|---|
| `Identifier` | `[A-Za-z_][A-Za-z0-9_]*` |
| `Number` | Integer or float literal |
| `StringLiteral` | `"..."` (for annotation string values) |
| `LBrace` | `{` |
| `RBrace` | `}` |
| `LAngle` | `<` |
| `RAngle` | `>` |
| `LParen` | `(` |
| `RParen` | `)` |
| `Semicolon` | `;` |
| `Equals` | `=` |
| `Comma` | `,` |
| `Slash` | `/` |
| `Star` | `*` |
| `LineComment` | `// ...` (skipped or preserved for stripped output) |
| `BlockComment` | `/* ... */` |
| `Preprocessor` | `#if`, `#else`, `#endif`, `#define`, `#include` — kept verbatim |
| `Whitespace` | Collapsed; not emitted |
| `Newline` | Tracked for line counting; not emitted |
| `EOF` | Sentinel |

The lexer must track `(line, column)` for every token. Line numbers are 1-based; columns are 1-based.

Preprocessor directives (`#if SM4`, `#define`, `#include`, `#pragma`) must pass through to the stripped output unchanged — they are not parsed, just forwarded verbatim.

### Parser (`FxPreParser`) — top-level algorithm

```
ParseFile():
    while not EOF:
        peek next top-level token
        if keyword == "technique":
            ParseTechnique()  → append to techniques list
        elif keyword in SAMPLER_TYPES:
            peek ahead for "= sampler_state {"
            if match: ParseSamplerDecl()  → append to samplers list
            else: copy token range to stripped output as-is
        elif token == Identifier followed by "<":
            attempt ParseAnnotationOnParameter() → append to paramAnnotations
            copy parameter declaration to stripped output, without < ... > block
        else:
            copy token to stripped output verbatim
    return FxParseResult(...)
```

Sampler type keywords: `sampler`, `sampler1D`, `sampler2D`, `sampler3D`, `samplerCUBE`, `sampler_state`, `SamplerState`, `Sampler2D`, `Sampler3D`, `SamplerCube`.

### `ParseTechnique()`

```
consume "technique" keyword
read Name (Identifier or quoted string)
optionally consume annotation block < ... >
expect '{'
while peek != '}':
    if peek == "pass":
        ParsePass() → append to passes
    else:
        error FX0001
expect '}'
```

### `ParsePass()`

```
consume "pass" keyword
read Name (Identifier)
optionally consume annotation block < ... >
expect '{'
while peek != '}':
    read Key (Identifier)
    expect '='
    if Key == "VertexShader" or "PixelShader":
        expect "compile" keyword
        read Profile (Identifier, e.g. "vs_3_0")
        read EntryPoint (Identifier)
        expect '(' and ')' (may have empty arg list)
        expect ';'
    else:
        read Value (Identifier or Number or "True"/"False")
        expect ';'
        store as RenderStateEntry
expect '}'
```

### `ParseSamplerDecl()`

```
consume sampler-type keyword
read Name (Identifier)
expect '='
expect "sampler_state" keyword
expect '{'
while peek != '}':
    read Key (Identifier)
    expect '='
    if Key == "Texture":
        accept either '<' Identifier '>' or bare Identifier
        store as TextureReference
    else:
        read Value (Identifier or Number)
        store as SamplerStateEntry
    expect ';'
expect '}'
expect ';'
```

### `ParseAnnotationBlock()`

```
expect '<'
while peek != '>':
    read Type (Identifier)
    read Name (Identifier)
    expect '='
    read Value (StringLiteral | Number | Identifier)
    expect ';'
expect '>'
return List<AnnotationEntry>
```

### Stripped output construction

The pre-parser builds the stripped output by maintaining a `StringBuilder`. As each character is consumed:
- Tokens inside `technique { ... }` blocks are replaced with blank lines (line count preserved for error reporting).
- Tokens inside `sampler_state { ... }` blocks on a sampler declaration are replaced with blank lines.
- Annotation blocks `< ... >` on global parameters are removed; the surrounding declaration is kept.
- All other source text copies verbatim, preserving original line numbers.

Preserving line numbers means errors in DXC output map back to the original `.fx` file without line-number offset arithmetic.

---

## Shader Profile Validation

Recognized vertex shader profiles:

`vs_1_1`, `vs_2_0`, `vs_2_a`, `vs_2_sw`, `vs_3_0`, `vs_4_0`, `vs_4_1`, `vs_5_0`, `vs_6_0`, `vs_6_1`, `vs_6_2`, `vs_6_3`, `vs_6_4`, `vs_6_5`, `vs_6_6`, `vs_6_7`

Recognized pixel shader profiles:

`ps_1_1`, `ps_1_2`, `ps_1_3`, `ps_1_4`, `ps_2_0`, `ps_2_a`, `ps_2_b`, `ps_2_sw`, `ps_3_0`, `ps_4_0`, `ps_4_1`, `ps_5_0`, `ps_6_0`, `ps_6_1`, `ps_6_2`, `ps_6_3`, `ps_6_4`, `ps_6_5`, `ps_6_6`, `ps_6_7`

Unrecognized profile string → error FX0004. Store the raw string in the `PassInfo` regardless; fail compilation later at the DXC invocation stage so the error message names the unsupported profile in context.

---

## Implementation Tasks

### 1. Project scaffolding

1. [x] Confirm `src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj` exists; create if absent with `<TargetFramework>net8.0</TargetFramework>` and `<Nullable>enable</Nullable>`.
2. [x] Add project reference from `ShadowDusk.HLSL` to `ShadowDusk.Core` (for `Result<T,E>` and `ShaderError`).
3. [x] Create subdirectories: `Ast/`, `Lexer/`.
4. [x] Confirm `tests/ShadowDusk.HLSL.Tests/` project exists and references `ShadowDusk.HLSL`.

### 2. Define AST types

5. [x] Create `src/ShadowDusk.HLSL/Ast/SourceSpan.cs`.
6. [x] Create `src/ShadowDusk.HLSL/Ast/AnnotationEntry.cs`.
7. [x] Create `src/ShadowDusk.HLSL/Ast/ParameterAnnotation.cs`.
8. [x] Create `src/ShadowDusk.HLSL/Ast/RenderStateEntry.cs`.
9. [x] Create `src/ShadowDusk.HLSL/Ast/SamplerStateEntry.cs`.
10. [x] Create `src/ShadowDusk.HLSL/Ast/SamplerInfo.cs`.
11. [x] Create `src/ShadowDusk.HLSL/Ast/PassInfo.cs`.
12. [x] Create `src/ShadowDusk.HLSL/Ast/TechniqueInfo.cs`.
13. [x] Create `src/ShadowDusk.HLSL/Ast/FxParseResult.cs`.

### 3. Define error type

14. [x] Create `src/ShadowDusk.HLSL/FxParseError.cs` with error codes FX0001–FX0010 as documented above.
15. [x] Define `FxParseErrorCode` enum with integer values matching the codes.

### 4. Implement lexer

16. [x] Create `src/ShadowDusk.HLSL/Lexer/TokenKind.cs` enum.
17. [x] Create `src/ShadowDusk.HLSL/Lexer/Token.cs` record: `(TokenKind Kind, string Text, int Line, int Column)`.
18. [x] Create `src/ShadowDusk.HLSL/Lexer/FxLexer.cs`.
    - [x] Constructor accepts `string source` and `string sourceFile`.
    - [x] `Tokenize()` method returns `IReadOnlyList<Token>`.
    - [x] Handles `//` line comments (emits `LineComment` token, content preserved).
    - [x] Handles `/* */` block comments (emits `BlockComment` token).
    - [x] Handles preprocessor lines starting with `#` (emit `Preprocessor` token for entire line).
    - [x] Handles `<` and `>` as distinct from comparison operators (context-free; parser disambiguates).
    - [x] Tracks line/column accurately across `\r\n`, `\r`, `\n`.
    - [x] Does NOT skip whitespace inside string literals.

### 5. Implement parser

19. [x] Create `src/ShadowDusk.HLSL/FxPreParser.cs`.
    - [x] Static entry point: `public static Result<FxParseResult, FxParseError> Parse(string source, string sourceFile)`.
    - [x] Internally instantiates `FxLexer`, calls `Tokenize()`, then runs parsing logic.
    - [x] Maintains `int _pos` cursor into `Token[]`.
    - [x] `Peek(int offset = 0)` — look ahead without consuming.
    - [x] `Consume()` — advance and return current token.
    - [x] `Expect(TokenKind kind)` — consume or return `Result.Fail(FX0001)` with span.
    - [x] `TryConsume(TokenKind kind)` — consume if match, else no-op.
    - [x] All `Parse*` methods return `Result<..., FxParseError>`; caller propagates failures via early return.
    - [x] Implement `ParseTechnique()` per spec above.
    - [x] Implement `ParsePass()` per spec above.
    - [x] Implement `ParseSamplerDecl()` per spec above.
    - [x] Implement `ParseAnnotationBlock()` per spec above.
    - [x] Implement stripped-output builder: `StrippedHlslBuilder` (inner class or separate type using `StringBuilder`).
    - [x] Duplicate technique name check → FX0005.
    - [x] Duplicate pass name within technique → FX0006.

### 6. Implement render-state mapper

20. [x] Create `src/ShadowDusk.HLSL/RenderStateMapper.cs`.
    - [x] `public static MappedRenderState? TryMap(string key, string value)` — returns null for unrecognized key.
    - [x] Key lookup is case-insensitive.
    - [x] Value parsing handles boolean aliases (True/False/1/0).
    - [x] Returns structured `MappedRenderState` record: `(string MonoGameTarget, string NormalizedValue)`.
21. [x] Create `src/ShadowDusk.HLSL/Ast/MappedRenderState.cs`.

### 7. Write unit tests

22. [x] Create `tests/ShadowDusk.HLSL.Tests/FxPreParserTests.cs`.

Test cases (each is a `[Fact]` or `[Theory]`):

| # | Test name | Input | Expected |
|---|---|---|---|
| T01 | `Parse_EmptySource_ReturnsEmptyResult` | `""` | 0 techniques, 0 samplers, stripped == `""` |
| T02 | `Parse_SingleTechniqueOnePass_ExtractsTechniqueAndPass` | See snippet A | 1 technique, 1 pass, VS=`VSMain` vs_3_0, PS=`PSMain` ps_3_0 |
| T03 | `Parse_MultiPassTechnique_AllPassesExtracted` | See snippet B | 1 technique, 2 passes |
| T04 | `Parse_MultiTechnique_AllExtracted` | Two `technique` blocks | 2 techniques |
| T05 | `Parse_RenderStates_ExtractedPerPass` | Pass with CullMode/AlphaBlendEnable | RenderStates list populated |
| T06 | `Parse_SamplerState_Extracted` | See snippet C | 1 SamplerInfo, TextureReference set |
| T07 | `Parse_Annotations_ExtractedOnTechnique` | `technique T < string UIName = "X"; > { ... }` | TechniqueInfo.Annotations has 1 entry |
| T08 | `Parse_GlobalParameterAnnotation_ExtractedAndStripped` | `float P < float UIMin = 0; > = 0.5;` | ParameterAnnotations has 1 entry; stripped source has no `<...>` |
| T09 | `Parse_StrippedOutputPreservesLineNumbers` | Technique on line 5; HLSL decl on line 1 | Line 1 decl still on line 1 in stripped output |
| T10 | `Parse_MissingClosingBrace_ReturnsFX0002` | Technique block without `}` | `Result.IsOk == false`, Code == FX0002 |
| T11 | `Parse_MalformedCompile_ReturnsFX0003` | `VertexShader = compile ;` | Code == FX0003 |
| T12 | `Parse_UnrecognizedProfile_StoredRawNotFailed` | `compile vs_99_0 VSMain()` | IsSuccess; profile stored raw (fail deferred to DXC stage) |
| T13 | `Parse_DuplicateTechniqueName_ReturnsFX0005` | Two `technique Foo { }` blocks | Code == FX0005 |
| T14 | `Parse_DuplicatePassName_ReturnsFX0006` | Technique with two `pass Pass1` | Code == FX0006 |
| T15 | `Parse_UnclosedAnnotation_ReturnsFX0007` | `technique T < string UIName = "X"` | Code == FX0007 |
| T16 | `Parse_MissingSemicolon_ReturnsFX0008` | Render state line without `;` | Code == FX0008 |
| T17 | `Parse_PreprocessorDirectivesPreserved` | `#if SM4` in source | `#if SM4` present in stripped output |
| T18 | `Parse_LineCommentInsidePass_DoesNotConfuseParser` | `// VertexShader = ...` inside pass | Not interpreted as assignment |
| T19 | `Parse_ShaderProfileCasing_Accepted` | `VS_3_0` (uppercase) | Normalizes to `vs_3_0` |
| T20 | `Parse_BasicEffectLikePattern_32Techniques_Succeeds` | 32 technique declarations | 32 TechniqueInfos, no error |
| T21 | `RenderStateMapper_CullModeNone_MapsCorrectly` | Key=`CullMode`, Value=`None` | `MonoGameTarget` == `RasterizerState.CullMode` |
| T22 | `RenderStateMapper_UnrecognizedKey_ReturnsNull` | Key=`UnknownXyz` | returns null |
| T23 | `Parse_SamplerTextureAngleBracket_Extracted` | `Texture = <MyTex>;` | `TextureReference == "MyTex"` |
| T24 | `Parse_SamplerTextureBareIdentifier_Extracted` | `Texture = MyTex;` | `TextureReference == "MyTex"` |
| T25 | `Parse_ErrorContainsLineAndColumn` | Bad token at line 7, col 3 | `FxParseError.Line == 7`, `Column == 3` |

Test input snippets for reference:

**Snippet A — single pass:**
```hlsl
technique MyTechnique
{
    pass Pass1
    {
        VertexShader = compile vs_3_0 VSMain();
        PixelShader  = compile ps_3_0 PSMain();
    }
}
```

**Snippet B — multi-pass:**
```hlsl
technique Multi
{
    pass A { VertexShader = compile vs_3_0 VS1(); PixelShader = compile ps_3_0 PS1(); }
    pass B { VertexShader = compile vs_3_0 VS2(); PixelShader = compile ps_3_0 PS2(); }
}
```

**Snippet C — sampler:**
```hlsl
sampler2D MySampler = sampler_state {
    Texture   = <MyTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU  = Wrap;
};
```

---

## Edge Cases and Gotchas

1. **`#if SM4` / `#define` blocks.** MonoGame's `BasicEffect.fx` uses heavy preprocessor conditionals that gate between `vs_2_0` and `vs_4_0` technique blocks. The pre-parser must not attempt to evaluate preprocessor directives — it forwards them verbatim and parses whatever blocks are syntactically present at the top level (both branches). DXC handles `#if` separately.

2. **`technique11` keyword.** FX9 source uses `technique`; FX 4.0/5.0 uses `technique11`. The parser should accept both keywords and record a flag in `TechniqueInfo.IsEffect11` so downstream stages can warn about mixed syntax.

3. **Shader entry points with arguments in `compile` expression.** The `compile` syntax uses `EntryPoint()` with an empty argument list — do not confuse with a function call that has arguments. The parser should consume `(` and `)` and error (FX0003) if anything appears between them.

4. **Sampler declared without `= sampler_state`.** Some FX files declare `sampler MySampler;` with no initializer. These are not `sampler_state` blocks; do not strip them; pass through verbatim.

5. **Multiple samplers referencing the same texture.** Valid and common. `SamplerInfo.TextureReference` is a name only; binding resolution happens later.

6. **Case sensitivity.** FX9 keywords (`technique`, `pass`, `sampler_state`, `compile`) are case-insensitive in the original D3DX runtime. Treat them case-insensitively in the lexer or parser. Identifiers (technique names, entry points) are case-sensitive.

7. **Annotations on pass blocks.** Less common than on techniques/parameters, but valid. `parse_pass` must attempt to parse a `< ... >` block after the pass name.

8. **Whitespace and semicolons after `}`** in technique/sampler blocks. Technique blocks do NOT require a trailing `;`; sampler declarations DO (`};`). The parser must enforce this difference.

9. **Nested block comments.** Standard HLSL does not support `/* /* */ */` nesting; do not attempt to handle it. Treat the first `*/` as closing.

10. **Empty technique body.** A technique with zero passes is syntactically valid; produce a `TechniqueInfo` with an empty `Passes` list and no error.

---

## Files to Create (Summary)

```
src/ShadowDusk.HLSL/
├── Ast/
│   ├── AnnotationEntry.cs
│   ├── FxParseResult.cs
│   ├── MappedRenderState.cs
│   ├── ParameterAnnotation.cs
│   ├── PassInfo.cs
│   ├── RenderStateEntry.cs
│   ├── SamplerInfo.cs
│   ├── SamplerStateEntry.cs
│   ├── SourceSpan.cs
│   └── TechniqueInfo.cs
├── Lexer/
│   ├── FxLexer.cs
│   ├── Token.cs
│   └── TokenKind.cs
├── FxParseError.cs
├── FxParseErrorCode.cs
├── FxPreParser.cs
└── RenderStateMapper.cs

tests/ShadowDusk.HLSL.Tests/
└── FxPreParserTests.cs
```

---

## Acceptance Criteria

- [x] All 25 unit tests pass with `dotnet test`.
- [x] `FxPreParser.Parse` never throws; all error paths return `Result.Fail(FxParseError)`.
- [ ] Stripped HLSL output compiles without syntax errors when passed to DXC (verified by integration test in Phase 3).
- [x] Line numbers in `FxParseError` and `SourceSpan` match line numbers in the original `.fx` source.
- [x] `TechniqueInfo`, `PassInfo`, and `SamplerInfo` are fully populated for the MonoGame `BasicEffect.fx` canonical test file.
- [x] No `Thread.Sleep`, no disk I/O, no process spawning in any type in this phase.
- [x] All public types carry XML doc comments.
- [x] `#nullable enable` in every `.cs` file.

---

## Dependencies on Other Phases

| Dependency | Phase | Notes |
|---|---|---|
| `Result<T, E>` type | Phase 1 (Core) | Must exist before FxPreParser can be implemented |
| `ShaderError` base type | Phase 1 (Core) | `FxParseError` may share a common base or be independent |
| DXC invocation | Phase 3 (HLSL compiler) | Consumes `StrippedHlsl` and `PassInfo` produced here |
| MonoGame `.mgfx` writer | Phase 4 (Core emit) | Consumes `TechniqueInfo`/`PassInfo` from this phase |

---

## Out of Scope for This Phase

- Evaluating preprocessor conditionals (`#if` / `#define`).
- Parsing HLSL types, function signatures, or constant buffer layouts.
- Mapping render-state values to actual MonoGame API calls (that is Phase 4).
- Emitting any binary output.
- Geometry shader or compute shader `compile` expressions (reserved for a future pass type).
