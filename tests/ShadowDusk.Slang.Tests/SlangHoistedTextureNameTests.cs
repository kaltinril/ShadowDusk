#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Tests.Fx2;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #302, through real slangc: the compiled effect's parameter table must carry the name
/// the author wrote. slangc hoists the texture out of a combined <c>Sampler2D Comb</c> as
/// <c>Comb_texture_0</c> (even with <c>-no-mangle</c>), and that generated name used to be the
/// effect parameter, so <c>effect.Parameters["Comb"]</c> was null in the real engine. The
/// expected table is the one the hand-written <c>.fx</c> equivalent produces through the
/// ordinary <c>.fx</c> route (<c>Texture2D Comb;</c> plus a sampler), which is also what
/// <c>mgfxc</c> names the texture (measured, 3.8.4.1). A texture hoisted out of a struct, a
/// cbuffer or an entry-point parameter has no author-written name and fails as <c>SD0640</c>.
/// The rendered proof in real MonoGame is <c>validation/SlangTexturedGl</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangHoistedTextureNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-hoisted-name-{Guid.NewGuid():N}");

    public SlangHoistedTextureNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is not a test failure.
        }
    }

    private const string FxHeader = """
        #if SM4
            #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
            #define PS_SHADERMODEL ps_3_0
        #endif

        """;

    private const string FxTechnique = """

        technique T
        {
            pass P0
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        }
        """;

    private const string EntryHead = """
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        """;

    private const string FxEntryHead = "float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target";

    private static string Errors(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "";

    /// <summary>Compiles through real slangc, counting the slangc processes spawned.</summary>
    private static (Result<CompiledShader, ShaderError[]> Result, int SlangcRuns) CompileSlang(
        string source, PlatformTarget target, params UserDefine[] defines)
    {
        int runs = 0;
        var compiler = new SlangCompiler(
            new EffectCompiler(),
            () => SlangToolPath.GetUnsupportedReason() is { } reason
                ? new SlangCompiler.SlangcLocation(reason, null)
                : new SlangCompiler.SlangcLocation(null, SlangToolPath.Resolve()),
            SlangNativeCache.EnsureRunnableSlangc,
            (path, directory, text, arguments) =>
            {
                runs++;
                return SlangCompiler.RunSlangc(path, directory, text, arguments);
            });
        var result = compiler.Compile(
            source, new CompilerOptions { Target = target, SourceFileName = "Names.slang", Defines = defines });
        return (result, runs);
    }

    private static MgfxBlobReader Slang(string source, PlatformTarget target, params UserDefine[] defines)
    {
        var (result, _) = CompileSlang(source, target, defines);
        result.IsSuccess.ShouldBeTrue(Errors(result));
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static MgfxBlobReader Fx(string declarationsAndShader, PlatformTarget target)
    {
        var result = new EffectCompiler().Compile(
            FxHeader + declarationsAndShader + FxTechnique,
            new CompilerOptions { Target = target, SourceFileName = "Names.fx" });
        result.IsSuccess.ShouldBeTrue(Errors(result));
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    // The whole reflected table a consumer's effect.Parameters sees, and what each sampler binds.
    private static string[] ParameterNames(MgfxBlobReader effect) => effect.Parameters.Select(p => p.Name).ToArray();

    private static (string Texture, byte TextureSlot, byte SamplerSlot)[] Bindings(MgfxBlobReader effect) =>
        effect.Samplers
            .Select(s => (effect.Parameters[s.Parameter].Name, s.TextureSlot, s.SamplerSlot))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();

    private string WriteModule(string fileName, string text)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
        return path.Replace('\\', '/');
    }

    // ---- The combined sampler --------------------------------------------------------------

    private const string CombinedSlang = "Sampler2D Comb;\n" + EntryHead + "\n{\n    return Comb.Sample(uv);\n}\n";

    // The same declarations written out by hand for the .fx route.
    private const string CombinedFx =
        "Texture2D Comb;\nSamplerState Comb_sampler_0;\n" + FxEntryHead + "\n{\n    return Comb.Sample(Comb_sampler_0, uv);\n}\n";

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public void CombinedSampler2D_ReflectsUnderTheAuthorsName_LikeTheHandWrittenFx(PlatformTarget target)
    {
        var (result, runs) = CompileSlang(CombinedSlang, target);
        result.IsSuccess.ShouldBeTrue(Errors(result));
        MgfxBlobReader effect = MgfxBlobReader.Parse(result.Value.Data);
        MgfxBlobReader handWritten = Fx(CombinedFx, target);

        // effect.Parameters["Comb"] finds the texture; slangc's generated name is gone.
        MgfxParameterRecord texture = effect.Parameters.Where(p => p.Name == "Comb").ShouldHaveSingleItem();
        texture.Type.ShouldBe((byte)7, "Texture2D");
        ParameterNames(effect).ShouldNotContain("Comb_texture_0");
        ParameterNames(effect).ShouldBe(ParameterNames(handWritten));
        Bindings(effect).ShouldBe(Bindings(handWritten));
        Bindings(effect).ShouldBe([("Comb", (byte)0, (byte)0)]);
        // The raw source decides the name: one slangc run for the one entry point, as before.
        runs.ShouldBe(1);
    }

    [Fact]
    public void CombinedSampler2D_OnFna_IsATextureParameterTheSamplerNamesByTheAuthorsName()
    {
        var (result, _) = CompileSlang(CombinedSlang, PlatformTarget.Fna);
        result.IsSuccess.ShouldBeTrue(Errors(result));

        Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(result.Value.Data);

        Fx2ParsedParameter texture = effect.Parameters.Where(p => p.Name == "Comb").ShouldHaveSingleItem();
        texture.Type.ShouldBeInRange(5, 9, "a texture parameter");
        effect.Parameters.Select(p => p.Name).ShouldNotContain("Comb_texture_0");
        effect.SamplerTextureMap["Comb_sampler_0"].ShouldBe("Comb");
    }

    public static TheoryData<string, string, string, int, PlatformTarget> CombinedShapes()
    {
        // (declaration, sample call, element name suffix, MGFX texture type, target)
        var data = new TheoryData<string, string, string, int, PlatformTarget>();
        foreach (PlatformTarget target in new[] { PlatformTarget.DirectX, PlatformTarget.OpenGL })
        {
            data.Add("Sampler3D Comb;", "Comb.Sample(float3(uv, 0))", "", 8, target);
            data.Add("SamplerCube Comb;", "Comb.Sample(float3(uv, 1))", "", 9, target);
            data.Add("Sampler2D<float4> Comb;", "Comb.Sample(uv)", "", 7, target);
        }
        // DirectX only: OpenGL rejects these shapes loudly on the .fx route as well (a 1D or
        // array sampler in the GL rewrite, SD0210; an array of separate samplers in SPIRV-Cross).
        data.Add("Sampler1D Comb;", "Comb.Sample(uv.x)", "", 6, PlatformTarget.DirectX);
        data.Add("Sampler2DArray Comb;", "Comb.Sample(float3(uv, 0))", "", 7, PlatformTarget.DirectX);
        data.Add("SamplerCubeArray Comb;", "Comb.Sample(float4(uv, 0, 0))", "", 9, PlatformTarget.DirectX);
        data.Add("Sampler2D Comb[2];", "Comb[0].Sample(uv) + Comb[1].Sample(uv)", "[0]", 7, PlatformTarget.DirectX);
        return data;
    }

    [Theory]
    [MemberData(nameof(CombinedShapes))]
    public void EveryCombinedSamplerShape_ReflectsUnderTheAuthorsName(
        string declaration, string sample, string element, int textureType, PlatformTarget target)
    {
        MgfxBlobReader effect = Slang($"{declaration}\n{EntryHead}\n{{\n    return {sample};\n}}\n", target);

        MgfxParameterRecord texture = effect.Parameters.Where(p => p.Name == "Comb" + element).ShouldHaveSingleItem();
        texture.Type.ShouldBe((byte)textureType);
        ParameterNames(effect).ShouldAllBe(name => !name.Contains("_texture_", StringComparison.Ordinal));
        Bindings(effect).ShouldContain(b => b.Texture == "Comb" + element);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSamplerUsedByBothEntryPoints_IsOneParameterUnderTheAuthorsName(PlatformTarget target)
    {
        const string source = """
            Sampler2D Comb;
            cbuffer Params { float4x4 WorldViewProjection; };
            struct VsOut { float4 Position : SV_Position; float2 UV : TEXCOORD0; };

            [shader("vertex")]
            VsOut MainVS(float4 position : POSITION0, float2 uv : TEXCOORD0)
            {
                VsOut o;
                o.Position = mul(position, WorldViewProjection);
                o.UV = uv;
                return o;
            }

            [shader("fragment")]
            float4 MainPS(VsOut input) : SV_Target
            {
                return Comb.Sample(input.UV);
            }
            """;

        MgfxBlobReader effect = Slang(source, target);

        effect.Parameters.Where(p => p.Name == "Comb").ShouldHaveSingleItem();
        ParameterNames(effect).ShouldContain("WorldViewProjection");
        ParameterNames(effect).ShouldNotContain("Comb_texture_0");
        Bindings(effect).ShouldBe([("Comb", (byte)0, (byte)0)]);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSamplerInAnImportedModule_ReflectsUnderTheModulesName(PlatformTarget target)
    {
        string module = WriteModule("combmod.slang", "module combmod;\npublic Sampler2D ModComb;\n");
        string source = $"import \"{module}\";\n{EntryHead}\n{{\n    return ModComb.Sample(uv);\n}}\n";

        MgfxBlobReader effect = Slang(source, target);

        effect.Parameters.Where(p => p.Name == "ModComb").ShouldHaveSingleItem();
        ParameterNames(effect).ShouldNotContain("ModComb_texture_0");
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSamplerNamedThroughAMacroOrADefine_ReflectsUnderTheExpandedName(PlatformTarget target)
    {
        // Neither raw text declares 'Comb' plainly, so slangc's own preprocessor is asked: one
        // extra run for these shapes only.
        const string macro = "#define DECLARE(n) Sampler2D n;\nDECLARE(Comb)\n" + EntryHead + "\n{\n    return Comb.Sample(uv);\n}\n";
        var (viaMacro, macroRuns) = CompileSlang(macro, target);
        viaMacro.IsSuccess.ShouldBeTrue(Errors(viaMacro));
        ParameterNames(MgfxBlobReader.Parse(viaMacro.Value.Data)).ShouldContain("Comb");
        macroRuns.ShouldBe(2);

        const string defined = "Sampler2D NAME;\n" + EntryHead + "\n{\n    return NAME.Sample(uv);\n}\n";
        var (viaDefine, defineRuns) = CompileSlang(defined, target, new UserDefine("NAME", "Comb"));
        viaDefine.IsSuccess.ShouldBeTrue(Errors(viaDefine));
        ParameterNames(MgfxBlobReader.Parse(viaDefine.Value.Data)).ShouldContain("Comb");
        ParameterNames(MgfxBlobReader.Parse(viaDefine.Value.Data)).ShouldNotContain("Comb_texture_0");
        defineRuns.ShouldBe(2);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void GeneratedNameWrittenInACommentBesideTheDeclaration_DoesNotKeepIt(PlatformTarget target)
    {
        // What a consumer who hit this bug would have noted in the shader.
        const string source =
            "Sampler2D Comb; // set through effect.Parameters[\"Comb_texture_0\"]\n" + EntryHead + "\n{\n    return Comb.Sample(uv);\n}\n";

        var (result, runs) = CompileSlang(source, target);

        result.IsSuccess.ShouldBeTrue(Errors(result));
        ParameterNames(MgfxBlobReader.Parse(result.Value.Data)).ShouldContain("Comb");
        ParameterNames(MgfxBlobReader.Parse(result.Value.Data)).ShouldNotContain("Comb_texture_0");
        runs.ShouldBe(1);
    }

    // ---- Names the author wrote stay --------------------------------------------------------

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void AuthorTexturesWithHoistedLookingNames_KeepThem_AtNoExtraCost(PlatformTarget target)
    {
        // 'tex_layer_0' has the shape of a hoisted name ('<global>_<field>_<n>'); so do the
        // halves of a combined sampler when an author spells them out. All three are the
        // author's own globals and must reach the table untouched.
        const string source = """
            Texture2D tex_layer_0;
            Texture2D Comb_texture_0;
            SamplerState Comb_sampler_0;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return tex_layer_0.Sample(Comb_sampler_0, uv) * Comb_texture_0.Sample(Comb_sampler_0, uv);
            }
            """;

        var (result, runs) = CompileSlang(source, target);

        result.IsSuccess.ShouldBeTrue(Errors(result));
        string[] names = ParameterNames(MgfxBlobReader.Parse(result.Value.Data));
        names.ShouldContain("tex_layer_0");
        names.ShouldContain("Comb_texture_0");
        names.ShouldNotContain("Comb");
        runs.ShouldBe(1);
    }

    // ---- A texture with no author-written name ----------------------------------------------

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Fna)]
    public void TextureHeldInAStruct_FailsAsSD0640_AtTheGlobal(PlatformTarget target)
    {
        const string source = """
            struct Material { Texture2D albedo; SamplerState state; };

              Material gMat;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return gMat.albedo.Sample(gMat.state, uv);
            }
            """;

        var (result, _) = CompileSlang(source, target);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0640");
        (error.File, error.Line, error.Column).ShouldBe(("Names.slang", 3, 12));
        error.Message.ShouldContain("a texture held in 'gMat' under a name it generated, 'gMat_albedo_0'", Case.Sensitive);
        error.Message.ShouldContain("Sampler2D Name;", Case.Sensitive);
    }

    public static TheoryData<string, string, string> HeldTextures() => new()
    {
        // A struct with a data field goes into slangc's global parameter block.
        {
            "struct M { Texture2D t; SamplerState s; float4 tint; };\nM gM;\n",
            "gM.t.Sample(gM.s, uv) * gM.tint",
            "a texture held in 'gM' under a name it generated, 'globalParams_gM_t_0'"
        },
        // A combined sampler inside a struct: the texture is still a field of the struct.
        {
            "struct M { Sampler2D c; };\nM gM;\n",
            "gM.c.Sample(uv)",
            "a texture held in 'gM' under a name it generated, 'gM_c_texture_0'"
        },
        // A texture declared inside a cbuffer block.
        {
            "cbuffer C { Texture2D T; SamplerState S; float4 x; }\n",
            "T.Sample(S, uv) * x",
            "a texture held in an aggregate under a name it generated, 'C_T_0'"
        },
    };

    [Theory]
    [MemberData(nameof(HeldTextures))]
    public void TextureHeldInAnyAggregate_FailsAsSD0640(string declarations, string expression, string expected)
    {
        var (result, _) = CompileSlang($"{declarations}{EntryHead}\n{{\n    return {expression};\n}}\n", PlatformTarget.DirectX);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0640");
        error.Message.ShouldContain(expected, Case.Sensitive);
    }

    [Fact]
    public void TextureAsAnEntryPointUniform_FailsAsSD0640()
    {
        const string source = """
            [shader("fragment")]
            float4 MainPS(uniform Texture2D T, uniform SamplerState S, float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return T.Sample(S, uv);
            }
            """;

        var (result, _) = CompileSlang(source, PlatformTarget.DirectX);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0640");
        error.Message.ShouldContain("'entryPointParams_T_0'", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void SamplerHeldInAStruct_StillCompiles(PlatformTarget target)
    {
        // Only a texture is set by name. A hoisted sampler keeps slangc's name and the
        // texture beside it keeps the author's.
        const string source = """
            struct Samplers { SamplerState s; };
            Samplers gS;
            Texture2D SpriteTexture;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return SpriteTexture.Sample(gS.s, uv);
            }
            """;

        MgfxBlobReader effect = Slang(source, target);

        Bindings(effect).ShouldBe([("SpriteTexture", (byte)0, (byte)0)]);
    }

    // ---- Collisions -------------------------------------------------------------------------

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void AuthorGlobalSpelledLikeTheGeneratedHalf_FailsAsSD0641(PlatformTarget target)
    {
        // slangc emits both the hoisted half and the author's global as 'Comb_texture_0'.
        const string source = """
            Sampler2D Comb;
            Texture2D Comb_texture_0;
            SamplerState S;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Comb.Sample(uv) + Comb_texture_0.Sample(S, uv);
            }
            """;

        var (result, _) = CompileSlang(source, target);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0641");
        error.Message.ShouldContain("declares the global 'Comb_texture_0' more than once", Case.Sensitive);
    }
}
