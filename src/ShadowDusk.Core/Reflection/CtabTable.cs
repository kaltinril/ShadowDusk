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
/// <param name="DefaultValue">The constant's default value when the CTAB carries one and
/// it is a scalar/vector (single register row); <see langword="null"/> otherwise. Matrix
/// defaults are intentionally not read — their majority in CTAB is unverified (see
/// <c>docs/fx2-binary-format.md</c> §15 F2) and a wrong-major default would silently
/// corrupt rendering.</param>
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
    IReadOnlyList<float>? DefaultValue);

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
