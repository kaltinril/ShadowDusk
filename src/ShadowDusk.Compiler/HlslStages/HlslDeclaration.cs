#nullable enable

namespace ShadowDusk.Compiler.HlslStages;

internal enum HlslDeclarationKind
{
    /// <summary>A preprocessor line: <c>#pragma</c>, <c>#include</c>, <c>#undef</c>, <c>#error</c>, <c>#line</c>.</summary>
    Directive,

    /// <summary>A <c>#define</c>; its name is the macro name.</summary>
    Macro,

    /// <summary>A balanced <c>#if</c> ... <c>#endif</c> block, kept opaque.</summary>
    Conditional,

    Struct,

    /// <summary><c>cbuffer</c> or <c>tbuffer</c>: its name is a reflected parameter buffer name.</summary>
    ConstantBuffer,

    /// <summary>A function with a body.</summary>
    Function,

    /// <summary>A function declaration with no body.</summary>
    Prototype,

    /// <summary><c>typedef</c>.</summary>
    Typedef,

    /// <summary>A texture, sampler or buffer object: its name is a reflected parameter name.</summary>
    Resource,

    /// <summary>A uniform global: its name is a reflected effect parameter name.</summary>
    Parameter,

    /// <summary>A <c>static</c> or <c>const</c> global: an internal name, not a parameter.</summary>
    Constant,

    /// <summary>A <c>technique</c> block. Not allowed in a stage source.</summary>
    Technique,
}

/// <summary>One top-level declaration of an HLSL source.</summary>
/// <param name="Kind">What the declaration is.</param>
/// <param name="Names">The names it declares (one, except for a multi-declarator global); empty for directives.</param>
/// <param name="Text">The original text, verbatim, with its trailing newline trimmed.</param>
/// <param name="Normalized">The text with comments removed and whitespace collapsed, for equality.</param>
/// <param name="StartLine">The 1-based line of its first character in the original source.</param>
/// <param name="StartColumn">The 1-based column of its first character.</param>
internal sealed record HlslDeclaration(
    HlslDeclarationKind Kind,
    IReadOnlyList<string> Names,
    string Text,
    string Normalized,
    int StartLine,
    int StartColumn)
{
    /// <summary>The first name, or an empty string.</summary>
    public string Name => Names.Count > 0 ? Names[0] : "";

    /// <summary>Whether a name clash with a different body is a resource conflict (SD0665).</summary>
    public bool IsResourceLike => Kind is HlslDeclarationKind.ConstantBuffer
        or HlslDeclarationKind.Resource or HlslDeclarationKind.Parameter;
}
