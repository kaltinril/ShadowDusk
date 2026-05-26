#nullable enable

using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL;

/// <summary>
/// Maps FX9 render-state key/value pairs to their MonoGame equivalents.
/// Key lookup is case-insensitive; boolean values are normalised to "true"/"false".
/// </summary>
public static class RenderStateMapper
{
    private static readonly Dictionary<string, string> KeyToTarget =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CullMode"]                = "RasterizerState.CullMode",
            ["AlphaBlendEnable"]        = "BlendState.AlphaBlendEnable",
            ["SrcBlend"]                = "BlendState.ColorSourceBlend",
            ["DestBlend"]               = "BlendState.ColorDestinationBlend",
            ["BlendOp"]                 = "BlendState.ColorBlendFunction",
            ["AlphaBlendOp"]            = "BlendState.AlphaBlendFunction",
            ["SrcBlendAlpha"]           = "BlendState.AlphaSourceBlend",
            ["DestBlendAlpha"]          = "BlendState.AlphaDestinationBlend",
            ["ColorWriteEnable"]        = "BlendState.ColorWriteChannels",
            ["DepthBufferEnable"]       = "DepthStencilState.DepthBufferEnable",
            ["DepthBufferWriteEnable"]  = "DepthStencilState.DepthBufferWriteEnable",
            ["DepthBufferFunction"]     = "DepthStencilState.DepthBufferFunction",
            ["ZEnable"]                 = "DepthStencilState.DepthBufferEnable",
            ["ZWriteEnable"]            = "DepthStencilState.DepthBufferWriteEnable",
            ["ZFunc"]                   = "DepthStencilState.DepthBufferFunction",
            ["StencilEnable"]           = "DepthStencilState.StencilEnable",
            ["FillMode"]                = "RasterizerState.FillMode",
            ["MultiSampleAntiAlias"]    = "RasterizerState.MultiSampleAntiAlias",
            ["ScissorTestEnable"]       = "RasterizerState.ScissorTestEnable",
        };

    /// <summary>
    /// Attempts to map an FX9 render-state key/value pair to a MonoGame render-state target.
    /// </summary>
    /// <param name="key">The FX9 state name (case-insensitive).</param>
    /// <param name="value">The raw value string from the pass block.</param>
    /// <returns>A <see cref="MappedRenderState"/> if the key is recognised; otherwise <see langword="null"/>.</returns>
    public static MappedRenderState? TryMap(string key, string value)
    {
        if (!KeyToTarget.TryGetValue(key, out string? target))
            return null;

        string normalizedValue = NormalizeValue(value);
        return new MappedRenderState(target, normalizedValue);
    }

    private static string NormalizeValue(string value) =>
        value switch
        {
            "True" or "TRUE" or "true" or "1" => "true",
            "False" or "FALSE" or "false" or "0" => "false",
            _ => value,
        };
}
