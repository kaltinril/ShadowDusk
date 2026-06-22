using System.Text;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// A hand-written lexer for the supported GLSL subset. Tokenizes identifiers, numeric/bool
/// literals (including exponent forms and a tolerated trailing <c>f</c>/<c>F</c> suffix), and the
/// punctuation / operator set the parser needs. Comments and whitespace are skipped. 1-based
/// line/column is tracked on every token for diagnostics.
/// </summary>
internal sealed class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public Lexer(string src) => _src = src;

    private char Current => _pos < _src.Length ? _src[_pos] : '\0';

    private char Peek(int ahead = 1) =>
        _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

    private void Advance()
    {
        if (_pos >= _src.Length)
        {
            return;
        }

        if (_src[_pos] == '\n')
        {
            _line++;
            _col = 1;
        }
        else
        {
            _col++;
        }

        _pos++;
    }

    /// <summary>Tokenize the entire source into a list terminated by a single EndOfFile token.</summary>
    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTrivia();
            if (_pos >= _src.Length)
            {
                tokens.Add(new Token(TokenKind.EndOfFile, "", _line, _col));
                return tokens;
            }

            tokens.Add(NextToken());
        }
    }

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            char c = Current;
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
            }
            else if (c == '/' && Peek() == '/')
            {
                while (_pos < _src.Length && Current != '\n')
                {
                    Advance();
                }
            }
            else if (c == '/' && Peek() == '*')
            {
                int startLine = _line;
                int startCol = _col;
                Advance();
                Advance();
                while (_pos < _src.Length && !(Current == '*' && Peek() == '/'))
                {
                    Advance();
                }

                if (_pos >= _src.Length)
                {
                    throw new ConvertException("Unterminated block comment.", startLine, startCol, "/*");
                }

                Advance();
                Advance();
            }
            else
            {
                return;
            }
        }
    }

    private Token NextToken()
    {
        int line = _line;
        int col = _col;
        char c = Current;

        if (char.IsLetter(c) || c == '_')
        {
            return LexIdentifierOrKeyword(line, col);
        }

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek())))
        {
            return LexNumber(line, col);
        }

        return LexPunctuation(line, col);
    }

    private Token LexIdentifierOrKeyword(int line, int col)
    {
        var sb = new StringBuilder();
        while (char.IsLetterOrDigit(Current) || Current == '_')
        {
            sb.Append(Current);
            Advance();
        }

        string text = sb.ToString();
        return text is "true" or "false"
            ? new Token(TokenKind.BoolLiteral, text, line, col)
            : new Token(TokenKind.Identifier, text, line, col);
    }

    private Token LexNumber(int line, int col)
    {
        var sb = new StringBuilder();
        bool isFloat = false;

        // Hex integer literal.
        if (Current == '0' && (Peek() is 'x' or 'X'))
        {
            sb.Append(Current);
            Advance();
            sb.Append(Current);
            Advance();
            while (Uri.IsHexDigit(Current))
            {
                sb.Append(Current);
                Advance();
            }

            RejectUnsignedSuffix(line, col, sb.ToString());
            return new Token(TokenKind.IntLiteral, sb.ToString(), line, col);
        }

        while (char.IsDigit(Current))
        {
            sb.Append(Current);
            Advance();
        }

        if (Current == '.')
        {
            isFloat = true;
            sb.Append(Current);
            Advance();
            while (char.IsDigit(Current))
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (Current is 'e' or 'E')
        {
            isFloat = true;
            sb.Append(Current);
            Advance();
            if (Current is '+' or '-')
            {
                sb.Append(Current);
                Advance();
            }

            if (!char.IsDigit(Current))
            {
                throw new ConvertException("Malformed exponent in numeric literal.", line, col, sb.ToString());
            }

            while (char.IsDigit(Current))
            {
                sb.Append(Current);
                Advance();
            }
        }

        // Tolerate a trailing float suffix (f / F / lf / LF).
        if (Current is 'f' or 'F')
        {
            isFloat = true;
            Advance();
        }
        else
        {
            // An unsigned-integer suffix ('123u' / '0xFFu') marks uint/uvec bit arithmetic, which the
            // float-based subset cannot map faithfully. Reject it with a clear, located message rather than
            // leaving the stray 'u'/'U' to surface as a confusing "expected ')'" parse error downstream.
            RejectUnsignedSuffix(line, col, sb.ToString());
        }

        return new Token(isFloat ? TokenKind.FloatLiteral : TokenKind.IntLiteral, sb.ToString(), line, col);
    }

    /// <summary>Throw a clear, located diagnostic if a numeric literal is immediately followed by an
    /// unsigned-integer suffix (<c>u</c>/<c>U</c>). Unsigned/uvec bit arithmetic (typically an integer hash)
    /// is outside the supported float-based subset, so surface that precisely at the literal.</summary>
    private void RejectUnsignedSuffix(int line, int col, string literal)
    {
        if (Current is 'u' or 'U')
        {
            throw new ConvertException(
                $"Unsigned-integer literals ('{literal}{Current}') are outside the supported subset. This " +
                "shader uses unsigned-integer (uint / uvec) bit arithmetic, typically an integer hash, which " +
                "has no faithful mapping to the float-based shader subset.",
                line, col, literal + Current);
        }
    }

    private Token LexPunctuation(int line, int col)
    {
        char c = Current;
        char n = Peek();
        char n2 = Peek(2);

        // Three-character operators first (the compound shift assignments).
        switch (c)
        {
            case '<' when n == '<' && n2 == '=': return Three(TokenKind.ShlAssign, "<<=", line, col);
            case '>' when n == '>' && n2 == '=': return Three(TokenKind.ShrAssign, ">>=", line, col);
        }

        // Two-character operators next.
        switch (c)
        {
            case '+' when n == '+': return Two(TokenKind.Increment, "++", line, col);
            case '+' when n == '=': return Two(TokenKind.PlusAssign, "+=", line, col);
            case '-' when n == '-': return Two(TokenKind.Decrement, "--", line, col);
            case '-' when n == '=': return Two(TokenKind.MinusAssign, "-=", line, col);
            case '*' when n == '=': return Two(TokenKind.StarAssign, "*=", line, col);
            case '/' when n == '=': return Two(TokenKind.SlashAssign, "/=", line, col);
            case '%' when n == '=': return Two(TokenKind.PercentAssign, "%=", line, col);
            case '=' when n == '=': return Two(TokenKind.EqualEqual, "==", line, col);
            case '!' when n == '=': return Two(TokenKind.NotEqual, "!=", line, col);
            case '<' when n == '=': return Two(TokenKind.LessEqual, "<=", line, col);
            case '>' when n == '=': return Two(TokenKind.GreaterEqual, ">=", line, col);
            case '<' when n == '<': return Two(TokenKind.Shl, "<<", line, col);
            case '>' when n == '>': return Two(TokenKind.Shr, ">>", line, col);
            case '&' when n == '&': return Two(TokenKind.AndAnd, "&&", line, col);
            case '|' when n == '|': return Two(TokenKind.OrOr, "||", line, col);
            case '&' when n == '=': return Two(TokenKind.AmpAssign, "&=", line, col);
            case '|' when n == '=': return Two(TokenKind.PipeAssign, "|=", line, col);
            case '^' when n == '=': return Two(TokenKind.CaretAssign, "^=", line, col);
        }

        TokenKind kind = c switch
        {
            '(' => TokenKind.LParen,
            ')' => TokenKind.RParen,
            '{' => TokenKind.LBrace,
            '}' => TokenKind.RBrace,
            '[' => TokenKind.LBracket,
            ']' => TokenKind.RBracket,
            ';' => TokenKind.Semicolon,
            ',' => TokenKind.Comma,
            '.' => TokenKind.Dot,
            '?' => TokenKind.Question,
            ':' => TokenKind.Colon,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '%' => TokenKind.Percent,
            '=' => TokenKind.Assign,
            '<' => TokenKind.Less,
            '>' => TokenKind.Greater,
            '!' => TokenKind.Not,
            '&' => TokenKind.Amp,
            '|' => TokenKind.Pipe,
            '^' => TokenKind.Caret,
            '$' => throw new ConvertException(
                "Unexpected character '$': this is a host-template placeholder (e.g. '$param', as used " +
                "by parameterized shader hosts). The converter cannot resolve a host-substituted value, " +
                "so this shader depends on a host-provided template parameter and is outside the " +
                "supported subset.",
                line, col, "$"),
            _ => throw new ConvertException(
                $"Unexpected character '{c}'.", line, col, c.ToString()),
        };

        string text = c.ToString();
        Advance();
        return new Token(kind, text, line, col);
    }

    private Token Two(TokenKind kind, string text, int line, int col)
    {
        Advance();
        Advance();
        return new Token(kind, text, line, col);
    }

    private Token Three(TokenKind kind, string text, int line, int col)
    {
        Advance();
        Advance();
        Advance();
        return new Token(kind, text, line, col);
    }
}
