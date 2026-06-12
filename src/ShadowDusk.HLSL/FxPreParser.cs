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
/// <see cref="FxSourceMode"/> selects how legacy D3D9 constructs in the shader body are
/// treated: rewritten forward to SM4 for DXC (the default), or preserved verbatim for an
/// SM1–3 backend (the FNA fx_2_0 target, compiled by vkd3d).
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
    // Legacy sampling intrinsics
    // -------------------------------------------------------------------------

    // SM 1.x–3.x sampling intrinsics that DXC 6.x dropped and that ShadowDusk
    // rewrites to the modern '<texture>.<Method>(<sampler>, …)' form. Only the
    // intrinsics whose argument lists align ONE-TO-ONE with the corresponding
    // Texture2D method are handled, so the rewrite is a clean identifier swap:
    //   tex2D(s, uv)               → T.Sample(s, uv)
    //   tex2Dgrad(s, uv, ddx, ddy) → T.SampleGrad(s, uv, ddx, ddy)
    // Matched case-sensitively because HLSL intrinsic names are case-sensitive.
    private static readonly Dictionary<string, string> LegacySampleIntrinsics = new(StringComparer.Ordinal)
    {
        ["tex2D"]     = "Sample",
        ["tex2Dgrad"] = "SampleGrad",
    };

    // Legacy sampling intrinsics whose arguments do NOT map 1:1 onto a modern
    // Texture method (tex2Dlod packs the LOD into coord.w, tex2Dproj divides by
    // coord.w, the bias forms pack the bias into coord.w; the 1D/3D/CUBE families
    // additionally need a non-Texture2D resource the sampler rewrite does not
    // synthesize). Rewriting them is NOT mechanical with the span-substitution
    // machinery here, so they fail loudly with a targeted FX0012 diagnostic instead
    // of dying later inside DXC with a misleading 'unknown identifier' error.
    // The FNA target (PreserveSm3) compiles all of these natively via vkd3d.
    private static readonly HashSet<string> UnsupportedLegacyIntrinsics = new(StringComparer.Ordinal)
    {
        "tex1D", "tex1Dbias", "tex1Dgrad", "tex1Dlod", "tex1Dproj",
        "tex2Dbias", "tex2Dlod", "tex2Dproj",
        "tex3D", "tex3Dbias", "tex3Dgrad", "tex3Dlod", "tex3Dproj",
        "texCUBE", "texCUBEbias", "texCUBEgrad", "texCUBElod", "texCUBEproj",
    };

    // -------------------------------------------------------------------------
    // Legacy effect-framework texture object types
    // -------------------------------------------------------------------------

    // The FX9 'texture' object type (and its dimensioned variants) declares a
    // texture resource in effect syntax (e.g. 'texture _dissolveTex;'). DXC
    // rejects these under -Weffects-syntax, so ShadowDusk rewrites the type
    // keyword to the modern Resource type it maps to (Texture2D, Texture3D, …).
    // This is the sibling of the sampler_state (gap #2) and tex2D (gap #4)
    // rewrites: a 'sampler S = sampler_state { Texture = <T>; }' form binds 'S'
    // to the texture 'T', which only exists as a modern resource once this
    // rewrite fires. Matched CASE-SENSITIVELY so the modern types 'Texture2D',
    // 'Texture3D', 'TextureCube', … (capital 'T', dimension suffix) are never
    // touched — only the legacy lowercase forms (and bare capital 'Texture')
    // are rewritten.
    private static readonly Dictionary<string, string> LegacyTextureTypeKeywords = new(StringComparer.Ordinal)
    {
        ["texture"]     = "Texture2D",
        ["Texture"]     = "Texture2D",
        ["texture1D"]   = "Texture1D",
        ["texture2D"]   = "Texture2D",
        ["texture3D"]   = "Texture3D",
        ["textureCUBE"] = "TextureCube",
    };

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------

    private readonly string _sourceFile;
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    // How legacy D3D9/SM3 constructs in the shader body are treated. RewriteToSm4
    // (the default) rewrites them forward for DXC; PreserveSm3 (the FNA fx_2_0
    // target) passes them through verbatim because vkd3d's D3D_BYTECODE profile
    // accepts them natively. Technique/pass and parameter-annotation stripping is
    // identical in both modes.
    private readonly FxSourceMode _mode;

    // Tracks character positions of each token in the original source so we can
    // reconstruct stripped output by erasing spans.  We store the cumulative
    // character offset at which each token starts.
    private readonly int[] _tokenCharOffset;

    // The original source text, needed for verbatim copy in stripped output.
    private readonly string _source;

    // Names of samplers that appear as the first argument of a legacy sampling
    // intrinsic (tex2D / tex2Dgrad). Populated by a pre-scan before the main loop.
    // A sampler declaration is only rewritten into the modern Texture2D +
    // SamplerState form when its name is in this set — declarations that no
    // legacy intrinsic references keep their existing handling (Form 1 erased,
    // bare passed through verbatim) so already-modern shaders are untouched.
    private IReadOnlySet<string> _legacyIntrinsicSamplers = new HashSet<string>(StringComparer.Ordinal);

    // Maps a rewritten sampler name to the Texture2D it should sample through in
    // the rewritten '<texture>.Sample(<sampler>, uv)' call. Populated as sampler
    // declarations are processed in the main loop; read when a tex2D call is
    // rewritten. Valid HLSL declares samplers before use, so a sampler is always
    // in this map by the time its tex2D call is reached.
    private readonly Dictionary<string, string> _samplerTextureBindings = new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // Constructor (private — callers use the static Parse entry point)
    // -------------------------------------------------------------------------

    private FxPreParser(string source, string sourceFile, IReadOnlyList<Token> tokens, int[] tokenCharOffset, FxSourceMode mode)
    {
        _source = source;
        _sourceFile = sourceFile;
        _tokens = tokens;
        _tokenCharOffset = tokenCharOffset;
        _mode = mode;
        _pos = 0;
    }

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses an FX9 .fx source file, strips FX9-specific blocks, and extracts metadata,
    /// rewriting legacy D3D9 constructs forward to SM4 (<see cref="FxSourceMode.RewriteToSm4"/>).
    /// </summary>
    /// <param name="source">Full text of the .fx file.</param>
    /// <param name="sourceFile">Display name used in diagnostics (file path or virtual name).</param>
    public static Result<FxParseResult, FxParseError> Parse(string source, string sourceFile) =>
        Parse(source, sourceFile, FxSourceMode.RewriteToSm4);

    /// <summary>
    /// Parses an FX9 .fx source file, strips FX9-specific blocks, and extracts metadata.
    /// </summary>
    /// <param name="source">Full text of the .fx file.</param>
    /// <param name="sourceFile">Display name used in diagnostics (file path or virtual name).</param>
    /// <param name="mode">How legacy D3D9/SM3 constructs in the shader body are treated
    /// (rewritten forward for DXC, or preserved verbatim for an SM1–3 backend).</param>
    public static Result<FxParseResult, FxParseError> Parse(string source, string sourceFile, FxSourceMode mode)
    {
        var lexer = new FxLexer(source, sourceFile);
        var tokens = lexer.Tokenize();
        var offsets = ComputeCharacterOffsets(source, tokens);
        var parser = new FxPreParser(source, sourceFile, tokens, offsets, mode);
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

        // Token spans we want to substitute (rather than erase). Used to rewrite
        // the legacy SM 3.0 return semantic ': COLOR<n>?' to ': SV_Target<n>?',
        // legacy sampler declarations to 'SamplerState' (+ a synthesized
        // 'Texture2D' for bare samplers), and 'tex2D(s, uv)' to 's' sampler's
        // texture '.Sample(s, uv)'.
        var replacedRanges = new List<(int Start, int End, string Replacement)>();

        // Pre-scan: discover which samplers are sampled through a legacy
        // intrinsic so the main loop knows which declarations to rewrite.
        _legacyIntrinsicSamplers = CollectLegacyIntrinsicSamplers();

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

            // Sampler declaration. Four legacy forms are recognized:
            //   Form 1:  samplerNd S = sampler_state { Texture = <T>; ... };
            //   Form 2:  sampler S;                  (bare)
            //   Form 3:  sampler S : register(sN);   (bare; ':' is dropped by the lexer)
            //   Form 4:  samplerNd S { Texture = <T>; ... };                  (brace form)
            //            samplerNd S : register(sN) { ... };  (brace form with register)
            //
            // fxc treats Form 4 exactly like Form 1 — a state block opened directly
            // after the name (or its register clause) IS a sampler_state block — so
            // both forms share one parse/capture/rewrite path.
            //
            // A declaration is rewritten into the modern 'Texture2D' + 'SamplerState'
            // form only when S is sampled through a legacy intrinsic (tex2D); see
            // _legacyIntrinsicSamplers. Declarations no legacy intrinsic references
            // keep their previous handling so already-modern shaders are unaffected:
            //   - Form 1/4 unused -> erased entirely (as before)
            //   - bare     unused -> passed through verbatim (as before)
            if (tok.Kind == TokenKind.Identifier && SamplerTypeKeywords.Contains(tok.Text))
            {
                int nameOffset = NextCodeOffset(1);
                var nameTok = Peek(nameOffset);
                if (nameTok.Kind == TokenKind.Identifier)
                {
                    int afterName = NextCodeOffset(nameOffset + 1);

                    bool isSamplerStateForm =
                        Peek(afterName).Kind == TokenKind.Equals &&
                        PeekIsKeywordAt(NextCodeOffset(afterName + 1), "sampler_state");

                    // Form 4 must be detected before the bare-form check below: a
                    // brace form with a register clause starts 'S register ( … )'
                    // exactly like Form 3, and the bare-form path would swallow the
                    // declaration only up to the first ';' INSIDE the state block.
                    bool isBraceStateForm =
                        !isSamplerStateForm && IsBraceSamplerStateForm(afterName);

                    if (isSamplerStateForm || isBraceStateForm)
                    {
                        int blockStart = _tokenCharOffset[_pos];
                        var result = ParseSamplerDecl();
                        if (result.IsFailure)
                            return Result<FxParseResult, FxParseError>.Fail(result.Error);

                        SamplerInfo info = result.Value;
                        samplers.Add(info);

                        if (_mode == FxSourceMode.PreserveSm3)
                        {
                            // FNA fx_2_0 target: vkd3d's D3D_BYTECODE profile parses the
                            // sampler_state initializer natively (and ignores the state
                            // block itself), so the declaration stays in the output
                            // verbatim — no erasure, no SamplerState/Texture2D rewrite.
                            // The SamplerInfo captured above still feeds the fx_2_0
                            // parameter/state metadata.
                        }
                        else if (_legacyIntrinsicSamplers.Contains(info.Name))
                        {
                            // The block's terminating ';' is the last token ParseSamplerDecl
                            // consumed, so it sits at _pos - 1.
                            int declEnd = _tokenCharOffset[_pos - 1] + _tokens[_pos - 1].Text.Length;

                            // Bind to the explicitly-referenced texture if present
                            // (declared separately as 'Texture2D T;'); otherwise synthesize.
                            string texture = info.TextureReference ?? SynthTextureName(info.Name);
                            _samplerTextureBindings[info.Name] = texture;
                            string newDecl = info.TextureReference is not null
                                ? $"SamplerState {info.Name};"
                                : $"Texture2D {texture}; SamplerState {info.Name};";

                            replacedRanges.Add((blockStart, declEnd,
                                BuildDeclReplacement(blockStart, declEnd, newDecl)));
                        }
                        else
                        {
                            int blockEnd = _pos < _tokens.Count ? _tokenCharOffset[_pos] : _source.Length;
                            erasedRanges.Add((blockStart, blockEnd));
                        }

                        SkipNonCodeTokens();
                        continue;
                    }

                    // Bare sampler: 'S ;' (Form 2) or 'S register ( ... ) ;' (Form 3).
                    bool isBareForm =
                        Peek(afterName).Kind == TokenKind.Semicolon ||
                        (Peek(afterName).Kind == TokenKind.Identifier &&
                         string.Equals(Peek(afterName).Text, "register", StringComparison.OrdinalIgnoreCase));

                    if (isBareForm && _mode == FxSourceMode.RewriteToSm4 &&
                        _legacyIntrinsicSamplers.Contains(nameTok.Text))
                    {
                        int blockStart = _tokenCharOffset[_pos];
                        (string name, int declEnd) = ConsumeBareSamplerDecl();

                        // A bare sampler binds no texture in source — synthesize one.
                        string synth = SynthTextureName(name);
                        _samplerTextureBindings[name] = synth;
                        string newDecl = $"Texture2D {synth}; SamplerState {name};";

                        replacedRanges.Add((blockStart, declEnd,
                            BuildDeclReplacement(blockStart, declEnd, newDecl)));

                        SkipNonCodeTokens();
                        continue;
                    }

                    // Any other use of a sampler-type keyword (a function parameter,
                    // an unused bare sampler, an unused Form 1 sampler whose intrinsic
                    // isn't tex2D) falls through to verbatim copy / existing handling.
                }
            }

            // Legacy effect-framework texture declaration:
            //   texture T;                        (bare)
            //   texture T < annotations >;        (with FX annotations)
            //   texture T : register(tN);         (':' dropped by lexer -> 'register')
            // DXC rejects the FX 'texture' object type under -Weffects-syntax. Rewrite
            // the whole declaration to a modern 'Texture2D T;' so the resource the
            // sampler_state form references (gap #2) actually exists. Modern types
            // ('Texture2D', 'Texture3D', …) are matched case-sensitively above and
            // never reach here. Any trailing annotation block / register clause is
            // dropped — modern resource declarations carry neither. In PreserveSm3
            // mode the legacy 'texture' type is valid for vkd3d and passes through
            // verbatim (including any annotation block — falls through to the
            // generic 'Identifier Identifier <...>' annotation strip below).
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.Identifier &&
                LegacyTextureTypeKeywords.TryGetValue(tok.Text, out string? modernTextureType))
            {
                int nameOffset = NextCodeOffset(1);
                if (Peek(nameOffset).Kind == TokenKind.Identifier)
                {
                    int blockStart = _tokenCharOffset[_pos];
                    (string texName, int declEnd) = ConsumeLegacyTextureDecl();
                    string newDecl = $"{modernTextureType} {texName};";
                    replacedRanges.Add((blockStart, declEnd,
                        BuildDeclReplacement(blockStart, declEnd, newDecl)));

                    SkipNonCodeTokens();
                    continue;
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

            // DXC ps_6_0 rejects the legacy SM 3.0 return semantic ': COLOR<n>?' on
            // entry functions — rewrite to ': SV_Target<n>?' so production SM 3.0
            // shaders that use ') : COLOR { ... }' compile. We discriminate against
            // struct-field input semantics (which are preceded by an identifier, not
            // ')') by requiring the token before the COLOR identifier to be RParen.
            // In PreserveSm3 mode ': COLOR<n>?' is a valid SM3 output semantic for
            // vkd3d and passes through verbatim.
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.RParen && TryMatchColorReturnSemantic(out int colorTokIdx, out string replacement))
            {
                int colorStart = _tokenCharOffset[colorTokIdx];
                var colorTok = _tokens[colorTokIdx];
                int colorEnd = colorStart + colorTok.Text.Length;
                replacedRanges.Add((colorStart, colorEnd, replacement));

                // Consume only the RParen; let the loop continue past the COLOR token
                // naturally so any subsequent COLOR-return on a non-entry helper is also
                // caught. (We don't fast-forward past the LBrace because the function
                // body still needs to be in stripped output.)
                Consume();
                SkipNonCodeTokens();
                continue;
            }

            // Legacy sampling intrinsics that CANNOT be rewritten mechanically (their
            // argument lists restructure — e.g. tex2Dlod packs the LOD into coord.w).
            // Fail loudly with a targeted diagnostic naming the intrinsic instead of
            // letting DXC die later with a misleading 'unknown identifier'. Only an
            // actual CALL trips this; a user variable that merely shares the name does
            // not. PreserveSm3 (FNA) passes these through verbatim — vkd3d compiles
            // them natively.
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.Identifier && UnsupportedLegacyIntrinsics.Contains(tok.Text) &&
                Peek(NextCodeOffset(1)).Kind == TokenKind.LParen)
            {
                return Fail<FxParseResult>(FxParseErrorCode.UnsupportedLegacyIntrinsic,
                    $"The legacy D3D9 sampling intrinsic '{tok.Text}' is not supported on this " +
                    "target: its arguments do not map 1:1 onto a modern Texture method, so " +
                    "ShadowDusk cannot rewrite it automatically. Rewrite the call to the modern " +
                    "form (e.g. tex2Dlod(s, t) becomes T.SampleLevel(s, t.xy, t.w); " +
                    "tex2Dproj(s, t) becomes T.Sample(s, t.xy / t.w)). The FNA (fx_2_0) target " +
                    "compiles this intrinsic natively.", tok);
            }

            // DXC 6.x dropped the legacy 'tex2D(s, uv)' / 'tex2Dgrad(s, uv, ddx, ddy)'
            // sampling intrinsics. Rewrite them to '<texture>.Sample(…)' /
            // '<texture>.SampleGrad(…)', where <texture> is the Texture2D the sampler
            // 's' was bound to during declaration processing. The argument lists align
            // one-to-one with the Texture2D methods (sampler first), so only the
            // intrinsic identifier itself is replaced; '(s, …)' is copied verbatim.
            // A sampler not in the binding map (declaration form not understood, e.g.
            // effect-framework syntax) is left alone so DXC surfaces a clear diagnostic
            // rather than ShadowDusk emitting bad HLSL. In PreserveSm3 mode these are
            // valid SM3 intrinsics for vkd3d and pass through verbatim (no bindings are
            // recorded in that mode, so the map lookup would fail anyway — the mode
            // check makes it explicit).
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.Identifier &&
                LegacySampleIntrinsics.TryGetValue(tok.Text, out string? sampleMethod) &&
                TryMatchTexSampleArgument(out string samplerArg) &&
                _samplerTextureBindings.TryGetValue(samplerArg, out string? boundTexture))
            {
                int texStart = _tokenCharOffset[_pos];
                int texEnd = texStart + tok.Text.Length;
                replacedRanges.Add((texStart, texEnd, $"{boundTexture}.{sampleMethod}"));

                // Consume only the intrinsic identifier; '(', the sampler argument,
                // and the rest of the call flow through the loop verbatim.
                Consume();
                SkipNonCodeTokens();
                continue;
            }

            // A genuinely-unknown character (e.g. '@', '$', a backtick) — fail loudly.
            // Historically the lexer silently swallowed these, which corrupted captured
            // values; now they surface with their exact location.
            if (tok.Kind == TokenKind.Unknown)
            {
                return Fail<FxParseResult>(FxParseErrorCode.UnknownCharacter,
                    $"Unexpected character '{tok.Text}' in effect source", tok);
            }

            // Everything else: copy verbatim (advance past single token).
            Consume();
            SkipNonCodeTokens();
        }

        string strippedHlsl = BuildStrippedOutput(erasedRanges, replacedRanges);

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
                // Generic render state: Key = Value ;  (Value may be a negative
                // numeric literal, e.g. 'DepthBias = -0.5;' — the '-' is its own token.)
                string negativePrefix = string.Empty;
                if (Peek().Kind == TokenKind.Minus)
                {
                    Consume(); // '-'
                    SkipNonCodeTokens();
                    if (Peek().Kind != TokenKind.Number)
                        return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                            $"Expected numeric render-state value after '-' but found '{Peek().Text}'", Peek());
                    negativePrefix = "-";
                }

                var valueTok = Peek();
                if (valueTok.Kind is not (TokenKind.Identifier or TokenKind.Number))
                    return Fail<PassInfo>(FxParseErrorCode.UnexpectedToken,
                        $"Expected render-state value but found '{valueTok.Text}'", valueTok);

                string value = negativePrefix + valueTok.Text;
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

        // Optional ': register(sN)' clause before a brace-form state block (the
        // lexer drops the ':', leaving 'register' '(' … ')').
        if (PeekIsKeyword("register"))
        {
            Consume(); // 'register'
            SkipNonCodeTokens();

            var regLParen = Expect(TokenKind.LParen);
            if (regLParen.IsFailure)
                return Result<SamplerInfo, FxParseError>.Fail(regLParen.Error);
            while (Peek().Kind is not (TokenKind.RParen or TokenKind.EOF))
                Consume();

            var regRParen = Expect(TokenKind.RParen);
            if (regRParen.IsFailure)
                return Result<SamplerInfo, FxParseError>.Fail(regRParen.Error);
            SkipNonCodeTokens();
        }

        // Form 1 carries '= sampler_state' before the state block; Form 4 (the
        // brace form) opens the block directly. fxc accepts both with identical
        // semantics, so everything from the '{' on is shared.
        if (Peek().Kind == TokenKind.Equals)
        {
            Consume(); // '='
            SkipNonCodeTokens();

            if (!PeekIsKeyword("sampler_state"))
                return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                    $"Expected 'sampler_state' but found '{Peek().Text}'", Peek());
            Consume(); // "sampler_state"
            SkipNonCodeTokens();
        }

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
                // Texture = <TexName>; OR Texture = (TexName); OR Texture = TexName;
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
                else if (Peek().Kind == TokenKind.LParen)
                {
                    // 'Texture = (TexName);' — ubiquitous legacy XNA syntax that fxc
                    // accepts identically to the angle-bracket form.
                    Consume(); // '('
                    SkipNonCodeTokens();
                    var texTok = Peek();
                    if (texTok.Kind != TokenKind.Identifier)
                        return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                            $"Expected texture name inside '()' but found '{texTok.Text}'", texTok);
                    textureRef = texTok.Text;
                    Consume();
                    SkipNonCodeTokens();
                    var rp = Expect(TokenKind.RParen);
                    if (rp.IsFailure)
                        return Result<SamplerInfo, FxParseError>.Fail(rp.Error);
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
                // Sampler-state value, optionally a negative numeric literal
                // (e.g. 'MipMapLodBias = -2;' — the '-' is its own token).
                string negativePrefix = string.Empty;
                if (Peek().Kind == TokenKind.Minus)
                {
                    Consume(); // '-'
                    SkipNonCodeTokens();
                    if (Peek().Kind != TokenKind.Number)
                        return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                            $"Expected numeric sampler state value after '-' but found '{Peek().Text}'", Peek());
                    negativePrefix = "-";
                }

                var valTok = Peek();
                if (valTok.Kind is not (TokenKind.Identifier or TokenKind.Number))
                    return Fail<SamplerInfo>(FxParseErrorCode.UnexpectedToken,
                        $"Expected sampler state value but found '{valTok.Text}'", valTok);

                var span = new SourceSpan(keyTok.Line, keyTok.Column,
                    valTok.Line, valTok.Column + valTok.Text.Length);
                stateEntries.Add(new SamplerStateEntry(key, negativePrefix + valTok.Text, span));
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

            // Annotation value, optionally a negative numeric literal
            // (e.g. '< float UIMin = -1.0; >' — the '-' is its own token).
            string negativePrefix = string.Empty;
            if (Peek().Kind == TokenKind.Minus)
            {
                Consume(); // '-'
                SkipNonCodeTokens();
                if (Peek().Kind != TokenKind.Number)
                    return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnexpectedToken,
                        $"Expected numeric annotation value after '-' but found '{Peek().Text}'", Peek());
                negativePrefix = "-";
            }

            var valueTok = Peek();
            if (valueTok.Kind is not (TokenKind.StringLiteral or TokenKind.Number or TokenKind.Identifier))
                return Fail<List<AnnotationEntry>>(FxParseErrorCode.UnexpectedToken,
                    $"Expected annotation value but found '{valueTok.Text}'", valueTok);

            string value = negativePrefix + valueTok.Text;
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

    private string BuildStrippedOutput(
        List<(int Start, int End)> erasedRanges,
        List<(int Start, int End, string Replacement)> replacedRanges)
    {
        if (erasedRanges.Count == 0 && replacedRanges.Count == 0)
            return _source;

        // Sort and merge overlapping erasures.
        erasedRanges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var mergedErased = new List<(int Start, int End)>();
        foreach (var range in erasedRanges)
        {
            if (mergedErased.Count > 0 && range.Start <= mergedErased[^1].End)
                mergedErased[^1] = (mergedErased[^1].Start, Math.Max(mergedErased[^1].End, range.End));
            else
                mergedErased.Add(range);
        }

        // Combine erasures and replacements into a single ordered edit list.
        // Replacements (currently only ': COLOR' rewrites) are produced only for
        // token spans that live outside any erased block, so they cannot overlap.
        var edits = new List<(int Start, int End, string? Replacement)>(
            mergedErased.Count + replacedRanges.Count);
        foreach (var (s, e) in mergedErased)
            edits.Add((s, e, null));
        foreach (var (s, e, r) in replacedRanges)
            edits.Add((s, e, r));
        edits.Sort((a, b) => a.Start.CompareTo(b.Start));

        var sb = new StringBuilder(_source.Length);
        int cursor = 0;

        foreach (var (start, end, replacement) in edits)
        {
            // Copy verbatim up to the edit start.
            if (cursor < start)
                sb.Append(_source, cursor, start - cursor);

            if (replacement is null)
            {
                // Erasure: preserve newlines, blank out the rest so line numbers stay aligned.
                for (int i = start; i < end && i < _source.Length; i++)
                {
                    char c = _source[i];
                    if (c == '\n')
                        sb.Append('\n');
                    else if (c == '\r')
                        sb.Append('\r');
                    else
                        sb.Append(' ');
                }
            }
            else
            {
                // Substitution: replace the span with the literal replacement text.
                // We assume the replacement contains no newlines (true for the
                // COLOR -> SV_Target rewrite), so column positions on the same line
                // may shift but line numbers remain accurate.
                sb.Append(replacement);
            }

            cursor = end;
        }

        // Copy any remaining source after the last edit.
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

    /// <summary>
    /// Returns the first offset at or after <paramref name="offset"/> (relative to
    /// the current position) whose token is not a comment, so callers can look
    /// past interleaved comments when matching a declaration shape.
    /// </summary>
    private int NextCodeOffset(int offset)
    {
        while (Peek(offset).Kind is TokenKind.LineComment or TokenKind.BlockComment)
            offset++;
        return offset;
    }

    /// <summary>The synthesized <c>Texture2D</c> name bound to a bare/untextured sampler.</summary>
    private static string SynthTextureName(string samplerName) => samplerName + "_SDTexture";

    /// <summary>
    /// One-pass scan over the whole token stream collecting the names of samplers
    /// used as the first argument of a legacy <c>tex2D</c> intrinsic. This drives
    /// which sampler declarations the main loop rewrites; declarations no intrinsic
    /// references are left exactly as before. Intrinsic text inside comments and
    /// preprocessor directives is never seen here because the lexer emits those as
    /// single comment / preprocessor tokens, not <c>Identifier</c> tokens.
    /// </summary>
    private HashSet<string> CollectLegacyIntrinsicSamplers()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < _tokens.Count; i++)
        {
            if (_tokens[i].Kind != TokenKind.Identifier || !LegacySampleIntrinsics.ContainsKey(_tokens[i].Text))
                continue;

            // Expect '<intrinsic>' '(' Identifier — comments may sit in between.
            int j = i + 1;
            while (j < _tokens.Count && _tokens[j].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                j++;
            if (j >= _tokens.Count || _tokens[j].Kind != TokenKind.LParen)
                continue;

            j++;
            while (j < _tokens.Count && _tokens[j].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                j++;
            if (j < _tokens.Count && _tokens[j].Kind == TokenKind.Identifier)
                used.Add(_tokens[j].Text);
        }

        return used;
    }

    /// <summary>
    /// Detects the brace-form sampler declaration '<c>sampler S { … };</c>' (with an
    /// optional '<c>: register(sN)</c>' clause before the '{' — the lexer drops the
    /// ':'). <paramref name="afterName"/> is the look-ahead offset of the first code
    /// token after the declared name. fxc treats this form exactly like
    /// '<c>= sampler_state { … }</c>'. No false positives on other '{'-bearing
    /// constructs: a function returning a sampler type ('<c>sampler F() { … }</c>')
    /// has '(' after the name, sampler-typed function parameters are followed by
    /// ',' or ')', and struct/cbuffer/technique bodies never reach this check
    /// because their type keywords are not sampler types.
    /// </summary>
    private bool IsBraceSamplerStateForm(int afterName)
    {
        int off = afterName;

        // Optional register clause: 'register' '(' … ')'.
        if (PeekIsKeywordAt(off, "register"))
        {
            off = NextCodeOffset(off + 1);
            if (Peek(off).Kind != TokenKind.LParen)
                return false;

            off = NextCodeOffset(off + 1);
            while (Peek(off).Kind is not (TokenKind.RParen or TokenKind.EOF))
                off = NextCodeOffset(off + 1);
            if (Peek(off).Kind != TokenKind.RParen)
                return false;

            off = NextCodeOffset(off + 1);
        }

        return Peek(off).Kind == TokenKind.LBrace;
    }

    /// <summary>
    /// At a <c>tex2D</c> identifier token, matches the following <c>'(' Identifier</c>
    /// and returns the sampler argument name. Comments may appear between tokens.
    /// </summary>
    private bool TryMatchTexSampleArgument(out string samplerArg)
    {
        samplerArg = string.Empty;

        int lparen = NextCodeOffset(1);
        if (Peek(lparen).Kind != TokenKind.LParen)
            return false;

        int arg = NextCodeOffset(lparen + 1);
        if (Peek(arg).Kind != TokenKind.Identifier)
            return false;

        samplerArg = Peek(arg).Text;
        return true;
    }

    /// <summary>
    /// Consumes a bare sampler declaration ('<c>sampler S;</c>' or
    /// '<c>sampler S : register(sN);</c>') starting at the sampler-type keyword and
    /// ending just past the terminating ';'. Returns the declared name and the
    /// exclusive character offset of the ';' end so the caller can substitute the span.
    /// </summary>
    private (string Name, int DeclEnd) ConsumeBareSamplerDecl()
    {
        Consume();              // sampler-type keyword
        SkipNonCodeTokens();
        var nameTok = Consume(); // sampler name (caller verified Identifier)

        // Swallow everything up to and including the terminating ';' (covers an
        // optional ': register(sN)' clause; the lexer already dropped the ':').
        while (Peek().Kind != TokenKind.Semicolon && Peek().Kind != TokenKind.EOF)
            Consume();

        int declEnd;
        if (Peek().Kind == TokenKind.Semicolon)
        {
            var semi = Consume();
            declEnd = _tokenCharOffset[_pos - 1] + semi.Text.Length;
        }
        else
        {
            declEnd = _source.Length; // malformed (no ';' before EOF)
        }

        return (nameTok.Text, declEnd);
    }

    /// <summary>
    /// Consumes a legacy effect-framework texture declaration ('<c>texture T;</c>',
    /// '<c>texture T &lt; ... &gt;;</c>', or '<c>texture T : register(tN);</c>')
    /// starting at the texture-type keyword and ending just past the terminating
    /// ';'. Returns the declared name and the exclusive character offset of the
    /// ';' end so the caller can substitute the whole span with a modern resource
    /// declaration. Any annotation block or register clause between the name and
    /// the ';' is swallowed (the modern declaration keeps neither).
    /// </summary>
    private (string Name, int DeclEnd) ConsumeLegacyTextureDecl()
    {
        Consume();              // texture-type keyword
        SkipNonCodeTokens();
        var nameTok = Consume(); // texture name (caller verified Identifier)

        // Swallow everything up to and including the terminating ';' (covers an
        // optional '< ... >' annotation block or ': register(tN)' clause).
        while (Peek().Kind != TokenKind.Semicolon && Peek().Kind != TokenKind.EOF)
            Consume();

        int declEnd;
        if (Peek().Kind == TokenKind.Semicolon)
        {
            var semi = Consume();
            declEnd = _tokenCharOffset[_pos - 1] + semi.Text.Length;
        }
        else
        {
            declEnd = _source.Length; // malformed (no ';' before EOF)
        }

        return (nameTok.Text, declEnd);
    }

    /// <summary>
    /// Builds the substitution text for a rewritten sampler declaration: the new
    /// declaration followed by every newline character from the original span, in
    /// order. This keeps the stripped output's total line count identical to the
    /// source so DXC diagnostics on later lines still point at the right line.
    /// </summary>
    private string BuildDeclReplacement(int spanStart, int spanEnd, string newDecl)
    {
        var sb = new StringBuilder(newDecl.Length + 8);
        sb.Append(newDecl);
        for (int i = spanStart; i < spanEnd && i < _source.Length; i++)
        {
            char c = _source[i];
            if (c == '\n' || c == '\r')
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Detects the trailing-form pixel-shader return semantic pattern
    /// '<c>) : COLOR&lt;n&gt;? {</c>' at the current position (which must be RParen).
    /// The FxLexer drops ':' as an unknown character, so at the token level the
    /// pattern is simply RParen → Identifier("COLOR"|"COLORn") → LBrace, with
    /// comments allowed in between. Struct-field input semantics never match
    /// because they are preceded by an identifier, not by ')'.
    /// </summary>
    /// <param name="colorTokenIndex">Absolute index of the COLOR identifier token when matched.</param>
    /// <param name="replacement">Replacement text ('SV_Target' or 'SV_Targetn') when matched.</param>
    private bool TryMatchColorReturnSemantic(out int colorTokenIndex, out string replacement)
    {
        colorTokenIndex = -1;
        replacement = string.Empty;

        // Find next non-comment token (the candidate COLOR identifier).
        int off = 1;
        while (Peek(off).Kind is TokenKind.LineComment or TokenKind.BlockComment)
            off++;

        var candidate = Peek(off);
        if (candidate.Kind != TokenKind.Identifier)
            return false;

        string text = candidate.Text;
        if (!text.StartsWith("COLOR", StringComparison.OrdinalIgnoreCase))
            return false;

        string suffix = text.Substring("COLOR".Length);
        if (suffix.Length > 1)
            return false;
        if (suffix.Length == 1 && !char.IsAsciiDigit(suffix[0]))
            return false;

        // Verify the next non-comment token after COLOR is '{' (function body opener).
        int off2 = off + 1;
        while (Peek(off2).Kind is TokenKind.LineComment or TokenKind.BlockComment)
            off2++;
        if (Peek(off2).Kind != TokenKind.LBrace)
            return false;

        colorTokenIndex = _pos + off;
        replacement = "SV_Target" + suffix;
        return true;
    }
}
