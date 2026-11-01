#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// PURE unit tests for <see cref="DxcPackMatrixGuard"/> (issue #452). The out-of-process proof that
/// the refused shapes crash the pinned DXC, and that the refusal keeps them away from it under
/// parallel load, is <c>ShadowDusk.Integration.Tests.Dxc.DxcPackMatrixTests</c>.
/// </summary>
public sealed class DxcPackMatrixGuardTests
{
    private const string Code = "float4 PS(float4 c : COLOR0) : SV_Target { return c; }\n";

    private static ShaderError? Check(string source, params (string Name, string? Value)[] macros) =>
        DxcPackMatrixGuard.Check(source, "pack.fx", macros);

    [Theory]
    [InlineData("#pragma pack_matrix(bogus)\n", 1, 21)]
    [InlineData("#pragma pack_matrix(ROW_MAJOR)\n", 1, 21)]
    [InlineData("#pragma pack_matrix()\n", 1, 21)]
    [InlineData("#pragma pack_matrix(1)\n", 1, 21)]
    [InlineData("#pragma pack_matrix\n", 1, 20)]
    [InlineData("#pragma pack_matrix(row_major\n", 1, 21)]
    [InlineData("#pragma pack_matrix(row_major, column_major)\n", 1, 21)]
    [InlineData("#pragma pack_matrix row_major\n", 1, 21)]
    [InlineData("\n  #  pragma   pack_matrix ( bogus )\n", 2, 29)]
    [InlineData("#define L bogus\n#pragma pack_matrix(L)\n", 2, 21)]
    [InlineData("#pragma /* c */ pack_matrix(bogus)\n", 1, 29)]
    [InlineData("_Pragma(\"pack_matrix(bogus)\")\n", 1, 1)]
    public void AnArgumentDxcRejects_IsRefusedWithSD0241_AtTheArgument(string pragma, int line, int column)
    {
        ShaderError error = Check(pragma + Code).ShouldNotBeNull();

        error.Code.ShouldBe(DxcPackMatrixGuard.ErrorCode);
        (error.File, error.Line, error.Column).ShouldBe(("pack.fx", line, column));
        error.Message.ShouldContain("row_major", Case.Sensitive);
    }

    [Theory]
    [InlineData("#pragma pack_matrix(row_major)\n")]
    [InlineData("#pragma pack_matrix(column_major)\n")]
    [InlineData("#pragma pack_matrix ( row_major )\n")]
    [InlineData("#pragma pack_matrix(row_major) junk\n")]      // DXC compiles trailing tokens
    [InlineData("#define L row_major\n#pragma pack_matrix(L)\n")]
    [InlineData("#if 0\n#pragma pack_matrix(bogus)\n#endif\n")]
    [InlineData("// #pragma pack_matrix(bogus)\n")]
    [InlineData("#pragma warning(disable : 3571)\n")]
    [InlineData("_Pragma(\"pack_matrix(row_major)\")\n")]
    [InlineData("")]
    public void AnArgumentDxcAccepts_OrAnInactiveDirective_IsLeftToDxc(string pragma)
    {
        Check(pragma + Code).ShouldBeNull();
    }

    [Fact]
    public void TheCompilesOwnMacros_AreDefinedFirst()
    {
        const string source = "#pragma pack_matrix(LAYOUT)\n" + Code;

        Check(source, ("LAYOUT", "row_major")).ShouldBeNull();
        ShaderError error = Check(source, ("LAYOUT", "bogus")).ShouldNotBeNull();
        (error.Line, error.Column).ShouldBe((1, 21));
        Check("#ifdef SKIP\n#pragma pack_matrix(bogus)\n#endif\n" + Code, ("SKIP", null)).ShouldNotBeNull();
        Check("#ifdef SKIP\n#pragma pack_matrix(bogus)\n#endif\n" + Code).ShouldBeNull();
    }

    [Fact]
    public void ADirectiveInAnIncludedFile_IsLocatedThroughTheFlattenersLineDirectives()
    {
        const string flattened =
            "#line 1 \"main.fx\"\nfloat4 A;\n#line 1 \"layout.fxh\"\n\n#pragma pack_matrix(bogus)\n" + Code;

        ShaderError error = Check(flattened).ShouldNotBeNull();

        (error.File, error.Line).ShouldBe(("layout.fxh", 2));
    }
}
