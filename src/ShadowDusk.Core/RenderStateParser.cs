#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace ShadowDusk.Core;

public sealed class RenderStateParser
{
    // Render-state keys fxc accepts in a pass block whose fx_2_0 ops FNA's Effect runtime
    // throws NotImplementedException on at EffectPass.Apply — the non-† (non-honored) ops
    // of docs/fx2-binary-format.md §8.2. They parse to no RenderStateBlock field; their
    // presence is recorded in RenderStateBlock.KnownFnaThrowingStates so the FNA path can
    // fail loudly instead of silently diverging from the fxc build (which crashes FNA at
    // runtime). MGFX paths ignore the metadata, preserving the silent-ignore behavior.
    private static readonly HashSet<string> FnaThrowingStateKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ShadeMode", "AlphaTestEnable", "LastPixel", "AlphaRef", "AlphaFunc",
            "DitherEnable", "FogEnable", "SpecularEnable", "FogColor", "FogTableMode",
            "FogStart", "FogEnd", "FogDensity", "RangeFogEnable", "TextureFactor",
            "Wrap0", "Wrap1", "Wrap2", "Wrap3", "Wrap4", "Wrap5", "Wrap6", "Wrap7",
            "Wrap8", "Wrap9", "Wrap10", "Wrap11", "Wrap12", "Wrap13", "Wrap14", "Wrap15",
            "Clipping", "Lighting", "Ambient", "FogVertexMode", "ColorVertex",
            "LocalViewer", "NormalizeNormals", "DiffuseMaterialSource",
            "SpecularMaterialSource", "AmbientMaterialSource", "EmissiveMaterialSource",
            "VertexBlend", "ClipPlaneEnable", "PointSize", "PointSize_Min",
            "PointSpriteEnable", "PointScaleEnable", "PointScale_A", "PointScale_B",
            "PointScale_C", "PatchEdgeStyle", "DebugMonitorToken", "PointSize_Max",
            "IndexedVertexBlendEnable", "TweenFactor", "PositionDegree", "NormalDegree",
            "AntialiasedLineEnable", "MinTessellationLevel", "MaxTessellationLevel",
            "AdaptiveTess_X", "AdaptiveTess_Y", "AdaptiveTess_Z", "AdaptiveTess_W",
            "EnableAdaptiveTessellation", "SRGBWriteEnable",
        };

    public Result<RenderStateBlock, ShaderError> Parse(IReadOnlyDictionary<string, string> kvp)
    {
        var block = new RenderStateBlock();
        List<string>? fnaThrowing = null;

        foreach (var (rawKey, rawValue) in kvp)
        {
            var key = rawKey.Trim();
            var value = rawValue.Trim();

            if (FnaThrowingStateKeys.Contains(key))
            {
                (fnaThrowing ??= []).Add(key);
                continue;
            }

            var result = ApplyKey(ref block, key, value);
            if (result.IsFailure)
                return Result<RenderStateBlock, ShaderError>.Fail(result.Error);
        }

        if (fnaThrowing is not null)
        {
            fnaThrowing.Sort(StringComparer.OrdinalIgnoreCase);
            block = block with { KnownFnaThrowingStates = fnaThrowing };
        }

        return Result<RenderStateBlock, ShaderError>.Ok(block);
    }

    private static Result<bool, ShaderError> ApplyKey(
        ref RenderStateBlock block, string key, string value)
    {
        if (key.Equals("CullMode", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseCullMode(value, out var v))
                return UnknownValue(key, value);
            block = block with { CullMode = v };
            return Ok();
        }

        if (key.Equals("FillMode", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseFillMode(value, out var v))
                return UnknownValue(key, value);
            block = block with { FillMode = v };
            return Ok();
        }

        if (key.Equals("ScissorTestEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { ScissorTestEnable = v };
            return Ok();
        }

        if (key.Equals("MultiSampleAntiAlias", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { MultiSampleAntiAlias = v };
            return Ok();
        }

        if (key.Equals("DepthBias", StringComparison.OrdinalIgnoreCase))
        {
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return UnknownValue(key, value);
            block = block with { DepthBias = v };
            return Ok();
        }

        if (key.Equals("SlopeScaleDepthBias", StringComparison.OrdinalIgnoreCase))
        {
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return UnknownValue(key, value);
            block = block with { SlopeScaleDepthBias = v };
            return Ok();
        }

        if (key.Equals("AlphaBlendEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { AlphaBlendEnable = v };
            return Ok();
        }

        if (key.Equals("SrcBlend", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBlend(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorSourceBlend = v };
            return Ok();
        }

        if (key.Equals("DestBlend", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBlend(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorDestinationBlend = v };
            return Ok();
        }

        if (key.Equals("BlendOp", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBlendFunction(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorBlendFunction = v };
            return Ok();
        }

        if (key.Equals("SrcBlendAlpha", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBlend(value, out var v))
                return UnknownValue(key, value);
            block = block with { AlphaSourceBlend = v };
            return Ok();
        }

        if (key.Equals("DestBlendAlpha", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBlend(value, out var v))
                return UnknownValue(key, value);
            block = block with { AlphaDestinationBlend = v };
            return Ok();
        }

        if (key.Equals("BlendOpAlpha", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBlendFunction(value, out var v))
                return UnknownValue(key, value);
            block = block with { AlphaBlendFunction = v };
            return Ok();
        }

        if (key.Equals("ColorWriteEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorWriteChannels = v };
            return Ok();
        }

        if (key.Equals("ZEnable", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("DepthBufferEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { DepthBufferEnable = v };
            return Ok();
        }

        if (key.Equals("ZWriteEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { DepthBufferWriteEnable = v };
            return Ok();
        }

        if (key.Equals("ZFunc", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseCompareFunction(value, out var v))
                return UnknownValue(key, value);
            block = block with { DepthBufferFunction = v };
            return Ok();
        }

        if (key.Equals("StencilEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilEnable = v };
            return Ok();
        }

        if (key.Equals("StencilRef", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value, out var v))
                return UnknownValue(key, value);
            block = block with { ReferenceStencil = v };
            return Ok();
        }

        if (key.Equals("StencilMask", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilMask = v };
            return Ok();
        }

        if (key.Equals("StencilWriteMask", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilWriteMask = v };
            return Ok();
        }

        if (key.Equals("StencilFail", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStencilOperation(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilFail = v };
            return Ok();
        }

        if (key.Equals("StencilZFail", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStencilOperation(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilDepthBufferFail = v };
            return Ok();
        }

        if (key.Equals("StencilPass", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStencilOperation(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilPass = v };
            return Ok();
        }

        if (key.Equals("StencilFunc", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseCompareFunction(value, out var v))
                return UnknownValue(key, value);
            block = block with { StencilFunction = v };
            return Ok();
        }

        // ---- FNA-only states (fx_2_0 ops FNA honors; MGFX has no analog and its
        // writer never reads these fields — see RenderStateBlock).

        if (key.Equals("SeparateAlphaBlendEnable", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { SeparateAlphaBlendEnable = v };
            return Ok();
        }

        if (key.Equals("BlendFactor", StringComparison.OrdinalIgnoreCase))
        {
            // A D3DCOLOR dword: hex (BlendFactor = 0x80FF8080) or decimal.
            if (!TryParseDword(value, out var v))
                return UnknownValue(key, value);
            block = block with { BlendFactor = v };
            return Ok();
        }

        if (key.Equals("MultiSampleMask", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDword(value, out var v))
                return UnknownValue(key, value);
            block = block with { MultiSampleMask = v };
            return Ok();
        }

        if (key.Equals("TwoSidedStencilMode", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBool(value, out var v))
                return UnknownValue(key, value);
            block = block with { TwoSidedStencilMode = v };
            return Ok();
        }

        if (key.Equals("CCW_StencilFail", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStencilOperation(value, out var v))
                return UnknownValue(key, value);
            block = block with { CounterClockwiseStencilFail = v };
            return Ok();
        }

        if (key.Equals("CCW_StencilZFail", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStencilOperation(value, out var v))
                return UnknownValue(key, value);
            block = block with { CounterClockwiseStencilDepthBufferFail = v };
            return Ok();
        }

        if (key.Equals("CCW_StencilPass", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseStencilOperation(value, out var v))
                return UnknownValue(key, value);
            block = block with { CounterClockwiseStencilPass = v };
            return Ok();
        }

        if (key.Equals("CCW_StencilFunc", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseCompareFunction(value, out var v))
                return UnknownValue(key, value);
            block = block with { CounterClockwiseStencilFunction = v };
            return Ok();
        }

        if (key.Equals("ColorWriteEnable1", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseColorWriteMask(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorWriteChannels1 = v };
            return Ok();
        }

        if (key.Equals("ColorWriteEnable2", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseColorWriteMask(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorWriteChannels2 = v };
            return Ok();
        }

        if (key.Equals("ColorWriteEnable3", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseColorWriteMask(value, out var v))
                return UnknownValue(key, value);
            block = block with { ColorWriteChannels3 = v };
            return Ok();
        }

        // Unknown keys are silently ignored per spec
        return Ok();
    }

    private static bool TryParseBool(string value, out bool result)
    {
        if (value.Equals("True", StringComparison.OrdinalIgnoreCase))  { result = true;  return true; }
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase)) { result = false; return true; }
        result = default;
        return false;
    }

    private static bool TryParseCullMode(string value, out CullModeValue result)
    {
        result = value switch
        {
            _ when value.Equals("None", StringComparison.OrdinalIgnoreCase) => CullModeValue.None,
            _ when value.Equals("CW",   StringComparison.OrdinalIgnoreCase) => CullModeValue.CullClockwiseFace,
            _ when value.Equals("CCW",  StringComparison.OrdinalIgnoreCase) => CullModeValue.CullCounterClockwiseFace,
            _ => (CullModeValue)(-1),
        };
        return (int)result != -1;
    }

    private static bool TryParseFillMode(string value, out FillModeValue result)
    {
        result = value switch
        {
            _ when value.Equals("Solid",     StringComparison.OrdinalIgnoreCase) => FillModeValue.Solid,
            _ when value.Equals("Wireframe", StringComparison.OrdinalIgnoreCase) => FillModeValue.WireFrame,
            _ => (FillModeValue)(-1),
        };
        return (int)result != -1;
    }

    private static bool TryParseBlend(string value, out BlendValue result)
    {
        result = value switch
        {
            _ when value.Equals("Zero",           StringComparison.OrdinalIgnoreCase) => BlendValue.Zero,
            _ when value.Equals("One",            StringComparison.OrdinalIgnoreCase) => BlendValue.One,
            _ when value.Equals("SrcColor",       StringComparison.OrdinalIgnoreCase) => BlendValue.SourceColor,
            _ when value.Equals("InvSrcColor",    StringComparison.OrdinalIgnoreCase) => BlendValue.InverseSourceColor,
            _ when value.Equals("SrcAlpha",       StringComparison.OrdinalIgnoreCase) => BlendValue.SourceAlpha,
            _ when value.Equals("InvSrcAlpha",    StringComparison.OrdinalIgnoreCase) => BlendValue.InverseSourceAlpha,
            _ when value.Equals("DestAlpha",      StringComparison.OrdinalIgnoreCase) => BlendValue.DestinationAlpha,
            _ when value.Equals("InvDestAlpha",   StringComparison.OrdinalIgnoreCase) => BlendValue.InverseDestinationAlpha,
            _ when value.Equals("DestColor",      StringComparison.OrdinalIgnoreCase) => BlendValue.DestinationColor,
            _ when value.Equals("InvDestColor",   StringComparison.OrdinalIgnoreCase) => BlendValue.InverseDestinationColor,
            _ when value.Equals("SrcAlphaSat",    StringComparison.OrdinalIgnoreCase) => BlendValue.SourceAlphaSaturation,
            _ when value.Equals("BlendFactor",    StringComparison.OrdinalIgnoreCase) => BlendValue.BlendFactor,
            _ when value.Equals("InvBlendFactor", StringComparison.OrdinalIgnoreCase) => BlendValue.InverseBlendFactor,
            _ => (BlendValue)(-1),
        };
        return (int)result != -1;
    }

    private static bool TryParseBlendFunction(string value, out BlendFunctionValue result)
    {
        result = value switch
        {
            _ when value.Equals("Add",          StringComparison.OrdinalIgnoreCase) => BlendFunctionValue.Add,
            _ when value.Equals("Subtract",     StringComparison.OrdinalIgnoreCase) => BlendFunctionValue.Subtract,
            _ when value.Equals("RevSubtract",  StringComparison.OrdinalIgnoreCase) => BlendFunctionValue.ReverseSubtract,
            _ when value.Equals("Min",          StringComparison.OrdinalIgnoreCase) => BlendFunctionValue.Min,
            _ when value.Equals("Max",          StringComparison.OrdinalIgnoreCase) => BlendFunctionValue.Max,
            _ => (BlendFunctionValue)(-1),
        };
        return (int)result != -1;
    }

    private static bool TryParseCompareFunction(string value, out CompareFunctionValue result)
    {
        result = value switch
        {
            _ when value.Equals("Never",        StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.Never,
            _ when value.Equals("Less",         StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.Less,
            _ when value.Equals("Equal",        StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.Equal,
            _ when value.Equals("LessEqual",    StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.LessEqual,
            _ when value.Equals("Greater",      StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.Greater,
            _ when value.Equals("NotEqual",     StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.NotEqual,
            _ when value.Equals("GreaterEqual", StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.GreaterEqual,
            _ when value.Equals("Always",       StringComparison.OrdinalIgnoreCase) => CompareFunctionValue.Always,
            _ => (CompareFunctionValue)(-1),
        };
        return (int)result != -1;
    }

    private static bool TryParseStencilOperation(string value, out StencilOperationValue result)
    {
        result = value switch
        {
            _ when value.Equals("Keep",    StringComparison.OrdinalIgnoreCase) => StencilOperationValue.Keep,
            _ when value.Equals("Zero",    StringComparison.OrdinalIgnoreCase) => StencilOperationValue.Zero,
            _ when value.Equals("Replace", StringComparison.OrdinalIgnoreCase) => StencilOperationValue.Replace,
            _ when value.Equals("Incr",    StringComparison.OrdinalIgnoreCase) => StencilOperationValue.Increment,
            _ when value.Equals("Decr",    StringComparison.OrdinalIgnoreCase) => StencilOperationValue.Decrement,
            _ when value.Equals("IncrSat", StringComparison.OrdinalIgnoreCase) => StencilOperationValue.IncrementSaturation,
            _ when value.Equals("DecrSat", StringComparison.OrdinalIgnoreCase) => StencilOperationValue.DecrementSaturation,
            _ when value.Equals("Invert",  StringComparison.OrdinalIgnoreCase) => StencilOperationValue.Invert,
            _ => (StencilOperationValue)(-1),
        };
        return (int)result != -1;
    }

    /// <summary>A raw dword: hex with a 0x prefix (e.g. <c>0x80FF8080</c>) or decimal.</summary>
    private static bool TryParseDword(string value, out uint result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out result);
        return uint.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// A D3DCOLORWRITEENABLE mask: an OR of RED/GREEN/BLUE/ALPHA flag tokens
    /// (e.g. <c>RED | GREEN</c>) and/or integer literals (decimal or 0x hex).
    /// The D3D9 flag bits are identical to XNA's ColorWriteChannels bits.
    /// </summary>
    private static bool TryParseColorWriteMask(string value, out int result)
    {
        result = 0;
        foreach (string part in value.Split('|'))
        {
            string token = part.Trim();
            if (token.Equals("Red", StringComparison.OrdinalIgnoreCase))        result |= 1;
            else if (token.Equals("Green", StringComparison.OrdinalIgnoreCase)) result |= 2;
            else if (token.Equals("Blue", StringComparison.OrdinalIgnoreCase))  result |= 4;
            else if (token.Equals("Alpha", StringComparison.OrdinalIgnoreCase)) result |= 8;
            else if (TryParseDword(token, out uint bits))                       result |= unchecked((int)bits);
            else { result = 0; return false; }
        }
        return true;
    }

    // SD0011 — unique to this condition (SD0010 is the pipeline's "no techniques" error;
    // the two previously collided). Registered in docs/error-codes.md.
    private static Result<bool, ShaderError> UnknownValue(string key, string value)
        => Result<bool, ShaderError>.Fail(new ShaderError(
            File: "",
            Line: 0,
            Column: 0,
            Code: "SD0011",
            Message: $"Unrecognised value '{Sanitize(value)}' for render state key '{key}'"));

    private static string Sanitize(string s)
    {
        var safe = string.Concat(s.Where(c => !char.IsControl(c)));
        return safe.Length <= 80 ? safe : string.Concat(safe.AsSpan(0, 80), "…");
    }

    private static Result<bool, ShaderError> Ok()
        => Result<bool, ShaderError>.Ok(true);
}
