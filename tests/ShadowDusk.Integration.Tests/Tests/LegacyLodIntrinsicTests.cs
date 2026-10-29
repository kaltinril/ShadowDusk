#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// <c>tex2Dlod</c> / <c>tex2Dbias</c> / <c>tex2Dproj</c>, the XNA spellings of explicit-LOD, biased
/// and projective sampling, through every RewriteToSm4 target. Before 2026-10-28 the pre-parser
/// refused all three with <c>FX0012</c> on DirectX 11, DirectX 12, Vulkan and OpenGL, while real
/// <c>mgfxc</c> 3.8.4.1 compiles them at <c>vs_4_0</c>/<c>ps_4_0</c> (DirectX_11) and, in a pixel
/// shader, at <c>ps_3_0</c> (OpenGL). The fixtures and their reference builds live in
/// <c>tests/fixtures/golden/legacy-lod-intrinsics/</c> (see its README); the render proofs are
/// <c>validation/DxModernFeatures</c> (arm "legacy-lod"), <c>validation/VsDrivenDx12 -- legacy-lod</c>
/// and <c>validation/VsDrivenVulkan -- legacy-lod</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacyLodIntrinsicTests
{
    // The standard MonoGame header: ps_3_0 on OpenGL, ps_4_0 on the DirectX targets (DirectX 11
    // refuses an SM3 profile with SD0015, as mgfxc does).
    private const string Header =
        "#if OPENGL\n#define PS_SHADERMODEL ps_3_0\n#else\n#define PS_SHADERMODEL ps_4_0\n#endif\n";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "legacy-lod-intrinsics");

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(string source, PlatformTarget target, string name)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        return await new EffectCompiler().CompileAsync(source,
            new CompilerOptions { Target = target, SourceFileName = name }, cts.Token);
    }

    private static async Task<MgfxBlobReader> CompileFixtureAsync(string fixture, PlatformTarget target)
    {
        var result = await CompileAsync(await File.ReadAllTextAsync(Path.Combine(Dir, fixture + ".fx")), target, fixture + ".fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static MgfxBlobReader Golden(string file) =>
        MgfxBlobReader.Parse(File.ReadAllBytes(Path.Combine(Dir, file)));

    // Each sampler record as the runtime binds it: stage, sampler type, texture and sampler slot,
    // the parameter it reads (by NAME, so a table order difference cannot hide a wrong binding) and
    // whether a state is baked.
    private static IReadOnlyList<string> Records(MgfxBlobReader r) =>
        r.Samplers.Select(s =>
                $"{(r.Shaders[s.ShaderIndex].IsVertex ? "vs" : "ps")} type={s.Type} t{s.TextureSlot} s{s.SamplerSlot} " +
                $"->{r.Parameters[s.Parameter].Name} state={(s.State is null ? "none" : $"filter{s.State.Filter}/u{s.State.AddressU}/v{s.State.AddressV}")}")
            .Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> Params(MgfxBlobReader r) =>
        r.Parameters.Select(p => $"{p.Name} class={p.Class} type={p.Type} {p.Rows}x{p.Columns}").ToList();

    // ---- DirectX 11: the table mgfxc 3.8.4.1 writes ------------------------------------------

    [Theory]
    [InlineData("Tex2DLodRender")]
    [InlineData("Tex2DLodPixel")]
    [Trait("Platform", "DirectX")]
    public async Task DirectX11_Compiles_AndItsTableIsMgfxcs(string fixture)
    {
        MgfxBlobReader subject = await CompileFixtureAsync(fixture, PlatformTarget.DirectX);
        MgfxBlobReader golden = Golden(fixture + ".DirectX_11.mgfx");

        Params(subject).ShouldBe(Params(golden));
        Records(subject).ShouldBe(Records(golden), customMessage:
            "every sampler record (stage, slots, parameter, baked state) must be mgfxc's");
        subject.Shaders.Select(s => s.IsVertex).Order().ShouldBe(golden.Shaders.Select(s => s.IsVertex).Order());
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task DirectX11_VertexStageFetch_BindsTheTextureToTheVertexShader()
    {
        // The point of the XNA spelling in a VS: the texture must reach the VERTEX stage.
        MgfxBlobReader subject = await CompileFixtureAsync("Tex2DLodRender", PlatformTarget.DirectX);
        Records(subject).ShouldContain(r => r.StartsWith("vs ", StringComparison.Ordinal) && r.Contains("->Tex ", StringComparison.Ordinal));
    }

    // ---- DirectX 12 and Vulkan: compile, with the modern twin's parameter table ---------------

    [Theory]
    [InlineData(PlatformTarget.DirectX12, "Tex2DLodRenderModern.DirectX_12.mgfx")]
    [InlineData(PlatformTarget.Vulkan, "Tex2DLodRenderModern.Vulkan.mgfx")]
    [Trait("Platform", "DirectX12")]
    [Trait("Platform", "Vulkan")]
    public async Task Sm6Targets_Compile_WithTheParametersOfMgfxcsModernTwinBuild(PlatformTarget target, string twinGolden)
    {
        // mgfxc 3.8.5 has no SM6 reference for the legacy spelling, so the reference is its build
        // of the same shader in Texture2D/SamplerState syntax (the render drivers diff the two).
        MgfxBlobReader subject = await CompileFixtureAsync("Tex2DLodRender", target);
        MgfxBlobReader twin = Golden(twinGolden);

        // Vulkan also lists the legacy SAMPLER as a parameter of its own ('TexSampler', type 5),
        // which the twin's SamplerState is not. That predates this rewrite: the same fixture with
        // every call spelled tex2D has it too (measured 2026-10-28), so it is left out here.
        Params(subject).Where(p => !p.StartsWith("TexSampler ", StringComparison.Ordinal))
            .ShouldBe(Params(twin));
        subject.Shaders.Count.ShouldBe(2);
        subject.Samplers.Select(s => subject.Shaders[s.ShaderIndex].IsVertex).Distinct().Count()
            .ShouldBe(2, "both the vertex and the pixel shader sample Tex");
        subject.Samplers.ShouldAllBe(s => subject.Parameters[s.Parameter].Name == "Tex");
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task Vulkan_TheHelperCallPairsTextureAndSampler_OnMgfxcsCombinedDescriptor()
    {
        // The helper samples its own parameters, so the global pair never appears in a
        // 'Tex.Sample(TexSampler, …)' call; the Vulkan binding rewriter must take the helper call
        // as the pairing signal. Without it the pair stayed auto-numbered (separate image and
        // sampler descriptors at low bindings): real DesktopVK then read mip level 0 for every
        // column and faulted natively in DrawIndexed (measured 2026-10-28,
        // validation/VsDrivenVulkan -- legacy-lod). The layout must be mgfxc's for the same shader.
        MgfxBlobReader subject = await CompileFixtureAsync("Tex2DLodRender", PlatformTarget.Vulkan);
        MgfxBlobReader twin = Golden("Tex2DLodRenderModern.Vulkan.mgfx");

        foreach (bool vertex in new[] { true, false })
        {
            VulkanShaderCodeReader ours = VulkanShaderCodeReader.Parse(subject.Shaders.Single(s => s.IsVertex == vertex).Bytecode);
            VulkanShaderCodeReader theirs = VulkanShaderCodeReader.Parse(twin.Shaders.Single(s => s.IsVertex == vertex).Bytecode);
            string stage = vertex ? "vs" : "ps";
            ours.TextureSlots.ShouldBe(theirs.TextureSlots, $"{stage} texture slots");
            ours.SamplerSlots.ShouldBe(theirs.SamplerSlots, $"{stage} sampler slots");
            ours.Bindings.Select(b => $"{b.Binding}:{b.DescriptorType}x{b.DescriptorCount}")
                .ShouldBe(theirs.Bindings.Select(b => $"{b.Binding}:{b.DescriptorType}x{b.DescriptorCount}"), $"{stage} descriptor layout");
        }
    }

    // ---- OpenGL --------------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_PixelStage_Compiles_OnMgfxcsTextureUnit_AndHonorsTheLod()
    {
        MgfxBlobReader subject = await CompileFixtureAsync("Tex2DLodPixel", PlatformTarget.OpenGL);
        MgfxBlobReader golden = Golden("Tex2DLodPixel.OpenGL.mgfx");

        subject.Samplers.Select(s => $"{subject.Parameters[s.Parameter].Name}@{s.TextureSlot}")
            .ShouldBe(golden.Samplers.Select(s => $"{golden.Parameters[s.Parameter].Name}@{s.TextureSlot}"));

        string glsl = Encoding.ASCII.GetString(subject.Shaders.Single().Bytecode);
        glsl.ShouldContain("texture2DLod(ps_s0,", Case.Sensitive);
        glsl.ShouldContain("#extension GL_ARB_shader_texture_lod : enable", Case.Sensitive);

        // The measured reference divergence (project_facts.md): MojoShader drops the explicit LOD
        // in a pixel shader and samples with a plain texture2D. ShadowDusk honors the LOD, as it
        // already did for Texture2D.SampleLevel. Pinned so a change on either side is noticed.
        string mgfxcGlsl = Encoding.ASCII.GetString(golden.Shaders.Single().Bytecode);
        mgfxcGlsl.ShouldNotContain("texture2DLod", Case.Sensitive);
        mgfxcGlsl.ShouldContain("texture2DProj(ps_s0,", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_VertexStageFetch_IsSD0210_AsMgfxcRefusesIt()
    {
        // mgfxc 3.8.4.1 /Profile:OpenGL: "invalid DCL register type for this shader model".
        const string source =
            "sampler2D S;\n" +
            "float4 VS(float4 p : POSITION0, inout float4 c : COLOR0) : POSITION0 { c = tex2Dlod(S, float4(0.5, 0.5, 0, 0)); return p; }\n" +
            "float4 PS(float4 c : COLOR0) : COLOR0 { return c; }\n" +
            "technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }\n";

        var result = await CompileAsync(source, PlatformTarget.OpenGL, "vtf.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain(e => e.Code == "SD0210");
        result.Error.ShouldNotContain(e => e.Code == "FX0012");
    }

    // ---- Through the legacy-sampler recovery: a call inside a macro body -----------------------

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task CallInAMacroBody_CompilesOnEveryTarget(PlatformTarget target)
    {
        // The raw pre-parse cannot see a call inside '#define'; the recovery re-parses the
        // preprocessed source, where it is an ordinary call (before 2026-10-28 that re-parse
        // was the FX0012).
        const string source =
            Header +
            "#define SAMPLE_LOD(s, uv, lod) tex2Dlod(s, float4(uv, 0, lod))\n" +
            "sampler2D SpriteSampler;\n" +
            "float4 MainPS(float4 position : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0\n" +
            "{ return SAMPLE_LOD(SpriteSampler, uv, 1) * color; }\n" +
            "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

        var result = await CompileAsync(source, target, "macro.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        MgfxBlobReader.Parse(result.Value.Data).Samplers.ShouldNotBeEmpty();
    }

    // ---- A compiler error inside the arguments keeps its line ---------------------------------

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task ErrorInsideTheCoordinate_IsReportedOnItsOwnLine(PlatformTarget target)
    {
        // The helper sits on the function's first line and the call's arguments are copied
        // verbatim, so an error in the coordinate keeps the author's line number.
        const string source =
            "#define PS_SHADERMODEL ps_4_0\n" +                                          // 1
            "sampler2D S;\n" +                                                            // 2
            "float4 MainPS(float2 uv : TEXCOORD0) : COLOR0\n" +                           // 3
            "{\n" +                                                                       // 4
            "    float4 c = tex2Dlod(S,\n" +                                              // 5
            "        float4(uv, 0, notDeclared));\n" +                                    // 6
            "    return c;\n" +                                                           // 7
            "}\n" +
            "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

        var result = await CompileAsync(source, target, "err.fx");

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.First(e => e.Severity == ShaderErrorSeverity.Error);
        error.Line.ShouldBe(6, error.FxcFormattedMessage);
        error.Message.ShouldContain("notDeclared", Case.Sensitive);
    }
}
