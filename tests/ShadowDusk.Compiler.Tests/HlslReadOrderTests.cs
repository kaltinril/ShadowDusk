#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for <see cref="HlslReadOrder"/>: the evaluation order of legacy texture reads
/// in the (rewritten, preprocessed) source, and its refusals. The order against real
/// <c>mgfxc</c> goldens is <c>DirectX11TextureSlotOrderTests</c> (Integration).
/// </summary>
public sealed class HlslReadOrderTests
{
    private static readonly HashSet<string> Legacy = new(StringComparer.Ordinal) { "TA", "TB", "TC" };
    private static readonly Dictionary<string, string> Samplers = new(StringComparer.Ordinal)
    {
        ["A"] = "TA", ["B"] = "TB", ["C"] = "TC",
    };

    private static IReadOnlyList<string>? Order(string body, out string? refusal, string extra = "", params string[] present)
    {
        string source = "Texture2D TA; Texture2D TB; Texture2D TC; SamplerState A; SamplerState B; SamplerState C;\n" +
                        "float k; static const bool On = true;\n" + extra +
                        "float4 P(float2 uv : TEXCOORD0) : COLOR0 { " + body + " }\n" +
                        "technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }\n";
        var set = new HashSet<string>(present.Length == 0 ? ["TA", "TB", "TC"] : present, StringComparer.Ordinal);
        return HlslReadOrder.FirstReads(source, "P", Legacy, Samplers, set, out refusal);
    }

    [Theory]
    [InlineData("return TB.Sample(B, uv) * TA.Sample(A, uv);", "TB,TA")]
    [InlineData("return TA.Sample(A, TB.Sample(B, uv).xy);", "TB,TA")]                    // inner read first
    [InlineData("return (TC.Sample(C, uv).r > TB.Sample(B, uv).g) ? 1 : 0;", "TC,TB")]   // '>' in token order
    [InlineData("if (TC.Sample(C, uv).r <= TB.Sample(B, uv).g) return 1; return 0;", "TC,TB")]
    [InlineData("float4 d = TB.Sample(B, uv); d = TC.Sample(C, uv); return d * TB.Sample(B, uv * 2);", "TB,TC")] // dead store counts
    [InlineData("float4 c = 0; if (k > 0) c = TB.Sample(B, uv); return c + TA.Sample(A, uv);", "TB,TA")]      // run-time branch
    [InlineData("float4 c = TA.Sample(A, uv); if (c.a < 0.5) discard; return c * TB.Sample(B, uv);", "TA,TB")]
    public void Reads_ComeInEvaluationOrder(string body, string expected)
    {
        Order(body, out string? refusal)!.ShouldBe(expected.Split(','), refusal);
    }

    [Fact]
    public void ACallEvaluatesItsArguments_ThenTheBody()
    {
        const string helper = "float4 F(float4 c, float2 uv) { return c * TB.Sample(B, uv); }\n";
        Order("return F(TA.Sample(A, uv), uv);", out _, helper)!.ShouldBe(["TA", "TB"]);
    }

    [Fact]
    public void AnUnrewrittenLegacyIntrinsic_IsItsSamplersTexture_OrTheSamplerItself()
    {
        Order("return tex2D(B, uv) + tex2D(Native, uv) + TA.Sample(A, uv);", out _, "", "TA", "TB", "Native")!
            .ShouldBe(["TB", "Native", "TA"]);
    }

    [Fact]
    public void ACompileTimeBranch_ThatDecidesTheOrder_IsRefused()
    {
        Order("float4 c = 0; if (On) c = TB.Sample(B, uv); return c + TA.Sample(A, uv) + TB.Sample(B, uv * 2);", out string? refusal)
            .ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("compile-time condition", Case.Sensitive);
    }

    [Fact]
    public void ACompileTimeBranch_ThatCannotChangeTheOrder_IsKept()
    {
        Order("float4 c = TA.Sample(A, uv); if (On) c += TB.Sample(B, uv); return c;", out _)!.ShouldBe(["TA", "TB"]);
    }

    [Fact]
    public void ABranchOnAnUnrolledLoopCounter_IsRefused()
    {
        Order("float4 r = 0; [unroll] for (int i = 0; i < 3; i++) { if (i == 2) r += TA.Sample(A, uv); else if (i == 1) r += TB.Sample(B, uv); } return r;",
              out string? refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("iteration order", Case.Sensitive);
    }

    [Fact]
    public void ALoopWithoutCompileTimeBranches_KeepsTokenOrder()
    {
        Order("float4 c = 0; [unroll] for (int i = 0; i < 3; i++) { c += TB.Sample(B, uv + i) * TA.Sample(A, uv - i); } return c;", out _)!
            .ShouldBe(["TB", "TA"]);
    }

    [Theory]
    [InlineData("for (int i = 0; i < 2; i += TA.Sample(A, uv).r) { } return TB.Sample(B, uv);", "increment")]
    [InlineData("return G(TA, uv);", "used other than")]
    [InlineData("return O(uv) + O(uv, 1);", "overloaded")]
    public void ShapesOutsideTheModel_AreRefused(string body, string reason)
    {
        const string helpers =
            "float4 G(Texture2D t, float2 uv) { return t.Sample(A, uv); }\n" +
            "float4 O(float2 uv) { return TA.Sample(A, uv); }\nfloat4 O(float2 uv, int n) { return TB.Sample(B, uv); }\n";
        Order(body, out string? refusal, helpers).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain(reason, Case.Sensitive);
    }

    [Fact]
    public void AMissingEntryPoint_IsRefused()
    {
        HlslReadOrder.FirstReads("float4 Q() : COLOR0 { return 0; }", "P", Legacy, Samplers, Legacy, out string? refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("'P'", Case.Sensitive);
    }
}
