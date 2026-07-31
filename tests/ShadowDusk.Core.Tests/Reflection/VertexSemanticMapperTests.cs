#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Pins the shared HLSL-semantic → MonoGame <c>VertexElementUsage</c> mapping. Both the
/// SPIR-V (Vulkan) and DXIL (DirectX 12) attribute-table builders go through this type, so
/// a regression here is a wrong vertex layout on two rung-4 targets at once — and it fails
/// at the consumer's first Draw, not at compile time.
///
/// <para>Bug-hunt 2026-07-27 N5 landed the PSIZE alias and the numeric-suffix overflow
/// guard with no test; these are that missing guard.</para>
/// </summary>
public sealed class VertexSemanticMapperTests
{
    [Theory]
    [InlineData("POSITION",  0, 0)]
    [InlineData("POSITION0", 0, 0)]
    [InlineData("POSITION1", 0, 1)]
    [InlineData("SV_POSITION", 0, 0)]
    [InlineData("COLOR",     1, 0)]
    [InlineData("COLOR1",    1, 1)]
    [InlineData("TEXCOORD",  2, 0)]
    [InlineData("TEXCOORD3", 2, 3)]
    [InlineData("NORMAL",    3, 0)]
    [InlineData("BINORMAL",  4, 0)]
    [InlineData("TANGENT",   5, 0)]
    [InlineData("BLENDINDICES", 6, 0)]
    [InlineData("BLENDWEIGHT",  7, 0)]
    [InlineData("DEPTH",     8, 0)]
    [InlineData("FOG",       9, 0)]
    [InlineData("TESSELLATEFACTOR", 12, 0)]
    public void Map_KnownSemantics(string semantic, byte usage, int index)
        => VertexSemanticMapper.Map(semantic).ShouldBe((usage, index));

    [Theory]
    [InlineData("PSIZE")]
    [InlineData("PSIZE0")]
    [InlineData("POINTSIZE")]
    [InlineData("POINTSIZE0")]
    public void Map_PointSizeSpellings_MapToPointSizeNotTextureCoordinate(string semantic)
    {
        // PSIZE is the real D3D9-era HLSL spelling. It used to fall through to the
        // TextureCoordinate default, where it collided with a genuine TEXCOORD0 attribute
        // and silently produced a wrong vertex layout.
        VertexSemanticMapper.Map(semantic).ShouldBe(((byte)10, 0));
    }

    [Fact]
    public void Map_IsCaseInsensitive()
        => VertexSemanticMapper.Map("TexCoord2").ShouldBe(((byte)2, 2));

    [Fact]
    public void Map_UnknownSemantic_FallsBackToTextureCoordinate()
    {
        // mgfxc's own default for a semantic it does not model.
        VertexSemanticMapper.Map("POSITIONT").ShouldBe(((byte)2, 0));
        VertexSemanticMapper.Map("MYTHING7").ShouldBe(((byte)2, 7));
    }

    [Fact]
    public void Map_AbsurdNumericSuffix_DoesNotThrow()
    {
        // A 40-digit index cannot fit an int. This mapper is pure and is called from the
        // middle of a compile, so it must degrade rather than throw OverflowException out
        // of the pipeline as an unhandled internal error.
        string semantic = "TEXCOORD" + new string('9', 40);

        var act = () => VertexSemanticMapper.Map(semantic);

        Should.NotThrow(act);
    }

    // ---- The recognised/unrecognised report (bug-hunt 2026-07-27 N5, warning half) ----

    [Theory]
    [InlineData("POSITION")]
    [InlineData("POSITION1")]
    [InlineData("SV_POSITION")]
    [InlineData("TEXCOORD3")]
    [InlineData("TexCoord2")]
    [InlineData("PSIZE")]
    [InlineData("TESSELLATEFACTOR")]
    public void Map_KnownSemantic_ReportsRecognized(string semantic)
    {
        VertexSemanticMapper.Map(semantic, out bool recognized);

        recognized.ShouldBeTrue($"'{semantic}' is in the table, so no SD0104 warning is owed");
    }

    [Theory]
    [InlineData("TEXCORD0")]   // the typo the warning exists for
    [InlineData("POSITIONT")]
    [InlineData("MYTHING7")]
    public void Map_UnknownSemantic_ReportsUnrecognized(string semantic)
    {
        (byte usage, _) = VertexSemanticMapper.Map(semantic, out bool recognized);

        recognized.ShouldBeFalse($"'{semantic}' took the TextureCoordinate default and mgfxc warns here");
        usage.ShouldBe((byte)2, "the fallback VALUE must not move — mgfxc defaults the same way");
    }

    [Fact]
    public void Map_AbsurdNumericSuffix_ReportsUnrecognized()
    {
        // The table name matches but the index cannot be parsed, so the mapper takes the
        // unknown-semantic path. Reporting it as recognised would hide a genuinely
        // unusable semantic behind a silently-defaulted attribute.
        VertexSemanticMapper.Map("TEXCOORD" + new string('9', 40), out bool recognized);

        recognized.ShouldBeFalse();
    }

    [Fact]
    public void Map_WithAndWithoutTheFlag_AgreeOnEveryValue()
    {
        // The two overloads MUST be the same function: the warning half of N5 must not
        // move a single emitted usage/index byte.
        string[] semantics =
        [
            "POSITION", "POSITION1", "SV_POSITION", "COLOR", "COLOR1", "TEXCOORD", "TEXCOORD3",
            "NORMAL", "BINORMAL", "TANGENT", "BLENDINDICES", "BLENDWEIGHT", "DEPTH", "FOG",
            "PSIZE", "POINTSIZE", "TESSELLATEFACTOR", "TexCoord2", "TEXCORD0", "POSITIONT",
            "MYTHING7", "TEXCOORD" + new string('9', 40),
        ];

        foreach (string semantic in semantics)
            VertexSemanticMapper.Map(semantic, out _).ShouldBe(VertexSemanticMapper.Map(semantic));
    }

    [Fact]
    public void UnrecognizedSemanticWarning_IsAWarningWithTheRegisteredCode()
    {
        // mgfxc accepts and defaults, so drop-in parity forbids making this an error.
        ShaderError warning = VertexSemanticMapper.UnrecognizedSemanticWarning("TEXCORD0", 0, "Typo.fx");

        warning.Code.ShouldBe("SD0104");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.File.ShouldBe("Typo.fx");
        warning.Message.ShouldContain("TEXCORD0", Case.Sensitive);
        warning.Message.ShouldContain("TextureCoordinate", Case.Sensitive);
        warning.Message.ShouldContain("mgfxc", Case.Sensitive);
    }

    [Fact]
    public void UnrecognizedSemanticWarning_NamesTheFallbackIndex()
    {
        VertexSemanticMapper.UnrecognizedSemanticWarning("MYTHING7", 7)
            .Message.ShouldContain("index 7", Case.Sensitive);
    }
}
