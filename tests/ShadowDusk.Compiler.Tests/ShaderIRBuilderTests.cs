#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Tests for the IR construction layer: smoke tests through the public
/// <see cref="EffectCompiler"/> API, plus direct unit tests of the internal
/// <c>ShaderIRBuilder.Build</c> (Phase 8 items closed by Phase 27;
/// <c>ShadowDusk.Compiler.csproj</c> grants
/// <c>InternalsVisibleTo("ShadowDusk.Compiler.Tests")</c>).
///
/// The direct tests are pure (no DXC, no SPIRV-Cross, no disk) and carry no
/// <c>[Trait("Category", "Integration")]</c> tag; the multipass test drives the
/// full native compile pipeline and is tagged Integration.
/// </summary>
public sealed class ShaderIRBuilderTests
{
    // ---------------------------------------------------------------------------
    // Build_EmptySource_ReturnsParseError
    // Pure: no technique → the compiler has nothing to build; must return a
    // failure result without invoking any native binaries.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Build_EmptySource_ReturnsParseError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var compiler = new EffectCompiler();
        var result   = await compiler.CompileAsync(
            string.Empty,
            new CompilerOptions { Target = PlatformTarget.OpenGL },
            cts.Token);

        result.IsFailure.Should().BeTrue(
            because: "an empty source string contains no techniques and must be rejected");
        result.Error.Should().NotBeEmpty(
            because: "at least one diagnostic must be reported when the source is empty");
    }

    // ---------------------------------------------------------------------------
    // Build_MultiPass_PreservesPassOrder
    // Tagged Integration because it drives the full compile pipeline to verify
    // that IR construction preserves the declared order of passes within a
    // technique.
    // ---------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "OpenGL")]
    public async Task Build_MultiPass_PreservesPassOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Inline multipass source: one technique, two named passes.
        // This mirrors the structure of tests/fixtures/shaders/multipass.fx.
        const string source = """
            struct VertexInput  { float4 Position : POSITION; float4 Color : COLOR0; };
            struct PixelInput   { float4 Position : SV_POSITION; float4 Color : COLOR0; };

            PixelInput VS(VertexInput input)
            {
                PixelInput output;
                output.Position = input.Position;
                output.Color    = input.Color;
                return output;
            }

            float4 PS_Opaque(PixelInput input) : SV_TARGET
            {
                return input.Color;
            }

            float4 PS_Transparent(PixelInput input) : SV_TARGET
            {
                return float4(input.Color.rgb, 0.5);
            }

            technique Technique1
            {
                pass Opaque
                {
                    VertexShader = compile vs_4_0 VS();
                    PixelShader  = compile ps_4_0 PS_Opaque();
                }
                pass Transparent
                {
                    VertexShader = compile vs_4_0 VS();
                    PixelShader  = compile ps_4_0 PS_Transparent();
                }
            }
            """;

        var compiler = new EffectCompiler();
        var result   = await compiler.CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL },
            cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure
                ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage))
                : "multipass compilation must succeed");

        result.Value.Data.Should().NotBeEmpty(
            because: "a multipass effect must produce a non-empty output blob");
    }

    // ---------------------------------------------------------------------------
    // Direct ShaderIRBuilder.Build unit tests (Phase 8, closed by Phase 27).
    // Pure: hand-built inputs, no native invocation.
    // ---------------------------------------------------------------------------

    private static CompiledShaderBlob Blob(ShaderStage stage) =>
        new(Bytes: [0x01, 0x02, 0x03], Stage: stage);

    private static MgfxPassInfo Pass(string name, int vsIndex, int psIndex,
        IReadOnlyList<AnnotationInfo>? annotations = null) =>
        new(
            Name: name,
            Annotations: annotations ?? [],
            VertexShaderIndex: vsIndex,
            PixelShaderIndex: psIndex,
            RenderState: new RenderStateBlock());

    [Fact]
    public void Build_ShaderIndicesAreZeroBased()
    {
        // A two-pass technique over four shader blobs: pass 0 references VS=0/PS=1,
        // pass 1 references VS=2/PS=3. Build must preserve the zero-based indices
        // verbatim — MgfxWriter writes them directly into the .mgfx pass records,
        // where MonoGame's EffectReader indexes the shader list with them.
        var blobs = new[]
        {
            Blob(ShaderStage.Vertex), Blob(ShaderStage.Pixel),
            Blob(ShaderStage.Vertex), Blob(ShaderStage.Pixel),
        };
        var technique = new MgfxTechniqueInfo(
            Name: "Technique1",
            Annotations: [],
            Passes: [Pass("Pass0", vsIndex: 0, psIndex: 1), Pass("Pass1", vsIndex: 2, psIndex: 3)]);

        ShaderIR ir = ShaderIRBuilder.Build(blobs, [technique], [], []);

        ir.Shaders.Should().HaveCount(4);
        ir.Techniques.Should().ContainSingle().Which.Passes.Should().HaveCount(2);

        MgfxPassInfo pass0 = ir.Techniques[0].Passes[0];
        MgfxPassInfo pass1 = ir.Techniques[0].Passes[1];

        pass0.VertexShaderIndex.Should().Be(0, because: "shader indices are zero-based");
        pass0.PixelShaderIndex.Should().Be(1);
        pass1.VertexShaderIndex.Should().Be(2);
        pass1.PixelShaderIndex.Should().Be(3);

        foreach (MgfxPassInfo pass in ir.Techniques[0].Passes)
        {
            pass.VertexShaderIndex.Should().BeInRange(0, ir.Shaders.Count - 1);
            pass.PixelShaderIndex.Should().BeInRange(0, ir.Shaders.Count - 1);
            ir.Shaders[pass.VertexShaderIndex].Stage.Should().Be(ShaderStage.Vertex);
            ir.Shaders[pass.PixelShaderIndex].Stage.Should().Be(ShaderStage.Pixel);
        }
    }

    [Fact]
    public void Build_EmptyAnnotationsAllowed()
    {
        // A pass (and technique) with no annotations must build without throwing
        // and surface empty — never null — annotation lists.
        var technique = new MgfxTechniqueInfo(
            Name: "Technique1",
            Annotations: [],
            Passes: [Pass("Pass0", vsIndex: 0, psIndex: 1, annotations: [])]);

        var act = () => ShaderIRBuilder.Build(
            [Blob(ShaderStage.Vertex), Blob(ShaderStage.Pixel)], [technique], [], []);

        ShaderIR ir = act.Should().NotThrow().Subject;
        ir.Techniques[0].Annotations.Should().NotBeNull().And.BeEmpty();
        ir.Techniques[0].Passes[0].Annotations.Should().NotBeNull().And.BeEmpty();
        ir.Parameters.Should().BeEmpty();
        ir.ConstantBuffers.Should().BeEmpty();
    }
}
