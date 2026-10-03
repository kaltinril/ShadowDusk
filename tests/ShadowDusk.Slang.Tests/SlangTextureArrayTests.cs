#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #324 on the real-slangc route: a combined-sampler ARRAY, <c>Sampler2D Comb[2]</c>, which
/// slangc emits as a texture array plus a sampler array (<c>Texture2D Comb_texture_0[int(2)]</c>,
/// renamed to the author's <c>Comb</c> by issue #302, and <c>SamplerState Comb_sampler_0[int(2)]</c>).
/// The <c>.fx</c> route's reference (mgfxc 3.8.5, measured) reflects a texture array on
/// DirectX_12 as one parameter bound to slot 0 and on Vulkan as nothing at all, so this route
/// must give the same table on DirectX_12 and the same loud <c>SD0221</c> on Vulkan, located at
/// the author's Slang declaration rather than at slangc's core module.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangTextureArrayTests
{
    private const string ArraySlang =
        "// a combined-sampler array\n" +
        "Sampler2D Comb[2];\n" +                                   // line 2, name at column 11
        "\n" +
        "[shader(\"fragment\")]\n" +
        "float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target\n" +
        "{\n" +
        "    return Comb[0].Sample(uv) + Comb[1].Sample(uv);\n" +
        "}\n";

    private static Result<CompiledShader, ShaderError[]> CompileSlang(string source, PlatformTarget target)
    {
        var compiler = new SlangCompiler(
            new EffectCompiler(),
            () => SlangToolPath.GetUnsupportedReason() is { } reason
                ? new SlangCompiler.SlangcLocation(reason, null)
                : new SlangCompiler.SlangcLocation(null, SlangToolPath.Resolve()),
            SlangNativeCache.EnsureRunnableSlangc,
            SlangCompiler.RunSlangc);
        return compiler.Compile(source, new CompilerOptions { Target = target, SourceFileName = "Arrays.slang" });
    }

    private static string Errors(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code} {e.File}({e.Line},{e.Column}): {e.Message}")) : "ok";

    [Fact]
    public void CombinedSamplerArray_OnVulkan_IsRefusedWithSd0221_AtTheSlangDeclaration()
    {
        var result = CompileSlang(ArraySlang, PlatformTarget.Vulkan);

        result.IsFailure.ShouldBeTrue("a texture array has no representation in MonoGame's Vulkan effect format");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0221");
        error.Message.ShouldContain("'Comb' is an array of 2 textures", Case.Sensitive);
        // Not slangc's `#line 93 "core"` for the hoisted half: the author's own line.
        error.File.ShouldBe("Arrays.slang");
        error.Line.ShouldBe(2);
        error.Column.ShouldBe(11);
    }

    [Fact]
    public void CombinedSamplerArray_OnDirectX11_IsOneParameterBoundToSlotZero_LikeTheFxRoute()
    {
        // Issue #339/#340 on this route: slangc lowers `Sampler2D Comb[2]` to a texture array plus a
        // sampler array; DirectX 11 must give mgfxc's one-parameter table (as the hand-written
        // `Texture2D Comb[2]; SamplerState CombSampler;` does, measured against mgfxc 3.8.4.1) and
        // must NOT refuse slangc's hoisted sampler half as an author-written sampler array (SD0223).
        var slang = CompileSlang(ArraySlang, PlatformTarget.DirectX);
        slang.IsSuccess.ShouldBeTrue(Errors(slang));
        MgfxBlobReader effect = MgfxBlobReader.Parse(slang.Value.Data);

        const string fx = """
            Texture2D Comb[2];
            SamplerState CombSampler;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Comb[0].Sample(CombSampler, uv) + Comb[1].Sample(CombSampler, uv);
            }
            technique T { pass P0 { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        var fxResult = new EffectCompiler().Compile(fx, new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "Arrays.fx" });
        fxResult.IsSuccess.ShouldBeTrue(Errors(fxResult));
        MgfxBlobReader handWritten = MgfxBlobReader.Parse(fxResult.Value.Data);

        MgfxParameterRecord texture = effect.Parameters.ShouldHaveSingleItem();
        texture.Name.ShouldBe("Comb");
        texture.Type.ShouldBe((byte)7, "Texture2D");
        effect.Parameters.Select(p => p.Name).ShouldBe(handWritten.Parameters.Select(p => p.Name));

        MgfxSamplerRecord record = effect.Samplers.ShouldHaveSingleItem();
        (record.TextureSlot, record.SamplerSlot, record.Parameter).ShouldBe(((byte)0, (byte)0, (byte)0));
        handWritten.Samplers.ShouldHaveSingleItem();

        // DirectX 11 raises no array diagnostic (its table is mgfxc's and WindowsDX reads the
        // other elements through GraphicsDevice.Textures[i]).
        slang.Value.Warnings.Where(w => w.Code is "SD0222" or "SD0223").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    public void AuthorWrittenSamplerArray_IsRefusedWithSd0223_AtTheSlangDeclaration(PlatformTarget target)
    {
        // The exemption covers slangc's hoisted half only: a `SamplerState S[N]` the Slang author
        // wrote is the shape mgfxc's parser refuses, so it is refused here too, relocated from the
        // merged HLSL to the author's own line.
        const string source =
            "Texture2D TexA;\n" +
            "Texture2D TexB;\n" +
            "SamplerState Samplers[2];\n" +                                // line 3, name at column 14
            "\n" +
            "[shader(\"fragment\")]\n" +
            "float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target\n" +
            "{\n" +
            "    return TexA.Sample(Samplers[0], uv) + TexB.Sample(Samplers[1], uv);\n" +
            "}\n";

        var result = CompileSlang(source, target);

        result.IsFailure.ShouldBeTrue("mgfxc refuses a sampler array on every profile; the Slang route must not compile what the .fx route refuses");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0223");
        error.Message.ShouldContain("'Samplers' is an array of 2 samplers", Case.Sensitive);
        error.File.ShouldBe("Arrays.slang");
        error.Line.ShouldBe(3);
        error.Column.ShouldBe(14);
    }

    [Fact]
    public void CombinedSamplerArray_OnDirectX12_IsOneParameterBoundToSlotZero_LikeTheFxRoute()
    {
        var slang = CompileSlang(ArraySlang, PlatformTarget.DirectX12);
        slang.IsSuccess.ShouldBeTrue(Errors(slang));
        MgfxBlobReader effect = MgfxBlobReader.Parse(slang.Value.Data);

        // The hand-written .fx route's equivalent, the shape mgfxc 3.8.5 was measured on.
        const string fx = """
            Texture2D Comb[2];
            SamplerState CombSampler;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Comb[0].Sample(CombSampler, uv) + Comb[1].Sample(CombSampler, uv);
            }
            technique T { pass P0 { PixelShader = compile ps_6_0 MainPS(); } }
            """;
        var fxResult = new EffectCompiler().Compile(fx, new CompilerOptions { Target = PlatformTarget.DirectX12, SourceFileName = "Arrays.fx" });
        fxResult.IsSuccess.ShouldBeTrue(Errors(fxResult));
        MgfxBlobReader handWritten = MgfxBlobReader.Parse(fxResult.Value.Data);

        MgfxParameterRecord texture = effect.Parameters.ShouldHaveSingleItem();
        texture.Name.ShouldBe("Comb");
        texture.Type.ShouldBe((byte)7, "Texture2D");
        texture.ElementCount.ShouldBe(0);
        effect.Parameters.Select(p => p.Name).ShouldBe(handWritten.Parameters.Select(p => p.Name));

        MgfxSamplerRecord record = effect.Samplers.ShouldHaveSingleItem();
        (record.TextureSlot, record.SamplerSlot, record.Parameter).ShouldBe(((byte)0, (byte)0, (byte)0));
        handWritten.Samplers.ShouldHaveSingleItem();

        // The DirectX 12 warning (SD0222) rides along, relocated to the author's Slang line.
        ShaderError warning = slang.Value.Warnings.Where(w => w.Code == "SD0222").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("'Comb' is an array of 2 textures", Case.Sensitive);
        warning.File.ShouldBe("Arrays.slang");
        warning.Line.ShouldBe(2);
        warning.Column.ShouldBe(11);
    }
}
