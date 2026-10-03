#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #230: a textured real-slangc shader on the FNA target must produce the parameter
/// table FNA binds from (a texture parameter plus a sampler whose Texture state names it),
/// not vkd3d's combined <c>S+T</c> texture-typed entry, which crashed real FNA on the first
/// draw. The rendered proof is <c>validation/FnaValidation -- slang</c>; this pins the table
/// on every host. The <c>.fx</c> route's matching rejection is pinned in
/// <c>FnaCompileFixtureTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangFnaTextureTests
{
    private const string Textured = """
        Texture2D SpriteTexture;
        SamplerState SpriteSampler;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            float4 c = SpriteTexture.Sample(SpriteSampler, uv);
            return float4(1.0 - c.rgb, c.a);
        }
        """;

    private static readonly CompilerOptions Fna = new() { Target = PlatformTarget.Fna, SourceFileName = "Invert.slang" };

    [Fact]
    public void TexturedShader_BindsTextureThroughANamedSampler()
    {
        var result = new SlangCompiler().Compile(Textured, Fna);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");

        Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(result.Value.Data);

        effect.Parameters.ShouldNotContain(p => p.Name.Contains('+'), "vkd3d's combined S+T entry must never reach the table");
        Fx2ParsedParameter texture = effect.Parameters.Where(p => p.Name == "SpriteTexture").ShouldHaveSingleItem();
        texture.Type.ShouldBeInRange(5, 9, "a texture parameter");
        Fx2ParsedParameter sampler = effect.Parameters.Where(p => p.Name == "SpriteSampler").ShouldHaveSingleItem();
        sampler.Type.ShouldBeInRange(10, 14, "a sampler parameter");
        sampler.SamplerStates.ShouldContain(s => s.Operation == 164, "the Texture state");
        effect.SamplerTextureMap["SpriteSampler"].ShouldBe("SpriteTexture");
    }

    [Fact]
    public void UnmodeledTextureCall_IsRejectedAsSD0627()
    {
        string source = Textured.Replace(
            "SpriteTexture.Sample(SpriteSampler, uv)", "SpriteTexture.SampleLevel(SpriteSampler, uv, 0.0)");

        var result = new SlangCompiler().Compile(source, Fna);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0627");
        error.Message.ShouldContain("SampleLevel", Case.Sensitive);
    }

    [Fact]
    public void TextureFunctionParameter_IsRejectedAsSuch_AtItsSourceLine()
    {
        const string source = """
            Texture2D SpriteTexture;
            SamplerState SpriteSampler;

            float4 Fetch(Texture2D t, SamplerState s, float2 uv)
            {
                return t.Sample(s, uv);
            }

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Fetch(SpriteTexture, SpriteSampler, uv);
            }
            """;

        var result = new SlangCompiler().Compile(source, Fna);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0627");
        error.Message.ShouldContain("passed as a function parameter", Case.Sensitive);
        error.Message.ShouldNotContain("has no DX9 equivalent", Case.Sensitive);
        error.Line.ShouldBe(4, "slangc's #line directives map the helper back to its Slang line");
    }

    [Fact]
    public void SamplerHoistedFromAStruct_CompilesWithTheTextureDeclaredFirst()
    {
        // slangc emits the struct's sampler (gS_s_0) BEFORE the global texture; fxc rejects a
        // sampler_state naming a texture declared after it, so the respelling reorders.
        const string source = """
            Texture2D SpriteTexture;
            struct Samplers { SamplerState s; };
            Samplers gS;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return SpriteTexture.Sample(gS.s, uv);
            }
            """;

        var capture = new CapturingCompiler();
        new SlangCompiler(capture).Compile(source, Fna).IsSuccess.ShouldBeTrue();
        string fx = capture.Captured.ShouldNotBeNull();
        int texture = fx.IndexOf("texture2D SpriteTexture;", StringComparison.Ordinal);
        texture.ShouldBeGreaterThanOrEqualTo(0);
        fx.IndexOf("sampler_state { Texture = <SpriteTexture>; }", StringComparison.Ordinal).ShouldBeGreaterThan(texture);

        var result = new SlangCompiler().Compile(source, Fna);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        Fx2BinaryValidator.Parse(result.Value.Data).SamplerTextureMap.Values.ShouldContain("SpriteTexture");
    }

    // ---- Issue #230 follow-up: a USER type whose name starts with "Texture" is not a texture.
    // Both shaders below are texture-free and compiled on OpenGL and DirectX all along; on FNA
    // they failed as SD0627 ("a texture or sampler passed as a function parameter
    // ('TextureRegion_0 r_0')" and "the texture array 'slots_0[...]'").

    private const string StructNamedTextureRegion = """
        struct TextureRegion { float4 Rect; };
        cbuffer P { float4 Rect; };
        float2 Remap(TextureRegion r, float2 uv) { return r.Rect.xy + uv * r.Rect.zw; }

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            TextureRegion r;
            r.Rect = Rect;
            return float4(Remap(r, uv), 0, 1);
        }
        """;

    private const string LocalArrayOfTextureSlot = """
        struct TextureSlot { float2 Scale; };
        cbuffer P { float4 Scales; };

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            TextureSlot slots[2];
            slots[0].Scale = Scales.xy;
            slots[1].Scale = Scales.zw;
            int i = uv.x > 0.5 ? 1 : 0;
            return float4(uv * slots[i].Scale, 0, 1);
        }
        """;

    [Theory]
    [InlineData(StructNamedTextureRegion, PlatformTarget.Fna)]
    [InlineData(StructNamedTextureRegion, PlatformTarget.OpenGL)]
    [InlineData(StructNamedTextureRegion, PlatformTarget.DirectX)]
    [InlineData(LocalArrayOfTextureSlot, PlatformTarget.Fna)]
    [InlineData(LocalArrayOfTextureSlot, PlatformTarget.OpenGL)]
    [InlineData(LocalArrayOfTextureSlot, PlatformTarget.DirectX)]
    public void TextureFreeShader_WithATextureNamedUserType_Compiles(string source, PlatformTarget target)
    {
        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = target, SourceFileName = "UserType.slang" });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        if (target == PlatformTarget.Fna)
        {
            Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(result.Value.Data);
            effect.SamplerTextureMap.ShouldBeEmpty("the shader has no texture");
        }
    }

    [Fact]
    public void TexturedShader_WithResourceNamedUserTypes_StillBindsItsRealTexture()
    {
        // The real Texture2D/SamplerState are respelled; 'TextureRegion' and 'SamplerStateInfo'
        // (user structs, one passed to a helper) are left exactly as slangc emitted them.
        const string source = """
            struct TextureRegion { float4 Rect; };
            struct SamplerStateInfo { float2 Scale; };
            cbuffer P { float4 Rect; float2 Scale; };
            Texture2D SpriteTexture;
            SamplerState SpriteSampler;
            float2 Remap(TextureRegion r, SamplerStateInfo info, float2 uv) { return r.Rect.xy + uv * r.Rect.zw * info.Scale; }

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                TextureRegion r;
                r.Rect = Rect;
                SamplerStateInfo info;
                info.Scale = Scale;
                return SpriteTexture.Sample(SpriteSampler, Remap(r, info, uv));
            }
            """;

        var result = new SlangCompiler().Compile(source, Fna);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(result.Value.Data);
        effect.SamplerTextureMap["SpriteSampler"].ShouldBe("SpriteTexture");
        effect.SamplerTextureMap.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public void OtherTargets_KeepTextureObjects(PlatformTarget target)
    {
        // The respelling is FNA-only: every other target compiles slangc's emission as-is.
        var capture = new CapturingCompiler();
        var result = new SlangCompiler(capture).Compile(
            Textured, new CompilerOptions { Target = target, SourceFileName = "Invert.slang" });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        string captured = capture.Captured.ShouldNotBeNull();
        captured.ShouldContain(".Sample(SpriteSampler", Case.Sensitive);
        captured.ShouldNotContain("sampler_state", Case.Sensitive);
    }

    [Fact]
    public void Fna_HandsThePipelineDx9TextureSyntax()
    {
        var capture = new CapturingCompiler();
        new SlangCompiler(capture).Compile(Textured, Fna).IsSuccess.ShouldBeTrue();

        string captured = capture.Captured.ShouldNotBeNull();
        captured.ShouldContain("sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };", Case.Sensitive);
        captured.ShouldContain("tex2D(SpriteSampler,", Case.Sensitive);
        captured.ShouldNotContain(".Sample(", Case.Sensitive);
    }

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Captured;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Captured = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58]));
        }
    }
}
