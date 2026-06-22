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

    /// <summary>Conventional screen-coordinate varying names (from ignored stage-I/O decls) that alias
    /// the harness normalized screen UV (see <see cref="ScreenCoordVaryings"/>).</summary>
    private readonly HashSet<string> _screenUvAliases = new(StringComparer.Ordinal);

    /// <summary>Names of user-defined structs declared so far, so they are recognized as type names
    /// (G6). GLSL requires a struct to be declared before use, so this is populated in source order
    /// and a forward reference naturally falls through to the "unknown type" reject.</summary>
    private readonly HashSet<string> _structNames = new(StringComparer.Ordinal);

    /// <summary>Compile-time integer constants in scope, name → value. Populated from
    /// <c>const int NAME = literal;</c> globals (and local <c>const int</c> declarations) in source
    /// order, so a later array size <c>[NAME]</c> resolves to the literal. A <c>#define</c>d size is
    /// already a literal by the time the parser runs (the preprocessor expands it), so this only needs
    /// to cover the const-int form. Populated as declarations are parsed; a forward reference naturally
    /// is not yet present and falls to the non-constant-size reject.</summary>
    private readonly Dictionary<string, int> _intConstants = new(StringComparer.Ordinal);

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
        var mutableGlobals = new List<GlobalVarDecl>();
        var structs = new List<StructDecl>();
        var fragmentOutputs = new List<FragmentOutputDecl>();

        while (!Check(TokenKind.EndOfFile))
        {
            ParseTopLevel(globals, functions, customUniforms, mutableGlobals, structs, fragmentOutputs);
        }

        return new TranslationUnit
        {
            Globals = globals,
            Functions = functions,
            CustomUniforms = customUniforms,
            MutableGlobals = mutableGlobals,
            Structs = structs,
            FragmentOutputs = fragmentOutputs,
            Aliases = _aliases,
            ScreenUvAliases = _screenUvAliases,
        };
    }

    private void ParseTopLevel(
        List<GlobalConstDecl> globals, List<FunctionDecl> functions, List<CustomUniformDecl> customUniforms,
        List<GlobalVarDecl> mutableGlobals, List<StructDecl> structs, List<FragmentOutputDecl> fragmentOutputs)
    {
        Token start = Current;

        // G2: a `layout(...)` qualifier prefix (e.g. `layout(location = 0) out vec4 X;`). The layout
        // qualifier is consumed; the declaration it prefixes is either a plain-GLSL fragment output
        // `out vec4 X;` OR an IGNORED stage input `layout(location=N) in <type> <name>;` (vertex-stage
        // leftover from a web/desktop export). Anything else after a layout qualifier is out of subset.
        if (Check(TokenKind.Identifier) && Current.Text == "layout")
        {
            ConsumeLayoutQualifier();
            if (Check(TokenKind.Identifier) && Current.Text is "in" or "varying" or "attribute")
            {
                // A stage input (`layout(location=N) in vec2 vUv;`): ignore the declaration (the harness
                // synthesizes its own vertex shader); record a conventional coordinate-varying name so a
                // reference resolves to the harness screen UV.
                HandleQualifiedTopLevelDecl(start, customUniforms);
                return;
            }

            if (!(Check(TokenKind.Identifier) && Current.Text == "out"))
            {
                throw Reject(
                    "Only a fragment-output 'layout(location=N) out vec4 <name>;' declaration or an " +
                    "ignored 'layout(location=N) in <type> <name>;' stage input is supported after a " +
                    "'layout(...)' qualifier.", Current);
            }

            fragmentOutputs.Add(ParseFragmentOutputDecl(start));
            return;
        }

        // G6: a top-level `struct Name { type member; ... };`. A struct declaration may be immediately
        // followed by a declarator (`struct Name { ... } g;`); that combined form is out of subset
        // (reject), but the plain declaration is accepted.
        if (Check(TokenKind.Identifier) && Current.Text == "struct")
        {
            structs.Add(ParseStruct(start));
            return;
        }

        // G2: a plain-GLSL fragment output `out vec4 <name>;` (GLSL ES 3.00 / desktop 330). This is a
        // user-declared fragment-output variable, NOT a custom uniform: consume it as a
        // FragmentOutputDecl (the harness uses its name as the synthesized PS's COLOR0 return). Only the
        // `out vec4 <name>;` shape is a fragment output; any other `out` declaration falls through to the
        // qualified-decl handler, which rejects it.
        if (Check(TokenKind.Identifier) && Current.Text == "out" &&
            Peek().Kind == TokenKind.Identifier && Peek().Text == "vec4" &&
            Peek(2).Kind == TokenKind.Identifier)
        {
            fragmentOutputs.Add(ParseFragmentOutputDecl(start));
            return;
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

        // G7: an array suffix `[N]` may appear AFTER the type, before the name, the GLSL-canonical
        // position (`const vec3[3] s = {...};`). Parse it here; a name-side `[N]` is parsed below.
        int? arraySize = null;
        if (Check(TokenKind.LBracket))
        {
            arraySize = ParseArraySuffix();
        }

        Token nameTok = Expect(TokenKind.Identifier, "an identifier");
        string name = nameTok.Text;

        // G7: an array suffix `[N]` may instead appear AFTER the name (`const float k[3] = ...;`). At
        // most one of the two positions carries the size.
        if (Check(TokenKind.LBracket))
        {
            if (arraySize is not null)
            {
                throw Reject(
                    "An array may carry a size on the type OR the name, not both.", Current);
            }

            arraySize = ParseArraySuffix();
        }

        if (Check(TokenKind.LParen))
        {
            if (isConst)
            {
                throw Reject("'const' cannot qualify a function.", typeTok);
            }

            if (arraySize is not null)
            {
                throw Reject("Array return types are outside the supported subset.", typeTok);
            }

            FunctionDecl fn = ParseFunctionRest(typeName, name, start);
            functions.Add(fn);
            return;
        }

        // A `const` global: one or more comma-separated declarators, each with a required initializer
        // (`const float PI = 3.14159, TAU = 2.*PI;`). A later declarator may reference an earlier one (TAU
        // uses PI) because each becomes its own GlobalConstDecl in source order. The initializer is parsed
        // at assignment precedence (NOT the comma operator) so the `,` separates declarators rather than
        // being swallowed into the first one's value. A const ARRAY (`const float k[3] = float[](...)`) is
        // supported only as a single declarator (its size bookkeeping does not combine with a comma list).
        if (isConst)
        {
            Expect(TokenKind.Assign, "'=' (a const global requires an initializer)");
            Expr cinit = arraySize is null ? ParseAssignment() : ParseInitializer();
            ValidateArrayInit(arraySize, cinit, nameTok);
            AddConstGlobal(globals, typeName, name, cinit, arraySize, start);

            while (arraySize is null && Match(TokenKind.Comma))
            {
                Token declNameTok = Expect(TokenKind.Identifier, "a const name");
                if (Check(TokenKind.LBracket))
                {
                    throw Reject(
                        "An array 'const' must be declared on its own, not in a comma-separated list.",
                        Current);
                }

                Expect(TokenKind.Assign, "'=' (a const declarator requires an initializer)");
                Expr declInit = ParseAssignment();
                AddConstGlobal(globals, typeName, declNameTok.Text, declInit, null, declNameTok);
            }

            Expect(TokenKind.Semicolon, "';'");
            return;
        }

        // A non-const top-level array global (`float k[3] = float[](...)` / `float k[3];`) is handled
        // by the mutable-global path below; pass the array size through.
        if (arraySize is not null)
        {
            Expr? arrInit = null;
            if (Match(TokenKind.Assign))
            {
                arrInit = ParseInitializer();
            }

            Expect(TokenKind.Semicolon, "';'");
            ValidateArrayInit(arraySize, arrInit, nameTok);
            mutableGlobals.Add(new GlobalVarDecl
            {
                TypeName = typeName,
                Name = name,
                Initializer = arrInit,
                ArraySize = arraySize,
                Line = start.Line,
                Column = start.Column,
            });
            return;
        }

        // G1: a top-level non-const mutable global of a supported type is accepted and emitted as an
        // HLSL `static <type> <name> [= <init>];` (GLSL fragment globals are per-invocation mutable
        // state, which HLSL `static` globals match). Multiple declarators (`float a, b = 1.0;`) each
        // become a GlobalVarDecl. The type was already validated by ExpectTypeName above, so an
        // unsupported-type global has already been rejected.
        ParseMutableGlobalRest(typeName, nameTok, start, mutableGlobals);
    }

    /// <summary>
    /// Parse a top-level <c>struct Name { type member; ... };</c> (G6). The leading <c>struct</c> token
    /// is the current token. Each member is a single supported-type field; an array member, a nested
    /// inline struct, or a member of an unknown/unsupported type is a loud, located reject. A combined
    /// <c>struct Name { ... } var;</c> declarator form is also rejected (declare the variable separately).
    /// </summary>
    private StructDecl ParseStruct(Token start)
    {
        _pos++; // consume 'struct'
        Token nameTok = Expect(TokenKind.Identifier, "a struct name");
        string name = nameTok.Text;

        if (TypeTable.IsTypeName(name) || _structNames.Contains(name))
        {
            throw Reject($"Struct name '{name}' collides with an existing type.", nameTok);
        }

        Expect(TokenKind.LBrace, "'{'");
        var members = new List<StructMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
        {
            Token mStart = Current;
            if (Check(TokenKind.Identifier) && Current.Text == "struct")
            {
                throw Reject("Nested / inline struct members are outside the supported subset.", mStart);
            }

            // A member type must be a built-in supported type or a previously-declared struct.
            string memberType = ExpectTypeName();

            // First member declarator, then any comma-separated siblings of the same type.
            do
            {
                Token memberName = Expect(TokenKind.Identifier, "a struct member name");

                // A fixed-size array member (`vec3 colors[4];` / `Sphere spheres[MAX];`). HLSL allows a
                // struct member array directly (`float3 colors[4];`), so we accept it; the size resolves
                // through the same const-int / literal path as any array suffix.
                int? memberArraySize = null;
                if (Check(TokenKind.LBracket))
                {
                    memberArraySize = ParseArraySuffix();
                }

                if (!seen.Add(memberName.Text))
                {
                    throw Reject($"Duplicate struct member '{memberName.Text}'.", memberName);
                }

                members.Add(new StructMember
                {
                    TypeName = memberType,
                    Name = memberName.Text,
                    ArraySize = memberArraySize,
                    Line = memberName.Line,
                    Column = memberName.Column,
                });
            }
            while (Match(TokenKind.Comma));

            Expect(TokenKind.Semicolon, "';'");
        }

        Expect(TokenKind.RBrace, "'}'");

        if (members.Count == 0)
        {
            throw Reject($"Struct '{name}' has no members.", nameTok);
        }

        // A trailing declarator (`struct Name { ... } g;`) is out of subset: only the bare declaration
        // is supported. Anything other than the closing ';' here is rejected.
        if (!Check(TokenKind.Semicolon))
        {
            throw Reject(
                "A combined 'struct Name { ... } variable;' declaration is outside the supported subset " +
                "(declare the struct and the variable separately).",
                Current);
        }

        Expect(TokenKind.Semicolon, "';'");
        _structNames.Add(name);
        return new StructDecl { Name = name, Members = members, Line = start.Line, Column = start.Column };
    }

    /// <summary>
    /// Parse a plain-GLSL fragment-output declaration <c>out vec4 &lt;name&gt;;</c> (G2). The current
    /// token is <c>out</c>. The type is required to be <c>vec4</c> (a fragment color); the name becomes
    /// the local the synthesized pixel shader returns as its <c>COLOR0</c>. A fragment output cannot
    /// carry an initializer (it is written by the shader's <c>main()</c>), so an <c>= ...</c> here is a
    /// loud reject.
    /// </summary>
    private FragmentOutputDecl ParseFragmentOutputDecl(Token start)
    {
        Expect(TokenKind.Identifier, "'out'"); // consume 'out' (already validated as 'out')
        Token typeTok = Current;
        if (!(Check(TokenKind.Identifier) && typeTok.Text == "vec4"))
        {
            throw Reject(
                "A plain-GLSL fragment output must be 'out vec4 <name>;' (only a vec4 color output is " +
                "supported).", typeTok);
        }

        _pos++; // consume 'vec4'
        Token nameTok = Expect(TokenKind.Identifier, "a fragment-output name");

        if (Check(TokenKind.Assign))
        {
            throw Reject(
                $"Fragment output '{nameTok.Text}' cannot have an initializer (it is written by main()).",
                Current);
        }

        Expect(TokenKind.Semicolon, "';'");
        return new FragmentOutputDecl
        {
            Name = nameTok.Text,
            Line = start.Line,
            Column = start.Column,
        };
    }

    /// <summary>
    /// Consume a <c>layout(...)</c> qualifier prefix (G2): the <c>layout</c> keyword followed by a
    /// balanced parenthesized list (e.g. <c>(location = 0)</c>). The contents are host-tooling layout
    /// metadata the converter does not need; only the declaration the qualifier prefixes matters.
    /// </summary>
    private void ConsumeLayoutQualifier()
    {
        _pos++; // consume 'layout'
        Expect(TokenKind.LParen, "'(' after 'layout'");
        int depth = 1;
        while (depth > 0 && !Check(TokenKind.EndOfFile))
        {
            if (Check(TokenKind.LParen))
            {
                depth++;
            }
            else if (Check(TokenKind.RParen))
            {
                depth--;
            }

            _pos++;
        }

        if (depth != 0)
        {
            throw Reject("Unterminated 'layout(...)' qualifier.", Current);
        }
    }

    /// <summary>
    /// Parse a fixed-size array suffix <c>[N]</c> at the current position and return N. The size must be
    /// a non-negative integer literal; an unsized <c>[]</c> (runtime / implicitly-sized array) is a loud
    /// reject (G7). The opening <c>[</c> is the current token.
    /// </summary>
    private int ParseArraySuffix()
    {
        Token open = Expect(TokenKind.LBracket, "'['");
        if (Check(TokenKind.RBracket))
        {
            throw Reject(
                "Unsized / runtime-sized arrays ('type name[]') are outside the supported subset " +
                "(only fixed-size arrays 'type name[N]' with a constant integer N are supported).",
                open);
        }

        // A constant integer expression: an integer literal, an identifier naming a compile-time int
        // constant (a `const int NAME = 16;` global / local, or a `#define`d size already expanded by the
        // preprocessor), or an arithmetic combination of those (`NUM_TRIANGLES * 3`, `N - 1`). Evaluating
        // it lets `Hitable h[MAX_HITABLES];`, `vec2 path[NUM];`, and `int idx[NUM_TRIANGLES * 3];` accept
        // a literal size. A genuinely runtime / non-constant size (an identifier with no known value)
        // stays a loud reject.
        Token startTok = Current;
        int size = ParseConstIntExpr(startTok);

        if (size <= 0)
        {
            throw Reject(
                $"Array size expression must evaluate to a positive integer (got {size}).", startTok);
        }

        Expect(TokenKind.RBracket, "']'");
        return size;
    }

    /// <summary>
    /// Evaluate a constant integer expression in an array-size suffix from the current token up to the
    /// closing <c>]</c>: integer literals, compile-time int constants (<c>const int</c> / <c>#define</c>),
    /// and the arithmetic operators <c>+ - * / %</c> with parentheses and unary <c>+</c>/<c>-</c>. A
    /// non-constant operand (an unknown identifier, a runtime value) is a loud reject — we never invent a
    /// size. Tokens are consumed up to (but not including) the <c>]</c>.
    /// </summary>
    private int ParseConstIntExpr(Token at) => ParseConstAddSub(at);

    private int ParseConstAddSub(Token at)
    {
        int value = ParseConstMulDiv(at);
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            bool add = Current.Kind == TokenKind.Plus;
            _pos++;
            int rhs = ParseConstMulDiv(at);
            value = add ? value + rhs : value - rhs;
        }

        return value;
    }

    private int ParseConstMulDiv(Token at)
    {
        int value = ParseConstUnary(at);
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            TokenKind op = Current.Kind;
            _pos++;
            int rhs = ParseConstUnary(at);
            value = op switch
            {
                TokenKind.Star => value * rhs,
                TokenKind.Slash => rhs == 0 ? throw Reject("Division by zero in array size.", at) : value / rhs,
                _ => rhs == 0 ? throw Reject("Modulo by zero in array size.", at) : value % rhs,
            };
        }

        return value;
    }

    private int ParseConstUnary(Token at)
    {
        if (Current.Kind == TokenKind.Minus)
        {
            _pos++;
            return -ParseConstUnary(at);
        }

        if (Current.Kind == TokenKind.Plus)
        {
            _pos++;
            return ParseConstUnary(at);
        }

        return ParseConstPrimary(at);
    }

    private int ParseConstPrimary(Token at)
    {
        if (Match(TokenKind.LParen))
        {
            int inner = ParseConstAddSub(at);
            Expect(TokenKind.RParen, "')'");
            return inner;
        }

        if (Check(TokenKind.IntLiteral))
        {
            Token tok = Current;
            _pos++;
            if (!int.TryParse(tok.Text, out int v))
            {
                throw Reject($"Array size literal '{tok.Text}' is not a valid integer.", tok);
            }

            return v;
        }

        if (Check(TokenKind.Identifier) && _intConstants.TryGetValue(Current.Text, out int constValue))
        {
            _pos++;
            return constValue;
        }

        throw Reject(
            "Array size must be a constant integer expression (a literal, a 'const int' / '#define'd " +
            "constant, or an arithmetic combination of those); a runtime / non-constant size is not " +
            "supported.",
            Current);
    }

    /// <summary>
    /// Parse a declaration initializer: either a GLSL brace initializer list <c>{ a, b, c }</c>
    /// (an aggregate initializer, used for arrays — GLSL ES 3.00+) or an ordinary assignment-level
    /// expression (which covers the <c>T[](...)</c> array-constructor form and every scalar/vector
    /// init). A brace list becomes a <see cref="BraceInitExpr"/> the emitter renders as <c>{ ... }</c>.
    /// </summary>
    private Expr ParseInitializer()
    {
        if (Check(TokenKind.LBrace))
        {
            Token open = Current;
            _pos++; // '{'
            var elements = new List<Expr>();
            if (!Check(TokenKind.RBrace))
            {
                do
                {
                    // Tolerate a trailing comma before '}' (GLSL allows it in an aggregate initializer).
                    if (Check(TokenKind.RBrace))
                    {
                        break;
                    }

                    elements.Add(ParseAssignment());
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.RBrace, "'}'");
            if (elements.Count == 0)
            {
                throw Reject("A brace initializer must have at least one element.", open);
            }

            return new BraceInitExpr { Elements = elements, Line = open.Line, Column = open.Column };
        }

        return ParseAssignment();
    }

    /// <summary>
    /// Validate the initializer of a declared array (<c>type name[N] = ...</c>, G7). When present the
    /// initializer must be a GLSL array constructor whose element count equals the declared size N; any
    /// other initializer shape, or a size mismatch, is a loud, located reject (so we never emit a brace
    /// list of the wrong length, which would be a downstream HLSL error).
    /// </summary>
    private void ValidateArrayInit(int? arraySize, Expr? init, Token at)
    {
        if (arraySize is null || init is null)
        {
            return;
        }

        int count;
        switch (init)
        {
            case ArrayConstructorExpr ctor:
                count = ctor.Elements.Count;
                break;
            case BraceInitExpr brace:
                count = brace.Elements.Count;
                break;
            default:
                throw Reject(
                    "An array initializer must be a GLSL array constructor 'type[](...)' or a brace " +
                    "initializer list '{ ... }'.", at);
        }

        if (count != arraySize.Value)
        {
            throw Reject(
                $"Array '{at.Text}' is declared size {arraySize.Value} but its initializer has " +
                $"{count} element(s).",
                at);
        }
    }

    /// <summary>
    /// Record a compile-time int constant (<c>const int NAME = &lt;literal&gt;;</c>) so a later array
    /// size <c>[NAME]</c> can resolve to its value. Only a bare non-negative integer literal initializer
    /// (optionally a parenthesized one) is recorded; anything else leaves the name unrecorded, so using
    /// it as an array size falls to the non-constant-size reject.
    /// </summary>
    private void RecordIntConstant(string name, Expr initializer)
    {
        Expr e = initializer;

        // A unary minus on a literal makes a negative constant; record it (an array size <= 0 is caught
        // later by ParseArraySuffix).
        bool negative = false;
        while (e is UnaryExpr { Op: "-" or "+", IsPostfix: false } un)
        {
            if (un.Op == "-")
            {
                negative = !negative;
            }

            e = un.Operand;
        }

        if (e is IntLiteralExpr lit && int.TryParse(lit.Text, out int value))
        {
            _intConstants[name] = negative ? -value : value;
        }
    }

    /// <summary>
    /// Parse the tail of a top-level mutable global declaration after the type and first name have been
    /// consumed: an optional <c>= initializer</c> and any comma-separated additional declarators, ending
    /// at the <c>;</c>. Each declarator is added as its own <see cref="GlobalVarDecl"/>.
    /// </summary>
    private void ParseMutableGlobalRest(
        string typeName, Token firstNameTok, Token start, List<GlobalVarDecl> mutableGlobals)
    {
        Expr? init = null;
        if (Match(TokenKind.Assign))
        {
            init = ParseAssignment();
        }

        mutableGlobals.Add(new GlobalVarDecl
        {
            TypeName = typeName,
            Name = firstNameTok.Text,
            Initializer = init,
            Line = start.Line,
            Column = start.Column,
        });

        while (Match(TokenKind.Comma))
        {
            Token nameTok = Expect(TokenKind.Identifier, "a variable name");
            if (Check(TokenKind.LBracket))
            {
                throw Reject("User-declared arrays are outside the supported subset.", Current);
            }

            Expr? declInit = null;
            if (Match(TokenKind.Assign))
            {
                declInit = ParseAssignment();
            }

            mutableGlobals.Add(new GlobalVarDecl
            {
                TypeName = typeName,
                Name = nameTok.Text,
                Initializer = declInit,
                Line = nameTok.Line,
                Column = nameTok.Column,
            });
        }

        Expect(TokenKind.Semicolon, "';'");
    }

    /// <summary>Add one <c>const</c>-global declarator. A scalar <c>const int NAME = &lt;literal&gt;;</c> is
    /// also recorded as a compile-time int constant so a later array size <c>[NAME]</c> resolves to it.</summary>
    private void AddConstGlobal(
        List<GlobalConstDecl> globals, string typeName, string name, Expr init, int? arraySize, Token at)
    {
        if (arraySize is null && typeName == "int")
        {
            RecordIntConstant(name, init);
        }

        globals.Add(new GlobalConstDecl
        {
            TypeName = typeName,
            Name = name,
            Initializer = init,
            ArraySize = arraySize,
            Line = at.Line,
            Column = at.Column,
        });
    }

    /// <summary>
    /// Handle a top-level <c>uniform</c>/<c>varying</c>/<c>attribute</c>/<c>in</c>/<c>out</c>
    /// declaration. The qualifier and base type apply to a comma-separated declarator list
    /// (<c>uniform float a, b, c;</c>); each declarator is classified independently:
    /// <list type="bullet">
    /// <item>A redundant re-declaration of a KNOWN ShaderToy built-in (e.g. <c>uniform float iTime;</c>,
    /// even with an initializer <c>uniform vec3 iResolution = vec3(1920,1080,1);</c> or a redundant
    /// <c>uniform sampler2D iChannel0;</c>) is silently DROPPED — the harness already injects that
    /// global, so the declaration (and any initializer) is irrelevant.</item>
    /// <item>A <c>uniform</c> of a SUPPORTED type (scalar/vector/matrix, or <c>sampler2D</c>) is
    /// ACCEPTED as a custom uniform: emitted as an HLSL effect parameter the consumer drives. A common
    /// glslViewer alias (<c>u_time</c>) is folded onto its ShaderToy built-in.</item>
    /// <item>Anything else (an unsupported uniform type, or a
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
        _pos++; // type name (validated per-declarator below only when we keep the declaration)

        // Each declarator in the comma list (`uniform float a, b = 1.0, c;`) is handled independently
        // against the shared qualifier + type.
        do
        {
            HandleQualifiedDeclarator(start, qualifier, typeTok, customUniforms);
        }
        while (Match(TokenKind.Comma));

        Expect(TokenKind.Semicolon, "';'");
    }

    /// <summary>
    /// Handle one declarator (name + optional <c>[N]</c> + optional initializer) of a qualified
    /// top-level declaration, against the shared <paramref name="qualifier"/> and
    /// <paramref name="typeTok"/>. The terminating <c>,</c>/<c>;</c> is NOT consumed here.
    /// </summary>
    private void HandleQualifiedDeclarator(
        Token start, string qualifier, Token typeTok, List<CustomUniformDecl> customUniforms)
    {
        if (!Check(TokenKind.Identifier))
        {
            throw Reject(
                $"Top-level '{qualifier}' declarations are outside the supported subset " +
                "(ShaderToy uniforms are predefined and injected automatically).", start);
        }

        Token nameTok = Current;
        string name = nameTok.Text;
        _pos++; // name

        if (UniformInfo.IsUniform(name))
        {
            // Redundant re-declaration of a built-in ShaderToy uniform: drop it (including any `[N]`
            // array suffix and any initializer — the harness injects the built-in, so the source's
            // declaration and its initializer value are irrelevant). Add nothing.
            ConsumeDeclaratorTail();
            return;
        }

        // A top-level `in`/`varying`/`attribute` declaration is web / desktop-export VERTEX-STAGE
        // leftover. The harness synthesizes its own fullscreen vertex shader, so we IGNORE the
        // declaration entirely (do not reject, do not emit it as a parameter). For the COMMON case where
        // the varying is a conventional fullscreen screen-coordinate name (texCoord/vUv/uv/…), record it
        // so a reference to it resolves to the harness normalized screen UV ([0,1]); a NON-coordinate /
        // unknown name is still ignored here, and if it is later referenced it becomes a loud
        // undeclared-identifier reject (we cannot invent its per-vertex value).
        if (qualifier is "in" or "varying" or "attribute")
        {
            if (ScreenCoordVaryings.IsScreenCoordName(name))
            {
                _screenUvAliases.Add(name);
            }

            ConsumeDeclaratorTail();
            return;
        }

        // A top-level `out` of a custom name has no host contract (the only supported `out` is the
        // plain-GLSL `out vec4 <name>;` fragment output, handled before this point) and stays a reject.
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
            ConsumeDeclaratorTail();
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

        if (Check(TokenKind.LBracket))
        {
            throw Reject(
                $"Array uniform '{name}' is outside the supported subset (custom uniforms must be scalar, " +
                "vector, matrix, or sampler2D).",
                Current);
        }

        // G4: a custom uniform may carry a default value (`uniform float x = 1.0;`, valid GLSL 1.20+).
        // The initializer becomes the HLSL parameter's default so the consumer gets that value unless
        // they override it. A sampler cannot have an initializer.
        Expr? initializer = null;
        if (Match(TokenKind.Assign))
        {
            if (isSampler)
            {
                throw Reject(
                    $"Sampler uniform '{name}' cannot have an initializer.", typeTok);
            }

            initializer = ParseAssignment();
        }

        customUniforms.Add(new CustomUniformDecl
        {
            TypeName = typeTok.Text,
            Name = name,
            IsSampler = isSampler,
            Initializer = initializer,
            Line = start.Line,
            Column = start.Column,
        });
    }

    /// <summary>Consume the tail of a dropped built-in/alias declarator: an optional <c>[N]</c> array
    /// suffix and an optional <c>= initializer</c> (the value is irrelevant for an injected built-in).
    /// Stops at the next <c>,</c>/<c>;</c>, which the caller consumes.</summary>
    private void ConsumeDeclaratorTail()
    {
        if (Check(TokenKind.LBracket))
        {
            while (!Check(TokenKind.RBracket) && !Check(TokenKind.EndOfFile))
            {
                _pos++;
            }

            Match(TokenKind.RBracket);
        }

        if (Match(TokenKind.Assign))
        {
            // Parse-and-discard the initializer so multi-token / call initializers
            // (`= vec3(1920, 1080, 1)`) are consumed correctly (commas inside are arg separators, not
            // declarator separators — ParseAssignment stops at the top-level ',').
            _ = ParseAssignment();
        }
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

        // G7c: an array parameter's size may appear AFTER the type, before the name
        // (`void f(inout float[9] k)`), the GLSL-canonical position. A size after the name
        // (`float k[9]`) is parsed below; at most one of the two forms appears.
        int? arraySize = null;
        if (Check(TokenKind.LBracket))
        {
            arraySize = ParseArraySuffix();
        }

        SkipStrayDeclModifiers();
        Token nameTok = Expect(TokenKind.Identifier, "a parameter name");

        // G7c: the array size may instead appear AFTER the name (`float k[9]`).
        if (Check(TokenKind.LBracket))
        {
            if (arraySize is not null)
            {
                throw Reject(
                    "An array parameter may carry a size on the type OR the name, not both.", Current);
            }

            arraySize = ParseArraySuffix();
        }

        return new ParamDecl
        {
            TypeName = typeName,
            Name = nameTok.Text,
            Qualifier = qual,
            ArraySize = arraySize,
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

        if (!TypeTable.IsTypeName(name) && !_structNames.Contains(name))
        {
            throw Reject(
                $"Unsupported or unknown type '{name}'. Supported: void/bool/int/float, " +
                "vecN/ivecN/bvecN, matN, and user-declared structs.", Current);
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
                    return ParseSwitch();
                case "struct":
                    throw Reject("Local 'struct' is outside the supported subset.", t);
            }

            // A local variable declaration starts with `const` or a type name. A type name
            // immediately followed by `(` is a constructor expression (e.g. a struct ctor `Ray(...)`
            // used as a statement), not a declaration, so fall through to the expression path.
            if (t.Text == "const" || (IsTypeAt(_pos) && Peek().Kind != TokenKind.LParen))
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
        return tok.Kind == TokenKind.Identifier &&
               (TypeTable.IsTypeName(tok.Text) || _structNames.Contains(tok.Text));
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

        // G7b: a type-side array suffix `[N]` (`vec2[4] c = {...};`) applies to every declarator in
        // the comma list. A name-side `[N]` is parsed per-declarator in ParseSingleDeclarator.
        int? typeArraySize = null;
        if (Check(TokenKind.LBracket))
        {
            typeArraySize = ParseArraySuffix();
        }

        // First declarator.
        VarDeclStmt first = ParseSingleDeclarator(typeName, isConst, start, typeArraySize);

        // GLSL allows `float a = 1.0, b, c = 2.0;`. Model the comma list as a non-scoping
        // MultiDeclStmt so the declarators stay siblings in the enclosing block (a nested
        // BlockStmt would wrongly scope `a`/`b`/`c` to braces and break later references).
        if (Check(TokenKind.Comma))
        {
            var list = new List<VarDeclStmt> { first };
            while (Match(TokenKind.Comma))
            {
                list.Add(ParseSingleDeclarator(typeName, isConst, Current, typeArraySize));
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

    private VarDeclStmt ParseSingleDeclarator(
        string typeName, bool isConst, Token start, int? typeArraySize = null)
    {
        SkipStrayDeclModifiers();
        Token nameTok = Expect(TokenKind.Identifier, "a variable name");

        // G7: a local fixed-size array. The size comes from the type side (`vec2[4] c`, passed in) OR
        // the name side (`float arr[4]`). At most one position carries it.
        int? arraySize = typeArraySize;
        if (Check(TokenKind.LBracket))
        {
            if (arraySize is not null)
            {
                throw Reject(
                    "An array may carry a size on the type OR the name, not both.", Current);
            }

            arraySize = ParseArraySuffix();
        }

        Expr? init = null;
        if (Match(TokenKind.Assign))
        {
            init = arraySize is null ? ParseAssignment() : ParseInitializer();
        }

        ValidateArrayInit(arraySize, init, nameTok);

        // Record a local `const int NAME = <literal>;` so a later local array size `[NAME]` resolves.
        if (isConst && arraySize is null && typeName == "int" && init is not null)
        {
            RecordIntConstant(nameTok.Text, init);
        }

        return new VarDeclStmt
        {
            TypeName = typeName,
            Name = nameTok.Text,
            Initializer = init,
            IsConst = isConst,
            ArraySize = arraySize,
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

    /// <summary>
    /// Parse a <c>switch (selector) { case K: stmts break; ... default: stmts }</c> statement into a
    /// <see cref="SwitchStmt"/> the emitter lowers to an if/else-if chain (portable to SM3 / FNA, which
    /// have no native <c>switch</c>). Multiple <c>case</c> labels stacked with no statements between them
    /// share the next body (<c>case 1: case 2: ...</c>). The terminating <c>break;</c> of an arm is
    /// consumed (not stored). A non-empty arm body that does NOT end in <c>break</c>/<c>return</c> is a
    /// true C fall-through, which is error-prone to lower faithfully, so it is a loud, located reject.
    /// </summary>
    private Stmt ParseSwitch()
    {
        Token start = Current;
        _pos++; // 'switch'
        Expect(TokenKind.LParen, "'(' after 'switch'");
        Expr selector = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        Expect(TokenKind.LBrace, "'{' to open the switch body");

        var cases = new List<SwitchCase>();
        bool seenDefault = false;

        while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
        {
            // Collect one or more stacked labels (`case K:` / `default:`) that share the next body.
            var labels = new List<Expr>();
            bool isDefault = false;
            Token labelStart = Current;

            while (Check(TokenKind.Identifier) && Current.Text is "case" or "default")
            {
                if (Current.Text == "default")
                {
                    if (seenDefault)
                    {
                        throw Reject("Duplicate 'default' label in 'switch'.", Current);
                    }

                    seenDefault = true;
                    isDefault = true;
                    _pos++; // 'default'
                    Expect(TokenKind.Colon, "':' after 'default'");
                }
                else
                {
                    _pos++; // 'case'
                    labels.Add(ParseConditional()); // a constant case label (no comma/assignment)
                    Expect(TokenKind.Colon, "':' after a 'case' label");
                }

                // If the very next token is another label, this is a stacked/shared label group: keep
                // collecting (an empty body between two labels means they share the following body).
                // Otherwise this label group is complete and its body follows.
                if (!(Check(TokenKind.Identifier) && Current.Text is "case" or "default"))
                {
                    break;
                }
            }

            if (labels.Count == 0 && !isDefault)
            {
                throw Reject(
                    "Expected a 'case' or 'default' label inside the 'switch' body.", labelStart);
            }

            // A group that stacks the `default` label together with one or more `case` labels sharing one
            // body is ambiguous to lower into an if/else chain (the default is the catch-all `else`, not a
            // value-matched arm). Keep it a loud reject rather than lower it wrong.
            if (isDefault && labels.Count > 0)
            {
                throw Reject(
                    "A 'switch' arm that stacks 'default' with 'case' labels on one body is outside the " +
                    "supported subset (give 'default' its own arm).", labelStart);
            }

            // Collect this arm's body statements up to the next label or the closing brace.
            var body = new List<Stmt>();
            while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile) &&
                   !(Check(TokenKind.Identifier) && Current.Text is "case" or "default"))
            {
                body.Add(ParseStatement());
            }

            // The arm must terminate with a `break;` (consumed, not stored) or a `return ...;` — anything
            // else with a non-empty body is real fall-through, which we do not lower. A trailing
            // `default:` arm without a break is fine (nothing falls through past it).
            bool endsClean = EndsCaseCleanly(body, out bool trailingBreakIndex);
            if (!endsClean)
            {
                throw Reject(
                    "'switch' fall-through (a non-empty 'case' body with no terminating 'break' or " +
                    "'return') is outside the supported subset: it cannot be lowered to an if/else chain " +
                    "without changing control flow. Add a 'break;' to each case.",
                    labelStart);
            }

            // Strip the trailing break from the stored body (the lowering is an if/else chain, so a
            // break would be illegal HLSL outside a loop). A `return` stays.
            if (trailingBreakIndex)
            {
                body.RemoveAt(body.Count - 1);
            }

            cases.Add(new SwitchCase
            {
                Labels = labels,
                IsDefault = isDefault,
                Body = body,
            });
        }

        Expect(TokenKind.RBrace, "'}' to close the switch body");

        return new SwitchStmt
        {
            Selector = selector,
            Cases = cases,
            Line = start.Line,
            Column = start.Column,
        };
    }

    /// <summary>
    /// Decide whether a <c>case</c> arm body terminates cleanly for if/else lowering: an EMPTY body is
    /// clean (a shared/stacked label, or an intentionally empty arm), a body whose last statement is a
    /// <c>break;</c> is clean (and <paramref name="hasTrailingBreak"/> is set so the caller strips it), and
    /// a body whose last statement is a <c>return ...;</c> is clean (the return exits the function, so no
    /// fall-through). Anything else with a non-empty body is a real fall-through and is NOT clean.
    /// </summary>
    private static bool EndsCaseCleanly(IReadOnlyList<Stmt> body, out bool hasTrailingBreak)
    {
        hasTrailingBreak = false;
        if (body.Count == 0)
        {
            return true;
        }

        Stmt last = body[^1];
        if (last is BreakStmt)
        {
            hasTrailingBreak = true;
            return true;
        }

        // A `return ...;` exits the function, so no fall-through; treat it as a clean terminator. (A
        // `discard;` is deliberately NOT treated as clean: in C the next case body would still run, so a
        // discard-terminated case with no break is true fall-through and stays a loud reject.)
        return last is ReturnStmt;
    }

    // ── expressions (precedence climbing) ────────────────────────────────────

    /// <summary>
    /// Parse a full expression, including the GLSL comma (sequence) operator <c>a, b, c</c> at the
    /// lowest precedence. The comma operator only applies at full-expression sites (statement,
    /// for-header parts, parenthesized expression, loop/branch conditions); argument lists and
    /// declarators call <see cref="ParseAssignment"/> directly so their commas stay separators.
    /// </summary>
    private Expr ParseExpression()
    {
        Expr first = ParseAssignment();
        if (!Check(TokenKind.Comma))
        {
            return first;
        }

        var items = new List<Expr> { first };
        while (Match(TokenKind.Comma))
        {
            items.Add(ParseAssignment());
        }

        return new SequenceExpr { Items = items, Line = first.Line, Column = first.Column };
    }

    private Expr ParseAssignment()
    {
        Expr left = ParseConditional();

        if (Current.Kind is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign
            or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.PercentAssign
            or TokenKind.AmpAssign or TokenKind.PipeAssign or TokenKind.CaretAssign
            or TokenKind.ShlAssign or TokenKind.ShrAssign)
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

        // G7: a GLSL array constructor `type[](a, b, c)` or `type[N](a, b, c)`. The head is a supported
        // type name followed by `[`. (A non-type identifier followed by `[` is an index expression,
        // handled by ParsePostfix, so only treat the type-name form as an array constructor here.)
        if (Check(TokenKind.LBracket) && (TypeTable.IsTypeName(t.Text) || _structNames.Contains(t.Text)))
        {
            return ParseArrayConstructorRest(t);
        }

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

    /// <summary>
    /// Parse the tail of a GLSL array constructor after the element-type token: <c>[N?](e0, e1, ...)</c>.
    /// The opening <c>[</c> is the current token. An unsized <c>[]</c> form is allowed here (the length
    /// is the element count). Produces an <see cref="ArrayConstructorExpr"/> the emitter renders as an
    /// HLSL brace initializer list.
    /// </summary>
    private Expr ParseArrayConstructorRest(Token typeTok)
    {
        Expect(TokenKind.LBracket, "'['");
        int? declaredSize = null;
        if (!Check(TokenKind.RBracket))
        {
            if (!Check(TokenKind.IntLiteral))
            {
                throw Reject("Array constructor size must be a constant integer literal.", Current);
            }

            Token sizeTok = Current;
            _pos++;
            if (!int.TryParse(sizeTok.Text, out int size) || size <= 0)
            {
                throw Reject($"Array constructor size '{sizeTok.Text}' must be a positive integer.", sizeTok);
            }

            declaredSize = size;
        }

        Expect(TokenKind.RBracket, "']'");
        Expect(TokenKind.LParen, "'(' (an array constructor needs an element list)");

        var elements = new List<Expr>();
        if (!Check(TokenKind.RParen))
        {
            do
            {
                elements.Add(ParseAssignment());
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.RParen, "')'");

        if (elements.Count == 0)
        {
            throw Reject("Array constructor must have at least one element.", typeTok);
        }

        if (declaredSize is { } n && n != elements.Count)
        {
            throw Reject(
                $"Array constructor declares size {n} but has {elements.Count} elements.", typeTok);
        }

        return new ArrayConstructorExpr
        {
            ElementTypeName = typeTok.Text,
            DeclaredSize = declaredSize,
            Elements = elements,
            Line = typeTok.Line,
            Column = typeTok.Column,
        };
    }
}
