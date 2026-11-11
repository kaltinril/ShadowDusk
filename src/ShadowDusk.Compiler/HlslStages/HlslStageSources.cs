#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Compiler;

/// <summary>
/// Two separate HLSL sources, one vertex shader and one pixel shader, to be compiled into one
/// effect by <see cref="HlslStageCompiler"/>. Each source is an ordinary HLSL file: its
/// declarations (structs, constant buffers, textures, samplers, globals, helper functions) plus
/// the entry-point function of its stage. No <c>technique</c> or <c>pass</c> block is needed (or
/// allowed): the frontend writes it.
/// </summary>
/// <remarks>
/// <para>Each source must be self-contained, as if it were compiled alone: the merge does not
/// check that a function in one source uses a declaration that only the other source makes.</para>
/// <para>Declarations that both sources make are written once when they are textually identical
/// (ignoring whitespace and comments) and rejected when they differ.</para>
/// </remarks>
/// <param name="VertexSource">The HLSL text of the vertex shader.</param>
/// <param name="PixelSource">The HLSL text of the pixel (fragment) shader.</param>
/// <param name="VertexName">
/// The logical file name of the vertex source. It appears in diagnostics (error locations point
/// at it), never on disk.
/// </param>
/// <param name="PixelName">The logical file name of the pixel source, used the same way.</param>
/// <param name="VertexEntry">The name of the vertex entry-point function. Defaults to <c>main</c>.</param>
/// <param name="PixelEntry">The name of the pixel entry-point function. Defaults to <c>main</c>.</param>
/// <param name="TechniqueName">The name of the synthesized <c>technique</c>.</param>
public sealed record HlslStageSources(
    string VertexSource,
    string PixelSource,
    string VertexName = "vertex.hlsl",
    string PixelName = "pixel.hlsl",
    string VertexEntry = "main",
    string PixelEntry = "main",
    string TechniqueName = "HlslStages");

/// <summary>The successful product of <see cref="HlslStageFrontend.ConvertToFx"/>.</summary>
/// <param name="FxText">
/// The generated <c>.fx</c> effect source: the merged declarations, with a <c>#line</c> marker
/// before each so a compile error points at the original file and line, then a one-pass
/// technique naming the two entry points.
/// </param>
/// <param name="VertexEntry">The vertex entry-point name in <paramref name="FxText"/> (renamed when both were <c>main</c>).</param>
/// <param name="PixelEntry">The pixel entry-point name in <paramref name="FxText"/>.</param>
/// <param name="Warnings">Non-fatal findings. Empty today; the slot exists for parity with the other frontends.</param>
public sealed record HlslStageFxConversion(
    string FxText,
    string VertexEntry,
    string PixelEntry,
    IReadOnlyList<ShaderError> Warnings);
