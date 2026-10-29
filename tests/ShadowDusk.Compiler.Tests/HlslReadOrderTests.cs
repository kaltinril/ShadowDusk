#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for <see cref="HlslReadOrder"/>: the evaluation order of legacy texture reads
/// in the (rewritten, preprocessed) source, and its refusals. The order against real
/// <c>mgfxc</c> goldens is <c>DirectX11TextureSlotOrderTests</c> (Integration); the shape names
/// in the comments are its golden files.
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
                        "float k; int mode; static const bool On = true;\n" + extra +
                        "float4 P(float2 uv : TEXCOORD0) : COLOR0 { " + body + " }\n" +
                        "technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }\n";
        // By default the compiled shader binds the textures the source mentions (a bound texture
        // the walk never sees read is a refusal of its own).
        var set = new HashSet<string>(
            present.Length == 0 ? Legacy.Where(t => (extra + body).Contains(t, StringComparison.Ordinal)) : present,
            StringComparer.Ordinal);
        return HlslReadOrder.FirstReads(source, "P", Legacy, Samplers, set, out refusal);
    }

    private const string Undecided = "which reads it keeps";

    [Theory]
    [InlineData("return TB.Sample(B, uv) * TA.Sample(A, uv);", "TB,TA")]
    [InlineData("return TA.Sample(A, TB.Sample(B, uv).xy);", "TB,TA")]                    // inner read first
    [InlineData("return (TC.Sample(C, uv).r > TB.Sample(B, uv).g) ? 1 : 0;", "TC,TB")]   // '>' in token order
    [InlineData("if (TC.Sample(C, uv).r <= TB.Sample(B, uv).g) return 1; return 0;", "TC,TB")] // a condition is read before its arms
    [InlineData("float4 d = TB.Sample(B, uv); d = TC.Sample(C, uv); return d * TB.Sample(B, uv * 2);", "TB,TC")] // dead store counts
    [InlineData("float4 c = 0; if (k > 0) c = TB.Sample(B, uv); return c + TA.Sample(A, uv);", "TB,TA")]      // TB never read again
    [InlineData("float4 c = TA.Sample(A, uv); if (c.a < 0.5) discard; return c * TB.Sample(B, uv);", "TA,TB")]
    [InlineData("float4 c = 0; discard; c = TB.Sample(B, uv); return c + TA.Sample(A, uv) + TB.Sample(B, uv * 2);", "TB,TA")] // T06: discard drops nothing
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

    // ---- Every read under a condition is a maybe-read: fxc may keep it or fold it away ------------

    private const string ProbeTail = "return c + TA.Sample(A, uv) + TB.Sample(B, uv * 2);";

    // A texture first read under a condition and read again after another texture: whether fxc
    // numbers it first depends on the condition, which the walk never evaluates. Each was guessed
    // once, and each guess was wrong for some shape (measured against mgfxc 3.8.4.1).
    [Theory]
    [InlineData("", "float4 c = 0; if (On) c = TB.Sample(B, uv); " + ProbeTail)]                                                     // a static
    [InlineData("float Zero() { return 0; }\n", "float4 c = 0; if (Zero() > 0) c = TB.Sample(B, uv); " + ProbeTail)]                 // K01 a call
    [InlineData("float Zero() { return 0; }\n", "float4 c = 0; float z = Zero(); if (z > 0) c = TB.Sample(B, uv); " + ProbeTail)]   // K02 a local
    [InlineData("float4 F(float2 uv, bool b = false) { float4 c = 0; if (b) c = TB.Sample(B, uv); return c; }\n",
                "float4 c = F(uv); " + ProbeTail)]                                                                              // K04 a default argument
    [InlineData("", "float4 c = 0; if (k > 0 && false) c = TB.Sample(B, uv); " + ProbeTail)]                                        // K07 && false
    [InlineData("", "float4 c = 0; float2 z = float2(k, 0); if (z.y > 0) c = TB.Sample(B, uv); " + ProbeTail)]                      // K09 one component
    [InlineData("", "float4 c = 0; if (k > 0) c = TB.Sample(B, uv); " + ProbeTail)]                                                 // K16 a uniform (fxc kept it)
    [InlineData("", "float4 c = 0; if (uv.x > 0.5) c = TB.Sample(B, uv); " + ProbeTail)]                                            // K17 an input (fxc kept it)
    [InlineData("", "float4 c = 0; float a = 1, k = 0; if (k > 0) c = TB.Sample(B, uv); " + ProbeTail)]                             // P05 a comma-declared local
    [InlineData("", "float4 c = 0; float a, k; k = 0; if (k > 0) c = TB.Sample(B, uv); " + ProbeTail)]                              // Q13
    [InlineData("", "float4 c = 0; k = 0; if (k > 0) c = TB.Sample(B, uv); " + ProbeTail)]                                          // R01 a uniform assigned
    [InlineData("void Z(out float v) { v = 0; }\n", "float4 c = 0; Z(k); if (k > 0) c = TB.Sample(B, uv); " + ProbeTail)]           // R02 an out argument
    [InlineData("", "float4 c = 0; (uv).x = 0; if (uv.x > 0.5) c = TB.Sample(B, uv); " + ProbeTail)]                                // P13 a parenthesized write
    [InlineData("unsigned int u;\n", "float4 c = 0; if (u < 0) c = TB.Sample(B, uv); " + ProbeTail)]                               // P09 unsigned
    [InlineData("", "float4 c = 0; switch (mode) { case 0: c = TB.Sample(B, uv); break; default: c = 1; break; } " + ProbeTail)]   // K33 a switch arm
    [InlineData("", "float4 c = 0; if (k > 0) { c = TA.Sample(A, uv); return c; } c = TB.Sample(B, uv); " + ProbeTail)]            // before and after a return that may not run
    public void AConditionalRead_ThatCouldChangeTheOrder_IsRefused(string helpers, string body)
    {
        Order(body, out string? refusal, helpers).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain(Undecided, Case.Sensitive);
    }

    // Loops: entered for certain only for an `int` counter from an integer literal against an
    // integer literal or a `static const int` from one, the first test holding exactly.
    [Theory]
    [InlineData("", "for (int i = 0; i < 0; i++) c += TB.Sample(B, uv);")]                        // K31 never runs
    [InlineData("int Count;\n", "for (int i = 0; i < Count; i++) c += TB.Sample(B, uv);")]         // K38 a uniform bound
    [InlineData("", "for (int i = 0.5; i > 0.4; i++) c += TB.Sample(B, uv);")]                    // P17 a float initialiser
    [InlineData("static const int N = 0.5;\n", "for (int i = 0; i < N; i++) c += TB.Sample(B, uv);")] // P18 a float static const
    [InlineData("", "for (uint i = 0; i > -1; i++) c += TB.Sample(B, uv);")]                       // P19 an unsigned counter
    [InlineData("", "for (int i = 0; i == 0; i++) c += TB.Sample(B, uv);")]                        // == is not one of the tests
    [InlineData("", "for (int i = 010; i < 9; i++) c += TB.Sample(B, uv);")]                       // octal
    [InlineData("static const int N = 2;\nstatic const int N = 0;\n", "for (int i = 0; i < N; i++) c += TB.Sample(B, uv);")] // declared twice
    [InlineData("static const int N = 2;\nfloat4 G(int N) { return 0; }\n", "for (int i = 0; i < N; i++) c += TB.Sample(B, uv);")] // the name declared again
    [InlineData("", "int n = 0; while (n < 2) { c += TB.Sample(B, uv); n++; }")]                    // a while loop
    public void ALoopNotCertainlyEntered_IsAGuard(string helpers, string loop)
    {
        Order("float4 c = 0; " + loop + " " + ProbeTail, out string? refusal, helpers).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain(Undecided, Case.Sensitive);
    }

    [Theory]
    [InlineData("", "for (int i = 0; i < 2; i++) { c += TB.Sample(B, uv); }")]
    [InlineData("static const int N = 2;\n", "for (int i = 0; i < N; i++) { c += TB.Sample(B, uv); }")]      // K32
    [InlineData("static const int N = -1;\n", "[unroll] for (int i = 3; i > N; i--) c += TB.Sample(B, uv);")]
    [InlineData("", "for (int i = 0; i != 4; i++) c += TB.Sample(B, uv);")]
    [InlineData("", "do { c += TB.Sample(B, uv); } while (c.x < 1);")]
    public void ALoopCertainlyEntered_KeepsTokenOrder(string helpers, string loop)
    {
        Order("float4 c = 0; " + loop + " " + ProbeTail, out string? refusal, helpers)!.ShouldBe(["TB", "TA"], refusal);
    }

    [Fact]
    public void ABranchOnAnUnrolledLoopCounter_IsRefused()
    {
        // D12: fxc reads in ITERATION order (TB in iteration 1, TA in iteration 2); every later
        // iteration is walked again, under guards of its own.
        Order("float4 r = 0; [unroll] for (int i = 0; i < 3; i++) { if (i == 2) r += TA.Sample(A, uv); else if (i == 1) r += TB.Sample(B, uv); } return r;",
              out string? refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("iteration", Case.Sensitive);
    }

    [Fact]
    public void ALoopWithoutBranches_KeepsTokenOrder()
    {
        Order("float4 c = 0; [unroll] for (int i = 0; i < 3; i++) { c += TB.Sample(B, uv + i) * TA.Sample(A, uv - i); } return c;", out _)!
            .ShouldBe(["TB", "TA"]);
    }

    [Theory]
    [InlineData("float4 c = 0; if (k > 0) { c = TB.Sample(B, uv) + TA.Sample(A, uv) + TB.Sample(B, uv * 2); } return c;", "TB,TA")] // T11: one guard, one fate
    [InlineData("if (k > 0) return TC.Sample(C, uv); float4 c = TB.Sample(B, uv); c += TA.Sample(A, uv); return c + TB.Sample(B, uv * 2);", "TC,TB,TA")] // T10
    [InlineData("float4 c = TA.Sample(A, uv); if (On) c += TB.Sample(B, uv); return c;", "TA,TB")]                 // a guard that cannot change it
    [InlineData("float4 c = (k > 0) ? TB.Sample(B, uv) : 0; " + ProbeTail, "TB,TA")]                             // K12/K22: both ?: arms are read
    [InlineData("float4 c = 0; [flatten] if (k > 0) c = TB.Sample(B, uv); else c = TA.Sample(A, uv); return c;", "TB,TA")] // P22: then before else
    public void AGuardThatCannotChangeTheOrder_IsKept(string body, string expected)
    {
        Order(body, out string? refusal)!.ShouldBe(expected.Split(','), refusal);
    }

    [Fact]
    public void ReadsAfterOneEarlyReturn_ShareItsFate_ButAReadBeforeItDoesNot()
    {
        // T12: TB before the return and again after TA; fxc numbers TB first only if it keeps the
        // early arm.
        Order("if (k > 0) return TB.Sample(B, uv); float4 c = TA.Sample(A, uv); return c + TB.Sample(B, uv * 2);", out string? refusal)
            .ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("'TB' before 'TA'", Case.Sensitive);
    }

    // ---- Reads after an unconditional return, break or continue are removed -------------------

    [Theory]
    [InlineData("", "float4 c = 0; for (int i = 0; i < 2; i++) { break; c += TB.Sample(B, uv); } " + ProbeTail)]          // P14
    [InlineData("", "float4 c = 0; do { continue; c += TB.Sample(B, uv); } while (false); " + ProbeTail)]                 // P15
    [InlineData("", "float4 c = 0; do { break; c += TB.Sample(B, uv); } while (false); " + ProbeTail)]                    // Q16
    [InlineData("float4 H(float2 uv) { return 0; return TB.Sample(B, uv); }\n", "float4 c = H(uv); " + ProbeTail)]         // P16
    [InlineData("", "float4 c = 0; if (k > 0) { return c; c = TB.Sample(B, uv); } " + ProbeTail)]                         // R03
    [InlineData("float4 H(float2 uv) { { return 0; } return TB.Sample(B, uv); }\n", "float4 c = H(uv); " + ProbeTail)]     // T08 a bare block
    [InlineData("", "float4 c = 0; for (int i = 0; i < 2; i++) { { break; } c += TB.Sample(B, uv); } " + ProbeTail)]      // T09
    public void AReadAfterAnUnconditionalReturnBreakOrContinue_IsGone(string helpers, string body)
    {
        Order(body, out string? refusal, helpers)!.ShouldBe(["TA", "TB"], refusal);
    }

    // ---- switch: the default arm is numbered last ----------------------------------------------

    [Theory]
    [InlineData("switch (mode) { default: c = TA.Sample(A, uv); break; case 1: c = TB.Sample(B, uv); break; }", "TB,TA")]                // T04
    [InlineData("switch (mode) { default: case 1: c = TA.Sample(A, uv); break; case 2: c = TB.Sample(B, uv); break; }", "TB,TA")]       // T01 a shared label
    [InlineData("switch (mode) { case 1: c = TA.Sample(A, uv); break; default: c = TB.Sample(B, uv); break; case 2: c = TC.Sample(C, uv); break; }", "TA,TC,TB")] // T02
    [InlineData("switch (mode) { case 0: c = TA.Sample(A, uv); break; case 1: default: c = TB.Sample(B, uv); break; case 2: c = TC.Sample(C, uv); break; }", "TA,TC,TB")] // T03
    [InlineData("[branch] switch (mode) { default: { c = TA.Sample(A, uv); break; } case 3: { c = TB.Sample(B, uv); break; } }", "TB,TA")] // braced arms
    public void ASwitch_NumbersItsDefaultArmLast(string sw, string expected)
    {
        Order("float4 c = 0; " + sw + " return c;", out string? refusal)!.ShouldBe(expected.Split(','), refusal);
    }

    [Fact]
    public void ASwitchArmThatFallsThrough_IsRefused()
    {
        // fxc rejects it (X3533); DXC would compile it, so the walk must not guess.
        Order("float4 c = 0; switch (mode) { case 0: c = TA.Sample(A, uv); case 1: c += TB.Sample(B, uv); break; } return c;", out string? refusal)
            .ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("does not end in a break or return", Case.Sensitive);
    }

    // ---- Shapes outside the model --------------------------------------------------------------

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

    [Theory]
    [InlineData("for (int i = 0; i < 2; i += TA.Sample(A, uv).r) { } return TB.Sample(B, uv);", "increment")]
    [InlineData("return G(TA, uv);", "used other than")]
    [InlineData("return O(uv) + O(uv, 1);", "overloaded")]
    [InlineData("return R(uv);", "calls itself")]
    public void ShapesOutsideTheModel_AreRefused(string body, string reason)
    {
        const string helpers =
            "float4 G(Texture2D t, float2 uv) { return t.Sample(A, uv); }\n" +
            "float4 O(float2 uv) { return TA.Sample(A, uv); }\nfloat4 O(float2 uv, int n) { return TB.Sample(B, uv); }\n" +
            "float4 R(float2 uv) { return R(uv) + TA.Sample(A, uv); }\n";
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
