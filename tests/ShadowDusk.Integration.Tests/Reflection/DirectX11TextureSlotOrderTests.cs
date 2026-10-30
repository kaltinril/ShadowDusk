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
    /// Shapes that warn <c>SD0230</c> and whose <c>mgfxc</c> order is NOT declaration order: the
    /// price of never guessing. The order hangs on whether fxc keeps a read under an <c>if</c>, a
    /// <c>switch</c> arm, a loop that may not run, a later loop iteration or after a <c>return</c>,
    /// and the walk does not evaluate conditions at all (every attempt to prove one run-time, a
    /// uniform, an input, was broken: <c>P05</c>, <c>P09</c>, <c>P13</c>, <c>Q13</c>, <c>R01</c>,
    /// <c>R02</c> are fxc folding a condition that looked run-time). Here fxc kept the read (a
    /// uniform, an input), or folded it in a way the token order does not show (<c>K15</c>,
    /// <c>K18</c>, <c>K20</c>, <c>D12</c>'s iteration order). The <c>U*</c> ones refuse shapes the
    /// walk does not model at all: a bare call statement fxc keeps (<c>max(tex2D(B, uv), 0);</c>; the
    /// walk refuses every bare call that reads, since fxc drops <c>tex2D(B, uv);</c> and
    /// <c>float4(tex2D(B, uv));</c>), a read in a global initializer (fxc numbers it before the entry
    /// point's), a struct method or namespace function (d3dcompiler only: vkd3d rejects the syntax).
    /// </summary>
    private static readonly HashSet<string> FalseSD0230 = new(StringComparer.Ordinal)
    {
        "D12_UnrollLoopIndex", "K15_StaticFromUniform", "K16_UniformCond", "K17_InputCond", "K18_UniformTimesZero",
        "K20_ReturnUnderCallZero", "K28_UniformBoolInitFalse", "K29_UniformInitZero", "K33_SwitchOnUniform",
        "K35_BreakInSwitchThenReads", "K38_UniformLoopBound", "P01_SelfSwizzleGt", "P02_ScalarVsSwizzle", "P03_InputSelfSwizzle",
        "P04_SelfSwizzleNe", "P10_IntEqHalf", "P11_IntGtMax", "P20_SwitchDefaultFirst", "P24_SwitchFallthrough",
        "P25_UnconditionalDiscardFirst", "P27_BoolUniformSelfNot", "P28_ConstUniformNotStatic", "P29_UniformKeyword", "P30_Extern",
        "Q02_SwitchDefaultMiddle", "Q03_SwitchThreeCases", "Q04_SwitchBranchAttr", "Q05_SwitchFlattenAttr", "Q06_SwitchFloatSel",
        "Q08_ElseIfChain", "Q09_ElseIfChain93", "Q14_UnsignedIntVecComp", "Q15_SwitchCaseBlockDefaultFirstTwoTex",
        "R05_UniformIncrement", "R06_ParamNoSemanticHelperUnused", "R07_ElseAfterBreakUncond", "T12_EarlyReturnReadLaterAgain",
        "U50_StmtMax", "U51_StmtSaturate", "U52_StmtDot", "U53_StmtLerp", "U54_StmtAbs", "U55_StmtLength",
        "U56_StmtModernSampleLegacyCoord",
        "U70_StaticGlobalUnused", "U71_StaticGlobalHelper", "U72_StaticArrayInit", "U73_StaticGlobalOrderTwo", "U74_StaticGlobalUsed",
        "U80_StructMethodFirst", "U81_StructMethodInExpr", "U83_NamespaceFunction",
    };

    /// <summary>
    /// Shapes vkd3d-shader rejects outright (a struct method, a namespace: <c>E5000</c>), loudly; the
    /// d3dcompiler_47 option compiles them and is held to the lists above.
    /// </summary>
    private static readonly HashSet<string> Vkd3dRejects = new(StringComparer.Ordinal)
    {
        "U80_StructMethodFirst", "U81_StructMethodInExpr", "U82_StructMethodLast", "U83_NamespaceFunction",
    };

    /// <summary>
    /// Open divergences, recorded in <c>docs/validation-matrix.md</c> §7, kept honest here (a shape
    /// that starts matching must leave the list):
    /// <list type="bullet">
    /// <item><c>TwoSamplersShareOneTexture</c>, <c>S13_ModernSampledAndStateNamed</c>: fxc gives every
    /// legacy sampler its own texture object, so one texture behind two samplers (or behind a
    /// sampler and a direct read) is two <c>mgfxc</c> records; the SM4 rewrite has one
    /// <c>Texture2D</c>, so one record. The same for a legacy sampler's texture the shader also
    /// calls a method on (<c>U60</c> <c>GetDimensions</c>, <c>U62</c> <c>Load</c>, <c>U63</c>
    /// <c>Sample</c>, <c>U64</c> <c>SampleLevel</c>, <c>U65</c>, <c>U67</c> the sprite template with
    /// <c>GetDimensions</c>): <c>mgfxc</c> has <c>A@t0 Tb@t1 Tb@t2</c>, ShadowDusk one <c>Tb</c>
    /// record, and these warn <c>SD0230</c> (pre-existing on <c>main</c>, which warned nothing).</item>
    /// <item><c>A02_DeclListLegacyReverse</c>: <c>sampler2D A, B;</c> gets its SAMPLER slots in
    /// another order (texture slots match; same on <c>main</c> before this change).</item>
    /// <item><c>A01_TexArrayPlusLegacy</c>: a texture array beside a legacy sampler is not modelled;
    /// the compile warns <c>SD0230</c>.</item>
    /// <item><c>B02_MacroTexture</c>: legacy samplers a macro declares are compiled natively by the
    /// backend (the raw pre-parse cannot see them), which names the parameter after the sampler
    /// and ignores the sampler order; the TEXTURE slots match (pre-existing, <c>project_facts.md</c>).</item>
    /// <item><c>K13_SamplerParam</c>: a legacy sampler passed to a function as an argument is
    /// compiled natively, and its SAMPLER slot is <c>s0</c> where <c>mgfxc</c> has <c>s1</c>
    /// (pre-existing native-path gap, like <c>B02_MacroTexture</c>); its texture slots warn
    /// <c>SD0230</c>, since the walk does not follow the parameter.</item>
    /// <item>The false-<c>SD0230</c> class (<see cref="FalseSD0230"/>): the walk cannot tell whether
    /// fxc keeps a read under a condition, so it warns and keeps declaration order, which is not
    /// <c>mgfxc</c>'s here. Loud, never silent.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> KnownDivergence = new HashSet<string>(StringComparer.Ordinal)
    {
        "TwoSamplersShareOneTexture", "S13_ModernSampledAndStateNamed", "A02_DeclListLegacyReverse", "A01_TexArrayPlusLegacy",
        "B02_MacroTexture", "K13_SamplerParam",
        "U60_StateTextureGetDimsUsed", "U62_StateTextureLoadFirst", "U63_StateTextureSampleKept", "U64_StateTextureSampleLevelKept",
        "U65_StateTextureGetDimsAfter", "U67_SpriteTemplateGetDims",
    }.Union(FalseSD0230).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// vkd3d only: its optimiser keeps reads fxc folds away (<c>x * 0</c>, <c>lerp(a, b, 1)</c>,
    /// <c>a - a</c>, <c>dot(x, 0)</c>, <c>saturate(-1 - abs(x))</c>, a <c>switch</c> on a
    /// <c>static const</c>), so the dead texture still has a record and takes a slot.
    /// d3dcompiler_47 folds them like fxc.
    /// </summary>
    private static readonly HashSet<string> Vkd3dDeadCodeDivergence = new(StringComparer.Ordinal)
    {
        "D01_MulZero", "D02_LerpOne", "D04_MulZeroOrder", "D06_SaturateNeg", "D07_MinusSelf", "D10_DotZero", "T07_SwitchStaticSel",
    };

    /// <summary>
    /// Shapes whose compile is expected to warn SD0230 (the slots could not be checked): every
    /// <see cref="FalseSD0230"/> shape, and the ones where the refusal keeps declaration order,
    /// which here IS <c>mgfxc</c>'s order (fxc folded the condition away: a call returning a
    /// constant, a local, a default or <c>out</c> argument, <c>&amp;&amp; false</c>, a vector
    /// component, a value range, a loop that runs zero times, a uniform the shader assigns, a
    /// <c>uint</c> compared with <c>0</c>, a loop bound that truncates). Each of those was guessed
    /// wrong, silently, by a walk that tried to prove a condition run-time. <c>A01</c> and
    /// <c>K13</c> refuse for their own reasons (a resource array, a sampler parameter). The
    /// <c>U0*</c>/<c>U1*</c> shapes are bare call statements fxc DROPS before it numbers
    /// (<c>tex2D(B, uv);</c>, in parentheses, a block, a loop, a void helper, a <c>for</c> init, a
    /// constructor, <c>tex2Dgrad</c>, <c>Tb.Sample(SB, uv);</c>); the <c>U6*</c> ones and <c>S13</c>
    /// use a legacy sampler's texture through a method of its own; <c>U82</c> is a struct method.
    /// </summary>
    private static readonly HashSet<string> ExpectedSD0230 = new HashSet<string>(StringComparer.Ordinal)
    {
        "A01_TexArrayPlusLegacy", "K13_SamplerParam",
        "U01_StmtBareRead", "U02_StmtParenRead", "U03_StmtCtor", "U04_StmtCtorTwoArgs", "U05_StmtGrad", "U06_StmtBlock",
        "U07_StmtInCertainLoop", "U08_StmtNestedRead", "U09_StmtAfterIf", "U10_VoidHelperBareRead", "U11_VoidHelperAssignThenBare",
        "U12_ForInitBareRead", "U13_StmtStateSampler", "U14_StmtCtorMulti", "U68_StmtStateTextureSample", "U69_StmtStateTextureSampleLevel",
        "S13_ModernSampledAndStateNamed", "U60_StateTextureGetDimsUsed", "U61_StateTextureGetDimsDead", "U62_StateTextureLoadFirst",
        "U63_StateTextureSampleKept", "U64_StateTextureSampleLevelKept", "U65_StateTextureGetDimsAfter",
        "U66_StateTextureSampleSwizzleStmt", "U67_SpriteTemplateGetDims",
        "U82_StructMethodLast",
        "K01_CallZeroCond", "K02_LocalFromCall", "K03_CallIgnoresRuntimeArg", "K04_DefaultArgBranch", "K05_OutParamZero",
        "K06_TaintedByStaticBranch", "K07_AndFalse", "K08_AndMacroZero", "K09_VectorComponent", "K10_SaturateRange",
        "K11_StructMemberShadowsStatic", "K21_LoopCallZero", "K26_IfFalseLiteral", "K31_ZeroTripLoop", "K36_InputAssigned",
        "K37_WhileCallZero", "P05_CommaLocalShadow", "P06_ForInitCommaShadow", "P09_UnsignedIntLt0", "P13_ParenAssignInput",
        "P17_IntCounterFloatInit", "P18_StaticConstIntFraction", "P19_UintCounterNeg", "Q01_SwitchCaseValuesReversed",
        "Q10_ParenWrappedAssign", "Q12_StaticConstIntNegFraction", "Q13_CommaListUninit", "R01_AssignUniform", "R02_UniformOutArg",
        "R04_HelperCommaShadow",
    }.Union(FalseSD0230).ToHashSet(StringComparer.Ordinal);

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
        AllShapes().Count.ShouldBeGreaterThanOrEqualTo(304);
    }

    [Fact]
    public Task AposShapes_KeepsMgfxcsTable_AndWarnsTheFalseSD0230_Vkd3d() => AposShapes(DxbcBackend.Vkd3d);

    [WindowsFact] // the d3dcompiler_47 oracle is Windows-only: skipped, not passed, elsewhere
    public Task AposShapes_KeepsMgfxcsTable_AndWarnsTheFalseSD0230_D3DCompiler() => AposShapes(DxbcBackend.D3DCompiler);

    private static async Task AposShapes(DxbcBackend backend)
    {
        // The real Apos.Shapes shader (issue #202). Its blur branch reads the blue-noise texture and
        // returns; the ramp reads come after it. Were that branch folded away the ramp would be read
        // first, and the walk does not evaluate conditions, so it cannot prove fxc keeps it: SD0230,
        // a false one. The declaration order it keeps IS mgfxc 3.8.4.1's table (measured
        // 2026-10-29, the same bytes as before the texture-slot change), so the slots are right.
        // If the walk ever proves the order, drop the warning assertion.
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "issues", "202", "apos-shapes.fx");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(path, cts.Token),
            new CompilerOptions { Target = PlatformTarget.DirectX, DxbcBackend = backend, SourceFileName = path }, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        Tables(MgfxBlobReader.Parse(result.Value.Data)).ShouldContain(
            "ps: ArcSampler@t3s3 BandSampler@t1s1 BlueNoiseSampler@t4s4 CurveSampler@t2s2 RampSampler@t5s5 TextureSampler@t0s0");
        result.Value.Warnings.Where(w => w.Code == "SD0230").ShouldHaveSingleItem()
              .Message.ShouldContain("'BlueNoiseSampler_SDTexture' before 'RampSampler_SDTexture'", Case.Sensitive);
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
        if (backend == DxbcBackend.Vkd3d && Vkd3dRejects.Contains(shape))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var rejected = await new EffectCompiler().CompileAsync(
                await File.ReadAllTextAsync(SourcePath(shape), cts.Token),
                new CompilerOptions { Target = PlatformTarget.DirectX, DxbcBackend = backend, SourceFileName = shape + ".fx" },
                cts.Token);
            rejected.IsFailure.ShouldBeTrue($"{shape} [{backend}] now compiles: take it off Vkd3dRejects and check its table");
            return;
        }

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

    [Fact]
    public Task ALegacySamplerInAnInclude_JoinsUnderTheSamplersName_WithoutAFalseSD0230_Vkd3d() =>
        ALegacySamplerInAnInclude_JoinsUnderTheSamplersName(DxbcBackend.Vkd3d);

    [WindowsFact] // the d3dcompiler_47 oracle is Windows-only: skipped, not passed, elsewhere
    public Task ALegacySamplerInAnInclude_JoinsUnderTheSamplersName_WithoutAFalseSD0230_D3DCompiler() =>
        ALegacySamplerInAnInclude_JoinsUnderTheSamplersName(DxbcBackend.D3DCompiler);

    private static async Task ALegacySamplerInAnInclude_JoinsUnderTheSamplersName(DxbcBackend backend)
    {
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

    [Theory]
    [InlineData("U60_StateTextureGetDimsUsed")]   // Tb.GetDimensions, then tex2D(A), then tex2D(SB)
    [InlineData("U63_StateTextureSampleKept")]    // the author's own Tb.Sample(SB, uv), which the rewrite's reads look like
    public async Task ALegacySamplersTextureWithAMethodOfItsOwn_WarnsSD0230(string shape)
    {
        // mgfxc 3.8.4.1: A@t0 Tb@t1 Tb@t2, the method use a record of its own. Counting the method
        // call as the legacy read put Tb on t0, silently.
        (MgfxBlobReader subject, IReadOnlyList<ShaderError> warnings) = await CompileAsync(shape, DxbcBackend.Vkd3d);
        warnings.Where(w => w.Code == "SD0230").ShouldHaveSingleItem()
                .Message.ShouldContain("'Tb' is also used through a method call", Case.Sensitive);
        Tables(subject).ShouldHaveSingleItem().ShouldBe("ps: A@t0s0 Tb@t1s1");
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
