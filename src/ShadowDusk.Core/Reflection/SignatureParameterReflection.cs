#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// One entry in a shader stage's input or output signature, describing a semantic-bound
/// attribute (its semantic, register, system-value role, component type, and write mask).
/// </summary>
public sealed record SignatureParameterReflection
{
    /// <summary>The HLSL semantic name (e.g. <c>POSITION</c>, <c>TEXCOORD</c>).</summary>
    public required string SemanticName  { get; init; }
    /// <summary>The semantic index (e.g. the <c>0</c> in <c>TEXCOORD0</c>).</summary>
    public required int    SemanticIndex { get; init; }
    /// <summary>The register the parameter is assigned to.</summary>
    public required int    Register      { get; init; }
    /// <summary>The system-value semantic role (e.g. <c>POS</c> for <c>SV_Position</c>), or empty.</summary>
    public required string SystemValue   { get; init; }
    /// <summary>The component data type (e.g. <c>float</c>, <c>uint</c>).</summary>
    public required string ComponentType { get; init; }
    /// <summary>The component write/read mask (bits for x/y/z/w).</summary>
    public required byte   Mask          { get; init; }
}
