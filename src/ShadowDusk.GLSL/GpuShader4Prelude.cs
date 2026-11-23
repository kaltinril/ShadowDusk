#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.GLSL;

/// <summary>
/// Rule 19 (issue #524): the guarded <c>GL_EXT_gpu_shader4</c> prelude for a stage whose GLSL uses
/// an integer construct the versionless dialect does not have: <c>gl_VertexID</c>,
/// <c>gl_InstanceID</c>, <c>uint</c>/<c>uvec</c>, a <c>u</c> literal, or an integer shift, bitwise
/// or modulo operator.
///
/// <para>The rewriter strips <c>#version</c> (Rule 1), so a desktop driver compiles the text as
/// GLSL 1.10/1.20, where none of these exist without the extension. Measured with
/// <c>glCompileShader</c> on each stack (issue #524): Apple's GL 2.1 and Mesa's compatibility and
/// core contexts refuse them, and so does NVIDIA's 4.1 core context, which is what MonoGame 3.8.6
/// DesktopGL4 runs on. Only NVIDIA's compatibility context accepted them unaided.</para>
///
/// <para>Why each piece of the prelude is there:</para>
/// <list type="bullet">
///   <item>The guard keeps it off every stack that must not see it. GLSL ES has no such extension
///   (<c>GL_ES</c>), and a stage KNI's WebGL2 path compiles as <c>#version 300 es</c> has these
///   constructs natively.</item>
///   <item><c>#define uint unsigned int</c>: the extension's own spelling of the type is
///   <c>unsigned int</c>. Apple refuses <c>uint</c> even with the extension enabled, and Mesa
///   refuses it at global scope; Apple, Mesa and NVIDIA all accept the macro. It is emitted only when the stage
///   says <c>uint</c>, since a macro nothing uses would make the portability lint report an
///   unsigned type the shader does not have. <c>uvec2/3/4</c> and <c>u</c> literals are spelled
///   the same way in the extension and need nothing.</item>
///   <item>The prelude goes before everything else, and every other unconditional extension
///   directive of the stage is hoisted behind it: a <c>#ifdef X / #extension X / #endif</c> block
///   (SPIRV-Cross's <c>gl_InstanceID</c> shape) or a bare <c>#extension</c> line (its
///   <c>asuint</c> shape), both of which can sit after declarations the rewriter put ahead of them.
///   <c>#extension</c> must precede every non-preprocessor token, and Mesa enforces that. A
///   directive inside another conditional (the texture-LOD header) stays where it is.</item>
/// </list>
///
/// <para>A stage with none of these constructs is returned unchanged, byte for byte.</para>
/// </summary>
internal static class GpuShader4Prelude
{
    private const string Guard = "#if !defined(GL_ES) && __VERSION__ < 130 && defined(GL_EXT_gpu_shader4)\n";
    private const string Extension = "#extension GL_EXT_gpu_shader4 : enable\n";
    private const string UintAlias = "#define uint unsigned int\n";
    private const string EndGuard = "#endif\n";

    // The lookarounds keep the logical operators &&, || and ^^ (legal in GLSL 1.10) from matching.
    private static readonly Regex NeedsExtension = new(
        @"\bgl_(?:Vertex|Instance)ID\b|\buint\b|\buvec[234]\b|\b(?:0[xX][0-9a-fA-F]+|[0-9]+)[uU]\b" +
        @"|<<|>>|(?<!&)&(?!&)|(?<!\|)\|(?!\|)|(?<!\^)\^(?!\^)|~|%",
        RegexOptions.Compiled);

    private static readonly Regex UintUse = new(@"\buint\b", RegexOptions.Compiled);

    // SPIRV-Cross's guarded extension block, exactly as it writes one.
    private static readonly Regex GuardedExtensionBlock = new(
        @"^#ifdef (GL_[A-Za-z0-9_]+)\n#extension \1 : (?:enable|require)\n#endif\n",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Returns <paramref name="glsl"/> with the prelude in front when the stage needs it, and the
    /// same string otherwise.
    /// </summary>
    /// <param name="glsl">The fully rewritten stage, newlines normalized to <c>\n</c>.</param>
    public static string Apply(string glsl)
    {
        if (!NeedsExtension.IsMatch(glsl))
        {
            return glsl;
        }

        var hoisted = new StringBuilder();
        string withoutBlocks = GuardedExtensionBlock.Replace(glsl, m =>
        {
            hoisted.Append(m.Value);
            return string.Empty;
        });

        var rest = new StringBuilder(withoutBlocks.Length);
        int depth = 0;
        foreach (string line in withoutBlocks.Split('\n'))
        {
            if (line.StartsWith("#if", StringComparison.Ordinal))
            {
                depth++;
            }
            else if (line.StartsWith("#endif", StringComparison.Ordinal))
            {
                depth--;
            }
            else if (depth == 0 && line.StartsWith("#extension", StringComparison.Ordinal))
            {
                hoisted.Append(line).Append('\n');
                continue;
            }
            rest.Append(line).Append('\n');
        }
        rest.Length--; // Split yields one piece more than there are newlines.

        return Guard + Extension + (UintUse.IsMatch(glsl) ? UintAlias : string.Empty) + EndGuard
            + hoisted + rest;
    }
}
