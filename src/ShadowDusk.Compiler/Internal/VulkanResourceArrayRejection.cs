#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// <b>Vulkan-only</b>: refuses an effect that declares an ARRAY of textures or samplers
/// (<c>Texture2D Tex[N]</c>, <c>SamplerState S[N]</c>) with <c>SD0221</c>, instead of emitting
/// an effect whose textures can never be set (issue #324).
///
/// <para><b>What the reference compiler does</b> (measured 2026-10-02, <c>dotnet-mgfxc</c>
/// 3.8.5 <c>/Profile:Vulkan</c>, <c>Texture2D Tex[N]</c> sampled through one
/// <c>SamplerState</c> for N = 1, 2 and 4, with and without an explicit register): the
/// <c>.mgfx</c> carries <b>no parameter at all, no sampler record and no descriptor-layout
/// binding</b>, although its SPIR-V still declares and samples the array. The texture is
/// unreachable through <c>Effect.Parameters</c>, and the shader module's descriptors are
/// absent from the layout MonoGame's native Vulkan path builds from that header. A sampler
/// array (<c>SamplerState S[N]</c>) mgfxc refuses outright, on every profile, in its own
/// parser (<c>Unexpected token '[' found</c>). Nothing a consumer can bind exists on either
/// side, so the honest output is a compile error, as <c>SD0218</c> is for wave intrinsics.</para>
///
/// <para>Before this check ShadowDusk's own Vulkan reflection silently DROPPED the array
/// (the SPIR-V reflector matched an image type but not an array of image types), so a
/// <c>Texture2D Tex[2]</c> compiled to an effect whose only parameter was the sampler. The
/// reflector now reports the array (<see cref="TextureReflection.ArrayLength"/>) and this
/// class turns it into the diagnostic. DirectX 12 is deliberately NOT covered: real mgfxc
/// 3.8.5's <c>DirectX_12</c> build reflects one <c>Tex</c> parameter bound to slot 0 for every
/// N (measured, same shapes), ShadowDusk already emits that same table, and a consumer can
/// still reach the other elements through <c>GraphicsDevice.Textures[i]</c>.</para>
/// </summary>
internal static class VulkanResourceArrayRejection
{
    /// <summary>The registered code (<c>docs/error-codes.md</c>).</summary>
    public const string Code = "SD0221";

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
    public static ShaderError? Check(ReflectedEffect reflected, string compiledSource, string sourceFileName)
    {
        TextureReflection? texture = reflected.Textures.FirstOrDefault(t => t.ArrayLength is not null);
        SamplerReflection? sampler = reflected.Samplers.FirstOrDefault(s => s.ArrayLength is not null);
        if (texture is null && sampler is null)
            return null;

        // A texture array first: that is the declaration the author set out to use, and a
        // combined `Sampler2D T[N]` from the Slang route reports both halves under one name.
        string name   = texture?.Name ?? sampler!.Name;
        int    length = texture?.ArrayLength ?? sampler!.ArrayLength ?? 0;
        string kind   = texture is not null ? "textures" : "samplers";
        string count  = length == 0 ? "an unbounded array of" : length == 1 ? "a 1-element array of" : $"an array of {length}";
        string declaration = texture is not null
            ? $"{TextureKeyword(texture.Dimension)} {name}[{(length == 0 ? "" : length)}]"
            : $"SamplerState {name}[{(length == 0 ? "" : length)}]";

        var (file, line, column) = Locate(compiledSource, name, sourceFileName);

        string reference = texture is not null
            ? "Real mgfxc 3.8.5 (/Profile:Vulkan) reflects this declaration as NO effect parameter, NO " +
              "sampler record and NO descriptor binding (measured for 1, 2 and 4 elements, with and " +
              "without an explicit register), so the texture can never be set through Effect.Parameters " +
              "and the effect cannot draw."
            : "Real mgfxc refuses an array of samplers on every profile (\"Unexpected token '[' found\"), " +
              "and MonoGame's Vulkan effect format binds one sampler per descriptor slot.";

        return new ShaderError(
            File:    file,
            Line:    line,
            Column:  column,
            Code:    Code,
            Message: $"Vulkan target: '{name}' is {count} {kind} ('{declaration}'). MonoGame's Vulkan " +
                     $"effect format has no representation for an array of {kind}: it binds one " +
                     $"{kind.TrimEnd('s')} per descriptor slot and names it by one effect parameter. " +
                     reference + " ShadowDusk refuses the shape rather than emit that effect. Declare " +
                     $"each element separately (e.g. '{Elementwise(texture, name)}') and sample each by " +
                     "name, or keep the array out of the Vulkan build with '#if !VULKAN'. DirectX 12 " +
                     "compiles it (as mgfxc does: one parameter, bound to the first slot).");
    }

    // The declaration `Name[` (the name immediately followed by the array brackets). The
    // register pass of the Slang route and the Vulkan binding rewriter leave the brackets in
    // place, so this finds the declaration in slangc's emission as well as in author HLSL.
    private static (string File, int Line, int Column) Locate(string text, string name, string sourceFileName)
    {
        Match m = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*\[", RegexOptions.CultureInvariant);
        if (!m.Success)
            return (sourceFileName, 0, 0);

        var (file, line, column) = VulkanTextureSamplerBindingRewriter.Locate(text, m.Index);
        return (string.IsNullOrEmpty(file) ? sourceFileName : file, line, column);
    }

    private static string TextureKeyword(TextureDimension dimension) => dimension switch
    {
        TextureDimension.Texture1D   => "Texture1D",
        TextureDimension.Texture3D   => "Texture3D",
        TextureDimension.TextureCube => "TextureCube",
        _                            => "Texture2D",
    };

    private static string Elementwise(TextureReflection? texture, string name) =>
        texture is not null
            ? $"{TextureKeyword(texture.Dimension)} {name}0; {TextureKeyword(texture.Dimension)} {name}1;"
            : $"SamplerState {name}0; SamplerState {name}1;";
}
