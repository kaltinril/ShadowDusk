#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The single source of truth for the raw D3D reflection numeric value → ShadowDusk
/// enum lookup tables (<c>D3D_SHADER_VARIABLE_CLASS</c>, <c>D3D_SHADER_VARIABLE_TYPE</c>,
/// and <c>D3D_SRV_DIMENSION</c>). Both reflection readers consume these so the numeric
/// mappings cannot drift apart: the pure-managed <see cref="RdefReader"/> passes the raw
/// values it reads from the DXBC container, while the DXIL extractor casts its
/// Vortice/D3D12 reflection enums (whose underlying values are these same D3D constants).
///
/// These helpers only own the value → enum table. Each caller keeps its own
/// unmapped-value policy: the DXIL extractor throws on an unmapped class/type, whereas
/// <see cref="RdefReader"/> reports failure (<see langword="false"/>) — that difference is
/// deliberate and is preserved at the call sites, not unified here.
/// </summary>
public static class D3DReflectionMaps
{
    /// <summary>
    /// Maps a <c>D3D_SHADER_VARIABLE_CLASS</c> numeric value to an
    /// <see cref="EffectParameterClass"/>. Row- and column-major matrices both fold to
    /// <see cref="EffectParameterClass.Matrix"/>.
    /// </summary>
    /// <param name="cls">The raw <c>D3D_SHADER_VARIABLE_CLASS</c> value.</param>
    /// <param name="mapped">The mapped class when the value is recognised.</param>
    /// <returns><see langword="true"/> when the value is recognised; otherwise <see langword="false"/>.</returns>
    public static bool TryMapClass(uint cls, out EffectParameterClass mapped)
    {
        switch (cls)
        {
            case 0: mapped = EffectParameterClass.Scalar; return true;
            case 1: mapped = EffectParameterClass.Vector; return true;
            case 2: mapped = EffectParameterClass.Matrix; return true; // row-major
            case 3: mapped = EffectParameterClass.Matrix; return true; // column-major
            case 4: mapped = EffectParameterClass.Object; return true;
            case 5: mapped = EffectParameterClass.Struct; return true;
            default: mapped = default; return false;
        }
    }

    /// <summary>
    /// Maps a <c>D3D_SHADER_VARIABLE_TYPE</c> numeric value to an
    /// <see cref="EffectParameterType"/>. <c>uint</c> (19) folds into
    /// <see cref="EffectParameterType.Int32"/>, matching mgfxc/MonoGame's parameter model.
    /// </summary>
    /// <param name="type">The raw <c>D3D_SHADER_VARIABLE_TYPE</c> value.</param>
    /// <param name="mapped">The mapped type when the value is recognised.</param>
    /// <returns><see langword="true"/> when the value is recognised; otherwise <see langword="false"/>.</returns>
    public static bool TryMapType(uint type, out EffectParameterType mapped)
    {
        switch (type)
        {
            case 0:  mapped = EffectParameterType.Void;        return true;
            case 1:  mapped = EffectParameterType.Bool;        return true;
            case 2:  mapped = EffectParameterType.Int32;       return true;
            case 19: mapped = EffectParameterType.Int32;       return true; // uint
            case 3:  mapped = EffectParameterType.Single;      return true;
            case 4:  mapped = EffectParameterType.String;      return true;
            case 5:  mapped = EffectParameterType.Texture;     return true;
            case 6:  mapped = EffectParameterType.Texture1D;   return true;
            case 7:  mapped = EffectParameterType.Texture2D;   return true;
            case 8:  mapped = EffectParameterType.Texture3D;   return true;
            case 9:  mapped = EffectParameterType.TextureCube; return true;
            default: mapped = default; return false;
        }
    }

    /// <summary>
    /// Maps a <c>D3D_SRV_DIMENSION</c> numeric value to a <see cref="TextureDimension"/>.
    /// Array and multisample variants collapse onto their base dimensionality; an
    /// unrecognised value yields <see cref="TextureDimension.Unknown"/>.
    /// </summary>
    /// <param name="dim">The raw <c>D3D_SRV_DIMENSION</c> value.</param>
    /// <returns>The folded texture dimensionality.</returns>
    public static TextureDimension MapSrvDimension(uint dim) =>
        dim switch
        {
            2  => TextureDimension.Texture1D,   // TEXTURE1D
            3  => TextureDimension.Texture1D,   // TEXTURE1DARRAY
            4  => TextureDimension.Texture2D,   // TEXTURE2D
            5  => TextureDimension.Texture2D,   // TEXTURE2DARRAY
            6  => TextureDimension.Texture2D,   // TEXTURE2DMS
            7  => TextureDimension.Texture2D,   // TEXTURE2DMSARRAY
            8  => TextureDimension.Texture3D,   // TEXTURE3D
            9  => TextureDimension.TextureCube, // TEXTURECUBE
            10 => TextureDimension.TextureCube, // TEXTURECUBEARRAY
            _  => TextureDimension.Unknown,
        };
}
