namespace ShadowDusk.ShaderToy;

/// <summary>The lexical category of a <see cref="Token"/>.</summary>
internal enum TokenKind
{
    // literals / identifiers
    Identifier,
    IntLiteral,
    FloatLiteral,
    BoolLiteral,

    // punctuation
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    Semicolon,
    Comma,
    Dot,
    Question,
    Colon,

    // operators
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Assign,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    Increment,
    Decrement,
    EqualEqual,
    NotEqual,
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    AndAnd,
    OrOr,
    Not,
    Amp,
    Pipe,
    Caret,
    Shl,
    Shr,
    AmpAssign,
    PipeAssign,
    CaretAssign,
    ShlAssign,
    ShrAssign,

    EndOfFile,
}

/// <summary>A single lexical token, carrying 1-based source position for diagnostics.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
