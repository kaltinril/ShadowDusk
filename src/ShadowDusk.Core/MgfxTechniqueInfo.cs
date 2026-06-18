#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// One effect technique as the MGFX writer serializes it: its name, any FX annotations, and
/// its ordered passes.
/// </summary>
/// <param name="Name">The technique name.</param>
/// <param name="Annotations">FX annotations attached to the technique.</param>
/// <param name="Passes">The technique's passes, in source order.</param>
public sealed record MgfxTechniqueInfo(
    string                        Name,
    IReadOnlyList<AnnotationInfo> Annotations,
    IReadOnlyList<MgfxPassInfo>   Passes
);

/// <summary>
/// One pass within a technique: its name, FX annotations, the vertex/pixel shader it binds
/// (by index into the effect's shader list, or <c>-1</c> when absent), and its render state.
/// </summary>
/// <param name="Name">The pass name.</param>
/// <param name="Annotations">FX annotations attached to the pass.</param>
/// <param name="VertexShaderIndex">Index of the vertex shader in the effect's shader list, or <c>-1</c> if none.</param>
/// <param name="PixelShaderIndex">Index of the pixel shader in the effect's shader list, or <c>-1</c> if none.</param>
/// <param name="RenderState">The render states declared in the pass.</param>
public sealed record MgfxPassInfo(
    string                        Name,
    IReadOnlyList<AnnotationInfo> Annotations,
    int                           VertexShaderIndex,
    int                           PixelShaderIndex,
    RenderStateBlock              RenderState
);
