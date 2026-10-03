#nullable enable

using System.Text.RegularExpressions;

namespace ShadowDusk.Slang;

/// <summary>
/// Rejects HLSL Effect (<c>.fx</c>) input handed to the real-slangc route (issue #231).
///
/// <para><b>Measured</b> (MonoGame v3.8.5, commit 4f9e3727, all of BasicEffect, AlphaTestEffect,
/// DualTextureEffect, EnvironmentMapEffect, SkinnedEffect and SpriteEffect, flattened, on every
/// target macro set, every VS and PS entry): 0 of 66 distinct entries compile through real slangc on each target. Two
/// independent blockers, in order. (1) The <c>technique</c> block the <c>TECHNIQUE(...)</c>
/// macro expands to: Slang has no technique/pass concept (<c>error[E20001]: unexpected token</c>).
/// (2) With techniques removed, the legacy <c>sampler</c> / <c>sampler2D</c> declarations in
/// <c>DECLARE_TEXTURE</c> (<c>error[E30015]: undefined identifier</c>); no target's macro branch
/// avoids both. These effects are not Slang; they compile, render-proven, through the
/// <c>.fx</c> route. Without this guard the author sees SD0603 ("no [shader] entry points"),
/// which sends them down a dead end: adding the attributes still fails on the technique.</para>
///
/// <para>A static scan of the Effect-framework vocabulary: the <c>technique</c> block, a
/// <c>VertexShader = compile</c> pass state, and MonoGame's own Macros.fxh entry macros
/// (<c>TECHNIQUE</c>, <c>DECLARE_TEXTURE</c>, <c>DECLARE_CUBEMAP</c>, <c>BEGIN_CONSTANTS</c>),
/// which hide the technique from a textual scan of an un-expanded source. Comments are ignored.
/// A source that spells none of these (a macro header of someone else's) falls through to
/// slangc's own diagnostic, unmodified.</para>
/// </summary>
internal static class SlangEffectFrameworkGuard
{
    private static readonly (string Construct, Regex Pattern)[] Constructs =
    [
        ("technique", new Regex(@"^[ \t]*technique(?:10|11)?\b[^;{}]*\{", RegexOptions.Multiline | RegexOptions.Compiled)),
        ("VertexShader = compile", new Regex(@"\b(?:VertexShader|PixelShader)\s*=\s*compile\b", RegexOptions.Compiled)),
        ("TECHNIQUE(...) macro", new Regex(@"^[ \t]*TECHNIQUE\s*\(", RegexOptions.Multiline | RegexOptions.Compiled)),
        ("DECLARE_TEXTURE(...) macro", new Regex(@"^[ \t]*DECLARE_(?:TEXTURE|CUBEMAP)\s*\(", RegexOptions.Multiline | RegexOptions.Compiled)),
        ("BEGIN_CONSTANTS macro", new Regex(@"^[ \t]*BEGIN_CONSTANTS\b", RegexOptions.Multiline | RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Returns the first Effect-framework construct and its 1-based line, or <c>null</c>.
    /// </summary>
    public static (string Construct, int Line)? FindConstruct(string source)
    {
        string code = ShadowDusk.Compiler.Slang.SlangSourceMask.Mask(source);
        (string Construct, int Index)? best = null;
        foreach ((string construct, Regex pattern) in Constructs)
        {
            Match m = pattern.Match(code);
            if (m.Success && (best is null || m.Index < best.Value.Index))
                best = (construct, m.Index);
        }

        if (best is null)
            return null;
        return (best.Value.Construct, 1 + code.AsSpan(0, best.Value.Index).Count('\n'));
    }
}
