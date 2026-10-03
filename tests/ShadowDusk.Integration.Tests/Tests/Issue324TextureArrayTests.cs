#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #324: an ARRAY of textures (<c>Texture2D Tex[N]</c>) on the MGFX v11 targets, pinned
/// against what real <c>mgfxc</c> 3.8.5 puts in the parameter table (measured 2026-10-02 for
/// N = 1, 2 and 4, with and without an explicit register; identical for every shape):
/// <list type="bullet">
///   <item><c>/Profile:DirectX_12</c>: one Object parameter <c>Tex</c> (Texture2D, no elements),
///   one sampler record (texture slot 0, sampler slot 0) pointing at it, header
///   <c>maxTextureSlot</c> 0. ShadowDusk emits the same table (the committed goldens under
///   <c>tests/fixtures/golden/DirectX_12/</c> are compared record for record).</item>
///   <item><c>/Profile:Vulkan</c>: NO parameter, NO sampler record, NO descriptor binding (the
///   committed <c>tests/fixtures/golden/Vulkan/</c> goldens pin that), so the texture can never be
///   set and the effect cannot draw. ShadowDusk used to reflect only the sampler; it now refuses
///   the shape with <c>SD0221</c>, located at the declaration.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "Vulkan")]
[Trait("Platform", "DirectX12")]
public sealed class Issue324TextureArrayTests
{
    private const string Vulkan = "Vulkan";
    private const string DirectX12 = "DirectX_12";

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

    // ---- Vulkan: refused loudly, at the declaration -------------------------------------------

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Vulkan_TextureArray_IsRefusedWithSd0221_AtTheDeclaration(int elements, bool explicitRegisters)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new EffectCompiler().CompileAsync(TextureArrayShader(elements, explicitRegisters), new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "TextureArray.fx",
        }, cts.Token);

        result.IsFailure.ShouldBeTrue("an array of textures has no representation in MonoGame's Vulkan effect format " +
                                      "(mgfxc 3.8.5 reflects it as nothing at all); it must be refused, not compiled");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0221");
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
        error.Message.ShouldContain("'Tex'", Case.Sensitive);
        error.Message.ShouldContain($"Texture2D Tex[{elements}]", Case.Sensitive);
        error.Message.ShouldContain("mgfxc 3.8.5", Case.Sensitive);
        error.Message.ShouldContain("#if !VULKAN", Case.Sensitive);
        // Located at the declaration: `Texture2D Tex[N]` is line 7, and the name starts at column 11.
        error.File.ShouldBe("TextureArray.fx");
        error.Line.ShouldBe(7);
        error.Column.ShouldBe(11);
    }

    [Fact]
    public async Task Vulkan_SamplerArray_IsRefusedWithSd0221_NamingTheSampler()
    {
        const string source = """
            Texture2D TexA : register(t0);
            Texture2D TexB : register(t1);
            SamplerState Samplers[2];

            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
            {
                return TexA.Sample(Samplers[0], uv) + TexB.Sample(Samplers[1], uv);
            }

            technique T { pass P { PixelShader = compile ps_6_0 MainPS(); } }
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "SamplerArray.fx",
        }, cts.Token);

        result.IsFailure.ShouldBeTrue("mgfxc refuses a sampler array on every profile; Vulkan binds one sampler per slot");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0221");
        error.Message.ShouldContain("'Samplers' is an array of 2 samplers", Case.Sensitive);
        error.Message.ShouldContain("SamplerState Samplers[2]", Case.Sensitive);
        error.File.ShouldBe("SamplerArray.fx");
        error.Line.ShouldBe(3);
        error.Column.ShouldBe(14);
    }

    [Fact]
    public async Task Vulkan_SingleTextureAndSampler_StillCompiles()
    {
        // The guard must fire on arrays only: the ordinary pair is the shape every Vulkan
        // fixture uses, and it must keep compiling byte for byte (the corpus sweep is the
        // byte-identity proof; this pins the no-false-positive half in-suite).
        const string source = """
            Texture2D Tex : register(t0);
            SamplerState TexSampler : register(s0);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
            {
                return Tex.Sample(TexSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_6_0 MainPS(); } }
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "Single.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        MgfxBlobReader.Parse(result.Value.Data).ParameterNames.ShouldContain("Tex");
    }

    [Fact]
    public async Task OpenGL_TextureArray_StillFailsLoudly_NamingTheArray()
    {
        // The GL route rejected the shape before (SD0217, from the combined-sampler pair walk), but
        // said "not declared as a separate texture", which it is. mgfxc fails on it too
        // ("Sequence contains no matching element"). The message now names the array.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string fxPath = TestHelpers.FixturePath(Path.Combine("texture-arrays", "TextureArray2.fx"));
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(fxPath, cts.Token), new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fxPath,
        }, cts.Token);

        result.IsFailure.ShouldBeTrue("MonoGame's GL effect format has no representation for a texture array");
        ShaderError error = result.Error.First(e => e.Severity == ShaderErrorSeverity.Error);
        error.Code.ShouldBe("SD0217");
        error.Message.ShouldContain("'Tex' is declared as an array of textures", Case.Sensitive);
        error.Message.ShouldContain("Sequence contains no matching element", Case.Sensitive);
    }

    // ---- DirectX 12: mgfxc's table, one parameter bound to slot 0 -------------------------------

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task DirectX12_TextureArray_ReflectsOneParameterBoundToSlotZero_AsMgfxcDoes(int elements, bool explicitRegisters)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new EffectCompiler().CompileAsync(TextureArrayShader(elements, explicitRegisters), new CompilerOptions
        {
            Target = PlatformTarget.DirectX12,
            SourceFileName = "TextureArray.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var reader = MgfxBlobReader.Parse(result.Value.Data);

        // mgfxc 3.8.5 /Profile:DirectX_12, measured: exactly this, for every N and register shape.
        MgfxParameterRecord tex = reader.Parameters.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.Class.ShouldBe((byte)3, "EffectParameterClass.Object");
        tex.Type.ShouldBe((byte)7, "EffectParameterType.Texture2D");
        tex.ElementCount.ShouldBe(0, "mgfxc writes no element records for a texture array");

        MgfxSamplerRecord record = reader.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)0);
        record.SamplerSlot.ShouldBe((byte)0);
        record.Parameter.ShouldBe((byte)0);

        var header = DirectX12ShaderCodeReader.Parse(reader.Shaders.Single().Bytecode);
        header.TextureMaxSlot.ShouldBe(0, "mgfxc's header names slot 0 as the highest texture slot for the whole array");
        header.SamplerMaxSlot.ShouldBe(0);

        // The consumer is told what that table means in the engine (SD0222), once, at the
        // declaration; a 1-element array is one texture and gets no warning.
        var warnings = result.Value.Warnings.Where(w => w.Code == "SD0222").ToList();
        if (elements == 1)
        {
            warnings.ShouldBeEmpty();
        }
        else
        {
            ShaderError warning = warnings.ShouldHaveSingleItem();
            warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
            warning.Message.ShouldContain($"'Tex' is an array of {elements} textures", Case.Sensitive);
            warning.Message.ShouldContain("GraphicsDevice.Textures[i]", Case.Sensitive);
            warning.File.ShouldBe("TextureArray.fx");
            warning.Line.ShouldBe(7);
            warning.Column.ShouldBe(11);
        }
    }

    // ---- The committed goldens: table equality on DirectX_12, emptiness on Vulkan -----------------

    public static TheoryData<string> Fixtures() => new() { "TextureArray2", "TextureArray4NoRegister" };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task DirectX12_Fixture_ParameterAndSamplerTablesEqualTheMgfxcGolden(string stem)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string fxPath = TestHelpers.FixturePath(Path.Combine("texture-arrays", stem + ".fx"));
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(fxPath, cts.Token), new CompilerOptions
        {
            Target = PlatformTarget.DirectX12,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fxPath,
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var golden  = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath(DirectX12, stem), cts.Token));
        var subject = MgfxBlobReader.Parse(result.Value.Data);

        golden.ProfileId.ShouldBe((byte)2);
        subject.ProfileId.ShouldBe((byte)2);

        // The parameter table, record for record (name, class, type, shape, elements, members).
        Describe(subject.Parameters).ShouldBe(Describe(golden.Parameters));

        // The sampler table: type, slots, parameter index and baked state. The record NAME is
        // the one known, pre-existing DX12 divergence (mgfxc writes the HLSL sampler name,
        // ShadowDusk the positional `ps_s{slot}`; tracked separately, not changed here).
        subject.Samplers.Select(s => (s.ShaderIndex, s.Type, s.TextureSlot, s.SamplerSlot, s.Parameter, s.State))
            .ShouldBe(golden.Samplers.Select(s => (s.ShaderIndex, s.Type, s.TextureSlot, s.SamplerSlot, s.Parameter, s.State)));

        // The DX12 header MonoGame sizes its descriptor tables from.
        var goldenHeader  = DirectX12ShaderCodeReader.Parse(golden.Shaders.Single().Bytecode);
        var subjectHeader = DirectX12ShaderCodeReader.Parse(subject.Shaders.Single().Bytecode);
        (subjectHeader.TextureMaxSlot, subjectHeader.SamplerMaxSlot)
            .ShouldBe((goldenHeader.TextureMaxSlot, goldenHeader.SamplerMaxSlot));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Vulkan_Fixture_MgfxcGoldenHasNoTable_AndShadowDuskRefuses(string stem)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The evidence the SD0221 rejection rests on: the reference compiler's own output has
        // nothing a consumer could bind.
        var golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath(Vulkan, stem), cts.Token));
        golden.ProfileId.ShouldBe((byte)80);
        golden.Parameters.ShouldBeEmpty("mgfxc 3.8.5 reflects a texture array as no parameter at all on Vulkan");
        golden.Samplers.ShouldBeEmpty();
        var layout = VulkanShaderCodeReader.Parse(golden.Shaders.Single().Bytecode);
        layout.Bindings.ShouldBeEmpty("no descriptor binding either, although the SPIR-V samples the array");
        layout.TextureSlots.ShouldBe(0u);
        layout.SamplerSlots.ShouldBe(0u);
        layout.SpirvMagicOk.ShouldBeTrue();

        string fxPath = TestHelpers.FixturePath(Path.Combine("texture-arrays", stem + ".fx"));
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(fxPath, cts.Token), new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fxPath,
        }, cts.Token);

        result.IsFailure.ShouldBeTrue("the shape must be refused, never compiled to an effect with an empty table");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0221");
        error.Message.ShouldContain("'Tex'", Case.Sensitive);
        // The preprocessor's #line marker spells the path its own way; the file name is the contract.
        Path.GetFileName(error.File).ShouldBe(stem + ".fx");
        // Located at the DECLARATION, not at the fixture's comment header, which also spells `Tex[N]`.
        string[] lines = await File.ReadAllLinesAsync(fxPath, cts.Token);
        int declarationLine = Array.FindIndex(lines, l => l.StartsWith("Texture2D Tex[", StringComparison.Ordinal)) + 1;
        declarationLine.ShouldBeGreaterThan(1, "the fixture declares Texture2D Tex[N] below its header");
        error.Line.ShouldBe(declarationLine);
        error.Column.ShouldBe("Texture2D ".Length + 1);
    }

    private static IEnumerable<string> Describe(IEnumerable<MgfxParameterRecord> parameters) =>
        parameters.Select(p => $"{p.Name} class={p.Class} type={p.Type} {p.Rows}x{p.Columns} elems={p.ElementCount} members={p.MemberCount}");

    private static string GoldenPath(string profile, string stem)
    {
        string path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", profile, stem + ".mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc 3.8.5 golden must exist at {path}");
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
