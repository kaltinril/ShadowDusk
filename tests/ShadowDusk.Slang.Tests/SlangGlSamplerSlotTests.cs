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

    // Each sampler record keyed by the name of the texture parameter it binds.
    private static (string Texture, byte TextureSlot, byte SamplerSlot)[] Named(MgfxBlobReader effect) =>
        effect.Samplers
            .Select(s => (effect.Parameters[s.Parameter].Name, s.TextureSlot, s.SamplerSlot))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
}
