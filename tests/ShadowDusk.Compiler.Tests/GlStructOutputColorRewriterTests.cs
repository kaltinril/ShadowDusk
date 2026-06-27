#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.HLSL.Ast;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Unit tests for the Phase 41 GAP-2 GL-only struct-output COLOR rewrite. The rewrite must
/// retarget a PIXEL-entry RETURN struct's <c>: COLOR&lt;n&gt;</c> members to <c>: SV_Target&lt;n&gt;</c>
/// (so DXC's GL/SPIR-V backend accepts them), and must NEVER touch a PS-input / VS-output
/// interpolant struct (whose <c>: COLOR0</c> is a valid DXC input semantic).
/// </summary>
public sealed class GlStructOutputColorRewriterTests
{
    private static IReadOnlyList<TechniqueInfo> WithPixelEntry(string? pixelEntry) =>
        new[]
        {
            new TechniqueInfo
            {
                Name = "T",
                Span = default,
                IsEffect11 = false,
                Annotations = Array.Empty<AnnotationEntry>(),
                Passes = new[]
                {
                    new PassInfo
                    {
                        Name = "P",
                        Span = default,
                        VertexEntryPoint = null,
                        PixelEntryPoint = pixelEntry,
                        VertexProfile = null,
                        PixelProfile = "ps_4_0",
                        RenderStates = Array.Empty<RenderStateEntry>(),
                        Annotations = Array.Empty<AnnotationEntry>(),
                    },
                },
            },
        };

    [Fact]
    public void PsReturnStruct_ColorMembers_RewrittenToSvTarget()
    {
        const string hlsl = """
            struct PixelOut { float4 color : COLOR0; float4 normal : COLOR1; };
            PixelOut PS(float2 uv : TEXCOORD0)
            {
                PixelOut o;
                o.color = float4(uv, 0, 1);
                o.normal = float4(0, 0, 1, 1);
                return o;
            }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Contain("float4 color : SV_Target0;");
        result.Should().Contain("float4 normal : SV_Target1;");
        result.Should().NotContain("COLOR0");
        result.Should().NotContain("COLOR1");
    }

    [Fact]
    public void PsInputInterpolantStruct_ColorMember_NotRewritten()
    {
        // VertexShaderOutput is the PS *input* (a parameter type), NOT the PS return type, so its
        // `Color : COLOR0` is a valid DXC interpolant and must stay COLOR0. Only PixelOut (the PS
        // return) is rewritten. This is the exact DeferredSprite shape.
        const string hlsl = """
            struct VertexShaderOutput { float4 Position : SV_POSITION; float4 Color : COLOR0; };
            struct PixelOut { float4 color : COLOR0; float4 normal : COLOR1; };
            PixelOut PS(VertexShaderOutput input)
            {
                PixelOut o;
                o.color = input.Color;
                o.normal = float4(0, 0, 1, 1);
                return o;
            }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        // The PS-input interpolant keeps COLOR0.
        result.Should().Contain("float4 Color : COLOR0;",
            because: "a PS-input/VS-output interpolant struct is not a PS return type and must not be rewritten");
        // The PS-output struct is retargeted.
        result.Should().Contain("float4 color : SV_Target0;");
        result.Should().Contain("float4 normal : SV_Target1;");
    }

    [Fact]
    public void Float4ReturnPs_NoStruct_IsNoOp()
    {
        // A PS returning float4 directly (function-return `: COLOR` form, handled by FxPreParser
        // B6 elsewhere) has no output STRUCT — this rewrite must not touch it.
        const string hlsl = "float4 PS(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }";

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Be(hlsl, because: "a non-struct PS return has no COLOR members to rewrite");
    }

    [Fact]
    public void StructWithColor_NotAPsReturnType_IsNotRewritten()
    {
        // A struct carrying COLOR members that is NEVER a pixel-entry return type must be left
        // alone (the discriminator is "struct == resolved PS-entry return type", nothing else).
        const string hlsl = """
            struct Unused { float4 a : COLOR0; };
            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Be(hlsl);
        result.Should().Contain("float4 a : COLOR0;");
    }

    [Fact]
    public void NoPixelEntry_IsNoOp()
    {
        const string hlsl = "struct PixelOut { float4 color : COLOR0; };";

        GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry(null)).Should().Be(hlsl);
        GlStructOutputColorRewriter.Rewrite(hlsl, Array.Empty<TechniqueInfo>()).Should().Be(hlsl);
    }

    [Fact]
    public void MixedCaseColorSemantic_IsRewritten_HlslSemanticsAreCaseInsensitive()
    {
        // HLSL semantics are case-insensitive; a third-party deferred shader may author
        // ': Color0' / ': color1'. These must be retargeted exactly like ': COLOR0', or they
        // reach DXC's GL backend as an invalid PS output (the failure this rewrite prevents).
        const string hlsl = """
            struct PixelOut { float4 color : Color0; float4 normal : color1; };
            PixelOut PS(float2 uv : TEXCOORD0) { PixelOut o; o.color = float4(uv, 0, 1); o.normal = float4(0,0,1,1); return o; }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Contain("float4 color : SV_Target0;");
        result.Should().Contain("float4 normal : SV_Target1;");
        System.Text.RegularExpressions.Regex.IsMatch(result, @":\s*[Cc]olor\d?\b")
            .Should().BeFalse("no COLOR-cased output semantic may survive in any casing");
    }

    [Fact]
    public void StructUsedAsBothReturnAndInputParam_IsLeftUnrewritten()
    {
        // A struct that is BOTH a PS-entry return type AND a function INPUT parameter must NOT be
        // rewritten: its members are read as input interpolants there, and SV_Target is invalid on
        // an input. We leave it unchanged so the GL compile surfaces the original LOUD COLOR error
        // rather than a silently-wrong rewrite that breaks the input use.
        const string hlsl = """
            struct Shared { float4 c : COLOR0; };
            float4 Other(Shared input) : SV_Target { return input.c; }
            Shared PS(float2 uv : TEXCOORD0) { Shared o; o.c = float4(uv, 0, 1); return o; }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Be(hlsl, because: "a shared input/output struct must not be rewritten");
        result.Should().Contain("float4 c : COLOR0;");
    }

    [Fact]
    public void StructUsedAsQualifiedInputParam_IsLeftUnrewritten()
    {
        // Same protection through an 'in'-qualified parameter (in/out/inout qualifiers are skipped).
        const string hlsl = """
            struct Shared { float4 c : COLOR0; };
            void Other(in Shared input) { }
            Shared PS(float2 uv : TEXCOORD0) { Shared o; o.c = float4(uv, 0, 1); return o; }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Be(hlsl);
    }

    [Fact]
    public void EntryCalledInsideAnotherBody_NotMistakenForDefinition()
    {
        // `PS(` appears inside a helper body (a call) AND as the top-level definition. Only the
        // definition (brace-depth 0, preceded by the return-type identifier) resolves the struct.
        const string hlsl = """
            struct PixelOut { float4 color : COLOR0; };
            float4 helper(float2 uv) { PixelOut tmp; return float4(uv, 0, 1); }
            PixelOut PS(float2 uv : TEXCOORD0)
            {
                PixelOut o;
                o.color = float4(uv, 0, 1);
                return o;
            }
            """;

        string result = GlStructOutputColorRewriter.Rewrite(hlsl, WithPixelEntry("PS"));

        result.Should().Contain("float4 color : SV_Target0;");
        result.Should().NotContain("COLOR0");
    }
}
