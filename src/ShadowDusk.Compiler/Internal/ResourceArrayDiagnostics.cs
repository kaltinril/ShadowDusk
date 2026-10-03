#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The diagnostics for an ARRAY of textures or samplers (<c>Texture2D Tex[N]</c>,
/// <c>SamplerState S[N]</c>): <c>SD0221</c>, an error, on Vulkan; <c>SD0222</c>, a warning for a
/// texture array, on DirectX 12 (both issue #324); <c>SD0223</c>, an error for a sampler array, on
/// DirectX 11 and DirectX 12 (issue #340). A texture array on DirectX 11 needs no diagnostic: since
/// issue #339 its table is mgfxc's (one parameter, one record at the base slot), and real MonoGame
/// WindowsDX reads the other elements through <c>GraphicsDevice.Textures[i]</c>.
///
/// <para><b>What the reference compiler does</b> (measured 2026-10-02, <c>dotnet-mgfxc</c> 3.8.5,
/// <c>Texture2D Tex[N]</c> sampled through one <c>SamplerState</c> for N = 1, 2 and 4, with and
/// without an explicit register, identical for every shape):</para>
/// <list type="bullet">
///   <item><c>/Profile:Vulkan</c>: the <c>.mgfx</c> carries <b>no parameter at all, no sampler
///   record and no descriptor-layout binding</b>, although its SPIR-V still declares and samples
///   the array. Loaded into real MonoGame 3.8.5 DesktopVK (<c>validation/VsDrivenVulkan --
///   texarr-reference</c>) the effect has no <c>Tex</c> parameter and draws nothing (0 opaque
///   pixels). A sampler array mgfxc refuses outright, on every profile, in its own parser
///   (<c>Unexpected token '[' found</c>). Nothing a consumer can bind exists on either side, so
///   the honest output is a compile error, as <c>SD0218</c> is for wave intrinsics.</item>
///   <item><c>/Profile:DirectX_12</c>: one <c>Tex</c> parameter bound to slot 0, header
///   <c>maxTextureSlot</c> 0, which ShadowDusk already emitted. Measured in real MonoGame 3.8.5
///   WindowsDX12 (<c>validation/VsDrivenDx12 -- texarr</c>, RTX 3080): both compilers' effects
///   load and draw the same picture, and element [1] reads as ZERO even with
///   <c>GraphicsDevice.Textures[1]</c> set, because the header sizes the descriptor range for one
///   texture. The table stays mgfxc's (the output bytes do not move); the consumer is told.</item>
/// </list>
///
/// <para>Before this ShadowDusk's own Vulkan reflection silently DROPPED the array (the SPIR-V
/// reflector matched an image type but not an array of image types), so a <c>Texture2D Tex[2]</c>
/// compiled to an effect whose only parameter was the sampler. The reflector now reports the
/// array (<see cref="TextureReflection.ArrayLength"/>) and this class turns it into the
/// diagnostic.</para>
/// </summary>
internal static class ResourceArrayDiagnostics
{
    /// <summary>The registered Vulkan error code (<c>docs/error-codes.md</c>).</summary>
    public const string VulkanCode = "SD0221";

    /// <summary>The registered DirectX 12 warning code (<c>docs/error-codes.md</c>).</summary>
    public const string DirectX12Code = "SD0222";

    /// <summary>The registered DirectX 11 / DirectX 12 sampler-array error code (<c>docs/error-codes.md</c>).</summary>
    public const string DirectXSamplerArrayCode = "SD0223";

    /// <summary>
    /// Returns the <c>SD0221</c> error for the first reflected texture or sampler array in
    /// <paramref name="reflected"/>, or <see langword="null"/> when the shader declares none.
    /// </summary>
    /// <param name="reflected">The reflected shader stage.</param>
    /// <param name="compiledSource">
    /// The text the stage was compiled from (preprocessor-flattened, with its <c>#line</c>
    /// markers), searched for the array declaration so the error points at the author's line.
    /// </param>
    /// <param name="sourceFileName">The effect's file name, used when the declaration is not found
    /// in <paramref name="compiledSource"/> or sits in text with no <c>#line</c> marker.</param>
    public static ShaderError? VulkanError(ReflectedEffect reflected, string compiledSource, string sourceFileName)
    {
        if (FirstArray(reflected) is not { } array)
            return null;

        string reference = array.Texture is not null
            ? "Real mgfxc 3.8.5 (/Profile:Vulkan) reflects this declaration as NO effect parameter, NO " +
              "sampler record and NO descriptor binding (measured for 1, 2 and 4 elements, with and " +
              "without an explicit register), so the texture can never be set through Effect.Parameters " +
              "and the effect draws nothing in real MonoGame DesktopVK."
            : "Real mgfxc refuses an array of samplers on every profile (\"Unexpected token '[' found\"), " +
              "and MonoGame's Vulkan effect format binds one sampler per descriptor slot.";

        var (file, line, column) = Locate(compiledSource, array.Name, sourceFileName);
        return new ShaderError(
            File:    file,
            Line:    line,
            Column:  column,
            Code:    VulkanCode,
            Message: $"Vulkan target: '{array.Name}' is {array.Count} {array.Kind} ('{array.Declaration}'). MonoGame's " +
                     $"Vulkan effect format has no representation for an array of {array.Kind}: it binds one " +
                     $"{array.Kind.TrimEnd('s')} per descriptor slot and names it by one effect parameter. " +
                     reference + " ShadowDusk refuses the shape rather than emit that effect. Declare " +
                     $"each element separately (e.g. '{array.Elementwise}') and sample each by name, or keep " +
                     "the array out of the Vulkan build with '#if !VULKAN'. DirectX 12 compiles it (as mgfxc " +
                     "does: one parameter, bound to the first slot).");
    }

    /// <summary>
    /// Returns the <c>SD0222</c> warning for the first reflected TEXTURE array of two or more
    /// elements in <paramref name="reflected"/>, or <see langword="null"/> when the shader declares
    /// none (a 1-element array is one texture and behaves as one). The output is not changed: it is
    /// the table mgfxc writes. A sampler array never reaches this: <see cref="DirectXSamplerArrayError"/>
    /// refuses it first.
    /// </summary>
    public static ShaderError? DirectX12Warning(ReflectedEffect reflected, string compiledSource, string sourceFileName)
    {
        TextureReflection? texture = reflected.Textures.FirstOrDefault(t => t.ArrayLength is not null);
        if (texture is null || texture.ArrayLength == 1)
            return null;

        var array = new Array(texture.Name, texture.ArrayLength!.Value, texture);
        var (file, line, column) = Locate(compiledSource, array.Name, sourceFileName);
        return new ShaderError(
            File:     file,
            Line:     line,
            Column:   column,
            Code:     DirectX12Code,
            Message:  $"DirectX 12 target: '{array.Name}' is {array.Count} {array.Kind} ('{array.Declaration}'). The " +
                      $"effect reflects it as ONE parameter bound to the first slot, exactly as mgfxc 3.8.5 does, " +
                      "and that is what ships; but elements beyond [0] cannot be set through Effect.Parameters, " +
                      "and the shader header sizes the descriptor range for one texture, so in real MonoGame 3.8.5 " +
                      "WindowsDX12 they read as zero even with GraphicsDevice.Textures[i] set (measured; mgfxc's " +
                      "own build behaves the same). Declare each element separately " +
                      $"(e.g. '{array.Elementwise}') and sample each by name if every element must be read.",
            Severity: ShaderErrorSeverity.Warning);
    }

    /// <summary>
    /// Returns the <c>SD0223</c> error for the first array of SAMPLERS the shader declares
    /// (<c>SamplerState S[N]</c>), or <see langword="null"/> when it declares none, for the
    /// DirectX 11 and DirectX 12 targets (issue #340).
    /// </summary>
    /// <remarks>
    /// Real <c>mgfxc</c> (3.8.4.1 and 3.8.5, measured 2026-10-02) refuses an array of samplers on
    /// EVERY profile in its own effect parser, before any shader compiles:
    /// <c>file(line,col) : Unexpected token '[' found. Expected Semicolon, Comma, or CloseParenthesis.</c>
    /// So no reference output exists for the shape, and MonoGame's effect format has nowhere to
    /// put it: a sampler record carries one sampler slot, keyed to one texture slot, and names no
    /// element. ShadowDusk used to compile it anyway, with records keyed on the texture bindings
    /// (so no sampler element ever had a record of its own). Measured as evidence first
    /// (<c>validation/VsDrivenDx -- samparr &lt;mgfx&gt;</c>, <c>VsDrivenDx12 -- samparr &lt;mgfx&gt;</c>):
    /// the pre-fix effects of <c>SamplerArray2.fx</c> loaded into real WindowsDX and WindowsDX12
    /// and drew with both textures sampled. They are nonetheless effects mgfxc never builds, so the
    /// shape is refused by name, located at the declaration, like <c>SD0221</c> does on Vulkan.
    ///
    /// <para>Detection is by reflection (<see cref="SamplerReflection.ArrayLength"/>: the collapsed
    /// SM5 records on DirectX 11, DXIL's <c>BindCount</c> on DirectX 12), with the declaration text
    /// as the fallback for the one case reflection cannot see, a 1-element sampler array on
    /// DirectX 12 (DXIL reports <c>BindCount</c> 1 for it, the same as a plain sampler), which mgfxc
    /// refuses all the same.</para>
    ///
    /// <para><paramref name="combinedSamplerHalves"/> names the sampler arrays the real-slangc
    /// route emitted as the sampler half of an author's combined-sampler array (<c>Sampler2D
    /// T[N]</c>; see <see cref="CompilerOptions.SamplerArraysFromCombinedSamplers"/>). Those are one
    /// author resource, lowered, so they are skipped here and the texture half carries the array
    /// diagnostics, which is the table the hand-written <c>Texture2D T[N]; SamplerState S;</c>
    /// gets. Empty for every <c>.fx</c> compile.</para>
    /// </remarks>
    public static ShaderError? DirectXSamplerArrayError(
        ReflectedEffect reflected, string compiledSource, string sourceFileName, PlatformTarget target,
        IReadOnlyCollection<string>? combinedSamplerHalves = null)
    {
        string targetName = target == PlatformTarget.DirectX12 ? "DirectX 12" : "DirectX 11";
        string masked = MaskCommentsAndStrings(compiledSource);

        Array? array = null;
        foreach (SamplerReflection sampler in reflected.Samplers)
        {
            if (combinedSamplerHalves is not null && combinedSamplerHalves.Contains(sampler.Name))
                continue;
            if (sampler.ArrayLength is { } length)
            {
                array = new Array(sampler.Name, length, null);
                break;
            }
            if (DeclaredSamplerArrayLength(masked, sampler.Name) is { } declaredLength)
            {
                array = new Array(sampler.Name, declaredLength, null);
                break;
            }
        }
        if (array is null)
            return null;

        var (file, line, column) = Locate(compiledSource, array.Name, sourceFileName);
        return new ShaderError(
            File:    file,
            Line:    line,
            Column:  column,
            Code:    DirectXSamplerArrayCode,
            Message: $"{targetName} target: '{array.Name}' is {array.Count} samplers ('{array.Declaration}'). Real mgfxc " +
                     "(3.8.4.1 and 3.8.5) refuses an array of samplers on every profile in its own effect parser " +
                     "(\"Unexpected token '[' found. Expected Semicolon, Comma, or CloseParenthesis.\"), so no " +
                     "reference output exists for the shape, and MonoGame's effect format has no place for it: a " +
                     "sampler record carries one sampler slot keyed to one texture slot and names no element. " +
                     "ShadowDusk refuses it rather than ship an effect mgfxc never builds. Declare each sampler " +
                     $"separately (e.g. '{array.Elementwise}') and sample through each by name. (Vulkan refuses the " +
                     "same shape with SD0221; OpenGL cannot lower it, SD0100.)");
    }

    // `SamplerState Name[N]` / `sampler Name[N]` in the masked source: the declared element count
    // (0 for `[]`), or null when the sampler is not declared as an array.
    private static int? DeclaredSamplerArrayLength(string masked, string name)
    {
        Match m = Regex.Match(
            masked,
            $@"\b(?:sampler\w*|SamplerState|SamplerComparisonState)\s+{Regex.Escape(name)}\s*\[\s*(?<n>\d*)\s*\]",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        string digits = m.Groups["n"].Value;
        return digits.Length == 0 ? 0 : int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private sealed record Array(string Name, int Length, TextureReflection? Texture)
    {
        public string Kind => Texture is not null ? "textures" : "samplers";

        public string Count => Length == 0 ? "an unbounded array of" : Length == 1 ? "a 1-element array of" : $"an array of {Length}";

        public string Declaration => Texture is not null
            ? $"{Keyword(Texture.Dimension)} {Name}[{(Length == 0 ? "" : Length)}]"
            : $"SamplerState {Name}[{(Length == 0 ? "" : Length)}]";

        public string Elementwise => Texture is not null
            ? $"{Keyword(Texture.Dimension)} {Name}0; {Keyword(Texture.Dimension)} {Name}1;"
            : $"SamplerState {Name}0; SamplerState {Name}1;";
    }

    // A texture array first: that is the declaration the author set out to use, and a combined
    // `Sampler2D T[N]` from the Slang route reports both halves under one name.
    private static Array? FirstArray(ReflectedEffect reflected)
    {
        TextureReflection? texture = reflected.Textures.FirstOrDefault(t => t.ArrayLength is not null);
        if (texture is not null)
            return new Array(texture.Name, texture.ArrayLength!.Value, texture);

        SamplerReflection? sampler = reflected.Samplers.FirstOrDefault(s => s.ArrayLength is not null);
        return sampler is null ? null : new Array(sampler.Name, sampler.ArrayLength!.Value, null);
    }

    // The declaration `Name[` (the name immediately followed by the array brackets). The
    // register pass of the Slang route and the Vulkan binding rewriter leave the brackets in
    // place, so this finds the declaration in slangc's emission as well as in author HLSL.
    // Searched on a copy with comments and string literals blanked (offsets preserved): the
    // preprocessed text keeps the author's comments, and a header that mentions `Tex[2]` is
    // not the declaration (measured on the TextureArray2.fx fixture, whose header does).
    internal static (string File, int Line, int Column) Locate(string text, string name, string sourceFileName)
    {
        Match m = Regex.Match(MaskCommentsAndStrings(text), $@"\b{Regex.Escape(name)}\s*\[", RegexOptions.CultureInvariant);
        if (!m.Success)
            return (sourceFileName, 0, 0);

        var (file, line, column) = VulkanTextureSamplerBindingRewriter.Locate(text, m.Index);
        return (string.IsNullOrEmpty(file) ? sourceFileName : file, line, column);
    }

    /// <summary>
    /// Blanks <c>//</c> and <c>/* */</c> comments and string literals with spaces, keeping every
    /// newline and every offset, so a match in the result locates in the original text.
    /// </summary>
    internal static string MaskCommentsAndStrings(string text)
    {
        char[] chars = text.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            char c = chars[i];
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n')
                    chars[i++] = ' ';
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                chars[i++] = ' ';
                chars[i++] = ' ';
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n')
                        chars[i] = ' ';
                    i++;
                }
                if (i < chars.Length)
                    chars[i++] = ' ';
                if (i < chars.Length)
                    chars[i++] = ' ';
            }
            else if (c == '"')
            {
                chars[i++] = ' ';
                while (i < chars.Length && chars[i] != '"' && chars[i] != '\n')
                {
                    if (chars[i] == '\\' && i + 1 < chars.Length)
                        chars[i++] = ' ';
                    chars[i++] = ' ';
                }
                if (i < chars.Length && chars[i] == '"')
                    chars[i++] = ' ';
            }
            else
            {
                i++;
            }
        }
        return new string(chars);
    }

    private static string Keyword(TextureDimension dimension) => dimension switch
    {
        TextureDimension.Texture1D   => "Texture1D",
        TextureDimension.Texture3D   => "Texture3D",
        TextureDimension.TextureCube => "TextureCube",
        _                            => "Texture2D",
    };
}
