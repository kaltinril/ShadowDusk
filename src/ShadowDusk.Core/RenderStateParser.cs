#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace ShadowDusk.Core;

public sealed class RenderStateParser
{
    public Result<RenderStateBlock, ShaderError> Parse(IReadOnlyDictionary<string, string> kvp)
    {
        var block = new RenderStateBlock();

        foreach (var (rawKey, rawValue) in kvp)
        {
            var key = rawKey.Trim();
            var value = rawValue.Trim();

            var result = ApplyKey(ref block, key, value);
            if (result.IsFailure)
                return Result<RenderStateBlock, ShaderError>.Fail(result.Error);
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

        if (key.Equals("ZEnable", StringComparison.OrdinalIgnoreCase))
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

    private static Result<bool, ShaderError> UnknownValue(string key, string value)
        => Result<bool, ShaderError>.Fail(new ShaderError(
            File: "",
            Line: 0,
            Column: 0,
            Code: "SD0010",
            Message: $"Unrecognised value '{Sanitize(value)}' for render state key '{key}'"));

    private static string Sanitize(string s)
    {
        var safe = string.Concat(s.Where(c => !char.IsControl(c)));
        return safe.Length <= 80 ? safe : string.Concat(safe.AsSpan(0, 80), "…");
    }

    private static Result<bool, ShaderError> Ok()
        => Result<bool, ShaderError>.Ok(true);
}
