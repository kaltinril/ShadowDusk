#nullable enable
using System.Text.RegularExpressions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// DirectX 11 TEXTURE slots of legacy samplers against real <c>mgfxc</c> 3.8.4.1
/// <c>/Profile:DirectX_11</c> goldens (<c>tests/fixtures/golden/dx11-texture-slots/</c>, sources
/// beside them; measured 2026-10-28).
///
/// <para>fxc gives a legacy sampler's texture the slot of its FIRST READ (evaluation order: an
/// argument before the call taking it, an inner read before the dependent read around it, a read
/// the compiler drops takes no slot), ahead of every unregistered modern texture, around every
/// explicit texture register (read or not). ShadowDusk numbered the SM4 rewrite in declaration
/// order, so in the XNA/MonoGame <c>BloomCombine.fx</c> shape (<c>sampler BloomSampler :
/// register(s0); sampler BaseSampler : register(s1) = sampler_state { Texture = (BaseTexture);
/// };</c>, the texture never declared) the synthesized <c>BaseTexture</c> took <c>t0</c>, where
/// SpriteBatch binds the sprite: the bloom and the base swapped (maxd 188 in real KNI
/// WinForms.DX11).</para>
///
/// <para>One deliberate difference from <c>mgfxc</c>: a <c>texture2D T : register(tN)</c> that a
/// legacy sampler's <c>sampler_state</c> names. fxc reserves <c>tN</c> for the (unread) texture
/// object and puts the sampler's texture on the next free slot (<c>DeferredBasicEffect</c>'s
/// <c>register(t0..t2)</c> land on t3/t4); XNA and KNI's own effect tests bind it at <c>tN</c>,
/// and so does ShadowDusk. Every other texture is on <c>mgfxc</c>'s slot, and every sampler slot
/// is <c>mgfxc</c>'s.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class DirectX11TextureSlotOrderTests
{
    private const string Folder = "dx11-texture-slots";

    /// <summary>
    /// Two legacy samplers sharing one texture: fxc gives each sampler its own texture slot (two
    /// <c>Tex</c> records), the SM4 rewrite has one <c>Texture2D Tex</c> and so one record. Open,
    /// recorded in <c>project_facts.md</c>; this list keeps it honest.
    /// </summary>
    private static readonly HashSet<string> KnownDivergence = new(StringComparer.Ordinal)
    {
        "TwoSamplersShareOneTexture",
    };

    public static TheoryData<string> AllShapes()
    {
        var data = new TheoryData<string>();
        foreach (string fx in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", Folder), "*.fx")
                                       .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(fx));
        }
        return data;
    }

    [Fact]
    public void TheGoldenFolder_HoldsEveryShape()
    {
        // MemberData over a folder would silently pass with zero rows if the fixtures were not copied.
        AllShapes().Count.ShouldBeGreaterThanOrEqualTo(39);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public async Task TextureAndSamplerSlots_AreMgfxcs_ExceptAnExplicitTextureRegister(string shape)
    {
        (MgfxBlobReader subject, IReadOnlyList<ShaderError> warnings) = await CompileAsync(shape);
        MgfxBlobReader golden = Golden(shape);
        IReadOnlyDictionary<string, int> explicitRegisters = ExplicitTextureRegisters(shape);

        warnings.ShouldNotContain(w => w.Code == "SD0228", "every shape here can be pinned");

        IReadOnlyList<string> expected = Tables(golden, explicitRegisters);
        IReadOnlyList<string> actual = Tables(subject, new Dictionary<string, int>(StringComparer.Ordinal));
        if (KnownDivergence.Contains(shape))
        {
            actual.ShouldNotBe(expected, $"{shape} now matches mgfxc: take it off KnownDivergence");
            return;
        }
        actual.ShouldBe(expected,
            customMessage: "each shader's sampler table must be mgfxc's, with an explicitly registered texture on its register");
    }

    [Fact]
    public async Task BloomCombine_PutsTheSpriteSamplerOnT0()
    {
        // The reported shape, spelled out: SpriteBatch binds the scene to t0, which must be
        // BloomSampler's texture, with BaseSampler's (the undeclared Tex2) on t1.
        (MgfxBlobReader subject, _) = await CompileAsync("BloomCombineUndeclaredTexture");
        Tables(subject, new Dictionary<string, int>(StringComparer.Ordinal))
            .ShouldHaveSingleItem().ShouldBe("ps: BloomSampler@t0s0 Tex2@t1s1");
    }

    [Fact]
    public async Task ExplicitStateTextureRegisters_AreHonoured_WhereMgfxcMovesThem()
    {
        // The recorded mgfxc deviation from XNA: mgfxc's golden has Diffuse t2 / Specular t3 for
        // `texture2D Diffuse : register(t1)` / `texture2D Specular : register(t0)`.
        (MgfxBlobReader subject, _) = await CompileAsync("ExplicitTextureRegistersReversed");
        Tables(subject, new Dictionary<string, int>(StringComparer.Ordinal))
            .ShouldHaveSingleItem().ShouldBe("ps: Diffuse@t1s0 Spec@t0s1");
        Tables(Golden("ExplicitTextureRegistersReversed"), new Dictionary<string, int>(StringComparer.Ordinal))
            .ShouldHaveSingleItem().ShouldBe("ps: Diffuse@t2s0 Spec@t3s1");
    }

    [Fact]
    public async Task ATextureThatCannotBePinned_CompilesWithTheOldSlots_AndWarnsSD0228()
    {
        // `Texture2D T, U;` is a declarator list, which the re-pin does not rewrite; the legacy
        // sampler A should be on t0 ahead of both, so the plan cannot be applied. Fail loudly:
        // the effect compiles as before and says the slots are not mgfxc's.
        const string source = """
            Texture2D T, U;
            SamplerState S;
            sampler2D A;
            float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * T.Sample(S, uv) + U.Sample(S, uv); }
            technique Main { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
            """;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(source,
            new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "list.fx" }, cts.Token);

        result.IsSuccess.ShouldBeTrue();
        ShaderError warning = result.Value.Warnings.Where(w => w.Code == "SD0228").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("A_SDTexture t0", Case.Sensitive);
        warning.Message.ShouldContain("declarator list", Case.Sensitive);
        Tables(MgfxBlobReader.Parse(result.Value.Data), new Dictionary<string, int>(StringComparer.Ordinal))
            .ShouldHaveSingleItem().ShouldBe("ps: A@t2s1 T@t0s0 U@t1s1");
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// One line per shader, `vs:`/`ps:` + its records sorted, the lines sorted (mgfxc writes a
    /// pass's pixel shader first, ShadowDusk its vertex shader first). A texture named in
    /// <paramref name="explicitRegisters"/> is put on that register.
    /// </summary>
    private static IReadOnlyList<string> Tables(MgfxBlobReader r, IReadOnlyDictionary<string, int> explicitRegisters) =>
        r.Shaders.Select(s => (s.IsVertex ? "vs: " : "ps: ") + string.Join(" ",
                r.Samplers.Where(x => x.ShaderIndex == s.Index)
                          .Select(x =>
                          {
                              string name = r.Parameters[x.Parameter].Name;
                              int slot = explicitRegisters.TryGetValue(name, out int reg) ? reg : x.TextureSlot;
                              return $"{name}@t{slot}s{x.SamplerSlot}";
                          })
                          .Order(StringComparer.Ordinal)))
         .Order(StringComparer.Ordinal)
         .ToList();

    private static IReadOnlyDictionary<string, int> ExplicitTextureRegisters(string shape) =>
        Regex.Matches(File.ReadAllText(SourcePath(shape)), @"\b[Tt]exture\w*\s+(\w+)\s*:\s*register\s*\(\s*t(\d+)\s*\)")
             .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                           StringComparer.Ordinal);

    private static string SourcePath(string shape) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", Folder, shape + ".fx");

    private static MgfxBlobReader Golden(string shape)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", Folder, $"{shape}.DirectX_11.mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden missing: {path}");
        return MgfxBlobReader.Parse(File.ReadAllBytes(path));
    }

    private static async Task<(MgfxBlobReader Reader, IReadOnlyList<ShaderError> Warnings)> CompileAsync(string shape)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(
            await File.ReadAllTextAsync(SourcePath(shape), cts.Token),
            new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = shape + ".fx" },
            cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        return (MgfxBlobReader.Parse(result.Value.Data), result.Value.Warnings);
    }
}
