#nullable enable

namespace ShadowDusk.Core;

public sealed record MgfxTechniqueInfo(
    string                        Name,
    IReadOnlyList<AnnotationInfo> Annotations,
    IReadOnlyList<MgfxPassInfo>   Passes
);

public sealed record MgfxPassInfo(
    string                        Name,
    IReadOnlyList<AnnotationInfo> Annotations,
    int                           VertexShaderIndex,
    int                           PixelShaderIndex,
    RenderStateBlock              RenderState
);
