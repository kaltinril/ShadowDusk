#nullable enable

using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Slang;

/// <summary>
/// Pure tests (no disk, no natives) for the Slang frontend — a pure managed text transform:
/// <c>[shader(...)]</c> entry discovery, Slang-only-construct rejection, attribute stripping,
/// and technique synthesis. The body itself is compiled by the same DXC as every <c>.fx</c>,
/// which is what makes <c>.slang</c> input work on every host with nothing extra to ship
/// (owner direction 2026-08-13: the HLSL-compatible subset, no Slang toolchain anywhere).
/// </summary>
public sealed class SlangEntryScannerTests
{
    [Fact]
    public void FindsVertexAndFragmentEntries_ByAttribute()
    {
        var result = SlangEntryScanner.Scan("""
            [shader("vertex")]
            VSOut MainVS(VSIn i) { return o; }

            [shader("fragment")]
            float4 MainPS(VSOut i) : SV_Target { return c; }
            """, "test.slang");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
        result.Value[0].ShouldBe(new SlangEntryPoint("MainVS", SlangStage.Vertex, 1));
        result.Value[1].Name.ShouldBe("MainPS");
        result.Value[1].Stage.ShouldBe(SlangStage.Fragment);
    }

    [Fact]
    public void PixelIsAcceptedAsTheHlslAliasOfFragment()
    {
        var result = SlangEntryScanner.Scan(
            """[shader("pixel")] float4 P() : SV_Target { return 0; }""", "t.slang");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Single().Stage.ShouldBe(SlangStage.Fragment);
    }

    [Fact]
    public void ComputeEntry_IsRejectedLoudly_ByName_WithSD0602()
    {
        // Valid Slang whose stage has nowhere to land in an Effect (Phase 58: stock MonoGame
        // and KNI hold exactly vertex + pixel). The author has every reason to expect it to
        // work, so silence would be the worst outcome.
        var result = SlangEntryScanner.Scan("""
            [shader("compute")]
            void Simulate(uint3 id : SV_DispatchThreadID) { }
            """, "sim.slang");

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0602");
        error.Message.ShouldContain("Simulate", Case.Sensitive);
        error.Message.ShouldContain("compute", Case.Sensitive);
        error.Message.ShouldContain("vertex and pixel", Case.Sensitive);
    }

    [Fact]
    public void NoEntryPoints_IsSD0603_ExplainingTheAttributeConvention()
    {
        var result = SlangEntryScanner.Scan("float4 helper() { return 0; }", "lib.slang");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0603");
        result.Error.Single().Message.ShouldContain("[shader(", Case.Sensitive);
    }

    [Fact]
    public void TwoFragmentEntries_IsSD0604_NamingBoth()
    {
        var result = SlangEntryScanner.Scan("""
            [shader("fragment")] float4 A() : SV_Target { return 0; }
            [shader("fragment")] float4 B() : SV_Target { return 1; }
            """, "two.slang");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0604");
        result.Error.Single().Message.ShouldContain("'A'", Case.Sensitive);
        result.Error.Single().Message.ShouldContain("'B'", Case.Sensitive);
    }
}

public sealed class SlangFrontendConvertTests
{
    private const string TwoStageSlang = """
        cbuffer Params { float4x4 WorldViewProjection; float Desaturation; }

        [shader("vertex")]
        float4 MainVS(float4 p : POSITION) : SV_Position { return mul(p, WorldViewProjection); }

        [shader("fragment")]
        float4 MainPS() : SV_Target { return (float4)Desaturation; }
        """;

    [Fact]
    public void SynthesizesTheTechnique_AndStripsTheAttributes()
    {
        var result = SlangFrontend.ConvertToFx(TwoStageSlang,
            new SlangConvertOptions { SourceName = "t.slang", TechniqueName = "T" });

        result.IsSuccess.ShouldBeTrue();
        string fx = result.Value.FxText;

        // The body rides through VERBATIM — the user's names ARE the effect parameter names,
        // with no compiler in between to mangle them.
        fx.ShouldContain("float4x4 WorldViewProjection", Case.Sensitive);
        fx.ShouldContain("float Desaturation", Case.Sensitive);

        // The attributes are stripped (fxc-lineage compilers reject them outside lib targets)…
        fx.ShouldNotContain("[shader(", Case.Sensitive);

        // …and the technique they expressed is synthesized in their place.
        fx.ShouldContain("technique T", Case.Sensitive);
        fx.ShouldContain("VertexShader = compile VS_SHADERMODEL MainVS();", Case.Sensitive);
        fx.ShouldContain("PixelShader = compile PS_SHADERMODEL MainPS();", Case.Sensitive);
        fx.ShouldContain("#if SM4", Case.Sensitive);
    }

    [Fact]
    public void PixelOnlySlang_SynthesizesAPixelOnlyPass()
    {
        var result = SlangFrontend.ConvertToFx(
            """[shader("fragment")] float4 P() : SV_Target { return 1; }""",
            new SlangConvertOptions { SourceName = "p.slang" });

        result.IsSuccess.ShouldBeTrue();
        result.Value.FxText.ShouldContain("PixelShader = compile", Case.Sensitive);
        result.Value.FxText.ShouldNotContain("VertexShader = compile", Case.Sensitive);
    }

    [Theory]
    [InlineData("import mymodule;", "import")]
    [InlineData("module mylib;", "module")]
    [InlineData("extension MyType { }", "extension")]
    [InlineData("associatedtype T;", "associatedtype")]
    [InlineData("__generic<T> T id(T x) { return x; }", "__generic")]
    public void SlangOnlyConstructs_AreRejectedByName_WithSD0600(string construct, string name)
    {
        string source = construct + "\n[shader(\"fragment\")] float4 P() : SV_Target { return 1; }";

        var result = SlangFrontend.ConvertToFx(source, new SlangConvertOptions { SourceName = "s.slang" });

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0600");
        error.Message.ShouldContain($"'{name}'", Case.Sensitive);
        error.Message.ShouldContain("HLSL-compatible", Case.Sensitive);
        error.Line.ShouldBe(1);
    }

    [Fact]
    public void SlangKeywordInsideAComment_DoesNotReject()
    {
        // 'import' in a comment must never trip the construct scan — false rejection of valid
        // input is the failure the line-anchored, comment-stripped scan exists to prevent.
        var result = SlangFrontend.ConvertToFx("""
            // TODO: consider import of a shared module one day
            /* module notes:
               import nothing */
            [shader("fragment")] float4 P() : SV_Target { return 1; }
            """, new SlangConvertOptions { SourceName = "c.slang" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
    }

    [Fact]
    public void AnIdentifierContainingAKeyword_DoesNotReject()
    {
        // A variable named 'extension'-ish or a field access must not false-positive: the
        // declaration keywords are line-anchored.
        var result = SlangFrontend.ConvertToFx("""
            [shader("fragment")]
            float4 P() : SV_Target
            {
                float file_extension = 1.0;
                float import_cost = 2.0;
                return float4(file_extension, import_cost, 0, 1);
            }
            """, new SlangConvertOptions { SourceName = "id.slang" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
    }
}
