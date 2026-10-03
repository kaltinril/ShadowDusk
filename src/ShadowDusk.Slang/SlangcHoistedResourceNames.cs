#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Slang;

/// <summary>
/// Issue #302: gives a texture slangc hoisted out of a combined sampler the name the author
/// wrote, and rejects a hoisted texture that has no author-written name.
/// </summary>
/// <remarks>
/// <para>slangc hoists every resource out of an aggregate under a name it generates,
/// <c>&lt;aggregate&gt;_&lt;field&gt;_&lt;n&gt;</c>, even with <c>-no-mangle</c> (measured,
/// v2026.14.1): a combined <c>Sampler2D Comb</c> is emitted as
/// <c>Texture2D&lt;float4 &gt; Comb_texture_0</c> plus <c>SamplerState Comb_sampler_0</c> (the
/// same for <c>Sampler1D</c>/<c>3D</c>/<c>Cube</c>, the <c>Array</c> and <c>Shadow</c> forms and
/// arrays of them), a struct global's field <c>gM.t</c> as <c>gM_t_0</c>, a texture declared
/// inside a <c>cbuffer C</c> as <c>C_T_0</c>, and an entry-point <c>uniform Texture2D T</c> as
/// <c>entryPointParams_T_0</c>. A global texture's name IS its parameter name in the compiled
/// effect, so the consumer's <c>effect.Parameters["Comb"]</c> found nothing.</para>
/// <para><b>What the name becomes.</b> Measured against the reference compiler and the
/// <c>.fx</c> route: <c>Texture2D Comb; SamplerState CombSampler;</c> reflects the texture as
/// <c>Comb</c> on every target, and <c>mgfxc</c> names a legacy <c>sampler2D Comb</c>'s
/// parameter <c>Comb</c> too. So the texture half of a combined sampler declared as a global is
/// renamed to that global's name, in slangc's HLSL text, before the <c>.fx</c> is assembled:
/// the rest of the pipeline sees the declaration an author would have written by hand. The
/// sampler half keeps slangc's name (<c>Comb_sampler_0</c>): no author-written name exists for
/// it, the texture already holds <c>Comb</c>, and nothing binds a sampler by name.</para>
/// <para><b>What is rejected.</b> A texture hoisted out of anything else (a struct global, a
/// cbuffer or <c>ParameterBlock</c>, an entry-point uniform, a combined sampler nested in one of
/// those) has no single name the author wrote, and the reference compiler has no convention to
/// follow: <c>fxc</c> rejects a texture inside a struct outright (<c>X3090</c>, measured through
/// <c>mgfxc</c> 3.8.4.1 on both profiles). It fails as <see cref="HoistedTextureCode"/> instead
/// of reaching the effect under a generated name. A hoisted SAMPLER is left alone: a sampler
/// is never set by name.</para>
/// <para><b>Telling a generated name from an author's.</b> An author may name a global
/// <c>tex_layer_0</c>, which has the same shape as a hoisted name, and renaming or rejecting
/// that would break a working shader. A name is treated as generated only when the source
/// provably never spells it:</para>
/// <list type="bullet">
/// <item>Free, from the raw source: its text and the <c>-D</c> values hold no such identifier
/// (comments and string contents masked) and nothing in them can form one (<c>##</c>, a
/// backslash splice, an <c>#include</c>, an <c>import</c>).</item>
/// <item>Free, the other way: slangc locates an author's own global at its declaration line
/// (measured: exact for every author declaration, while a hoisted resource's <c>#line</c> is
/// slangc's core module or a stale line), so a name spelled on the entry-source line slangc
/// names for it is the author's.</item>
/// <item>Otherwise from slangc's own preprocess-only output (<c>-E</c>), the texts the register
/// pass already read when it ran (issue #292), else one extra run over the entry source. A name
/// no read text spells is generated when nothing else was compiled, or when a read text declares
/// the global it was hoisted from; anything still undecided fails as
/// <see cref="AuthorshipUnprovenCode"/>.</item>
/// </list>
/// <para>slangc does not keep a hoisted name apart from an author global of the same spelling:
/// <c>Sampler2D Comb; Texture2D Comb_texture_0;</c> comes back as two declarations of
/// <c>Comb_texture_0</c> (measured). That, and an author name that is already taken in the
/// output, fail as <see cref="NameCollisionCode"/>.</para>
/// </remarks>
internal static class SlangcHoistedResourceNames
{
    /// <summary><c>SD0640</c>: a texture hoisted out of an aggregate has no author-written name.</summary>
    public const string HoistedTextureCode = "SD0640";

    /// <summary><c>SD0641</c>: a hoisted name, or the author name it maps to, is used twice.</summary>
    public const string NameCollisionCode = "SD0641";

    /// <summary><c>SD0642</c>: no text ShadowDusk read proves whether a name is the author's.</summary>
    public const string AuthorshipUnprovenCode = "SD0642";

    // A global texture/sampler declaration in slangc's emission, with or without a register:
    // 'Texture2D<float4 > Comb_texture_0 : register(t0);', 'SamplerState  Arr_sampler_0[int(2)];'.
    private static readonly Regex GlobalDeclaration = new(
        $$"""^\s*(?:(?<tex>{{SlangcResourceTypes.Texture}})|{{SlangcResourceTypes.Sampler}})(?:\s*<[^>;{}]*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^\];{}]*\]\s*)?(?::\s*register\s*\([^()]*\)\s*)?;""",
        RegexOptions.Compiled);

    private static readonly Regex LineDirective = new(
        """^\s*#line\s+(?<line>\d+)(?:\s+"(?<file>[^"]*)")?""",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new("""[A-Za-z_]\w*""", RegexOptions.Compiled);

    // The texture half of a combined sampler: '<global>_texture_<n>'.
    private static readonly Regex CombinedTexture = new("""^(?<base>.+)_texture_\d+$""", RegexOptions.Compiled);

    // Prefixes slangc puts in front of a hoisted name for its own parameter groups.
    private static readonly string[] GroupPrefixes = ["globalParams_", "entryPointParams_"];

    /// <summary>A global texture or sampler declaration in slangc's emission.</summary>
    /// <param name="Name">The emitted name.</param>
    /// <param name="IsTexture">A texture object; otherwise a sampler state.</param>
    /// <param name="File">The file slangc's <c>#line</c> names.</param>
    /// <param name="Line">The line in <paramref name="File"/>.</param>
    public readonly record struct GlobalResource(string Name, bool IsTexture, string File, int Line);

    /// <summary>The outcome of <see cref="Decide"/>.</summary>
    /// <param name="Renames">Emitted name to author name, to apply with <see cref="Apply"/>.</param>
    /// <param name="NeedsPreprocess">Nothing free decides: call again with slangc's
    /// preprocess-only output.</param>
    /// <param name="Error">The compile must fail with this.</param>
    public sealed record Decision(
        IReadOnlyDictionary<string, string> Renames, bool NeedsPreprocess, ShaderError? Error)
    {
        public static Decision Nothing { get; } = new(new Dictionary<string, string>(), false, null);
    }

    /// <summary>
    /// What slangc's preprocess-only (<c>-E</c>) output says about names: every identifier the
    /// read files spell, and every global they declare.
    /// </summary>
    public sealed class PreprocessedTexts
    {
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
        private readonly List<SlangcRegisterStripper.AuthorBindings> _bindings = [];

        /// <param name="entry">The entry source's preprocessed text.</param>
        /// <param name="others">The preprocessed text of every other file that was read.</param>
        public PreprocessedTexts(string entry, IEnumerable<string> others)
        {
            Add(entry);
            // '#include' is expanded by -E; 'import' and '__include' are not (measured), so an
            // entry text that spells neither is everything slangc compiled.
            Complete = !_tokens.Contains("import") && !_tokens.Contains("__include");
            foreach (string other in others)
                Add(other);
        }

        /// <summary>The entry source's text is everything slangc compiled.</summary>
        public bool Complete { get; }

        private void Add(string text)
        {
            foreach (Match m in Identifier.Matches(SlangSourceMask.Mask(text)))
                _tokens.Add(m.Value);
            _bindings.Add(SlangcRegisterStripper.AuthorBindings.Parse(text));
        }

        public bool Spells(string name) => _tokens.Contains(name);

        public bool Declares(string name) => _bindings.Any(b => b.DeclaresGlobal(name));
    }

    /// <summary>
    /// Every global texture/sampler declaration in <paramref name="hlsl"/> (slangc's emission,
    /// one entry's or the merged one), with the location its <c>#line</c> directives give it.
    /// </summary>
    public static IReadOnlyList<GlobalResource> FindGlobals(string hlsl)
    {
        var found = new List<GlobalResource>();
        string file = SlangcRegisterStripper.EntrySourceFile;
        int line = 1;
        int depth = 0;
        foreach (string text in hlsl.Split('\n'))
        {
            Match directive = LineDirective.Match(text);
            if (directive.Success)
            {
                line = int.Parse(directive.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (directive.Groups["file"].Success)
                    file = directive.Groups["file"].Value;
                continue;
            }
            if (depth == 0)
            {
                Match m = GlobalDeclaration.Match(text);
                if (m.Success)
                    found.Add(new GlobalResource(m.Groups["name"].Value, m.Groups["tex"].Success, file, line));
            }
            foreach (char c in text)
            {
                if (c == '{')
                    depth++;
                else if (c == '}')
                    depth--;
            }
            line++;
        }
        return found;
    }

    /// <summary>
    /// Decides the author name of every hoisted texture in <paramref name="hlsl"/>.
    /// </summary>
    /// <param name="hlsl">slangc's emission for the whole effect (merged across entry points).</param>
    /// <param name="slangSource">The entry source, as the author wrote it.</param>
    /// <param name="sourceName">The entry source's name, for diagnostics.</param>
    /// <param name="defines">The compile's <c>-D</c> values.</param>
    /// <param name="texts">slangc's preprocess-only output, when a pass has run; null to decide
    /// from the raw source alone, which may answer <see cref="Decision.NeedsPreprocess"/>.</param>
    public static Decision Decide(
        string hlsl,
        string slangSource,
        string sourceName,
        IReadOnlyList<UserDefine> defines,
        PreprocessedTexts? texts)
    {
        IReadOnlyList<GlobalResource> globals = FindGlobals(hlsl);
        List<GlobalResource> candidates = globals
            .Where(g => g.IsTexture && SlangcRegisterStripper.HoistBases(g.Name).Any())
            .ToList();
        if (candidates.Count == 0)
            return Decision.Nothing;

        var raw = new RawSource(slangSource, defines);

        // slangc keeps no hoisted name apart from an author global of the same spelling: both
        // come back under the one name (measured). No name can tell them apart downstream.
        foreach (GlobalResource candidate in candidates)
        {
            if (CountDeclarations(hlsl, candidate.Name) > 1)
            {
                return Failed(new ShaderError(
                    File: sourceName, Line: 0, Column: 0, Code: NameCollisionCode,
                    Message: $"slangc's output declares the global '{candidate.Name}' more than once: slangc generates that " +
                             "name for a resource it hoists out of an aggregate (a combined sampler such as Sampler2D, or a " +
                             $"struct), and the source also declares a global spelled '{candidate.Name}'. slangc does not keep " +
                             "the two apart, so they cannot both be effect parameters. Rename the global."));
            }
        }

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        bool needsPreprocess = false;
        foreach (GlobalResource candidate in candidates.DistinctBy(c => c.Name))
        {
            Origin origin = Classify(candidate, raw, texts);
            if (origin == Origin.Author)
                continue;
            if (origin == Origin.NeedsPreprocess)
            {
                needsPreprocess = true;
                continue;
            }
            if (origin == Origin.Unproven)
            {
                return Failed(new ShaderError(
                    File: sourceName, Line: 0, Column: 0, Code: AuthorshipUnprovenCode,
                    Message: $"ShadowDusk cannot prove whether the texture '{candidate.Name}' in slangc's output carries a name " +
                             "the author wrote or one slangc generated for a texture it hoisted out of an aggregate (a combined " +
                             $"sampler or a struct): no file ShadowDusk preprocessed spells '{candidate.Name}' or declares a " +
                             "global it could have been hoisted from, and the source imports files that were not read (an " +
                             "import by module name is not followed). ShadowDusk will not guess: renaming an author's parameter " +
                             "and leaving a generated name in the effect would both break effect.Parameters lookups silently. " +
                             "Declare the texture in the entry source, or in a module the entry source imports by quoted path."));
            }

            // Generated. The texture half of a combined sampler that is itself a global takes
            // that global's name; nothing else has a name the author wrote.
            Match combined = CombinedTexture.Match(candidate.Name);
            string? authorName = null;
            if (combined.Success)
            {
                string global = combined.Groups["base"].Value;
                var samplerHalf = new Regex($@"^{Regex.Escape(global)}_sampler_\d+$");
                if (globals.Any(g => !g.IsTexture && samplerHalf.IsMatch(g.Name)))
                {
                    if (texts is not null ? texts.Declares(global) : raw.Declares(global))
                    {
                        authorName = global;
                    }
                    else if (texts is null && raw.Spells(global))
                    {
                        // Spelled but not plainly declared in the raw text (declared through a
                        // macro, or the name of something else): only the preprocessed text can tell.
                        needsPreprocess = true;
                        continue;
                    }
                }
            }

            if (authorName is null)
                return Failed(HoistedTexture(candidate.Name, raw, texts, sourceName));

            if (renames.ContainsValue(authorName) || SpellsOutsideSemantics(hlsl, authorName))
            {
                (int line, int column) = raw.LocateDeclaration(authorName);
                return Failed(new ShaderError(
                    File: sourceName, Line: line, Column: column, Code: NameCollisionCode,
                    Message: $"The combined sampler '{authorName}' cannot keep its name in the compiled effect: slangc splits " +
                             $"it into the texture '{candidate.Name}' and a sampler, ShadowDusk names the texture parameter " +
                             $"'{authorName}' (what the author wrote), and slangc's output already uses the identifier " +
                             $"'{authorName}' for something else. Rename one of them."));
            }
            renames[candidate.Name] = authorName;
        }

        if (needsPreprocess && texts is null)
            return new Decision(new Dictionary<string, string>(), true, null);
        return renames.Count == 0 ? Decision.Nothing : new Decision(renames, false, null);
    }

    /// <summary>
    /// Renames every identifier in <paramref name="hlsl"/> that <paramref name="renames"/> maps.
    /// <c>#line</c> directives are left alone (their strings are file names).
    /// </summary>
    public static string Apply(string hlsl, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0)
            return hlsl;
        string[] lines = hlsl.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (LineDirective.IsMatch(lines[i]))
                continue;
            lines[i] = Identifier.Replace(
                lines[i], m => renames.TryGetValue(m.Value, out string? author) ? author : m.Value);
        }
        return string.Join('\n', lines);
    }

    private enum Origin
    {
        /// <summary>A name the author wrote: leave it.</summary>
        Author,
        /// <summary>A name slangc generated for a resource it hoisted.</summary>
        Generated,
        /// <summary>No read text decides, and not everything was read.</summary>
        Unproven,
        /// <summary>The raw source does not decide: the preprocessed text is needed.</summary>
        NeedsPreprocess,
    }

    private static Origin Classify(GlobalResource resource, RawSource raw, PreprocessedTexts? texts)
    {
        if (texts is not null)
        {
            if (texts.Spells(resource.Name))
                return Origin.Author;
            if (texts.Complete || HoistedFrom(resource.Name).Any(texts.Declares))
                return Origin.Generated;
            return Origin.Unproven;
        }

        if (!raw.CanFormIdentifiers && !raw.Spells(resource.Name))
            return Origin.Generated;
        // slangc locates an author's own global at its declaration (measured); a hoisted
        // resource is located in slangc's core module or at a stale line.
        if (resource.File == SlangcRegisterStripper.EntrySourceFile && raw.SpellsOnLine(resource.Line, resource.Name))
            return Origin.Author;
        return Origin.NeedsPreprocess;
    }

    // The globals a hoisted name may come from, longest first, with and without the prefix
    // slangc adds for its own parameter groups ('globalParams_gM_t_0' is field t of global gM).
    private static IEnumerable<string> HoistedFrom(string emittedName)
    {
        foreach (string b in SlangcRegisterStripper.HoistBases(emittedName))
            yield return b;
        foreach (string prefix in GroupPrefixes)
        {
            if (!emittedName.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            foreach (string b in SlangcRegisterStripper.HoistBases(emittedName[prefix.Length..]))
                yield return b;
        }
    }

    private static ShaderError HoistedTexture(string emittedName, RawSource raw, PreprocessedTexts? texts, string sourceName)
    {
        string? aggregate = HoistedFrom(emittedName)
            .FirstOrDefault(b => texts is not null ? texts.Declares(b) : raw.Declares(b));
        (int line, int column) = aggregate is null ? (0, 0) : raw.LocateDeclaration(aggregate);
        string held = aggregate is null
            ? "a texture held in an aggregate"
            : $"a texture held in '{aggregate}'";
        return new ShaderError(
            File: sourceName, Line: line, Column: column, Code: HoistedTextureCode,
            Message: $"slangc emits {held} under a name it generated, '{emittedName}', and that name would be the texture's " +
                     $"parameter name in the compiled effect (effect.Parameters[\"{emittedName}\"]): no name the author wrote " +
                     "identifies it. slangc hoists a resource out of every aggregate (a struct-typed global, a cbuffer or " +
                     "ParameterBlock, an entry-point uniform parameter) as '<aggregate>_<field>_<n>', and the reference " +
                     "compiler has no name for the shape either (fxc rejects a texture inside a struct, X3090). Declare the " +
                     "texture as a global of its own: 'Texture2D Name;' with a 'SamplerState', or a combined " +
                     "'Sampler2D Name;'. Both reflect as 'Name'.");
    }

    private static Decision Failed(ShaderError error) => new(new Dictionary<string, string>(), false, error);

    // 'Type Name;', 'Type Name : register(...)', 'Type Name[2];', '> Name,' and the like:
    // every place slangc's emission declares something called 'name'.
    private static int CountDeclarations(string hlsl, string name)
    {
        var declaration = new Regex(
            $@"(?<![\w.])(?:(?<type>[A-Za-z_]\w*)|>)\s+{Regex.Escape(name)}\b\s*(?:\[[^\];{{}}]*\]\s*)?(?=[;:=,)])");
        int count = 0;
        foreach (string line in hlsl.Split('\n'))
        {
            if (LineDirective.IsMatch(line))
                continue;
            foreach (Match m in declaration.Matches(line))
            {
                if (m.Groups["type"].Value != "return")
                    count++;
            }
        }
        return count;
    }

    // An identifier spelled 'name' anywhere in the emission other than as a semantic
    // ('float2 uv_0 : Comb;'), which shares no namespace with a global.
    private static bool SpellsOutsideSemantics(string hlsl, string name)
    {
        var use = new Regex($@"(?<!\w){Regex.Escape(name)}(?!\w)");
        foreach (string line in hlsl.Split('\n'))
        {
            if (LineDirective.IsMatch(line))
                continue;
            foreach (Match m in use.Matches(line))
            {
                int before = m.Index - 1;
                while (before >= 0 && char.IsWhiteSpace(line[before]))
                    before--;
                if (before < 0 || line[before] != ':')
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// What the entry source's raw text can prove without running slangc.
    /// </summary>
    private sealed class RawSource
    {
        private readonly string[] _maskedLines;
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
        private readonly SlangcRegisterStripper.AuthorBindings _bindings;

        public RawSource(string slangSource, IReadOnlyList<UserDefine> defines)
        {
            string masked = SlangSourceMask.Mask(slangSource);
            _maskedLines = masked.Split('\n');
            _bindings = SlangcRegisterStripper.AuthorBindings.Parse(masked);
            foreach (Match m in Identifier.Matches(masked))
                _tokens.Add(m.Value);

            // Read from the unmasked text, like SlangcRegisterStripper.MayWriteRegister: a
            // backslash at the end of a '//' comment splices the next line into the comment,
            // which the mask does not model, so any backslash counts.
            CanFormIdentifiers = CanForm(slangSource);
            foreach (UserDefine define in defines)
            {
                CanFormIdentifiers |= CanForm(define.Name) || CanForm(define.Value);
                foreach (Match m in Identifier.Matches(define.Name + " " + define.Value))
                    _tokens.Add(m.Value);
            }
        }

        /// <summary>
        /// The compile can hold an identifier this text does not spell: token pasting, a
        /// line splice, or another file (<c>#include</c>, <c>__include</c>, <c>import</c>).
        /// </summary>
        public bool CanFormIdentifiers { get; }

        private static bool CanForm(string? text) =>
            text is not null
            && (text.Contains("##", StringComparison.Ordinal)
                || text.Contains('\\')
                || text.Contains("include", StringComparison.Ordinal)
                || text.Contains("import", StringComparison.Ordinal));

        public bool Spells(string name) => _tokens.Contains(name);

        public bool Declares(string name) => _bindings.DeclaresGlobal(name);

        public bool SpellsOnLine(int line, string name) =>
            line >= 1 && line <= _maskedLines.Length
            && Regex.IsMatch(_maskedLines[line - 1], $@"(?<!\w){Regex.Escape(name)}(?!\w)");

        /// <summary>The 1-based line and column of <paramref name="name"/> where the source
        /// declares it as a global, or (0, 0) when the raw text does not show one.</summary>
        public (int Line, int Column) LocateDeclaration(string name)
        {
            var declaration = new Regex(
                $@"(?:\b[A-Za-z_]\w*|>)\s+(?<name>{Regex.Escape(name)})\s*(?:\[[^\];{{}}]*\]\s*)?[;:]");
            int depth = 0;
            for (int i = 0; i < _maskedLines.Length; i++)
            {
                string line = _maskedLines[i];
                Match m = declaration.Match(line);
                if (m.Success && depth + Depth(line[..m.Index]) == 0)
                    return (i + 1, m.Groups["name"].Index + 1);
                depth += Depth(line);
            }
            return (0, 0);
        }

        private static int Depth(string text)
        {
            int depth = 0;
            foreach (char c in text)
            {
                if (c == '{')
                    depth++;
                else if (c == '}')
                    depth--;
            }
            return depth;
        }
    }
}
