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
/// </remarks>
internal static class SlangcRegisterStripper
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    // A global texture/sampler declaration in slangc's emission, e.g.
    // 'Texture2D<float4 > SpriteTexture : register(t0);' or 'SamplerState S : register(s0);'.
    private static readonly Regex EmittedRegister = new(
        $$"""(?<decl>(?:{{SlangcResourceTypes.Texture}}|{{SlangcResourceTypes.Sampler}})(?:\s*<[^>;{}]*>)?\s+(?<name>[A-Za-z_]\w*)(?:\s*\[[^\];{}]*\])?)\s*:\s*register\s*\(\s*[ts]\d+\s*(?:,\s*space\d+\s*)?\)""",
        RegexOptions.Compiled);

    // 'Name : register(...)' or 'Name[4] : register(...)' in the preprocessed Slang source,
    // which slangc prints as a token stream ('Mask : register ( t1 ) ;').
    private static readonly Regex AuthorRegister = new(
        """\b(?<name>[A-Za-z_]\w*)\s*(?:\[[^\];{}]*\])?\s*:\s*register\s*\(""",
        RegexOptions.Compiled);

    /// <summary>
    /// False only when no <c>register</c> token can reach the compile, however the source is
    /// preprocessed, so the preprocess-only slangc pass can be skipped and nothing is
    /// author-bound. True means "ask slangc's preprocessor", never "there is a register".
    /// </summary>
    /// <remarks>
    /// A <c>register</c> token can only come from the literal word in the source or in a
    /// <c>-D</c> value, from an <c>#include</c>d file, from token pasting (<c>##</c>), or from
    /// a backslash line splice, which slangc honours inside an identifier and inside a
    /// directive name (measured: <c>regis\&lt;newline&gt;ter(t3)</c> binds t3). Any of those
    /// spellings anywhere (comments included: this errs toward running the pass) returns true.
    /// The check is case-sensitive because slangc's <c>register</c> is (<c>REGISTER(t3)</c> is
    /// a syntax error, measured).
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
    public static IReadOnlySet<string> AuthorBoundNames(string preprocessedSlangSource)
    {
        // The preprocessor already dropped comments; the mask still blanks string-literal
        // contents (an attribute argument that happens to read 'X : register(').
        string masked = SlangSourceMask.Mask(preprocessedSlangSource);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in AuthorRegister.Matches(masked))
            names.Add(m.Groups["name"].Value);
        return names;
    }

    /// <summary>No author-bound name: every slangc-numbered texture/sampler register goes.</summary>
    public static IReadOnlySet<string> NoAuthorBoundNames => NoNames;

    /// <summary>Strips every slangc-numbered texture/sampler register in
    /// <paramref name="hlsl"/> whose declaration name is not in <paramref name="authorBound"/>.</summary>
    public static string Strip(string hlsl, IReadOnlySet<string> authorBound) =>
        EmittedRegister.Replace(hlsl, m =>
            authorBound.Contains(m.Groups["name"].Value) ? m.Value : m.Groups["decl"].Value);
}
