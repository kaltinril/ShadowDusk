#nullable enable

namespace ShadowDusk.HLSL.Lexer;

/// <summary>An immutable token produced by <see cref="FxLexer"/>.</summary>
/// <param name="Kind">The token category.</param>
/// <param name="Text">The raw source text for this token.</param>
/// <param name="Line">1-based line number of the first character.</param>
/// <param name="Column">1-based column number of the first character.</param>
public sealed record Token(TokenKind Kind, string Text, int Line, int Column);
