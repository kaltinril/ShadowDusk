#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #252: on OpenGL, the real-slangc route must put a texture on the same sampler slot
/// the <c>.fx</c> route gives the equivalent hand-written HLSL. slangc numbers every
/// resource itself, and the GL allocator reads a <c>SamplerState : register(sN)</c> as an
/// author reservation, so before the fix a single texture landed on <c>ps_s1</c> where
/// SpriteBatch (unit 0) never reached it. The rendered proof is
/// <c>validation/SlangTexturedGl</c>; this pins the slot table on every host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangGlSamplerSlotTests
{
    private const string PixelShader = """
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(SpriteSampler, uv);
        }
        """;

    private const string FxPixelShader = """
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(SpriteSampler, uv);
        }

        technique T
        {
            pass P0
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        }
        """;

    private const string FxHeader = """
        #if SM4
            #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
            #define PS_SHADERMODEL ps_3_0
        #endif

        """;

    private static IReadOnlyList<MgfxSamplerRecord> SlangSamplers(string declarations) =>
        SlangSamplers(declarations + "\n" + PixelShader, PlatformTarget.OpenGL);

    private static IReadOnlyList<MgfxSamplerRecord> FxSamplers(string declarations) =>
        FxSamplers(FxHeader + declarations + "\n" + FxPixelShader, PlatformTarget.OpenGL);

    private static IReadOnlyList<MgfxSamplerRecord> SlangSamplers(
        string source, PlatformTarget target, params UserDefine[] defines) =>
        SlangEffect(source, target, defines).Samplers;

    private static MgfxBlobReader SlangEffect(
        string source, PlatformTarget target, params UserDefine[] defines)
    {
        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = target, SourceFileName = "Slot.slang", Defines = defines });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static IReadOnlyList<MgfxSamplerRecord> FxSamplers(
        string source, PlatformTarget target, params UserDefine[] defines)
    {
        var result = new EffectCompiler().Compile(
            source, new CompilerOptions { Target = target, SourceFileName = "Slot.fx", Defines = defines });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        return MgfxBlobReader.Parse(result.Value.Data).Samplers;
    }

    private static (byte Texture, byte Sampler)[] Slots(IReadOnlyList<MgfxSamplerRecord> samplers) =>
        samplers.Select(s => (s.TextureSlot, s.SamplerSlot)).ToArray();

    [Fact]
    public void UnannotatedTexture_LandsOnUnitZero_LikeTheFxRoute()
    {
        const string decls = "Texture2D SpriteTexture;\nSamplerState SpriteSampler;\n";

        var slang = SlangSamplers(decls);
        var fx = FxSamplers(decls);

        slang.Count.ShouldBe(1);
        slang[0].TextureSlot.ShouldBe((byte)0);
        slang[0].SamplerSlot.ShouldBe((byte)0);
        slang.Select(s => (s.TextureSlot, s.SamplerSlot)).ShouldBe(fx.Select(s => (s.TextureSlot, s.SamplerSlot)));
    }

    [Fact]
    public void AuthorWrittenSamplerRegister_IsHonoured_LikeTheFxRoute()
    {
        // The author's own 'register(s0)' on a modern SamplerState is a reservation under the
        // .fx route's (mgfxc-measured) rule, which moves the pair to ps_s1. Stripping must not
        // touch it, or the two routes would disagree on hand-written intent.
        const string decls = "Texture2D SpriteTexture;\nSamplerState SpriteSampler : register(s0);\n";

        var slang = SlangSamplers(decls);
        var fx = FxSamplers(decls);

        slang.Count.ShouldBe(1);
        slang[0].TextureSlot.ShouldBe((byte)1);
        slang.Select(s => (s.TextureSlot, s.SamplerSlot)).ShouldBe(fx.Select(s => (s.TextureSlot, s.SamplerSlot)));
    }

    [Fact]
    public void VkBindingOnly_IsNotAGlReservation_LikeTheFxRoute()
    {
        // slangc drops vk::binding from its HLSL and invents register(s0); the .fx route never
        // reads vk::binding as a GL reservation, so the texture stays on unit 0 on both.
        const string decls = "Texture2D SpriteTexture;\n[[vk::binding(3)]] SamplerState SpriteSampler;\n";

        var slang = SlangSamplers(decls);
        var fx = FxSamplers(decls);

        slang.Count.ShouldBe(1);
        slang[0].TextureSlot.ShouldBe((byte)0);
        slang.Select(s => (s.TextureSlot, s.SamplerSlot)).ShouldBe(fx.Select(s => (s.TextureSlot, s.SamplerSlot)));
    }

    // ---------------------------------------------------------------------------------------
    // Issue #252 follow-up: "the author wrote a register" is decided AFTER preprocessing, with
    // the macros the compile uses. Before it, the raw source text was scanned, so a register in
    // an inactive #if branch counted (and kept slangc's invented number: the original ps_s1
    // symptom), and a register written through a macro did not (and was stripped).
    //
    // Each shape must land exactly where the declarations the preprocessor LEAVES land when an
    // author writes them out by hand, on the Slang route and on the .fx route alike. That is
    // also what the reference compiler does with the same text in a .fx file (mgfxc 3.8.4.1
    // /Profile:OpenGL, measured 2026-10-01: the '#if OPENGL' shape below gives ps_s0, and two
    // textures registered through a macro give ps_s2 + ps_s3, both identical to its build of
    // the hand-resolved text). The .fx comparison uses the hand-resolved text on purpose: the
    // .fx route's own GL reservation scan (FxPreParser) still reads unpreprocessed tokens and
    // disagrees with mgfxc on exactly these two shapes (recorded as a known gap in
    // project_facts.md), so it is not a usable oracle for the unresolved text.

    private const string Unregistered = "Texture2D SpriteTexture;\nSamplerState SpriteSampler;\n";

    private const string SamplerOnSlotZero = "Texture2D SpriteTexture;\nSamplerState SpriteSampler : register(s0);\n";

    private const string BothOnSlotOne = "Texture2D SpriteTexture : register(t1);\nSamplerState SpriteSampler : register(s1);\n";

    // The reported shape: the register exists only in the branch OpenGL does not take.
    private const string RegisterOnlyOutsideOpenGl =
        "Texture2D SpriteTexture;\n#if OPENGL\nSamplerState SpriteSampler;\n#else\nSamplerState SpriteSampler : register(s0);\n#endif\n";

    // Each target takes a different branch, and only DirectX's writes registers.
    private const string SlotOneOutsideOpenGl =
        "#if OPENGL\n" + Unregistered + "#else\n" + BothOnSlotOne + "#endif\n";

    // The mirror image: only OpenGL's branch writes registers.
    private const string SlotOneOnlyOnOpenGl =
        "#if OPENGL\n" + BothOnSlotOne + "#else\n" + Unregistered + "#endif\n";

    private const string SlotOneThroughAMacro =
        "#define SLOT(n) : register(n)\nTexture2D SpriteTexture SLOT(t1);\nSamplerState SpriteSampler SLOT(s1);\n";

    private const string SlotOneThroughDefines =
        "Texture2D SpriteTexture TEXTURE_SLOT;\nSamplerState SpriteSampler SAMPLER_SLOT;\n";

    private static readonly UserDefine[] SlotDefines =
    [
        new("TEXTURE_SLOT", ": register(t1)"),
        new("SAMPLER_SLOT", ": register(s1)"),
    ];

    private static string Slang(string declarations) => declarations + PixelShader;

    private static string Fx(string declarations) => FxHeader + declarations + FxPixelShader;

    [Theory]
    // declarations, target, what the preprocessor leaves for that target, the slot it lands on.
    // OpenGL: an unregistered pair takes unit 0; 'register(s0)' on the sampler is a reservation
    // (mgfxc's rule) and pushes the pair to ps_s1; 'register(s1)' reserves 1, so the pair stays
    // on 0. DirectX honours the registers outright.
    [InlineData(RegisterOnlyOutsideOpenGl, PlatformTarget.OpenGL, Unregistered, 0)]
    [InlineData(RegisterOnlyOutsideOpenGl, PlatformTarget.DirectX, SamplerOnSlotZero, 0)]
    [InlineData(SlotOneOutsideOpenGl, PlatformTarget.OpenGL, Unregistered, 0)]
    [InlineData(SlotOneOutsideOpenGl, PlatformTarget.DirectX, BothOnSlotOne, 1)]
    [InlineData(SlotOneOnlyOnOpenGl, PlatformTarget.OpenGL, BothOnSlotOne, 0)]
    [InlineData(SlotOneOnlyOnOpenGl, PlatformTarget.DirectX, Unregistered, 0)]
    [InlineData(SlotOneThroughAMacro, PlatformTarget.OpenGL, BothOnSlotOne, 0)]
    [InlineData(SlotOneThroughAMacro, PlatformTarget.DirectX, BothOnSlotOne, 1)]
    public void PreprocessorDependentRegisters_LandWhereTheResolvedDeclarationsDo(
        string declarations, PlatformTarget target, string resolved, int expectedSlot)
    {
        var slang = SlangSamplers(Slang(declarations), target);
        var slangResolved = SlangSamplers(Slang(resolved), target);
        var fxResolved = FxSamplers(Fx(resolved), target);

        Slots(slang).ShouldBe([((byte)expectedSlot, (byte)expectedSlot)]);
        Slots(slang).ShouldBe(Slots(slangResolved));
        Slots(slang).ShouldBe(Slots(fxResolved));
    }

    [Fact]
    public void SamplerRegisterOnlyInAnInactiveBranch_OpenGL_IsNotAReservation()
    {
        // The #252 symptom through the back door: with the register counted as the author's,
        // slangc's invented register(s0) survived and the texture landed on ps_s1. The same
        // source with the register in the LIVE branch must still be honoured (ps_s1).
        var inactive = SlangSamplers(Slang(RegisterOnlyOutsideOpenGl), PlatformTarget.OpenGL);
        var live = SlangSamplers(Slang(SamplerOnSlotZero), PlatformTarget.OpenGL);

        Slots(inactive).ShouldBe([((byte)0, (byte)0)]);
        Slots(live).ShouldBe([((byte)1, (byte)1)]);
    }

    [Fact]
    public void RegisterWrittenThroughAUserDefine_DirectX_IsHonoured()
    {
        // The source never says 'register'; the -D values do, and slangc's preprocessor sees them.
        var slang = SlangSamplers(Slang(SlotOneThroughDefines), PlatformTarget.DirectX, SlotDefines);
        var fxResolved = FxSamplers(Fx(BothOnSlotOne), PlatformTarget.DirectX);

        Slots(slang).ShouldBe([((byte)1, (byte)1)]);
        Slots(slang).ShouldBe(Slots(fxResolved));
    }

    private const string TwoTexturePixelShader = """
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Overlay.Sample(OverlaySampler, uv) * Base.Sample(BaseSampler, uv);
        }
        """;

    private const string TwoTexturesThroughAMacro =
        "#define SLOT(n) : register(n)\n" +
        "Texture2D Overlay SLOT(t1);\nSamplerState OverlaySampler SLOT(s1);\n" +
        "Texture2D Base SLOT(t0);\nSamplerState BaseSampler SLOT(s0);\n";

    private const string TwoTexturesWrittenDirectly =
        "Texture2D Overlay : register(t1);\nSamplerState OverlaySampler : register(s1);\n" +
        "Texture2D Base : register(t0);\nSamplerState BaseSampler : register(s0);\n";

    private static string TwoTextureSlang(string declarations) =>
        declarations + "[shader(\"fragment\")]\n" + TwoTexturePixelShader;

    private static string TwoTextureFx(string declarations) =>
        FxHeader + declarations + TwoTexturePixelShader +
        "\n\ntechnique T\n{\n    pass P0\n    {\n        PixelShader = compile PS_SHADERMODEL MainPS();\n    }\n}\n";

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public void TwoTexturesRegisteredThroughAMacro_MatchTheDirectForm_OnBothRoutes(PlatformTarget target)
    {
        // On OpenGL both modern sampler registers are reservations (mgfxc's rule), so the two
        // pairs go AROUND s0 and s1, to units 2 and 3; with the macro's registers stripped they
        // landed on units 0 and 1. On DirectX the registers are honoured outright (0 and 1).
        MgfxBlobReader macroEffect = SlangEffect(TwoTextureSlang(TwoTexturesThroughAMacro), target);
        var macro = macroEffect.Samplers;
        MgfxBlobReader directEffect = SlangEffect(TwoTextureSlang(TwoTexturesWrittenDirectly), target);
        var direct = directEffect.Samplers;
        var fxDirect = FxSamplers(TwoTextureFx(TwoTexturesWrittenDirectly), target);

        (byte, byte)[] expected = target == PlatformTarget.OpenGL
            ? [(2, 2), (3, 3)]
            : [(0, 0), (1, 1)];
        Slots(macro).OrderBy(s => s.Texture).ShouldBe(expected);
        Slots(macro).ShouldBe(Slots(direct));
        Slots(macro).OrderBy(s => s.Texture).ShouldBe(Slots(fxDirect).OrderBy(s => s.Texture));

        // Per texture, not just as a set: with the macro's registers stripped, DirectX numbered
        // the two textures in declaration and use order (Overlay first) and swapped them
        // (Overlay on 0, Base on 1), which the slot set alone cannot see.
        Named(macroEffect).ShouldBe(Named(directEffect));
        if (target == PlatformTarget.DirectX)
            Named(macroEffect).ShouldBe([("Base", (byte)0, (byte)0), ("Overlay", (byte)1, (byte)1)]);
    }

    // ---------------------------------------------------------------------------------------
    // Coverage added after the #252 review: 3+ unannotated textures, texture arrays on GL, and
    // an author register on a combined Sampler2D.

    private const string ThreeTextureShader = """
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return A.Sample(SA, uv) + B.Sample(SB, uv) + C.Sample(SC, uv);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public void ThreeUnannotatedTextures_LandOnUnitsZeroOneTwo_InDeclarationOrder(PlatformTarget target)
    {
        const string decls =
            "Texture2D A;\nSamplerState SA;\nTexture2D B;\nSamplerState SB;\nTexture2D C;\nSamplerState SC;\n";
        MgfxBlobReader effect = SlangEffect(decls + ThreeTextureShader, target);

        Named(effect).ShouldBe([("A", (byte)0, (byte)0), ("B", (byte)1, (byte)1), ("C", (byte)2, (byte)2)]);
    }

    [Fact]
    public void ThreeCombinedSamplers_OnOpenGL_LandOnUnitsZeroOneTwo()
    {
        const string source = """
            Sampler2D A;
            Sampler2D B;
            Sampler2D C;
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return A.Sample(uv) + B.Sample(uv) + C.Sample(uv);
            }
            """;

        Named(SlangEffect(source, PlatformTarget.OpenGL))
            .ShouldBe([("A", (byte)0, (byte)0), ("B", (byte)1, (byte)1), ("C", (byte)2, (byte)2)]);
    }

    // The .fx reference for both array shapes below: an array of textures sampled through one
    // SamplerState, which the .fx route refuses on OpenGL with SD0217 (as real mgfxc refuses it).
    private const string FxTextureArrayReference = """
        Texture2D Tex[3];
        SamplerState S;
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Tex[0].Sample(S, uv) + Tex[1].Sample(S, uv) + Tex[2].Sample(S, uv);
        }
        technique T { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } }
        """;

    public static TheoryData<string, string> GlArrayShapes() => new()
    {
        // MonoGame's GL effect format has one texture per named sampler uniform, so an array
        // of textures has no slots to land on.
        {
            """
            Texture2D Tex[3];
            SamplerState S;
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Tex[0].Sample(S, uv) + Tex[1].Sample(S, uv) + Tex[2].Sample(S, uv);
            }
            """,
            "'Tex' is declared as an array of textures"
        },
        // Issue #356: slangc lowers 'Sampler2D Comb[3]' to a texture array plus a SAMPLER array,
        // which SPIRV-Cross cannot remap (it used to surface as its bare SD0100). Same verdict,
        // same code, as the texture-array shape.
        {
            """
            Sampler2D Comb[3];
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Comb[0].Sample(uv) + Comb[1].Sample(uv) + Comb[2].Sample(uv);
            }
            """,
            "'Comb' is declared as an array of combined samplers ('Sampler2D Comb[...]')"
        },
    };

    [Theory]
    [MemberData(nameof(GlArrayShapes))]
    public void TextureOrCombinedSamplerArray_OnOpenGL_IsRefusedWithSd0217_LikeTheFxRoute(string source, string named)
    {
        // The Slang route must refuse both shapes exactly as the .fx route refuses the texture
        // array, naming the author's declaration, and never invent consecutive units.
        var slang = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Arr.slang" });
        var fx = new EffectCompiler().Compile(
            FxHeader + FxTextureArrayReference,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Arr.fx" });

        fx.IsFailure.ShouldBeTrue("the .fx route's own verdict is the reference");
        slang.IsFailure.ShouldBeTrue("an array of textures has no GL sampler slots");
        ShaderError error = slang.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0217");
        error.Code.ShouldBe(fx.Error[0].Code);
        error.Message.ShouldContain(named, Case.Sensitive);
    }

    [Fact]
    public void CombinedSamplerArray_OnOpenGL_IsLocatedAtTheAuthorsDeclaration_AndKeepsSpirvCrossVerbatim()
    {
        // Issue #356: the SD0217 points at the author's line (not slangc's lowering, which has
        // none), tells the author what to do, and keeps SPIRV-Cross's own report word for word.
        const string source = """
            float4 Tint;
            Sampler2D Comb[3];
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Tint * (Comb[0].Sample(uv) + Comb[2].Sample(uv));
            }
            """;

        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Comb.slang" });

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Comb.slang", 2, 1, "SD0217"));
        error.Message.ShouldContain("Declare each element as its own Sampler2D and sample each by name.", Case.Sensitive);
        const string spirvCross =
            "SPIRV-Cross [build_combined_image_samplers]: Attempting to use arrays or structs of separate samplers.";
        error.Message.ShouldContain(spirvCross, Case.Sensitive);
        error.RawDiagnostics.ShouldNotBeNull().ShouldStartWith(spirvCross, Case.Sensitive);
    }

    [Fact]
    public void CombinedCubeSamplerArray_OnOpenGL_IsSd0217_InItsOwnTypesWords()
    {
        const string source = """
            SamplerCube Sky[2];
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float3 dir : TEXCOORD0) : SV_Target
            {
                return Sky[0].Sample(dir) + Sky[1].Sample(dir);
            }
            """;

        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Sky.slang" });

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.Line, error.Code).ShouldBe((1, "SD0217"));
        error.Message.ShouldContain("'Sky' is declared as an array of combined samplers ('SamplerCube Sky[...]')", Case.Sensitive);
        error.Message.ShouldContain("'TextureCube Sky[N]'", Case.Sensitive);
        error.Message.ShouldContain("its own SamplerCube", Case.Sensitive);
    }

    [Fact]
    public void AuthorSamplerArrayNamedLikeSlangcsLowering_OnOpenGL_StaysSpirvCrossesOwnSd0100()
    {
        // The control for issue #356's rewrite: 'My_sampler_0[2]' is shaped like slangc's lowering
        // of a combined 'Sampler2D My[2]', but the author wrote it as a SamplerState array and no
        // 'Sampler2D My[...]' exists. It must stay SPIRV-Cross's SD0100, never claim a Sampler2D.
        const string source = """
            Texture2D Tex;
            SamplerState My_sampler_0[2];
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Tex.Sample(My_sampler_0[1], uv);
            }
            """;

        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "My.slang" });

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0100");
        error.Message.ShouldStartWith("SPIRV-Cross [build_combined_image_samplers]:", Case.Sensitive);
        error.Message.ShouldNotContain("Sampler2D", Case.Sensitive);
    }

    [Fact]
    public void AuthorWrittenSamplerArray_OnOpenGL_StaysSpirvCrossesOwnSd0100()
    {
        // The control: an author-written 'SamplerState S[2]' is not a combined sampler slangc
        // lowered, and the .fx route reports the same shape as SPIRV-Cross's SD0100. Issue #356
        // must not relabel it.
        const string source = """
            Texture2D T;
            SamplerState S[2];
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return T.Sample(S[1], uv);
            }
            """;

        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "S.slang" });

        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0100");
        error.Message.ShouldStartWith("SPIRV-Cross [build_combined_image_samplers]:", Case.Sensitive);
    }

    private const string CombinedPixelShader = """
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(uv);
        }
        """;

    [Theory]
    // slangc splits 'Sampler2D X : register(rN)' into a texture half and a sampler half: tN goes
    // to the texture, sN to the sampler, and the OTHER half gets slangc's own number (stripped).
    // The oracle is the hand-written pair carrying only the author's register.
    // OpenGL: a texture register is not a reservation (mgfxc's rule). A SAMPLER register on a
    // combined sampler is the legacy combined sampler's register on OpenGL instead (see
    // CombinedSamplerRegister_OnOpenGL_PinsTheUnitMgfxcGivesTheLegacySampler).
    [InlineData("Sampler2D SpriteTexture : register(t2);\n", PlatformTarget.OpenGL,
        "Texture2D SpriteTexture : register(t2);\nSamplerState SpriteSampler;\n", 0)]
    [InlineData("Sampler2D SpriteTexture : register(t2);\n", PlatformTarget.DirectX,
        "Texture2D SpriteTexture : register(t2);\nSamplerState SpriteSampler;\n", 2)]
    public void AuthorRegisterOnACombinedSampler_IsHonoured_LikeTheHandWrittenPair(
        string combined, PlatformTarget target, string handWritten, int expectedSlot)
    {
        var slang = SlangSamplers(combined + CombinedPixelShader, target);
        var fx = FxSamplers(FxHeader + handWritten + FxPixelShader, target);

        Slots(slang).ShouldBe([((byte)expectedSlot, (byte)expectedSlot)]);
        Slots(slang).ShouldBe(Slots(fx));
    }

    public static TheoryData<string, string, string, string, (string, byte)[]> LegacyCombinedSamplerCases() => new()
    {
        // Slang declarations, the legacy .fx declarations, the Slang and .fx return expressions,
        // and the units real mgfxc 3.8.4.1 /Profile:OpenGL gives the legacy shader (measured
        // 2026-10-03, decoded with validation/decode_mgfx.py: texture parameter -> ps_sN unit).
        { "Sampler2D A : register(s0);", "sampler2D A : register(s0);", "A.Sample(uv)", "tex2D(A, uv)", [("A", 0)] },
        { "Sampler2D A : register(s1);", "sampler2D A : register(s1);", "A.Sample(uv)", "tex2D(A, uv)", [("A", 1)] },
        { "Sampler2D A : register(s2);", "sampler2D A : register(s2);", "A.Sample(uv)", "tex2D(A, uv)", [("A", 2)] },
        {
            "Sampler2D A : register(s1);\nSampler2D B : register(s0);", "sampler2D A : register(s1);\nsampler2D B : register(s0);",
            "A.Sample(uv) + B.Sample(uv)", "tex2D(A, uv) + tex2D(B, uv)", [("A", 1), ("B", 0)]
        },
        {
            "Sampler2D A : register(s2);\nSampler2D B;", "sampler2D A : register(s2);\nsampler2D B;",
            "A.Sample(uv) + B.Sample(uv)", "tex2D(A, uv) + tex2D(B, uv)", [("A", 2), ("B", 0)]
        },
        {
            "Sampler2D A;\nSampler2D B : register(s0);", "sampler2D A;\nsampler2D B : register(s0);",
            "A.Sample(uv) + B.Sample(uv)", "tex2D(A, uv) + tex2D(B, uv)", [("A", 1), ("B", 0)]
        },
        {
            // Sampled in the reverse of their declaration order.
            "Sampler2D A : register(s0);\nSampler2D B : register(s1);", "sampler2D A : register(s0);\nsampler2D B : register(s1);",
            "B.Sample(uv) + A.Sample(uv)", "tex2D(B, uv) + tex2D(A, uv)", [("A", 0), ("B", 1)]
        },
    };

    [Theory]
    [MemberData(nameof(LegacyCombinedSamplerCases))]
    public void CombinedSamplerRegister_OnOpenGL_PinsTheUnitMgfxcGivesTheLegacySampler(
        string slangDeclarations, string fxDeclarations, string slangSample, string fxSample, (string, byte)[] mgfxcUnits)
    {
        // A combined Sampler2D is the legacy combined sampler2D; its sampler register is the unit
        // the texture binds on OpenGL (the issue #252 symptom otherwise: 'register(s0)' landed on
        // unit 1, off SpriteBatch's unit 0, while DirectX 11 sampled t0). The split
        // 'Texture2D + SamplerState : register(sN)' pair keeps mgfxc's reservation rule
        // (AuthorWrittenSamplerRegister_IsHonoured_LikeTheFxRoute).
        MgfxBlobReader slang = SlangEffect(
            slangDeclarations + "\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return " +
            slangSample + "; }\n",
            PlatformTarget.OpenGL);
        var fx = new EffectCompiler().Compile(
            FxHeader + fxDeclarations + "\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0 { return " + fxSample +
            "; }\ntechnique T { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n",
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Legacy.fx" });
        fx.IsSuccess.ShouldBeTrue(fx.IsFailure ? string.Join("; ", fx.Error.Select(e => e.FxcFormattedMessage)) : "");

        var expected = mgfxcUnits.Select(u => (u.Item1, u.Item2, u.Item2)).ToArray();
        Named(slang).ShouldBe(expected);
        // The uniform each record names is the one the GLSL samples through (ps_s<unit>).
        slang.Samplers.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(mgfxcUnits.Select(u => $"ps_s{u.Item2}").OrderBy(n => n, StringComparer.Ordinal));
        // The .fx route's units already match mgfxc (issue #189); it names a bare legacy sampler's
        // texture parameter 'A_SDTexture' where mgfxc says 'A', so the units are compared by name stem.
        FxNamed(MgfxBlobReader.Parse(fx.Value.Data)).ShouldBe(expected, "the .fx route already matches mgfxc (issue #189)");
    }

    [Fact]
    public void CombinedSamplerRegister_BesideASplitPair_OnOpenGL_MatchesTheLegacyAndModernFxMix()
    {
        // A combined sampler pins its unit; an author's own split pair beside it keeps the
        // split-pair rule. The oracle is the .fx route's legacy-plus-modern mix (its allocator is
        // the one verified against mgfxc on 10 shapes, issue #189).
        MgfxBlobReader slang = SlangEffect("""
            Sampler2D A : register(s1);
            Texture2D T;
            SamplerState S : register(s0);
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Sample(uv) + T.Sample(S, uv); }
            """, PlatformTarget.OpenGL);
        var fx = new EffectCompiler().Compile(FxHeader + """
            sampler2D A : register(s1);
            Texture2D T;
            SamplerState S : register(s0);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) + T.Sample(S, uv); }
            technique T0 { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Mix.fx" });
        fx.IsSuccess.ShouldBeTrue(fx.IsFailure ? string.Join("; ", fx.Error.Select(e => e.FxcFormattedMessage)) : "");

        Named(slang).ShouldBe(FxNamed(MgfxBlobReader.Parse(fx.Value.Data)));
        Named(slang).ShouldBe([("A", (byte)1, (byte)1), ("T", (byte)2, (byte)2)]);
    }

    [Fact]
    public void TwoCombinedSamplersOnOneRegister_OnOpenGL_AreRefusedLoudly()
    {
        // fxc refuses the legacy pair (X4500) and the DirectX targets refuse it too; on OpenGL the
        // allocator would silently move the second texture to another unit.
        var result = new SlangCompiler().Compile("""
            Sampler2D A : register(s0);
            Sampler2D B : register(s0);
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Sample(uv) + B.Sample(uv); }
            """, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Dup.slang" });

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe(("Dup.slang", 2, "SD0644"));
        error.Message.ShouldContain("'A' and 'B' all declare register(s0)", Case.Sensitive);
    }

    [Fact]
    public void CombinedSamplerSharingARegisterWithASplitSamplerState_OnOpenGL_MatchesTheFxRoute()
    {
        // mgfxc accepts a legacy sampler and a modern SamplerState on one register; so does this.
        MgfxBlobReader slang = SlangEffect("""
            Sampler2D A : register(s0);
            Texture2D T;
            SamplerState S : register(s0);
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return T.Sample(S, uv) + A.Sample(uv); }
            """, PlatformTarget.OpenGL);
        var fx = new EffectCompiler().Compile(FxHeader + """
            sampler2D A : register(s0);
            Texture2D T;
            SamplerState S : register(s0);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0 { return T.Sample(S, uv) + tex2D(A, uv); }
            technique T0 { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Share.fx" });
        fx.IsSuccess.ShouldBeTrue(fx.IsFailure ? string.Join("; ", fx.Error.Select(e => e.FxcFormattedMessage)) : "");

        Named(slang).ShouldBe(FxNamed(MgfxBlobReader.Parse(fx.Value.Data)));
        Named(slang).ShouldBe([("A", (byte)0, (byte)0), ("T", (byte)1, (byte)1)]);
    }

    [Fact]
    public void CombinedSamplerRegisterWithASpace_OnOpenGL_PinsTheUnit()
    {
        // OpenGL has no register spaces: 'register(s1, space1)' is unit 1 like 'register(s1)'.
        Named(SlangEffect("""
            Sampler2D A : register(s1, space1);
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Sample(uv); }
            """, PlatformTarget.OpenGL)).ShouldBe([("A", (byte)1, (byte)1)]);
    }

    [Theory]
    // slangc emits globals in FIRST-USE order; mgfxc fills GL units in DECLARATION order. Each
    // shape samples the later-declared texture first. Measured with mgfxc 3.8.4.1 for the first
    // (T unit 1, A unit 2); the .fx route's allocator is the oracle for both.
    [InlineData("Texture2D T;\nSamplerState S : register(s0);\nSampler2D A;", "sampler2D A;", "T1A2")]
    [InlineData("Texture2D T;\nSamplerState S;\nSampler2D A;", "sampler2D A;", "T0A1")]
    public void UnitsFollowTheAuthorsDeclarationOrder_NotSlangcsFirstUseOrder(string slangDeclarations, string fxLegacy, string expected)
    {
        string split = slangDeclarations[..slangDeclarations.LastIndexOf('\n')];
        MgfxBlobReader slang = SlangEffect(
            slangDeclarations + "\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Sample(uv) + T.Sample(S, uv); }\n",
            PlatformTarget.OpenGL);
        var fx = new EffectCompiler().Compile(
            FxHeader + split + "\n" + fxLegacy +
            "\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) + T.Sample(S, uv); }\ntechnique T0 { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n",
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Order.fx" });
        fx.IsSuccess.ShouldBeTrue(fx.IsFailure ? string.Join("; ", fx.Error.Select(e => e.FxcFormattedMessage)) : "");

        Named(slang).ShouldBe(FxNamed(MgfxBlobReader.Parse(fx.Value.Data)));
        string.Concat(Named(slang).OrderBy(s => s.TextureSlot).Select(s => s.Texture + s.TextureSlot)).ShouldBe(expected);
    }

    private const string CombinedAPixelShader = """
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return A.Sample(uv);
        }
        """;

    // The pixel shader's DXBC bindings, read from its own reflection chunk.
    private static (string Textures, string Samplers) DxbcBindings(MgfxBlobReader effect)
    {
        MgfxShaderRecord ps = effect.Shaders.Single(s => !s.IsVertex);
        var reflected = ShadowDusk.Core.Reflection.RdefReader.Read(ps.Bytecode);
        reflected.IsSuccess.ShouldBeTrue(reflected.IsFailure ? reflected.Error.Message : "");
        return (
            string.Join(",", reflected.Value.Textures.Select(t => $"{t.Name}@t{t.BindSlot}")),
            string.Join(",", reflected.Value.Samplers.Select(s => $"@s{s.BindSlot}")));
    }

    [Fact]
    public void SamplerRegisterOnACombinedSampler_OnDirectX11_BindsTheSamplerHalf_TextureStaysOnT0_LikeMgfxcsLegacySampler()
    {
        // Issue #355. 'Sampler2D A : register(s2)' on DirectX 11 compiles to texture t0 + sampler
        // s2, and that is the reference behaviour, not a lost register:
        //  * slangc binds a register(sN) on a combined sampler to its SAMPLER half only and numbers
        //    the texture half itself (its emission: 'Texture2D A;' + 'SamplerState A_sampler_0 :
        //    register(s2);'), and ShadowDusk keeps exactly the author's s2 (issue #252);
        //  * the hand-written combined equivalent, the legacy 'sampler2D A : register(s2);' sampled
        //    with tex2D, compiles under real mgfxc 3.8.4.1 /Profile:DirectX_11 to the same bindings.
        //    Measured 2026-10-03 with 'fxc /dumpbin' on mgfxc's own shader: 'dcl_sampler s2' +
        //    'dcl_resource_texture2d t0' + 'sample ..., t0, s2', and a sampler record with texture
        //    slot 0. So under mgfxc too, GraphicsDevice.Textures[2] does NOT reach that texture on
        //    DirectX 11; the effect parameter (or Textures[0]) does, and SamplerStates[2] is the
        //    sampler state the shader reads, on both compilers.
        // The record's sampler slot (0 here, 2 in mgfxc's) is read by MonoGame only to apply a baked
        // sampler_state (EffectPass.SetShaderSamplers: 'if (sampler.state != null)'), which Slang
        // source cannot declare, so it changes nothing at runtime. 'register(t2) : register(s2)'
        // is the spelling that binds both halves to slot 2 (next test).
        MgfxBlobReader effect = SlangEffect("Sampler2D A : register(s2);\n" + CombinedAPixelShader, PlatformTarget.DirectX);

        Named(effect).Select(s => (s.Texture, s.TextureSlot)).ShouldBe([("A", (byte)0)]);
        DxbcBindings(effect).ShouldBe(("A@t0", "@s2"));
    }

    [Fact]
    public void TextureAndSamplerRegisterOnACombinedSampler_OnDirectX11_BindBothHalvesToSlot2()
    {
        // Issue #355, the spelling for a game that binds GraphicsDevice.Textures[2]: both halves
        // carry the author's register, the record names texture slot 2, and the bytecode reads t2/s2.
        MgfxBlobReader effect = SlangEffect(
            "Sampler2D A : register(t2) : register(s2);\n" + CombinedAPixelShader, PlatformTarget.DirectX);

        Named(effect).ShouldBe([("A", (byte)2, (byte)2)]);
        DxbcBindings(effect).ShouldBe(("A@t2", "@s2"));
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public void SamplerRegisterOnACombinedSampler_OnTheOtherDirectXAndVulkanTargets_MatchesTheHandWrittenPair(PlatformTarget target)
    {
        // Issue #355 on the remaining record-keyed targets: the combined sampler gets the table the
        // hand-written split pair 'Texture2D A; SamplerState S : register(s2);' gets through the
        // .fx route (texture slot 0), never a slot the author did not write.
        var slang = SlangSamplers("Sampler2D A : register(s2);\n" + CombinedAPixelShader, target);
        var fx = FxSamplers(FxHeader + """
            Texture2D A;
            SamplerState S : register(s2);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return A.Sample(S, uv);
            }
            technique T { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """, target);

        Slots(slang).ShouldBe(Slots(fx));
        slang.ShouldHaveSingleItem().TextureSlot.ShouldBe((byte)0);
    }

    // Named, with the .fx route's synthesized texture name for a bare legacy sampler ('A_SDTexture')
    // taken back to the sampler's own name, as mgfxc spells the parameter.
    private static (string Texture, byte TextureSlot, byte SamplerSlot)[] FxNamed(MgfxBlobReader effect) =>
        Named(effect)
            .Select(s => (s.Texture.EndsWith("_SDTexture", StringComparison.Ordinal) ? s.Texture[..^"_SDTexture".Length] : s.Texture, s.TextureSlot, s.SamplerSlot))
            .OrderBy(s => s.Item1, StringComparer.Ordinal)
            .ToArray();

    // Each sampler record keyed by the name of the texture parameter it binds.
    private static (string Texture, byte TextureSlot, byte SamplerSlot)[] Named(MgfxBlobReader effect) =>
        effect.Samplers
            .Select(s => (effect.Parameters[s.Parameter].Name, s.TextureSlot, s.SamplerSlot))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
}
