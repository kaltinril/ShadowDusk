#nullable enable

namespace ShadowDusk.Core;

/// <summary>A small wrapper around a GLSL source string produced by the transpiler.</summary>
/// <param name="Text">The GLSL source text.</param>
public readonly record struct GlslSource(string Text)
{
    /// <summary>Returns the GLSL source text.</summary>
    public override string ToString() => Text;
}
