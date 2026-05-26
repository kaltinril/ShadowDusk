#nullable enable

namespace ShadowDusk.HLSL.Lexer;

/// <summary>Categories of tokens produced by <see cref="FxLexer"/>.</summary>
public enum TokenKind
{
    /// <summary>[A-Za-z_][A-Za-z0-9_]* — keywords and identifiers share this kind.</summary>
    Identifier,

    /// <summary>[0-9]+(\.[0-9]*)? — integer or floating-point literal.</summary>
    Number,

    /// <summary>A double-quoted string literal including the surrounding quotes.</summary>
    StringLiteral,

    /// <summary>{</summary>
    LBrace,

    /// <summary>}</summary>
    RBrace,

    /// <summary>&lt;</summary>
    LAngle,

    /// <summary>&gt;</summary>
    RAngle,

    /// <summary>(</summary>
    LParen,

    /// <summary>)</summary>
    RParen,

    /// <summary>;</summary>
    Semicolon,

    /// <summary>=</summary>
    Equals,

    /// <summary>,</summary>
    Comma,

    /// <summary>/</summary>
    Slash,

    /// <summary>*</summary>
    Star,

    /// <summary>. — member-access or swizzle separator; emitted so adjacent identifiers like color.a remain distinct.</summary>
    Dot,

    /// <summary>A // line comment including leading slashes and trailing newline (if any).</summary>
    LineComment,

    /// <summary>A /* ... */ block comment including delimiters.</summary>
    BlockComment,

    /// <summary>A preprocessor directive line beginning with #.</summary>
    Preprocessor,

    /// <summary>End of the input stream.</summary>
    EOF,
}
