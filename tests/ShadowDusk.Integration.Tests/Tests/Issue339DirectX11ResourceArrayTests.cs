#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Integration.Tests.Dxc;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issues #339 and #340: arrays of textures and samplers on the DirectX targets, pinned against
/// what real <c>mgfxc</c> does (measured 2026-10-02 with 3.8.4.1, the pinned v10 oracle, and 3.8.5):
/// <list type="bullet">
///   <item><b>Texture array on DirectX_11</b> (#339): ONE Object parameter <c>Tex</c> and ONE sampler
///   record at the array's base slot, for N = 1, 2 and 4, with or without a register, whichever
///   elements are read. mgfxc compiles at the author's <c>ps_4_0</c>, where fxc reflects the array as
///   one binding with <c>BindCount</c> N; ShadowDusk compiles at <c>ps_5_0</c>, where the RDEF stores
///   one record per element (<c>Tex[0]</c>, <c>Tex[1]</c>), which used to become two parameters no
///   author names (<c>effect.Parameters["Tex"]</c> was null). The committed goldens under
///   <c>tests/fixtures/golden/DirectX_11/</c> are compared record for record.</item>
///   <item><b>Sampler array</b> (#340): refused by mgfxc on EVERY profile in its own parser
///   (<c>Unexpected token '[' found. Expected Semicolon, Comma, or CloseParenthesis.</c>), so DirectX 11
///   and DirectX 12 refuse it too, <c>SD0223</c> at the declaration. Before the fix both compiled it.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
[Trait("Platform", "DirectX12")]
public sealed class Issue339DirectX11ResourceArrayTests
{
    /// <summary>The probe shape: N textures through one sampler, PS-only (SpriteBatch-compatible).</summary>
    private static string TextureArrayShader(int elements, bool explicitRegisters) =>
        "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#else\n#define PS_SHADERMODEL ps_4_0\n#endif\n" +
        "\n" +
        $"Texture2D Tex[{elements}]{(explicitRegisters ? " : register(t0)" : "")};\n" +   // line 7
        $"SamplerState TexSampler{(explicitRegisters ? " : register(s0)" : "")};\n" +
        "\n" +
        "float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0\n" +
        "{\n" +
        "    float4 c = 0" + string.Concat(Enumerable.Range(0, elements).Select(i => $" + Tex[{i}].Sample(TexSampler, uv)")) + ";\n" +
        $"    return c * color / {elements}.0;\n" +
        "}\n" +
        "\n" +
        "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

    public static TheoryData<int, bool> Shapes()
    {
        var data = new TheoryData<int, bool>();
        foreach (int n in new[] { 1, 2, 4 })
        foreach (bool reg in new[] { false, true })
            data.Add(n, reg);
        return data;
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string source, PlatformTarget target, string fileName, DxbcBackend backend = DxbcBackend.Vkd3d, CancellationToken ct = default)
        => await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            DxbcBackend = backend,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fileName,
        }, ct);

    private static string Failure(Result<CompiledShader, ShaderError[]> r) =>
        r.IsFailure ? string.Join("; ", r.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok";

    // ---- #339: DirectX 11 reflects mgfxc's one `Tex` parameter -----------------------------------

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task DirectX11_TextureArray_ReflectsOneParameterAndOneRecord_AsMgfxcDoes(int elements, bool explicitRegisters)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(TextureArrayShader(elements, explicitRegisters), PlatformTarget.DirectX, "TextureArray.fx", ct: cts.Token);

        result.IsSuccess.ShouldBeTrue(Failure(result));
        var reader = MgfxBlobReader.Parse(result.Value.Data);

        // mgfxc 3.8.4.1 and 3.8.5 /Profile:DirectX_11, measured: exactly this, for every N and register shape.
        MgfxParameterRecord tex = reader.Parameters.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex", "the array's name, never Tex[0]/Tex[1]");
        tex.Class.ShouldBe((byte)3, "EffectParameterClass.Object");
        tex.Type.ShouldBe((byte)7, "EffectParameterType.Texture2D");
        tex.ElementCount.ShouldBe(0, "mgfxc writes no element records for a texture array");

        MgfxSamplerRecord record = reader.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)0);
        record.SamplerSlot.ShouldBe((byte)0);
        record.Parameter.ShouldBe((byte)0);
        record.Name.ShouldBe(string.Empty, "DX11 records carry no name, as in every DirectX_11 golden");

        // No array diagnostic on DirectX 11: the table is mgfxc's and WindowsDX reads the other
        // elements through GraphicsDevice.Textures[i] (validation/VsDrivenDx -- texarr).
        result.Value.Warnings.Where(w => w.Code is "SD0222" or "SD0223").ShouldBeEmpty();
    }

    [Fact]
    public async Task DirectX11_OnlyElementOneRead_StillRecordsTheArrayBaseSlot()
    {
        // fxc at ps_4_0 (mgfxc) binds the whole array: `Tex` t0 count 2, record at slot 0, even
        // when only Tex[1] is read; the SM5 RDEF has only Tex[1] at t1 and the base is slot - index.
        const string source = """
            Texture2D Tex[2] : register(t0);
            SamplerState TexSampler : register(s0);
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
            {
                return Tex[1].Sample(TexSampler, uv) * color;
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(source, PlatformTarget.DirectX, "OnlyElement1.fx", ct: cts.Token);

        result.IsSuccess.ShouldBeTrue(Failure(result));
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.Parameters.ShouldHaveSingleItem().Name.ShouldBe("Tex");
        MgfxSamplerRecord record = reader.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)0, "mgfxc's record sits at the array's base register, measured");
        record.SamplerSlot.ShouldBe((byte)0);
    }

    [Fact]
    public async Task DirectX11_ArrayAfterAnotherTexture_KeepsMgfxcsOrderAndSlots()
    {
        // mgfxc 3.8.4.1, measured: records (t0/s0 -> Other), (t1/s1 -> Tex); parameters Other, Tex.
        const string source = """
            Texture2D Other : register(t0);
            Texture2D Tex[2] : register(t1);
            SamplerState TexSampler : register(s0);
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
            {
                return (Other.Sample(TexSampler, uv) + Tex[0].Sample(TexSampler, uv) + Tex[1].Sample(TexSampler, uv)) * color / 3.0;
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(source, PlatformTarget.DirectX, "ArrayAtT1.fx", ct: cts.Token);

        result.IsSuccess.ShouldBeTrue(Failure(result));
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.Parameters.Select(p => p.Name).ShouldBe(["Other", "Tex"]);
        reader.Samplers.Select(s => (s.TextureSlot, s.SamplerSlot, s.Parameter))
            .ShouldBe([((byte)0, (byte)0, (byte)0), ((byte)1, (byte)1, (byte)1)]);
    }

    public static TheoryData<string> Fixtures() => new() { "TextureArray2", "TextureArray4NoRegister" };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task DirectX11_Fixture_TableEqualsTheMgfxcGolden_Vkd3d(string stem)
        => await AssertFixtureTableEqualsGolden(stem, DxbcBackend.Vkd3d);

    [WindowsTheory]
    [MemberData(nameof(Fixtures))]
    public async Task DirectX11_Fixture_TableEqualsTheMgfxcGolden_D3DCompilerOracle(string stem)
        => await AssertFixtureTableEqualsGolden(stem, DxbcBackend.D3DCompiler);

    private static async Task AssertFixtureTableEqualsGolden(string stem, DxbcBackend backend)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string fxPath = TestHelpers.FixturePath(Path.Combine("texture-arrays", stem + ".fx"));
        var result = await CompileAsync(await File.ReadAllTextAsync(fxPath, cts.Token), PlatformTarget.DirectX, fxPath, backend, cts.Token);
        result.IsSuccess.ShouldBeTrue(Failure(result));

        var golden  = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath("DirectX_11", stem), cts.Token));
        var subject = MgfxBlobReader.Parse(result.Value.Data);

        golden.ProfileId.ShouldBe((byte)1);
        subject.ProfileId.ShouldBe((byte)1);

        // The parameter table, record for record (name, class, type, shape, elements, members).
        Describe(subject.Parameters).ShouldBe(Describe(golden.Parameters));
        golden.Parameters.ShouldHaveSingleItem().Name.ShouldBe("Tex");

        // The sampler table in full: DX11 record names are empty on both sides, so nothing is excluded.
        subject.Samplers.Select(s => (s.ShaderIndex, s.Type, s.TextureSlot, s.SamplerSlot, s.Name, s.Parameter, s.State))
            .ShouldBe(golden.Samplers.Select(s => (s.ShaderIndex, s.Type, s.TextureSlot, s.SamplerSlot, s.Name, s.Parameter, s.State)));
        golden.Samplers.ShouldHaveSingleItem();
    }

    // ---- #340: a sampler array is refused on DirectX 11 and 12 ------------------------------------

    public static TheoryData<PlatformTarget> DirectXTargets() => new() { PlatformTarget.DirectX, PlatformTarget.DirectX12 };

    [Theory]
    [MemberData(nameof(DirectXTargets))]
    public async Task SamplerArray_IsRefusedWithSd0223_AtTheDeclaration(PlatformTarget target)
    {
        const string source = """
            Texture2D TexA : register(t0);
            Texture2D TexB : register(t1);
            SamplerState Samplers[2];

            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
            {
                return TexA.Sample(Samplers[0], uv) + TexB.Sample(Samplers[1], uv);
            }

            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(source, target, "SamplerArray.fx", ct: cts.Token);

        result.IsFailure.ShouldBeTrue("mgfxc refuses a sampler array on every profile in its parser; ShadowDusk must not compile what mgfxc never builds");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0223");
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
        error.Message.ShouldContain(target == PlatformTarget.DirectX12 ? "DirectX 12 target" : "DirectX 11 target", Case.Sensitive);
        error.Message.ShouldContain("'Samplers' is an array of 2 samplers", Case.Sensitive);
        error.Message.ShouldContain("SamplerState Samplers[2]", Case.Sensitive);
        error.Message.ShouldContain("Unexpected token '[' found. Expected Semicolon, Comma, or CloseParenthesis.", Case.Sensitive);
        error.Message.ShouldContain("SamplerState Samplers0; SamplerState Samplers1;", Case.Sensitive);
        // Located at the declaration: `SamplerState Samplers[2];` is line 3, the name starts at column 14.
        error.File.ShouldBe("SamplerArray.fx");
        error.Line.ShouldBe(3);
        error.Column.ShouldBe(14);
    }

    [Theory]
    [MemberData(nameof(DirectXTargets))]
    public async Task SamplerArray_WithATextureArray_IsRefused_TheSamplerErrorWinsOverTheTextureWarning(PlatformTarget target)
    {
        const string source = """
            Texture2D Tex[2] : register(t0);
            SamplerState TexSampler[2] : register(s0);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
            {
                return Tex[0].Sample(TexSampler[0], uv) + Tex[1].Sample(TexSampler[1], uv);
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(source, target, "TexArr2SampArr.fx", ct: cts.Token);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0223");
        error.Message.ShouldContain("'TexSampler' is an array of 2 samplers", Case.Sensitive);
        error.Line.ShouldBe(2);
        error.Column.ShouldBe(14);
    }

    [Theory]
    [MemberData(nameof(DirectXTargets))]
    public async Task OneElementSamplerArray_IsRefusedToo(PlatformTarget target)
    {
        // mgfxc's parser refuses any `[` after a sampler name, size 1 included. DXIL reports
        // BindCount 1 for it (indistinguishable from a plain sampler), so DirectX 12 falls back to
        // the declaration text; DirectX 11 sees the collapsed `S[0]` record.
        const string source = """
            Texture2D Tex : register(t0);
            SamplerState S[1] : register(s0);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
            {
                return Tex.Sample(S[0], uv);
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(source, target, "SamplerArray1.fx", ct: cts.Token);

        result.IsFailure.ShouldBeTrue("a 1-element sampler array is still a shape mgfxc's parser refuses");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0223");
        error.Message.ShouldContain("'S' is a 1-element array of samplers", Case.Sensitive);
        error.Line.ShouldBe(2);
        error.Column.ShouldBe(14);
    }

    [Theory]
    [MemberData(nameof(DirectXTargets))]
    public async Task SamplerArrayFixture_IsRefused_AtMgfxcsOwnLine(PlatformTarget target)
    {
        // mgfxc 3.8.4.1 and 3.8.5 on every profile: "SamplerArray2.fx(31,22) : Unexpected token '['
        // found..." (the `[`); ShadowDusk points at the name on the same line, column 14.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string fxPath = TestHelpers.FixturePath(Path.Combine("texture-arrays", "SamplerArray2.fx"));
        var result = await CompileAsync(await File.ReadAllTextAsync(fxPath, cts.Token), target, fxPath, ct: cts.Token);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0223");
        Path.GetFileName(error.File).ShouldBe("SamplerArray2.fx");
        error.Line.ShouldBe(31);
        error.Column.ShouldBe(14);
    }

    [Fact]
    public async Task DirectX11_TwoTexturesThroughTwoSeparateSamplers_StillCompiles()
    {
        // The no-false-positive half: the element-wise shape SD0223 recommends compiles, with two
        // records, as before (the corpus sweep is the byte-identity proof for every other shape).
        const string source = """
            Texture2D TexA : register(t0);
            Texture2D TexB : register(t1);
            SamplerState SamplerA : register(s0);
            SamplerState SamplerB : register(s1);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
            {
                return TexA.Sample(SamplerA, uv) + TexB.Sample(SamplerB, uv);
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await CompileAsync(source, PlatformTarget.DirectX, "TwoSamplers.fx", ct: cts.Token);

        result.IsSuccess.ShouldBeTrue(Failure(result));
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.Parameters.Select(p => p.Name).ShouldBe(["TexA", "TexB"]);
        reader.Samplers.Count.ShouldBe(2);
    }

    private static IEnumerable<string> Describe(IEnumerable<MgfxParameterRecord> parameters) =>
        parameters.Select(p => $"{p.Name} class={p.Class} type={p.Type} {p.Rows}x{p.Columns} elems={p.ElementCount} members={p.MemberCount}");

    private static string GoldenPath(string profile, string stem)
    {
        string path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", profile, stem + ".mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden must exist at {path}");
        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root (ShadowDusk.slnx).");
    }
}
