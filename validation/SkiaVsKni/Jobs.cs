#nullable enable

namespace ShadowDusk.Validation.SkiaVsKni;

/// <summary>One KNI render: ShadowDusk's OpenGL <c>.mgfx</c>, drawn through SpriteBatch with <paramref name="Tint"/> as the vertex color.</summary>
/// <param name="Name">Output stem, <c>{shader}-{tint}</c>.</param>
/// <param name="Mgfx">The MGFX v10 bytes.</param>
/// <param name="Tint">RGBA8 SpriteBatch color (COLOR0).</param>
/// <param name="Uniforms">Free uniforms set by name (1-4 floats); absent parameters are skipped.</param>
internal sealed record KniJob(string Name, byte[] Mgfx, byte[] Tint, IReadOnlyDictionary<string, float[]> Uniforms);

/// <summary>One Skia render: the converter's SkSL as an <c>SKRuntimeEffect</c> shader.</summary>
/// <param name="Name">Output stem.</param>
/// <param name="Sksl">The runtime-effect source.</param>
/// <param name="Children">Child shader names, each the HLSL texture it binds.</param>
/// <param name="Tint">RGBA8 tint written to <c>ShadowDusk_Color</c> as <c>tint / 255</c>.</param>
/// <param name="Uniforms">Free uniforms set by name.</param>
/// <param name="Resolution">The value of <c>ShadowDusk_Resolution</c> (the child size in pixels).</param>
internal sealed record SkiaJob(
    string Name,
    string Sksl,
    IReadOnlyList<string> Children,
    byte[] Tint,
    IReadOnlyDictionary<string, float[]> Uniforms,
    float[] Resolution);
