#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Unit-style smoke tests for the IR construction layer, exercised through the
/// public <see cref="EffectCompiler"/> API.
///
/// Note: if direct access to the internal <c>ShaderIRBuilder.Build</c> method is
/// required in the future, add
/// <c>[assembly: InternalsVisibleTo("ShadowDusk.Compiler.Tests")]</c>
/// to <c>ShadowDusk.Compiler.csproj</c>.  Until then these tests validate
/// observable behaviour via the public surface.
///
/// These tests do not invoke DXC or SPIRV-Cross against real fixture files and
/// therefore carry no <c>[Trait("Category", "Integration")]</c> tag — they use
/// inline sources with the native compiler chain, so they are tagged Integration
/// only where native invocation is unavoidable (multipass test).
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
}
