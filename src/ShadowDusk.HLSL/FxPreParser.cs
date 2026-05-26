#nullable enable

using System.Text;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.Lexer;

namespace ShadowDusk.HLSL;

/// <summary>
/// FX9 pre-parser: strips technique, pass, and sampler_state blocks from .fx source
/// and extracts all FX9 metadata needed by the compilation pipeline.
/// DXC rejects these constructs, so they must be removed before invoking DXC.
/// Stripped output preserves original line numbers by replacing removed lines with blank lines.
/// </summary>
public sealed class FxPreParser
{
    // -------------------------------------------------------------------------
    // Known shader profiles
    // -------------------------------------------------------------------------

    // All profiles accepted at pre-parse time; unrecognized profiles will be
    // rejected by DXC later with a proper diagnostic. We store the raw string.
    private static readonly HashSet<string> KnownProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // VS profiles
        "vs_1_1",
        "vs_2_0", "vs_2_a", "vs_2_sw",
        "vs_3_0",
        "vs_4_0", "vs_4_1",
        "vs_5_0",
        "vs_6_0", "vs_6_1", "vs_6_2", "vs_6_3", "vs_6_4", "vs_6_5", "vs_6_6", "vs_6_7",
        // PS profiles
        "ps_1_1", "ps_1_2", "ps_1_3", "ps_1_4",
        "ps_2_0", "ps_2_a", "ps_2_b", "ps_2_sw",
        "ps_3_0",
        "ps_4_0", "ps_4_1",
        "ps_5_0",
        "ps_6_0", "ps_6_1", "ps_6_2", "ps_6_3", "ps_6_4", "ps_6_5", "ps_6_6", "ps_6_7",
    };

    /// <summary>Returns true when the given profile string (already lowercased) is a recognized shader profile.</summary>
    public static bool IsKnownProfile(string profile) => KnownProfiles.Contains(profile);

    // -------------------------------------------------------------------------
    // Sampler type keywords
    // -------------------------------------------------------------------------

    private static readonly HashSet<string> SamplerTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE",
        "sampler_state", "SamplerState", "Sampler2D", "Sampler3D", "SamplerCube",
    };

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------

    private readonly string _sourceFile;
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    // Tracks character positions of each token in the original source so we can
    // reconstruct stripped output by erasing spans.  We store the cumulative
    // character offset at which each token starts.
    private readonly int[] _tokenCharOffset;

    // The original source text, needed for verbatim copy in stripped output.
    private readonly string _source;

    // -------------------------------------------------------------------------
    // Constructor (private — callers use the static Parse entry point)
    // -------------------------------------------------------------------------

    private FxPreParser(string source, string sourceFile, IReadOnlyList<Token> tokens, int[] tokenCharOffset)
    {
        _source = source;
        _sourceFile = sourceFile;
        _tokens = tokens;
        _tokenCharOffset = tokenCharOffset;
        _pos = 0;
    }

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses an FX9 .fx source file, strips FX9-specific blocks, and extracts metadata.
    /// </summary>
    /// <param name="source">Full text of the .fx file.</param>
    /// <param name="sourceFile">Display name used in diagnostics (file path or virtual name).</param>
    public static Result<FxParseResult, FxParseError> Parse(string source, string sourceFile)
    {
        var lexer = new FxLexer(source, sourceFile);
        var tokens = lexer.Tokenize();
        var offsets = ComputeCharacterOffsets(source, tokens);
        var parser = new FxPreParser(source, sourceFile, tokens, offsets);
        return parser.ParseFile();
    }

    // -------------------------------------------------------------------------
    // Character-offset computation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the zero-based character offset in <paramref name="source"/> for each token
    /// by walking the source text and matching line/column coordinates.
    /// This is O(n) in source length and only called once per parse.
    /// </summary>
    private static int[] ComputeCharacterOffsets(string source, IReadOnlyList<Token> tokens)
    {
        var offsets = new int[tokens.Count];

        // Build a fast line-start table (one entry per logical line).
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\r')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n')
                {
                    // CRLF pair — consume both chars, next line starts after \n.
                    lineStarts.Add(i + 2);
                    i++; // skip the \n on the next iteration
                }
                else
                {
                    lineStarts.Add(i + 1);
                }
            }
            else if (source[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        for (int t = 0; t < tokens.Count; t++)
        {
            var tok = tokens[t];
            int lineIdx = tok.Line - 1;
            if (lineIdx >= 0 && lineIdx < lineStarts.Count)
                offsets[t] = lineStarts[lineIdx] + (tok.Column - 1);
            else
                offsets[t] = 0;
        }

        return offsets;
    }

    // -------------------------------------------------------------------------
    // Token stream helpers
    // -------------------------------------------------------------------------

    private Token Peek(int offset = 0)
    {
        int index = _pos + offset;
        if (index < 0) return _tokens[0];
        if (index >= _tokens.Count) return _tokens[^1]; // last is always EOF
        return _tokens[index];
    }

    private Token Consume()
    {
        var t = _tokens[_pos];
        if (_pos < _tokens.Count - 1)
            _pos++;
        return t;
    }

    private Result<Token, FxParseError> Expect(TokenKind kind)
    {
        var t = Peek();
        if (t.Kind != kind)
            return Fail<Token>(FxParseErrorCode.UnexpectedToken,
                $"Expected '{kind}' but found '{t.Text}' ({t.Kind})", t);
        return Result<Token, FxParseError>.Ok(Consume());
    }

    private bool PeekIsKeyword(string value, int offset = 0) =>
        Peek(offset) is { Kind: TokenKind.Identifier } t &&
        string.Equals(t.Text, value, StringComparison.OrdinalIgnoreCase);

    private FxParseError MakeError(FxParseErrorCode code, string message, Token at) =>
        new()
        {
            SourceFile = _sourceFile,
            Line = at.Line,
            Column = at.Column,
            Message = message,
            Code = code,
            Span = new SourceSpan(at.Line, at.Column, at.Line, at.Column + at.Text.Length),
        };

    private Result<T, FxParseError> Fail<T>(FxParseErrorCode code, string message, Token at) =>
        Result<T, FxParseError>.Fail(MakeError(code, message, at));

    private Result<T, FxParseError> Fail<T>(FxParseErrorCode code, string message) =>
        Fail<T>(code, message, Peek());

    // -------------------------------------------------------------------------
    // Top-level parse
    // -------------------------------------------------------------------------

    private Result<FxParseResult, FxParseError> ParseFile()
    {
        var techniques = new List<TechniqueInfo>();
        var samplers = new List<SamplerInfo>();
        var paramAnnotations = new List<ParameterAnnotation>();
        var techniqueNames = new HashSet<string>(StringComparer.Ordinal);

        // We'll build stripped output by copying verbatim character ranges from _source,
        // except for ranges we decide to erase (replaced with blank lines).
        // erased[i] = true means the character at _source[i] should be replaced by ' '.
        // We track erased ranges as (start, exclusiveEnd) intervals.
        var erasedRanges = new List<(int Start, int End)>();

        // Skip comments and preprocessor directives at the start of the token stream
        // (they are always included verbatim in stripped output).
        SkipNonCodeTokens();

        while (Peek().Kind != TokenKind.EOF)
        {
            var tok = Peek();

            // technique / technique11 — but NOT macro calls like TECHNIQUE(name, vs, ps).
            // A real technique declaration is followed by an identifier name (or annotation '<' or body '{'),
            // never by '(' which would indicate a preprocessor macro invocation.
            if (tok.Kind == TokenKind.Identifier &&
                (string.Equals(tok.Text, "technique", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tok.Text, "technique11", StringComparison.OrdinalIgnoreCase)))
            {
                int la = 1;
                while (Peek(la).Kind is TokenKind.LineComment or TokenKind.BlockComment)
                    la++;
                if (Peek(la).Kind == TokenKind.LParen)
                {
                    // Macro call (e.g. TECHNIQUE(Name, VS, PS)) — pass through verbatim.
                    Consume();
                    SkipNonCodeTokens();
                    continue;
                }

                int blockStart = _tokenCharOffset[_pos];
                var result = ParseTechnique();
                if (result.IsFailure)
                    return Result<FxParseResult, FxParseError>.Fail(result.Error);

                var tech = result.Value;

                if (!techniqueNames.Add(tech.Name))
                    return Fail<FxParseResult>(FxParseErrorCode.DuplicateTechniqueName,
                        $"Duplicate technique name '{tech.Name}'", tok);

                techniques.Add(tech);

                // Erase from blockStart up to (but not including) current position's char offset.
                int blockEnd = _pos < _tokens.Count ? _tokenCharOffset[_pos] : _source.Length;
                erasedRanges.Add((blockStart, blockEnd));

                SkipNonCodeTokens();
                continue;
            }

            // sampler declaration with "= sampler_state" initialiser.
            // Detection: current token is a sampler-type keyword AND
            //            next non-comment token at offset+1 is Identifier (name) AND
            //            next is '=' AND next is "sampler_state".
            if (tok.Kind == TokenKind.Identifier && SamplerTypeKeywords.Contains(tok.Text))
            {
                // Look ahead: name = sampler_state
                int lookAhead = 1;
                while (Peek(lookAhead).Kind is TokenKind.LineComment or TokenKind.BlockComment)
                    lookAhead++;

                var nameTok = Peek(lookAhead);
                if (nameTok.Kind == TokenKind.Identifier)
                {
                    int la2 = lookAhead + 1;
                    while (Peek(la2).Kind is TokenKind.LineComment or TokenKind.BlockComment)
                        la2++;

                    if (Peek(la2).Kind == TokenKind.Equals)
                    {
                        int la3 = la2 + 1;
                        while (Peek(la3).Kind is TokenKind.LineComment or TokenKind.BlockComment)
                            la3++;

                        if (PeekIsKeywordAt(la3, "sampler_state"))
                        {
                            int blockStart = _tokenCharOffset[_pos];
                            var result = ParseSamplerDecl();
                            if (result.IsFailure)
                                return Result<FxParseResult, FxParseError>.Fail(result.Error);

                            samplers.Add(result.Value);

                            int blockEnd = _pos < _tokens.Count ? _tokenCharOffset[_pos] : _source.Length;
                            erasedRanges.Add((blockStart, blockEnd));

                            SkipNonCodeTokens();
                            continue;
                        }
                    }
                }
            }

            // Annotation on a global parameter: Identifier < ... > pattern.
            // We look for: Identifier (possibly type) Identifier (name) < annotations > ;
            // This is heuristic: if we see Identifier followed eventually by < we try to
            // parse annotations, stripping just the < ... > range.
            if (tok.Kind == TokenKind.Identifier)
            {
                // Look ahead to see if there is a matching annotation: type name < ...
                int la = 1;
                while (Peek(la).Kind is TokenKind.LineComment or TokenKind.BlockComment)
                    la++;

                // Next might be another identifier (the variable name), then LAngle.
                var next = Peek(la);
                if (next.Kind == TokenKind.Identifier)
                {
                    int la2 = la + 1;
                    while (Peek(la2).Kind is TokenKind.LineComment or TokenKind.BlockComment)
                        la2++;

                    if (Peek(la2).Kind == TokenKind.LAngle)
                    {
                        // type name < ... > ; -- try annotation parse.
                        var typeTok = Consume(); // type
                        SkipNonCodeTokens();
                        var nameTok2 = Consume(); // name
                        SkipNonCodeTokens();

                        int annotStart = _tokenCharOffset[_pos]; // points to '<'
                        var annotResult = ParseAnnotationBlock();
                        if (annotResult.IsFailure)
                            return Result<FxParseResult, FxParseError>.Fail(annotResult.Error);

                        int annotEnd = _pos < _tokens.Count ? _tokenCharOffset[_pos] : _source.Length;
                        erasedRanges.Add((annotStart, annotEnd));

                        paramAnnotations.Add(new ParameterAnnotation
                        {
                            ParameterName = nameTok2.Text,
                            Entries = annotResult.Value,
                            Span = new SourceSpan(typeTok.Line, typeTok.Column,
                                nameTok2.Line, nameTok2.Column + nameTok2.Text.Length),
                        });

                        SkipNonCodeTokens();
                        continue;
                    }
                }
            }

            // Everything else: copy verbatim (advance past single token).
            Consume();
            SkipNonCodeTokens();
        }

        string strippedHlsl = BuildStrippedOutput(erasedRanges);

        return Result<FxParseResult, FxParseError>.Ok(new FxParseResult
        {
            StrippedHlsl = strippedHlsl,
            Techniques = techniques,
            Samplers = samplers,
            ParameterAnnotations = paramAnnotations,
        });
    }

    // -------------------------------------------------------------------------
    // Technique parsing
    // -------------------------------------------------------------------------

    private Result<TechniqueInfo, FxParseError> ParseTechnique()
    {
        var startTok = Peek();
        bool isEffect11 = string.Equals(startTok.Text, "technique11", StringComparison.OrdinalIgnoreCase);
        Consume(); // consume "technique"/"technique11"

        SkipNonCodeTokens();

        var nameTok = Peek();
        if (nameTok.Kind != TokenKind.Identifier)
            return Fail<TechniqueInfo>(FxParseErrorCode.UnexpectedToken,
                $"Expected technique name but found '{nameTok.Text}'", nameTok);
        string name = nameTok.Text;
        Consume();

        SkipNonCodeTokens();

        // Optional annotation block < ... >
        List<AnnotationEntry> annotations = new();
        if (Peek().Kind == TokenKind.LAngle)
        {
            var annotResult = ParseAnnotationBlock();
            if (annotResult.IsFailure)
                return Result<TechniqueInfo, FxParseError>.Fail(annotResult.Error);
            annotations = new List<AnnotationEntry>(annotResult.Value);
            SkipNonCodeTokens();
        }

        var lbrace = Expect(TokenKind.LBrace);
        if (lbrace.IsFailure)
            return Result<TechniqueInfo, FxParseError>.Fail(lbrace.Error);

        SkipNonCodeTokens();

        var passes = new List<PassInfo>();
        var passNames = new HashSet<string>(StringComparer.Ordinal);

        while (Peek().Kind != TokenKind.RBrace)
        {
            if (Peek().Kind == TokenKind.EOF)
                return Fail<TechniqueInfo>(FxParseErrorCode.UnexpectedEof,
                    $"Unexpected end-of-file inside technique '{name}'");

            if (!PeekIsKeyword("pass"))
                return Fail<TechniqueInfo>(FxParseErrorCode.UnexpectedToken,
                    $"Expected 'pass' but found '{Peek().Text}'", Peek());

            var passResult = ParsePass(passes.Count);
            if (passResult.IsFailure)
                return Result<TechniqueInfo, FxParseError>.Fail(passResult.Error);

            var pass = passResult.Value;
            if (!passNames.Add(pass.Name))
                return Fail<TechniqueInfo>(FxParseErrorCode.DuplicatePassName,
                    $"Duplicate pass name '{pass.Name}' in technique '{name}'", startTok);

            passes.Add(pass);
            SkipNonCodeTokens();
        }

        Consume(); // consume '}'

        return Result<TechniqueInfo, FxParseError>.Ok(new TechniqueInfo
        {
            Name = name,
            Span = new SourceSpan(startTok.Line, startTok.Column,
                Peek(-1).Line, Peek(-1).Column + 1),
            Passes = passes,
            Annotations = annotations,
            IsEffect11 = isEffect11,
        });
    }

    // -------------------------------------------------------------------------
    // Pass parsing
    // -------------------------------------------------------------------------

    private Result<PassInfo, FxParseError> ParsePass(int passIndex = 0)
    {
        var startTok = Peek();
        Consume(); // "pass"

        SkipNonCodeTokens();

        // Pass name is optional — anonymous passes (pass { ... }) are legal in FX9.
        string name;
        var nameTok = Peek();
        if (nameTok.Kind == TokenKind.Identifier)
        {
            name = nameTok.Text;
            Consume();
        }
        else if (nameTok.Kind == TokenKind.LBrace)
        {
            name = $"P{passIndex}";
        }
        else
        {
            return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                $"Expected pass name but found '{nameTok.Text}'", nameTok);
        }

        SkipNonCodeTokens();

        // Optional annotation block
        List<AnnotationEntry> annotations = new();
        if (Peek().Kind == TokenKind.LAngle)
        {
            var annotResult = ParseAnnotationBlock();
            if (annotResult.IsFailure)
                return Result<PassInfo, FxParseError>.Fail(annotResult.Error);
            annotations = new List<AnnotationEntry>(annotResult.Value);
            SkipNonCodeTokens();
        }

        var lbrace = Expect(TokenKind.LBrace);
        if (lbrace.IsFailure)
            return Result<PassInfo, FxParseError>.Fail(lbrace.Error);

        SkipNonCodeTokens();

        string? vertexEntry = null, pixelEntry = null;
        string? vertexProfile = null, pixelProfile = null;
        var renderStates = new List<RenderStateEntry>();

        while (Peek().Kind != TokenKind.RBrace)
        {
            if (Peek().Kind == TokenKind.EOF)
                return Fail<PassInfo>(FxParseErrorCode.UnexpectedEof,
                    $"Unexpected end-of-file inside pass '{name}'");

            var keyTok = Peek();
            if (keyTok.Kind != TokenKind.Identifier)
                return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                    $"Expected render-state key but found '{keyTok.Text}'", keyTok);

            string key = keyTok.Text;
            Consume();
            SkipNonCodeTokens();

            var eq = Expect(TokenKind.Equals);
            if (eq.IsFailure)
                return Result<PassInfo, FxParseError>.Fail(eq.Error);
            SkipNonCodeTokens();

            bool isVs = string.Equals(key, "VertexShader", StringComparison.OrdinalIgnoreCase);
            bool isPs = string.Equals(key, "PixelShader", StringComparison.OrdinalIgnoreCase);

            if (isVs || isPs)
            {
                // compile <profile> <entrypoint>( )
                if (!PeekIsKeyword("compile"))
                    return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                        $"Expected 'compile' keyword after '{key} =' but found '{Peek().Text}'", Peek());
                Consume(); // "compile"
                SkipNonCodeTokens();

                var profileTok = Peek();
                if (profileTok.Kind != TokenKind.Identifier)
                    return Fail<PassInfo>(FxParseErrorCode.MalformedCompileExpression,
                        $"Expected shader profile after 'compile' but found '{profileTok.Text}'", profileTok);

                string profile = profileTok.Text.ToLowerInvariant();
                Consume();
                SkipNonCodeTokens();

                var entryTok = Peek();
                if (entryTok.Kind != TokenKind.Identifier)
                    return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                        $"Expected shader entry point but found '{entryTok.Text}'", entryTok);

                string entry = entryTok.Text;
                Consume();
                SkipNonCodeTokens();

                // Expect '(' and ')' with nothing between them.
                var lp = Expect(TokenKind.LParen);
                if (lp.IsFailure)
                    return Result<PassInfo, FxParseError>.Fail(lp.Error);
                SkipNonCodeTokens();

                if (Peek().Kind != TokenKind.RParen)
                    return Fail<PassInfo>(FxParseErrorCode.MalformedCompileExpression,
                        $"Unexpected tokens inside compile() argument list for '{key}'", Peek());

                Consume(); // ')'
                SkipNonCodeTokens();

                var semi = Expect(TokenKind.Semicolon);
                if (semi.IsFailure)
                    return Result<PassInfo, FxParseError>.Fail(semi.Error);

                if (isVs) { vertexEntry = entry; vertexProfile = profile; }
                else { pixelEntry = entry; pixelProfile = profile; }
            }
            else
            {
                // Generic render state: Key = Value ;
                var valueTok = Peek();
                if (valueTok.Kind is not (TokenKind.Identifier or TokenKind.Number))
                    return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                        $"Expected render-state value but found '{valueTok.Text}'", valueTok);

                string value = valueTok.Text;
                var valueSpan = new SourceSpan(keyTok.Line, keyTok.Column,
                    valueTok.Line, valueTok.Column + valueTok.Text.Length);
                Consume();
                SkipNonCodeTokens();

                if (Peek().Kind != TokenKind.Semicolon)
                    return Fail<PassInfo>(FxParseErrorCode.MissingSemicolon,
                        $"Expected ';' after render-state '{key} = {value}'", Peek());
                Consume(); // ';'

                renderStates.Add(new RenderStateEntry(key, value, valueSpan));
            }

            SkipNonCodeTokens();
        }

        Consume(); // '}'

        return Result<PassInfo, FxParseError>.Ok(new PassInfo
        {
            Name = name,
            Span = new SourceSpan(startTok.Line, startTok.Column, Peek().Line, Peek().Column),
            VertexEntryPoint = vertexEntry,
            PixelEntryPoint = pixelEntry,
            VertexProfile = vertexProfile,
            PixelProfile = pixelProfile,
            RenderStates = renderStates,
            Annotations = annotations,
        });
    }

    // -------------------------------------------------------------------------
    // Sampler declaration parsing
    // -------------------------------------------------------------------------

    private Result<SamplerInfo, FxParseError> ParseSamplerDecl()
    {
        var startTok = Peek();
        string samplerType = startTok.Text;
        Consume(); // sampler type keyword

        SkipNonCodeTokens();

        var nameTok = Peek();
        if (nameTok.Kind != TokenKind.Identifier)
            return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                $"Expected sampler name but found '{nameTok.Text}'", nameTok);
        string name = nameTok.Text;
        Consume();

        SkipNonCodeTokens();

        var eq = Expect(TokenKind.Equals);
        if (eq.IsFailure)
            return Result<SamplerInfo, FxParseError>.Fail(eq.Error);
        SkipNonCodeTokens();

        if (!PeekIsKeyword("sampler_state"))
            return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                $"Expected 'sampler_state' but found '{Peek().Text}'", Peek());
        Consume(); // "sampler_state"
        SkipNonCodeTokens();

        var lbrace = Expect(TokenKind.LBrace);
        if (lbrace.IsFailure)
            return Result<SamplerInfo, FxParseError>.Fail(lbrace.Error);
        SkipNonCodeTokens();

        string? textureRef = null;
        var stateEntries = new List<SamplerStateEntry>();

        while (Peek().Kind != TokenKind.RBrace)
        {
            if (Peek().Kind == TokenKind.EOF)
                return Fail<SamplerInfo>(FxParseErrorCode.UnclosedSamplerBlock,
                    $"Unexpected end-of-file inside sampler '{name}' — unclosed sampler_state block");

            var keyTok = Peek();
            if (keyTok.Kind != TokenKind.Identifier)
                return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                    $"Expected sampler state key but found '{keyTok.Text}'", keyTok);

            string key = keyTok.Text;
            Consume();
            SkipNonCodeTokens();

            var entryEq = Expect(TokenKind.Equals);
            if (entryEq.IsFailure)
                return Result<SamplerInfo, FxParseError>.Fail(entryEq.Error);
            SkipNonCodeTokens();

            if (string.Equals(key, "Texture", StringComparison.OrdinalIgnoreCase))
            {
                // Texture = <TexName>; OR Texture = TexName;
                if (Peek().Kind == TokenKind.LAngle)
                {
                    Consume(); // '<'
                    SkipNonCodeTokens();
                    var texTok = Peek();
                    if (texTok.Kind != TokenKind.Identifier)
                        return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                            $"Expected texture name inside '<>' but found '{texTok.Text}'", texTok);
                    textureRef = texTok.Text;
                    Consume();
                    SkipNonCodeTokens();
                    var ra = Expect(TokenKind.RAngle);
                    if (ra.IsFailure)
                        return Result<SamplerInfo, FxParseError>.Fail(ra.Error);
                }
                else
                {
                    var texTok = Peek();
                    if (texTok.Kind != TokenKind.Identifier)
                        return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                            $"Expected texture name but found '{texTok.Text}'", texTok);
                    textureRef = texTok.Text;
                    Consume();
                }
            }
            else
            {
                var valTok = Peek();
                if (valTok.Kind is not (TokenKind.Identifier or TokenKind.Number))
                    return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                        $"Expected sampler state value but found '{valTok.Text}'", valTok);

                var span = new SourceSpan(keyTok.Line, keyTok.Column,
                    valTok.Line, valTok.Column + valTok.Text.Length);
                stateEntries.Add(new SamplerStateEntry(key, valTok.Text, span));
                Consume();
            }

            SkipNonCodeTokens();

            if (Peek().Kind != TokenKind.Semicolon)
                return Fail<SamplerInfo>(FxParseErrorCode.MissingSemicolon,
                    $"Expected ';' after sampler state entry '{key}'", Peek());
            Consume(); // ';'
            SkipNonCodeTokens();
        }

        var rbrace = Expect(TokenKind.RBrace);
        if (rbrace.IsFailure)
            return Result<SamplerInfo, FxParseError>.Fail(rbrace.Error);
        SkipNonCodeTokens();

        // sampler declarations require a trailing ';'
        var trailSemi = Expect(TokenKind.Semicolon);
        if (trailSemi.IsFailure)
            return Result<SamplerInfo, FxParseError>.Fail(trailSemi.Error);

        return Result<SamplerInfo, FxParseError>.Ok(new SamplerInfo
        {
            Name = name,
            SamplerType = samplerType,
            TextureReference = textureRef,
            StateEntries = stateEntries,
            Span = new SourceSpan(startTok.Line, startTok.Column,
                Peek().Line, Peek().Column),
        });
    }

    // -------------------------------------------------------------------------
    // Annotation block parsing
    // -------------------------------------------------------------------------

    private Result<List<AnnotationEntry>, FxParseError> ParseAnnotationBlock()
    {
        var openTok = Peek();
        var la = Expect(TokenKind.LAngle);
        if (la.IsFailure)
            return Result<List<AnnotationEntry>, FxParseError>.Fail(la.Error);
        SkipNonCodeTokens();

        var entries = new List<AnnotationEntry>();

        while (Peek().Kind != TokenKind.RAngle)
        {
            if (Peek().Kind == TokenKind.EOF)
                return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnclosedAnnotationBlock,
                    "Unexpected end-of-file inside annotation block — unclosed '<'", openTok);

            var typeTok = Peek();
            if (typeTok.Kind != TokenKind.Identifier)
                return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnexpectedToken,
                    $"Expected annotation type but found '{typeTok.Text}'", typeTok);
            string type = typeTok.Text;
            Consume();
            SkipNonCodeTokens();

            var entryNameTok = Peek();
            if (entryNameTok.Kind != TokenKind.Identifier)
                return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnexpectedToken,
                    $"Expected annotation name but found '{entryNameTok.Text}'", entryNameTok);
            string entryName = entryNameTok.Text;
            Consume();
            SkipNonCodeTokens();

            var annotEq = Expect(TokenKind.Equals);
            if (annotEq.IsFailure)
                return Result<List<AnnotationEntry>, FxParseError>.Fail(annotEq.Error);
            SkipNonCodeTokens();

            var valueTok = Peek();
            if (valueTok.Kind is not (TokenKind.StringLiteral or TokenKind.Number or TokenKind.Identifier))
                return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnexpectedToken,
                    $"Expected annotation value but found '{valueTok.Text}'", valueTok);

            string value = valueTok.Text;
            var entrySpan = new SourceSpan(typeTok.Line, typeTok.Column,
                valueTok.Line, valueTok.Column + valueTok.Text.Length);
            Consume();
            SkipNonCodeTokens();

            var semi = Expect(TokenKind.Semicolon);
            if (semi.IsFailure)
            {
                // If we've hit the technique/pass body opener or EOF, the annotation was never closed.
                if (Peek().Kind is TokenKind.LBrace or TokenKind.EOF)
                    return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnclosedAnnotationBlock,
                        "Annotation block missing closing '>'", openTok);
                return Result<List<AnnotationEntry>, FxParseError>.Fail(semi.Error);
            }
            SkipNonCodeTokens();

            entries.Add(new AnnotationEntry(type, entryName, value, entrySpan));
        }

        Consume(); // '>'
        return Result<List<AnnotationEntry>, FxParseError>.Ok(entries);
    }

    // -------------------------------------------------------------------------
    // Stripped output construction
    // -------------------------------------------------------------------------

    private string BuildStrippedOutput(List<(int Start, int End)> erasedRanges)
    {
        if (erasedRanges.Count == 0)
            return _source;

        // Sort and merge overlapping ranges.
        erasedRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(int Start, int End)>();
        foreach (var range in erasedRanges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else
                merged.Add(range);
        }

        var sb = new StringBuilder(_source.Length);
        int cursor = 0;

        foreach (var (start, end) in merged)
        {
            // Copy verbatim up to the erased range.
            if (cursor < start)
                sb.Append(_source, cursor, start - cursor);

            // Replace erased range with blank lines to preserve line numbers.
            // Only newlines are preserved; everything else becomes spaces.
            for (int i = start; i < end && i < _source.Length; i++)
            {
                char c = _source[i];
                if (c == '\n')
                    sb.Append('\n');
                else if (c == '\r')
                {
                    sb.Append('\r');
                    // If next is \n it will be handled on the next iteration.
                }
                else
                    sb.Append(' ');
            }

            cursor = end;
        }

        // Copy any remaining source after the last erased range.
        if (cursor < _source.Length)
            sb.Append(_source, cursor, _source.Length - cursor);

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private void SkipNonCodeTokens()
    {
        while (Peek().Kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor)
            Consume();
    }

    private bool PeekIsKeywordAt(int offset, string keyword)
    {
        var t = Peek(offset);
        return t.Kind == TokenKind.Identifier &&
               string.Equals(t.Text, keyword, StringComparison.OrdinalIgnoreCase);
    }
}
