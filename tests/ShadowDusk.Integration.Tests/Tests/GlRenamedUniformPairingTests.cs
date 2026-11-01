#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issues #438 and #440: SPIRV-Cross renames a uniform GLSL cannot take (<c>noise</c> to
/// <c>_noise</c>, <c>input</c> to <c>_input_1</c> beside a real <c>_input</c>), and the GL
/// cbuffer join keyed on that spelling. #438: the renamed PS <c>noise</c> joined the VS's real
/// <c>_noise</c> parameter, so the PS record was <c>[_noise@0 c@1 noise@2]</c> and
/// <c>SetValue</c> on <c>noise</c> reached only a dead phantom register (silent). #440: the
/// <c>_input_1</c> spelling joined nothing and the compile failed with an internal SD0012. The
/// layout is now paired with DXC's SPIR-V member names (the HLSL names) by position, as the SkSL
/// and raylib converters already did (issue #435).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlRenamedUniformPairingTests
{
    // Issue #438's repro: a VS-only cbuffer declares a real `_noise`; the PS reads `noise` and `c`.
    private const string CrossStageSource = """
        cbuffer VSOnly { float4 _noise; };
        float4 noise;
        float4 c;
        float4 VS(float4 p : POSITION0) : POSITION0 { return p + _noise; }
        float4 PS() : COLOR0 { return noise + c; }
        technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
        """;

    [Fact]
    public async Task RenamedPixelUniform_DoesNotBindToTheVertexStagesSameSpelledParameter()
    {
        byte[] desktop = await CompileAsync(CrossStageSource, null);
        byte[] spirvReflected = await CompileAsync(CrossStageSource, () => new SpirvReflector());

        foreach (byte[] bytes in new[] { desktop, spirvReflected })
        {
            MgfxBlobReader reader = MgfxBlobReader.Parse(bytes);
            Records(reader, "ps_uniforms_vec4").ShouldBe([("noise", 0), ("c", 1)]);
            Records(reader, "vs_uniforms_vec4").ShouldBe([("_noise", 0)]);
            // No phantom register for `noise`: the PS array holds exactly its two members.
            string ps = Encoding.UTF8.GetString(reader.Shaders.Single(s => !s.IsVertex).Bytecode);
            ps.ShouldContain("uniform vec4 ps_uniforms_vec4[2];", Case.Sensitive);
        }
        spirvReflected.ShouldBe(desktop);
    }

    [Theory]
    // Issue #440's report: both loose globals (the declaration-order bridge also maps this one).
    [InlineData("float _input = 0.25;\nfloat input = 0.5;")]
    // The twins in two blocks: SPIRV-Cross spells the second `_input` too, so the name join bound
    // both registers to `_input` and `input` got only a phantom (silent), and the bridge, which
    // proves one block only, could not help.
    [InlineData("float _input = 0.25;\ncbuffer B { float input = 0.5; };")]
    public async Task ReservedWordBesideItsUnderscoredSpelling_CompilesWithItsDefaults(string declarations)
    {
        string source = declarations + """

            float4 PS() : COLOR0 { return float4(_input, input, 0, 1); }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        foreach (Func<IShaderReflector>? factory in new Func<IShaderReflector>?[] { null, () => new SpirvReflector() })
        {
            var result = await CompileResultAsync(source, factory);
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
            MgfxBlobReader reader = MgfxBlobReader.Parse(result.Value.Data);
            Records(reader, "ps_uniforms_vec4").ShouldBe([("_input", 0), ("input", 1)]);
            result.Value.Parameters.Single(p => p.Name == "_input").DefaultValue.ShouldBe([0.25f]);
            result.Value.Parameters.Single(p => p.Name == "input").DefaultValue.ShouldBe([0.5f]);
        }
    }

    private static (string Name, int Register)[] Records(MgfxBlobReader reader, string cbufferName)
    {
        MgfxConstantBufferRecord record = reader.ConstantBuffers.Single(cb => cb.Name == cbufferName);
        return record.ParameterIndices
            .Select((index, i) => (reader.ParameterNames[index], record.ParameterOffsets[i] / 16))
            .ToArray();
    }

    private static async Task<byte[]> CompileAsync(string source, Func<IShaderReflector>? reflectorFactory)
    {
        var result = await CompileResultAsync(source, reflectorFactory);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value.Data;
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileResultAsync(
        string source, Func<IShaderReflector>? reflectorFactory)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        return await new EffectCompiler(reflectorFactory: reflectorFactory).CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Renamed.fx",
        }, cts.Token);
    }
}
