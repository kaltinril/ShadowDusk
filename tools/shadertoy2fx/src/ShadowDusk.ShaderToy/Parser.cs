using ShadowDusk.ShaderToy.Ast;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// A recursive-descent parser for the supported GLSL subset. Produces a <see cref="TranslationUnit"/>
/// of <c>const</c> globals and user functions. Anything outside the subset (user <c>struct</c>s,
/// declared arrays, <c>switch</c>, unsupported types) throws a located <see cref="ConvertException"/>.
/// </summary>
internal sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    /// <summary>glslViewer-alias name → ShaderToy built-in it was folded onto (e.g. u_time → iTime).</summary>
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public Parser(IReadOnlyList<Token> tokens) => _tokens = tokens;

    private Token Current => _tokens[_pos];

    private Token Peek(int ahead = 1)
    {
        int i = _pos + ahead;
        return i < _tokens.Count ? _tokens[i] : _tokens[^1];
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (Check(kind))
        {
            _pos++;
            return true;
        }

        return false;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (!Check(kind))
        {
            throw new ConvertException(
                $"Expected {what} but found '{Current.Text}'.", Current.Line, Current.Column, Current.Text);
        }

        Token t = Current;
        _pos++;
        return t;
    }

    private static ConvertException Reject(string message, Token at) =>
        new(message, at.Line, at.Column, at.Text);

    // ── translation unit ────────────────────────────────────────────────────

    public TranslationUnit Parse()
    {
        var globals = new List<GlobalConstDecl>();
        var functions = new List<FunctionDecl>();
        var customUniforms = new List<CustomUniformDecl>();

        while (!Check(TokenKind.EndOfFile))
        {
            ParseTopLevel(globals, functions, customUniforms);
        }

        return new TranslationUnit
        {
            Globals = globals,
            Functions = functions,
            CustomUniforms = customUniforms,
            Aliases = _aliases,
        };
    }

    private void ParseTopLevel(
        List<GlobalConstDecl> globals, List<FunctionDecl> functions, List<CustomUniformDecl> customUniforms)
    {
        Token start = Current;

        if (Check(TokenKind.Identifier) && Current.Text == "struct")
        {
            throw Reject(
                "User-defined 'struct' is outside the supported subset.", start);
        }

        if (Check(TokenKind.Identifier) && (Current.Text is "uniform" or "varying" or "attribute" or "in" or "out"))
        {
            HandleQualifiedTopLevelDecl(start, customUniforms);
            return;
        }

        bool isConst = false;
        if (Check(TokenKind.Identifier) && Current.Text == "const")
        {
            isConst = true;
            _pos++;
        }

        // Must be a type now.
        Token typeTok = Current;
        string typeName = ExpectTypeName();
        Token nameTok = Expect(TokenKind.Identifier, "an identifier");
        string name = nameTok.Text;

        if (Check(TokenKind.LBracket))
        {
            throw Reject(
                "User-declared arrays are outside the supported subset.", Current);
        }

        if (Check(TokenKind.LParen))
        {
            if (isConst)
            {
                throw Reject("'const' cannot qualify a function.", typeTok);
            }

            FunctionDecl fn = ParseFunctionRest(typeName, name, start);
            functions.Add(fn);
            return;
        }

        // Global variable: only `const` globals are supported.
        if (!isConst)
        {
            throw Reject(
                $"Top-level non-const global '{name}' is outside the supported subset (only 'const' globals).",
                nameTok);
        }

        Expect(TokenKind.Assign, "'=' (a const global requires an initializer)");
        Expr init = ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        globals.Add(new GlobalConstDecl
        {
            TypeName = typeName,
            Name = name,
            Initializer = init,
            Line = start.Line,
            Column = start.Column,
        });
    }

    /// <summary>
    /// Handle a top-level <c>uniform</c>/<c>varying</c>/<c>attribute</c>/<c>in</c>/<c>out</c>
    /// declaration. Three outcomes:
    /// <list type="bullet">
    /// <item>A redundant re-declaration of a KNOWN ShaderToy built-in (e.g. <c>uniform float iTime;</c>)
    /// is silently DROPPED — the harness already injects that global, so it is harmless.</item>
    /// <item>A <c>uniform</c> of a SUPPORTED type (scalar/vector/matrix, or <c>sampler2D</c>) is
    /// ACCEPTED as a custom uniform: emitted as an HLSL effect parameter the consumer drives. A common
    /// glslViewer alias (<c>u_time</c>) is folded onto its ShaderToy built-in.</item>
    /// <item>Anything else (an unsupported uniform type, a uniform with an initializer, or a
    /// <c>varying</c>/<c>attribute</c>/<c>in</c>/<c>out</c> of a custom name — none of which the host
    /// can drive) is a loud, located reject.</item>
    /// </list>
    /// </summary>
    private void HandleQualifiedTopLevelDecl(Token start, List<CustomUniformDecl> customUniforms)
    {
        string qualifier = Current.Text;
        _pos++; // consume the qualifier keyword

        // Tolerate a precision qualifier between the storage qualifier and the type
        // (e.g. `uniform highp float iTime;`); the preprocessor strips these to bare tokens, but be
        // robust if one survives as an identifier.
        while (Check(TokenKind.Identifier) && Current.Text is "highp" or "mediump" or "lowp")
        {
            _pos++;
        }

        if (!Check(TokenKind.Identifier))
        {
            throw Reject(
                $"Malformed top-level '{qualifier}' declaration.", start);
        }

        Token typeTok = Current;
        _pos++; // type name (validated below only when we need to keep the declaration)

        if (!Check(TokenKind.Identifier))
        {
            throw Reject(
                $"Top-level '{qualifier}' declarations are outside the supported subset " +
                "(ShaderToy uniforms are predefined and injected automatically).", start);
        }

        Token nameTok = Current;
        string name = nameTok.Text;

        if (UniformInfo.IsUniform(name))
        {
            // Redundant re-declaration of a built-in ShaderToy uniform: drop it. Consume the rest of
            // the declaration (optional `[N]` array suffix and the terminating ';') and add nothing.
            _pos++; // name
            ConsumeUniformTail(nameTok);
            return;
        }

        // Only `uniform` declares a host-drivable value; `varying`/`attribute`/`in`/`out` of a custom
        // name has no host contract and stays a loud reject.
        if (qualifier != "uniform")
        {
            throw Reject(
                $"Top-level '{qualifier}' declaration of '{name}' is outside the supported subset. " +
                "Only the predefined ShaderToy uniforms (iTime, iResolution, iMouse, iChannelN, ...) " +
                "and custom 'uniform' declarations are available.",
                nameTok);
        }

        // A common glslViewer alias whose type matches a ShaderToy built-in exactly: fold it onto the
        // built-in so it Just Works (u_time -> iTime). Only the zero-risk exact-type aliases are
        // mapped; everything else is exposed verbatim as a custom uniform.
        if (UniformAliases.TryResolve(name, typeTok.Text, out string? aliasOf))
        {
            _pos++; // name
            ConsumeUniformTail(nameTok);
            _aliases[name] = aliasOf!;
            return;
        }

        // A custom uniform: validate the type, then emit it as an effect parameter.
        bool isSampler = typeTok.Text == "sampler2D";
        if (!isSampler)
        {
            if (TypeTable.RejectedTypes.TryGetValue(typeTok.Text, out string? reason))
            {
                throw Reject(
                    $"Custom uniform '{name}' has an unsupported type: {reason}", typeTok);
            }

            if (!TypeTable.IsTypeName(typeTok.Text) || typeTok.Text == "void")
            {
                throw Reject(
                    $"Custom uniform '{name}' has an unsupported type '{typeTok.Text}'. Supported uniform " +
                    "types: bool/int/float, vecN/ivecN/bvecN, matN, and sampler2D.",
                    typeTok);
            }
        }

        _pos++; // name

        if (Check(TokenKind.LBracket))
        {
            throw Reject(
                $"Array uniform '{name}' is outside the supported subset (custom uniforms must be scalar, " +
                "vector, matrix, or sampler2D).",
                Current);
        }

        if (Check(TokenKind.Assign))
        {
            throw Reject(
                $"Custom uniform '{name}' has an initializer, which is not valid GLSL for a 'uniform' " +
                "(its value is host-supplied).",
                Current);
        }

        Expect(TokenKind.Semicolon, "';'");
        customUniforms.Add(new CustomUniformDecl
        {
            TypeName = typeTok.Text,
            Name = name,
            IsSampler = isSampler,
            Line = start.Line,
            Column = start.Column,
        });
    }

    /// <summary>Consume the tail of a built-in/alias uniform declaration: an optional <c>[N]</c>
    /// array suffix and the terminating <c>;</c>.</summary>
    private void ConsumeUniformTail(Token nameTok)
    {
        _ = nameTok;
        if (Check(TokenKind.LBracket))
        {
            while (!Check(TokenKind.RBracket) && !Check(TokenKind.EndOfFile))
            {
                _pos++;
            }

            Match(TokenKind.RBracket);
        }

        Expect(TokenKind.Semicolon, "';'");
    }

    private FunctionDecl ParseFunctionRest(string returnType, string name, Token start)
    {
        Expect(TokenKind.LParen, "'('");
        var parameters = new List<ParamDecl>();

        // `void` parameter list is empty.
        if (Check(TokenKind.Identifier) && Current.Text == "void" && Peek().Kind == TokenKind.RParen)
        {
            _pos++;
        }
        else if (!Check(TokenKind.RParen))
        {
            do
            {
                parameters.Add(ParseParam());
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.RParen, "')'");

        if (Check(TokenKind.Semicolon))
        {
            // A forward declaration / prototype: accept and skip (we only emit definitions).
            _pos++;
            return new FunctionDecl
            {
                ReturnType = returnType,
                Name = name,
                Parameters = parameters,
                Body = new BlockStmt { Statements = Array.Empty<Stmt>() },
                Line = start.Line,
                Column = start.Column,
            };
        }

        BlockStmt body = ParseBlock();
        return new FunctionDecl
        {
            ReturnType = returnType,
            Name = name,
            Parameters = parameters,
            Body = body,
            Line = start.Line,
            Column = start.Column,
        };
    }

    private ParamDecl ParseParam()
    {
        Token start = Current;
        ParamQualifier qual = ParamQualifier.In;

        while (Check(TokenKind.Identifier) && Current.Text is "in" or "out" or "inout" or "const")
        {
            switch (Current.Text)
            {
                case "out":
                    qual = ParamQualifier.Out;
                    break;
                case "inout":
                    qual = ParamQualifier.InOut;
                    break;
                // "in" and "const" leave the default (in).
            }

            _pos++;
        }

        string typeName = ExpectTypeName();
        SkipStrayDeclModifiers();
        Token nameTok = Expect(TokenKind.Identifier, "a parameter name");

        if (Check(TokenKind.LBracket))
        {
            throw Reject("Array parameters are outside the supported subset.", Current);
        }

        return new ParamDecl
        {
            TypeName = typeName,
            Name = nameTok.Text,
            Qualifier = qual,
            Line = start.Line,
            Column = start.Column,
        };
    }

    /// <summary>Consume a type-name token, validating it against the supported / rejected tables.</summary>
    private string ExpectTypeName()
    {
        if (!Check(TokenKind.Identifier))
        {
            throw Reject($"Expected a type name but found '{Current.Text}'.", Current);
        }

        string name = Current.Text;
        if (TypeTable.RejectedTypes.TryGetValue(name, out string? reason))
        {
            throw Reject(reason, Current);
        }

        if (!TypeTable.IsTypeName(name))
        {
            throw Reject(
                $"Unsupported or unknown type '{name}'. Supported: void/bool/int/float, " +
                "vecN/ivecN/bvecN, matN.", Current);
        }

        _pos++;
        return name;
    }

    // ── statements ──────────────────────────────────────────────────────────

    private BlockStmt ParseBlock()
    {
        Token start = Expect(TokenKind.LBrace, "'{'");
        var stmts = new List<Stmt>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
        {
            stmts.Add(ParseStatement());
        }

        Expect(TokenKind.RBrace, "'}'");
        return new BlockStmt { Statements = stmts, Line = start.Line, Column = start.Column };
    }

    private Stmt ParseStatement()
    {
        Token t = Current;

        if (Check(TokenKind.LBrace))
        {
            return ParseBlock();
        }

        if (Check(TokenKind.Semicolon))
        {
            _pos++;
            return new BlockStmt { Statements = Array.Empty<Stmt>(), Line = t.Line, Column = t.Column };
        }

        if (Check(TokenKind.Identifier))
        {
            switch (t.Text)
            {
                case "if": return ParseIf();
                case "for": return ParseFor();
                case "while": return ParseWhile();
                case "do": return ParseDoWhile();
                case "return": return ParseReturn();
                case "break":
                    _pos++;
                    Expect(TokenKind.Semicolon, "';'");
                    return new BreakStmt { Line = t.Line, Column = t.Column };
                case "continue":
                    _pos++;
                    Expect(TokenKind.Semicolon, "';'");
                    return new ContinueStmt { Line = t.Line, Column = t.Column };
                case "discard":
                    _pos++;
                    Expect(TokenKind.Semicolon, "';'");
                    return new DiscardStmt { Line = t.Line, Column = t.Column };
                case "switch":
                    throw Reject("'switch' is outside the supported subset.", t);
                case "struct":
                    throw Reject("Local 'struct' is outside the supported subset.", t);
            }

            // A local variable declaration starts with `const` or a type name.
            if (t.Text == "const" || IsTypeAt(_pos))
            {
                return ParseLocalVarDecl();
            }

            // A rejected type spelling at statement start (e.g. `double x;`) gets a tailored message
            // rather than a confusing "expected ';'" — but only when it actually looks like a decl
            // (type followed by an identifier), so we don't mis-handle a same-named local variable.
            if (TypeTable.RejectedTypes.TryGetValue(t.Text, out string? reason) &&
                Peek().Kind == TokenKind.Identifier)
            {
                throw Reject(reason, t);
            }
        }

        // Otherwise: an expression statement.
        Expr expr = ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        return new ExprStmt { Expression = expr, Line = t.Line, Column = t.Column };
    }

    private bool IsTypeAt(int index)
    {
        Token tok = _tokens[index];
        return tok.Kind == TokenKind.Identifier && TypeTable.IsTypeName(tok.Text);
    }

    private Stmt ParseLocalVarDecl()
    {
        Token start = Current;
        bool isConst = false;
        if (Check(TokenKind.Identifier) && Current.Text == "const")
        {
            isConst = true;
            _pos++;
        }

        string typeName = ExpectTypeName();

        // First declarator.
        VarDeclStmt first = ParseSingleDeclarator(typeName, isConst, start);

        // GLSL allows `float a = 1.0, b, c = 2.0;`. Model the comma list as a non-scoping
        // MultiDeclStmt so the declarators stay siblings in the enclosing block (a nested
        // BlockStmt would wrongly scope `a`/`b`/`c` to braces and break later references).
        if (Check(TokenKind.Comma))
        {
            var list = new List<VarDeclStmt> { first };
            while (Match(TokenKind.Comma))
            {
                list.Add(ParseSingleDeclarator(typeName, isConst, Current));
            }

            Expect(TokenKind.Semicolon, "';'");
            return new MultiDeclStmt { Declarators = list, Line = start.Line, Column = start.Column };
        }

        Expect(TokenKind.Semicolon, "';'");
        return first;
    }

    /// <summary>
    /// Consume any stray storage / precision modifier that appears AFTER the type spelling
    /// (B5: "modifiers must appear before type"). GLSL requires qualifiers before the type, and the
    /// preprocessor strips precision qualifiers, but some copied/generated declarations carry a
    /// qualifier in the wrong place (e.g. <c>vec4 const x</c> / <c>float mediump y</c>). Left in, the
    /// modifier would either be mis-parsed as the declared name or emitted after the type, which the
    /// stricter HLSL compilers (fxc / FNA) reject. Dropping it here yields a valid <c>type name</c>.
    /// </summary>
    private void SkipStrayDeclModifiers()
    {
        while (Check(TokenKind.Identifier) &&
               Current.Text is "const" or "in" or "out" or "inout" or "highp" or "mediump" or "lowp")
        {
            _pos++;
        }
    }

    private VarDeclStmt ParseSingleDeclarator(string typeName, bool isConst, Token start)
    {
        SkipStrayDeclModifiers();
        Token nameTok = Expect(TokenKind.Identifier, "a variable name");
        if (Check(TokenKind.LBracket))
        {
            throw Reject("Local array declarations are outside the supported subset.", Current);
        }

        Expr? init = null;
        if (Match(TokenKind.Assign))
        {
            init = ParseAssignment();
        }

        return new VarDeclStmt
        {
            TypeName = typeName,
            Name = nameTok.Text,
            Initializer = init,
            IsConst = isConst,
            Line = start.Line,
            Column = start.Column,
        };
    }

    private Stmt ParseIf()
    {
        Token start = Current;
        _pos++; // if
        Expect(TokenKind.LParen, "'('");
        Expr cond = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        Stmt then = ParseStatement();
        Stmt? els = null;
        if (Check(TokenKind.Identifier) && Current.Text == "else")
        {
            _pos++;
            els = ParseStatement();
        }

        return new IfStmt { Condition = cond, Then = then, Else = els, Line = start.Line, Column = start.Column };
    }

    private Stmt ParseFor()
    {
        Token start = Current;
        _pos++; // for
        Expect(TokenKind.LParen, "'('");

        Stmt? init;
        if (Check(TokenKind.Semicolon))
        {
            init = null;
            _pos++;
        }
        else if (Check(TokenKind.Identifier) && (Current.Text == "const" || IsTypeAt(_pos)))
        {
            init = ParseLocalVarDecl(); // consumes the ';'
        }
        else
        {
            Expr e = ParseExpression();
            Expect(TokenKind.Semicolon, "';'");
            init = new ExprStmt { Expression = e, Line = e.Line, Column = e.Column };
        }

        Expr? cond = null;
        if (!Check(TokenKind.Semicolon))
        {
            cond = ParseExpression();
        }

        Expect(TokenKind.Semicolon, "';'");

        Expr? inc = null;
        if (!Check(TokenKind.RParen))
        {
            inc = ParseExpression();
        }

        Expect(TokenKind.RParen, "')'");
        Stmt body = ParseStatement();
        return new ForStmt
        {
            Init = init,
            Condition = cond,
            Increment = inc,
            Body = body,
            Line = start.Line,
            Column = start.Column,
        };
    }

    private Stmt ParseWhile()
    {
        Token start = Current;
        _pos++; // while
        Expect(TokenKind.LParen, "'('");
        Expr cond = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        Stmt body = ParseStatement();
        return new WhileStmt { Condition = cond, Body = body, Line = start.Line, Column = start.Column };
    }

    private Stmt ParseDoWhile()
    {
        Token start = Current;
        _pos++; // do
        Stmt body = ParseStatement();
        if (!(Check(TokenKind.Identifier) && Current.Text == "while"))
        {
            throw Reject("Expected 'while' to close a 'do' loop.", Current);
        }

        _pos++; // while
        Expect(TokenKind.LParen, "'('");
        Expr cond = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        Expect(TokenKind.Semicolon, "';'");
        return new DoWhileStmt { Body = body, Condition = cond, Line = start.Line, Column = start.Column };
    }

    private Stmt ParseReturn()
    {
        Token start = Current;
        _pos++; // return
        Expr? value = null;
        if (!Check(TokenKind.Semicolon))
        {
            value = ParseExpression();
        }

        Expect(TokenKind.Semicolon, "';'");
        return new ReturnStmt { Value = value, Line = start.Line, Column = start.Column };
    }

    // ── expressions (precedence climbing) ────────────────────────────────────

    private Expr ParseExpression() => ParseAssignment();

    private Expr ParseAssignment()
    {
        Expr left = ParseConditional();

        if (Current.Kind is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign
            or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.PercentAssign)
        {
            Token opTok = Current;
            _pos++;
            Expr value = ParseAssignment(); // right associative
            return new AssignExpr
            {
                Op = opTok.Text,
                Target = left,
                Value = value,
                Line = left.Line,
                Column = left.Column,
            };
        }

        return left;
    }

    private Expr ParseConditional()
    {
        Expr cond = ParseBinary(0);
        if (Match(TokenKind.Question))
        {
            Expr t = ParseAssignment();
            Expect(TokenKind.Colon, "':'");
            Expr f = ParseAssignment();
            return new ConditionalExpr
            {
                Condition = cond,
                WhenTrue = t,
                WhenFalse = f,
                Line = cond.Line,
                Column = cond.Column,
            };
        }

        return cond;
    }

    // Binary precedence levels (lowest number = lowest precedence), C/GLSL-like.
    private static int Precedence(TokenKind kind) => kind switch
    {
        TokenKind.OrOr => 1,
        TokenKind.AndAnd => 2,
        TokenKind.Pipe => 3,
        TokenKind.Caret => 4,
        TokenKind.Amp => 5,
        TokenKind.EqualEqual or TokenKind.NotEqual => 6,
        TokenKind.Less or TokenKind.Greater or TokenKind.LessEqual or TokenKind.GreaterEqual => 7,
        TokenKind.Shl or TokenKind.Shr => 8,
        TokenKind.Plus or TokenKind.Minus => 9,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 10,
        _ => -1,
    };

    private Expr ParseBinary(int minPrec)
    {
        Expr left = ParseUnary();
        while (true)
        {
            int prec = Precedence(Current.Kind);
            if (prec < 0 || prec < minPrec)
            {
                break;
            }

            Token opTok = Current;
            _pos++;
            Expr right = ParseBinary(prec + 1); // left-associative
            left = new BinaryExpr
            {
                Op = opTok.Text,
                Left = left,
                Right = right,
                Line = left.Line,
                Column = left.Column,
            };
        }

        return left;
    }

    private Expr ParseUnary()
    {
        Token t = Current;
        if (t.Kind is TokenKind.Minus or TokenKind.Plus or TokenKind.Not
            or TokenKind.Increment or TokenKind.Decrement)
        {
            _pos++;
            Expr operand = ParseUnary();
            return new UnaryExpr
            {
                Op = t.Text,
                Operand = operand,
                IsPostfix = false,
                Line = t.Line,
                Column = t.Column,
            };
        }

        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        Expr expr = ParsePrimary();

        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                Token member = Expect(TokenKind.Identifier, "a swizzle / member name");
                expr = new SwizzleExpr
                {
                    Target = expr,
                    Member = member.Text,
                    Line = expr.Line,
                    Column = expr.Column,
                };
            }
            else if (Match(TokenKind.LBracket))
            {
                Expr index = ParseExpression();
                Expect(TokenKind.RBracket, "']'");
                expr = new IndexExpr
                {
                    Target = expr,
                    Index = index,
                    Line = expr.Line,
                    Column = expr.Column,
                };
            }
            else if (Current.Kind is TokenKind.Increment or TokenKind.Decrement)
            {
                Token op = Current;
                _pos++;
                expr = new UnaryExpr
                {
                    Op = op.Text,
                    Operand = expr,
                    IsPostfix = true,
                    Line = expr.Line,
                    Column = expr.Column,
                };
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expr ParsePrimary()
    {
        Token t = Current;

        switch (t.Kind)
        {
            case TokenKind.IntLiteral:
                _pos++;
                return new IntLiteralExpr { Text = t.Text, Line = t.Line, Column = t.Column };

            case TokenKind.FloatLiteral:
                _pos++;
                return new FloatLiteralExpr { Text = t.Text, Line = t.Line, Column = t.Column };

            case TokenKind.BoolLiteral:
                _pos++;
                return new BoolLiteralExpr { Value = t.Text == "true", Line = t.Line, Column = t.Column };

            case TokenKind.LParen:
                _pos++;
                Expr inner = ParseExpression();
                Expect(TokenKind.RParen, "')'");
                return inner;

            case TokenKind.Identifier:
                return ParseIdentifierExpr();

            default:
                throw Reject($"Unexpected token '{t.Text}' in an expression.", t);
        }
    }

    private Expr ParseIdentifierExpr()
    {
        Token t = Current;
        _pos++;

        // Reject double/uint literals dressed as identifiers in a call head is handled by type tables.
        if (Check(TokenKind.LParen))
        {
            _pos++;
            var args = new List<Expr>();
            if (!Check(TokenKind.RParen))
            {
                do
                {
                    args.Add(ParseAssignment());
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.RParen, "')'");
            return new CallExpr { Callee = t.Text, Args = args, Line = t.Line, Column = t.Column };
        }

        return new IdentifierExpr { Name = t.Text, Line = t.Line, Column = t.Column };
    }
}
