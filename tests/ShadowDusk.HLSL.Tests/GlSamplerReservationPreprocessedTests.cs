#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Preprocessing;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #283: which modern <c>SamplerState X : register(sN)</c> declarations reserve an OpenGL
/// sampler register is decided on the PREPROCESSED source, like <c>mgfxc</c> 3.8.4.1, not on the
/// raw tokens. Measured against the pinned mgfxc <c>/Profile:OpenGL</c> (2026-10-02): a register
/// only in an inactive <c>#if</c> branch reserves nothing (<c>ps_s0</c>), registers spelled through
/// <c>#define SLOT(n) : register(n)</c> do reserve (<c>ps_s2</c>/<c>ps_s3</c>), and so does one in
/// an <c>#include</c>d file (<c>ps_s1</c>). These tests are pure: the source is handed over already
/// flattened, with the OpenGL macro prepend the compile uses.
/// </summary>
public sealed class GlSamplerReservationPreprocessedTests
{
    private static string Gl(string body, params UserDefine[] userDefines)
    {
        MacroSet macros = PlatformMacros.For(PlatformTarget.OpenGL) with { UserDefines = userDefines };
        return macros.ToTextPrepend("t.fx") + body;
    }

    private static IReadOnlySet<int> Reserved(string flattened)
    {
        var result = FxPreParser.CollectReservedGlSamplerSlots(flattened, "t.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value;
    }

    private static ShaderError Failure(string flattened)
    {
        var result = FxPreParser.CollectReservedGlSamplerSlots(flattened, "t.fx");
        result.IsFailure.ShouldBeTrue("the preprocessed view should not have been buildable");
        return result.Error;
    }

    [Fact]
    public void RegisterOnlyInTheInactiveBranch_ReservesNothing()
    {
        string src = Gl("""
            Texture2D SpriteTexture;
            #if OPENGL
            SamplerState SpriteSampler;
            #else
            SamplerState SpriteSampler : register(s0);
            #endif
            """);

        // The raw-token reading (the pre-fix behaviour) counts the dead branch.
        FxPreParser.Parse(src, "t.fx").Value.ReservedGlSamplerSlots.ShouldBe(new[] { 0 });
        Reserved(src).ShouldBeEmpty();
    }

    [Fact]
    public void RegisterOnlyInTheActiveBranch_IsReserved()
    {
        string src = Gl("""
            #ifndef OPENGL
            SamplerState S;
            #elif defined(GLSL) && MGFX
            SamplerState S : register(s3);
            #else
            SamplerState S : register(s5);
            #endif
            """);

        Reserved(src).ShouldBe(new[] { 3 });
    }

    [Fact]
    public void RegistersSpelledThroughAFunctionLikeMacro_AreReserved()
    {
        string src = Gl("""
            #define SLOT(n) : register(n)
            Texture2D MaskA;
            Texture2D MaskB;
            SamplerState SampA SLOT(s0);
            SamplerState SampB SLOT(s1);
            """);

        FxPreParser.Parse(src, "t.fx").Value.ReservedGlSamplerSlots.ShouldBeEmpty();
        Reserved(src).OrderBy(x => x).ShouldBe(new[] { 0, 1 });
    }

    [Fact]
    public void RegisterBuiltByTokenPasting_IsReserved()
    {
        string src = Gl("""
            #define DECLARE_SAMPLER(name, index) SamplerState name##Sampler : register(s##index)
            DECLARE_SAMPLER(Mask, 2);
            """);

        Reserved(src).ShouldBe(new[] { 2 });
    }

    [Fact]
    public void RegisterFromAUserDefine_IsReserved()
    {
        string src = Gl("SamplerState S : register(SAMPLER_REG);\n", new UserDefine("SAMPLER_REG", "s4"));

        Reserved(src).ShouldBe(new[] { 4 });
    }

    [Fact]
    public void RegisterGatedOnAnUndefinedUserDefine_ReservesNothing()
    {
        string src = Gl("""
            #if USE_FIXED_SLOTS
            SamplerState S : register(s1);
            #else
            SamplerState S;
            #endif
            """);

        Reserved(src).ShouldBeEmpty();
        Reserved(Gl(src[src.IndexOf("#if", StringComparison.Ordinal)..], new UserDefine("USE_FIXED_SLOTS")))
            .ShouldBe(new[] { 1 });
    }

    [Fact]
    public void RegisterInsideAComment_IsNotReserved()
    {
        string src = Gl("""
            SamplerState A; // SamplerState B : register(s1);
            /* SamplerState C : register(s2); */
            SamplerState D : register(s3);
            """);

        Reserved(src).ShouldBe(new[] { 3 });
    }

    [Fact]
    public void NestedObjectAndFunctionMacros_ExpandFully()
    {
        string src = Gl("""
            #define BASE 2
            #define NEXT (BASE + 1)
            #define REG(x) register(x)
            #define SLOT_OF(x) : REG(x)
            #if NEXT == 3 && (BASE << 1) == 4 && !defined(VULKAN) && (1 ? 1 : 1 / 0)
            SamplerState S SLOT_OF(s7);
            #endif
            """);

        Reserved(src).ShouldBe(new[] { 7 });
    }

    [Fact]
    public void UndefStopsAMacro()
    {
        string src = Gl("""
            #define SLOT(n) : register(n)
            #undef SLOT
            #ifdef SLOT
            SamplerState S : register(s1);
            #endif
            """);

        Reserved(src).ShouldBeEmpty();
    }

    [Fact]
    public void HeaderGuardedIncludeFlattenedTwice_ReservesOnce()
    {
        string header = "#ifndef SAMPLERS_FXH\n#define SAMPLERS_FXH\nSamplerState S : register(s1);\n#endif\n";
        string src = Gl("#line 1 \"a.fxh\"\n" + header + "#line 2 \"t.fx\"\n#line 1 \"a.fxh\"\n" + header);

        Reserved(src).ShouldBe(new[] { 1 });
    }

    [Fact]
    public void SelfReferentialMacro_DoesNotLoop()
    {
        string src = Gl("""
            #define register register
            SamplerState S : register(s1);
            """);

        Reserved(src).ShouldBe(new[] { 1 });
    }

    [Fact]
    public void VariadicMacro_ExpandsVaArgs()
    {
        string src = Gl("""
            #define DECL(type, ...) type __VA_ARGS__
            DECL(SamplerState, S : register(s6));
            """);

        Reserved(src).ShouldBe(new[] { 6 });
    }

    [Fact]
    public void UnterminatedConditional_FailsWithSd0009()
    {
        ShaderError error = Failure(Gl("#if OPENGL\nSamplerState S : register(s1);\n"));

        error.Code.ShouldBe("SD0009");
        error.Message.ShouldContain("#endif", Case.Sensitive);
    }

    [Fact]
    public void UnevaluableCondition_FailsWithSd0009AtTheLine()
    {
        ShaderError error = Failure(Gl("\n\n#if __has_include(\"x.fxh\")\n#endif\n"));

        error.Code.ShouldBe("SD0009");
        error.File.ShouldBe("t.fx");
        error.Line.ShouldBe(3);
    }

    [Fact]
    public void ElseWithoutIf_FailsWithSd0009()
    {
        Failure(Gl("#else\n")).Code.ShouldBe("SD0009");
    }

    [Fact]
    public void WrongMacroArgumentCount_FailsWithSd0009()
    {
        Failure(Gl("#define SLOT(a, b) : register(a)\nSamplerState S SLOT(s1);\n")).Code.ShouldBe("SD0009");
    }

    // -------------------------------------------------------------------------
    // The preprocessed view itself
    // -------------------------------------------------------------------------

    [Fact]
    public void View_StringizesAndDropsInactiveText()
    {
        var view = FxMacroPreprocessor.Process(
            "#define STR(x) #x\n#if 0\nhidden\n#elif 2 > 1\nshown STR(a  +  \"b\")\n#endif\n", "t.fx");

        view.IsSuccess.ShouldBeTrue();
        view.Value.ShouldNotContain("hidden", Case.Sensitive);
        view.Value.ShouldContain("shown", Case.Sensitive);
        view.Value.ShouldContain("\"a + \\\"b\\\"\"", Case.Sensitive);
    }

    [Fact]
    public void View_FunctionLikeMacroNameWithoutParens_IsNotExpanded()
    {
        var view = FxMacroPreprocessor.Process("#define F(x) x x\nF;\nF(y);\n", "t.fx");

        view.IsSuccess.ShouldBeTrue();
        view.Value.ShouldContain("F;", Case.Sensitive);
        view.Value.ShouldContain("y y", Case.Sensitive);
    }

    [Fact]
    public void View_LineContinuationJoinsADefine()
    {
        var view = FxMacroPreprocessor.Process("#define A 1 + \\\n 2\n#if A == 3\nyes\n#endif\n", "t.fx");

        view.IsSuccess.ShouldBeTrue();
        view.Value.ShouldContain("yes", Case.Sensitive);
    }
}
