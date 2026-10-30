#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The D3D9 register file a CTAB constant is bound to
/// (<c>D3DXREGISTER_SET</c>; the values are the on-disk u16).
/// </summary>
public enum CtabRegisterSet
{
    /// <summary>Boolean registers (<c>b#</c>).</summary>
    Bool = 0,
    /// <summary>Integer registers (<c>i#</c>).</summary>
    Int4 = 1,
    /// <summary>Float registers (<c>c#</c>) — the common case for uniforms.</summary>
    Float4 = 2,
    /// <summary>Sampler registers (<c>s#</c>).</summary>
    Sampler = 3,
}

/// <summary>
/// One constant from a D3D9 shader's CTAB constant table: the bridge between an HLSL
/// global and the register(s) the SM1–3 bytecode reads it from. <see cref="Class"/>,
/// <see cref="Type"/> carry the raw <c>D3DXPARAMETER_CLASS</c>/<c>D3DXPARAMETER_TYPE</c>
/// values — numerically identical to MojoShader's symbol class/type and to the fx_2_0
/// typedef encoding (see <c>docs/fx2-binary-format.md</c> §6.1).
/// </summary>
/// <param name="Name">The constant's name — in fx_2_0 effects this MUST match the effect
/// parameter name exactly (MojoShader binds by <c>strcmp</c>).</param>
/// <param name="RegisterSet">Which register file the constant occupies.</param>
/// <param name="RegisterIndex">First register.</param>
/// <param name="RegisterCount">Number of registers occupied.</param>
/// <param name="Class">Raw <c>D3DXPARAMETER_CLASS</c> (0=scalar, 1=vector, 2=matrix-rows,
/// 3=matrix-cols, 4=object, 5=struct).</param>
/// <param name="Type">Raw <c>D3DXPARAMETER_TYPE</c> (1=bool, 2=int, 3=float, 10–14=sampler…).</param>
/// <param name="Rows">Logical row count (CTAB stores rows-then-columns, the documented
/// D3D order — unlike the fx_2_0 typedef quirk).</param>
/// <param name="Columns">Logical column count.</param>
/// <param name="Elements">Array element count (0 or 1 for non-arrays as written by the
/// compiler; normalized to ≥ 1 here).</param>
/// <param name="DefaultValue">The constant's default value as the RAW REGISTER IMAGE
/// (<see cref="RegisterCount"/> x 4 floats, one float4 per register, exactly as the CTAB
/// stores it) when the CTAB carries one; <see langword="null"/> otherwise. This holds for
/// every class: scalars and vectors (one register per array element), matrices (one
/// register per column for MATRIX_COLUMNS, per row for MATRIX_ROWS), and arrays of all of
/// those. Use <see cref="LogicalDefaultValue"/> for the declaration-order values. fxc
/// and vkd3d trim the image to the registers the shader actually reads, so it can be
/// shorter than the full constant.</param>
public sealed record CtabConstant(
    string Name,
    CtabRegisterSet RegisterSet,
    int RegisterIndex,
    int RegisterCount,
    int Class,
    int Type,
    int Rows,
    int Columns,
    int Elements,
    IReadOnlyList<float>? DefaultValue)
{
    // fx_2_0 value blobs are bounded at 65536 dwords by Fx2EffectWriter; stay in step so a
    // hostile Elements*Rows*Columns product cannot drive a huge allocation here.
    private const long MaxLogicalDefaultFloats = 65536;

    /// <summary>
    /// The default value as an fx_2_0 effect-parameter value blob wants it: dense,
    /// declaration order, <c>Max(1, Elements) x Rows x Columns</c> floats, each array
    /// element and each matrix row one after another with no register padding. This is
    /// what fxc writes (measured against <c>D3DCompile fx_2_0</c> for float2x2, 3x3, 4x4,
    /// non-square, row_major, and arrays of scalars, vectors and matrices): a matrix is
    /// the logical row-major matrix even though the CTAB register image of a default
    /// (MATRIX_COLUMNS) matrix is its transpose.
    /// Components whose register fell outside the (trimmed) image are 0: the shader
    /// cannot read them, so nothing observable is lost beyond a getter on the unused
    /// tail. <see langword="null"/> when there is no default, the class is not numeric,
    /// or the shape is implausibly large.
    /// </summary>
    public IReadOnlyList<float>? LogicalDefaultValue()
    {
        if (DefaultValue is null || Class > 3 || Rows <= 0 || Columns <= 0)
            return null;

        int elements = Math.Max(1, Elements);
        long total = (long)Rows * Columns * elements;
        if (total > MaxLogicalDefaultFloats)
            return null;

        bool matrix = Class >= 2;
        bool columnMajor = Class == 3; // MATRIX_COLUMNS: one register per column

        var result = new float[total];
        int o = 0;
        for (int e = 0; e < elements; e++)
        {
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    int register;
                    int lane;
                    if (matrix && columnMajor)
                    {
                        register = e * Columns + c;
                        lane = r;
                    }
                    else
                    {
                        // Vectors, scalars and MATRIX_ROWS: one register per logical row.
                        register = e * Rows + r;
                        lane = c;
                    }

                    int index = register * 4 + lane;
                    result[o++] = lane < 4 && index < DefaultValue.Count ? DefaultValue[index] : 0f;
                }
            }
        }

        return result;
    }
}

/// <summary>
/// A parsed D3D9 CTAB constant table — the SM1–3 analog of DXIL/SPIR-V reflection. It is
/// what MojoShader itself uses at load time to bind effect parameters to shader registers,
/// which makes it the faithful reflection source for the FNA fx_2_0 path.
/// </summary>
/// <param name="VersionToken">The shader's version token (e.g. <c>0xFFFF0300</c> for ps_3_0),
/// echoed inside the CTAB header.</param>
/// <param name="Creator">The compiler's creator string.</param>
/// <param name="TargetProfile">The target profile string (e.g. <c>"ps_2_0"</c>).</param>
/// <param name="Constants">The constants, in CTAB order.</param>
public sealed record CtabTable(
    uint VersionToken,
    string Creator,
    string TargetProfile,
    IReadOnlyList<CtabConstant> Constants);
