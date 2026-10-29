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
        // By default the compiled shader binds the textures the source mentions (a bound texture
        // the walk never sees read is a refusal of its own).
        var set = new HashSet<string>(
            present.Length == 0 ? Legacy.Where(t => (extra + body).Contains(t, StringComparison.Ordinal)) : present,
            StringComparer.Ordinal);
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
        refusal.ShouldNotBeNull().ShouldContain("evaluate at compile time", Case.Sensitive);
    }

    // Conditions fxc folds that no syntactic test sees (each measured against mgfxc 3.8.4.1: fxc
    // folds them and numbers TA first; the walk once called them run-time and put TB first,
    // silently). Every one must now refuse. Goldens: dx11-texture-slots/K*.
    private const string ProbeTail = "return c + TA.Sample(A, uv) + TB.Sample(B, uv * 2);";

    [Theory]
    [InlineData("float Zero() { return 0; }\n", "float4 c = 0; if (Zero() > 0) c = TB.Sample(B, uv); " + ProbeTail)]                 // K01 a call
    [InlineData("float Zero() { return 0; }\n", "float4 c = 0; float z = Zero(); if (z > 0) c = TB.Sample(B, uv); " + ProbeTail)]   // K02 a local
    [InlineData("float Never(float x) { return 0; }\n", "float4 c = 0; if (Never(uv.x) > 0) c = TB.Sample(B, uv); " + ProbeTail)]  // K03 a run-time argument ignored
    [InlineData("float4 F(float2 uv, bool b = false) { float4 c = 0; if (b) c = TB.Sample(B, uv); return c; }\n",
                "float4 c = F(uv); " + ProbeTail)]                                                                              // K04 a default argument
    [InlineData("void GetZero(out float z) { z = 0; }\n", "float4 c = 0; float z; GetZero(z); if (z > 0) c = TB.Sample(B, uv); " + ProbeTail)] // K05 an out argument
    [InlineData("", "float4 c = 0; float z = 0; if (On) z = k; if (z > 0) c = TB.Sample(B, uv); " + ProbeTail)]                      // K06 a local tainted under a static
    [InlineData("", "float4 c = 0; if (k > 0 && false) c = TB.Sample(B, uv); " + ProbeTail)]                                        // K07 && false
    [InlineData("", "float4 c = 0; if (k > 0 && 0) c = TB.Sample(B, uv); " + ProbeTail)]                                            // K08 && a macro's 0
    [InlineData("", "float4 c = 0; float2 z = float2(k, 0); if (z.y > 0) c = TB.Sample(B, uv); " + ProbeTail)]                      // K09 one component
    [InlineData("", "float4 c = 0; if (saturate(-1 - abs(k)) > 0) c = TB.Sample(B, uv); " + ProbeTail)]                             // K10 a value range
    [InlineData("struct S { float On; };\n", "float4 c = 0; if (On) c = TB.Sample(B, uv); " + ProbeTail)]                          // K11 a struct member named like the static
    [InlineData("", "float4 c = 0; for (int i = 0; i < 0; i++) c += TB.Sample(B, uv); " + ProbeTail)]                              // K31 a loop that never runs
    [InlineData("float Zero() { return 0; }\n", "float4 c = 0; int n = 0; while (Zero() > n) { c += TB.Sample(B, uv); n++; } " + ProbeTail)] // K37
    [InlineData("", "float4 c = 0; uv.x = 0; if (uv.x > 0.5) c = TB.Sample(B, uv); " + ProbeTail)]                                  // K36 an input overwritten
    [InlineData("float Zero() { return 0; }\n", "if (Zero() > 0) return TA.Sample(A, uv); return TB.Sample(B, uv) + TA.Sample(A, uv * 2);")] // K20 a folded return
    public void AConditionFxcMayFold_BeforeALaterRead_IsRefused(string helpers, string body)
    {
        Order(body, out string? refusal, helpers).ShouldBeNull();
        refusal.ShouldNotBeNull();
    }

    // Conditions fxc cannot fold keep token order (measured: mgfxc numbers TB first).
    [Theory]
    [InlineData("", "float4 c = 0; if (k > 0) c = TB.Sample(B, uv); " + ProbeTail)]                         // K16 a uniform
    [InlineData("", "float4 c = 0; if (uv.x > 0.5) c = TB.Sample(B, uv); " + ProbeTail)]                    // K17 an input
    [InlineData("bool UseB = false;\n", "float4 c = 0; if (UseB) c = TB.Sample(B, uv); " + ProbeTail)]      // K28 a uniform bool with a default
    [InlineData("float k2 = 0;\n", "float4 c = 0; if (!(k2 > 0)) c = TB.Sample(B, uv); " + ProbeTail)]
    [InlineData("int mode;\n", "float4 c = 0; switch (mode) { case 0: c = TB.Sample(B, uv); break; default: c = 1; break; } " + ProbeTail)] // K33
    [InlineData("int mode;\n", "float4 c = 0; switch (mode) { case 0: c = 1; break; default: c = 2; break; } if (k > 0) c += TB.Sample(B, uv); " + ProbeTail)] // K35 a break ends at its switch
    [InlineData("int Count;\n", "float4 c = 0; for (int i = 0; i < Count; i++) { c += TB.Sample(B, uv); } " + ProbeTail)] // K38 a run-time trip count
    [InlineData("static const int N = 2;\n", "float4 c = 0; for (int i = 0; i < N; i++) { c += TB.Sample(B, uv); c += TA.Sample(A, uv); } return c;")] // K32
    [InlineData("float Zero() { return 0; }\n", "float4 c = (Zero() > 0) ? TB.Sample(B, uv) : 0; " + ProbeTail)] // K12 both arms of ?: are read
    [InlineData("", "float4 c = false ? TB.Sample(B, uv) : 0; " + ProbeTail)]                               // K22
    public void ARunTimeCondition_OrATernaryArm_KeepsTokenOrder(string helpers, string body)
    {
        Order(body, out string? refusal, helpers)!.ShouldBe(["TB", "TA"], refusal);
    }

    [Theory]
    [InlineData("", "float k = 0; if (k > 0) c = TB.Sample(B, uv);")]       // a local shadows the uniform
    [InlineData("", "if (k > k) c = TB.Sample(B, uv);")]                     // the same value twice
    [InlineData("uint u;\n", "if (u >= 0) c = TB.Sample(B, uv);")]           // a uint compared by range
    [InlineData("", "if (k > 1e40) c = TB.Sample(B, uv);")]                  // not a short literal
    [InlineData("const float kc = 1;\n", "if (kc > 0) c = TB.Sample(B, uv);")]
    [InlineData("", "if (k > 0 && uv.x > 0) c = TB.Sample(B, uv);")]         // a combination, not modelled
    public void ANearlyRunTimeCondition_IsNotTrusted(string helpers, string branch)
    {
        Order("float4 c = 0; " + branch + " " + ProbeTail, out string? refusal, helpers).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("evaluate at compile time", Case.Sensitive);
    }

    [Fact]
    public void AReadThroughASamplerParameter_IsRefused()
    {
        // K13: the walk does not follow `s` to B; placing B by guess gave A t0 where mgfxc has B t0.
        const string helper = "float4 F(SamplerState s, float2 uv) { return tex2D(s, uv); }\n";
        Order("return F(B, uv) * 0.5 + TA.Sample(A, uv);", out string? refusal, helper).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("sampler parameter", Case.Sensitive);
    }

    [Fact]
    public void ABoundTextureTheWalkNeverSawRead_IsRefused()
    {
        Order("return TA.Sample(A, uv);", out string? refusal, "", "TA", "TB").ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("'TB'", Case.Sensitive);
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
