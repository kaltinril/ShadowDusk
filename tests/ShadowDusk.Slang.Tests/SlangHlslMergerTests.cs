#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Pure tests (no disk, no process) for <see cref="SlangHlslMerger"/>, using literal HLSL
/// text shaped exactly like real slangc <c>-target hlsl</c> emissions (Phase 66 A3: verified
/// against real slangc separately in <c>SlangCompilerTests</c> — this class pins the
/// dedup/merge algorithm itself against fixed strings so it does not need a slangc process
/// to run).
/// </summary>
public sealed class SlangHlslMergerTests
{
    private const string Prelude = """
        #pragma pack_matrix(column_major)
        #ifdef SLANG_HLSL_ENABLE_NVAPI
        #include "nvHLSLExtns.h"
        #endif

        """;

    [Fact]
    public void SingleEntry_ReturnedUnchanged()
    {
        string unit = Prelude + """

            #line 1
            float4 MainPS() : SV_Target
            {
                return 0;
            }

            """;

        string merged = SlangHlslMerger.Merge([unit]);

        merged.ShouldBe(unit);
    }

    [Fact]
    public void TwoEntries_SharedDeclaration_AppearsOnce()
    {
        // Mirrors a real VS+PS pair: both units redeclare the identical VSOutput_0 struct
        // (slangc emits each entry's translation unit fully self-contained), and each unit's
        // own function is unique to it.
        string sharedStruct = """
            #line 16
            struct VSOutput_0
            {
                float4 Position_0 : SV_Position;
            };

            """;

        string vsUnit = Prelude + sharedStruct + """
            #line 23
            VSOutput_0 MainVS()
            {
                VSOutput_0 o_0;
                return o_0;
            }

            """;

        string psUnit = Prelude + sharedStruct + """
            #line 34
            float4 MainPS(VSOutput_0 input_0) : SV_TARGET
            {
                return input_0.Position_0;
            }

            """;

        string merged = SlangHlslMerger.Merge([vsUnit, psUnit]);

        // The shared struct is not duplicated...
        CountOccurrences(merged, "struct VSOutput_0").ShouldBe(1);
        // ...but both entries' own functions survive.
        merged.ShouldContain("MainVS()", Case.Sensitive);
        merged.ShouldContain("MainPS(VSOutput_0 input_0)", Case.Sensitive);
        // The shared boilerplate header is written once, not once per entry.
        CountOccurrences(merged, "#pragma pack_matrix").ShouldBe(1);
    }

    [Fact]
    public void TwoEntries_DeclarationOrderIsPreserved_TypeBeforeFirstUse()
    {
        // The VS entry's own translation unit already orders its dependency (the cbuffer)
        // before the function that uses it; the merge must not reorder that relative to
        // anything the FIRST entry introduces.
        string vsUnit = Prelude + """
            #line 4
            cbuffer Params_0 : register(b0)
            {
                float4x4 WorldViewProjection_0;
            }

            #line 23
            float4 MainVS() : SV_Position
            {
                return mul(float4(0,0,0,1), WorldViewProjection_0);
            }

            """;

        string psUnit = Prelude + """
            #line 34
            float4 MainPS() : SV_TARGET
            {
                return 0;
            }

            """;

        string merged = SlangHlslMerger.Merge([vsUnit, psUnit]);

        int cbufferIndex = merged.IndexOf("cbuffer Params_0", StringComparison.Ordinal);
        int mainVsIndex = merged.IndexOf("float4 MainVS()", StringComparison.Ordinal);
        cbufferIndex.ShouldBeGreaterThanOrEqualTo(0);
        mainVsIndex.ShouldBeGreaterThan(cbufferIndex, "the cbuffer must still precede the function that uses it");
    }

    [Fact]
    public void DifferingLineDirectiveNumbers_StillDedupe_WhenBodyIsIdentical()
    {
        // Two entries compiled via stdin can legitimately report the same declaration under
        // slightly different #line bookkeeping; the dedup key ignores the #line line itself.
        string vsUnit = Prelude + """
            #line 16 "<stdin>"
            struct VSOutput_0
            {
                float4 Position_0 : SV_Position;
            };

            #line 23
            VSOutput_0 MainVS() { VSOutput_0 o_0; return o_0; }

            """;

        string psUnit = Prelude + """
            #line 16
            struct VSOutput_0
            {
                float4 Position_0 : SV_Position;
            };

            #line 34
            float4 MainPS(VSOutput_0 input_0) : SV_TARGET { return input_0.Position_0; }

            """;

        string merged = SlangHlslMerger.Merge([vsUnit, psUnit]);

        CountOccurrences(merged, "struct VSOutput_0").ShouldBe(1);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyString()
    {
        SlangHlslMerger.Merge([]).ShouldBe("");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
