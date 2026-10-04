#nullable enable
using System.Text;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// The effect-parameter NAMES a legacy sampler gets, against real <c>mgfxc</c> 3.8.4.1 goldens
/// (<c>tests/fixtures/golden/legacy-sampler-names/</c>, sources beside them). A texture-less
/// legacy sampler (<c>sampler2D A;</c>) is one object to <c>mgfxc</c>, and its parameter is named
/// <c>A</c> on every profile; the SM4 rewrite splits it into <c>Texture2D A_SDTexture;
/// SamplerState A;</c>, and that synthesized name used to reach the table, so game code's
/// <c>effect.Parameters["A"]</c> got null (DirectX) or a standalone sampler parameter no
/// sampler record points at (OpenGL, Vulkan), and setting a texture through it drew nothing.
///
/// <para>What is compared, per target:</para>
/// <list type="bullet">
///   <item><b>DirectX 11</b>: the whole table against the golden, in order (name, class, type),
///   and the parameter each sampler record binds.</item>
///   <item><b>OpenGL</b>: the same, except that ShadowDusk's additive standalone SAMPLER
///   parameters (Phase 5 §7.4.3, the one remaining pinned divergence) are set aside, and the
///   order is compared as a set: mgfxc orders a GL table by texture unit, ShadowDusk by
///   reflection order, which differs only when a register reorders the units
///   (<c>RegisterReordered</c>: mgfxc <c>[B, A]</c>, ShadowDusk <c>[A, B]</c>; MonoGame looks
///   parameters up by name).</item>
///   <item><b>DirectX 12 and Vulkan</b>: no reference exists. <c>mgfxc</c> 3.8.5 rejects the legacy
///   sampler types for Vulkan (<c>unknown type name 'sampler2D'</c>) and, for DirectX 12, emits an
///   effect with NO parameters and NO sampler records for every one of these shapes (measured
///   2026-10-03), so nothing could be bound at all. These targets are held to the DirectX 11
///   golden's names, the one name <c>mgfxc</c> gives this object everywhere it compiles it
///   (Vulkan keeps the additive sampler parameters like OpenGL).</item>
///   <item><b>KNI</b> (the KNIFX container) and <b>FNA</b> (<c>fx_2_0</c>, which keeps the legacy
///   declaration verbatim and never synthesized a texture): the synthesized name never
///   appears.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacySamplerParameterNameTests
{
    private const byte ClassObject = 3;  // EffectParameterClass.Object
    private const byte TypeSampler = 5;  // EffectParameterType.Texture: a standalone sampler parameter

    public static TheoryData<string> Shapes() => new()
    {
        "BareSampler2D", "BareSampler2DRegister0", "BareSampler2DRegister1", "BareSampler",
        "SpriteBatchS0", "TwoBareSamplers", "RegisterReordered", "StateBlockNoTexture",
        "StateBlockTexture", "StateBlockUndeclaredTexture", "StateBlockUndeclaredTextureShared",
        "SpriteTextureSampler", "ParamNamesRender",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "DirectX")]
    public async Task DirectX11_ParameterTable_MatchesMgfxc(string shape)
    {
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.DirectX);
        MgfxBlobReader golden = Golden(shape, "DirectX_11");

        Table(subject).ShouldBe(Table(golden), customMessage: "the DirectX 11 table must be mgfxc's, in order");
        RecordTargets(subject).ShouldBe(RecordTargets(golden), customMessage: "every sampler record must bind the parameter mgfxc's binds");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_ParameterTable_MatchesMgfxc(string shape)
    {
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.OpenGL);
        MgfxBlobReader golden = Golden(shape, "OpenGL");

        WithoutAdditiveSamplers(subject, golden).Order(StringComparer.Ordinal)
            .ShouldBe(Table(golden).Order(StringComparer.Ordinal), customMessage: "the OpenGL table must be mgfxc's");
        RecordTargets(subject).ShouldBe(RecordTargets(golden), customMessage: "every sampler record must bind the parameter mgfxc's binds");
        TextureUnits(subject).ShouldBe(TextureUnits(golden), customMessage: "each parameter must sit on mgfxc's texture unit");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "DirectX12")]
    public async Task DirectX12_ParameterTable_CarriesMgfxcsNames(string shape)
    {
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.DirectX12);
        MgfxBlobReader reference = Golden(shape, "DirectX_11");

        Table(subject).ShouldBe(Table(reference), customMessage: "DirectX 12 carries the name mgfxc gives this object on every profile it compiles");
        RecordTargets(subject).ShouldBe(RecordTargets(reference));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "Vulkan")]
    public async Task Vulkan_ParameterTable_CarriesMgfxcsNames(string shape)
    {
        MgfxBlobReader subject = await CompileAsync(shape, PlatformTarget.Vulkan);
        MgfxBlobReader reference = Golden(shape, "DirectX_11");

        WithoutAdditiveSamplers(subject, reference).ShouldBe(Table(reference),
            customMessage: "Vulkan carries the name mgfxc gives this object on every profile it compiles");
        RecordTargets(subject).ShouldBe(RecordTargets(reference));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    [Trait("Platform", "OpenGL")]
    public async Task Kni_And_Fna_NeverCarryTheSynthesizedName(string shape)
    {
        byte[] knifx = await CompileBytesAsync(shape, PlatformTarget.OpenGL, CapabilityProfile.KniGL_4_02);
        Encoding.Latin1.GetString(knifx).ShouldNotContain("_SDTexture", Case.Sensitive);

        byte[] fxb = await CompileBytesAsync(shape, PlatformTarget.Fna);
        Encoding.Latin1.GetString(fxb).ShouldNotContain("_SDTexture", Case.Sensitive);
    }

    // ------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> Table(MgfxBlobReader r) => Table(r.Parameters);

    private static IReadOnlyList<string> Table(IEnumerable<MgfxParameterRecord> parameters) =>
        parameters.Select(p => $"{p.Name}:{p.Class}/{p.Type}").ToList();

    /// <summary>
    /// The subject's table minus the standalone sampler parameters ShadowDusk adds on OpenGL and
    /// Vulkan (an Object parameter of type <c>Texture</c> whose name the reference does not have).
    /// </summary>
    private static IReadOnlyList<string> WithoutAdditiveSamplers(MgfxBlobReader subject, MgfxBlobReader reference)
    {
        var referenceNames = reference.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var extras = subject.Parameters.Where(p => !referenceNames.Contains(p.Name)).ToList();
        foreach (MgfxParameterRecord extra in extras)
        {
            extra.Class.ShouldBe(ClassObject, customMessage: $"extra parameter '{extra.Name}' must be an object");
            extra.Type.ShouldBe(TypeSampler, customMessage: $"extra parameter '{extra.Name}' may only be a standalone sampler parameter, never another texture name");
        }
        return Table(subject.Parameters.Where(p => referenceNames.Contains(p.Name)));
    }

    private static IReadOnlyList<string> RecordTargets(MgfxBlobReader r) =>
        r.Samplers.Select(s => r.Parameters[s.Parameter].Name)
                  .Distinct(StringComparer.Ordinal)
                  .Order(StringComparer.Ordinal)
                  .ToList();

    private static IReadOnlyList<string> TextureUnits(MgfxBlobReader r) =>
        r.Samplers.Select(s => $"{r.Parameters[s.Parameter].Name}@{s.TextureSlot}")
                  .Order(StringComparer.Ordinal)
                  .ToList();

    private static MgfxBlobReader Golden(string shape, string profile)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "legacy-sampler-names",
                                   $"{shape}.{profile}.mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden missing: {path}");
        return MgfxBlobReader.Parse(File.ReadAllBytes(path));
    }

    private static async Task<MgfxBlobReader> CompileAsync(string shape, PlatformTarget target) =>
        MgfxBlobReader.Parse(await CompileBytesAsync(shape, target));

    private static async Task<byte[]> CompileBytesAsync(string shape, PlatformTarget target, CapabilityProfile? profile = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "legacy-sampler-names", shape + ".fx");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(
            await File.ReadAllTextAsync(path, cts.Token),
            new CompilerOptions { Target = target, Profile = profile, SourceFileName = shape + ".fx" },
            cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        return result.Value.Data;
    }
}
