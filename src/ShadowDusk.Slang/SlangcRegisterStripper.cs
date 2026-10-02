#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Slang;

/// <summary>
/// Issue #252: removes the texture and sampler <c>register(tN)</c> / <c>register(sN)</c>
/// annotations slangc invents on its own, keeping every one the author actually wrote.
/// </summary>
/// <remarks>
/// <para>slangc's <c>-target hlsl</c> emission gives every resource an explicit register,
/// whether or not the Slang source asked for one (<c>SamplerState SpriteSampler;</c> comes back
/// as <c>SamplerState SpriteSampler : register(s0);</c>). For OpenGL that is not harmless:
/// ShadowDusk's GL sampler allocator follows <c>mgfxc</c>'s measured rule (issue #189), under
/// which a modern <c>SamplerState : register(sN)</c> RESERVES register N, so the one combined
/// texture/sampler pair moved to <c>ps_s1</c>. SpriteBatch binds the draw texture to unit 0,
/// so a textured Slang shader sampled an empty unit. Stripping slangc's own numbering hands the
/// pipeline the same text an author would write by hand for the equivalent <c>.fx</c>, which
/// <c>mgfxc</c> puts on unit 0.</para>
/// <para>An author-written register is never touched: a declaration whose name carries
/// <c>: register(...)</c> in the Slang source AS SLANGC'S PREPROCESSOR LEAVES IT keeps it (that
/// IS the author's intent, and the allocator honours it exactly as it does for a hand-written
/// <c>.fx</c>, ps_s1 included). "As the preprocessor leaves it" matters both ways (the #252
/// follow-up): a register written only in an inactive <c>#if</c> branch is not the author's
/// intent for this target, and one written through a macro
/// (<c>#define SLOT(n) : register(n)</c>) is. So the names are read from slangc's own
/// preprocess-only output (<see cref="SlangcArguments.BuildPreprocess"/>, same macros as the
/// compile), never from the raw source text.
/// <c>-no-mangle</c> keeps global resource names at the author's spelling, which is what makes
/// the per-name match sound. A <c>[[vk::binding(N)]]</c> attribute is NOT a register: slangc's
/// HLSL drops it and emits its own auto number instead (measured, v2026.14.1:
/// <c>[[vk::binding(3)]] SamplerState S;</c> comes back as <c>register(s0)</c>), and the
/// <c>.fx</c> route does not read <c>vk::binding</c> as a GL sampler reservation either, so
/// stripping slangc's invented number keeps the two routes identical. Constant-buffer
/// <c>register(bN)</c> annotations are left alone: no allocator reads them as a reservation.</para>
/// <para>Issue #292, two shapes the per-name match used to strip silently:</para>
/// <list type="bullet">
/// <item>A combined <c>Sampler2D C : register(t2)</c> is emitted as two declarations,
/// <c>Texture2D&lt;float4 &gt; C_texture_0 : register(t2)</c> and
/// <c>SamplerState C_sampler_0 : register(s0)</c> (measured, v2026.14.1, for
/// <c>Sampler1D</c>/<c>2D</c>/<c>3D</c>/<c>Cube</c>, the <c>Array</c> forms and arrays of them).
/// The author's register applies to the half of its own class only: <c>register(t2)</c> binds
/// the texture and slangc numbers the sampler, <c>register(s3)</c> the reverse, and
/// <c>: register(t2) : register(s3)</c> binds both. So a split name maps back to the author's
/// declaration together with the register class.</item>
/// <item>A resource declared in an <c>import</c>ed module (or an <c>__include</c>d file) is
/// not in the entry source's <c>slangc -E</c> output at all: <c>-E</c> expands neither.
/// slangc auto-numbers an imported resource that has no author register (measured: an
/// imported <c>Texture2D ModNoReg;</c> comes back as <c>register(t1)</c>), so the emission
/// alone cannot tell. slangc names the file each declaration came from in its <c>#line</c>
/// directives; <see cref="SlangCompiler"/> runs the same <c>-E</c> pass over that file (same
/// macros, which slangc applies to imported modules too, measured) and judges the declaration
/// from its text, but only once the file is known to be a MODULE: reached through a
/// quoted-path import, or opening with a <c>module</c>/<c>implementing</c> declaration.
/// Macros do not cross an <c>import</c> or an <c>__include</c> (measured), so a module's own
/// <c>-E</c> output is what slangc compiled; an <c>#include</c>d fragment's is not (its
/// includer's macros decide), so a fragment is read through the module that includes it.
/// A declaration no trusted text decides fails as <c>SD0628</c>, never a guess.</item>
/// </list>
/// </remarks>
internal static class SlangcRegisterStripper
{
    /// <summary>The file name slangc gives the source it read from stdin.</summary>
    public const string EntrySourceFile = "<stdin>";

    // A global texture/sampler declaration in slangc's emission, e.g.
    // 'Texture2D<float4 > SpriteTexture : register(t0);' or 'SamplerState S : register(s0);'.
    private static readonly Regex EmittedRegister = new(
        $$"""(?<decl>(?:{{SlangcResourceTypes.Texture}}|{{SlangcResourceTypes.Sampler}})(?:\s*<[^>;{}]*>)?\s+(?<name>[A-Za-z_]\w*)(?:\s*\[[^\];{}]*\])?)\s*:\s*register\s*\(\s*(?<cls>[ts])\d+\s*(?:,\s*space\d+\s*)?\)""",
        RegexOptions.Compiled);

    // '#line 12 "file"' or '#line 12' in slangc's emission.
    private static readonly Regex LineDirective = new(
        """^\s*#line\s+(?<line>\d+)(?:\s+"(?<file>[^"]*)")?""",
        RegexOptions.Compiled);

    // 'Name : register(t1)', 'Name[4] : register(t4)' or 'Name : register(t2) : register(s3)' in
    // the preprocessed Slang source, which slangc prints as a token stream
    // ('Mask : register ( t1 ) ;'). One 'cls' capture per register written.
    private static readonly Regex AuthorRegister = new(
        """\b(?<name>[A-Za-z_]\w*)\s*(?:\[[^\];{}]*\])?(?:\s*:\s*register\s*\(\s*(?<cls>[A-Za-z]?)[^()]*\))+""",
        RegexOptions.Compiled);

    // A declaration with NO annotation at all: 'Texture2D Name ;', 'Texture2D < float4 > Name ;',
    // 'Name [ 4 ] ;'. Read at brace depth 0 only, so a local or a struct member is not one.
    private static readonly Regex PlainDeclaration = new(
        """(?:\b(?<type>[A-Za-z_]\w*)|>)\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^\];{}]*\]\s*)?;""",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NotATypeKeyword = new(StringComparer.Ordinal)
    {
        "return", "import", "module", "implementing", "__include", "using", "goto", "break", "continue",
    };

    /// <summary>
    /// False when the main source (and its <c>-D</c> values) cannot spell a <c>register</c>
    /// token however it is preprocessed, so the preprocess-only slangc pass can be skipped and
    /// nothing is author-bound. True means "ask slangc's preprocessor", never "there is a
    /// register".
    /// </summary>
    /// <remarks>
    /// <para>This reads the entry source only. A register written in an <c>import</c>ed module
    /// or an <c>__include</c>d file is found from slangc's emission instead (its <c>#line</c>
    /// names a file other than <see cref="EntrySourceFile"/>), and <see cref="SlangCompiler"/>
    /// runs the pass whenever such a declaration exists (issue #292).</para>
    /// <para>A <c>register</c> token can only come from the literal word in the source or in a
    /// <c>-D</c> value, from an <c>#include</c>d file, from token pasting (<c>##</c>), or from
    /// a backslash line splice, which slangc honours inside an identifier and inside a
    /// directive name (measured: <c>regis\&lt;newline&gt;ter(t3)</c> binds t3). Any of those
    /// spellings anywhere (comments included: this errs toward running the pass) returns true.
    /// The check is case-sensitive because slangc's <c>register</c> is (<c>REGISTER(t3)</c> is
    /// a syntax error, measured).</para>
    /// </remarks>
    public static bool MayWriteRegister(string slangSource, IReadOnlyList<UserDefine> defines)
    {
        if (CanSpellRegister(slangSource))
            return true;
        foreach (UserDefine define in defines)
        {
            if (CanSpellRegister(define.Name) || CanSpellRegister(define.Value))
                return true;
        }
        return false;
    }

    private static bool CanSpellRegister(string? text) =>
        text is not null
        && (text.Contains("register", StringComparison.Ordinal)
            || text.Contains("include", StringComparison.Ordinal)
            || text.Contains("##", StringComparison.Ordinal)
            || text.Contains('\\'));

    /// <summary>
    /// The global names the author bound explicitly, read from
    /// <paramref name="preprocessedSlangSource"/>: slangc's preprocess-only (<c>-E</c>) output
    /// for the source, produced with the same macros as the compile.
    /// </summary>
    public static IReadOnlySet<string> AuthorBoundNames(string preprocessedSlangSource) =>
        new HashSet<string>(AuthorBindings.Parse(preprocessedSlangSource).BoundNames, StringComparer.Ordinal);

    /// <summary>
    /// Every texture/sampler declaration in <paramref name="hlsl"/> (one slangc emission) that
    /// carries a <c>t</c>/<c>s</c> register, with the file and line slangc's <c>#line</c>
    /// directives place it at. Before any directive the file is <see cref="EntrySourceFile"/>.
    /// </summary>
    public static IReadOnlyList<EmittedResource> FindRegistered(string hlsl)
    {
        var found = new List<EmittedResource>();
        string file = EntrySourceFile;
        int line = 1;
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
            foreach (Match m in EmittedRegister.Matches(text))
                found.Add(new EmittedResource(m.Groups["name"].Value, m.Groups["cls"].Value[0], file, line));
            line++;
        }
        return found;
    }

    /// <summary>
    /// True when the entry source (or a <c>-D</c> value) can spell an <c>import</c>, the only way
    /// a declaration reaches the compile from a file the entry source's own preprocessed text
    /// does not contain (<c>__include</c> spells <c>include</c>, and a pasted or spliced token
    /// is covered by <see cref="MayWriteRegister"/>).
    /// </summary>
    public static bool MayImport(string slangSource, IReadOnlyList<UserDefine> defines)
    {
        if (slangSource.Contains("import", StringComparison.Ordinal))
            return true;
        foreach (UserDefine define in defines)
        {
            if (define.Name.Contains("import", StringComparison.Ordinal)
                || (define.Value?.Contains("import", StringComparison.Ordinal) ?? false))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// One spelling per file for the paths slangc and a Slang source write: backslashes as
    /// forward slashes, repeated separators collapsed (slangc's <c>#line</c> prints an import
    /// written with backslashes as <c>C://dir//m.slang</c>, measured). Used only to tell that
    /// two spellings are the same file, never as a path to open.
    /// </summary>
    public static string PathKey(string path)
    {
        string key = path.Replace('\\', '/');
        while (key.Contains("//", StringComparison.Ordinal))
            key = key.Replace("//", "/", StringComparison.Ordinal);
        return key;
    }

    // A preprocessed file that opens with 'module x ;' or 'implementing x ;'.
    private static readonly Regex ModuleOpening = new(
        """^\s*(?:module|implementing)\b""", RegexOptions.Compiled);

    /// <summary>
    /// The preprocessed text opens with a <c>module</c> or <c>implementing</c> declaration, so
    /// the file is a module (or a module's <c>__include</c>d part), never an <c>#include</c>d
    /// fragment. A module is preprocessed on its own: macros do not cross an <c>import</c> or
    /// an <c>__include</c> (measured), so its own <c>-E</c> output is what slangc compiled.
    /// </summary>
    public static bool OpensAsModule(string preprocessed) => ModuleOpening.IsMatch(preprocessed);

    /// <summary>
    /// Whether the author wrote <paramref name="resource"/>'s register.
    /// </summary>
    /// <param name="resource">The emitted declaration.</param>
    /// <param name="entry">The entry source's preprocessed text.</param>
    /// <param name="modules">The preprocessed text of every file proven to be a module so far,
    /// by <see cref="PathKey"/>.</param>
    /// <param name="closureComplete">Every file reachable through quoted-path imports has been
    /// read. Until then a declaration that needs them all is <see cref="RegisterVerdict.Pending"/>.</param>
    /// <param name="closureBroken">A quoted-path import could not be read, so "no module declares
    /// it" proves nothing.</param>
    /// <remarks>
    /// In order: the entry text decides whatever it binds or declares (its own declarations and
    /// those of the files it <c>#include</c>s, which <c>-E</c> expands). A declaration slangc
    /// locates in a module is decided by that module's own text alone. Anything else (a
    /// resource hoisted out of an aggregate, whose location is slangc's core module, or one
    /// located in a file not proven to be a module, which may be a fragment <c>#include</c>d
    /// by one) is decided by all the modules together, and only when they agree.
    /// </remarks>
    public static RegisterVerdict Judge(
        EmittedResource resource,
        AuthorBindings entry,
        IReadOnlyDictionary<string, AuthorBindings> modules,
        bool closureComplete,
        bool closureBroken = false)
    {
        if (entry.Binds(resource))
            return RegisterVerdict.Keep;
        // Written in the entry source (or a file it #includes, which -E expands) without one.
        if (resource.File == EntrySourceFile || entry.Declares(resource))
            return RegisterVerdict.Strip;

        if (modules.TryGetValue(PathKey(resource.File), out AuthorBindings? own))
        {
            if (own.Binds(resource))
            {
                if (!own.DeclaresPlainly(resource))
                    return RegisterVerdict.Keep;
            }
            else if (own.Declares(resource))
            {
                return RegisterVerdict.Strip;
            }
        }

        if (!closureComplete)
            return RegisterVerdict.Pending;
        if (closureBroken)
            return RegisterVerdict.Unproven;

        // Bound in one module and plainly declared in another: the two readings disagree, and
        // which one slangc compiled cannot be told from here.
        if (modules.Values.Any(m => m.Binds(resource)))
            return modules.Values.Any(m => m.DeclaresPlainly(resource)) ? RegisterVerdict.Unproven : RegisterVerdict.Keep;
        return modules.Values.Any(m => m.Declares(resource)) ? RegisterVerdict.Strip : RegisterVerdict.Unproven;
    }

    /// <summary>
    /// Strips every slangc-numbered texture/sampler register in <paramref name="hlsl"/> whose
    /// emitted declaration name is not in <paramref name="keep"/>.
    /// </summary>
    public static string Strip(string hlsl, IReadOnlySet<string> keep) =>
        EmittedRegister.Replace(hlsl, m =>
            keep.Contains(m.Groups["name"].Value) ? m.Value : m.Groups["decl"].Value);

    /// <summary>
    /// The author globals <paramref name="emittedName"/> may have been hoisted out of, longest
    /// first: every prefix <c>B</c> with <paramref name="emittedName"/> = <c>B_&lt;field&gt;_&lt;n&gt;</c>.
    /// slangc names a resource it hoists out of an aggregate that way (measured, v2026.14.1):
    /// a combined <c>Sampler2D C</c> becomes <c>C_texture_0</c>/<c>C_sampler_0</c>, and a
    /// <c>SamplerState s</c> field of a global struct <c>gS</c> becomes <c>gS_s_0</c>. Either way
    /// an author register on <c>B</c> lands on the hoisted resource of its own class only
    /// (<c>M gM : register(t5)</c> puts t5 on the texture field, and slangc numbers the sampler).
    /// </summary>
    internal static IEnumerable<string> HoistBases(string emittedName)
    {
        Match tail = HoistTail.Match(emittedName);
        if (!tail.Success)
            yield break;
        // 'B_<field>_<n>': the '_' before <n> is at tail.Index; every '_' before it may end B.
        for (int i = tail.Index - 1; i > 0; i--)
        {
            if (emittedName[i] == '_' && i + 1 < tail.Index)
                yield return emittedName[..i];
        }
    }

    // The '_<n>' suffix slangc appends to a hoisted resource's name.
    private static readonly Regex HoistTail = new("""_\d+$""", RegexOptions.Compiled);

    /// <summary>A texture/sampler declaration in slangc's emission that carries a register.</summary>
    /// <param name="Name">The emitted name (<c>C_texture_0</c> for a split combined sampler).</param>
    /// <param name="RegisterClass"><c>t</c> or <c>s</c>.</param>
    /// <param name="File">The file slangc's <c>#line</c> names (<see cref="EntrySourceFile"/> for the entry source).</param>
    /// <param name="Line">The line in <paramref name="File"/>.</param>
    public readonly record struct EmittedResource(string Name, char RegisterClass, string File, int Line)
    {
        /// <summary>The longest author global this may have been hoisted out of, else <see cref="Name"/>.</summary>
        public string AuthorName => HoistBases(Name).FirstOrDefault() ?? Name;

        /// <summary>
        /// Shaped like a resource slangc hoisted out of an aggregate (a combined sampler's half, a
        /// struct's resource field). slangc's <c>#line</c> for those names its own core module
        /// (<c>"core"</c>, <c>"hlsl.meta.slang"</c>, measured), not the author's file.
        /// </summary>
        public bool IsHoisted => HoistBases(Name).Any();

        /// <summary>
        /// <see cref="IsHoisted"/> and located in a file with a bare name: slangc's embedded
        /// core module, which is not a file anyone can preprocess.
        /// </summary>
        public bool IsCoreHoist => IsHoisted && File.IndexOfAny(['/', '\\']) < 0;
    }

    // 'import "path" ;', '__exported import "path" ;' or '__include "path" ;' in a preprocessed
    // token stream. Read from the unmasked text: the mask blanks string contents.
    private static readonly Regex QuotedImport = new(
        """\b(?:import|__include)\s+"(?<path>[^"\r\n]+)"\s*;""",
        RegexOptions.Compiled);

    /// <summary>
    /// The files <paramref name="preprocessed"/> imports or <c>__include</c>s by quoted path,
    /// resolved the way slangc resolves a relative one: against the importing file's directory
    /// (<paramref name="importingFile"/>), or as written for the entry source (slangc then
    /// resolves it from its working directory, which the <c>-E</c> pass shares). Rootedness is
    /// decided by spelling, not by the host, so every host resolves the same text the same way.
    /// An import by module name (<c>import foo.bar;</c>) is not followed: slangc's search for
    /// it is not modelled, and a declaration only it could explain fails as <c>SD0628</c>.
    /// </summary>
    public static IEnumerable<string> QuotedImports(string preprocessed, string? importingFile)
    {
        foreach (Match m in QuotedImport.Matches(preprocessed))
        {
            // The token is a string literal: an escaped backslash is one backslash of the path.
            string path = m.Groups["path"].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
            if (importingFile is null || IsRooted(path))
            {
                yield return path;
                continue;
            }
            int slash = Math.Max(importingFile.LastIndexOf('/'), importingFile.LastIndexOf('\\'));
            yield return slash < 0 ? path : importingFile[..(slash + 1)] + path;
        }
    }

    private static bool IsRooted(string path) =>
        path.StartsWith('/') || path.StartsWith('\\')
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '/' || path[2] == '\\'));

    /// <summary>The verdict on one emitted register.</summary>
    public enum RegisterVerdict
    {
        /// <summary>The author wrote it: keep.</summary>
        Keep,
        /// <summary>slangc numbered it: strip.</summary>
        Strip,
        /// <summary>No text ShadowDusk can read decides.</summary>
        Unproven,
        /// <summary>Not decided yet: more imported files have to be read first.</summary>
        Pending,
    }

    /// <summary>
    /// What preprocessed (<c>slangc -E</c>) text says about resource registers: which names
    /// carry an author register (and of which classes), and which are declared at global scope
    /// with no annotation at all.
    /// </summary>
    internal sealed class AuthorBindings
    {
        private readonly Dictionary<string, HashSet<char>> _bound = new(StringComparer.Ordinal);
        private readonly HashSet<string> _plain = new(StringComparer.Ordinal);

        /// <summary>Nothing bound, nothing declared.</summary>
        public static AuthorBindings None { get; } = new();

        public IEnumerable<string> BoundNames => _bound.Keys;

        public static AuthorBindings Parse(string preprocessedSlangSource) =>
            Union([preprocessedSlangSource]);

        /// <summary>The union of several files' preprocessed texts.</summary>
        public static AuthorBindings Union(IEnumerable<string> preprocessedTexts)
        {
            var bindings = new AuthorBindings();
            foreach (string text in preprocessedTexts)
                bindings.Add(text);
            return bindings;
        }

        private void Add(string preprocessed)
        {
            // The preprocessor already dropped comments; the mask still blanks string-literal
            // contents (an attribute argument that happens to read 'X : register(').
            string masked = SlangSourceMask.Mask(preprocessed);
            foreach (Match m in AuthorRegister.Matches(masked))
            {
                string name = m.Groups["name"].Value;
                if (!_bound.TryGetValue(name, out HashSet<char>? classes))
                    _bound[name] = classes = [];
                foreach (Capture c in m.Groups["cls"].Captures)
                {
                    if (c.Value.Length > 0)
                        classes.Add(char.ToLowerInvariant(c.Value[0]));
                }
            }

            int depth = 0;
            int scanned = 0;
            foreach (Match m in PlainDeclaration.Matches(masked))
            {
                for (; scanned < m.Index; scanned++)
                {
                    if (masked[scanned] == '{')
                        depth++;
                    else if (masked[scanned] == '}')
                        depth--;
                }
                if (depth != 0)
                    continue;
                if (m.Groups["type"].Success && NotATypeKeyword.Contains(m.Groups["type"].Value))
                    continue;
                _plain.Add(m.Groups["name"].Value);
            }
        }

        /// <summary>The author wrote this emitted declaration's register.</summary>
        public bool Binds(EmittedResource resource)
        {
            // An emitted name the author wrote verbatim (any class, as since issue #252).
            if (_bound.ContainsKey(resource.Name))
                return true;
            // Hoisted out of an author global: only a register of this resource's own class counts.
            char cls = resource.RegisterClass;
            return HoistBases(resource.Name).Any(
                b => _bound.TryGetValue(b, out HashSet<char>? classes) && classes.Contains(cls));
        }

        /// <summary>
        /// The author declared this emitted declaration's resource here without a register for
        /// it: plainly, or (hoisted out of an aggregate) with a register of the other class only.
        /// </summary>
        public bool Declares(EmittedResource resource) =>
            DeclaresPlainly(resource) || HoistBases(resource.Name).Any(_bound.ContainsKey);

        /// <summary>
        /// The text declares a global called <paramref name="name"/>, with or without a register
        /// (issue #302: the global a hoisted resource's generated name maps back to).
        /// </summary>
        public bool DeclaresGlobal(string name) => _plain.Contains(name) || _bound.ContainsKey(name);

        /// <summary>The author declared this emitted declaration's resource here with no register at all.</summary>
        public bool DeclaresPlainly(EmittedResource resource) =>
            _plain.Contains(resource.Name) || HoistBases(resource.Name).Any(_plain.Contains);
    }
}
