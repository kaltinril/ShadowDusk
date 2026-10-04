#nullable enable
using System.Text.RegularExpressions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Where LEGACY samplers (<c>sampler2D A;</c>, <c>sampler A : register(sN);</c>, a
/// <c>sampler_state</c> block) land beside MODERN <c>Texture2D</c> + <c>SamplerState</c> pairs,
/// against real <c>mgfxc</c> 3.8.4.1 goldens (<c>tests/fixtures/golden/legacy-sampler-units/</c>,
/// sources beside them; measured 2026-10-03).
///
/// <para><b>OpenGL</b>: fxc allocates every modern pair's unit before any legacy sampler's, so a
/// legacy sampler declared FIRST still lands after the pair (<c>S+T</c> on <c>ps_s0</c>, <c>A</c>
/// on <c>ps_s1</c>), and SpriteBatch's unit 0 reaches the modern texture. ShadowDusk used to
/// allocate in plain declaration order, which put the legacy sampler on unit 0 under the sprite.
/// An explicit register on a legacy sampler is still taken first.</para>
///
/// <para><b>DirectX 11</b>: a legacy <c>sampler A : register(sN)</c> binds its sampler at
/// <c>sN</c> and <c>mgfxc</c>'s record carries <c>sN</c>; ShadowDusk dropped the register (s0).
/// More generally the record of a legacy sampler's texture carries that sampler's own binding.
/// The TEXTURE slots still differ from <c>mgfxc</c> where fxc puts legacy textures before
/// modern ones (and legacy textures in first-use order): those shapes are compared on the
/// legacy SAMPLER slots only and listed in <see cref="DirectX11TextureOrderDivergence"/>; see
/// <c>project_facts.md</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacySamplerUnitTests
{
    private static readonly string[] Shapes =
    {
        "LegacyBeforeModern", "ModernBeforeLegacy", "LegacyBeforeModernSampledSecond", "LegacyBeforeModernSamplerFirst",
        "SamplerLegacyTexture", "TextureLegacySampler", "LegacyRegister0BeforeModern", "LegacyRegister1BeforeModern",
        "LegacyBeforeModernSamplerRegister0", "LegacyBeforeModernTextureRegister1", "LegacyRegister2BeforeModern",
        "ModernBeforeLegacyRegister0", "TwoLegacyTwoModern", "TwoModernTwoLegacy", "LegacyModernInterleaved",
        "ModernLegacyInterleaved", "TwoLegacyTwoTexturesOneSampler", "TwoTexturesTwoLegacyOneSampler",
        "SecondLegacyRegister1TwoModern", "LegacyStateTextureBeforeModern", "ModernBeforeLegacyStateTexture",
        "LegacyTextureModernLegacySampler", "ModernTextureLegacyModernSampler", "TwoLegacySampledInReverse",
        "GlMixedLegacyAfterModernRender", "GlMixedTwoOfEachRender", "DxLegacyRegisterRender",
    };

    /// <summary>
    /// DirectX 11 shapes whose TEXTURE slots still differ from mgfxc (fxc puts legacy textures
    /// first, in first-use order). Measured, recorded, not fixed here.
    /// </summary>
    private static readonly HashSet<string> DirectX11TextureOrderDivergence = new(StringComparer.Ordinal)
    {
        "ModernBeforeLegacy", "TextureLegacySampler", "ModernBeforeLegacyRegister0", "TwoModernTwoLegacy",
        "LegacyModernInterleaved", "ModernLegacyInterleaved", "TwoTexturesTwoLegacyOneSampler",
        "ModernBeforeLegacyStateTexture", "ModernTextureLegacyModernSampler", "TwoLegacySampledInReverse",
        "GlMixedTwoOfEachRender",
    };

    public static TheoryData<string> AllShapes()
    {
        var data = new TheoryData<string>();
        foreach (string s in Shapes)
            data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_TextureUnits_MatchMgfxc(string shape)
    {
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.OpenGL);
        MgfxBlobReader golden = Golden(shape, "OpenGL");

        Units(subject).ShouldBe(Units(golden), customMessage: "every texture must sit on mgfxc's OpenGL unit");
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    [Trait("Platform", "DirectX")]
    public async Task DirectX11_LegacySamplerSlots_MatchMgfxc(string shape)
    {
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.DirectX);
        MgfxBlobReader golden = Golden(shape, "DirectX_11");
        HashSet<string> modern = ModernTextures(shape);

        LegacySamplerSlots(subject, modern).ShouldBe(LegacySamplerSlots(golden, modern),
            customMessage: "each legacy sampler's record must carry mgfxc's DirectX 11 sampler slot");

        if (!DirectX11TextureOrderDivergence.Contains(shape))
        {
            Records(subject).ShouldBe(Records(golden),
                customMessage: "the whole DirectX 11 sampler table (texture slot, sampler slot, parameter) must be mgfxc's");
        }
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    [Trait("Platform", "DirectX")]
    public async Task DirectX11_TextureOrderDivergence_IsStillExactlyTheListedShapes(string shape)
    {
        // Keeps the divergence list honest: a shape leaves it the moment its table matches.
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.DirectX);
        MgfxBlobReader golden = Golden(shape, "DirectX_11");
        bool matches = Records(subject).SequenceEqual(Records(golden));
        matches.ShouldBe(!DirectX11TextureOrderDivergence.Contains(shape),
            customMessage: $"{shape}: update DirectX11TextureOrderDivergence (ours {string.Join(" ", Records(subject))}, mgfxc {string.Join(" ", Records(golden))})");
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>`texture@unit`, the texture named as ShadowDusk names it (mgfxc's `S+T` reduced to `T`).</summary>
    private static IReadOnlyList<string> Units(MgfxBlobReader r) =>
        r.Samplers.Select(s => $"{Plain(r.Parameters[s.Parameter].Name)}@{s.TextureSlot}")
                  .Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> Records(MgfxBlobReader r) =>
        r.Samplers.Select(s => $"{Plain(r.Parameters[s.Parameter].Name)}@t{s.TextureSlot}s{s.SamplerSlot}")
                  .Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> LegacySamplerSlots(MgfxBlobReader r, HashSet<string> modern) =>
        r.Samplers.Select(s => (Name: Plain(r.Parameters[s.Parameter].Name), s.SamplerSlot))
                  .Where(x => !modern.Contains(x.Name))
                  .Select(x => $"{x.Name}@s{x.SamplerSlot}")
                  .Order(StringComparer.Ordinal).ToList();

    private static string Plain(string name)
    {
        int plus = name.IndexOf('+');
        return plus >= 0 ? name[(plus + 1)..] : name;
    }

    private static HashSet<string> ModernTextures(string shape) =>
        Regex.Matches(File.ReadAllText(SourcePath(shape)), @"\bTexture2D\s+(\w+)")
             .Select(m => m.Groups[1].Value)
             .ToHashSet(StringComparer.Ordinal);

    private static string SourcePath(string shape) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "legacy-sampler-units", shape + ".fx");

    private static MgfxBlobReader Golden(string shape, string profile)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "legacy-sampler-units",
                                   $"{shape}.{profile}.mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden missing: {path}");
        return MgfxBlobReader.Parse(File.ReadAllBytes(path));
    }

    private static async Task<MgfxBlobReader> CompileAsync(string shape, PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(
            await File.ReadAllTextAsync(SourcePath(shape), cts.Token),
            new CompilerOptions { Target = target, SourceFileName = shape + ".fx" },
            cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        return MgfxBlobReader.Parse(result.Value.Data);
    }
}
