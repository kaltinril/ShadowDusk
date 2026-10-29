#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>
/// One MATRIX-typed declaration carrying a <c>TEXCOORD</c> semantic inside a struct or a parameter
/// list (an interpolant, e.g. <c>row_major float3x3 TangentToWorld : TEXCOORD2</c>), read off the
/// preprocessed source by <see cref="FxPreParser.CollectGlSamplerSlots(string, string, FxParseResult)"/>.
///
/// <para>The OpenGL target needs it because DXC emits byte-identical SPIR-V, and so GLSL, for a
/// <c>row_major</c> and a <c>column_major</c> matrix interpolant, while fxc (and so <c>mgfxc</c>)
/// packs them differently: one HLSL COLUMN per register for <c>column_major</c> (the default; a
/// <c>#pragma pack_matrix</c> does not change it for interpolants), one ROW per register for an
/// explicit <c>row_major</c>. The rewriter must reproduce that layout, or a stage that declares the
/// interpolant as separate vectors reads the wrong data. A <c>float1xN</c> (column_major) or
/// <c>row_major floatNx1</c> spreads over N registers in fxc but reaches the rewriter as a plain
/// vector, so it is recorded too, to be refused.</para>
/// </summary>
/// <param name="Semantic">The semantic, uppercased, index included (<c>TEXCOORD</c> is <c>TEXCOORD0</c>).</param>
/// <param name="Name">The declared member or parameter name.</param>
/// <param name="Rows">HLSL rows (<c>float4x3</c> is 4 rows of 3 columns).</param>
/// <param name="Columns">HLSL columns.</param>
/// <param name="RowMajor">The declaration (or the typedef it uses) says <c>row_major</c>.</param>
public sealed record MatrixInterpolantDeclaration(
    string Semantic, string Name, int Rows, int Columns, bool RowMajor)
{
    /// <summary>
    /// The shapes fxc spreads over more registers than the vector the rewriter is handed:
    /// a column_major <c>float1xN</c> (N registers of one component) and a row_major
    /// <c>floatNx1</c>.
    /// </summary>
    public bool IsDegenerate => RowMajor ? (Columns == 1 && Rows > 1) : (Rows == 1 && Columns > 1);
}
