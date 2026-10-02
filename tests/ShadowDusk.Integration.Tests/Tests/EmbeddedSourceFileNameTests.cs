#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The library contract behind issue #274: an MGFX v11 container stores a source-file string
/// per shader, and <see cref="CompilerOptions.EmbeddedSourceFileName"/> decides what it is
/// <b>independently</b> of <see cref="CompilerOptions.SourceFileName"/>, which keeps feeding
/// diagnostics and <c>#include</c> resolution.
///
/// <para>The default is pinned as deliberately as the override: with nothing set, the string is
/// <see cref="CompilerOptions.SourceFileName"/> exactly as passed, which is what the
/// <c>mgfxc</c> CLI writes for its own source argument and what the ShadowDusk CLI relies on.
/// A change to that default is a change to CLI output.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmbeddedSourceFileNameTests
{
    private const string Shader = """
        Texture2D SpriteTexture;
        SamplerState SpriteTextureSampler;

        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

        VOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
        {
            VOut o;
            o.Pos = pos;
            o.UV = uv;
            return o;
        }

        float4 PS(VOut i) : SV_Target0
        {
            return SpriteTexture.Sample(SpriteTextureSampler, i.UV);
        }

        technique T
        {
            pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); }
        }
        """;

    private const string HostPath = "/home/somebody/checkout/Content/Effects/Thing.fx";

    public static TheoryData<PlatformTarget> V11Targets => new()
    {
        PlatformTarget.Vulkan,
        PlatformTarget.DirectX12,
    };

    [Theory]
    [MemberData(nameof(V11Targets))]
    public void Default_EmbedsSourceFileNameExactlyAsPassed(PlatformTarget target)
    {
        byte[] bytes = Compile(new CompilerOptions { Target = target, SourceFileName = HostPath });

        SourceFiles(bytes).ShouldAllBe(
            name => name == HostPath,
            "with EmbeddedSourceFileName unset the container records SourceFileName as passed (mgfxc CLI parity)");
    }

    [Theory]
    [MemberData(nameof(V11Targets))]
    public void Default_WithNoSourceFileName_EmbedsUnknown(PlatformTarget target)
    {
        byte[] bytes = Compile(new CompilerOptions { Target = target });

        SourceFiles(bytes).ShouldAllBe(name => name == "<unknown>");
    }

    [Theory]
    [MemberData(nameof(V11Targets))]
    public void EmbeddedSourceFileName_IsWrittenVerbatim_AndMakesTheOutputIndependentOfSourceFileName(
        PlatformTarget target)
    {
        byte[] fromOnePath = Compile(new CompilerOptions
        {
            Target                 = target,
            SourceFileName         = HostPath,
            EmbeddedSourceFileName = "<unknown>",
        });
        byte[] fromAnotherPath = Compile(new CompilerOptions
        {
            Target                 = target,
            SourceFileName         = @"D:\a\different\machine\Thing.fx",
            EmbeddedSourceFileName = "<unknown>",
        });

        SourceFiles(fromOnePath).ShouldAllBe(name => name == "<unknown>");
        fromAnotherPath.ShouldBe(
            fromOnePath,
            "once the embedded name is fixed, SourceFileName must no longer reach the output bytes");

        // And it equals a compile that never had a path to begin with: the override hides the
        // path, it does not add anything of its own.
        fromOnePath.ShouldBe(Compile(new CompilerOptions { Target = target }));
    }

    [Theory]
    [MemberData(nameof(V11Targets))]
    public void EmbeddedSourceFileName_AcceptsAnyString(PlatformTarget target)
    {
        byte[] bytes = Compile(new CompilerOptions
        {
            Target                 = target,
            SourceFileName         = HostPath,
            EmbeddedSourceFileName = "Effects/Thing.fx",
        });

        SourceFiles(bytes).ShouldAllBe(name => name == "Effects/Thing.fx");
    }

    /// <summary>
    /// The half the fix must not break: a diagnostic still names
    /// <see cref="CompilerOptions.SourceFileName"/>, at the exact line and column, however the
    /// embedded name is set.
    /// </summary>
    [Theory]
    [MemberData(nameof(V11Targets))]
    public void EmbeddedSourceFileName_DoesNotChangeDiagnostics(PlatformTarget target)
    {
        // Line 3, column 12: the `n` of `notADeclaredThing` (four spaces, then `return `).
        const string broken =
            "float4 PS(float4 pos : SV_POSITION) : SV_Target0\n" +
            "{\n" +
            "    return notADeclaredThing;\n" +
            "}\n" +
            "technique T { pass P { PixelShader = compile ps_6_0 PS(); } }\n";

        var result = new EffectCompiler().Compile(broken, new CompilerOptions
        {
            Target                 = target,
            SourceFileName         = HostPath,
            EmbeddedSourceFileName = "<unknown>",
        });

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.First(e => e.Message.Contains("notADeclaredThing", StringComparison.Ordinal));
        error.File.ShouldBe(HostPath);
        error.Line.ShouldBe(3);
        error.Column.ShouldBe(12);
        result.Error.ShouldAllBe(e => e.File != "<unknown>");
    }

    /// <summary>
    /// MGFX v10 has no source-file field, so the option cannot move a byte of the default
    /// OpenGL / DirectX 11 output a current consumer relies on.
    /// </summary>
    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public void EmbeddedSourceFileName_DoesNotTouchV10Output(PlatformTarget target)
    {
        string fx = File.ReadAllText(TestHelpers.FixturePath("Grayscale.fx"));

        byte[] without = Compile(new CompilerOptions { Target = target, SourceFileName = "Grayscale.fx" }, fx);
        byte[] with    = Compile(
            new CompilerOptions { Target = target, SourceFileName = "Grayscale.fx", EmbeddedSourceFileName = "<unknown>" },
            fx);

        with.ShouldBe(without);
        MgfxBlobReader.Parse(with).MgfxVersion.ShouldBe((byte)10);
    }

    private static byte[] Compile(CompilerOptions options, string source = Shader)
    {
        var result = new EffectCompiler().Compile(source, options);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value.Data;
    }

    private static IReadOnlyList<string?> SourceFiles(byte[] mgfx)
    {
        var reader = MgfxBlobReader.Parse(mgfx);
        reader.MgfxVersion.ShouldBe((byte)11);
        reader.Shaders.Count.ShouldBe(2, "one vertex + one pixel shader record");
        return reader.Shaders.Select(s => s.SourceFile).ToList();
    }
}
