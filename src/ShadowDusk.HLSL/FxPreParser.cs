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
public sealed partial class FxPreParser
{
    // -------------------------------------------------------------------------
    // Known shader profiles
    // -------------------------------------------------------------------------

    // All profiles accepted at pre-parse time; an unrecognized literal profile (or a
    // macro that does not expand to one) is rejected LATER by the pipeline's recognized-
    // profile check (SD0013) after macro expansion — the pre-parser stays lenient because
    // it runs before macro expansion and cannot know what 'PS_SHADERMODEL' resolves to.
    // We store the raw string.
    private static readonly HashSet<string> KnownProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // VS profiles
        "vs_1_1",
        "vs_2_0", "vs_2_a", "vs_2_sw",
        "vs_3_0", "vs_3_sw",
        "vs_4_0", "vs_4_1",
        // Direct3D feature-level 9 variants of the SM4 vertex profile. These are the
        // EXACT tokens the standard MonoGame cross-platform header expands to on the
        // DirectX branch ('#define VS_SHADERMODEL vs_4_0_level_9_1'), so they MUST be
        // recognized — otherwise every stock MonoGame DirectX shader is wrongly rejected
        // once recognized-profile validation is enabled. fxc accepts the
        // _level_9_0/_9_1/_9_3 forms for vs_4_0 (there is no _level_9_2).
        "vs_4_0_level_9_0", "vs_4_0_level_9_1", "vs_4_0_level_9_3",
        "vs_5_0",
        "vs_6_0", "vs_6_1", "vs_6_2", "vs_6_3", "vs_6_4", "vs_6_5", "vs_6_6", "vs_6_7", "vs_6_8", "vs_6_9",
        // PS profiles
        "ps_1_1", "ps_1_2", "ps_1_3", "ps_1_4",
        "ps_2_0", "ps_2_a", "ps_2_b", "ps_2_sw",
        "ps_3_0", "ps_3_sw",
        "ps_4_0", "ps_4_1",
        // Direct3D feature-level 9 variants of the SM4 pixel profile (the DirectX-branch
        // '#define PS_SHADERMODEL ps_4_0_level_9_1'). Same fxc support as the vs_ forms.
        "ps_4_0_level_9_0", "ps_4_0_level_9_1", "ps_4_0_level_9_3",
        "ps_5_0",
        "ps_6_0", "ps_6_1", "ps_6_2", "ps_6_3", "ps_6_4", "ps_6_5", "ps_6_6", "ps_6_7", "ps_6_8", "ps_6_9",
    };

    /// <summary>Returns true when the given profile string (already lowercased) is a recognized shader profile.</summary>
    public static bool IsKnownProfile(string profile) => KnownProfiles.Contains(profile);

    /// <summary>Every recognized profile, for tests that sweep the whole set (e.g. <c>KniDirectXProfileTests</c>).</summary>
    internal static IReadOnlyCollection<string> KnownProfileNames => KnownProfiles;

    // -------------------------------------------------------------------------
    // MonoGame DirectX_11 profile floor (Phase 51 A10)
    // -------------------------------------------------------------------------

    // mgfxc's DirectX_11 shader profile accepts EXACTLY these five vertex targets and
    // their pixel siblings; everything else it refuses with
    //   "Invalid profile 'X'. Vertex shader 'E' must be SM 4.0 level 9.1 or higher!"
    // This set is EMPIRICAL, not derived from the profile names: measured 2026-07-31 by
    // sweeping every KnownProfiles entry through the pinned mgfxc (dotnet-mgcb 3.8.4.1,
    // the golden oracle) for /Profile:DirectX_11. Two results are unobvious and are why
    // "major >= 4" would be WRONG in both directions:
    //   * vs_4_0_level_9_0 / ps_4_0_level_9_0 are REJECTED (only _9_1 and _9_3 pass),
    //   * vs_6_0 / ps_6_0 (and every other SM6 profile) are REJECTED TOO — MonoGame's
    //     DirectX_11 profile regex tops out at major 5, so SM6 is below-the-floor as far
    //     as its message is concerned even though it is numerically higher.
    private static readonly HashSet<string> DirectX11Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "vs_4_0_level_9_1", "vs_4_0_level_9_3", "vs_4_0", "vs_4_1", "vs_5_0",
        "ps_4_0_level_9_1", "ps_4_0_level_9_3", "ps_4_0", "ps_4_1", "ps_5_0",
    };

    /// <summary>
    /// Returns true when <paramref name="profile"/> (a recognized profile, already
    /// lowercased) is one MonoGame's <c>DirectX_11</c> shader profile accepts. Anything
    /// else is below that target's floor and is rejected by <c>mgfxc</c> with
    /// <c>"must be SM 4.0 level 9.1 or higher!"</c>; ShadowDusk reports the same condition
    /// as <c>SD0015</c>. See the empirical note on the backing set.
    /// </summary>
    public static bool IsDirectX11Profile(string profile) => DirectX11Profiles.Contains(profile);

    /// <summary>
    /// Returns true when <paramref name="token"/> is SHAPED like a shader profile
    /// (a <c>vs_</c>/<c>ps_</c>/<c>gs_</c>/<c>hs_</c>/<c>ds_</c>/<c>cs_</c> stage prefix
    /// immediately followed by a digit) REGARDLESS of whether it is a recognized profile.
    /// This distinguishes a typo'd real-shaped profile (<c>ps_9_9</c>) — which is
    /// unconditionally invalid and can be rejected without any macro expansion — from a
    /// macro NAME (<c>PS_SHADERMODEL</c>), which may still expand to a valid profile. It is
    /// NOT a substitute for <see cref="IsKnownProfile"/>: a profile-shaped token is not
    /// necessarily a real profile.
    /// </summary>
    public static bool LooksLikeProfile(string token) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            token,
            @"^(vs|ps|gs|hs|ds|cs)_\d",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // -------------------------------------------------------------------------
    // Sampler type keywords
    // -------------------------------------------------------------------------

    private static readonly HashSet<string> SamplerTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE",
        "sampler_state", "SamplerState", "Sampler2D", "Sampler3D", "SamplerCube",
    };

    // -------------------------------------------------------------------------
    // Render-state keys whose value is a D3DCOLORWRITEENABLE flag mask
    // -------------------------------------------------------------------------

    // The ColorWriteEnable family takes an OR of RED/GREEN/BLUE/ALPHA/ALL flags
    // (e.g. 'ColorWriteEnable = Red | Green | Blue;'). The FxLexer drops '|', so
    // the value arrives as several adjacent Identifier tokens; for these keys the
    // pass render-state value parser accumulates the consecutive identifiers and
    // re-joins them with '|' (which RenderStateParser.TryParseColorWriteMask then
    // splits). Scoped to these keys only — every other render state is a single
    // value token, so the single-token path is unchanged for them.
    private static readonly HashSet<string> ColorWriteMaskKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ColorWriteEnable", "ColorWriteEnable1", "ColorWriteEnable2", "ColorWriteEnable3",
    };

    // -------------------------------------------------------------------------
    // Pass shader-stage assignments the consumer runtime cannot load (FX0014)
    // -------------------------------------------------------------------------

    // A pass may assign VertexShader and PixelShader; every OTHER key falls through to
    // render-state parsing. For these four that produced a nonsense diagnostic — the
    // 'compile' expression is not a render-state value, so the parser blamed a missing
    // ';' (FX0008) on a file with no punctuation error, sending the user hunting for a
    // syntax bug that does not exist.
    //
    // They are rejected rather than modelled because the CONSUMER RUNTIME has nowhere to
    // put them, measured from source 2026-08-05:
    //   * MonoGame v3.8.5 and develop: 'ShaderStage' declares exactly { Vertex, Pixel },
    //     and the effect reader decodes the stage as ONE BOOL
    //     ("var isVertexShader = reader.ReadBoolean();"), so the container cannot even
    //     name a third stage. The new 3.8.5 DesktopVK / WindowsDX12 backends inherit that
    //     same shared managed reader.
    //   * KNI v4.29001: 'ShaderStage : byte { Pixel = 0, Vertex = 1 }' and MGFXReader10
    //     switches on it with a throwing 'default'.
    // So this is a runtime limit no compiler can work around, not a ShadowDusk gap.
    //
    // The reject set is mgfxc-faithful, MEASURED not assumed (2026-08-05, pinned mgfxc
    // 3.8.2.1105, /Profile:DirectX_11): mgfxc refuses all four stages in BOTH the
    // '= compile <profile> Entry();' and the '= NULL;' form, pointing at the stage keyword
    // ("Unexpected token 'H' found. Expected CloseBracket"). We reject the same eight
    // inputs at the same token and only say why. Nothing that used to compile stops
    // compiling; no output byte moves.
    private static readonly Dictionary<string, string> UnloadableShaderStages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HullShader"]     = "hull",
            ["DomainShader"]   = "domain",
            ["GeometryShader"] = "geometry",
            ["ComputeShader"]  = "compute",
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

    // The 2D legacy sampling intrinsics that pack a second operand into the
    // coordinate's w component, so their argument list does NOT align with the
    // Texture2D method: tex2Dlod(s, t) is fxc's sample_l at LOD t.w, tex2Dbias(s, t)
    // its sample_b with bias t.w, tex2Dproj(s, t) its sample at t.xy / t.w. Each is
    // rewritten to a call of a small helper the pre-parser defines at global scope
    // right before the declaration that uses it (see RewriteRestructuredIntrinsic):
    //   tex2Dlod(s, t)  -> tex2Dlod_SD0(T, s, t)
    //   float4 tex2Dlod_SD0(Texture2D sdTexture, SamplerState sdSampler, float4 sdCoord)
    //   { return sdTexture.SampleLevel(sdSampler, sdCoord.xy, sdCoord.w); }
    // A helper (not an inline '.SampleLevel(s, (t).xy, (t).w)') because the coordinate
    // must be evaluated ONCE, exactly as the intrinsic evaluates it: an expression with
    // a side effect (an assignment, '++', a call with an inout parameter) would run
    // twice if it were copied. Its parameters are the texture and the sampler, so it
    // references no global and is valid wherever it is placed, including when the
    // texture or the sampler is declared inside a conditional.
    // Value: the helper's return expression over sdTexture / sdSampler / sdCoord.
    // mgfxc 3.8.4.1 /Profile:DirectX_11 compiles all three at ps_4_0, and tex2Dlod at
    // vs_4_0 as well (measured 2026-10-28).
    private static readonly Dictionary<string, string> RestructuredLegacySampleIntrinsics = new(StringComparer.Ordinal)
    {
        ["tex2Dlod"]  = "sdTexture.SampleLevel(sdSampler, sdCoord.xy, sdCoord.w)",
        ["tex2Dbias"] = "sdTexture.SampleBias(sdSampler, sdCoord.xy, sdCoord.w)",
        ["tex2Dproj"] = "sdTexture.Sample(sdSampler, sdCoord.xy / sdCoord.w)",
    };

    // Legacy sampling intrinsics with no rewrite: the 1D/3D/CUBE families need a
    // non-Texture2D resource (Texture1D / Texture3D / TextureCube) that the sampler
    // rewrite does not synthesize, so they fail loudly with a targeted FX0012
    // diagnostic instead of dying later inside DXC with a misleading 'unknown
    // identifier' error. mgfxc 3.8.4.1 /Profile:DirectX_11 compiles every one of them
    // (measured 2026-10-28), so this is a ShadowDusk gap, not reference parity.
    // The FNA target (PreserveSm3) compiles all of these natively via vkd3d.
    private static readonly HashSet<string> UnsupportedLegacyIntrinsics = new(StringComparer.Ordinal)
    {
        "tex1D", "tex1Dbias", "tex1Dgrad", "tex1Dlod", "tex1Dproj",
        "tex3D", "tex3Dbias", "tex3Dgrad", "tex3Dlod", "tex3Dproj",
        "texCUBE", "texCUBEbias", "texCUBEgrad", "texCUBElod", "texCUBEproj",
    };

    // SM4+ resource/sampler types with no fx_2_0 (D3D9 SM1-3) equivalent. Only the FNA
    // target (PreserveSm3) rejects these — see the FX0013 guard in the token loop for why
    // this must happen BEFORE vkd3d sees the source.
    private static readonly HashSet<string> UnsupportedFx2ResourceTypes = new(StringComparer.Ordinal)
    {
        "SamplerComparisonState",
        "Texture2DArray", "Texture1DArray", "TextureCubeArray",
        "Texture2DMS", "Texture2DMSArray",
        "RWTexture1D", "RWTexture2D", "RWTexture3D",
        "StructuredBuffer", "RWStructuredBuffer", "ByteAddressBuffer", "RWByteAddressBuffer",
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

    // Names of samplers that appear as an argument of a MODERN Texture method
    // call ('Tex.Sample(S, uv)', '.SampleGrad', '.SampleLevel', …), where the
    // legacy 'tex2D' is NOT used. Populated by a pre-scan before the main loop.
    // A 'sampler S = sampler_state { … }' / 'SamplerState S { … }' declaration in
    // this set must NOT be erased in RewriteToSm4 mode (the old behavior): DXC
    // cannot parse the FX9 '= sampler_state { … }' initializer, but the modern
    // '.Sample(S, …)' call still needs 'S' declared, so the declaration is
    // rewritten to a passthrough 'SamplerState S;'. A sampler in NEITHER this set
    // nor _legacyIntrinsicSamplers is genuinely unused and stays erased.
    private IReadOnlySet<string> _modernMethodSamplers = new HashSet<string>(StringComparer.Ordinal);

    // Maps a rewritten sampler name to the Texture2D it should sample through in
    // the rewritten '<texture>.Sample(<sampler>, uv)' call. Populated as sampler
    // declarations are processed in the main loop; read when a tex2D call is
    // rewritten. Valid HLSL declares samplers before use, so a sampler is always
    // in this map by the time its tex2D call is reached.
    private readonly Dictionary<string, string> _samplerTextureBindings = new(StringComparer.Ordinal);

    // The subset of _samplerTextureBindings whose texture this parser INVENTED
    // (SynthTextureName) because the source bound none: sampler name -> synthesized name.
    // Kept apart from the bindings because the effect parameter for such a pair must carry
    // the SAMPLER's name, as mgfxc's does (see FxParseResult.SynthesizedSamplerTextures).
    private readonly Dictionary<string, string> _synthesizedSamplerTextures = new(StringComparer.Ordinal);

    // SAMPLER name -> the texture its sampler_state block names, for every legacy sampler whose
    // texture the MAIN file's code never declares or mentions (see IsOnlyNamedByTextureStates).
    // Only candidates: an #include'd file or a macro can still declare it, which only the
    // preprocessed source shows, so the compiler decides (FxParseResult.UndeclaredStateTextures).
    private readonly Dictionary<string, string> _undeclaredStateTextures = new(StringComparer.Ordinal);

    /// <summary>
    /// SAMPLER name -> the explicit <c>register(sN)</c> index on its legacy declaration, captured
    /// before the SM4 rewrite drops the clause (issue #189). Resolved to TEXTURE names against
    /// <see cref="_samplerTextureBindings"/> when the result is built, because the GL sampler
    /// table joins on the texture.
    /// </summary>
    private readonly Dictionary<string, int> _explicitSamplerRegisters = new(StringComparer.Ordinal);

    // Character spans of trailing sampler-level FX annotation blocks
    // ('sampler S = sampler_state { … } < … >;') that ParseSamplerDecl consumed.
    // These are FX metadata that no shader backend understands, so they are erased
    // from the stripped output in EVERY mode — including PreserveSm3, where the
    // rest of the sampler declaration otherwise passes through verbatim to vkd3d.
    // Drained by the ParseFile sampler dispatch after each ParseSamplerDecl call.
    private readonly List<(int Start, int End)> _samplerAnnotationErasures = new();

    // The helpers RewriteRestructuredIntrinsic defines, keyed by the character offset of the
    // top-level declaration each is placed before. One helper per (declaration, intrinsic): a
    // second call in the same declaration reuses it, and a declaration in another #if branch
    // gets its own, so no configuration ever sees two definitions of one name.
    private readonly Dictionary<int, StringBuilder> _restructuredHelperText = new();
    // Declaration start -> the end offset of the top-level ';' / '}' that precedes it (-1 at the
    // start of the file): the helper's second-choice position (see the placement in ParseFile).
    private readonly Dictionary<int, int> _restructuredBoundaryEnd = new();
    // Calls left verbatim for the preprocessed re-parse (FxParseResult.DeferredLegacyIntrinsicCalls).
    private readonly List<FxParseError> _deferredRestructuredCalls = new();
    private readonly Dictionary<(int DeclStart, string Intrinsic, string TextureType), string> _restructuredHelperNames = new();
    private int _restructuredHelperCount;

    // True when the text being parsed is the PREPROCESSED source of the legacy-sampler recovery
    // (issue #308): conditionals already evaluated, macros already expanded, so every token is
    // one the compiler will see. Only then is it safe to rewrite a legacy sampler declaration
    // nothing reads (see the bare-form dispatch in ParseFile); on raw source the same text can
    // sit in a branch the target never compiles.
    private readonly bool _preprocessed;

    // -------------------------------------------------------------------------
    // Constructor (private — callers use the static Parse entry point)
    // -------------------------------------------------------------------------

    private FxPreParser(string source, string sourceFile, IReadOnlyList<Token> tokens, int[] tokenCharOffset, FxSourceMode mode, bool preprocessed = false,
        IReadOnlySet<string>? excludedSamplerParameterFunctions = null)
    {
        _source = source;
        _sourceFile = sourceFile;
        _tokens = tokens;
        _tokenCharOffset = tokenCharOffset;
        _mode = mode;
        _preprocessed = preprocessed;
        _excludedSamplerParameterFunctions = excludedSamplerParameterFunctions ?? new HashSet<string>(StringComparer.Ordinal);
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
        return ParseFileExcludingUnboundSamplerParameterFunctions(
            excluded => new FxPreParser(source, sourceFile, tokens, offsets, mode, preprocessed: false, excluded));
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

        // Deferred ': COLOR' -> ': SV_Target' rewrites (B6). The COLOR-return rewrite
        // must apply ONLY to PIXEL-shader entry points: a VERTEX shader's return
        // semantic of ': COLOR' is legal HLSL (e.g. a VS that writes POSITION via an
        // 'out' parameter and returns a colour) that fxc/mgfxc accept, but rewriting
        // it to the PS-only ': SV_Target' makes the VS invalid. Techniques may be
        // parsed AFTER the functions, so we cannot know a function is a VS entry when
        // its '): COLOR {' is scanned. We therefore COLLECT each candidate rewrite
        // together with the enclosing function name, collect the VS entry names from
        // the 'compile vs_* <name>' pass statements, and at the very end apply every
        // deferred rewrite EXCEPT those whose function is a VS entry. (PS entries and
        // helper functions keep being rewritten — the audit confirmed that is correct.)
        var deferredColorRewrites = new List<(int Start, int End, string Replacement, string? Function)>();
        var vertexEntryNames = new HashSet<string>(StringComparer.Ordinal);

        // Brace-nesting depth in the main scan (B7). Incremented when the verbatim
        // path consumes a '{' and decremented on '}'. Technique / pass / sampler_state
        // bodies are consumed inside their own parsers (never via the main loop), so
        // this depth reflects ONLY genuine HLSL body scope — a function/struct/cbuffer
        // body is depth >= 1, global/declaration scope is depth 0, and a global
        // initializer list ('float3 c = {1,2,3};') balances back to 0. The GENERIC
        // global-parameter annotation strip is gated on depth 0 so a relational/
        // ternary expression in a function body (whose dropped '?'/':'/'['/']' can
        // mimic the 'Ident Ident <' annotation shape, e.g. 'x[i] < y ? z = w : q;')
        // can never be misread as an annotation. ONLY the annotation strip is gated;
        // the legacy 'tex2D' -> '.Sample' rewrite and the other in-body rewrites must
        // still fire at depth >= 1.
        int braceDepth = 0;

        // Pre-scan: discover which samplers are sampled through a legacy
        // intrinsic so the main loop knows which declarations to rewrite.
        _legacyIntrinsicSamplers = CollectLegacyIntrinsicSamplers();

        // Pre-scan (issue #443): user functions taking a legacy sampler parameter whose every use
        // the rewrite can follow. Their parameters, calls and reads are rewritten below, and the
        // samplers passed to them are rewritten like samplers a tex2D reads.
        IReadOnlySet<string> passedSamplers = CollectSamplerParameterFunctions();
        if (passedSamplers.Count > 0)
            _legacyIntrinsicSamplers = new HashSet<string>(_legacyIntrinsicSamplers.Concat(passedSamplers), StringComparer.Ordinal);

        // Pre-scan: discover which samplers are used through a MODERN Texture
        // method call (e.g. 'Tex.Sample(S, uv)') so a 'sampler_state' declaration
        // that the modern code actually references is rewritten to a passthrough
        // 'SamplerState S;' rather than erased (see _modernMethodSamplers).
        _modernMethodSamplers = CollectModernMethodSamplers();

        // Skip comments and preprocessor directives at the start of the token stream
        // (they are always included verbatim in stripped output).
        SkipNonCodeTokens();

        while (Peek().Kind != TokenKind.EOF)
        {
            var tok = Peek();

            // Issue #443: a legacy sampler parameter of a function the pre-scan can follow
            // becomes the pair the declaration rewrite produces ('sampler2D s' ->
            // 'Texture2D s_SDTexture, SamplerState s'), and a sampler passed to it becomes its
            // texture and itself ('F(B, uv)' -> 'F(B_SDTexture, B, uv)').
            if (TryRewriteSamplerParameter(replacedRanges) || TryRewriteSamplerArgument(tok, replacedRanges))
            {
                SkipNonCodeTokens();
                continue;
            }

            // technique / technique10 / technique11 — but NOT macro calls like TECHNIQUE(name, vs, ps).
            // A real technique declaration is followed by an identifier name (or annotation '<' or body '{'),
            // never by '(' which would indicate a preprocessor macro invocation.
            if (tok.Kind == TokenKind.Identifier &&
                (string.Equals(tok.Text, "technique", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tok.Text, "technique10", StringComparison.OrdinalIgnoreCase) ||
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

                // On raw text the same name can legally appear once per #if branch
                // (`#if SM4 technique T {…} #else technique T {…} #endif`), so a duplicate is
                // only an error once the text is preprocessed. The compile reads its techniques
                // from the preprocessed translation unit (ParseTechniquesPreprocessed, issue
                // #407), which reports a real duplicate as FX0005. Anonymous techniques all carry ""
                // and are never duplicates: mgfxc 3.8.4.1 (OpenGL, DirectX_11), KNI's pipeline
                // 4.3.9001 and fxc fx_2_0 compile two of them (measured 2026-10-28).
                if (tech.Name.Length > 0 && !techniqueNames.Add(tech.Name) && _preprocessed)
                    return Fail<FxParseResult>(FxParseErrorCode.DuplicateTechniqueName,
                        $"Duplicate technique name '{tech.Name}'", tok);

                techniques.Add(tech);

                // Record every vertex-shader entry name so the deferred ': COLOR'
                // rewrite (B6) skips a VS function-return semantic.
                foreach (var pass in tech.Passes)
                    if (pass.VertexEntryPoint is { } vsEntry)
                        vertexEntryNames.Add(vsEntry);

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

                    // The '= sampler_state' initializer may be preceded by a
                    // 'register(sN)' clause ('sampler S : register(s0) = sampler_state
                    // { … };'). The lexer drops the ':', so after the name we may see
                    // 'register' '(' … ')' before the '='. Skip it so the '= sampler_state'
                    // form is detected and routed to ParseSamplerDecl (which consumes the
                    // register clause itself) rather than mis-routed to the bare path.
                    int afterRegister = OffsetAfterOptionalRegister(afterName);

                    bool isSamplerStateForm =
                        Peek(afterRegister).Kind == TokenKind.Equals &&
                        PeekIsKeywordAt(NextCodeOffset(afterRegister + 1), "sampler_state");

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
                            //
                            // EXCEPTION (B9): a trailing sampler-level FX annotation
                            // ('… } < string UIName = "x"; >;') is NOT raw HLSL and vkd3d
                            // rejects it, so erase just that span (the rest of the
                            // declaration still passes through verbatim).
                            erasedRanges.AddRange(_samplerAnnotationErasures);
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
                            if (info.TextureReference is null)
                                _synthesizedSamplerTextures[info.Name] = texture;
                            // A referenced texture the main file never declares
                            // ('sampler2D A = sampler_state { Texture = <T>; };' and no 'T'):
                            // mgfxc reads the name off the state block and emits a 'T'
                            // parameter. Recorded as a candidate only; the compiler declares it
                            // when the PREPROCESSED source (includes inlined, macros expanded)
                            // does not, because a header or a macro can declare it.
                            if (info.TextureReference is not null && IsOnlyNamedByTextureStates(info.TextureReference))
                                _undeclaredStateTextures[info.Name] = info.TextureReference;
                            string newDecl = info.TextureReference is null
                                ? $"Texture2D {texture}; SamplerState {info.Name};"
                                : $"SamplerState {info.Name};";

                            replacedRanges.Add((blockStart, declEnd,
                                BuildDeclReplacement(blockStart, declEnd, newDecl)));
                        }
                        else if (_modernMethodSamplers.Contains(info.Name))
                        {
                            // Modern shape (e.g. MonoGame HiDef SpriteEffect): the
                            // declaration is referenced by a modern Texture method
                            // ('Tex.Sample(S, …)'), NOT by tex2D. DXC cannot parse the
                            // FX9 '= sampler_state { … }' initializer, but '.Sample(S, …)'
                            // still needs 'S' declared, so rewrite the whole declaration
                            // to a passthrough 'SamplerState S;' (mirroring the
                            // legacy-intrinsic branch's texture-referenced form). No
                            // Texture2D is synthesized — the modern shader declares its
                            // own 'Texture2D Tex;' and references it directly. If 'S' is
                            // genuinely unused DXC drops it, so a truly-unused sampler is
                            // not in _modernMethodSamplers and stays erased below.
                            int declEnd = _tokenCharOffset[_pos - 1] + _tokens[_pos - 1].Text.Length;
                            replacedRanges.Add((blockStart, declEnd,
                                BuildDeclReplacement(blockStart, declEnd, $"SamplerState {info.Name};")));
                        }
                        else
                        {
                            int blockEnd = _pos < _tokens.Count ? _tokenCharOffset[_pos] : _source.Length;
                            erasedRanges.Add((blockStart, blockEnd));
                        }

                        // The annotation span (if any) is already covered by the
                        // replacement/erasure of the RewriteToSm4 branches and was
                        // erased explicitly in the PreserveSm3 branch above; clear it so
                        // it does not carry over to the next sampler declaration.
                        _samplerAnnotationErasures.Clear();

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
                        _synthesizedSamplerTextures[name] = synth;
                        string newDecl = $"Texture2D {synth}; SamplerState {name};";

                        replacedRanges.Add((blockStart, declEnd,
                            BuildDeclReplacement(blockStart, declEnd, newDecl)));

                        SkipNonCodeTokens();
                        continue;
                    }

                    // Issue #308, preprocessed re-parse only: a bare declaration NOTHING reads
                    // (no legacy intrinsic, no modern Texture method), whose type is a D3D9
                    // sampler type DXC does not have ('sampler2D U;',
                    // 'samplerCUBE U : register(s0);'). Verbatim it stops the compile ("unknown
                    // type name 'sampler2D'"), while fxc/mgfxc accept it and simply drop the
                    // unused object. It becomes a plain 'SamplerState U;', which DXC drops the
                    // same way. Its register still RESERVES (mgfxc does that for an unused
                    // sampler too, measured), and that is read off the preprocessed view of the
                    // raw source, not off this rewritten text.
                    //
                    // Deliberately narrow. One a modern method reads ('Tex.Sample(U, uv)' with
                    // 'sampler2D U;') is left alone: mgfxc rejects that (X3013), so it must not
                    // start compiling here. A misspelt keyword ('samplerstate') is not a D3D9
                    // type and is left alone too.
                    //
                    // Not done on raw source: there the declaration can sit in a branch this
                    // target never compiles, and rewriting it would change the text handed to
                    // the compiler for an effect that compiles today.
                    if (isBareForm && _preprocessed && _mode == FxSourceMode.RewriteToSm4 &&
                        IsD3d9OnlySamplerType(tok.Text) &&
                        !_modernMethodSamplers.Contains(nameTok.Text))
                    {
                        int blockStart = _tokenCharOffset[_pos];
                        (string name, int declEnd) = ConsumeBareSamplerDecl();
                        replacedRanges.Add((blockStart, declEnd,
                            BuildDeclReplacement(blockStart, declEnd, $"SamplerState {name};")));

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
            // Guard: a capitalized 'Texture'/'Texture2D'... keyword can also be the
            // VARIABLE NAME of a modern declaration rather than a legacy 'texture T;'
            // type, and in that position the rewrite must NOT fire (it would turn
            // 'Texture2D Texture : register(t0);' into a broken 'Texture2D Texture2D
            // register;'). The keyword is in NAME position — never a legacy type
            // declaration — exactly when its immediately preceding code token is:
            //   - an Identifier — the preceding type, e.g. 'Texture2D Texture' (B5);
            //   - '>' (RAngle)  — closing a template, e.g. 'Texture2D<float4> Texture'
            //     (the Phase 41 re-parse of the SM4 'DECLARE_TEXTURE' macro expansion).
            // A genuine legacy 'texture'/'Texture' type declaration always sits at a
            // statement boundary (preceded by ';' / '}' / a preprocessor line / start
            // of file), so declining on just these two predecessors is the minimal,
            // lowest-risk discriminator: every previously-rewritten shape is unchanged.
            TokenKind prevKind = PrevCodeKind();
            bool prevIsNamePosition = prevKind is TokenKind.Identifier or TokenKind.RAngle;
            if (_mode == FxSourceMode.RewriteToSm4 &&
                !prevIsNamePosition &&
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
            //
            // CRITICAL: the bare 'Identifier Identifier LAngle' shape ALSO matches a
            // relational/shift/ternary expression inside a function body, because the
            // lexer emits '<' as LAngle, lexes '<=' as 'LAngle Equals' (two tokens), and
            // DROPS '?' and ':'. So 'return value <= 0.5f ? 0.0f : 1.0f;' tokenizes to
            // 'Identifier("return") Identifier("value") LAngle Equals Number Number Number',
            // which without a discriminator was consumed as 'type name <' and then failed in
            // ParseAnnotationBlock with FX0001 ("Expected annotation type but found '='").
            // Gate the annotation path on the GENUINE annotation-block shape: after the '<'
            // a real FX annotation block is either empty ('<' immediately '>') or one or more
            // entries, each 'Type Name = Value ;' — so the first three code tokens after '<'
            // are 'Identifier Identifier Equals' (see ParseAnnotationBlock). A relational/
            // shift/ternary expression never matches that ('value <= …' → Equals; 'a < b ? c : d'
            // → Identifier Identifier Identifier with '?'/':' dropped; 'a << b' → LAngle), so a
            // non-matching shape falls through to the verbatim Consume() at the bottom of the
            // loop and the original operator reaches DXC/vkd3d unchanged.
            //
            // SCOPE GATE (B7): a global-parameter annotation is ALWAYS at declaration
            // scope (brace depth 0). An annotation-shaped relational/ternary expression
            // only occurs INSIDE a function body (depth >= 1) — e.g. 'x[i] < y ? z = w
            // : q;' tokenizes (after the lexer drops '?'/':'/'['/']') to 'x i < y z = w
            // q', whose 'y z =' tail satisfies IsAnnotationBlockStart and would be
            // misparsed as an annotation. Firing the strip only at depth 0 makes that
            // structurally impossible, hardening the whole #106 class; IsAnnotationBlockStart
            // remains as a second layer for the depth-0 shapes.
            if (braceDepth == 0 && tok.Kind == TokenKind.Identifier)
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

                    if (Peek(la2).Kind == TokenKind.LAngle && IsAnnotationBlockStart(la2))
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
            //
            // The rewrite is DEFERRED (B6), not applied here: it must NOT fire on a
            // VERTEX-shader entry, whose ': COLOR' is legal (e.g. a VS writing POSITION
            // via an 'out' parameter). We record the candidate plus the enclosing
            // function name and resolve VS entries after the whole file is scanned.
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.RParen && TryMatchColorReturnSemantic(out int colorTokIdx, out string replacement))
            {
                int colorStart = _tokenCharOffset[colorTokIdx];
                var colorTok = _tokens[colorTokIdx];
                int colorEnd = colorStart + colorTok.Text.Length;
                deferredColorRewrites.Add((colorStart, colorEnd, replacement, EnclosingFunctionName()));

                // Consume only the RParen; let the loop continue past the COLOR token
                // naturally so any subsequent COLOR-return on a non-entry helper is also
                // caught. (We don't fast-forward past the LBrace because the function
                // body still needs to be in stripped output.)
                Consume();
                SkipNonCodeTokens();
                continue;
            }

            // Legacy sampling intrinsics with no rewrite (the 1D/3D/CUBE families, which
            // need a resource type the sampler rewrite does not synthesize). Fail loudly
            // with a targeted diagnostic naming the intrinsic instead of letting DXC die
            // later with a misleading 'unknown identifier'. Only an actual CALL trips
            // this; a user variable that merely shares the name does not. PreserveSm3
            // (FNA) passes these through verbatim — vkd3d compiles them natively.
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.Identifier && UnsupportedLegacyIntrinsics.Contains(tok.Text) &&
                Peek(NextCodeOffset(1)).Kind == TokenKind.LParen)
            {
                return Fail<FxParseResult>(FxParseErrorCode.UnsupportedLegacyIntrinsic,
                    $"The legacy D3D9 sampling intrinsic '{tok.Text}' is not supported on this " +
                    "target: ShadowDusk rewrites the 2D sampler family (tex2D, tex2Dgrad, " +
                    "tex2Dlod, tex2Dbias, tex2Dproj) to modern Texture2D methods, but not the " +
                    "1D, 3D and cube families. Declare the texture as Texture1D / Texture3D / " +
                    "TextureCube with a SamplerState and call its method instead (e.g. " +
                    "texCUBE(s, d) becomes T.Sample(s, d); tex3Dlod(s, t) becomes " +
                    "T.SampleLevel(s, t.xyz, t.w)). The FNA (fx_2_0) target compiles this " +
                    "intrinsic natively.", tok);
            }

            // tex2Dlod / tex2Dbias / tex2Dproj: the coordinate's w component carries the
            // LOD, the bias or the projective divisor, so the call becomes a call of a
            // helper defined right before the enclosing declaration (see
            // RestructuredLegacySampleIntrinsics). Only the span from the intrinsic name
            // through its '(' is replaced; the sampler and coordinate arguments are copied
            // verbatim, so a nested call, swizzle or macro in them is untouched and any
            // later rewrite inside them (a nested tex2D) still applies. A sampler not in
            // the binding map (a function parameter, or one an #include or a macro declares)
            // is left alone exactly like tex2D's: the legacy-sampler recovery then repeats
            // the pre-parse on the preprocessed source.
            if (_mode == FxSourceMode.RewriteToSm4 &&
                tok.Kind == TokenKind.Identifier &&
                RestructuredLegacySampleIntrinsics.ContainsKey(tok.Text) &&
                TryMatchTexSampleArgument(out string restructuredSampler) &&
                TryGetBoundTexture(restructuredSampler, out string? restructuredTexture))
            {
                Result<(int Start, int End, string Replacement)?, FxParseError> call =
                    RewriteRestructuredIntrinsic(tok, restructuredTexture, braceDepth);
                if (call.IsFailure)
                    return Result<FxParseResult, FxParseError>.Fail(call.Error);

                // A null rewrite leaves the call verbatim, exactly as an unbound sampler's is
                // left: a shape whose answer depends on which #if branch compiles (see
                // RewriteRestructuredIntrinsic). On the raw text the compile then fails and the
                // legacy-sampler recovery rewrites the PREPROCESSED source, where every
                // conditional is resolved.
                if (call.Value is { } rewrite)
                    replacedRanges.Add(rewrite);

                Consume();
                SkipNonCodeTokens();
                continue;
            }

            // SM4+ resource/sampler types that the FNA (fx_2_0) target cannot lower. vkd3d's
            // SM1 backend does not reject these cleanly: a SamplerComparisonState makes
            // hlsl_sm1_base_type log "Invalid dimension" then hit "Unreachable code reached",
            // which takes the whole PROCESS down with an AccessViolationException (confirmed
            // 2026-07-22 on MonoGame's own CustomSpriteBatchEffectComparisonSampler.fx). A
            // crash gives the user nothing, so catch it here and fail loudly instead —
            // ShadowDusk's "fail loudly with diagnostics" rule. Non-FNA targets compile these
            // fine and never reach this check.
            if (_mode == FxSourceMode.PreserveSm3 &&
                tok.Kind == TokenKind.Identifier &&
                UnsupportedFx2ResourceTypes.Contains(tok.Text))
            {
                return Fail<FxParseResult>(FxParseErrorCode.UnsupportedSm4TypeForFx2,
                    $"'{tok.Text}' is a shader-model 4+ type and has no equivalent in the D3D9 " +
                    "fx_2_0 profile the FNA target compiles to. Use a plain sampler/sampler2D " +
                    "(and tex2D) for the FNA target, or compile this effect for the DirectX_11 / " +
                    "Vulkan targets, which support it.", tok);
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
                TryGetBoundTexture(samplerArg, out string? boundTexture))
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

            // Everything else: copy verbatim (advance past single token). Track HLSL
            // body brace depth (B7) as the verbatim path passes '{' / '}' — only braces
            // that reach the main loop (function / struct / cbuffer bodies, global
            // initializer lists) are counted; technique / pass / sampler bodies are
            // consumed inside their own parsers and never seen here.
            if (tok.Kind == TokenKind.LBrace)
                braceDepth++;
            else if (tok.Kind == TokenKind.RBrace && braceDepth > 0)
                braceDepth--;
            Consume();
            SkipNonCodeTokens();
        }

        // Apply the deferred ': COLOR' -> ': SV_Target' rewrites (B6), skipping any
        // whose enclosing function is a vertex-shader entry (its ': COLOR' is a valid
        // VS output semantic that the rewrite would break).
        foreach (var (start, end, replacement, function) in deferredColorRewrites)
        {
            if (function is not null && vertexEntryNames.Contains(function))
                continue;
            replacedRanges.Add((start, end, replacement));
        }

        // The tex2Dlod / tex2Dbias / tex2Dproj helpers: zero-length insertions at the first
        // token of each declaration that calls one. The text holds no newline, so every line
        // keeps its number; only that line's columns move, as a tex2D rewrite moves its own.
        foreach ((int declStart, StringBuilder helpers) in _restructuredHelperText)
        {
            bool Inside(int offset) =>
                erasedRanges.Exists(r => r.Start < offset && offset < r.End) ||
                replacedRanges.Exists(r => r.Start < offset && offset < r.End);

            // Preferred position: the start of the line ABOVE the declaration, when that line
            // holds nothing but whitespace or comments (the usual blank or comment line between
            // functions), so not one column of the author's code moves. That line holds no
            // directive, so it sits in the same conditional branch as the declaration. Otherwise
            // the helper goes right before the declaration, on its first line.
            // Second choice: right after the ';' / '}' that ended the previous declaration, when
            // only whitespace and comments (no directive) lie between it and this declaration's
            // line: the helper then shares the end of that line, again moving no code column.
            int boundaryEnd = _restructuredBoundaryEnd.GetValueOrDefault(declStart, -1);
            int insertAt =
                LineAboveIfFree(declStart, boundaryEnd) is int above && !Inside(above) ? above :
                boundaryEnd >= 0 && OnlyTriviaAcrossALine(boundaryEnd, declStart) && !Inside(boundaryEnd) ? boundaryEnd :
                declStart;

            // Defensive: the declaration start is the first code character after a top-level
            // ';' or '}', which no erasure or replacement spans. Should the brace tracking ever
            // place it inside one, the insertion would be lost or duplicated, so refuse.
            if (Inside(insertAt))
            {
                int atIndex = Array.IndexOf(_tokenCharOffset, declStart);
                Token at = atIndex >= 0 ? _tokens[atIndex] : _tokens[^1];
                return Fail<FxParseResult>(FxParseErrorCode.UnsupportedLegacyIntrinsic,
                    "ShadowDusk could not find a global-scope position before this declaration for the " +
                    "helper its tex2Dlod / tex2Dbias / tex2Dproj call is rewritten to. Use the modern " +
                    "form instead (tex2Dlod(s, t) becomes T.SampleLevel(s, t.xy, t.w)).", at);
            }
            replacedRanges.Add((insertAt, insertAt, insertAt == boundaryEnd ? " " + helpers : helpers.ToString()));
        }

        string strippedHlsl = BuildStrippedOutput(erasedRanges, replacedRanges);

        // Re-key the captured `register(sN)` indices from SAMPLER name onto TEXTURE name,
        // which is what the OpenGL sampler table joins on (issue #189). A sampler whose
        // texture is unknown is dropped rather than guessed: the consumer of this map falls
        // back to declaration-index allocation, which is the behaviour that shipped before.
        var explicitGlSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string samplerName, int slot) in _explicitSamplerRegisters)
        {
            if (_samplerTextureBindings.TryGetValue(samplerName, out string? textureName))
                explicitGlSlots[textureName] = slot;
        }

        return Result<FxParseResult, FxParseError>.Ok(new FxParseResult
        {
            StrippedHlsl = strippedHlsl,
            Techniques = techniques,
            Samplers = samplers,
            ParameterAnnotations = paramAnnotations,
            ExplicitGlSamplerSlots = explicitGlSlots,
            LegacySamplerTextures = new Dictionary<string, string>(_samplerTextureBindings, StringComparer.Ordinal),
            SynthesizedSamplerTextures = new Dictionary<string, string>(_synthesizedSamplerTextures, StringComparer.Ordinal),
            UndeclaredStateTextures = new Dictionary<string, string>(_undeclaredStateTextures, StringComparer.Ordinal),
            ReservedGlSamplerSlots = CollectReservedSamplerRegisters(),
            DeferredLegacyIntrinsicCalls = _deferredRestructuredCalls.ToArray(),
            RestructuredHelpers = LocateRestructuredHelpers(strippedHlsl),
            SamplerParameterTextures = SamplerParameterTexturesResult(),
        });
    }

    // -------------------------------------------------------------------------
    // Technique parsing
    // -------------------------------------------------------------------------

    private Result<TechniqueInfo, FxParseError> ParseTechnique()
    {
        var startTok = Peek();
        // technique10 (fx_4_0) parses identically to technique11 — fxc accepts both and
        // ShadowDusk's own IdentifierSafety reserves the word, but the block used to pass
        // through unrecognized and leak its pass body into the DXC input (bug-hunt
        // 2026-07-27 M14).
        bool isEffect11 = !string.Equals(startTok.Text, "technique", StringComparison.OrdinalIgnoreCase);
        Consume(); // consume "technique"/"technique10"/"technique11"

        SkipNonCodeTokens();

        // Technique name is OPTIONAL, exactly like the pass name below: `technique { ... }`
        // and `technique < annotations > { ... }` are legal FX and are what MonoGame's own
        // test effects use (Tests/Assets/Effects/CustomSpriteBatchEffect.fx, Instancing.fx,
        // ParserTest.fx, …). Verified against the reference compiler: mgfxc 3.8.5 compiles
        // them and writes an EMPTY technique name into the container, so an anonymous
        // technique carries "" here too rather than a synthesized identifier.
        string name;
        var nameTok = Peek();
        if (nameTok.Kind == TokenKind.Identifier)
        {
            name = nameTok.Text;
            Consume();
        }
        else if (nameTok.Kind is TokenKind.LBrace or TokenKind.LAngle)
        {
            name = string.Empty;
        }
        else
        {
            return Fail<TechniqueInfo>(FxParseErrorCode.UnexpectedToken,
                $"Expected technique name but found '{nameTok.Text}'", nameTok);
        }

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

            var passResult = ParsePass();
            if (passResult.IsFailure)
                return Result<TechniqueInfo, FxParseError>.Fail(passResult.Error);

            // Several anonymous passes in one technique are legal (mgfxc 3.8.4.1 and fxc
            // fx_2_0 both compile `pass { ... } pass { ... }`); they all carry "", so only a
            // named pass can collide.
            var pass = passResult.Value;
            if (pass.Name.Length > 0 && !passNames.Add(pass.Name))
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

    private Result<PassInfo, FxParseError> ParsePass()
    {
        var startTok = Peek();
        Consume(); // "pass"

        SkipNonCodeTokens();

        // Pass name is optional — anonymous passes (pass { ... }) are legal in FX9. Like an
        // anonymous technique, an anonymous pass carries an EMPTY name, which is what the
        // reference compilers write: mgfxc 3.8.4.1 stores "" in MGFX (OpenGL and DirectX_11),
        // KNI's own pipeline stores "" in KNIFX, and fxc fx_2_0 writes an empty string. The
        // runtimes then report EffectPass.Name == "". ShadowDusk used to synthesize "P<index>".
        string name;
        var nameTok = Peek();
        if (nameTok.Kind == TokenKind.Identifier)
        {
            name = nameTok.Text;
            Consume();
        }
        else if (nameTok.Kind == TokenKind.LBrace)
        {
            name = string.Empty;
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
        string? vertexProfileToken = null, pixelProfileToken = null;
        SourceSpan? vertexProfileSpan = null, pixelProfileSpan = null;
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

            // Reject the stages the consumer runtime has nowhere to put, BEFORE the
            // render-state path chokes on the 'compile' expression and blames punctuation.
            // Checked at the stage keyword so the caret lands on the real problem, which is
            // also where mgfxc's own (much less helpful) message points.
            if (UnloadableShaderStages.TryGetValue(key, out string? stageName))
            {
                return Fail<PassInfo>(FxParseErrorCode.UnsupportedShaderStage,
                    $"'{key}' assigns a {stageName} shader, which the consumer runtime cannot " +
                    "load: MonoGame's and KNI's Effect has exactly two shader stages, vertex " +
                    "and pixel, so no compiler can produce an effect containing one. This is a " +
                    "runtime limit, not a ShadowDusk gap; mgfxc rejects this effect too. " +
                    $"Remove the '{key}' assignment, or express the work in the vertex or pixel " +
                    "stage. (MonoGame issues #4567 and #7533 request these stages upstream and " +
                    "remain open and undesigned; the cpt-max/MonoGame fork does run them, but " +
                    "writes a different effect container stock MonoGame cannot read.)", keyTok);
            }

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
                // fxc parity (bug-hunt 2026-07-27 M14): `VertexShader = NULL;` is the
                // D3D9 idiom for "no shader bound to this stage in this pass" (the app
                // binds one at runtime, or the stage is fixed-function). fxc and mgfxc
                // accept it; treat it exactly like an absent assignment.
                if (PeekIsKeyword("NULL"))
                {
                    Consume(); // NULL
                    SkipNonCodeTokens();
                    var nullSemi = Expect(TokenKind.Semicolon);
                    if (nullSemi.IsFailure)
                        return Result<PassInfo, FxParseError>.Fail(nullSemi.Error);
                    SkipNonCodeTokens();
                    continue;
                }

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

                string profileToken = profileTok.Text;
                string profile = profileToken.ToLowerInvariant();
                var profileSpan = new SourceSpan(
                    profileTok.Line, profileTok.Column,
                    profileTok.Line, profileTok.Column + profileTok.Text.Length);
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

                if (isVs) { vertexEntry = entry; vertexProfile = profile; vertexProfileToken = profileToken; vertexProfileSpan = profileSpan; }
                else { pixelEntry = entry; pixelProfile = profile; pixelProfileToken = profileToken; pixelProfileSpan = profileSpan; }
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
                var lastValueTok = valueTok;
                Consume();
                SkipNonCodeTokens();

                // ColorWriteEnable flag masks: 'Red | Green | Blue' loses its '|'
                // separators in the lexer and arrives as adjacent Identifier tokens.
                // For these keys only, accumulate the consecutive identifiers and
                // re-join them with '|' so the value is captured whole (otherwise the
                // ';' check below would fail on the second flag, FX0008). Every other
                // render state is a single token, so this never affects them.
                if (ColorWriteMaskKeys.Contains(key) && valueTok.Kind == TokenKind.Identifier)
                {
                    while (Peek().Kind == TokenKind.Identifier)
                    {
                        lastValueTok = Peek();
                        value += "|" + lastValueTok.Text;
                        Consume();
                        SkipNonCodeTokens();
                    }
                }

                var valueSpan = new SourceSpan(keyTok.Line, keyTok.Column,
                    lastValueTok.Line, lastValueTok.Column + lastValueTok.Text.Length);

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
            VertexProfileToken = vertexProfileToken,
            PixelProfileToken = pixelProfileToken,
            VertexProfileSpan = vertexProfileSpan,
            PixelProfileSpan = pixelProfileSpan,
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
        // lexer drops the ':', leaving 'register' '(' … ')'). The index is RECORDED
        // (issue #189): the SM4 rewrite drops this clause, so it is the last point at
        // which the register exists. TryReadSamplerRegister consumes the whole clause
        // when it matches the exact `register ( sN )` shape; anything else falls
        // through to the original permissive skip below, unchanged.
        if (PeekIsKeyword("register"))
        {
            if (TryReadSamplerRegister() is { } explicitSlot)
            {
                _explicitSamplerRegisters[name] = explicitSlot;
                SkipNonCodeTokens();
            }
            else
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
                    // fxc parity (bug-hunt 2026-07-27 M14): `Texture = NULL;` means "the
                    // app binds the texture at runtime". Leave the reference unset so the
                    // sampler binds its synthesized texture parameter, instead of carrying
                    // a literal identifier named NULL into the rewritten HLSL
                    // (`NULL.Sample(...)` — an undeclared-identifier DXC error).
                    textureRef = IsNullKeyword(texTok.Text) ? null : texTok.Text;
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
                    // Same NULL semantics as the parenthesized form above.
                    textureRef = IsNullKeyword(texTok.Text) ? null : texTok.Text;
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

        // Optional sampler-level FX annotation block after the '}':
        //   'sampler2D S = sampler_state { … } < string UIName = "x"; >;'
        // fxc accepts an annotation here exactly as on any other effect variable.
        // Consume (and validate) it so the trailing ';' check below still succeeds;
        // the annotation contributes no SamplerInfo. The captured span is erased in
        // EVERY mode (recorded here, applied by the ParseFile caller): in
        // RewriteToSm4 the whole declaration span (through the trailing ';') is
        // already erased or replaced, and in PreserveSm3 the annotation must be
        // erased on its own because the rest of the declaration passes through
        // verbatim and vkd3d does not accept an FX annotation on a sampler.
        if (Peek().Kind == TokenKind.LAngle)
        {
            int annotStart = _tokenCharOffset[_pos]; // points to '<'
            var samplerAnnot = ParseAnnotationBlock();
            if (samplerAnnot.IsFailure)
                return Result<SamplerInfo, FxParseError>.Fail(samplerAnnot.Error);
            int annotEnd = _pos < _tokens.Count ? _tokenCharOffset[_pos] : _source.Length;
            _samplerAnnotationErasures.Add((annotStart, annotEnd));
            SkipNonCodeTokens();
        }

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
        // Replacements are produced only for token spans that live outside any
        // erased block, so they cannot overlap. A zero-length replacement (an
        // insertion: the tex2Dlod-family helpers) can share its offset with the
        // span that starts there, so at equal offsets the shorter edit goes first:
        // the insertion lands BEFORE that span's own text or replacement.
        var edits = new List<(int Start, int End, string? Replacement)>(
            mergedErased.Count + replacedRanges.Count);
        foreach (var (s, e) in mergedErased)
            edits.Add((s, e, null));
        foreach (var (s, e, r) in replacedRanges)
            edits.Add((s, e, r));
        edits.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : (a.End - a.Start).CompareTo(b.End - b.Start));

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

    /// <summary>
    /// Read-only scan for <c>SamplerState &lt;name&gt; : register(sN)</c> — a MODERN sampler
    /// declaration carrying an explicit register. Returns the set of N found (issue #189).
    ///
    /// <para><b>Why these are RESERVED rather than assigned.</b> Compiling for OpenGL means
    /// compiling at <c>ps_3_0</c>, where a texture and a sampler are ONE object with one register
    /// namespace. A modern <c>SamplerState</c> still occupies its declared sampler register, but
    /// the combined samplers fxc has to synthesize for each (texture, sampler) pair cannot reuse
    /// it — so they are allocated around it. Measured against the pinned mgfxc, and this is the
    /// rule the whole allocation follows:</para>
    /// <list type="bullet">
    ///   <item><description>no explicit register  → pairs get 0, 1, 2 …</description></item>
    ///   <item><description><c>S : register(s1)</c>, 2 textures → <c>ps_s0</c>, <c>ps_s2</c> (1 skipped)</description></item>
    ///   <item><description><c>S : register(s1)</c>, 3 textures → <c>ps_s0</c>, <c>ps_s2</c>, <c>ps_s3</c></description></item>
    ///   <item><description><c>P : register(s0)</c> + <c>Q : register(s1)</c> → <c>ps_s2</c>, <c>ps_s3</c></description></item>
    ///   <item><description><c>P : register(s2)</c> + <c>Q : register(s3)</c> → <c>ps_s0</c>, <c>ps_s1</c></description></item>
    ///   <item><description>one texture, <c>S : register(s0)</c> → <c>ps_s1</c></description></item>
    /// </list>
    ///
    /// <para>This is a separate READ-ONLY pass over the token list precisely so it cannot perturb
    /// the rewrite: a shape it fails to recognise costs an allocation difference, never a parse.
    /// It requires the exact <c>SamplerState IDENT register ( sN )</c> token shape, which a
    /// function parameter (<c>float4 f(SamplerState s, …)</c>) cannot match because no
    /// <c>register</c> clause follows.</para>
    /// </summary>
    private HashSet<int> CollectReservedSamplerRegisters() => CollectReservedSamplerRegisters(_tokens);

    /// <summary>
    /// The OpenGL sampler registers reserved by modern <c>SamplerState X : register(sN)</c>
    /// declarations, decided on the PREPROCESSED source, the way <c>mgfxc</c> decides it
    /// (issue #283).
    ///
    /// <para><see cref="FxParseResult.ReservedGlSamplerSlots"/> is read from the tokens the
    /// pre-parser was given, which for a compile is the raw source: a register that only exists in
    /// an inactive <c>#if</c> branch is counted there, one written through a macro
    /// (<c>#define SLOT(n) : register(n)</c>) is missed, and one in an <c>#include</c>d file is
    /// never seen. This entry point first builds the preprocessed view of
    /// <paramref name="flattenedSource"/> (conditionals evaluated, macros expanded, with the
    /// <c>#define</c>s the flattener prepended for the target and the user's defines), then runs
    /// the same declaration matcher over it. It is pure managed code, so the answer is identical
    /// on every host, the browser included.</para>
    /// </summary>
    /// <param name="flattenedSource">
    /// The effect source with <c>#include</c>s inlined and the compile's macros prepended as
    /// <c>#define</c> lines (the output of <c>ShadowDusk.Core.Preprocessor.Preprocessor.Flatten</c>).
    /// </param>
    /// <param name="sourceFile">Display name used in diagnostics.</param>
    /// <returns>
    /// The reserved register indices, or an <c>SD0009</c> error when the preprocessed view
    /// cannot be built (a directive or <c>#if</c> expression this preprocessor does not model).
    /// </returns>
    public static Result<IReadOnlySet<int>, ShaderError> CollectReservedGlSamplerSlots(
        string flattenedSource, string sourceFile)
    {
        var tokens = PreprocessedViewTokens(flattenedSource, sourceFile);
        if (tokens.IsFailure)
            return Result<IReadOnlySet<int>, ShaderError>.Fail(tokens.Error);

        return Result<IReadOnlySet<int>, ShaderError>.Ok(CollectReservedSamplerRegisters(tokens.Value));
    }

    /// <summary>
    /// Both halves of the OpenGL sampler-register decision, read off ONE preprocessed view of the
    /// source, the way <c>mgfxc</c> reads them (issues #283 and #299):
    /// <list type="bullet">
    ///   <item><description><see cref="GlSamplerSlots.Reserved"/>: the registers modern
    ///   <c>SamplerState X : register(sN)</c> declarations take out of circulation, exactly what
    ///   <see cref="CollectReservedGlSamplerSlots"/> returns.</description></item>
    ///   <item><description><see cref="GlSamplerSlots.Explicit"/>: texture name -> the register an
    ///   explicit <c>register(sN)</c> on its LEGACY sampler declaration pins it to, the
    ///   preprocessed counterpart of <see cref="FxParseResult.ExplicitGlSamplerSlots"/>.</description></item>
    /// </list>
    ///
    /// <para><b>Why the explicit map needs the preprocessed view too (issue #299).</b> The
    /// pre-parser records a legacy sampler's register from the raw tokens, so it sees both arms
    /// of an <c>#if</c> and no macro expansion. Measured against the pinned <c>mgfxc</c> 3.8.4.1
    /// <c>/Profile:OpenGL</c>: <c>#if OPENGL</c> / <c>sampler S = sampler_state { … };</c> /
    /// <c>#else</c> / <c>sampler S : register(s1) = sampler_state { … };</c> is <c>ps_s0</c>
    /// (the raw reading said <c>ps_s1</c>, off <c>SpriteBatch</c>'s unit 0), and
    /// <c>#define REG s1</c> / <c>sampler S : register(REG);</c> is <c>ps_s1</c> (the raw reading
    /// missed it: <c>ps_s0</c>).</para>
    ///
    /// <para><b>Which declarations count</b> is unchanged from the raw reading, only the text is
    /// different: a sampler-type keyword, the name, then the exact <c>register ( sN )</c> clause;
    /// and only a sampler the SM4 rewrite bound to a texture (one a legacy intrinsic such as
    /// <c>tex2D</c> reads) gets an entry. The sampler-to-texture join comes from
    /// <paramref name="parsed"/>, the parse of the source that is actually compiled, because that
    /// is the texture the rewritten HLSL samples through. Both names of that join are resolved
    /// through the preprocessor first, so a sampler or texture whose NAME is a macro
    /// (<c>#define SAMP MySampler</c> / <c>sampler SAMP : register(s1);</c>) still finds its
    /// declaration in the view and its texture in the SPIR-V.</para>
    /// </summary>
    /// <param name="flattenedSource">
    /// The RAW effect source with <c>#include</c>s inlined and the compile's macros prepended as
    /// <c>#define</c> lines (the output of <c>ShadowDusk.Core.Preprocessor.Preprocessor.Flatten</c>).
    /// </param>
    /// <param name="sourceFile">Display name used in diagnostics.</param>
    /// <param name="parsed">The pre-parse of the same effect that feeds the compile.</param>
    /// <returns>
    /// Both maps, or an <c>SD0009</c> error when the preprocessed view cannot be built.
    /// </returns>
    public static Result<GlSamplerSlots, ShaderError> CollectGlSamplerSlots(
        string flattenedSource, string sourceFile, FxParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        // The raw parse knows each sampler and texture by the token the author wrote. Either can
        // itself be a macro (`#define SAMP MySampler`), in which case the view, and the compiled
        // SPIR-V, only ever show what it expands to. So the names are resolved through the same
        // preprocessor before they are used as join keys.
        var rawNames = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string samplerName, string textureName) in parsed.LegacySamplerTextures)
        {
            rawNames.Add(samplerName);
            rawNames.Add(textureName);
        }

        var view = Preprocessing.FxMacroPreprocessor.Process(flattenedSource, sourceFile, rawNames);
        if (view.IsFailure)
            return Result<GlSamplerSlots, ShaderError>.Fail(view.Error);

        IReadOnlyList<Token> tokens = new FxLexer(view.Value.View, sourceFile).Tokenize();
        IReadOnlyDictionary<string, string> resolved = view.Value.Resolved;

        // A name that expands to anything but one identifier is not a name the view can be
        // searched for; it keeps its raw spelling (and then simply finds no register).
        string ViewName(string rawName) =>
            resolved.TryGetValue(rawName, out string? expansion) && IsIdentifier(expansion) ? expansion : rawName;

        // Re-key SAMPLER name -> register onto TEXTURE name, as the raw parse does for
        // FxParseResult.ExplicitGlSamplerSlots: a sampler with no register in the view is not
        // pinned, and the allocator falls back to declaration order for it.
        Dictionary<string, int> registers = CollectLegacySamplerRegisters(tokens);
        var explicitSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        var legacyRegisters = new Dictionary<string, int>(StringComparer.Ordinal);
        var legacyTextures = new HashSet<string>(StringComparer.Ordinal);
        var legacySamplerTextures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string samplerName, string textureName) in parsed.LegacySamplerTextures)
        {
            legacyTextures.Add(ViewName(textureName));
            legacySamplerTextures[ViewName(samplerName)] = ViewName(textureName);
            if (registers.TryGetValue(ViewName(samplerName), out int slot))
            {
                explicitSlots[ViewName(textureName)] = slot;
                legacyRegisters[ViewName(samplerName)] = slot;
            }
        }

        // Issue #447: every sampler's state-block texture in the view, the native ones included; a
        // direct use of any of them is looked for, not only of the textures the rewrite bound.
        Dictionary<string, string> stateTextures = CollectStateTextures(tokens);
        var directlyUsable = new HashSet<string>(legacyTextures, StringComparer.Ordinal);
        directlyUsable.UnionWith(stateTextures.Values);

        return Result<GlSamplerSlots, ShaderError>.Ok(
            new GlSamplerSlots(explicitSlots, CollectReservedSamplerRegisters(tokens))
            {
                LegacySamplerRegisters = legacyRegisters,
                LegacyTextures = legacyTextures,
                LegacySamplerTextures = legacySamplerTextures,
                EntryPointInterpolants = GlEntryPointInterpolantScanner.Scan(tokens),
                ExplicitTextureRegisters = CollectTextureRegisters(tokens),
                ModernMethodTextures = CollectMethodObjects(tokens, directlyUsable),
                ValueUseTextures = CollectValueUses(tokens, directlyUsable),
                StateTextures = stateTextures,
            });
    }

    /// <summary>
    /// The names in <paramref name="textures"/> that the source uses as the object of a method call
    /// (<c>T.Method(</c>, not itself a member <c>x.T.Method(</c>). See
    /// <see cref="GlSamplerSlots.ModernMethodTextures"/>.
    /// </summary>
    private static HashSet<string> CollectMethodObjects(IReadOnlyList<Token> tokens, IReadOnlySet<string> textures)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var code = new List<Token>(tokens.Count);
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor))
                code.Add(t);
        }
        for (int i = 0; i + 3 < code.Count; i++)
        {
            if (code[i].Kind == TokenKind.Identifier && textures.Contains(code[i].Text)
                && (i == 0 || code[i - 1].Kind != TokenKind.Dot)
                && code[i + 1].Kind == TokenKind.Dot && code[i + 2].Kind == TokenKind.Identifier
                && code[i + 3].Kind == TokenKind.LParen)
            {
                found.Add(code[i].Text);
            }
        }
        return found;
    }

    /// <summary>
    /// Sampler -> the texture its state block names, for every <c>name [: register(…)] = sampler_state
    /// { … Texture = &lt;T&gt; … }</c> and brace form <c>name [: register(…)] { … }</c> in
    /// <paramref name="tokens"/> (the lexer drops the ':'). A sampler named twice keeps its last
    /// texture; one whose name cannot be found before its block is skipped.
    /// </summary>
    private static Dictionary<string, string> CollectStateTextures(IReadOnlyList<Token> tokens)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var code = new List<Token>(tokens.Count);
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor or TokenKind.EOF))
                code.Add(t);
        }
        for (int i = 0; i + 2 < code.Count; i++)
        {
            if (code[i].Kind != TokenKind.Identifier || !string.Equals(code[i].Text, "Texture", StringComparison.OrdinalIgnoreCase)
                || code[i + 1].Kind != TokenKind.Equals)
            {
                continue;
            }
            int k = i + 2;
            if (code[k].Kind is TokenKind.LAngle or TokenKind.LParen)
                k++;
            if (k >= code.Count || code[k].Kind != TokenKind.Identifier)
                continue;
            string texture = code[k].Text;

            // The '{' that opens the block, then the sampler's name before it.
            int open = i - 1, depth = 0;
            for (; open >= 0; open--)
            {
                if (code[open].Kind == TokenKind.RBrace) depth++;
                else if (code[open].Kind == TokenKind.LBrace && depth-- == 0) break;
            }
            int n = open - 1;
            if (n >= 0 && code[n].Kind == TokenKind.Identifier && string.Equals(code[n].Text, "sampler_state", StringComparison.OrdinalIgnoreCase))
            {
                n--;
                if (n < 0 || code[n].Kind != TokenKind.Equals)
                    continue;
                n--;
            }
            if (n >= 0 && code[n].Kind == TokenKind.RParen)
            {
                while (n >= 0 && code[n].Kind != TokenKind.LParen) n--;
                n -= 2; // '(' and 'register'
            }
            if (n >= 1 && code[n].Kind == TokenKind.Identifier && code[n - 1].Kind == TokenKind.Identifier
                && SamplerTypeKeywords.Contains(code[n - 1].Text))
            {
                found[code[n].Text] = texture;
            }
        }
        return found;
    }

    /// <summary>
    /// The names in <paramref name="textures"/> the source uses as a VALUE (issue #447): every
    /// reference but a declaration (after a <c>texture*</c> type, a template's <c>&gt;</c>, or a
    /// <c>,</c> at global scope outside any bracket, a declarator list), the binding of a state block
    /// (<c>Texture = &lt;T&gt;</c>, <c>Texture = (T)</c>, <c>Texture = T</c>, <c>Texture[0] = &lt;T&gt;</c>), the
    /// object of a method call (<see cref="CollectMethodObjects"/>) and a member (<c>x.T</c>). See
    /// <see cref="GlSamplerSlots.ValueUseTextures"/>.
    /// </summary>
    private static HashSet<string> CollectValueUses(IReadOnlyList<Token> tokens, IReadOnlySet<string> textures)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var code = new List<Token>(tokens.Count);
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor or TokenKind.EOF))
                code.Add(t);
        }
        int braces = 0, parens = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Token t = code[i];
            switch (t.Kind)
            {
                case TokenKind.LBrace: braces++; continue;
                case TokenKind.RBrace: braces = Math.Max(0, braces - 1); continue;
                case TokenKind.LParen: parens++; continue;
                case TokenKind.RParen: parens = Math.Max(0, parens - 1); continue;
            }
            if (t.Kind != TokenKind.Identifier || !textures.Contains(t.Text))
                continue;
            Token? prev = i > 0 ? code[i - 1] : null;
            if (prev is { Kind: TokenKind.Dot })
                continue; // a member of something else
            if (i + 3 < code.Count && code[i + 1].Kind == TokenKind.Dot && code[i + 2].Kind == TokenKind.Identifier
                && code[i + 3].Kind == TokenKind.LParen)
            {
                continue; // a method call: CollectMethodObjects
            }
            bool declaration =
                (prev is { Kind: TokenKind.Identifier } && prev.Text.StartsWith("texture", StringComparison.OrdinalIgnoreCase))
                || prev is { Kind: TokenKind.RAngle } && i + 1 < code.Count && code[i + 1].Kind is TokenKind.Semicolon or TokenKind.Identifier or TokenKind.LAngle or TokenKind.Comma
                || (prev is { Kind: TokenKind.Comma } && braces == 0 && parens == 0);
            if (declaration || IsStateTextureBinding(code, i) || IsStateKey(code, i, braces))
                continue;
            found.Add(t.Text);
        }
        return found;

        // The KEY of a state assignment, `{ Texture = <T>; }` or `Texture[0] = ...` (the lexer drops
        // the brackets): the classic XNA texture is itself named `Texture`, and its state key must
        // not read as a value use of it.
        static bool IsStateKey(List<Token> code, int i, int braces)
        {
            if (braces == 0 || i == 0 || code[i - 1].Kind is not (TokenKind.LBrace or TokenKind.Semicolon))
                return false;
            int k = i + 1;
            if (k < code.Count && code[k].Kind == TokenKind.Number)
                k++;
            return k < code.Count && code[k].Kind == TokenKind.Equals;
        }

        // `Texture = <T>`, `Texture = (T)`, `Texture = T`, `Texture[0] = <T>` (the lexer drops '[' and ']').
        static bool IsStateTextureBinding(List<Token> code, int i)
        {
            int k = i - 1;
            if (k >= 0 && code[k].Kind is TokenKind.LAngle or TokenKind.LParen)
                k--;
            if (k < 0 || code[k].Kind != TokenKind.Equals)
                return false;
            k--;
            if (k >= 0 && code[k].Kind == TokenKind.Number)
                k--;
            return k >= 0 && code[k].Kind == TokenKind.Identifier && string.Equals(code[k].Text, "Texture", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Every declaration of a resource that lives in a <c>t</c> register (any <c>texture*</c>
    /// type in any case, <c>Buffer</c>, <c>StructuredBuffer</c>, <c>ByteAddressBuffer</c>,
    /// <c>tbuffer</c>; optional template arguments) with an explicit
    /// <c>register([vs|ps,] tN [, spaceM])</c>, in declaration order. See
    /// <see cref="GlSamplerSlots.ExplicitTextureRegisters"/>.
    /// </summary>
    private static List<ExplicitTextureRegister> CollectTextureRegisters(IReadOnlyList<Token> tokens)
    {
        var registers = new List<ExplicitTextureRegister>();

        var code = new List<Token>(tokens.Count);
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor))
                code.Add(t);
        }

        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Kind != TokenKind.Identifier || !IsTRegisterResourceType(code[i].Text))
                continue;

            int k = i + 1;
            if (k < code.Count && code[k].Kind == TokenKind.LAngle)
            {
                while (k < code.Count && code[k].Kind is not (TokenKind.RAngle or TokenKind.Semicolon))
                    k++;
                if (k >= code.Count || code[k].Kind != TokenKind.RAngle)
                    continue;
                k++;
            }

            // The lexer drops ':', so the declaration reads `Type Name register ( [ps ,] tN [, spaceM] )`.
            if (k + 3 >= code.Count) continue;
            if (code[k].Kind != TokenKind.Identifier) continue;
            if (code[k + 1].Kind != TokenKind.Identifier ||
                !string.Equals(code[k + 1].Text, "register", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (code[k + 2].Kind != TokenKind.LParen) continue;

            int r = k + 3;
            string? stage = null;
            // A stage qualifier: `ps`/`vs`, or a profile such as `ps_4_0`.
            if (r + 1 < code.Count && code[r].Kind == TokenKind.Identifier && code[r + 1].Kind == TokenKind.Comma
                && code[r].Text.Length >= 2
                && (code[r].Text.StartsWith("ps", StringComparison.OrdinalIgnoreCase)
                    || code[r].Text.StartsWith("vs", StringComparison.OrdinalIgnoreCase))
                && (code[r].Text.Length == 2 || code[r].Text[2] == '_'))
            {
                stage = code[r].Text[..2].ToLowerInvariant();
                r += 2;
            }
            if (r >= code.Count) continue;

            Token slotTok = code[r];
            if (slotTok.Kind != TokenKind.Identifier ||
                slotTok.Text.Length < 2 ||
                (slotTok.Text[0] != 't' && slotTok.Text[0] != 'T') ||
                !int.TryParse(slotTok.Text.AsSpan(1), System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int slot))
            {
                continue;
            }
            r++;
            if (r + 1 < code.Count && code[r].Kind == TokenKind.Comma && code[r + 1].Kind == TokenKind.Identifier
                && code[r + 1].Text.StartsWith("space", StringComparison.OrdinalIgnoreCase))
            {
                r += 2;
            }
            if (r >= code.Count || code[r].Kind != TokenKind.RParen) continue;

            if (slot is >= 0 and <= 127)
                registers.Add(new ExplicitTextureRegister(code[k].Text, slot, stage));
        }

        return registers;
    }

    /// <summary>The declaration types whose resource occupies a <c>t</c> register.</summary>
    private static bool IsTRegisterResourceType(string type) =>
        type.StartsWith("texture", StringComparison.OrdinalIgnoreCase)
        || type is "Buffer" or "StructuredBuffer" or "ByteAddressBuffer" or "tbuffer";

    // -------------------------------------------------------------------------
    // Legacy-sampler recovery on the preprocessed source (issue #308)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="flattenedSource"/>, once PREPROCESSED, still holds legacy D3D9
    /// sampler syntax that DXC does not compile: a sampler type it has no keyword for
    /// (<c>sampler2D</c>, <c>samplerCUBE</c>, <c>sampler_state</c> …) or a call to a legacy
    /// sampling intrinsic (<c>tex2D</c> and its family).
    ///
    /// <para>This is the test for "the pre-parser's SM4 rewrite missed something" (issue #308).
    /// The rewrite reads the raw tokens of the main file, so a legacy sampler declared in an
    /// <c>#include</c>d file, one whose register clause or whole declaration comes out of a macro
    /// (<c>DECLARE_TEXTURE(S, 1)</c>), or a <c>tex2D</c> hidden in a macro body
    /// (<c>SAMPLE_TEXTURE(S, uv)</c>) reaches the compiler unrewritten. Pass the text the compiler
    /// was actually given (the rewritten source, <c>#include</c>s inlined); it is preprocessed
    /// here first, because on raw text the same syntax can sit in a branch the target never
    /// compiles.</para>
    /// </summary>
    /// <param name="flattenedSource">
    /// The compiler's input with <c>#include</c>s inlined and the compile's macros prepended
    /// (the output of <c>ShadowDusk.Core.Preprocessor.Preprocessor.Flatten</c>).
    /// </param>
    /// <param name="sourceFile">Display name used in diagnostics.</param>
    /// <returns>
    /// Whether such syntax remains, and the compiler-predefined macro a conditional tested if one
    /// did (the view evaluated it as undefined, so the answer may not be what the compiler saw);
    /// or an <c>SD0009</c> error when the preprocessed view cannot be built.
    /// </returns>
    public static Result<LegacySamplerResidueCheck, ShaderError> HasLegacySamplerResidue(string flattenedSource, string sourceFile)
    {
        var view = Preprocessing.FxMacroPreprocessor.ProcessForCompiler(flattenedSource, sourceFile);
        if (view.IsFailure)
            return Result<LegacySamplerResidueCheck, ShaderError>.Fail(view.Error);

        bool found = FindLegacySamplerResidue(new FxLexer(view.Value.Text, sourceFile).Tokenize()) is not null;
        Preprocessing.FxMacroPreprocessor.CompilerPredefinedMacroUse? predefined = view.Value.PredefinedMacroUse;
        return Result<LegacySamplerResidueCheck, ShaderError>.Ok(new LegacySamplerResidueCheck(
            found,
            predefined is null ? null : new CompilerPredefinedMacroTest(predefined.Name, predefined.File, predefined.Line)));
    }

    /// <summary>
    /// A plain word search for the same legacy sampler syntax
    /// <see cref="HasLegacySamplerResidue"/> looks for, with NO preprocessing: it also sees a
    /// macro body and an inactive branch. Only good for "this effect uses that syntax somewhere",
    /// which is all the caller needs when the preprocessed view cannot be built.
    /// </summary>
    /// <param name="source">Any effect text.</param>
    public static bool MentionsLegacySamplerSyntax(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return LegacySamplerSyntaxWord.IsMatch(source);
    }

    /// <summary>
    /// <see cref="MentionsLegacySamplerSyntax"/> on CODE only: a legacy-sampler word inside a
    /// comment (<c>// was tex2D(...)</c>) does not count. For callers that already have the
    /// preprocessed text and must not change behavior because of a comment.
    /// </summary>
    /// <param name="source">Any effect text.</param>
    public static bool MentionsLegacySamplerSyntaxInCode(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!LegacySamplerSyntaxWord.IsMatch(source))
            return false; // cheap reject: most effects never mention it
        foreach (Token t in new FxLexer(source, "<scan>").Tokenize())
        {
            if (t.Kind == TokenKind.Identifier && LegacySamplerSyntaxWord.IsMatch(t.Text))
                return true;
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex LegacySamplerSyntaxWord = new(
        @"\b(sampler1D|sampler2D|sampler3D|samplerCUBE|sampler_state|tex(1D|2D|3D|CUBE)(bias|grad|lod|proj)?)\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Pre-parses the PREPROCESSED form of an effect (issue #308): the legacy-sampler recovery
    /// for an effect the compiler rejected because the raw pre-parse could not see a legacy
    /// sampler declaration or <c>tex2D</c> call that an <c>#include</c> or a macro supplies.
    ///
    /// <para><paramref name="flattenedRawSource"/> is preprocessed by the managed
    /// <see cref="Preprocessing.FxMacroPreprocessor"/> (conditionals evaluated, macros expanded,
    /// one output line per input line, <c>#line</c> and <c>#pragma</c> passed through), and the
    /// ordinary pre-parse then runs on THAT text, which is how <c>mgfxc</c> itself works: it
    /// preprocesses first and parses techniques and samplers second. The returned
    /// <see cref="FxParseResult.StrippedHlsl"/> is the text to hand to the compiler in place of
    /// the flattened raw source; every span in the result, and any error, is mapped back onto
    /// the author's lines through the <c>#line</c> directives.</para>
    ///
    /// <para>It is a RECOVERY, never the first attempt: an effect that compiles from its raw
    /// source keeps compiling from exactly that source, so no output byte moves for it.</para>
    /// </summary>
    /// <param name="flattenedRawSource">
    /// The RAW effect source (before any pre-parse) with <c>#include</c>s inlined and the
    /// compile's macros prepended (the output of
    /// <c>ShadowDusk.Core.Preprocessor.Preprocessor.Flatten</c>).
    /// </param>
    /// <param name="sourceFile">Display name used in diagnostics.</param>
    /// <returns>
    /// The parse, or an error: <c>SD0009</c> when the preprocessed text cannot be built, or the
    /// pre-parser's own <c>FXnnnn</c> diagnostic for the preprocessed text.
    /// </returns>
    public static Result<FxPreprocessedParse, ShaderError> ParsePreprocessed(string flattenedRawSource, string sourceFile)
    {
        var view = Preprocessing.FxMacroPreprocessor.ProcessForCompiler(flattenedRawSource, sourceFile);
        if (view.IsFailure)
            return Result<FxPreprocessedParse, ShaderError>.Fail(view.Error);

        string text = view.Value.Text;
        var lineMap = new Preprocessing.SourceLineMap(text, sourceFile);

        IReadOnlyList<Token> tokens = new FxLexer(text, sourceFile).Tokenize();
        int[] offsets = ComputeCharacterOffsets(text, tokens);
        Result<FxParseResult, FxParseError> parsed = ParseFileExcludingUnboundSamplerParameterFunctions(
            excluded => new FxPreParser(text, sourceFile, tokens, offsets, FxSourceMode.RewriteToSm4, preprocessed: true, excluded));
        if (parsed.IsFailure)
        {
            FxParseError error = parsed.Error;
            (string file, int line) = lineMap.Resolve(error.Line);
            return Result<FxPreprocessedParse, ShaderError>.Fail(new ShaderError(
                File: file,
                Line: line,
                Column: error.Column,
                Code: $"FX{(int)error.Code:D4}",
                Message: error.Message));
        }

        // What the rewrite still could not model: the text is fully preprocessed and line-aligned
        // with the view, so a plain token scan finds it and the line map places it.
        LegacySamplerResidue? residue = null;
        if (FindLegacySamplerResidue(new FxLexer(parsed.Value.StrippedHlsl, sourceFile).Tokenize()) is { } left)
        {
            (string file, int line) = lineMap.Resolve(left.Line);
            residue = new LegacySamplerResidue(left.Text, file, line, left.Column);
        }

        Preprocessing.FxMacroPreprocessor.CompilerPredefinedMacroUse? predefined = view.Value.PredefinedMacroUse;
        return Result<FxPreprocessedParse, ShaderError>.Ok(new FxPreprocessedParse
        {
            Parsed = lineMap.Remap(parsed.Value),
            Residue = residue,
            CompilerPredefinedMacro = predefined is null
                ? null
                : new CompilerPredefinedMacroTest(predefined.Name, predefined.File, predefined.Line),
        });
    }

    // -------------------------------------------------------------------------
    // Techniques of the preprocessed translation unit (issue #407)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The techniques and passes of an effect, read off its PREPROCESSED translation unit, the
    /// way <c>mgfxc</c> reads them (issue #407).
    ///
    /// <para>The ordinary pre-parse reads techniques from the raw tokens of the main file, so a
    /// technique inside an inactive <c>#if</c> was still compiled, a technique in an
    /// <c>#include</c>d file was never found, and a macro used as a <c>compile</c> target
    /// (<c>compile VS_SHADERMODEL V()</c>) was recorded as its unexpanded name.
    /// <paramref name="flattenedRawSource"/> is preprocessed here first by the managed
    /// <see cref="Preprocessing.FxMacroPreprocessor"/> (conditionals evaluated with the compile's
    /// macros, macros expanded, one output line per input line), and only the technique blocks
    /// are then parsed from it. Every span is mapped back onto the author's lines.</para>
    ///
    /// <para>A conditional on a compiler-predefined macro (<c>__HLSL_VERSION</c> …) is evaluated
    /// as undefined, which is what <c>mgfxc</c>'s own effect preprocessor does when it finds the
    /// techniques: that preprocessor never defines the compiler's macros either.</para>
    /// </summary>
    /// <param name="flattenedRawSource">
    /// The RAW effect source (before any pre-parse) with <c>#include</c>s inlined and the
    /// compile's macros prepended (the output of
    /// <c>ShadowDusk.Core.Preprocessor.Preprocessor.Flatten</c>).
    /// </param>
    /// <param name="sourceFile">Display name used in diagnostics.</param>
    /// <returns>
    /// The techniques, or an error: <c>SD0009</c> when the preprocessed text cannot be built, or
    /// the pre-parser's own <c>FXnnnn</c> diagnostic for a technique block in it (a duplicate
    /// technique name is <c>FX0005</c> here, where it is real).
    /// </returns>
    public static Result<FxPreprocessedTechniques, ShaderError> ParseTechniquesPreprocessed(
        string flattenedRawSource, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(flattenedRawSource);

        var view = Preprocessing.FxMacroPreprocessor.ProcessForCompiler(flattenedRawSource, sourceFile);
        if (view.IsFailure)
            return Result<FxPreprocessedTechniques, ShaderError>.Fail(view.Error);

        if (view.Value.PredefinedMacroUse is { } predefined)
        {
            // DXC defines this macro and the managed view does not, so the technique list read off
            // it could differ from the one DXC sees: the caller keeps the raw list instead.
            return Result<FxPreprocessedTechniques, ShaderError>.Fail(new ShaderError(
                File: predefined.File,
                Line: predefined.Line,
                Column: 0,
                Code: Preprocessing.FxMacroPreprocessor.ErrorCode,
                Message: $"a conditional tests '{predefined.Name}', which the compiler defines and the preprocessed view of this effect does not"));
        }

        string text = view.Value.Text;
        var lineMap = new Preprocessing.SourceLineMap(text, sourceFile);

        IReadOnlyList<Token> tokens = new FxLexer(text, sourceFile).Tokenize();
        var parser = new FxPreParser(
            text, sourceFile, tokens, ComputeCharacterOffsets(text, tokens), FxSourceMode.PreserveSm3, preprocessed: true);
        Result<List<TechniqueInfo>, FxParseError> parsed = parser.ParseTechniqueBlocks();
        if (parsed.IsFailure)
        {
            FxParseError error = parsed.Error;
            (string file, int line) = lineMap.Resolve(error.Line);
            return Result<FxPreprocessedTechniques, ShaderError>.Fail(new ShaderError(
                File: file,
                Line: line,
                Column: error.Column,
                Code: $"FX{(int)error.Code:D4}",
                Message: error.Message));
        }

        // The view keeps one output line per input line, so a technique whose keyword is written
        // on that line of the raw text is the author's; one whose line holds no such keyword came
        // out of a macro (MonoGame's TECHNIQUE(name, vs, ps)).
        // Both tests need the physical lines, so they run before the spans are remapped. A
        // compile profile is the author's when its line in the raw text spells it; otherwise it
        // came out of a macro (`compile VS_SHADERMODEL V()`).
        IReadOnlyList<Token> rawTokens = new FxLexer(flattenedRawSource, sourceFile).Tokenize();
        HashSet<int> literalLines = LiteralTechniqueLines(rawTokens);
        var rawWordsByLine = new Dictionary<int, HashSet<string>>();
        foreach (Token t in rawTokens)
        {
            if (t.Kind != TokenKind.Identifier)
                continue;
            if (!rawWordsByLine.TryGetValue(t.Line, out HashSet<string>? words))
                rawWordsByLine[t.Line] = words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            words.Add(t.Text);
        }
        bool FromMacro(string? profile, SourceSpan? span) =>
            profile is not null && span is { } s &&
            !(rawWordsByLine.TryGetValue(s.StartLine, out HashSet<string>? words) && words.Contains(profile));

        // A line a macro touched does not keep its columns in the view, so a compile target's
        // span is moved onto the token the author wrote there: the Nth compile target of the
        // view line is the Nth of the raw line (SD0013/SD0014 point at the macro name, as
        // they did when the raw text was parsed).
        Dictionary<int, List<Token>> viewTargets = CompileTargetsByLine(tokens);
        Dictionary<int, List<Token>> rawTargets = CompileTargetsByLine(rawTokens);
        Token? AuthorToken(SourceSpan? span)
        {
            if (span is not { } s ||
                !viewTargets.TryGetValue(s.StartLine, out List<Token>? inView) ||
                !rawTargets.TryGetValue(s.StartLine, out List<Token>? inRaw) ||
                inView.Count != inRaw.Count)
            {
                return null;
            }
            int index = inView.FindIndex(t => t.Column == s.StartColumn);
            return index < 0 ? null : inRaw[index];
        }
        SourceSpan? AuthorSpan(SourceSpan? span) =>
            AuthorToken(span) is { } written
                ? new SourceSpan(written.Line, written.Column, written.Line, written.Column + written.Text.Length)
                : span;
        // The macro name the author wrote for a profile a macro produced (null otherwise).
        string? MacroName(string? profile, SourceSpan? span) =>
            FromMacro(profile, span) ? AuthorToken(span)?.Text : null;

        List<TechniqueInfo> marked = parsed.Value.Select(t => t with
        {
            Passes = t.Passes.Select(p => p with
            {
                VertexProfileFromMacro = FromMacro(p.VertexProfile, p.VertexProfileSpan),
                PixelProfileFromMacro = FromMacro(p.PixelProfile, p.PixelProfileSpan),
                VertexProfileMacroName = MacroName(p.VertexProfile, p.VertexProfileSpan),
                PixelProfileMacroName = MacroName(p.PixelProfile, p.PixelProfileSpan),
                VertexProfileSpan = AuthorSpan(p.VertexProfileSpan),
                PixelProfileSpan = AuthorSpan(p.PixelProfileSpan),
            }).ToList(),
        }).ToList();

        IReadOnlyList<TechniqueInfo> remapped = lineMap.Remap(new FxParseResult
        {
            StrippedHlsl = string.Empty,
            Techniques = marked,
            Samplers = [],
            ParameterAnnotations = [],
        }).Techniques;
        var remappedLiteral = new List<TechniqueInfo>();
        for (int i = 0; i < parsed.Value.Count; i++)
        {
            if (literalLines.Contains(parsed.Value[i].Span.StartLine))
                remappedLiteral.Add(remapped[i]);
        }

        return Result<FxPreprocessedTechniques, ShaderError>.Ok(new FxPreprocessedTechniques
        {
            Techniques = remapped,
            LiteralTechniques = remappedLiteral,
        });
    }

    /// <summary>
    /// Blanks every technique block written literally in <paramref name="source"/> (a
    /// <c>technique</c> / <c>technique10</c> / <c>technique11</c> keyword at global scope, an
    /// optional name and annotation block, then a balanced brace body), keeping every line break
    /// so no line number moves. Used on the compiler's input once techniques come from the
    /// preprocessed translation unit (issue #407): the main file's blocks are already stripped by
    /// the pre-parse, so what this removes is a technique in an <c>#include</c>d file, which the
    /// compiler would otherwise be handed as effect syntax. A macro call (<c>TECHNIQUE(...)</c>)
    /// and a block whose braces do not balance are left as they are.
    /// </summary>
    /// <param name="source">Any effect text, typically the flattened compiler input.</param>
    /// <param name="sourceFile">Display name used by the lexer.</param>
    /// <returns><paramref name="source"/> itself when it holds no such block.</returns>
    public static string StripTechniqueBlocks(string source, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(source);

        IReadOnlyList<Token> tokens = new FxLexer(source, sourceFile).Tokenize();
        int[] offsets = ComputeCharacterOffsets(source, tokens);
        List<(int Start, int End)>? blocks = null;

        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            if (t.Kind == TokenKind.LBrace) { depth++; continue; }
            if (t.Kind == TokenKind.RBrace) { depth = Math.Max(0, depth - 1); continue; }
            if (depth != 0 || !IsTechniqueKeywordToken(tokens, i))
                continue;

            // keyword [name] [< ... >] {
            int j = NextCode(tokens, i + 1);
            if (j < tokens.Count && tokens[j].Kind == TokenKind.Identifier)
                j = NextCode(tokens, j + 1);
            if (j < tokens.Count && tokens[j].Kind == TokenKind.LAngle)
            {
                while (j < tokens.Count && tokens[j].Kind is not (TokenKind.RAngle or TokenKind.EOF))
                    j++;
                j = NextCode(tokens, j + 1);
            }
            if (j >= tokens.Count || tokens[j].Kind != TokenKind.LBrace)
                continue;

            int braces = 0, k = j;
            for (; k < tokens.Count && tokens[k].Kind != TokenKind.EOF; k++)
            {
                if (tokens[k].Kind == TokenKind.LBrace) braces++;
                else if (tokens[k].Kind == TokenKind.RBrace && --braces == 0) break;
            }
            if (braces != 0 || k >= tokens.Count || tokens[k].Kind != TokenKind.RBrace)
                continue;

            (blocks ??= []).Add((offsets[i], offsets[k] + 1));
            i = k;
        }

        if (blocks is null)
            return source;

        var sb = new StringBuilder(source);
        foreach ((int start, int end) in blocks)
        {
            for (int c = start; c < end && c < sb.Length; c++)
            {
                if (sb[c] is not ('\n' or '\r'))
                    sb[c] = ' ';
            }
        }
        return sb.ToString();
    }

    /// <summary>Per line, the tokens that directly follow a <c>compile</c> keyword, in order.</summary>
    private static Dictionary<int, List<Token>> CompileTargetsByLine(IReadOnlyList<Token> tokens)
    {
        var byLine = new Dictionary<int, List<Token>>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Identifier ||
                !string.Equals(tokens[i].Text, "compile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int next = NextCode(tokens, i + 1);
            if (next >= tokens.Count || tokens[next].Kind != TokenKind.Identifier)
                continue;
            if (!byLine.TryGetValue(tokens[next].Line, out List<Token>? list))
                byLine[tokens[next].Line] = list = [];
            list.Add(tokens[next]);
        }
        return byLine;
    }

    /// <summary>The 1-based lines that hold a technique keyword written as one (not a macro call).</summary>
    private static HashSet<int> LiteralTechniqueLines(IReadOnlyList<Token> tokens)
    {
        var lines = new HashSet<int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (IsTechniqueKeywordToken(tokens, i))
                lines.Add(tokens[i].Line);
        }
        return lines;
    }

    /// <summary>
    /// True when <paramref name="tokens"/>[<paramref name="index"/>] is a <c>technique</c>,
    /// <c>technique10</c> or <c>technique11</c> keyword (any case, as fxc accepts) that is not a
    /// macro call: the same test the main parse applies.
    /// </summary>
    private static bool IsTechniqueKeywordToken(IReadOnlyList<Token> tokens, int index)
    {
        Token t = tokens[index];
        if (t.Kind != TokenKind.Identifier || !IsTechniqueKeyword(t.Text))
            return false;
        int next = NextCode(tokens, index + 1);
        return next >= tokens.Count || tokens[next].Kind != TokenKind.LParen;
    }

    private static bool IsTechniqueKeyword(string text) =>
        string.Equals(text, "technique", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "technique10", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "technique11", StringComparison.OrdinalIgnoreCase);

    /// <summary>The index of the first token at or after <paramref name="index"/> that is not trivia.</summary>
    private static int NextCode(IReadOnlyList<Token> tokens, int index)
    {
        while (index < tokens.Count &&
               tokens[index].Kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor)
        {
            index++;
        }
        return index;
    }

    /// <summary>
    /// Parses only the technique blocks of an already preprocessed token stream (issue #407):
    /// every other construct is skipped, tracking brace depth so a technique is only recognized
    /// at global scope.
    /// </summary>
    private Result<List<TechniqueInfo>, FxParseError> ParseTechniqueBlocks()
    {
        var techniques = new List<TechniqueInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;

        while (Peek().Kind != TokenKind.EOF)
        {
            Token tok = Peek();
            if (tok.Kind == TokenKind.LBrace) { depth++; Consume(); continue; }
            if (tok.Kind == TokenKind.RBrace) { depth = Math.Max(0, depth - 1); Consume(); continue; }

            if (depth == 0 && IsTechniqueKeywordToken(_tokens, _pos))
            {
                var result = ParseTechnique();
                if (result.IsFailure)
                    return Result<List<TechniqueInfo>, FxParseError>.Fail(result.Error);
                // An anonymous technique ("") is never a duplicate, as for passes (see above).
                if (result.Value.Name.Length > 0 && !names.Add(result.Value.Name))
                {
                    return Fail<List<TechniqueInfo>>(FxParseErrorCode.DuplicateTechniqueName,
                        $"Duplicate technique name '{result.Value.Name}'", tok);
                }
                techniques.Add(result.Value);
                continue;
            }

            Consume();
        }

        return Result<List<TechniqueInfo>, FxParseError>.Ok(techniques);
    }

    /// <summary>
    /// The first token of legacy D3D9 sampler syntax DXC does not compile, in a token stream that
    /// is already preprocessed: a sampler type keyword other than the two DXC has, or a legacy
    /// sampling intrinsic that is actually CALLED (a variable that merely shares the name is not
    /// one).
    /// </summary>
    private static Token? FindLegacySamplerResidue(IReadOnlyList<Token> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            if (t.Kind != TokenKind.Identifier)
                continue;

            if (SamplerTypeKeywords.Contains(t.Text) && !IsDxcSamplerType(t.Text))
                return t;

            if (LegacySampleIntrinsics.ContainsKey(t.Text) || RestructuredLegacySampleIntrinsics.ContainsKey(t.Text) ||
                UnsupportedLegacyIntrinsics.Contains(t.Text))
            {
                int j = i + 1;
                while (j < tokens.Count &&
                       tokens[j].Kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor)
                {
                    j++;
                }
                if (j < tokens.Count && tokens[j].Kind == TokenKind.LParen)
                    return t;
            }
        }

        return null;
    }

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !(char.IsAsciiLetter(text[0]) || text[0] == '_'))
            return false;
        foreach (char c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The preprocessed view of <paramref name="flattenedSource"/> (conditionals evaluated,
    /// macros expanded), tokenized. Shared by every OpenGL sampler-register reading so they are
    /// all decided on the same text.
    /// </summary>
    private static Result<IReadOnlyList<Token>, ShaderError> PreprocessedViewTokens(
        string flattenedSource, string sourceFile)
    {
        var view = Preprocessing.FxMacroPreprocessor.Process(flattenedSource, sourceFile);
        if (view.IsFailure)
            return Result<IReadOnlyList<Token>, ShaderError>.Fail(view.Error);

        return Result<IReadOnlyList<Token>, ShaderError>.Ok(new FxLexer(view.Value, sourceFile).Tokenize());
    }

    /// <summary>
    /// Read-only scan for <c>&lt;sampler type&gt; &lt;name&gt; : register(sN)</c>, returning
    /// name -> N (a later declaration of the same name wins, as it does in
    /// the main parse). It matches the same shape the main loop records into
    /// <see cref="_explicitSamplerRegisters"/>: any <see cref="SamplerTypeKeywords"/> keyword,
    /// the name, then the exact <c>register ( sN )</c> clause that
    /// <see cref="TryReadSamplerRegister"/> accepts (the lexer has already dropped the ':').
    /// Whether the declaration then carries <c>= sampler_state { … }</c>, a brace block or just
    /// <c>;</c> does not matter to the register.
    ///
    /// <para>Like <see cref="CollectReservedSamplerRegisters(IReadOnlyList{Token})"/> it is a
    /// separate pass so it cannot perturb the rewrite: a shape it does not recognise costs an
    /// allocation difference, never a parse.</para>
    /// </summary>
    private static Dictionary<string, int> CollectLegacySamplerRegisters(IReadOnlyList<Token> tokens)
    {
        var registers = new Dictionary<string, int>(StringComparer.Ordinal);

        var code = new List<Token>(tokens.Count);
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor))
                code.Add(t);
        }

        for (int i = 0; i + 5 < code.Count; i++)
        {
            if (code[i].Kind != TokenKind.Identifier || !SamplerTypeKeywords.Contains(code[i].Text))
                continue;
            if (code[i + 1].Kind != TokenKind.Identifier) continue;
            if (code[i + 2].Kind != TokenKind.Identifier ||
                !string.Equals(code[i + 2].Text, "register", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (code[i + 3].Kind != TokenKind.LParen) continue;

            Token slotTok = code[i + 4];
            if (slotTok.Kind != TokenKind.Identifier ||
                slotTok.Text.Length < 2 ||
                (slotTok.Text[0] != 's' && slotTok.Text[0] != 'S') ||
                !int.TryParse(slotTok.Text.AsSpan(1), System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int slot))
            {
                continue;
            }
            if (code[i + 5].Kind != TokenKind.RParen) continue;

            // The same single-byte bound TryReadSamplerRegister applies.
            if (slot is >= 0 and <= 255)
                registers[code[i + 1].Text] = slot;
        }

        return registers;
    }

    private static HashSet<int> CollectReservedSamplerRegisters(IReadOnlyList<Token> tokens)
    {
        var reserved = new HashSet<int>();

        // Code tokens only — trivia can sit anywhere between the five we need to match.
        var code = new List<Token>(tokens.Count);
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor))
                code.Add(t);
        }

        for (int i = 0; i + 4 < code.Count; i++)
        {
            if (code[i].Kind != TokenKind.Identifier || !IsRegisterReservingSamplerType(code[i].Text))
                continue;
            if (code[i + 1].Kind != TokenKind.Identifier) continue;
            if (code[i + 2].Kind != TokenKind.Identifier ||
                !string.Equals(code[i + 2].Text, "register", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (code[i + 3].Kind != TokenKind.LParen) continue;

            Token slotTok = code[i + 4];
            if (slotTok.Kind != TokenKind.Identifier ||
                slotTok.Text.Length < 2 ||
                (slotTok.Text[0] != 's' && slotTok.Text[0] != 'S') ||
                !int.TryParse(slotTok.Text.AsSpan(1), System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int slot))
            {
                continue;
            }
            if (i + 5 < code.Count && code[i + 5].Kind != TokenKind.RParen) continue;
            if (slot is >= 0 and <= 255)
                reserved.Add(slot);
        }

        return reserved;
    }

    /// <summary>
    /// The type keywords whose declaration RESERVES its explicit <c>register(sN)</c> on OpenGL
    /// (issue #309): every sampler type, not only the exact spelling <c>SamplerState</c>.
    ///
    /// <para>Measured against the pinned <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c>, one texture
    /// read through <c>Tex.Sample(S, uv)</c> with an unannotated <c>SamplerState S</c> unless noted:
    /// <c>sampler S : register(s0)</c> as the sampler read is <c>ps_s1</c>, bare and with a
    /// <c>= sampler_state { … }</c> block; an UNUSED <c>sampler U : register(s0)</c>,
    /// <c>sampler2D U : register(s0)</c>, <c>samplerCUBE U : register(s0)</c> or
    /// <c>SamplerComparisonState C : register(s0)</c> is <c>ps_s1</c> too; and a legacy sampler
    /// with a register that ANOTHER entry point reads through <c>tex2D</c> still takes that
    /// register away from this one (<c>sampler X : register(s0); sampler2D Y;</c> with one pixel
    /// shader per sampler is <c>ps_s0</c> for the first and <c>ps_s1</c> for the second). fxc
    /// keeps an explicitly bound register out of circulation for the whole source, whatever the
    /// keyword and whether or not the entry point being compiled uses the object.</para>
    ///
    /// <para>A legacy sampler a <c>tex2D</c> reads is therefore reserved AND pinned
    /// (<see cref="CollectLegacySamplerRegisters"/>): the pin wins for its own pair in pass 1 of
    /// <c>SpirvCombinedSamplerPairs.ResolveSlots</c>, and the reservation keeps every other pair
    /// off that register in pass 2.</para>
    /// </summary>
    private static bool IsRegisterReservingSamplerType(string keyword) =>
        SamplerTypeKeywords.Contains(keyword) ||
        string.Equals(keyword, "SamplerComparisonState", StringComparison.Ordinal);

    /// <summary>
    /// True for the two sampler type keywords DXC itself declares a sampler with. Every other
    /// <see cref="SamplerTypeKeywords"/> entry (<c>sampler2D</c>, <c>samplerCUBE</c>,
    /// <c>sampler_state</c> …) is D3D9 effect syntax DXC rejects.
    /// </summary>
    private static bool IsDxcSamplerType(string keyword) =>
        keyword is "sampler" or "SamplerState" or "SamplerComparisonState";

    /// <summary>
    /// True for the dimensioned D3D9 sampler types fxc declares a sampler with and DXC does not
    /// (exact spelling: fxc is case-sensitive here, <c>samplerstate</c> is its X3000).
    /// </summary>
    private static bool IsD3d9OnlySamplerType(string keyword) =>
        keyword is "sampler1D" or "sampler2D" or "sampler3D" or "samplerCUBE";

    /// <summary>True for the D3D9 effect-framework <c>NULL</c> keyword (case-insensitive,
    /// matching fxc) used in <c>VertexShader = NULL;</c> / <c>Texture = NULL;</c>.</summary>
    private static bool IsNullKeyword(string text) =>
        string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The kind of the nearest preceding code token (skipping comments) before the current
    /// position, or <see cref="TokenKind.EOF"/> when there is none. Used to disambiguate a
    /// type keyword that is actually a variable name closing a template ('...&gt; Texture').
    /// </summary>
    private TokenKind PrevCodeKind()
    {
        int offset = -1;
        while (_pos + offset >= 0 &&
               Peek(offset).Kind is TokenKind.LineComment or TokenKind.BlockComment)
            offset--;
        return _pos + offset >= 0 ? Peek(offset).Kind : TokenKind.EOF;
    }

    /// <summary>
    /// Discriminates a GENUINE FX annotation block from a relational/shift/ternary
    /// expression that merely happens to match the 'Identifier Identifier LAngle' shape
    /// in this flat (scope-unaware) token scanner. <paramref name="langleOffset"/> is the
    /// lookahead offset (relative to <c>_pos</c>) of the <c>&lt;</c> token already confirmed
    /// by the caller. A real annotation block (see <see cref="ParseAnnotationBlock"/>) is
    /// either empty (<c>&lt;</c> immediately followed by <c>&gt;</c>) or one-or-more entries
    /// each of the form <c>Type Name = Value ;</c>, so the first code token after the
    /// <c>&lt;</c> is <c>RAngle</c>, or it is <c>Identifier</c> followed by <c>Identifier</c>
    /// followed by <c>Equals</c>. Relational operators never match: <c>value &lt;= 0.5f</c>
    /// has <c>Equals</c> right after the <c>&lt;</c>; <c>a &lt; b ? c : d</c> tokenizes to
    /// three identifiers (<c>?</c>/<c>:</c> are dropped) so the third token is an
    /// <c>Identifier</c>, not <c>Equals</c>; and <c>a &lt;&lt; b</c> has another <c>LAngle</c>.
    /// Comments are skipped between each token, mirroring the caller's lookahead idiom.
    /// </summary>
    private bool IsAnnotationBlockStart(int langleOffset)
    {
        int o1 = NextCodeOffset(langleOffset + 1);
        var first = Peek(o1);

        // Empty annotation block: '< >'.
        if (first.Kind == TokenKind.RAngle)
            return true;

        // First entry must be 'Type Name = …': Identifier Identifier Equals.
        if (first.Kind != TokenKind.Identifier)
            return false;

        int o2 = NextCodeOffset(o1 + 1);
        if (Peek(o2).Kind != TokenKind.Identifier)
            return false;

        int o3 = NextCodeOffset(o2 + 1);
        return Peek(o3).Kind == TokenKind.Equals;
    }

    /// <summary>
    /// Whether <paramref name="textureName"/> appears in the source ONLY as the value of a
    /// sampler_state <c>Texture = &lt;T&gt;</c> (or <c>(T)</c>, or bare <c>T</c>) entry. Then nothing
    /// declares it, and without a declaration the rewritten <c>T.Sample(…)</c> cannot compile
    /// (while <c>mgfxc</c>, which reads the name off the state block itself, emits a <c>T</c>
    /// parameter). Every other code mention (a declaration anywhere in the file, a use) and any
    /// mention inside a preprocessor directive (a macro could produce or rename it) keeps the
    /// rewrite exactly as it was. Comments do not count.
    /// </summary>
    private bool IsOnlyNamedByTextureStates(string textureName)
    {
        var word = new System.Text.RegularExpressions.Regex(
            $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(textureName)}(?![A-Za-z0-9_])");

        int PreviousCode(int index)
        {
            int k = index - 1;
            while (k >= 0 && _tokens[k].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                k--;
            return k;
        }

        int mentions = 0, stateValues = 0;
        for (int i = 0; i < _tokens.Count; i++)
        {
            Token tok = _tokens[i];
            if (tok.Kind == TokenKind.Preprocessor && word.IsMatch(tok.Text))
                return false;
            if (tok.Kind != TokenKind.Identifier || !string.Equals(tok.Text, textureName, StringComparison.Ordinal))
                continue;

            mentions++;
            int k = PreviousCode(i);
            if (k >= 0 && _tokens[k].Kind is TokenKind.LAngle or TokenKind.LParen)
                k = PreviousCode(k);
            if (k < 0 || _tokens[k].Kind != TokenKind.Equals)
                continue;
            k = PreviousCode(k);
            if (k >= 0 && _tokens[k].Kind == TokenKind.Identifier
                && string.Equals(_tokens[k].Text, "Texture", StringComparison.OrdinalIgnoreCase))
            {
                stateValues++;
            }
        }
        return mentions > 0 && mentions == stateValues;
    }

    /// <summary>
    /// For each candidate texture (<see cref="FxParseResult.UndeclaredStateTextures"/>) that the
    /// macro-expanded, include-inlined <paramref name="flattened"/> text declares nowhere, adds
    /// <c>Texture2D T;</c> once, ahead of the first top-level code in the file. "Declares" is a
    /// declaration scan of the preprocessed view, not a spelling match: any type whose name starts
    /// with <c>texture</c> (any case: <c>texture</c>, <c>Texture2D</c>, <c>TextureCube</c>,
    /// <c>Texture2DArray</c>, <c>texture2D</c>), optional template arguments
    /// (<c>Texture2D&lt;float4&gt;</c>, MonoGame's own <c>Macros.fxh</c> spelling), then a
    /// comma-separated declarator list with arrays, registers and semantics. The declaration goes
    /// at the start of the file rather than beside the sampler, because the sampler can be declared
    /// once per <c>#if</c> branch and the first one in the text may be in the branch that does not
    /// compile; it is put on the line of the first code outside every conditional, so no line
    /// number moves.
    /// </summary>
    /// <param name="flattened">The rewritten source with includes inlined and the compile's macros prepended.</param>
    /// <param name="sourceFileName">Display name for diagnostics.</param>
    /// <param name="parsed">The pre-parse that produced the rewrite (its <see cref="FxParseResult.UndeclaredStateTextures"/>).</param>
    /// <returns>The text to compile, or <c>SD0009</c> when the preprocessed view cannot be built.</returns>
    public static Result<string, ShaderError> DeclareUndeclaredStateTextures(
        string flattened, string sourceFileName, FxParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        IReadOnlyDictionary<string, string> candidates = parsed.UndeclaredStateTextures;
        if (candidates.Count == 0)
            return Result<string, ShaderError>.Ok(flattened);
        Result<string, ShaderError> view = Preprocessing.FxMacroPreprocessor.Process(flattened, sourceFileName);
        if (view.IsFailure)
            return view;

        HashSet<string> declared = DeclaredTextureNames(new FxLexer(view.Value, sourceFileName).Tokenize());
        var missing = candidates.Values
            .Distinct(StringComparer.Ordinal)
            .Where(t => !declared.Contains(t))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (missing.Count == 0)
            return Result<string, ShaderError>.Ok(flattened);

        int at = FirstTopLevelCodeOffset(flattened, sourceFileName);
        string declarations = string.Concat(missing.Select(t => $"Texture2D {t}; "));
        return Result<string, ShaderError>.Ok(flattened.Insert(at, declarations));
    }

    /// <summary>Names declared with a <c>texture*</c> type (any case, optional template arguments).</summary>
    private static HashSet<string> DeclaredTextureNames(IReadOnlyList<Token> tokens)
    {
        var code = tokens.Where(t => t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor)).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Kind != TokenKind.Identifier
                || !code[i].Text.StartsWith("texture", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int j = i + 1;
            if (j < code.Count && code[j].Kind == TokenKind.LAngle)
            {
                int depth = 0;
                for (; j < code.Count; j++)
                {
                    if (code[j].Kind == TokenKind.LAngle) depth++;
                    else if (code[j].Kind == TokenKind.RAngle && --depth == 0) { j++; break; }
                    else if (code[j].Kind is TokenKind.Semicolon or TokenKind.LBrace) break;
                }
            }

            // Declarator list: a name, then anything (array size, ': register(t2)', a semantic,
            // annotations) up to ',' (next name) or the end of the statement.
            bool expectName = true;
            int parens = 0;
            for (; j < code.Count; j++)
            {
                Token t = code[j];
                if (t.Kind is TokenKind.Semicolon or TokenKind.LBrace or TokenKind.RBrace) break;
                if (t.Kind == TokenKind.LParen) { parens++; continue; }
                if (t.Kind == TokenKind.RParen) { parens--; continue; }
                if (parens == 0 && t.Kind == TokenKind.Comma) { expectName = true; continue; }
                if (expectName && t.Kind == TokenKind.Identifier)
                {
                    names.Add(t.Text);
                    expectName = false;
                    continue;
                }
                if (expectName)
                    break; // not a declaration (e.g. a cast or a function's return type use)
            }
        }
        return names;
    }

    /// <summary>
    /// The character offset of the first code token outside every <c>#if</c>/<c>#ifdef</c>/
    /// <c>#ifndef</c> block, or 0 when there is none.
    /// </summary>
    private static int FirstTopLevelCodeOffset(string text, string sourceFile)
    {
        IReadOnlyList<Token> tokens = new FxLexer(text, sourceFile).Tokenize();
        int[] offsets = ComputeCharacterOffsets(text, tokens);
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            if (t.Kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.EOF)
                continue;
            if (t.Kind == TokenKind.Preprocessor)
            {
                string d = t.Text.TrimStart('#', ' ', '\t');
                if (d.StartsWith("if", StringComparison.Ordinal)) depth++;
                else if (d.StartsWith("endif", StringComparison.Ordinal) && depth > 0) depth--;
                continue;
            }
            if (depth == 0)
                return offsets[i];
        }
        return 0;
    }

    /// <summary>The synthesized <c>Texture2D</c> name bound to a bare/untextured sampler.</summary>
    private static string SynthTextureName(string samplerName) => samplerName + "_SDTexture";

    /// <summary>
    /// One-pass scan over the whole token stream collecting the names of samplers
    /// used as the first argument of a legacy <c>tex2D</c>-family intrinsic (<c>tex2D</c>,
    /// <c>tex2Dgrad</c>, <c>tex2Dlod</c>, <c>tex2Dbias</c>, <c>tex2Dproj</c>). This drives
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
            if (_tokens[i].Kind != TokenKind.Identifier ||
                !(LegacySampleIntrinsics.ContainsKey(_tokens[i].Text) || RestructuredLegacySampleIntrinsics.ContainsKey(_tokens[i].Text)))
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
    /// One-pass scan collecting the names of samplers passed as the first argument
    /// of a MODERN Texture method call ('<c>Tex.Sample(S, uv)</c>',
    /// '<c>.SampleGrad</c>', '<c>.SampleLevel</c>', …). The token shape is
    /// '<c>. Sample… ( Identifier</c>' — a <see cref="TokenKind.Dot"/>, an
    /// identifier whose name begins with 'Sample' (the SM4 sample-method family),
    /// '(', then the sampler argument. This drives B2: a <c>sampler_state</c>
    /// declaration referenced this way is rewritten to a passthrough
    /// '<c>SamplerState S;</c>' instead of being erased. Matched case-sensitively
    /// because HLSL method names are case-sensitive. Text inside comments and
    /// preprocessor directives is never seen here (the lexer emits those as single
    /// tokens, not identifiers).
    /// </summary>
    private HashSet<string> CollectModernMethodSamplers()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 1; i < _tokens.Count; i++)
        {
            if (_tokens[i].Kind != TokenKind.Identifier ||
                !_tokens[i].Text.StartsWith("Sample", StringComparison.Ordinal))
                continue;

            // The method must be invoked on a resource: the preceding code token is '.'.
            int p = i - 1;
            while (p >= 0 && _tokens[p].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                p--;
            if (p < 0 || _tokens[p].Kind != TokenKind.Dot)
                continue;

            // Expect 'Sample…' '(' Identifier — comments may sit in between.
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
    private bool IsBraceSamplerStateForm(int afterName) =>
        Peek(OffsetAfterOptionalRegister(afterName)).Kind == TokenKind.LBrace;

    /// <summary>
    /// Returns the look-ahead offset (relative to <c>_pos</c>) of the first code
    /// token AFTER an optional '<c>register ( … )</c>' clause beginning at
    /// <paramref name="offset"/>. The FxLexer drops the leading ':', so a register
    /// clause surfaces as '<c>register</c>' '<c>(</c>' … '<c>)</c>'. When no
    /// register clause is present (or it is malformed / unterminated), the input
    /// <paramref name="offset"/> is returned unchanged so the caller's own shape
    /// check decides the outcome. Shared by the '<c>= sampler_state</c>' dispatch
    /// (B8) and the brace-form detector.
    /// </summary>
    private int OffsetAfterOptionalRegister(int offset)
    {
        if (!PeekIsKeywordAt(offset, "register"))
            return offset;

        int off = NextCodeOffset(offset + 1);
        if (Peek(off).Kind != TokenKind.LParen)
            return offset;

        off = NextCodeOffset(off + 1);
        while (Peek(off).Kind is not (TokenKind.RParen or TokenKind.EOF))
            off = NextCodeOffset(off + 1);
        if (Peek(off).Kind != TokenKind.RParen)
            return offset;

        return NextCodeOffset(off + 1);
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
    /// Rewrites the <c>tex2Dlod</c> / <c>tex2Dbias</c> / <c>tex2Dproj</c> call whose name is the
    /// token at <c>_pos</c> (its sampler already matched and bound to
    /// <paramref name="texture"/>) into a call of a helper, and records the helper for insertion
    /// before the enclosing top-level declaration. Returns the replacement for the span from
    /// the intrinsic name through its <c>(</c>: <c>tex2Dlod (</c> becomes
    /// <c>tex2Dlod_SD0(T, </c>, so <c>tex2Dlod(s, t)</c> reads <c>tex2Dlod_SD0(T, s, t)</c> with
    /// both arguments untouched. Every newline in the replaced span is kept. Returns a null rewrite
    /// (the call stays verbatim) when the right answer depends on which conditional branch
    /// compiles; see the two cases below.
    /// </summary>
    /// <param name="intrinsic">The intrinsic-name token at <c>_pos</c>.</param>
    /// <param name="texture">The texture the sampler argument is bound to.</param>
    /// <param name="braceDepth">The main loop's brace depth at the call (0 at global scope).</param>
    private Result<(int Start, int End, string Replacement)?, FxParseError> RewriteRestructuredIntrinsic(
        Token intrinsic, string texture, int braceDepth)
    {
        // The call must have exactly two arguments, the sampler and the float4 coordinate:
        // that is the only overload the D3D9 intrinsic has. Anything else is refused here
        // rather than surfacing as a mismatch against a helper name the author never wrote.
        int lparen = NextCodeOffset(1);
        int depth = 0;
        int commas = 0;
        int off = lparen;
        for (; ; off++)
        {
            Token t = Peek(off);
            if (t.Kind == TokenKind.EOF)
                break;
            if (t.Kind == TokenKind.LParen)
                depth++;
            else if (t.Kind == TokenKind.RParen && --depth == 0)
                break;
            else if (t.Kind == TokenKind.Comma && depth == 1)
                commas++;
        }
        if (Peek(off).Kind != TokenKind.RParen || commas != 1)
        {
            return Fail<(int, int, string)?>(FxParseErrorCode.UnsupportedLegacyIntrinsic,
                $"'{intrinsic.Text}' takes two arguments, a sampler and a float4 coordinate " +
                $"({intrinsic.Text}(s, t)); this call has {(Peek(off).Kind == TokenKind.RParen ? (commas + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "an unterminated list of")} " +
                "arguments, so ShadowDusk cannot rewrite it to the modern Texture2D method.", intrinsic);
        }

        (int declStart, int declIndex, int boundaryEnd) = TopLevelDeclarationStart(braceDepth);
        _restructuredBoundaryEnd[declStart] = boundaryEnd;

        // A conditional directive that closes or switches the branch the declaration starts in
        // before the call (one body under two #if-selected headers): no single position before
        // the declaration serves every configuration. Only the preprocessed source can say which
        // header compiles, so the call is left for the recovery. Measured: fxc compiles the
        // shape, and so does this route on OpenGL, Vulkan and DirectX 12; on that preprocessed
        // text the conditionals are gone, so this can only fire on raw text.
        if (FindConditionalBoundary(declIndex, _pos) is { } boundary)
        {
            FxParseError split = MakeError(FxParseErrorCode.UnsupportedLegacyIntrinsic,
                $"ShadowDusk rewrites '{intrinsic.Text}' to a helper function it defines right before " +
                "the declaration that calls it, but a conditional directive ('" + boundary.Text.Trim() +
                $"', line {boundary.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)}) " +
                "closes or switches the branch that declaration starts in before the call, so no single " +
                "position serves every configuration, and the effect could not be re-parsed from its " +
                "preprocessed source either. Use the modern form for this call (tex2Dlod(s, t) " +
                "becomes T.SampleLevel(s, t.xy, t.w); tex2Dbias(s, t) becomes T.SampleBias(s, t.xy, t.w); " +
                "tex2Dproj(s, t) becomes T.Sample(s, t.xy / t.w)).", intrinsic);
            if (!_preprocessed)
            {
                _deferredRestructuredCalls.Add(split);
                return Result<(int, int, string)?, FxParseError>.Ok(null);
            }
            return Result<(int, int, string)?, FxParseError>.Fail(split);
        }

        // A texture declared with a different type in different #if branches: the helper's
        // parameter type depends on the branch, which only the preprocessed source knows.
        if (ResolveTextureParameterType(texture) is not { } textureType)
        {
            if (!_preprocessed)
            {
                _deferredRestructuredCalls.Add(MakeError(FxParseErrorCode.UnsupportedLegacyIntrinsic,
                    $"The texture '{texture}' that '{intrinsic.Text}' samples is declared with different " +
                    "types in different conditional branches, so the type of the helper ShadowDusk " +
                    "rewrites the call to depends on the branch that compiles, and the effect could not be " +
                    "re-parsed from its preprocessed source either. Use the modern form for this call " +
                    $"({texture}.SampleLevel(s, t.xy, t.w) for tex2Dlod).", intrinsic));
                return Result<(int, int, string)?, FxParseError>.Ok(null);
            }
            textureType = "Texture2D";
        }
        if (!_restructuredHelperNames.TryGetValue((declStart, intrinsic.Text, textureType), out string? helper))
        {
            do
            {
                helper = $"{intrinsic.Text}_SD{_restructuredHelperCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                _restructuredHelperCount++;
            }
            while (IsIdentifierInSource(helper));

            _restructuredHelperNames[(declStart, intrinsic.Text, textureType)] = helper;
            if (!_restructuredHelperText.TryGetValue(declStart, out StringBuilder? text))
                _restructuredHelperText[declStart] = text = new StringBuilder();
            text.Append("float4 ").Append(helper)
                .Append('(').Append(textureType).Append(" sdTexture, SamplerState sdSampler, float4 sdCoord) { return ")
                .Append(RestructuredLegacySampleIntrinsics[intrinsic.Text])
                .Append("; } ");
        }

        int start = _tokenCharOffset[_pos];
        int end = _tokenCharOffset[_pos + lparen] + 1;
        return Result<(int, int, string)?, FxParseError>.Ok(
            (start, end, BuildDeclReplacement(start, end, $"{helper}({texture}, ")));
    }

    /// <summary>
    /// Where the top-level declaration that contains the token at <c>_pos</c> starts: walking
    /// back, the nearest <c>;</c> or <c>}</c> at global scope (or the start of the file), then
    /// forward past whitespace, comments and preprocessor lines to the first character of code.
    /// Returns that character offset and the index of the first token at or after it.
    ///
    /// <para>Character-level on purpose: the lexer drops <c>[</c> and <c>]</c>, so an attribute
    /// list (<c>[earlydepthstencil]</c>, <c>[numthreads(8, 8, 1)]</c>, several, on their own
    /// lines) is invisible as tokens, and the first TOKEN would be the attribute's name; the
    /// declaration starts at the <c>[</c>. A <c>;</c> inside an FX annotation block
    /// (<c>float4 PS(…) : COLOR0 &lt; string s = "x"; &gt;</c>) is not a terminator: at global
    /// scope the walk keeps an angle-bracket depth and only a <c>;</c> outside every <c>&lt; &gt;</c>
    /// ends the previous declaration. <paramref name="braceDepth"/> is how many braces enclose
    /// <c>_pos</c>; technique, pass and sampler-state blocks are balanced, so they read as whole
    /// top-level blocks on the way.</para>
    /// </summary>
    private (int Offset, int TokenIndex, int BoundaryEnd) TopLevelDeclarationStart(int braceDepth)
    {
        int level = braceDepth;
        int i = _pos - 1;
        for (; i >= 0; i--)
        {
            TokenKind k = _tokens[i].Kind;
            if (level == 0)
            {
                if (k is TokenKind.RBrace or TokenKind.Semicolon)
                    break;
                // An FX annotation block: skip it whole, so the ';' between its entries does not
                // read as the previous declaration's terminator. Only a '>' in annotation
                // position counts (right before the body's '{' or the declaration's ';'); a '>'
                // in an expression ('uniform bool hi = (2 > 1)') is not a bracket.
                if (k == TokenKind.RAngle && IsAnnotationClose(i) && AnnotationOpenBefore(i) is int open)
                {
                    i = open;
                    continue;
                }
            }
            if (k == TokenKind.RBrace)
                level++;
            else if (k == TokenKind.LBrace && level > 0)
                level--;
        }

        int offset = i >= 0 ? _tokenCharOffset[i] + _tokens[i].Text.Length : 0;
        int j = i + 1;
        while (true)
        {
            while (offset < _source.Length && char.IsWhiteSpace(_source[offset]))
                offset++;
            while (j < _pos && _tokenCharOffset[j] < offset)
                j++;
            if (j < _pos && _tokenCharOffset[j] == offset &&
                _tokens[j].Kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor)
            {
                offset += _tokens[j].Text.Length;
                j++;
                continue;
            }
            break;
        }

        return (offset, j, i >= 0 ? _tokenCharOffset[i] + _tokens[i].Text.Length : -1);
    }

    // Where each helper definition landed in the stripped text (see FxParseResult.RestructuredHelpers).
    private RestructuredHelperDefinition[] LocateRestructuredHelpers(string stripped)
    {
        var found = new List<RestructuredHelperDefinition>();
        foreach (((_, string intrinsic, _), string name) in _restructuredHelperNames)
        {
            int start = stripped.IndexOf("float4 " + name + "(", StringComparison.Ordinal);
            if (start < 0)
                continue;
            int end = stripped.IndexOf("; } ", start, StringComparison.Ordinal);
            end = end < 0 ? start : end + 3;
            int lineStart = stripped.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            if (start == 0)
                lineStart = 0;
            int line = 1;
            for (int c = 0; c < lineStart; c++)
                if (stripped[c] == '\n')
                    line++;
            found.Add(new RestructuredHelperDefinition(name, intrinsic, line, start - lineStart + 1, end - lineStart + 1));
        }
        return found.ToArray();
    }

    // A '>' closes an annotation when the next code token is the '{' of the body or the ';' of the
    // declaration.
    private bool IsAnnotationClose(int index)
    {
        int n = index + 1;
        while (n < _tokens.Count && _tokens[n].Kind is TokenKind.LineComment or TokenKind.BlockComment)
            n++;
        return n < _tokens.Count && _tokens[n].Kind is TokenKind.LBrace or TokenKind.Semicolon;
    }

    // The '<' that opens the annotation the '>' at 'close' ends: the nearest '<' before it that
    // starts an entry list ('<' then 'Type Name', or an empty '<>') and follows a declarator, a
    // semantic or a ')'. A comparison inside an entry's value ('int n = 3 > 2;') does not match.
    private int? AnnotationOpenBefore(int close)
    {
        for (int q = close - 1; q >= 0; q--)
        {
            TokenKind k = _tokens[q].Kind;
            if (k is TokenKind.LBrace or TokenKind.RBrace)
                return null;
            if (k != TokenKind.LAngle)
                continue;

            int p = q - 1;
            while (p >= 0 && _tokens[p].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                p--;
            if (p < 0 || _tokens[p].Kind is not (TokenKind.Identifier or TokenKind.RParen))
                continue;

            int n1 = q + 1;
            while (n1 < close && _tokens[n1].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                n1++;
            if (n1 == close)
                return q;                    // '<>'
            int n2 = n1 + 1;
            while (n2 < close && _tokens[n2].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                n2++;
            if (_tokens[n1].Kind == TokenKind.Identifier && n2 < close && _tokens[n2].Kind == TokenKind.Identifier)
                return q;
        }
        return null;
    }

    /// <summary>
    /// Whether <c>[from, to)</c> holds only whitespace and comments (no code, no directive, no
    /// character the lexer drops) and at least one line break.
    /// </summary>
    private bool OnlyTriviaAcrossALine(int from, int to)
    {
        bool lineBreak = false;
        for (int c = from; c < to; c++)
        {
            char ch = _source[c];
            if (ch == '\n')
            {
                lineBreak = true;
                continue;
            }
            if (char.IsWhiteSpace(ch))
                continue;
            int t = TokenStartingAt(c);
            if (t < 0 || _tokens[t].Kind is not (TokenKind.LineComment or TokenKind.BlockComment))
                return false;
            string text = _tokens[t].Text;
            if (text.Contains('\n'))
                lineBreak = true;
            c += text.Length - 1;
        }
        return lineBreak;
    }

    private int TokenStartingAt(int offset)
    {
        for (int t = 0; t < _tokens.Count; t++)
        {
            if (_tokens[t].Kind != TokenKind.EOF && _tokenCharOffset[t] == offset)
                return t;
        }
        return -1;
    }

    /// <summary>
    /// The start of the line above the one <paramref name="offset"/> is on, when that line holds
    /// only whitespace and comments and its first character is not inside a token (a block
    /// comment or a continued directive running through it); otherwise <c>null</c>.
    /// </summary>
    private int? LineAboveIfFree(int offset, int boundaryEnd)
    {
        int lineStart = offset;
        while (lineStart > 0 && _source[lineStart - 1] != '\n')
            lineStart--;
        if (lineStart == 0)
            return null;

        int above = lineStart - 1;            // the '\n' ending the line above
        while (above > 0 && _source[above - 1] != '\n')
            above--;

        // The line must lie after the previous declaration's terminator: a blank line inside a
        // multi-line initializer ('= { …\n\n};') is not at global scope.
        if (above < boundaryEnd)
            return null;

        // A backslash-newline splice joins a line onto the one before it (the preprocessor does
        // that before anything else), so a line spliced onto a '// comment \' is part of the
        // comment: text placed there is commented out.
        if (EndsInLineSplice(above))
            return null;

        for (int t = 0; t < _tokens.Count; t++)
        {
            int ts = _tokenCharOffset[t];
            int te = ts + _tokens[t].Text.Length;
            if (_tokens[t].Kind == TokenKind.EOF || te <= above || ts >= lineStart)
                continue;
            if (ts < above)
                return null;                  // a token runs into the line from above
            if (_tokens[t].Kind is not (TokenKind.LineComment or TokenKind.BlockComment))
                return null;                  // code or a directive on the line
            if (te > lineStart)
                return null;                  // a block comment runs on into the declaration's line
        }

        // A character the lexer drops (']', ':', '+', ...) is code, though it is no token.
        for (int c = above; c < lineStart; c++)
        {
            if (char.IsWhiteSpace(_source[c]))
                continue;
            bool inComment = false;
            for (int t = 0; t < _tokens.Count && !inComment; t++)
            {
                int ts = _tokenCharOffset[t];
                inComment = _tokens[t].Kind is TokenKind.LineComment or TokenKind.BlockComment &&
                            ts <= c && c < ts + _tokens[t].Text.Length;
            }
            if (!inComment)
                return null;
        }

        return above;
    }

    // Whether the line that ends right before 'lineStart' ends in a backslash (a splice that joins
    // the line starting at 'lineStart' onto it). Allows a '\r' before the '\n' and trailing
    // spaces/tabs after the backslash, which some preprocessors still splice on.
    private bool EndsInLineSplice(int lineStart)
    {
        int c = lineStart - 1;               // the '\n' ending the previous line
        if (c < 0 || _source[c] != '\n')
            return false;
        c--;
        while (c >= 0 && (_source[c] == '\r' || _source[c] == ' ' || _source[c] == '\t'))
            c--;
        return c >= 0 && _source[c] == '\\';
    }

    /// <summary>
    /// The first conditional directive in tokens <c>[from, to)</c> that leaves the conditional
    /// branch token <paramref name="from"/> starts in (an <c>#else</c>, <c>#elif</c> or
    /// <c>#endif</c> not matched by an <c>#if</c> opened inside the range), or <c>null</c>.
    /// Branches opened and closed inside the range are fine: a helper placed at
    /// <paramref name="from"/> references no global, so it is valid in all of them.
    /// </summary>
    private Token? FindConditionalBoundary(int from, int to)
    {
        int nesting = 0;
        for (int i = from; i < to; i++)
        {
            Token t = _tokens[i];
            if (t.Kind != TokenKind.Preprocessor)
                continue;
            string directive = PreprocessorDirectiveName(t.Text);
            if (directive is "if" or "ifdef" or "ifndef")
                nesting++;
            else if (directive == "endif")
            {
                if (nesting == 0)
                    return t;
                nesting--;
            }
            else if (directive.StartsWith("el", StringComparison.Ordinal) && nesting == 0)
                return t; // #else, #elif, #elifdef, #elifndef
        }
        return null;
    }

    private static string PreprocessorDirectiveName(string text)
    {
        int i = 0;
        while (i < text.Length && (text[i] == '#' || text[i] == ' ' || text[i] == '\t'))
            i++;
        int start = i;
        while (i < text.Length && char.IsAsciiLetter(text[i]))
            i++;
        return text[start..i];
    }

    /// <summary>
    /// The resource type a helper's texture parameter takes for <paramref name="texture"/>: the
    /// type of its declaration in this text (a modern <c>Texture2D</c> / <c>Texture2D&lt;float&gt;</c>
    /// spelled as written, a legacy <c>texture</c> keyword as the type it is rewritten to), or
    /// <c>Texture2D</c> when the text does not declare it (a synthesized texture, or one an
    /// <c>#include</c> declares). A typed texture must keep its type: DXC and vkd3d refuse to pass
    /// a <c>Texture2D&lt;float&gt;</c> to a <c>Texture2D</c> parameter. <c>null</c> when the text
    /// declares the texture with DIFFERENT types (one per <c>#if</c> branch): the right type then
    /// depends on the branch that compiles.
    /// </summary>
    private string? ResolveTextureParameterType(string texture)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (string type in DeclaredTextureTypes(texture))
            types.Add(NormalizeTextureType(type));
        return types.Count switch
        {
            0 => "Texture2D",
            1 => types.First(),
            _ => null,
        };
    }

    // One spelling per type: whitespace and comments removed, and a float4 element type, the
    // default, dropped ('Texture2D <float4>' is 'Texture2D').
    private static string NormalizeTextureType(string type)
    {
        string compact = new(type.Where(c => !char.IsWhiteSpace(c)).ToArray());
        int lt = compact.IndexOf('<');
        return lt > 0 && compact[(lt + 1)..] is "float4>" or "vector<float,4>>" ? compact[..lt] : compact;
    }

    // The types the texture is declared with at GLOBAL scope (outside every brace and parenthesis:
    // a function parameter or a local of the same name is a different object).
    private IEnumerable<string> DeclaredTextureTypes(string texture)
    {
        int braces = 0, parens = 0;
        for (int i = 1; i < _tokens.Count; i++)
        {
            switch (_tokens[i - 1].Kind)
            {
                case TokenKind.LBrace: braces++; break;
                case TokenKind.RBrace: braces = Math.Max(0, braces - 1); break;
                case TokenKind.LParen: parens++; break;
                case TokenKind.RParen: parens = Math.Max(0, parens - 1); break;
            }
            if (braces != 0 || parens != 0)
                continue;
            if (_tokens[i].Kind != TokenKind.Identifier || !string.Equals(_tokens[i].Text, texture, StringComparison.Ordinal))
                continue;

            int p = i - 1;
            while (p >= 0 && _tokens[p].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                p--;
            if (p < 0)
                continue;

            Token prev = _tokens[p];
            if (prev.Kind == TokenKind.Identifier)
            {
                if (LegacyTextureTypeKeywords.TryGetValue(prev.Text, out string? modern))
                    yield return modern;
                else if (IsModernTextureType(prev.Text))
                    yield return prev.Text;
                continue;
            }

            if (prev.Kind == TokenKind.RAngle)
            {
                int q = p - 1;
                int angle = 1;
                for (; q >= 0 && angle > 0; q--)
                {
                    if (_tokens[q].Kind == TokenKind.RAngle) angle++;
                    else if (_tokens[q].Kind == TokenKind.LAngle) angle--;
                    else if (_tokens[q].Kind is TokenKind.Semicolon or TokenKind.LBrace or TokenKind.RBrace) break;
                }
                if (angle != 0)
                    continue;
                // q is now one before the '<'; the type name precedes it.
                while (q >= 0 && _tokens[q].Kind is TokenKind.LineComment or TokenKind.BlockComment)
                    q--;
                if (q >= 0 && _tokens[q].Kind == TokenKind.Identifier && IsModernTextureType(_tokens[q].Text))
                {
                    int typeStart = _tokenCharOffset[q];
                    int typeEnd = _tokenCharOffset[p] + 1;
                    yield return CollapseToOneLine(typeStart, typeEnd);
                }
            }
        }
    }

    private static bool IsModernTextureType(string text) =>
        text is "Texture1D" or "Texture2D" or "Texture3D" or "TextureCube";

    // The source text of [start, end) on one line: comments blanked, line breaks as spaces.
    private string CollapseToOneLine(int start, int end)
    {
        var sb = new StringBuilder(_source, start, end - start, end - start);
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (_tokens[i].Kind is not (TokenKind.LineComment or TokenKind.BlockComment))
                continue;
            int s = _tokenCharOffset[i];
            int e = Math.Min(s + _tokens[i].Text.Length, end);
            for (int k = Math.Max(s, start); k < e; k++)
                sb[k - start] = ' ';
        }
        sb.Replace('\r', ' ').Replace('\n', ' ');
        return sb.ToString();
    }

    private bool IsIdentifierInSource(string name)
    {
        foreach (Token t in _tokens)
        {
            if (t.Kind == TokenKind.Identifier && string.Equals(t.Text, name, StringComparison.Ordinal))
                return true;
            if (t.Kind == TokenKind.Preprocessor && t.Text.Contains(name, StringComparison.Ordinal))
                return true;
        }
        return false;
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
        // The register number is RECORDED on the way past (issue #189) — the SM4
        // rewrite below drops the clause, so this is the last point at which it exists.
        while (Peek().Kind != TokenKind.Semicolon && Peek().Kind != TokenKind.EOF)
        {
            if (TryReadSamplerRegister() is { } slot)
                _explicitSamplerRegisters[nameTok.Text] = slot;
            else
                Consume();
        }

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
    /// At the current position, matches <c>register ( sN )</c> and consumes it, returning N.
    /// Returns null and consumes NOTHING otherwise, so the caller's swallow loop still makes
    /// progress. The ':' is already dropped by the lexer, so 'register' is a bare Identifier.
    ///
    /// <para>Deliberately narrow: only the <c>s</c> register class, only a plain decimal index,
    /// and only the exact 4-token shape. Anything else (a <c>space</c> operand, a macro, a
    /// malformed clause) is left to the ordinary swallow path — a missed register falls back to
    /// declaration-index allocation, which is the pre-issue-#189 behaviour, whereas a
    /// mis-parsed one would silently move a texture unit.</para>
    /// </summary>
    private int? TryReadSamplerRegister()
    {
        Token tok = Peek();
        if (tok.Kind != TokenKind.Identifier ||
            !string.Equals(tok.Text, "register", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // register ( sN )   — offsets from the current position, skipping trivia.
        int save = _pos;
        Consume();                       // 'register'
        SkipNonCodeTokens();
        if (Peek().Kind != TokenKind.LParen) { _pos = save; return null; }
        Consume();                       // '('
        SkipNonCodeTokens();

        Token slotTok = Peek();
        if (slotTok.Kind != TokenKind.Identifier ||
            slotTok.Text.Length < 2 ||
            (slotTok.Text[0] != 's' && slotTok.Text[0] != 'S') ||
            !int.TryParse(slotTok.Text.AsSpan(1), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out int slot))
        {
            _pos = save;
            return null;
        }

        Consume();                       // 'sN'
        SkipNonCodeTokens();
        if (Peek().Kind != TokenKind.RParen) { _pos = save; return null; }
        Consume();                       // ')'

        // MonoGame's sampler record stores the slot in a single byte, and the GL runtime
        // has 16 texture units. A number outside that is not something to bind to.
        if (slot is < 0 or > 255) { _pos = save; return null; }
        return slot;
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
        // optional '< ... >' annotation block or ': register(tN)' clause). The
        // ';' that terminates the DECLARATION is the one at angle-bracket depth 0:
        // an FX annotation block ('< string Name = "x"; float2 Dim = {1,1}; >') has
        // its own inner ';' separators between '<' and '>', and stopping at the
        // first of those would leak the rest of the annotation plus the trailing
        // '>;' into the rewritten output (DXC 'expected unqualified-id'). Track '<'
        // / '>' depth so only a top-level ';' ends the declaration.
        int angleDepth = 0;
        while (Peek().Kind != TokenKind.EOF &&
               !(Peek().Kind == TokenKind.Semicolon && angleDepth == 0))
        {
            switch (Peek().Kind)
            {
                case TokenKind.LAngle: angleDepth++; break;
                case TokenKind.RAngle: if (angleDepth > 0) angleDepth--; break;
            }
            Consume();
        }

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
    /// Returns the name of the function whose parameter list is closed by the
    /// <c>)</c> at the current position (<c>_pos</c> must be that RParen), or
    /// <c>null</c> when it cannot be determined. Used by the deferred COLOR-return
    /// rewrite (B6) to look the function up among the vertex-shader entry names.
    /// Scans backward from the RParen to the matching <c>(</c> (balancing nested
    /// parens, e.g. default-valued or cast parameters) and returns the identifier
    /// immediately preceding that <c>(</c> — the function name in
    /// '<c>retType Name ( params ) : COLOR {</c>'. Comments are skipped.
    /// </summary>
    private string? EnclosingFunctionName()
    {
        // Walk back to the '(' that matches the RParen at _pos.
        int depth = 0;
        int i = _pos;
        for (; i >= 0; i--)
        {
            TokenKind k = _tokens[i].Kind;
            if (k == TokenKind.RParen) depth++;
            else if (k == TokenKind.LParen)
            {
                depth--;
                if (depth == 0) break;
            }
        }
        if (i < 0)
            return null;

        // The function name is the nearest preceding code token before that '('.
        int j = i - 1;
        while (j >= 0 && _tokens[j].Kind is TokenKind.LineComment or TokenKind.BlockComment)
            j--;
        return j >= 0 && _tokens[j].Kind == TokenKind.Identifier ? _tokens[j].Text : null;
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
