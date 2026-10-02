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
/// from its text. A declaration neither pass decides fails as <c>SD0628</c>, never a guess.</item>
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

    // slangc's split of a combined SamplerXD: '<name>_texture_<n>' / '<name>_sampler_<n>'.
    private static readonly Regex SplitName = new(
        """^(?<base>[A-Za-z_]\w*?)_(?<kind>texture|sampler)_\d+$""",
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
    /// Whether the author wrote <paramref name="resource"/>'s register, judged from the entry
    /// source's preprocessed text (<paramref name="entry"/>) and, for a declaration that came
    /// from another file, the preprocessed text of the files it came from
    /// (<paramref name="otherFiles"/>, null while those have not been read).
    /// </summary>
    public static RegisterVerdict Judge(EmittedResource resource, AuthorBindings entry, AuthorBindings? otherFiles)
    {
        if (entry.Binds(resource))
            return RegisterVerdict.Keep;
        // Written in the entry source (or a file it #includes, which -E expands) without one.
        if (resource.File == EntrySourceFile || entry.Declares(resource))
            return RegisterVerdict.Strip;
        if (otherFiles is null)
            return RegisterVerdict.Unproven;
        // Bound in one file and plainly declared in another: the two readings disagree, and
        // which one slangc compiled cannot be told from here.
        if (otherFiles.Binds(resource))
            return otherFiles.DeclaresPlainly(resource) ? RegisterVerdict.Unproven : RegisterVerdict.Keep;
        return otherFiles.Declares(resource) ? RegisterVerdict.Strip : RegisterVerdict.Unproven;
    }

    /// <summary>
    /// Strips every slangc-numbered texture/sampler register in <paramref name="hlsl"/> whose
    /// emitted declaration name is not in <paramref name="keep"/>.
    /// </summary>
    public static string Strip(string hlsl, IReadOnlySet<string> keep) =>
        EmittedRegister.Replace(hlsl, m =>
            keep.Contains(m.Groups["name"].Value) ? m.Value : m.Groups["decl"].Value);

    /// <summary>
    /// For slangc's split of a combined <c>SamplerXD C</c>: the author's name <c>C</c> and the
    /// register class the author must have written for this half to count. Null for any other
    /// emitted name.
    /// </summary>
    private static (string Name, char Class)? SplitAuthorName(EmittedResource resource)
    {
        Match m = SplitName.Match(resource.Name);
        if (!m.Success)
            return null;
        char kindClass = m.Groups["kind"].Value == "texture" ? 't' : 's';
        // A split texture half always carries t, a sampler half s; anything else is not the split.
        return kindClass == resource.RegisterClass ? (m.Groups["base"].Value, kindClass) : null;
    }

    /// <summary>A texture/sampler declaration in slangc's emission that carries a register.</summary>
    /// <param name="Name">The emitted name (<c>C_texture_0</c> for a split combined sampler).</param>
    /// <param name="RegisterClass"><c>t</c> or <c>s</c>.</param>
    /// <param name="File">The file slangc's <c>#line</c> names (<see cref="EntrySourceFile"/> for the entry source).</param>
    /// <param name="Line">The line in <paramref name="File"/>.</param>
    public readonly record struct EmittedResource(string Name, char RegisterClass, string File, int Line)
    {
        /// <summary>The name the author wrote: the base of a split combined sampler, else <see cref="Name"/>.</summary>
        public string AuthorName => SplitAuthorName(this)?.Name ?? Name;

        /// <summary>
        /// One half of slangc's split of a combined <c>SamplerXD</c>. Its <see cref="File"/> is
        /// slangc's own core module (<c>"core"</c>, <c>"hlsl.meta.slang"</c>, measured), never
        /// the author's file.
        /// </summary>
        public bool IsSplitHalf => SplitAuthorName(this) is not null;
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
            string path = m.Groups["path"].Value;
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
        /// <summary>The preprocessed text available cannot decide.</summary>
        Unproven,
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
            return SplitAuthorName(resource) is { } s
                && _bound.TryGetValue(s.Name, out HashSet<char>? classes)
                && classes.Contains(s.Class);
        }

        /// <summary>
        /// The author declared this emitted declaration's resource here without a register for
        /// it: plainly, or (for one half of a combined sampler) with a register of the other class.
        /// </summary>
        public bool Declares(EmittedResource resource) =>
            DeclaresPlainly(resource)
            || (SplitAuthorName(resource) is { } s && _bound.ContainsKey(s.Name));

        /// <summary>The author declared this emitted declaration's resource here with no register at all.</summary>
        public bool DeclaresPlainly(EmittedResource resource) =>
            _plain.Contains(resource.Name)
            || (SplitAuthorName(resource) is { } s && _plain.Contains(s.Name));
    }
}
