#nullable enable

using ShadowDusk.Compiler.Slang;
using ShadowDusk.Compiler.Sksl;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Generated text is compiler input, so it must be identical on every host: AppendLine and
/// Environment.NewLine emit CRLF on Windows and LF elsewhere.
/// </summary>
public sealed class GeneratedTextLineEndingTests
{
    [Fact]
    public void SlangFrontend_FxText_HasNoCarriageReturn()
    {
        string slang = string.Join("\n",
            "[shader(\"vertex\")]",
            "float4 VS(float4 p : POSITION) : SV_Position { return p; }",
            "[shader(\"fragment\")]",
            "float4 PS() : SV_Target { return 1; }");

        var result = SlangFrontend.ConvertToFx(slang, new SlangConvertOptions { SourceName = "t.slang" });

        result.IsSuccess.ShouldBeTrue();
        result.Value.FxText.ShouldNotContain("\r", Case.Sensitive);
    }

    [Fact]
    public void SkslGlslMapper_UniformBlockRewrite_HasNoCarriageReturn()
    {
        string glsl = string.Join("\n",
            "#version 450",
            "uniform type_Globals",
            "{",
            "    float a;",
            "    vec2 b;",
            "} _Globals;",
            "layout(location = 0) out vec4 outColor;",
            "void main() { outColor = vec4(_Globals.a, _Globals.b, 0.0); }");

        var result = SkslGlslMapper.Map(glsl, [], new HashSet<string>(), "t.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.SkslText.ShouldContain("uniform float a;", Case.Sensitive);
        result.Value.SkslText.ShouldNotContain("\r", Case.Sensitive);
    }
}
