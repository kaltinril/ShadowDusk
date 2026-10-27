#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// GitHub issue #407 for the raylib and SkSL converters, which share the OpenGL route's
/// technique resolution: techniques come from the preprocessed translation unit, so a technique
/// in an inactive <c>#if</c> does not count against the single-technique rule, one in an
/// <c>#include</c>d file is found, and a <c>compile</c> target written through a macro behaves
/// like the profile it expands to. The bar (no reference compiler exists for either) is that each
/// shape converts to EXACTLY what the directly written single technique converts to.
/// </summary>
public sealed class Issue407ConverterTechniqueTests
{
    private const string Functions = """
        float4 P(float4 pos : SV_POSITION, float4 color : COLOR0) : COLOR0 { return color; }
        float4 Q(float4 pos : SV_POSITION, float4 color : COLOR0) : COLOR0 { return color * 0.5; }

        """;

    private const string Direct = Functions + "technique B { pass { PixelShader = compile ps_3_0 Q(); } }\n";

    private const string IfZero = Functions + """
        #if 0
        technique A { pass { PixelShader = compile ps_3_0 P(); } }
        #endif
        technique B { pass { PixelShader = compile ps_3_0 Q(); } }
        """;

    private const string IfTechnique = Functions + """
        #if TECHNIQUE == 1
        technique A { pass { PixelShader = compile ps_3_0 P(); } }
        #else
        technique B { pass { PixelShader = compile ps_3_0 Q(); } }
        #endif
        """;

    private const string MacroTarget = "#define PS_SM ps_3_0\n" + Functions +
        "technique B { pass { PixelShader = compile PS_SM Q(); } }\n";

    private const string IncludedTechnique = "technique B { pass { PixelShader = compile ps_3_0 Q(); } }\n";

    private static IIncludeResolver Include() =>
        new InMemoryIncludeResolver(new Dictionary<string, string> { ["tech.fxh"] = IncludedTechnique });

    private static RaylibShader Raylib(string fx)
    {
        var result = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = "Issue407.fx", IncludeResolver = Include() });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value;
    }

    private static SkslConversion Sksl(string fx)
    {
        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "Issue407.fx", IncludeResolver = Include() });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value;
    }

    public static TheoryData<string> Shapes() => new()
    {
        IfZero,
        // No TECHNIQUE define reaches the converters, so the #else arm (B) is the one that exists.
        IfTechnique,
        MacroTarget,
        Functions + "#include \"tech.fxh\"\n",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Raylib_PreprocessedSingleTechnique_ConvertsLikeTheDirectForm(string fx)
    {
        Raylib(fx).FragmentShader.ShouldBe(Raylib(Direct).FragmentShader);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Sksl_PreprocessedSingleTechnique_ConvertsLikeTheDirectForm(string fx)
    {
        Sksl(fx).SkslText.ShouldBe(Sksl(Direct).SkslText);
    }
}
