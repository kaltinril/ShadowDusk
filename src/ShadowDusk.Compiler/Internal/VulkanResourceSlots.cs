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

    internal const string WrongDescriptorSetCode = "SD0229";

    /// <summary>
    /// The one descriptor set MonoGame's DesktopVK binds for a stage: the vertex shader's
    /// resources in set 0, the pixel shader's in set 1 (the <c>-auto-binding-space</c> and
    /// <c>-fvk-bind-globals</c> sets <see cref="ShadowDusk.HLSL.Dxc.DxcFlagBuilder"/> passes, and
    /// what mgfxc 3.8.5's own compile command gives).
    /// </summary>
    internal static int DescriptorSetOf(ShaderStage stage) => stage == ShaderStage.Vertex ? 0 : 1;

    /// <summary>
    /// Returns <paramref name="reflected"/> with each texture's and sampler's
    /// <see cref="TextureReflection.BindSlot"/> / <see cref="SamplerReflection.BindSlot"/> set to
    /// <c>RawBinding - 32</c>. Fails with <c>SD0228</c>, naming EVERY texture and sampler whose
    /// binding is below 32, or with <c>SD0229</c> for a resource outside the stage's descriptor set.
    /// Constant buffers and parameters are returned unchanged.
    /// </summary>
    /// <param name="reflected">One stage's SPIR-V reflection.</param>
    /// <param name="stage">The stage, which decides the descriptor set its resources must be in.</param>
    /// <param name="compiledSource">The text the stage was compiled from, searched for the
    /// declaration so the error points at the author's line.</param>
    /// <param name="sourceFileName">The effect's file name, used when the declaration is not found.</param>
    public static Result<ReflectedEffect, ShaderError> FromBindings(
        ReflectedEffect reflected, ShaderStage stage, string compiledSource, string sourceFileName)
    {
        var below = new List<(string Kind, string Name, int Binding)>();
        foreach (TextureReflection tex in reflected.Textures)
            if (tex.RawBinding < SlotOffset)
                below.Add(("texture", tex.Name, tex.RawBinding));
        foreach (SamplerReflection samp in reflected.Samplers)
            if (samp.RawBinding < SlotOffset)
                below.Add(("sampler", samp.Name, samp.RawBinding));
        if (below.Count > 0)
            return Result<ReflectedEffect, ShaderError>.Fail(BelowOffset(below, compiledSource, sourceFileName));

        int set = DescriptorSetOf(stage);
        var outside = reflected.Textures.Where(t => t.RawDescriptorSet != set).Select(t => ("texture", t.Name, t.RawDescriptorSet))
            .Concat(reflected.Samplers.Where(s => s.RawDescriptorSet != set).Select(s => ("sampler", s.Name, s.RawDescriptorSet)))
            .Concat(reflected.ConstantBuffers.Where(c => c.RawDescriptorSet != set).Select(c => ("constant buffer", c.Name, c.RawDescriptorSet)))
            .ToList();
        if (outside.Count > 0)
            return Result<ReflectedEffect, ShaderError>.Fail(WrongSet(outside[0], stage, set, compiledSource, sourceFileName));

        var textures = reflected.Textures.Select(t => t with { BindSlot = t.RawBinding - SlotOffset }).ToList();
        var samplers = reflected.Samplers.Select(s => s with { BindSlot = s.RawBinding - SlotOffset }).ToList();
        return Result<ReflectedEffect, ShaderError>.Ok(reflected with { Textures = textures, Samplers = samplers });
    }

    private static ShaderError BelowOffset(
        IReadOnlyList<(string Kind, string Name, int Binding)> below, string compiledSource, string sourceFileName)
    {
        // Located at the first one the source declares literally; every one is named.
        (string File, int Line, int Column) at = (sourceFileName, 0, 0);
        foreach (var (_, name, _) in below)
        {
            at = Locate(compiledSource, name, sourceFileName);
            if (at.Line > 0)
                break;
        }

        string list = string.Join(", ", below.Select(b => $"{b.Kind} '{b.Name}' (binding {b.Binding})"));
        string example = below[0].Kind == "texture" ? "t0" : "s0";
        return new ShaderError(
            File:    at.File,
            Line:    at.Line,
            Column:  at.Column,
            Code:    BelowOffsetCode,
            Message: $"Vulkan target: {list} {(below.Count == 1 ? "was" : "were")} numbered by DXC's automatic " +
                     $"binding, below the {SlotOffset} that MonoGame's Vulkan runtime subtracts to find a slot " +
                     $"(slot = binding - {SlotOffset}), so {(below.Count == 1 ? "it has" : "they have")} no slot the " +
                     "runtime can bind. ShadowDusk pins every texture and sampler declaration it recognises to an " +
                     "explicit register before compiling (on the macro-expanded source too); " +
                     $"{(below.Count == 1 ? "this one is" : "these are")} declared in a form that rewrite does not " +
                     "model, such as two on one line. Declare each on its own line with an explicit register " +
                     $"(e.g. ': register({example})'), or keep it out of the Vulkan build with '#if !VULKAN'.");
    }

    private static ShaderError WrongSet(
        (string Kind, string Name, int Set) resource, ShaderStage stage, int expected, string compiledSource, string sourceFileName)
    {
        var (file, line, column) = Locate(compiledSource, resource.Name, sourceFileName);
        string stageName = stage == ShaderStage.Vertex ? "vertex" : "pixel";
        return new ShaderError(
            File:    file,
            Line:    line,
            Column:  column,
            Code:    WrongDescriptorSetCode,
            Message: $"Vulkan target: {resource.Kind} '{resource.Name}' is in descriptor set {resource.Set}, but " +
                     $"MonoGame's DesktopVK binds a {stageName} shader's resources from set {expected} only, so the " +
                     "shader would read a descriptor the runtime never writes (mgfxc 3.8.5's build of this shape " +
                     "draws (0,0,0,0)). A register space ('register(t1, space0)') is a Vulkan descriptor set; " +
                     "ShadowDusk drops the space from every register clause it can see, and this one reaches the " +
                     "compiler in a form it cannot (for example from a macro). Remove the ', spaceN' from the " +
                     "declaration.");
    }

    // The declaration: `<Type>[<...>] name` followed by `:` / `;` / `,` (comments and strings masked).
    private static (string File, int Line, int Column) Locate(string text, string name, string sourceFileName)
    {
        Match m = Regex.Match(
            ResourceArrayDiagnostics.MaskCommentsAndStrings(text),
            $@"\b(?:Texture\w*|Sampler\w*|sampler\w*|cbuffer|ConstantBuffer)\b[^;{{}}()]*?\b{Regex.Escape(name)}\b",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return (sourceFileName, 0, 0);

        int nameOffset = m.Index + m.Length - name.Length;
        var (file, line, column) = VulkanTextureSamplerBindingRewriter.Locate(text, nameOffset);
        return (string.IsNullOrEmpty(file) ? sourceFileName : file, line, column);
    }
}
