#nullable enable

using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>Pure unit tests for <see cref="SlangcRegisterStripper"/> (issue #252).</summary>
public sealed class SlangcRegisterStripperTests
{
    private const string SlangcEmission = """
        Texture2D<float4 > SpriteTexture : register(t0);
        SamplerState SpriteSampler : register(s0);
        TextureCube<float4 > Env : register(t1, space0);
        SamplerComparisonState Shadow : register(s1);
        Texture2D<float4 > Arr[2] : register(t2);
        cbuffer Params : register(b0)
        {
            float Levels;
        }
        """;

    [Fact]
    public void Strip_RemovesEverySlangcNumberedTextureAndSamplerRegister()
    {
        string stripped = SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string>());

        stripped.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
        stripped.ShouldContain("SamplerState SpriteSampler;", Case.Sensitive);
        stripped.ShouldContain("TextureCube<float4 > Env;", Case.Sensitive);
        stripped.ShouldContain("SamplerComparisonState Shadow;", Case.Sensitive);
        stripped.ShouldContain("Texture2D<float4 > Arr[2];", Case.Sensitive);
        stripped.ShouldNotContain("register(t", Case.Sensitive);
        stripped.ShouldNotContain("register(s", Case.Sensitive);
    }

    [Fact]
    public void Strip_LeavesConstantBufferRegistersAlone()
    {
        SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string>())
            .ShouldContain("cbuffer Params : register(b0)", Case.Sensitive);
    }

    [Fact]
    public void Strip_KeepsTheRegisterOfAnAuthorBoundName()
    {
        string stripped = SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string> { "SpriteSampler" });

        stripped.ShouldContain("SamplerState SpriteSampler : register(s0);", Case.Sensitive);
        stripped.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
    }

    [Fact]
    public void AuthorBoundNames_FindsRegisterAnnotations_IncludingArrays()
    {
        const string source = """
            Texture2D Mask : register(t1);
            SamplerState MaskSampler: register( s1 );
            Texture2D Layers[4] : register(t4);
            Texture2D Free;
            """;

        var names = SlangcRegisterStripper.AuthorBoundNames(source);

        names.ShouldBe(new[] { "Mask", "MaskSampler", "Layers" }, ignoreOrder: true);
    }

    [Fact]
    public void AuthorBoundNames_IgnoresCommentsAndVkBinding()
    {
        // A register in a comment is not one; vk::binding is not an HLSL register and slangc's
        // HLSL drops it (measured), so it reserves nothing on the .fx route either.
        const string source = """
            // SamplerState Old : register(s0);
            /* Texture2D Gone : register(t0); */
            [[vk::binding(3)]] SamplerState S;
            """;

        SlangcRegisterStripper.AuthorBoundNames(source).ShouldBeEmpty();
    }

    // ---- Issue #252 follow-up: the names come from slangc's preprocess-only (-E) output,
    // which is a one-line token stream. The strings below are that output, verbatim, for the
    // sources named in each test (slangc v2026.14.1).

    [Fact]
    public void AuthorBoundNames_ReadsSlangcsPreprocessedTokenStream()
    {
        // '#define SLOT(n) : register(n)' + 'Texture2D Mask SLOT(t1); SamplerState MaskSampler SLOT(s1);'
        const string preprocessed =
            "Texture2D Mask : register ( t1 ) ; SamplerState MaskSampler : register ( s1 ) ; " +
            "Texture2D Layers [ 4 ] : register ( t4 ) ; Texture2D Free ; " +
            "[ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return Mask . Sample ( MaskSampler , uv ) ; } \n";

        SlangcRegisterStripper.AuthorBoundNames(preprocessed)
            .ShouldBe(new[] { "Mask", "MaskSampler", "Layers" }, ignoreOrder: true);
    }

    [Fact]
    public void AuthorBoundNames_InactiveBranchRegister_IsAbsentFromThePreprocessedText()
    {
        // '#if OPENGL / SamplerState SpriteSampler; / #else / SamplerState SpriteSampler : register(s0); / #endif'
        // preprocessed with -DOPENGL=1: the register never reaches the compile, so it is not bound.
        const string openGl =
            "Texture2D SpriteTexture ; SamplerState SpriteSampler ; [ shader ( \"fragment\" ) ] float4 MainPS ( ) : SV_Target { return 0 ; } \n";
        // The same source preprocessed for DirectX (OPENGL undefined): the #else branch is live.
        const string directX =
            "Texture2D SpriteTexture ; SamplerState SpriteSampler : register ( s0 ) ; [ shader ( \"fragment\" ) ] float4 MainPS ( ) : SV_Target { return 0 ; } \n";

        SlangcRegisterStripper.AuthorBoundNames(openGl).ShouldBeEmpty();
        SlangcRegisterStripper.AuthorBoundNames(directX).ShouldBe(new[] { "SpriteSampler" });
    }

    [Fact]
    public void AuthorBoundNames_IgnoresARegisterSpelledInsideAStringLiteral()
    {
        const string preprocessed = "[ shader ( \"Fake : register(s0)\" ) ] SamplerState Real : register ( s2 ) ; \n";

        SlangcRegisterStripper.AuthorBoundNames(preprocessed).ShouldBe(new[] { "Real" });
    }

    [Theory]
    [InlineData("Texture2D T;\nSamplerState S;\n", false)]
    [InlineData("#if OPENGL\nSamplerState S;\n#endif\n#define TWO 2\n", false)]
    // slangc's 'register' is case-sensitive (REGISTER(t3) is a syntax error, measured).
    [InlineData("Texture2D T : REGISTER(t3);\n", false)]
    [InlineData("Texture2D T : register(t3);\n", true)]
    [InlineData("// a comment that says register\nTexture2D T;\n", true)]
    [InlineData("#include \"Slots.fxh\"\nTexture2D T SLOT;\n", true)]
    [InlineData("#define R(k) : regi##ster(k)\nTexture2D T R(t3);\n", true)]
    // slangc splices a backslash-newline inside an identifier (measured: this binds t3).
    [InlineData("Texture2D T : regis\\\nter(t3);\n", true)]
    public void MayWriteRegister_IsFalseOnlyWhenNoRegisterTokenCanExist(string source, bool expected) =>
        SlangcRegisterStripper.MayWriteRegister(source, []).ShouldBe(expected);

    [Theory]
    [InlineData("SLOT", ": register(t5)", true)]
    [InlineData("SLOT", "regi##ster", true)]
    [InlineData("QUALITY", "2", false)]
    public void MayWriteRegister_AlsoReadsUserDefines(string name, string value, bool expected) =>
        SlangcRegisterStripper.MayWriteRegister("Texture2D T SLOT;\n", [new UserDefine(name, value)]).ShouldBe(expected);

    [Theory]
    [InlineData("Texture1D<float4 > A")]
    [InlineData("Texture1DArray<float4 > A")]
    [InlineData("Texture2D<float4 > A")]
    [InlineData("Texture2DArray<float4 > A")]
    [InlineData("Texture2DMS<float4 > A")]
    [InlineData("Texture2DMSArray<float4 > A")]
    [InlineData("Texture3D<float4 > A")]
    [InlineData("TextureCube<float4 > A")]
    [InlineData("TextureCubeArray<float4 > A")]
    [InlineData("RWTexture2D<float4 > A")]
    [InlineData("Texture2D<float4 >  A[int(2)]")]
    [InlineData("SamplerState A")]
    [InlineData("SamplerComparisonState A")]
    public void Strip_CoversEveryRealResourceType(string declaration)
    {
        string register = declaration.StartsWith("Sampler", StringComparison.Ordinal) ? "s3" : "t3";

        SlangcRegisterStripper.Strip($"{declaration} : register({register});", new HashSet<string>())
            .ShouldBe($"{declaration};");
    }

    [Theory]
    // User types that merely START with a resource type's name are not resources.
    [InlineData("TextureRegion_0 gRegion : register(t0);")]
    [InlineData("Texture2DInfo_0 gInfo : register(t0);")]
    [InlineData("MyTexture2D gMine : register(t0);")]
    [InlineData("SamplerStateInfo_0 gInfo : register(s0);")]
    [InlineData("SamplerStates_0 gStates : register(s0);")]
    public void Strip_LeavesUserTypesNamedLikeAResourceAlone(string declaration) =>
        SlangcRegisterStripper.Strip(declaration, new HashSet<string>()).ShouldBe(declaration);
}
