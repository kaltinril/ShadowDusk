#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Pure tests for <see cref="DxcTimestampMacro"/> (issue #444): the pinned DXC crashes the process
/// expanding <c>__TIMESTAMP__</c>, even preprocessing only, so a DXC input that names it as a
/// token is refused (SD0238) before DXC runs; a comment or a string that mentions it is not.
/// </summary>
public sealed class DxcTimestampMacroTests
{
    [Theory]
    [InlineData("static const int x = __TIMESTAMP__;\n", 1, 22)]
    [InlineData("#if 0\nstatic const int x = __TIMESTAMP__;\n#endif\n", 2, 22)]   // a dead branch: whether DXC expands it is DXC's crash to answer
    [InlineData("#define STAMP __TIMESTAMP__\n", 1, 15)]                          // a macro body
    [InlineData("float a; // x\n/* y */ float b = __TIMESTAMP__;\n", 2, 19)]
    public void ATimestampToken_IsRefused_AtItsPosition(string source, int line, int column)
    {
        ShaderError? error = DxcTimestampMacro.Refusal(source, "t.fx");

        error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0238");
        error.File.ShouldBe("t.fx");
        error.Line.ShouldBe(line);
        error.Column.ShouldBe(column);
        error.Message.ShouldContain("issue #444", Case.Sensitive);
    }

    [Theory]
    [InlineData("// __TIMESTAMP__\nfloat4 a;\n")]
    [InlineData("/* __TIMESTAMP__ */ float4 a;\n")]
    [InlineData("float4 a < string Doc = \"__TIMESTAMP__\"; >;\n")]
    [InlineData("float4 __TIMESTAMP__X;\nfloat4 X__TIMESTAMP__;\n")]       // other identifiers
    [InlineData("float4 PS() : SV_Target { return 1; }\n")]
    public void ACommentAStringOrAnotherName_IsNotRefused(string source)
    {
        DxcTimestampMacro.Refusal(source, "t.fx").ShouldBeNull();
    }

    [Fact]
    public void TheRefusalIsReportedAtTheAuthorsLine_ThroughTheFlattenersLineDirectives()
    {
        string source = "#define OPENGL 1\n#line 1 \"effect.fx\"\nfloat a;\nstatic const int x = __TIMESTAMP__;\n";

        ShaderError? error = DxcTimestampMacro.Refusal(source, "t.fx");

        error.ShouldNotBeNull();
        error.File.ShouldBe("effect.fx");
        error.Line.ShouldBe(2);
    }
}
