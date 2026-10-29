#nullable enable
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests.Dxc;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// DirectX 11 <c>t</c> slots of legacy samplers' textures against real <c>mgfxc</c> 3.8.4.1
/// <c>/Profile:DirectX_11</c> goldens (<c>tests/fixtures/golden/dx11-texture-slots/</c>, sources
/// beside them; measured 2026-10-28), on BOTH DXBC backends.
///
/// <para>fxc gives a legacy sampler's texture the slot of its FIRST READ in evaluation order (an
/// argument before the call taking it, an inner read before the dependent read around it, a read
/// fxc's optimiser drops takes no slot), ahead of every unregistered modern resource, around every
/// explicit <c>t</c> register (read or not, the register on a <c>texture2D</c> a
/// <c>sampler_state</c> names included: that one only reserves). ShadowDusk numbered the SM4
/// rewrite in declaration order, so in the XNA/MonoGame <c>BloomCombine.fx</c> shape the
/// undeclared state texture took <c>t0</c>, where SpriteBatch binds the sprite (maxd 188 in real
/// KNI WinForms.DX11).</para>
///
/// <para><b>The evaluation order is read from the source</b> (<c>HlslReadOrder</c>), on both
/// backends: no compiled instruction stream carries it. <c>d3dcompiler_47</c> schedules its reads
/// (<c>S10_ClipFirst</c>, <c>D14_PrecedenceMulLeft</c>), and vkd3d swaps the operands of
/// <c>&gt;</c> and <c>&lt;=</c> (<c>G01_Gt</c>, <c>G04_Le</c>, <c>ZZ_GreaterThanReview</c>); the
/// rows for those shapes turn red if the order is ever taken from a compiled shader again.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class DirectX11TextureSlotOrderTests
{
    private const string Folder = "dx11-texture-slots";

    /// <summary>
    /// Open divergences, recorded in <c>docs/validation-matrix.md</c> §7, kept honest here (a shape
    /// that starts matching must leave the list):
    /// <list type="bullet">
    /// <item><c>TwoSamplersShareOneTexture</c>, <c>S13_ModernSampledAndStateNamed</c>: fxc gives every
    /// legacy sampler its own texture object, so one texture behind two samplers (or behind a
    /// sampler and a direct read) is two <c>mgfxc</c> records; the SM4 rewrite has one
    /// <c>Texture2D</c>, so one record.</item>
    /// <item><c>A02_DeclListLegacyReverse</c>: <c>sampler2D A, B;</c> gets its SAMPLER slots in
    /// another order (texture slots match; same on <c>main</c> before this change).</item>
    /// <item><c>A01_TexArrayPlusLegacy</c>: a texture array beside a legacy sampler is not modelled;
    /// the compile warns <c>SD0230</c>.</item>
    /// <item><c>D12_UnrollLoopIndex</c>: reads under a branch on an <c>[unroll]</c> loop's counter
    /// come in iteration order in fxc; the source walk does not unroll, so it refuses with
    /// <c>SD0230</c> instead of guessing.</item>
    /// <item><c>B02_MacroTexture</c>: legacy samplers a macro declares are compiled natively by the
    /// backend (the raw pre-parse cannot see them), which names the parameter after the sampler
    /// and ignores the sampler order; the TEXTURE slots match (pre-existing, <c>project_facts.md</c>).</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> KnownDivergence = new(StringComparer.Ordinal)
    {
        "TwoSamplersShareOneTexture", "S13_ModernSampledAndStateNamed", "A02_DeclListLegacyReverse", "A01_TexArrayPlusLegacy",
        "D12_UnrollLoopIndex", "B02_MacroTexture",
    };

    /// <summary>
    /// vkd3d only: its optimiser keeps reads fxc folds away (<c>x * 0</c>, <c>lerp(a, b, 1)</c>,
    /// <c>a - a</c>, <c>dot(x, 0)</c>, <c>saturate(-1 - abs(x))</c>), so the dead texture still has
    /// a record and takes a slot. d3dcompiler_47 folds them like fxc.
    /// </summary>
    private static readonly HashSet<string> Vkd3dDeadCodeDivergence = new(StringComparer.Ordinal)
    {
        "D01_MulZero", "D02_LerpOne", "D04_MulZeroOrder", "D06_SaturateNeg", "D07_MinusSelf", "D10_DotZero",
    };

    /// <summary>Shapes whose compile is expected to warn SD0230 (the slots could not be checked).</summary>
    private static readonly HashSet<string> ExpectedSD0230 = new(StringComparer.Ordinal) { "A01_TexArrayPlusLegacy", "D12_UnrollLoopIndex" };

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
        AllShapes().Count.ShouldBeGreaterThanOrEqualTo(144);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public Task Vkd3d_TextureAndSamplerSlots_AreMgfxcs(string shape) =>
        AssertMatchesGolden(shape, DxbcBackend.Vkd3d);

    [WindowsTheory]
    [MemberData(nameof(AllShapes))]
    public Task D3DCompiler_TextureAndSamplerSlots_AreMgfxcs(string shape) =>
        AssertMatchesGolden(shape, DxbcBackend.D3DCompiler);

    private static async Task AssertMatchesGolden(string shape, DxbcBackend backend)
    {
        (MgfxBlobReader subject, IReadOnlyList<ShaderError> warnings) = await CompileAsync(shape, backend);
        MgfxBlobReader golden = Golden(shape);

        bool sd0230 = warnings.Any(w => w.Code == "SD0230");
        sd0230.ShouldBe(ExpectedSD0230.Contains(shape), $"{shape} [{backend}]: SD0230 expected only for the listed shapes");

        bool divergent = KnownDivergence.Contains(shape)
                         || (backend == DxbcBackend.Vkd3d && Vkd3dDeadCodeDivergence.Contains(shape));
        IReadOnlyList<string> expected = Tables(golden);
        IReadOnlyList<string> actual = Tables(subject);
        if (divergent)
        {
            actual.ShouldNotBe(expected, $"{shape} [{backend}] now matches mgfxc: take it off its divergence list");
            return;
        }
        actual.ShouldBe(expected, customMessage: $"{shape} [{backend}]: each shader's sampler table must be mgfxc's");
    }

    [Fact]
    public async Task BloomCombine_PutsTheSpriteSamplerOnT0()
    {
        // The reported shape, spelled out: SpriteBatch binds the scene to t0, which must be
        // BloomSampler's texture, with BaseSampler's (the undeclared Tex2) on t1.
        (MgfxBlobReader subject, _) = await CompileAsync("BloomCombineUndeclaredTexture", DxbcBackend.Vkd3d);
        Tables(subject).ShouldHaveSingleItem().ShouldBe("ps: BloomSampler@t0s0 Tex2@t1s1");
    }

    [Fact]
    public async Task ARegisterOnAStateTexture_OnlyReserves_LikeMgfxc()
    {
        // `texture2D Diffuse : register(t1)` / `texture2D Spec : register(t0)`, both named only by
        // sampler_state blocks: fxc keeps t0/t1 for the unread texture objects and gives the
        // samplers' textures the next free slots, in first-read order.
        (MgfxBlobReader subject, _) = await CompileAsync("ExplicitTextureRegistersReversed", DxbcBackend.Vkd3d);
        Tables(subject).ShouldHaveSingleItem().ShouldBe("ps: Diffuse@t2s0 Spec@t3s1");
        Tables(Golden("ExplicitTextureRegistersReversed")).ShouldHaveSingleItem().ShouldBe("ps: Diffuse@t2s0 Spec@t3s1");
    }

    [Theory]
    [InlineData("G01_Gt", "SC@t0")]
    [InlineData("G04_Le", "SC@t0")]
    [InlineData("ZZ_GreaterThanReview", "A@t0")]
    public async Task EvaluationOrder_IsTheSources_NotVkd3dsSwappedComparisonOperands(string shape, string onT0)
    {
        // vkd3d 2.1 lowers `a > b` to LESS(b, a) and `a <= b` to GEQUAL(b, a) and emits the right
        // operand's read first; fxc numbers the left one first.
        (MgfxBlobReader subject, _) = await CompileAsync(shape, DxbcBackend.Vkd3d);
        Tables(subject).ShouldHaveSingleItem().ShouldBe(Tables(Golden(shape)).Single());
        Tables(subject).Single().ShouldContain(onT0, Case.Sensitive);
    }

    [WindowsFact]
    public async Task EvaluationOrder_IsTheSources_UnderTheD3DCompilerBackend()
    {
        // d3dcompiler_47 emits C first for `tex2D(A) * tex2D(B) + tex2D(C) * 2` and B first when B
        // feeds a clip; fxc's slots follow evaluation order. Read off d3dcompiler's own stream the
        // tables below would be C/A/B and B/A.
        // (A dead store, E05_DeadFirstOverwritten, is the other direction: fxc numbers the read it
        // later drops; its row is in the golden theory.)
        (MgfxBlobReader mul, _) = await CompileAsync("D14_PrecedenceMulLeft", DxbcBackend.D3DCompiler);
        Tables(mul).ShouldHaveSingleItem().ShouldBe(Tables(Golden("D14_PrecedenceMulLeft")).Single());
        Tables(mul).Single().ShouldContain("A@t0", Case.Sensitive);

        (MgfxBlobReader clip, _) = await CompileAsync("S10_ClipFirst", DxbcBackend.D3DCompiler);
        Tables(clip).ShouldHaveSingleItem().ShouldBe(Tables(Golden("S10_ClipFirst")).Single());
        Tables(clip).Single().ShouldContain("A@t0", Case.Sensitive);
    }

    [Theory]
    [InlineData(DxbcBackend.Vkd3d)]
    [InlineData(DxbcBackend.D3DCompiler)]
    public async Task ALegacySamplerInAnInclude_JoinsUnderTheSamplersName_WithoutAFalseSD0230(DxbcBackend backend)
    {
        if (backend == DxbcBackend.D3DCompiler && !OperatingSystem.IsWindows())
            return; // the d3dcompiler_47 oracle is Windows-only
        // `sampler SB = sampler_state { Texture = <TexB>; }` lives in a header the raw pre-parse does
        // not rewrite; the backend compiles it natively and its RDEF texture is named `SB`, while the
        // preprocessed view knows the texture as `TexB`. mgfxc 3.8.4.1: TexB@t0s1 SA@t1s0 (the
        // sampler slot and the parameter name are a separate, pre-existing native-path gap).
        const string main = """
            sampler2D SA;
            #include "inc_hdr.fxh"
            float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SB, uv) * 0.5 + tex2D(SA, uv); }
            technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
            """;
        const string header = "texture2D TexB;\nsampler SB = sampler_state { Texture = <TexB>; };\n";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(main, new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            DxbcBackend = backend,
            SourceFileName = "B01.fx",
            IncludeResolver = new ShadowDusk.Core.Preprocessor.InMemoryIncludeResolver(
                new Dictionary<string, string> { ["inc_hdr.fxh"] = header }),
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0230");
        MgfxBlobReader r = MgfxBlobReader.Parse(result.Value.Data);
        var slots = r.Samplers.ToDictionary(x => r.Parameters[x.Parameter].Name, x => (int)x.TextureSlot);
        slots.Values.Order().ShouldBe([0, 1]);
        slots["SA"].ShouldBe(1, "the header's SB is read first, so it takes t0 as in mgfxc");
    }

    [Fact]
    public async Task ATextureThatCannotBePinned_CompilesWithTheOldSlots_AndWarnsSD0230()
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
        ShaderError warning = result.Value.Warnings.Where(w => w.Code == "SD0230").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("A_SDTexture t0", Case.Sensitive);
        warning.Message.ShouldContain("declarator list", Case.Sensitive);
        Tables(MgfxBlobReader.Parse(result.Value.Data))
            .ShouldHaveSingleItem().ShouldBe("ps: A@t2s1 T@t0s0 U@t1s1");
    }

    [Fact]
    public async Task ATextureArrayBesideALegacySampler_WarnsSD0230_InsteadOfSkippingSilently()
    {
        (_, IReadOnlyList<ShaderError> warnings) = await CompileAsync("A01_TexArrayPlusLegacy", DxbcBackend.Vkd3d);
        warnings.Where(w => w.Code == "SD0230").ShouldHaveSingleItem()
                .Message.ShouldContain("array of resources", Case.Sensitive);
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// One line per shader, `vs:`/`ps:` + its records sorted, the lines sorted (mgfxc writes a
    /// pass's pixel shader first, ShadowDusk its vertex shader first).
    /// </summary>
    private static IReadOnlyList<string> Tables(MgfxBlobReader r) =>
        r.Shaders.Select(s => (s.IsVertex ? "vs: " : "ps: ") + string.Join(" ",
                r.Samplers.Where(x => x.ShaderIndex == s.Index)
                          .Select(x => $"{r.Parameters[x.Parameter].Name}@t{x.TextureSlot}s{x.SamplerSlot}")
                          .Order(StringComparer.Ordinal)))
         .Order(StringComparer.Ordinal)
         .ToList();

    private static string SourcePath(string shape) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", Folder, shape + ".fx");

    private static MgfxBlobReader Golden(string shape)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", Folder, $"{shape}.DirectX_11.mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden missing: {path}");
        return MgfxBlobReader.Parse(File.ReadAllBytes(path));
    }

    private static async Task<(MgfxBlobReader Reader, IReadOnlyList<ShaderError> Warnings)> CompileAsync(string shape, DxbcBackend backend)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await new EffectCompiler().CompileAsync(
            await File.ReadAllTextAsync(SourcePath(shape), cts.Token),
            new CompilerOptions { Target = PlatformTarget.DirectX, DxbcBackend = backend, SourceFileName = shape + ".fx" },
            cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        return (MgfxBlobReader.Parse(result.Value.Data), result.Value.Warnings);
    }
}
