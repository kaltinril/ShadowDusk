#nullable enable

using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Slang;

/// <summary>
/// Pure tests (no slangc, no disk) for the Slang frontend's managed passes — the entry
/// scanner, the emission post-processor, and the diagnostic parser. Everything asserted here
/// is either measured slangc v2026.14.1 behaviour (the emission shapes come from real
/// captures) or a Phase 61 A6 contract (what gets rejected, and how loudly).
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
        // A6's middle band: valid Slang whose stage has nowhere to land in an Effect. The
        // author has every reason to expect it to work, so silence would be the worst outcome.
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

public sealed class SlangHlslPostProcessorTests
{
    // A faithful miniature of slangc v2026.14.1's measured emission shape: per-module
    // prologue, #line directives, mangled names, synthetic registers, and the
    // parameter-group struct wrapping — twice over, as the multi-entry stdout concatenates.
    private const string TwoModuleEmission = """
        #pragma pack_matrix(column_major)
        #ifdef SLANG_HLSL_ENABLE_NVAPI
        #include "nvHLSLExtns.h"
        #endif

        #ifndef __DXC_VERSION_MAJOR
        // warning X3557: loop doesn't seem to do anything, forcing loop to unroll
        #pragma warning(disable : 3557)
        #endif

        #line 2 "probe.slang"
        struct SLANG_ParameterGroup_Params_0
        {
            float4x4 WorldViewProjection_0;
            float Desaturation_0;
        };

        #line 2
        cbuffer Params_0 : register(b0)
        {
            SLANG_ParameterGroup_Params_0 Params_0;
        }

        VSOut_0 MainVS(VSIn_0 input_0)
        {
            VSOut_0 o_0;
            o_0.Position_0 = mul(input_0.Position_1, Params_0.WorldViewProjection_0);
            return o_0;
        }

        #pragma pack_matrix(column_major)
        #ifdef SLANG_HLSL_ENABLE_NVAPI
        #include "nvHLSLExtns.h"
        #endif

        #ifndef __DXC_VERSION_MAJOR
        // warning X3557: loop doesn't seem to do anything, forcing loop to unroll
        #pragma warning(disable : 3557)
        #endif

        #line 8 "probe.slang"
        Texture2D<float4 > SpriteTexture_0 : register(t0);

        #line 2
        struct SLANG_ParameterGroup_Params_0
        {
            float4x4 WorldViewProjection_0;
            float Desaturation_0;
        };

        #line 2
        cbuffer Params_0 : register(b0)
        {
            SLANG_ParameterGroup_Params_0 Params_0;
        }

        float4 MainPS() : SV_TARGET
        {
            return (float4)Params_0.Desaturation_0;
        }
        """;

    [Fact]
    public void MergesModules_StripsBoilerplate_Demangles_AndFlattensTheParameterGroup()
    {
        var result = SlangHlslPostProcessor.Process(
            TwoModuleEmission, userSourceHadRegisters: false, "probe.slang");

        result.IsSuccess.ShouldBeTrue();
        string body = result.Value.Body;

        // Boilerplate and #line directives are gone.
        body.ShouldNotContain("pack_matrix", Case.Sensitive);
        body.ShouldNotContain("#line", Case.Sensitive);
        body.ShouldNotContain("NVAPI", Case.Sensitive);

        // The duplicated shared declarations collapsed to one.
        CountOf(body, "cbuffer Params").ShouldBe(1);

        // Demangled: the names the user wrote are the names in the HLSL — and therefore the
        // effect parameter names the consumer's Parameters["..."] lookups see.
        body.ShouldContain("WorldViewProjection", Case.Sensitive);
        body.ShouldContain("Desaturation", Case.Sensitive);
        body.ShouldContain("SpriteTexture", Case.Sensitive);
        body.ShouldNotContain("_0", Case.Sensitive);

        // The parameter-group wrapping is flattened: no struct, plain members, plain access.
        body.ShouldNotContain("SLANG_ParameterGroup", Case.Sensitive);
        body.ShouldNotContain("Params.", Case.Sensitive);

        // Synthetic registers stripped (the user's source declared none), so the pipeline's own
        // faithful allocation applies exactly as it does to a plain .fx.
        body.ShouldNotContain("register(", Case.Sensitive);
    }

    [Fact]
    public void UserRegisters_AreKept_AndAMergeConflictFailsLoudly()
    {
        const string conflicting = """
            Texture2D A_0 : register(t0);
            float4 MainPS() : SV_TARGET { return A_0.Load(int3(0,0,0)); }
            Texture2D B_0 : register(t0);
            float4 MainVS() : SV_POSITION { return B_0.Load(int3(0,0,0)); }
            """;

        var kept = SlangHlslPostProcessor.Process(conflicting, userSourceHadRegisters: true, "r.slang");
        kept.IsFailure.ShouldBeTrue();
        kept.Error.Single().Code.ShouldBe("SD0605");
        kept.Error.Single().Message.ShouldContain("t0", Case.Sensitive);
    }

    [Fact]
    public void RowMajorPragma_IsRejected_NeverPassedThrough()
    {
        // Passing it through would silently transpose every matrix against the pipeline's
        // layout conventions — the exact class of wrong-output this project refuses.
        var result = SlangHlslPostProcessor.Process(
            "#pragma pack_matrix(row_major)\nfloat4 f() { return 0; }",
            userSourceHadRegisters: false, "m.slang");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0607");
        result.Error.Single().Message.ShouldContain("row_major", Case.Sensitive);
    }

    [Fact]
    public void UnsafeRename_IsSkippedWithSD0606_NeverForced()
    {
        // 'Color' already exists as its own symbol, so 'Color_0' must stay mangled — the worst
        // case is a mangled parameter name, never a capture/miscompile.
        var result = SlangHlslPostProcessor.Process(
            "float4 Color;\nfloat4 Color_0;\nfloat4 f() { return Color + Color_0; }",
            userSourceHadRegisters: false, "c.slang");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Body.ShouldContain("Color_0", Case.Sensitive);
        result.Value.Warnings.Single().Code.ShouldBe("SD0606");
        result.Value.Warnings.Single().Message.ShouldContain("Color_0", Case.Sensitive);
    }

    [Fact]
    public void ParameterGroupSharedByAnotherUse_IsLeftAlone()
    {
        // The struct type appears beyond the cbuffer member (a function parameter), so
        // flattening would change meaning. It must survive untouched — and then be accepted or
        // loudly rejected downstream, never silently rewritten.
        const string shared = """
            struct SLANG_ParameterGroup_P_0 { float X_0; };
            cbuffer P_0 : register(b0) { SLANG_ParameterGroup_P_0 P_0; }
            float f(SLANG_ParameterGroup_P_0 p) { return p.X_0; }
            """;

        var result = SlangHlslPostProcessor.Process(shared, userSourceHadRegisters: false, "s.slang");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Body.ShouldContain("SLANG_ParameterGroup_P", Case.Sensitive);
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}

public sealed class SlangDiagnosticParserTests
{
    [Fact]
    public void RustcStyleDiagnostics_ParseToLocatedErrors_WithSlangsOwnCodeVerbatim()
    {
        const string stderr = """
            error[E20001]: unexpected token
             --> broken.slang:2:1
              |
            2 |
              | ^
            """;

        var errors = SlangToolchain.ParseDiagnostics(stderr, "broken.slang");

        var error = errors.Single();
        error.Code.ShouldBe("E20001");                       // slangc's code, never reworded
        error.Message.ShouldBe("unexpected token");          // slangc's text, verbatim
        error.File.ShouldBe("broken.slang");
        error.Line.ShouldBe(2);
        error.Column.ShouldBe(1);
    }

    [Fact]
    public void UnparseableStderr_BecomesOneSD0601_CarryingTheFullTextVerbatim()
    {
        var errors = SlangToolchain.ParseDiagnostics("segfault in slang-compiler.dll", "x.slang");

        errors.Single().Code.ShouldBe("SD0601");
        errors.Single().Message.ShouldContain("segfault in slang-compiler.dll", Case.Sensitive);
    }
}
