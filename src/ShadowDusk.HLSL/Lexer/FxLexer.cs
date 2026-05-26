#nullable enable

using System.Text;

namespace ShadowDusk.HLSL.Lexer;

/// <summary>
/// Single-pass character scanner that tokenises an FX9 HLSL source file.
/// Whitespace and newlines are not emitted as tokens; they update line/column tracking only.
/// Comments and preprocessor directives are emitted verbatim so the parser can
/// reconstruct stripped output that preserves original line numbers.
/// </summary>
public sealed class FxLexer
{
    private readonly string _source;
    private int _pos;
    private int _line;
    private int _col;

    /// <param name="source">The full text of the .fx source file.</param>
    /// <param name="sourceFile">Display name used in diagnostics (reserved for future error reporting).</param>
    public FxLexer(string source, string sourceFile)
    {
        _source = source;
        // sourceFile is accepted for API symmetry with the parser; the lexer does not
        // currently emit diagnostics but may do so in future passes.
        _ = sourceFile;
        _pos = 0;
        _line = 1;
        _col = 1;
    }

    /// <summary>Tokenises the entire source and returns an immutable token list ending with an EOF token.</summary>
    public IReadOnlyList<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_pos < _source.Length)
        {
            SkipWhitespace();
            if (_pos >= _source.Length)
                break;

            char c = _source[_pos];

            // Preprocessor directive — consume the entire logical line (respects line-continuation \).
            if (c == '#')
            {
                tokens.Add(ReadPreprocessor());
                continue;
            }

            // Line comment //
            if (c == '/' && Peek(1) == '/')
            {
                tokens.Add(ReadLineComment());
                continue;
            }

            // Block comment /* ... */
            if (c == '/' && Peek(1) == '*')
            {
                tokens.Add(ReadBlockComment());
                continue;
            }

            // String literal
            if (c == '"')
            {
                tokens.Add(ReadStringLiteral());
                continue;
            }

            // Number literal
            if (char.IsAsciiDigit(c))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // Identifier or keyword
            if (char.IsAsciiLetter(c) || c == '_')
            {
                tokens.Add(ReadIdentifier());
                continue;
            }

            // Single-character tokens
            TokenKind? single = c switch
            {
                '{' => TokenKind.LBrace,
                '}' => TokenKind.RBrace,
                '<' => TokenKind.LAngle,
                '>' => TokenKind.RAngle,
                '(' => TokenKind.LParen,
                ')' => TokenKind.RParen,
                ';' => TokenKind.Semicolon,
                '=' => TokenKind.Equals,
                ',' => TokenKind.Comma,
                '/' => TokenKind.Slash,
                '*' => TokenKind.Star,
                '.' => TokenKind.Dot,
                _ => null,
            };

            if (single is not null)
            {
                tokens.Add(new Token(single.Value, c.ToString(), _line, _col));
                Advance();
                continue;
            }

            // Unknown character — advance so we don't spin forever.
            Advance();
        }

        tokens.Add(new Token(TokenKind.EOF, string.Empty, _line, _col));
        return tokens;
    }

    // -------------------------------------------------------------------------
    // Scanning helpers
    // -------------------------------------------------------------------------

    private char Peek(int offset = 0)
    {
        int index = _pos + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    /// <summary>
    /// Advances position by one character, updating line/column counters.
    /// CRLF is treated as a single newline to avoid double-counting.
    /// </summary>
    private void Advance()
    {
        if (_pos >= _source.Length)
            return;

        char c = _source[_pos];
        _pos++;

        if (c == '\r')
        {
            // Consume \n of a CRLF pair without counting it as a separate line break.
            if (_pos < _source.Length && _source[_pos] == '\n')
                _pos++;
            _line++;
            _col = 1;
        }
        else if (c == '\n')
        {
            _line++;
            _col = 1;
        }
        else
        {
            _col++;
        }
    }

    private void SkipWhitespace()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                Advance();
            else
                break;
        }
    }

    private Token ReadIdentifier()
    {
        int startLine = _line, startCol = _col;
        var sb = new StringBuilder();

        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
                Advance();
            }
            else
            {
                break;
            }
        }

        return new Token(TokenKind.Identifier, sb.ToString(), startLine, startCol);
    }

    private Token ReadNumber()
    {
        int startLine = _line, startCol = _col;
        var sb = new StringBuilder();

        while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
        {
            sb.Append(_source[_pos]);
            Advance();
        }

        // Optional fractional part.
        if (_pos < _source.Length && _source[_pos] == '.')
        {
            sb.Append('.');
            Advance();
            while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
            {
                sb.Append(_source[_pos]);
                Advance();
            }
        }

        // Optional float suffix (f/F) — consume to avoid leaving it as a stray identifier.
        if (_pos < _source.Length && (_source[_pos] == 'f' || _source[_pos] == 'F'))
        {
            sb.Append(_source[_pos]);
            Advance();
        }

        return new Token(TokenKind.Number, sb.ToString(), startLine, startCol);
    }

    private Token ReadStringLiteral()
    {
        int startLine = _line, startCol = _col;
        var sb = new StringBuilder();

        // Consume opening quote.
        sb.Append('"');
        Advance();

        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (c == '\\' && _pos + 1 < _source.Length)
            {
                // Escaped character — consume both chars verbatim.
                sb.Append(c);
                Advance();
                sb.Append(_source[_pos]);
                Advance();
                continue;
            }

            if (c == '"')
            {
                sb.Append('"');
                Advance();
                break;
            }

            sb.Append(c);
            Advance();
        }

        return new Token(TokenKind.StringLiteral, sb.ToString(), startLine, startCol);
    }

    private Token ReadLineComment()
    {
        int startLine = _line, startCol = _col;
        var sb = new StringBuilder();

        // Consume characters until end-of-line (the newline is included in the token text
        // so the parser can reproduce it verbatim in stripped output).
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\r' || c == '\n')
            {
                // Include the newline in the token text then stop.
                if (c == '\r' && _pos + 1 < _source.Length && _source[_pos + 1] == '\n')
                {
                    sb.Append("\r\n");
                    _pos += 2;
                }
                else
                {
                    sb.Append(c);
                    _pos++;
                }
                _line++;
                _col = 1;
                break;
            }

            sb.Append(c);
            Advance();
        }

        return new Token(TokenKind.LineComment, sb.ToString(), startLine, startCol);
    }

    private Token ReadBlockComment()
    {
        int startLine = _line, startCol = _col;
        var sb = new StringBuilder();

        // Consume '/' and '*'.
        sb.Append("/*");
        Advance(); // /
        Advance(); // *

        while (_pos < _source.Length)
        {
            if (_source[_pos] == '*' && Peek(1) == '/')
            {
                sb.Append("*/");
                Advance(); // *
                Advance(); // /
                break;
            }

            sb.Append(_source[_pos]);
            Advance();
        }

        return new Token(TokenKind.BlockComment, sb.ToString(), startLine, startCol);
    }

    private Token ReadPreprocessor()
    {
        int startLine = _line, startCol = _col;
        var sb = new StringBuilder();

        // A preprocessor directive occupies one logical line; backslash-newline continues it.
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (c == '\\' && _pos + 1 < _source.Length)
            {
                char next = _source[_pos + 1];
                if (next == '\r' || next == '\n')
                {
                    // Line continuation — include the backslash and the newline.
                    sb.Append(c);
                    Advance(); // backslash
                    if (_pos < _source.Length)
                    {
                        sb.Append(_source[_pos]);
                        Advance(); // newline (handles CRLF via Advance)
                    }
                    continue;
                }
            }

            if (c == '\r' || c == '\n')
            {
                // Include terminating newline so line numbers in stripped output stay aligned.
                if (c == '\r' && _pos + 1 < _source.Length && _source[_pos + 1] == '\n')
                {
                    sb.Append("\r\n");
                    _pos += 2;
                }
                else
                {
                    sb.Append(c);
                    _pos++;
                }
                _line++;
                _col = 1;
                break;
            }

            sb.Append(c);
            Advance();
        }

        return new Token(TokenKind.Preprocessor, sb.ToString(), startLine, startCol);
    }
}
