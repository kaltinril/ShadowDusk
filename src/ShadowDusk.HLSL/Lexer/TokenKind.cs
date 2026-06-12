#nullable enable

namespace ShadowDusk.HLSL.Lexer;

/// <summary>Categories of tokens produced by <see cref="FxLexer"/>.</summary>
public enum TokenKind
{
    /// <summary>[A-Za-z_][A-Za-z0-9_]* — keywords and identifiers share this kind.</summary>
    Identifier,

    /// <summary>
    /// Numeric literal: decimal integer/float with optional fraction, exponent
    /// (<c>1e-4</c>) and f/F suffix, or a hex literal (<c>0x80FF8080</c>) with an
    /// optional u/U/l/L suffix.
    /// </summary>
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

    /// <summary>- — emitted so negative numeric values (e.g. <c>DepthBias = -0.5;</c>) are visible to the parser.</summary>
    Minus,

    /// <summary>
    /// A character the lexer does not recognise as part of any token. Carried through
    /// (instead of silently skipped) so the parser can fail loudly when it reaches one;
    /// the known HLSL operator characters the FX pre-parser deliberately tokenizes-through
    /// (<c>: + [ ] &amp; | ! ? % ^ ~</c>) are still skipped, not emitted as Unknown.
    /// </summary>
    Unknown,

    /// <summary>A // line comment including leading slashes and trailing newline (if any).</summary>
    LineComment,

    /// <summary>A /* ... */ block comment including delimiters.</summary>
    BlockComment,

    /// <summary>A preprocessor directive line beginning with #.</summary>
    Preprocessor,

    /// <summary>End of the input stream.</summary>
    EOF,
}
