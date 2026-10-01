#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Pure tests (no disk, no process) for <see cref="SlangHlslMerger"/>'s name-collision
/// handling (issue #228). The literal HLSL is shaped exactly like real slangc output, where
/// the SAME generated name (<c>helper_0</c>) means DIFFERENT bodies in two entry units because
/// slangc numbers generic instantiations per unit in first-use order. The real-slangc
/// counterparts are in <c>SlangGenericsCollisionTests</c>.
/// </summary>
public sealed class SlangHlslMergerCollisionTests
{
    private const string Prelude = """
        #pragma pack_matrix(column_major)
        #ifdef SLANG_HLSL_ENABLE_NVAPI
        #include "nvHLSLExtns.h"
        #endif

        """;

    private static string Merge(string[] units, params string[] reserved) =>
        SlangHlslMerger.TryMerge(units, reserved, out _);

    private static int Count(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private const string VsFloatHelper = """
        #line 1
        float helper_0(float x_0)
        {

        #line 5
            return x_0 * x_0;
        }


        #line 9
        float4 MainVS() : SV_Position
        {
            return helper_0(2.0f);
        }

        """;

    private const string PsFloat2Helper = """
        #line 1
        float2 helper_0(float2 x_0)
        {

        #line 5
            return x_0 * x_0;
        }


        #line 20
        float4 MainPS() : SV_TARGET
        {
            return float4(helper_0(float2(1.0f, 2.0f)), 0, 1);
        }

        """;

    [Fact]
    public void SameNameDifferentBodies_BothSurvive_AndEachEntryKeepsItsOwnCallee()
    {
        string merged = Merge([Prelude + VsFloatHelper, Prelude + PsFloat2Helper], "MainVS", "MainPS");

        merged.ShouldContain("float helper_0(float x_0)", Case.Sensitive);
        merged.ShouldContain("float2 helper_0_e1(float2 x_0)", Case.Sensitive);
        merged.ShouldNotContain("float2 helper_0(", Case.Sensitive);
        // The vertex entry still calls the float one, the pixel entry the renamed float2 one.
        merged.ShouldContain("return helper_0(2.0f);", Case.Sensitive);
        merged.ShouldContain("helper_0_e1(float2(1.0f, 2.0f))", Case.Sensitive);
    }

    [Fact]
    public void IdenticalTrailingFragmentsInDifferentFunctions_AreNeverDroppedAsDuplicates()
    {
        // Both helper bodies end in the byte-identical return-and-close fragment. The
        // original per-#line merger treated the second as a duplicate and dropped it, leaving
        // the function without its return and closing brace.
        string merged = Merge([Prelude + VsFloatHelper, Prelude + PsFloat2Helper], "MainVS", "MainPS");

        Count(merged, "return x_0 * x_0;").ShouldBe(2);
        Count(merged, '{'.ToString()).ShouldBe(Count(merged, '}'.ToString()));
    }

    [Fact]
    public void ReferencingDeclarationWithIdenticalText_IsRenamedWithItsCallee()
    {
        // f_0 has byte-identical text in both units but calls g_0, whose body differs: f_0
        // would silently bind to whichever g_0 survived. The fixed point renames both.
        const string vs = """
            #line 1
            float g_0(float x_0)
            {
                return x_0 + 1.0f;
            }

            #line 4
            float f_0(float v_0)
            {
                return g_0(v_0);
            }

            #line 9
            float4 MainVS() : SV_Position
            {
                return f_0(1.0f);
            }

            """;
        const string ps = """
            #line 1
            float g_0(float x_0)
            {
                return x_0 + 2.0f;
            }

            #line 4
            float f_0(float v_0)
            {
                return g_0(v_0);
            }

            #line 12
            float4 MainPS() : SV_TARGET
            {
                return f_0(1.0f);
            }

            """;

        string merged = Merge([Prelude + vs, Prelude + ps], "MainVS", "MainPS");

        merged.ShouldContain("float g_0_e1(float x_0)", Case.Sensitive);
        merged.ShouldContain("float f_0_e1(float v_0)", Case.Sensitive);
        merged.ShouldContain("return g_0_e1(v_0);", Case.Sensitive);
        merged.ShouldContain("return f_0_e1(1.0f);", Case.Sensitive);
        merged.ShouldContain("return f_0(1.0f);", Case.Sensitive);
    }

    [Fact]
    public void RenameCandidate_NeverCollidesWithAnIdentifierAlreadyInAnyUnit()
    {
        const string ps = """
            #line 1
            float helper_0_e1(float q_0)
            {
                return q_0;
            }

            #line 4
            float2 helper_0(float2 x_0)
            {
                return x_0 * x_0;
            }

            #line 9
            float4 MainPS() : SV_TARGET
            {
                return float4(helper_0(float2(1.0f, 2.0f)), helper_0_e1(1.0f), 1);
            }

            """;

        string merged = Merge([Prelude + VsFloatHelper, Prelude + ps], "MainVS", "MainPS");

        merged.ShouldContain("float2 helper_0_e1_(float2 x_0)", Case.Sensitive);
        merged.ShouldContain("float helper_0_e1(float q_0)", Case.Sensitive);
    }

    [Fact]
    public void ConstantBufferWithSameNameButDifferentMembers_IsReportedNotRenamed()
    {
        // A cbuffer name IS a reflected effect-parameter group; renaming it would change the
        // consumer-visible surface, so a true conflict is reported instead.
        const string vs = """
            #line 3
            cbuffer Params : register(b0)
            {
                float A;
            }

            #line 9
            float4 MainVS() : SV_Position
            {
                return A;
            }

            """;
        const string ps = """
            #line 3
            cbuffer Params : register(b0)
            {
                float B;
            }

            #line 9
            float4 MainPS() : SV_TARGET
            {
                return B;
            }

            """;

        string merged = SlangHlslMerger.TryMerge(
            [Prelude + vs, Prelude + ps], ["MainVS", "MainPS"], out var conflicts);

        conflicts.Count.ShouldBe(1);
        conflicts[0].Name.ShouldBe("Params");
        conflicts[0].UnitIndex.ShouldBe(1);
        merged.ShouldNotContain("Params_e1", Case.Sensitive);
        merged.ShouldContain("float A;", Case.Sensitive);
        merged.ShouldContain("float B;", Case.Sensitive);
    }

    [Fact]
    public void EntryPointNames_AreNeverRenamed_AndACollisionOnOneIsReported()
    {
        const string vs = """
            #line 9
            float4 Main() : SV_Position
            {
                return 1.0f;
            }

            """;
        const string ps = """
            #line 9
            float4 Main() : SV_TARGET
            {
                return 2.0f;
            }

            """;

        string merged = SlangHlslMerger.TryMerge(
            [Prelude + vs, Prelude + ps], ["Main"], out var conflicts);

        conflicts.Count.ShouldBe(1);
        conflicts[0].Name.ShouldBe("Main");
        merged.ShouldNotContain("Main_e1", Case.Sensitive);
    }

    [Fact]
    public void StaticGlobalWithSameNameButDifferentValue_IsRenamedConsistently()
    {
        const string vs = """
            #line 2
            static const float _S4[2] = { 1.0f, 2.0f };

            #line 9
            float4 MainVS() : SV_Position
            {
                return _S4[0];
            }

            """;
        const string ps = """
            #line 2
            static const float _S4[2] = { 3.0f, 4.0f };

            #line 9
            float4 MainPS() : SV_TARGET
            {
                return _S4[1];
            }

            """;

        string merged = Merge([Prelude + vs, Prelude + ps], "MainVS", "MainPS");

        merged.ShouldContain("static const float _S4[2] = { 1.0f, 2.0f };", Case.Sensitive);
        merged.ShouldContain("static const float _S4_e1[2] = { 3.0f, 4.0f };", Case.Sensitive);
        merged.ShouldContain("return _S4_e1[1];", Case.Sensitive);
        merged.ShouldContain("return _S4[0];", Case.Sensitive);
    }

    [Fact]
    public void IdenticalResourceDeclarations_CollapseAndKeepTheirNames()
    {
        const string resources = """
            #line 11
            Texture2D<float4 > SpriteTexture : register(t0);

            #line 1
            SamplerState SpriteTextureSampler : register(s0);

            """;
        string vs = Prelude + resources + """
            #line 20
            float4 MainVS() : SV_Position
            {
                return 1.0f;
            }

            """;
        string ps = Prelude + resources + """
            #line 30
            float4 MainPS() : SV_TARGET
            {
                return 2.0f;
            }

            """;

        string merged = Merge([vs, ps], "MainVS", "MainPS");

        Count(merged, "Texture2D<float4 > SpriteTexture : register(t0);").ShouldBe(1);
        Count(merged, "SamplerState SpriteTextureSampler : register(s0);").ShouldBe(1);
    }
}
