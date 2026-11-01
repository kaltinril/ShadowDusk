#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.Lexer;

namespace ShadowDusk.HLSL;

/// <summary>
/// Legacy sampler PARAMETERS (issue #443): <c>float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }</c>.
///
/// <para>The SM4 rewrite turns a legacy sampler <c>B</c> into <c>Texture2D B_SDTexture; SamplerState B;</c>.
/// A function taking a <c>sampler2D</c> used to be copied verbatim, so a sampler both read directly
/// (rewritten) and passed to such a function reached the backend as a <c>SamplerState</c> handed to a
/// <c>sampler2D</c> parameter: vkd3d refused it (<c>E5005</c>), and <c>d3dcompiler_47</c> compiled the
/// parameter natively and gave the effect two texture records for <c>B</c>; DXC (OpenGL, Vulkan,
/// DirectX 12) rejects the <c>sampler2D</c> type outright. The rewrite now follows the parameter the
/// way it follows a declaration: <c>sampler2D s</c> becomes <c>Texture2D s_SDTexture, SamplerState s</c>,
/// <c>tex2D(s, uv)</c> inside the function becomes <c>s_SDTexture.Sample(s, uv)</c>, and every call
/// passes the sampler's texture beside it, <c>F(B_SDTexture, B, uv)</c>. Each sampler passed to such a
/// function is rewritten like a sampler a <c>tex2D</c> reads.</para>
///
/// <para><b>Only shapes every use of which the pre-scan sees and follows.</b> A function is rewritten
/// when it is defined once (prototypes allowed, with the same sampler positions), every sampler
/// parameter is spelled <c>[in] sampler|sampler2D name</c>, the parameter is only ever the first
/// argument of a <c>tex2D</c>-family call or passed on to another rewritten function, and every call
/// passes, at each sampler position, a sampler declared at global scope in a form the declaration
/// rewrite handles or a sampler parameter of the rewritten function it sits in, with as many
/// arguments as there are parameters. It is NOT rewritten when its name appears in a preprocessor
/// line (a macro may call it), when it is the target of a technique's <c>compile</c>, when a sampler it
/// is passed is also used in any way the rewrite cannot follow (passed to a macro or to a function
/// that is not rewritten), or when a call's argument cannot be bound once the declarations are
/// processed (the parse is then repeated without the function). Anything left out keeps its previous
/// handling, verbatim: such an effect compiles, or fails, exactly as before.</para>
/// </summary>
public sealed partial class FxPreParser
{
    // [in] sampler|sampler2D name, the whole text of one parameter.
    private static readonly Regex SamplerParameterText = new(
        @"^\s*(?:in\s+)?(?:sampler|sampler2D)\s+([A-Za-z_][A-Za-z0-9_]*)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    // Functions the caller excludes up front: a call to one could not be bound in an earlier parse.
    private readonly IReadOnlySet<string> _excludedSamplerParameterFunctions;

    // First token of a sampler parameter -> the delimiter token after it, its character span and replacement.
    private readonly Dictionary<int, (int Delimiter, int Start, int End, string Replacement, string Function)> _samplerParameterRewrites = new();

    // A sampler argument's token -> the function called, the argument, and whether it is a
    // sampler PARAMETER of the rewritten function the call sits in (else a global sampler).
    private readonly Dictionary<int, (string Function, string Argument, bool Parameter)> _samplerArgumentRewrites = new();

    // Token ranges (inclusive) of the rewritten functions' bodies -> their sampler parameters.
    private readonly List<(int Start, int End, IReadOnlySet<string> Parameters)> _samplerParameterScopes = new();

    // Rewritten function -> its texture parameters (FxParseResult.SamplerParameterTextures).
    private readonly Dictionary<string, List<string>> _samplerParameterTextures = new(StringComparer.Ordinal);

    // Rewrites the main loop applied (token indices), and the functions one of whose calls could not be bound.
    private readonly HashSet<int> _appliedSamplerParameterRewrites = new();
    private readonly HashSet<string> _unboundSamplerParameterFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// Parses with the sampler-parameter rewrite, repeating the parse without every function one
    /// of whose rewrites could not be applied (a call whose sampler has no binding when it is
    /// reached, a token the main loop consumed in another rewrite), so a half-rewritten function
    /// never reaches the compiler. Bounded by the number of functions.
    /// </summary>
    private static Result<FxParseResult, FxParseError> ParseFileExcludingUnboundSamplerParameterFunctions(
        Func<IReadOnlySet<string>, FxPreParser> create)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            FxPreParser parser = create(excluded);
            Result<FxParseResult, FxParseError> result = parser.ParseFile();
            if (result.IsFailure)
                return result;
            var unbound = parser.UnappliedSamplerParameterFunctions();
            if (unbound.Count == 0 || !unbound.Except(excluded, StringComparer.Ordinal).Any())
                return result;
            excluded.UnionWith(unbound);
        }
    }

    private HashSet<string> UnappliedSamplerParameterFunctions()
    {
        var functions = new HashSet<string>(_unboundSamplerParameterFunctions, StringComparer.Ordinal);
        foreach ((int token, var rewrite) in _samplerParameterRewrites)
        {
            if (!_appliedSamplerParameterRewrites.Contains(token))
                functions.Add(rewrite.Function);
        }
        foreach ((int token, var rewrite) in _samplerArgumentRewrites)
        {
            if (!_appliedSamplerParameterRewrites.Contains(token))
                functions.Add(rewrite.Function);
        }
        return functions;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> SamplerParameterTexturesResult() =>
        _samplerParameterTextures.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.Ordinal);

    /// <summary>The texture <paramref name="sampler"/> samples through at <c>_pos</c>: a sampler parameter of the rewritten function around it, else the global binding.</summary>
    private bool TryGetBoundTexture(string sampler, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? texture)
    {
        foreach ((int start, int end, IReadOnlySet<string> parameters) in _samplerParameterScopes)
        {
            if (_pos >= start && _pos <= end && parameters.Contains(sampler))
            {
                texture = SynthTextureName(sampler);
                return true;
            }
        }
        return _samplerTextureBindings.TryGetValue(sampler, out texture);
    }

    /// <summary>At a rewritten sampler parameter: replaces it and moves to the delimiter after it.</summary>
    private bool TryRewriteSamplerParameter(List<(int Start, int End, string Replacement)> replacedRanges)
    {
        if (!_samplerParameterRewrites.TryGetValue(_pos, out var rewrite))
            return false;
        replacedRanges.Add((rewrite.Start, rewrite.End, BuildDeclReplacement(rewrite.Start, rewrite.End, rewrite.Replacement)));
        _appliedSamplerParameterRewrites.Add(_pos);
        _pos = rewrite.Delimiter;
        return true;
    }

    /// <summary>At a sampler argument of a call to a rewritten function: passes its texture beside it.</summary>
    private bool TryRewriteSamplerArgument(Token tok, List<(int Start, int End, string Replacement)> replacedRanges)
    {
        if (!_samplerArgumentRewrites.TryGetValue(_pos, out var rewrite))
            return false;
        string? texture = rewrite.Parameter
            ? SynthTextureName(rewrite.Argument)
            : _samplerTextureBindings.GetValueOrDefault(rewrite.Argument);
        if (texture is null)
        {
            // Not bound when reached (a declaration form the rewrite left alone): the parse is
            // repeated without this function (ParseFileExcludingUnboundSamplerParameterFunctions).
            _unboundSamplerParameterFunctions.Add(rewrite.Function);
        }
        else
        {
            int start = _tokenCharOffset[_pos];
            replacedRanges.Add((start, start + tok.Text.Length, $"{texture}, {rewrite.Argument}"));
        }
        _appliedSamplerParameterRewrites.Add(_pos);
        Consume();
        return true;
    }

    private sealed class SamplerParameterHeader
    {
        public required int NameIndex { get; init; }       // code index of the name
        public required int Open { get; init; }            // code index of '('
        public required int Close { get; init; }           // code index of ')'
        public int BodyOpen { get; set; } = -1;            // code index of '{', or -1 for a prototype
        public List<(int First, int Last, int Delimiter, string? Sampler, bool Unmodelled)> Parameters { get; } = new();
        public string Name { get; init; } = "";
    }

    /// <summary>
    /// Finds the functions whose legacy sampler parameters the rewrite follows, records every
    /// rewrite they need, and returns the GLOBAL samplers passed to them (rewritten like samplers a
    /// <c>tex2D</c> reads). RewriteToSm4 only; empty when anything about a function is not modelled.
    /// </summary>
    private IReadOnlySet<string> CollectSamplerParameterFunctions()
    {
        var passed = new HashSet<string>(StringComparer.Ordinal);
        if (_mode != FxSourceMode.RewriteToSm4)
            return passed;

        // ---- Code tokens, brackets, brace depth -------------------------------------------------
        var code = new List<int>();
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (_tokens[i].Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor or TokenKind.EOF))
                code.Add(i);
        }
        Token T(int c) => _tokens[code[c]];
        bool Is(int c, TokenKind kind) => c >= 0 && c < code.Count && T(c).Kind == kind;
        bool IsWord(int c, string word) => Is(c, TokenKind.Identifier) && string.Equals(T(c).Text, word, StringComparison.OrdinalIgnoreCase);

        var match = new int[code.Count];
        var depth = new int[code.Count];
        var stack = new Stack<int>();
        int braces = 0;
        for (int c = 0; c < code.Count; c++)
        {
            TokenKind k = T(c).Kind;
            if (k == TokenKind.RBrace)
                braces--;
            depth[c] = braces;
            if (k == TokenKind.LBrace)
                braces++;
            if (k is TokenKind.LParen or TokenKind.LBrace)
            {
                stack.Push(c);
            }
            else if (k is TokenKind.RParen or TokenKind.RBrace)
            {
                if (stack.Count == 0)
                    return passed;
                int open = stack.Pop();
                if ((T(open).Kind == TokenKind.LParen) != (k == TokenKind.RParen))
                    return passed;
                match[open] = c;
                match[c] = open;
            }
        }
        if (stack.Count != 0 || braces != 0)
            return passed;

        int CharStart(int c) => _tokenCharOffset[code[c]];
        int CharEnd(int c) => _tokenCharOffset[code[c]] + T(c).Text.Length;

        // The comma-separated items between '(' at `open` and its ')': (first, last, delimiter) code indices.
        List<(int First, int Last, int Delimiter)> Items(int open)
        {
            var items = new List<(int, int, int)>();
            int close = match[open];
            int first = open + 1;
            for (int c = open + 1; c <= close; c++)
            {
                if (c == close || Is(c, TokenKind.Comma))
                {
                    if (c > first || items.Count > 0 || c != close)
                        items.Add((first, c - 1, c));
                    first = c + 1;
                    continue;
                }
                if (T(c).Kind is TokenKind.LParen or TokenKind.LBrace)
                    c = match[c];
            }
            return items;
        }

        // The text between two delimiters, comments included (an item's own text).
        string ItemText(int delimiterBefore, int delimiterAfter) =>
            _source[CharEnd(delimiterBefore)..CharStart(delimiterAfter)];

        // ---- Global legacy samplers the declaration rewrite handles -------------------------------
        var globalSamplers = new HashSet<string>(StringComparer.Ordinal);
        for (int c = 0; c + 2 < code.Count; c++)
        {
            if (depth[c] != 0 || !(IsWord(c, "sampler") || IsWord(c, "sampler2D")) || !Is(c + 1, TokenKind.Identifier))
                continue;
            bool rewritable = Is(c + 2, TokenKind.Semicolon) || IsWord(c + 2, "register") || Is(c + 2, TokenKind.LBrace)
                              || (Is(c + 2, TokenKind.Equals) && IsWord(c + 3, "sampler_state"));
            if (rewritable)
                globalSamplers.Add(T(c + 1).Text);
        }

        // ---- Technique blocks: a sampler or function named in one is not a use -----------------
        var inTechnique = new bool[code.Count];
        for (int c = 0; c < code.Count; c++)
        {
            if (depth[c] != 0 || !(IsWord(c, "technique") || IsWord(c, "technique10") || IsWord(c, "technique11")))
                continue;
            int b = c + 1;
            while (b < code.Count && !Is(b, TokenKind.LBrace) && !Is(b, TokenKind.Semicolon) && !Is(b, TokenKind.LParen))
                b++;
            if (Is(b, TokenKind.LBrace))
            {
                for (int k = c; k <= match[b]; k++)
                    inTechnique[k] = true;
            }
        }

        // ---- Function headers at global scope -----------------------------------------------------
        var headers = new List<SamplerParameterHeader>();
        var headerNameIndices = new HashSet<int>();
        for (int c = 1; c + 1 < code.Count; c++)
        {
            if (depth[c] != 0 || inTechnique[c] || !Is(c, TokenKind.Identifier) || !Is(c + 1, TokenKind.LParen)
                || !(Is(c - 1, TokenKind.Identifier) || Is(c - 1, TokenKind.RAngle)))
            {
                continue;
            }
            int close = match[c + 1];
            int after = close + 1;
            if (Is(after, TokenKind.Identifier) && Is(after + 1, TokenKind.LBrace))
                after++; // a return semantic (the lexer drops the ':')
            if (!Is(after, TokenKind.LBrace) && !Is(after, TokenKind.Semicolon))
                continue;
            var header = new SamplerParameterHeader
            {
                Name = T(c).Text,
                NameIndex = c,
                Open = c + 1,
                Close = close,
                BodyOpen = Is(after, TokenKind.LBrace) ? after : -1,
            };
            int previous = c + 1;
            foreach ((int first, int last, int delimiter) in Items(c + 1))
            {
                bool samplerish = false;
                for (int k = first; k <= last; k++)
                {
                    if (Is(k, TokenKind.Identifier) && SamplerTypeKeywords.Contains(T(k).Text))
                        samplerish = true;
                }
                Match m = SamplerParameterText.Match(ItemText(previous, delimiter));
                header.Parameters.Add((first, last, delimiter, m.Success ? m.Groups[1].Value : null, samplerish && !m.Success));
                previous = delimiter;
            }
            headers.Add(header);
            headerNameIndices.Add(c);
            if (header.BodyOpen >= 0)
                c = match[header.BodyOpen];
        }

        // ---- Candidates ----------------------------------------------------------------------------
        var byName = headers.GroupBy(h => h.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var candidates = new Dictionary<string, SamplerParameterHeader>(StringComparer.Ordinal); // name -> definition
        var excluded = new HashSet<string>(_excludedSamplerParameterFunctions, StringComparer.Ordinal);
        foreach ((string name, List<SamplerParameterHeader> list) in byName)
        {
            if (!list.Any(h => h.Parameters.Any(p => p.Sampler is not null)))
                continue;
            List<SamplerParameterHeader> definitions = list.Where(h => h.BodyOpen >= 0).ToList();
            string Shape(SamplerParameterHeader h) =>
                string.Join(",", h.Parameters.Select(p => p.Sampler is null ? "v" : "s"));
            if (definitions.Count != 1 || list.Any(h => h.Parameters.Any(p => p.Unmodelled))
                || list.Select(Shape).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                excluded.Add(name);
                continue;
            }
            candidates[name] = definitions[0];
        }
        if (candidates.Count == 0)
            return passed;

        // A name in a preprocessor line (a macro may call the function or name the sampler) is a
        // use the raw tokens cannot see.
        bool InDirective(string name)
        {
            var word = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(name) + "(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
            foreach (Token t in _tokens)
            {
                if (t.Kind == TokenKind.Preprocessor && word.IsMatch(t.Text))
                    return true;
            }
            return false;
        }
        foreach (string name in candidates.Keys)
        {
            if (InDirective(name))
                excluded.Add(name);
        }

        // The function definition (any, rewritten or not) whose body holds code index `c`.
        SamplerParameterHeader? EnclosingDefinition(int c) =>
            headers.FirstOrDefault(h => h.BodyOpen >= 0 && c > h.BodyOpen && c < match[h.BodyOpen]);

        // A name declared in a function body or parameter list (conservatively: after an identifier
        // other than 'return', before what can follow a declarator).
        bool DeclaredIn(SamplerParameterHeader fn, string name)
        {
            foreach (var p in fn.Parameters)
            {
                for (int k = p.First; k <= p.Last; k++)
                {
                    if (Is(k, TokenKind.Identifier) && T(k).Text == name)
                        return true;
                }
            }
            for (int k = fn.BodyOpen + 1; k < match[fn.BodyOpen]; k++)
            {
                if (Is(k, TokenKind.Identifier) && T(k).Text == name && Is(k - 1, TokenKind.Identifier)
                    && !string.Equals(T(k - 1).Text, "return", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // ---- Calls ---------------------------------------------------------------------------------
        // (callee, enclosing rewritten-candidate name or null, sampler arguments: code index, name, is a parameter)
        var calls = new List<(string Callee, string? Enclosing, List<(int Index, string Name, bool Parameter)> Args)>();
        var nestedUses = new Dictionary<int, string>(); // code index of a parameter passed on -> callee
        foreach (string name in candidates.Keys.ToList())
        {
            SamplerParameterHeader def = candidates[name];
            for (int c = 0; c < code.Count; c++)
            {
                if (!Is(c, TokenKind.Identifier) || T(c).Text != name || headerNameIndices.Contains(c))
                    continue;
                if (inTechnique[c] || Is(c - 1, TokenKind.Dot) || !Is(c + 1, TokenKind.LParen))
                {
                    excluded.Add(name);
                    break;
                }
                var items = Items(c + 1);
                if (items.Count != def.Parameters.Count)
                {
                    excluded.Add(name);
                    break;
                }
                SamplerParameterHeader? enclosing = EnclosingDefinition(c);
                var args = new List<(int, string, bool)>();
                int previous = c + 1;
                bool ok = true;
                for (int j = 0; j < items.Count && ok; j++)
                {
                    string text = ItemText(previous, items[j].Delimiter).Trim();
                    previous = items[j].Delimiter;
                    if (def.Parameters[j].Sampler is null)
                        continue;
                    if (!PlainIdentifier.IsMatch(text) || items[j].First != items[j].Last)
                    {
                        ok = false;
                        break;
                    }
                    bool isParameter = enclosing is not null && candidates.ContainsKey(enclosing.Name)
                                       && enclosing.Parameters.Any(p => p.Sampler == text);
                    if (isParameter)
                    {
                        args.Add((items[j].First, text, true));
                        nestedUses[items[j].First] = name;
                    }
                    else if (globalSamplers.Contains(text) && (enclosing is null || !DeclaredIn(enclosing, text)))
                    {
                        args.Add((items[j].First, text, false));
                    }
                    else
                    {
                        ok = false;
                    }
                }
                if (!ok)
                {
                    excluded.Add(name);
                    break;
                }
                calls.Add((name, enclosing is not null && candidates.ContainsKey(enclosing.Name) ? enclosing.Name : null, args));
            }
        }

        // A function nothing calls is left as it was: there is no sampler to bind its parameter to,
        // and leaving it keeps every effect that does not need the rewrite byte for byte as before.
        foreach (string name in candidates.Keys)
        {
            if (!calls.Any(x => string.Equals(x.Callee, name, StringComparison.Ordinal)))
                excluded.Add(name);
        }

        // ---- Parameter uses inside each definition -------------------------------------------------
        foreach ((string name, SamplerParameterHeader def) in candidates)
        {
            var parameters = def.Parameters.Where(p => p.Sampler is not null).Select(p => p.Sampler!).ToHashSet(StringComparer.Ordinal);
            for (int k = def.BodyOpen + 1; k < match[def.BodyOpen] && !excluded.Contains(name); k++)
            {
                if (!Is(k, TokenKind.Identifier))
                    continue;
                string text = T(k).Text;
                if (parameters.Any(p => string.Equals(SynthTextureName(p), text, StringComparison.Ordinal)))
                {
                    excluded.Add(name); // the texture parameter's name is already taken in the body
                    break;
                }
                if (!parameters.Contains(text) || Is(k - 1, TokenKind.Dot))
                    continue;
                bool intrinsicArgument = Is(k - 1, TokenKind.LParen) && Is(k - 2, TokenKind.Identifier)
                                         && (LegacySampleIntrinsics.ContainsKey(T(k - 2).Text)
                                             || RestructuredLegacySampleIntrinsics.ContainsKey(T(k - 2).Text))
                                         && Is(k + 1, TokenKind.Comma);
                if (!intrinsicArgument && !nestedUses.ContainsKey(k))
                    excluded.Add(name);
            }
        }

        // ---- Fixed point: a function left out takes every function it is tied to with it ---------
        bool changed = true;
        while (changed)
        {
            changed = false;
            void Exclude(string name)
            {
                if (excluded.Add(name))
                    changed = true;
            }

            foreach (var call in calls)
            {
                bool calleeOut = excluded.Contains(call.Callee);
                bool enclosingOut = call.Enclosing is not null && excluded.Contains(call.Enclosing);
                if (calleeOut && call.Enclosing is not null && call.Args.Any(a => a.Parameter))
                    Exclude(call.Enclosing); // its parameter is passed to a function left verbatim
                if (enclosingOut && call.Args.Any(a => a.Parameter))
                    Exclude(call.Callee);    // the parameter it is passed stays a sampler2D
            }

            // A global sampler the rewrite would now turn into a SamplerState must have no use it
            // cannot follow: anything but a tex2D-family read and a sampler argument of a call to a
            // rewritten function (its declarations and technique blocks aside).
            var receivers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var followed = new HashSet<int>();
            foreach (var call in calls.Where(x => !excluded.Contains(x.Callee)))
            {
                foreach (var arg in call.Args.Where(a => !a.Parameter))
                {
                    if (!receivers.TryGetValue(arg.Name, out var set))
                        receivers[arg.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(call.Callee);
                    followed.Add(arg.Index);
                }
            }
            foreach ((string sampler, HashSet<string> functions) in receivers)
            {
                if (SamplerUseIsFollowed(sampler, followed))
                    continue;
                foreach (string f in functions)
                    Exclude(f);
            }
        }

        bool SamplerUseIsFollowed(string sampler, HashSet<int> followed)
        {
            if (InDirective(sampler))
                return false;
            for (int c = 0; c < code.Count; c++)
            {
                if (!Is(c, TokenKind.Identifier) || T(c).Text != sampler || inTechnique[c] || followed.Contains(c))
                    continue;
                bool declaration = depth[c] == 0 && Is(c - 1, TokenKind.Identifier) && SamplerTypeKeywords.Contains(T(c - 1).Text);
                bool intrinsicArgument = Is(c - 1, TokenKind.LParen) && Is(c - 2, TokenKind.Identifier)
                                         && (LegacySampleIntrinsics.ContainsKey(T(c - 2).Text)
                                             || RestructuredLegacySampleIntrinsics.ContainsKey(T(c - 2).Text));
                SamplerParameterHeader? enclosing = EnclosingDefinition(c);
                bool shadowed = enclosing is not null && DeclaredIn(enclosing, sampler);
                if (!declaration && !intrinsicArgument && !shadowed)
                    return false;
            }
            return true;
        }

        // ---- Record the rewrites -------------------------------------------------------------------
        foreach ((string name, SamplerParameterHeader def) in candidates)
        {
            if (excluded.Contains(name))
                continue;
            var textures = new List<string>();
            foreach (SamplerParameterHeader header in byName[name])
            {
                foreach (var p in header.Parameters.Where(p => p.Sampler is not null))
                {
                    _samplerParameterRewrites[code[p.First]] = (code[p.Delimiter], CharStart(p.First), CharEnd(p.Last),
                        $"Texture2D {SynthTextureName(p.Sampler!)}, SamplerState {p.Sampler}", name);
                    if (ReferenceEquals(header, def))
                        textures.Add(SynthTextureName(p.Sampler!));
                }
            }
            _samplerParameterTextures[name] = textures;
            _samplerParameterScopes.Add((code[def.BodyOpen], code[match[def.BodyOpen]],
                def.Parameters.Where(p => p.Sampler is not null).Select(p => p.Sampler!).ToHashSet(StringComparer.Ordinal)));
        }
        foreach (var call in calls)
        {
            if (excluded.Contains(call.Callee))
                continue;
            foreach (var arg in call.Args)
            {
                _samplerArgumentRewrites[code[arg.Index]] = (call.Callee, arg.Name, arg.Parameter);
                if (!arg.Parameter)
                    passed.Add(arg.Name);
            }
        }
        return passed;
    }
}
