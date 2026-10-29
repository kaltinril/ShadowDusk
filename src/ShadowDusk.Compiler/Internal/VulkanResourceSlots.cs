#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// <b>Vulkan only:</b> gives every reflected texture and sampler the slot MonoGame's Vulkan
/// runtime and <c>mgfxc</c> 3.8.5 <c>/Profile:Vulkan</c> use, which is its SPIR-V binding minus
/// the 32 that <c>-fvk-t-shift 32 all</c> / <c>-fvk-s-shift 32 all</c> add
/// (<see cref="ShadowDusk.HLSL.Dxc.DxcFlagBuilder"/>). For an explicit <c>register(t1)</c> that is
/// slot 1, the same slot DirectX 11 and DirectX 12 give it.
///
/// <para><b>Why the slot must be the binding.</b> Two halves of MonoGame 3.8.5's DesktopVK runtime
/// meet on this number. <c>EffectPass.Apply</c> writes a texture parameter to
/// <c>GraphicsDevice.Textures[record.TextureSlot]</c>; the native descriptor writer
/// (<c>MGVK_UpdateDescriptors</c> in <c>MGG_Vulkan.cpp</c>) reads the image for a descriptor from
/// <c>textures[stage][binding - 32]</c>. They agree only when the record's slot IS
/// <c>binding - 32</c>. <c>mgfxc</c> computes exactly that
/// (<c>BindingSlot.Value - SlotOffset</c>), and so do the header's texture/sampler slot masks and
/// texture-type table (<see cref="ShadowDusk.Core.VulkanShaderCodeWrapper"/>).</para>
///
/// <para><b>What it replaces.</b> The SPIR-V reflector numbers textures and samplers by their
/// ORDER among the stage's bindings (0, 1, 2, ...). That equals <c>binding - 32</c> only when the
/// registers start at 0 with no gap, which is every shape the binding rewriter numbers itself
/// (<see cref="VulkanTextureSamplerBindingRewriter"/>). An explicit register with nothing below
/// it, or a gap (<c>t0</c> + <c>t2</c>), broke the agreement: a lone <c>Texture2D Tex1 :
/// register(t1)</c> was written as slot 0 while its descriptor read slot 1, so in real DesktopVK
/// the parameter's texture went to slot 0 and the effect sampled an unbound slot ((0,0,0,0)
/// where <c>mgfxc</c>'s build draws the texture). The same held for a <c>Texture2DArray</c> on
/// <c>register(t1)</c>.</para>
///
/// <para><b>Binding below 32</b> (<c>SD0228</c>). Every texture and sampler declaration the
/// rewriter recognises reaches DXC with an explicit register, so its binding is at least 32. One
/// it does not recognise is auto-numbered by DXC from 0 and has no slot the runtime can recover:
/// <c>mgfxc</c>'s own arithmetic wraps such a slot to 224+ and its container access-violates in
/// DesktopVK. Refused by name instead of written with a slot that cannot bind. The pipeline first
/// retries such an effect once from its macro-expanded preprocessed source (a declaration that
/// comes out of a macro is literal there), so the refusal stands only for a shape that retry
/// cannot pin either.</para>
/// </summary>
internal static class VulkanResourceSlots
{
    /// <summary>The <c>-fvk-t-shift</c> / <c>-fvk-s-shift</c> amount (mgfxc's <c>SlotOffset</c>).</summary>
    internal const int SlotOffset = 32;

    internal const string BelowOffsetCode = "SD0228";

    /// <summary>
    /// Returns <paramref name="reflected"/> with each texture's and sampler's
    /// <see cref="TextureReflection.BindSlot"/> / <see cref="SamplerReflection.BindSlot"/> set to
    /// <c>RawBinding - 32</c>, or <c>SD0228</c> for the first one whose binding is below 32.
    /// Constant buffers and parameters are returned unchanged.
    /// </summary>
    /// <param name="reflected">One stage's SPIR-V reflection.</param>
    /// <param name="compiledSource">The text the stage was compiled from, searched for the
    /// declaration so the error points at the author's line.</param>
    /// <param name="sourceFileName">The effect's file name, used when the declaration is not found.</param>
    public static Result<ReflectedEffect, ShaderError> FromBindings(
        ReflectedEffect reflected, string compiledSource, string sourceFileName)
    {
        var textures = new List<TextureReflection>(reflected.Textures.Count);
        foreach (TextureReflection tex in reflected.Textures)
        {
            if (tex.RawBinding < SlotOffset)
                return Result<ReflectedEffect, ShaderError>.Fail(BelowOffset("texture", tex.Name, tex.RawBinding, compiledSource, sourceFileName));
            textures.Add(tex with { BindSlot = tex.RawBinding - SlotOffset });
        }

        var samplers = new List<SamplerReflection>(reflected.Samplers.Count);
        foreach (SamplerReflection samp in reflected.Samplers)
        {
            if (samp.RawBinding < SlotOffset)
                return Result<ReflectedEffect, ShaderError>.Fail(BelowOffset("sampler", samp.Name, samp.RawBinding, compiledSource, sourceFileName));
            samplers.Add(samp with { BindSlot = samp.RawBinding - SlotOffset });
        }

        return Result<ReflectedEffect, ShaderError>.Ok(reflected with { Textures = textures, Samplers = samplers });
    }

    private static ShaderError BelowOffset(string kind, string name, int binding, string compiledSource, string sourceFileName)
    {
        var (file, line, column) = Locate(compiledSource, name, sourceFileName);
        return new ShaderError(
            File:    file,
            Line:    line,
            Column:  column,
            Code:    BelowOffsetCode,
            Message: $"Vulkan target: {kind} '{name}' was given SPIR-V binding {binding} by DXC's automatic numbering, " +
                     $"below the {SlotOffset} that MonoGame's Vulkan runtime subtracts to find a {kind}'s slot " +
                     $"(slot = binding - {SlotOffset}), so it has no slot the runtime can bind. ShadowDusk pins every " +
                     "texture and sampler declaration it recognises to an explicit register before compiling; this " +
                     "one is declared in a form that rewrite does not model. Declare it on its own line with an " +
                     $"explicit register (e.g. ': register({(kind == "texture" ? "t" : "s")}0)'), or keep it out of the " +
                     "Vulkan build with '#if !VULKAN'.");
    }

    // The declaration: `<Type>[<...>] name` followed by `:` / `;` / `,` (comments and strings masked).
    private static (string File, int Line, int Column) Locate(string text, string name, string sourceFileName)
    {
        Match m = Regex.Match(
            ResourceArrayDiagnostics.MaskCommentsAndStrings(text),
            $@"\b(?:Texture\w*|Sampler\w*|sampler\w*)\b[^;{{}}()]*?\b{Regex.Escape(name)}\b",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return (sourceFileName, 0, 0);

        int nameOffset = m.Index + m.Length - name.Length;
        var (file, line, column) = VulkanTextureSamplerBindingRewriter.Locate(text, nameOffset);
        return (string.IsNullOrEmpty(file) ? sourceFileName : file, line, column);
    }
}
