#nullable enable

namespace ShadowDusk.HLSL;

/// <summary>Diagnostic codes emitted by the FX9 pre-parser.</summary>
public enum FxParseErrorCode
{
    /// <summary>FX0001: An unexpected token was encountered during parsing.</summary>
    UnexpectedToken = 1,

    /// <summary>FX0002: The source ended before the current construct was closed.</summary>
    UnexpectedEof = 2,

    /// <summary>FX0003: A compile() expression was malformed (unexpected tokens inside argument list).</summary>
    MalformedCompileExpression = 3,

    /// <summary>FX0004: The shader profile string is not a recognized profile name.</summary>
    UnrecognizedShaderProfile = 4,

    /// <summary>FX0005: Two or more techniques share the same name.</summary>
    DuplicateTechniqueName = 5,

    /// <summary>FX0006: Two or more passes within a technique share the same name.</summary>
    DuplicatePassName = 6,

    /// <summary>FX0007: An annotation block was opened but never closed.</summary>
    UnclosedAnnotationBlock = 7,

    /// <summary>FX0008: A required semicolon is missing after a statement.</summary>
    MissingSemicolon = 8,

    /// <summary>FX0009: A sampler_state block was opened but never closed.</summary>
    UnclosedSamplerBlock = 9,

    /// <summary>FX0010: A render-state key is not recognized (non-fatal warning).</summary>
    UnrecognizedRenderStateKey = 10,
}
