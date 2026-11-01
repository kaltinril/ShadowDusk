#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #446: a vertex shader OUTPUT declared <c>: POSITION</c> / <c>: POSITION0</c> (the XNA and
/// MonoGame spelling, which fxc and vkd3d treat as <c>SV_Position</c> on SM4+) must be the
/// system-value position on the two DXC targets too. Before the fix DXC compiled it as a user
/// output: the DirectX 12 vertex shader had no <c>SV_Position</c> (MonoGame 3.8.5 WindowsDX12
/// threw creating the pipeline) and the Vulkan one no <c>BuiltIn Position</c> (DesktopVK drew
/// nothing). The vertex INPUT <c>POSITION0</c> must stay the vertex attribute. Render rows:
/// <c>validation/KniEffectTestDx12</c> and <c>KniEffectTestVulkan</c>,
/// <c>VertexTextureTex2Dlod/render</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue446VertexPositionOutputTests
{
    // validation/SharedKniEffectTest/Companion/VertexTextureTex2Dlod.fx, the issue's repro.
    private const string StructOutput = """
        float4x4 MatrixTransform;
        texture Tex;
        sampler TexSampler = sampler_state { Texture = <Tex>; MinFilter = Point; MagFilter = Point; MipFilter = Point; };

        struct VIn  { float4 Pos : POSITION0; float4 Col : COLOR0; float2 UV : TEXCOORD0; };
        struct VOut { float4 Pos : POSITION0; float4 Col : COLOR0; };

        VOut MainVS(VIn i)
        {
            VOut o;
            o.Pos = mul(i.Pos, MatrixTransform);
            o.Col = tex2Dlod(TexSampler, float4(0.5, 0.5, 0, 0));
            return o;
        }

        float4 MainPS(VOut i) : COLOR0 { return i.Col; }

        technique T { pass P { VertexShader = compile vs_4_0 MainVS(); PixelShader = compile ps_4_0 MainPS(); } }
        """;

    // The two non-struct spellings: an `out` parameter, and the function's own return semantic.
    private const string OutParameter = """
        float4x4 M;
        void MainVS(float4 pos : POSITION0, float4 col : COLOR0, out float4 oPos : POSITION0, out float4 oCol : COLOR0)
        { oPos = mul(pos, M); oCol = col; }
        float4 MainPS(float4 col : COLOR0) : COLOR0 { return col; }
        technique T { pass P { VertexShader = compile vs_4_0 MainVS(); PixelShader = compile ps_4_0 MainPS(); } }
        """;

    private const string ReturnSemantic = """
        float4x4 M;
        float4 MainVS(float4 pos : POSITION) : POSITION { return mul(pos, M); }
        float4 MainPS() : COLOR0 { return float4(0, 1, 0, 1); }
        technique T { pass P { VertexShader = compile vs_4_0 MainVS(); PixelShader = compile ps_4_0 MainPS(); } }
        """;

    public static TheoryData<string> Shapes() => new() { nameof(StructOutput), nameof(OutParameter), nameof(ReturnSemantic) };

    private static string Source(string shape) => shape switch
    {
        nameof(StructOutput) => StructOutput,
        nameof(OutParameter) => OutParameter,
        _ => ReturnSemantic,
    };

    // SPIR-V: OpDecorate (71), Decoration BuiltIn (11), BuiltIn Position (0) / FragCoord (15);
    // OpDecorate Location (30).
    private const uint OpDecorate = 71;
    private const uint BuiltInDecoration = 11;
    private const uint BuiltInPosition = 0;
    private const uint BuiltInFragCoord = 15;

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "DirectX12")]
    public async Task DirectX12_VertexOutputPosition_IsSvPosition_AndTheVertexInputIsStillPosition(string shape)
    {
        MgfxBlobReader mgfx = MgfxBlobReader.Parse(await CompileAsync(Source(shape), PlatformTarget.DirectX12));

        MgfxShaderRecord vs = mgfx.Shaders.Single(s => s.IsVertex);
        string text = DxcTargetsCrossHostByteIdentityTests.Disassemble(DirectX12ShaderCodeReader.Parse(vs.Bytecode).Dxil);
        string output = Section(text, "; Output signature:");
        string input = Section(text, "; Input signature:");

        Regex.IsMatch(output, @"^; SV_Position\s+0\s+xyzw\s+\d+\s+POS\b", RegexOptions.Multiline)
            .ShouldBeTrue($"{shape}: the vertex shader must write SV_Position:\n{output}");
        Regex.IsMatch(output, @"^; POSITION\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .ShouldBeFalse($"{shape}: no user POSITION output may remain:\n{output}");
        Regex.IsMatch(input, @"^; POSITION\s+0\s+xyzw\s+\d+\s+NONE\b", RegexOptions.Multiline)
            .ShouldBeTrue($"{shape}: the vertex INPUT must still be the POSITION0 attribute:\n{input}");
        vs.Attributes.ShouldContain(a => a.Usage == 0 && a.Index == 0, $"{shape}: the attribute table still binds VertexElementUsage.Position 0");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "Vulkan")]
    public async Task Vulkan_VertexOutputPosition_IsBuiltInPosition(string shape)
    {
        MgfxBlobReader mgfx = MgfxBlobReader.Parse(await CompileAsync(Source(shape), PlatformTarget.Vulkan));

        uint[] vs = Words(VulkanShaderCodeReader.Parse(mgfx.Shaders.Single(s => s.IsVertex).Bytecode).Spirv);
        HasBuiltIn(vs, BuiltInPosition).ShouldBeTrue($"{shape}: the vertex shader must write the built-in Position");
        mgfx.Shaders.Single(s => s.IsVertex).Attributes
            .ShouldContain(a => a.Usage == 0 && a.Index == 0, $"{shape}: the attribute table still binds VertexElementUsage.Position 0");

    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task Vulkan_APixelShaderReadingTheSharedStruct_ReadsFragCoord()
    {
        // The pixel shader takes the vertex shader's struct, so its Pos is SV_Position too (the
        // pixel position, as on DirectX 11), never a user input the vertex shader no longer writes.
        const string source = """
            float4x4 M;
            struct VOut { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
            VOut MainVS(float4 p : POSITION0, float2 uv : TEXCOORD0) { VOut o; o.Pos = mul(p, M); o.UV = uv; return o; }
            float4 MainPS(VOut i) : COLOR0 { return float4(i.Pos.xy / 1000, i.UV); }
            technique T { pass P { VertexShader = compile vs_4_0 MainVS(); PixelShader = compile ps_4_0 MainPS(); } }
            """;
        MgfxBlobReader mgfx = MgfxBlobReader.Parse(await CompileAsync(source, PlatformTarget.Vulkan));

        HasBuiltIn(Words(VulkanShaderCodeReader.Parse(mgfx.Shaders.Single(s => s.IsVertex).Bytecode).Spirv), BuiltInPosition).ShouldBeTrue();
        HasBuiltIn(Words(VulkanShaderCodeReader.Parse(mgfx.Shaders.Single(s => !s.IsVertex).Bytecode).Spirv), BuiltInFragCoord).ShouldBeTrue();
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public async Task OtherTargets_StillCompileTheSameShape(PlatformTarget target)
    {
        // DirectX 11 maps the semantic in vkd3d and OpenGL in the GLSL rewrite; their bytes are
        // pinned by the cross-host manifests (which this change must not move), so this only proves
        // the shape still compiles there. (StructOutput itself samples a texture in the vertex
        // shader, which the OpenGL target refuses with SD0210.)
        (await CompileAsync(OutParameter, target)).ShouldNotBeEmpty();
        (await CompileAsync(ReturnSemantic, target)).ShouldNotBeEmpty();
    }

    private static async Task<byte[]> CompileAsync(string source, PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = target, SourceFileName = "Issue446.fx" }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value.Data;
    }

    /// <summary>The disassembly lines from <paramref name="header"/> to the next blank comment run.</summary>
    private static string Section(string disassembly, string header)
    {
        int at = disassembly.IndexOf(header, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"no '{header}' in the disassembly");
        int end = disassembly.IndexOf("\n;\n;", at + header.Length + 3, StringComparison.Ordinal);
        return disassembly.Substring(at, (end < 0 ? disassembly.Length : end) - at).Replace("\r", "", StringComparison.Ordinal);
    }

    private static uint[] Words(byte[] spirv)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, words.Length * 4);
        return words;
    }

    private static bool HasBuiltIn(uint[] words, uint builtIn)
    {
        for (int i = 5; i < words.Length;)
        {
            uint count = words[i] >> 16;
            uint op = words[i] & 0xFFFF;
            if (count == 0) return false;
            if (op == OpDecorate && count == 4 && words[i + 2] == BuiltInDecoration && words[i + 3] == builtIn)
                return true;
            i += (int)count;
        }
        return false;
    }
}
