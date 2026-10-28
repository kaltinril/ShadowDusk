#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #425: an OpenGL compile failed when HLSL's cbuffer packing put a member somewhere
/// std140 cannot. DXC laid the uniform block out with <c>-fvk-use-dx-layout</c>, which packs a
/// <c>float3</c> (or <c>float2</c>) declared after a <c>float</c> into the same 16-byte register
/// at byte 4; SPIRV-Cross's <c>#version 140</c> GLSL can express only std140, so it refused the
/// block (<c>SD0100</c>, "cannot be expressed as any of std430, std140, scalar"). A member that
/// packed into an array's last register came out as an enhanced-layouts <c>layout(offset = N)</c>
/// member instead, which the rewriter refused (<c>SD0210</c>). The OpenGL compile now lays the
/// block out std140 (<c>-fvk-use-gl-layout</c>).
///
/// <para>The SPIR-V byte offsets never reach a MonoGame GL effect: the rewriter replaces the block
/// with MojoShader's <c>ps_uniforms_vec4[]</c>, one register per member in declaration order (the
/// allocation mgfxc's D3D9 constant table gives), and the cbuffer record comes from that same
/// layout. These tests pin that register layout for each formerly failing shape, the
/// GLSL that reads it, the KNIFX container, and that the pure-managed SPIR-V reflection
/// (the browser and Android host) still gives the desktop's bytes.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class Issue425GlUniformPackingTests
{
    /// <summary>One formerly failing declaration list, its use, and the register each member gets.</summary>
    public sealed record Shape(string Declarations, string Use, (string Name, int Register)[] Registers, string[] GlslReads);

    private static readonly Dictionary<string, Shape> s_pixelShapes = new()
    {
        // The minimal form: a float3 packed behind a float at byte 4 (was SD0100).
        ["Float3AfterFloat"] = new(
            "float Gain;\nfloat3 Axis;\nfloat4 Tail;",
            "float4(Axis * Gain, 1) + Tail",
            [("Gain", 0), ("Axis", 1), ("Tail", 2)],
            ["ps_uniforms_vec4[1].xyz", "ps_uniforms_vec4[0].x", "ps_uniforms_vec4[2]"]),

        // The shape the issue was reported with: a float3 ahead of a float4x4, behind a scalar.
        ["Float3BeforeFloat4x4"] = new(
            "float4 Tint;\nfloat Gain;\nfloat3 Axis;\nfloat4x4 World;\nfloat4 NoDefault;",
            "mul(NoDefault, World) + Tint * Gain + float4(Axis, 0)",
            [("Tint", 0), ("Gain", 1), ("Axis", 2), ("World", 3), ("NoDefault", 7)],
            ["ps_uniforms_vec4[2].xyz", "ps_uniforms_vec4[3]", "ps_uniforms_vec4[6]", "ps_uniforms_vec4[7]"]),

        // The same packing with a float2 (was SD0100).
        ["Float2AfterFloat"] = new(
            "float Gain;\nfloat2 Offset;",
            "float4(Offset * Gain, 0, 1)",
            [("Gain", 0), ("Offset", 1)],
            ["ps_uniforms_vec4[1].xy"]),

        // A named cbuffer packs the same way as loose globals (was SD0100).
        ["NamedCbuffer"] = new(
            "cbuffer Params\n{\n    float Gain;\n    float3 Axis;\n};",
            "float4(Axis * Gain, 1)",
            [("Gain", 0), ("Axis", 1)],
            ["ps_uniforms_vec4[1].xyz"]),

        // A scalar packed into an array's last register (was SD0210: `layout(offset = 0)`).
        ["ScalarAfterArray"] = new(
            "float Weights[2];\nfloat Bias;",
            "float4(Weights[0], Weights[1], Bias, 1)",
            [("Weights", 0), ("Bias", 2)],
            ["ps_uniforms_vec4[0].x", "ps_uniforms_vec4[1].x", "ps_uniforms_vec4[2].x"]),
    };

    public static TheoryData<string> PixelShapes() => new(s_pixelShapes.Keys);

    private static string PixelSource(Shape shape) =>
        shape.Declarations + "\n" +
        "float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0) : COLOR0\n" +
        "{\n    return " + shape.Use + ";\n}\n" +
        "technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }\n";

    // The vertex stage reads the same packing through vs_uniforms_vec4[] (was SD0100).
    private const string VertexSource = """
        float Gain;
        float3 Axis;
        float4x4 WorldViewProj;

        struct VSOut { float4 Pos : POSITION0; float4 Col : COLOR0; };

        VSOut MainVS(float4 pos : POSITION0)
        {
            VSOut o;
            o.Pos = mul(pos, WorldViewProj);
            o.Col = float4(Axis * Gain, 1);
            return o;
        }

        float4 MainPS(VSOut i) : COLOR0 { return i.Col; }

        technique T { pass P { VertexShader = compile vs_3_0 MainVS(); PixelShader = compile ps_3_0 MainPS(); } }
        """;

    private static async Task<byte[]> CompileAsync(
        string source,
        EffectContainer container = EffectContainer.Mgfx,
        Func<IShaderReflector>? reflectorFactory = null)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var compiler = new EffectCompiler(reflectorFactory: reflectorFactory);
        var result = await compiler.CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            Container = container,
            SourceFileName = "Issue425.fx",
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
            : "must compile");
        return result.Value.Data;
    }

    private static void AssertRegisters(MgfxBlobReader reader, string cbufferName, (string Name, int Register)[] expected)
    {
        MgfxConstantBufferRecord record = reader.ConstantBuffers.Single(cb => cb.Name == cbufferName);
        var actual = record.ParameterIndices
            .Select((index, i) => (reader.ParameterNames[index], record.ParameterOffsets[i] / 16))
            .ToArray();
        actual.ShouldBe(expected, $"{cbufferName}: one register per member, in declaration order");
    }

    [Theory]
    [MemberData(nameof(PixelShapes))]
    public async Task PackedPixelUniforms_CompileForOpenGl_OneRegisterPerMember(string name)
    {
        Shape shape = s_pixelShapes[name];
        MgfxBlobReader reader = MgfxBlobReader.Parse(await CompileAsync(PixelSource(shape)));

        AssertRegisters(reader, "ps_uniforms_vec4", shape.Registers);

        string glsl = Encoding.UTF8.GetString(reader.Shaders.Single(s => !s.IsVertex).Bytecode);
        // Every shape ends with a one-register member, so the array ends one past it.
        int registers = shape.Registers.Max(r => r.Register) + 1;
        glsl.ShouldContain($"uniform vec4 ps_uniforms_vec4[{registers}];", Case.Sensitive);
        foreach (string read in shape.GlslReads)
            glsl.ShouldContain(read, Case.Sensitive);
        glsl.ShouldNotContain("layout(", Case.Sensitive);
    }

    [Theory]
    [MemberData(nameof(PixelShapes))]
    public async Task PackedPixelUniforms_CompileForKnifx(string name)
    {
        byte[] knifx = await CompileAsync(PixelSource(s_pixelShapes[name]), EffectContainer.Knifx);
        knifx.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task PackedVertexUniforms_CompileForOpenGl_OneRegisterPerMember()
    {
        MgfxBlobReader reader = MgfxBlobReader.Parse(await CompileAsync(VertexSource));

        AssertRegisters(reader, "vs_uniforms_vec4", [("Gain", 0), ("Axis", 1), ("WorldViewProj", 2)]);

        string glsl = Encoding.UTF8.GetString(reader.Shaders.Single(s => s.IsVertex).Bytecode);
        glsl.ShouldContain("uniform vec4 vs_uniforms_vec4[6];", Case.Sensitive);
        glsl.ShouldContain("vs_uniforms_vec4[1].xyz * vs_uniforms_vec4[0].x", Case.Sensitive);
    }

    /// <summary>
    /// The browser and Android hosts reflect from the SPIR-V (<see cref="SpirvReflector"/>), whose
    /// offsets are now std140; the desktop reflects the DXIL. The GL output must not depend on
    /// which, so the bytes must be the same.
    /// </summary>
    [Theory]
    [MemberData(nameof(PixelShapes))]
    public async Task PackedPixelUniforms_SpirvReflection_GivesTheDesktopBytes(string name)
    {
        string source = PixelSource(s_pixelShapes[name]);
        byte[] desktop = await CompileAsync(source);
        byte[] spirvReflected = await CompileAsync(source, reflectorFactory: () => new SpirvReflector());
        spirvReflected.ShouldBe(desktop);
    }

    /// <summary>
    /// The fixture the real-engine <c>validation/InitializerDefaults*</c> drivers render for this issue
    /// (no <c>Set</c> call, so its defaults are what the pixel reads): it compiles on OpenGL, keeps
    /// one register per member, and carries each default.
    /// </summary>
    [Fact]
    public async Task RenderProbeFixture_CompilesForOpenGl_WithItsDefaults()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "initializer-defaults", "G_PackedFloat3.fx");
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(path, cts.Token), new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = path,
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
            : "must compile");

        AssertRegisters(MgfxBlobReader.Parse(result.Value.Data), "ps_uniforms_vec4", [("Gain", 0), ("Axis", 1), ("World", 2)]);
        result.Value.Parameters.Single(p => p.Name == "Gain").DefaultValue.ShouldBe(new[] { 0.5f });
        result.Value.Parameters.Single(p => p.Name == "Axis").DefaultValue.ShouldBe(new[] { 0.25f, 0.5f, 1f });
    }

    [Fact]
    public async Task PackedVertexUniforms_SpirvReflection_GivesTheDesktopBytes()
    {
        byte[] desktop = await CompileAsync(VertexSource);
        byte[] spirvReflected = await CompileAsync(VertexSource, reflectorFactory: () => new SpirvReflector());
        spirvReflected.ShouldBe(desktop);
    }
}
