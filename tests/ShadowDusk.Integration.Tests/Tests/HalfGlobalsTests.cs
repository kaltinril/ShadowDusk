#nullable enable

using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.Integration.Tests.Dxc;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// <c>half</c> / <c>halfN</c> / <c>halfNxM</c> GLOBALS (and cbuffer members), the XNA-era spelling
/// of a float uniform: <c>half4 Tint = half4(1, 0.5, 0, 1);</c>.
///
/// <para><b>The gap.</b> The DirectX target (vkd3d-shader) refused every such declaration with
/// <c>E5002: The half type is not allowed for global variables.</c>, while <c>mgfxc</c> 3.8.4.1
/// (the pinned v10 oracle) compiles it: its DirectX_11 compile passes fxc
/// <c>D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY</c>, under which a <c>half</c> global IS a
/// <c>float</c> (fxc without the flag: <c>X3650 ... To treat this variable as a float, use the
/// backwards compatibility flag.</c>). vkd3d 2.1 has the matching switch,
/// <c>VKD3D_SHADER_COMPILE_OPTION_BACKCOMPAT_ALLOW_HALF_GLOBALS</c>, which vkd3d-utils' own
/// <c>D3DCompile</c> sets exactly when that fxc flag is set; ShadowDusk now passes it on the
/// SM4+ target (<c>Vkd3dCompileContract.ResolveCompileOptions</c>). <c>half</c> locals,
/// parameters and struct fields always compiled.</para>
///
/// <para><b>What "faithful" means here, measured 2026-10-28.</b> The DXBC of a <c>half</c> effect
/// equals its <c>float</c> twin's in every chunk except one string: the RDEF type NAME keeps the
/// author's spelling (<c>half4</c>). fxc does exactly the same (the d3dcompiler_47 oracle's
/// unstripped blob says <c>half4</c> too); <c>mgfxc</c> strips RDEF from what it saves, a known
/// inert difference. The reflected TYPE (<c>D3D_SVT_FLOAT</c>), class, rows, columns, offsets and
/// initializer defaults are the twin's, so the MGFX parameter table equals the twin's and the
/// committed <c>mgfxc</c> golden's. The DXC targets (DirectX 12, Vulkan, OpenGL, KNIFX) treat
/// <c>half</c> as <c>float</c> already (no <c>-enable-16bit-types</c>) and are byte-identical to
/// the twin. FNA is the exception by design: the FNA reference, <c>fxc /T fx_2_0</c> with default
/// flags (d3dcompiler_43 and _47 alike), refuses a <c>half</c> global with X3650, so ShadowDusk's
/// E5002 there is reference parity and the SM1-3 target gets no option.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class HalfGlobalsTests
{
    private const string Fixture = "HalfGlobals.fx";

    /// <summary>Every <c>half</c> global shape the DirectX targets take, cbuffer members included.</summary>
    private const string AllShapes = """
        half Scalar = 0.5;
        half2 V2 = half2(0.25, 0.75);
        half3 V3;
        half4 Tint = half4(1, 0.5, 0, 1);
        uniform half4 Uniform4 = { 0.5, 0.25, 0.125, 1 };
        static const half Doubler = 2.0;
        half4x4 M44;
        half3x3 M33 = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        half2x4 M24;
        row_major half2x2 Rm = { 1, 2, 3, 4 };
        half Arr[3] = { 0.1, 0.2, 0.3 };
        cbuffer HalfParams
        {
            half4 Glow;
            half  GlowScale;
            half3x3 GlowMatrix;
        };
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
        {
            float4 c = Tint * Scalar * Doubler + Uniform4 + float4(V2, V3.x, 0) + mul(float4(uv, 0, 1), M44);
            c.rgb += mul(V3, M33) + mul(Glow.rgb, GlowMatrix) * GlowScale;
            c.xy += mul(V2, Rm) + mul(V2, M24).zw;
            return c + Arr[1];
        }
        technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
        """;

    /// <summary><see cref="AllShapes"/> with no cbuffer: Vulkan takes one constant buffer per stage (SD0026).</summary>
    private const string GlobalShapes = """
        half Scalar = 0.5;
        half2 V2 = half2(0.25, 0.75);
        half3 V3;
        half4 Tint = half4(1, 0.5, 0, 1);
        half4x4 M44;
        half3x3 M33 = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        half Arr[3] = { 0.1, 0.2, 0.3 };
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
        {
            float4 c = Tint * Scalar + float4(V2, V3.x, 0) + mul(float4(uv, 0, 1), M44);
            c.rgb += mul(V3, M33);
            return c + Arr[1];
        }
        technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
        """;

    /// <summary>
    /// The <c>float</c> twin: every <c>half</c> TYPE keyword (<c>half</c>, <c>halfN</c>,
    /// <c>halfNxM</c>) becomes <c>float</c>; an identifier that only starts with <c>half</c>
    /// (<c>halfWidth</c>) is left alone. Line and column structure is unchanged per token.
    /// </summary>
    private static string FloatTwin(string source) =>
        Regex.Replace(source, @"\bhalf(?=(?:[1-4](?:x[1-4])?)?\b)", "float");

    private static string Load(string fixture) =>
        File.ReadAllText(TestHelpers.FixturePath(fixture)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string source, PlatformTarget target, string fileName,
        DxbcBackend backend = DxbcBackend.Vkd3d, EffectContainer container = EffectContainer.Mgfx)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        return await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            DxbcBackend = backend,
            Container = container,
            SourceFileName = fileName,
        }, cts.Token);
    }

    private static async Task<CompiledShader> CompileOkAsync(
        string source, PlatformTarget target, string fileName,
        DxbcBackend backend = DxbcBackend.Vkd3d, EffectContainer container = EffectContainer.Mgfx)
    {
        var result = await CompileAsync(source, target, fileName, backend, container);
        result.IsSuccess.ShouldBeTrue(
            $"must compile for {target} ({backend}, {container}): " +
            (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code} ({e.Line},{e.Column}): {e.Message}")) : ""));
        return result.Value;
    }

    // ---- DirectX 11: the gap ----------------------------------------------------------------------

    [FnaFact] // the vkd3d-shader availability gate
    public async Task DirectX11_TheReportedRepro_Compiles()
    {
        // The reviewer's repro, verbatim: E5002 at (1,7) before the fix.
        const string repro = """
            half4 Tintz = half4(0.25, 0.5, 1, 1);
            float4 P(float2 uv : TEXCOORD0) : COLOR0 { return Tintz; }
            technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
            """;

        CompiledShader shader = await CompileOkAsync(repro, PlatformTarget.DirectX, "half.fx");

        ParameterReflectionShouldBeFloat4(shader, "Tintz", [0.25f, 0.5f, 1f, 1f]);
    }

    [FnaFact]
    public async Task DirectX11_EveryHalfShape_ReflectsAndEncodesExactlyAsItsFloatTwin()
    {
        CompiledShader half = await CompileOkAsync(AllShapes, PlatformTarget.DirectX, "Shapes.fx");
        CompiledShader twin = await CompileOkAsync(FloatTwin(AllShapes), PlatformTarget.DirectX, "Shapes.fx");

        AssertDxbcEffectEqualsTwinExceptRdefTypeNames(half, twin);
    }

    [FnaFact]
    public async Task DirectX11_TheFixture_EqualsItsFloatTwin_ExceptTheRdefTypeNames()
    {
        string source = Load(Fixture);
        CompiledShader half = await CompileOkAsync(source, PlatformTarget.DirectX, Fixture);
        CompiledShader twin = await CompileOkAsync(FloatTwin(source), PlatformTarget.DirectX, Fixture);

        AssertDxbcEffectEqualsTwinExceptRdefTypeNames(half, twin);
    }

    /// <summary>
    /// The d3dcompiler_47 oracle (real fxc, with mgfxc's flags) gives the same verdict, which is
    /// what makes "the RDEF keeps <c>half4</c>" fxc's behavior rather than vkd3d's.
    /// </summary>
    [WindowsFact]
    public async Task DirectX11_D3DCompilerOracle_AlsoKeepsTheHalfTypeNameAndOnlyThat()
    {
        string source = Load(Fixture);
        CompiledShader half = await CompileOkAsync(source, PlatformTarget.DirectX, Fixture, DxbcBackend.D3DCompiler);
        CompiledShader twin = await CompileOkAsync(FloatTwin(source), PlatformTarget.DirectX, Fixture, DxbcBackend.D3DCompiler);

        AssertDxbcEffectEqualsTwinExceptRdefTypeNames(half, twin);
    }

    /// <summary>
    /// Against the reference: <c>tests/fixtures/golden/DirectX_11/HalfGlobals.mgfx</c> is
    /// <c>mgfxc</c> 3.8.4.1's own build of the fixture (<c>tools/compile-fixtures.ps1</c>). Its
    /// parameter table (name, class, type, rows, columns, elements) and its <c>$Globals</c>
    /// layout must be ShadowDusk's, on both DXBC backends, and so must every scalar and vector
    /// default. The two default blobs that differ are the documented, deliberate #422
    /// deviations (CHANGELOG 0.22.0), the same for the <c>float</c> twin: mgfxc's DirectX 11
    /// build writes a matrix default in declaration order where the runtime reads it column by
    /// column, and drops array elements to zero.
    /// </summary>
    [FnaFact]
    public async Task DirectX11_TheFixture_TableEqualsTheMgfxcGolden_Vkd3d()
        => await AssertFixtureTableEqualsGoldenAsync(DxbcBackend.Vkd3d);

    [WindowsFact]
    public async Task DirectX11_TheFixture_TableEqualsTheMgfxcGolden_D3DCompilerOracle()
        => await AssertFixtureTableEqualsGoldenAsync(DxbcBackend.D3DCompiler);

    private static async Task AssertFixtureTableEqualsGoldenAsync(DxbcBackend backend)
    {
        CompiledShader shader = await CompileOkAsync(Load(Fixture), PlatformTarget.DirectX, Fixture, backend);
        var subject = MgfxBlobReader.Parse(shader.Data);
        var golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath("DirectX_11", "HalfGlobals")));

        golden.MgfxVersion.ShouldBe((byte)10);
        subject.MgfxVersion.ShouldBe((byte)10);
        subject.ProfileId.ShouldBe(golden.ProfileId);

        DescribeParameters(subject.Parameters, withDefaults: false)
            .ShouldBe(DescribeParameters(golden.Parameters, withDefaults: false));
        foreach (string name in new[] { "Tint", "Offset", "Strength", "Bias" })
        {
            subject.Parameters.Single(p => p.Name == name).DefaultValue
                .ShouldBe(golden.Parameters.Single(p => p.Name == name).DefaultValue, $"{name}'s default blob is mgfxc's");
        }

        // #422's matrix layout: ShadowDusk's blob is mgfxc's declaration-order blob transposed.
        float[] goldenMatrix = Floats(golden.Parameters.Single(p => p.Name == "ColorMatrix").DefaultValue);
        float[] ourMatrix = Floats(subject.Parameters.Single(p => p.Name == "ColorMatrix").DefaultValue);
        goldenMatrix.ShouldBe([0.5f, 0, 0, 0, 0, 1, 0, 0, 0.25f, 0, 1, 0, 0, 0, 0, 1], "mgfxc: declaration order");
        ourMatrix.ShouldBe(Enumerable.Range(0, 16).Select(i => goldenMatrix[(i % 4) * 4 + i / 4]).ToArray(),
            "ShadowDusk: the same matrix, column by column (#422)");

        // #422's arrays: mgfxc's DirectX 11 build zeroes the elements; ShadowDusk keeps the initializer.
        golden.Parameters.Single(p => p.Name == "Weights").Elements.Select(e => Floats(e.DefaultValue)[0]).ShouldBe([0f, 0f]);
        subject.Parameters.Single(p => p.Name == "Weights").Elements.Select(e => Floats(e.DefaultValue)[0]).ShouldBe([0.25f, 0.5f]);

        subject.ConstantBuffers.Select(c => (c.Name, c.Size, string.Join(",", c.ParameterIndices), string.Join(",", c.ParameterOffsets)))
            .ShouldBe(golden.ConstantBuffers.Select(c => (c.Name, c.Size, string.Join(",", c.ParameterIndices), string.Join(",", c.ParameterOffsets))));

        // mgfxc 3.8.4.1, measured: every half global is a Single parameter with the float shape.
        MgfxParameterRecord tint = golden.Parameters.Single(p => p.Name == "Tint");
        (tint.Class, tint.Type, tint.Rows, tint.Columns).ShouldBe(((byte)1, (byte)3, (byte)1, (byte)4),
            "EffectParameterClass.Vector, EffectParameterType.Single, 1x4");
        MgfxParameterRecord matrix = golden.Parameters.Single(p => p.Name == "ColorMatrix");
        (matrix.Class, matrix.Type, matrix.Rows, matrix.Columns).ShouldBe(((byte)2, (byte)3, (byte)4, (byte)4),
            "EffectParameterClass.Matrix, EffectParameterType.Single, 4x4");

        // And the library's own reflection says the same, defaults included.
        ParameterReflectionShouldBeFloat4(shader, "Tint", [1f, 0.5f, 0.25f, 1f]);
        shader.Parameters.Single(p => p.Name == "Strength").DefaultValue.ShouldBe([0.75f]);
        shader.Parameters.Single(p => p.Name == "Bias").DefaultValue.ShouldBeNull("no initializer: unknown, never zero");
        shader.Parameters.Single(p => p.Name == "Weights").DefaultValue.ShouldBe([0.25f, 0.5f]);
    }

    /// <summary>
    /// A real error in a shader that declares <c>half</c> globals keeps vkd3d's verbatim text at
    /// the author's line and column, identical to the same error in the <c>float</c> twin: the
    /// option changes what compiles, never where a diagnostic points.
    /// </summary>
    [FnaFact]
    public async Task DirectX11_ADiagnosticAfterHalfGlobals_IsTheFloatTwinsDiagnostic_AtTheAuthorsPosition()
    {
        const string broken = """
            half4 Tint = half4(1, 0.5, 0, 1);
            half  Scale = 0.5;
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0
            {
                return Tint * Scale * Missing;
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;

        var half = await CompileAsync(broken, PlatformTarget.DirectX, "Broken.fx");
        var twin = await CompileAsync(FloatTwin(broken), PlatformTarget.DirectX, "Broken.fx");

        half.IsFailure.ShouldBeTrue();
        twin.IsFailure.ShouldBeTrue();
        ShaderError error = half.Error[0];
        error.Line.ShouldBe(5);
        error.Column.ShouldBe(27, "the column of 'Missing' on the author's line");
        error.Message.ShouldContain("Missing", Case.Sensitive);
        error.Message.ShouldNotContain("half type", Case.Sensitive);
        (error.Code, error.Line, error.Column, error.Message).ShouldBe(
            (twin.Error[0].Code, twin.Error[0].Line, twin.Error[0].Column, twin.Error[0].Message));
    }

    // ---- The DXC targets: byte-identical to the twin -----------------------------------------------

    public static TheoryData<PlatformTarget, EffectContainer, string> DxcTwinCases() => new()
    {
        { PlatformTarget.DirectX12, EffectContainer.Mgfx, nameof(AllShapes) },
        { PlatformTarget.DirectX12, EffectContainer.Mgfx, Fixture },
        { PlatformTarget.Vulkan,    EffectContainer.Mgfx, nameof(GlobalShapes) },
        { PlatformTarget.Vulkan,    EffectContainer.Mgfx, Fixture },
        { PlatformTarget.OpenGL,    EffectContainer.Mgfx, Fixture },
        { PlatformTarget.OpenGL,    EffectContainer.Knifx, Fixture },
    };

    [Theory]
    [MemberData(nameof(DxcTwinCases))]
    [Trait("Platform", "DirectX12")]
    [Trait("Platform", "Vulkan")]
    [Trait("Platform", "OpenGL")]
    public async Task DxcTargets_HalfEffect_IsByteIdenticalToItsFloatTwin(PlatformTarget target, EffectContainer container, string shape)
    {
        string source = shape switch
        {
            nameof(AllShapes) => AllShapes,
            nameof(GlobalShapes) => GlobalShapes,
            _ => Load(shape),
        };

        CompiledShader half = await CompileOkAsync(source, target, "Shape.fx", container: container);
        CompiledShader twin = await CompileOkAsync(FloatTwin(source), target, "Shape.fx", container: container);

        half.Data.ShouldBe(twin.Data, $"DXC has no 16-bit types without -enable-16bit-types: on {target} a half IS a float");
        DescribeReflection(half).ShouldBe(DescribeReflection(twin));
        half.Warnings.Select(w => w.FxcFormattedMessage).ShouldBe(twin.Warnings.Select(w => w.FxcFormattedMessage));
    }

    // ---- FNA: reference parity is a refusal ---------------------------------------------------------

    [FnaFact]
    [Trait("Platform", "FNA")]
    public async Task Fna_HalfGlobal_IsRefusedWithVkd3dsE5002_AtTheDeclaration_AsFxcFx20RefusesIt()
    {
        // fxc /T fx_2_0 (default flags, d3dcompiler_47 and the June 2010 d3dcompiler_43), measured:
        // "HalfGlobals.fx(27,7-10): error X3650: 'Tint': global variables cannot use the 'half' type
        // in ps_2_0. To treat this variable as a float, use the backwards compatibility flag."
        var result = await CompileAsync(Load(Fixture), PlatformTarget.Fna, Fixture);

        result.IsFailure.ShouldBeTrue("the FNA reference compiler refuses a half global; so does ShadowDusk");
        ShaderError error = result.Error[0];
        error.Code.ShouldBe("E5002");
        error.Message.ShouldBe("The half type is not allowed for global variables.");
        (error.Line, error.Column).ShouldBe((27, 7), "the 'Tint' declaration, where fxc points too");
    }

    [FnaFact]
    [Trait("Platform", "FNA")]
    public async Task Fna_HalfLocalsAndParameters_StillCompile()
    {
        const string locals = """
            float4 Tint = float4(1, 0.5, 0.25, 1);
            half4 Shade(half4 c, half k) { half4 shaded = c * k; return shaded; }
            float4 MainPS(float2 uv : TEXCOORD0) : COLOR0 { half k = 0.5; return Shade(Tint, k); }
            technique T { pass P { PixelShader = compile ps_2_0 MainPS(); } }
            """;

        await CompileOkAsync(locals, PlatformTarget.Fna, "Locals.fx");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static void ParameterReflectionShouldBeFloat4(CompiledShader shader, string name, float[] defaults)
    {
        var p = shader.Parameters.Single(x => x.Name == name);
        p.Class.ShouldBe(EffectParameterClass.Vector);
        p.Type.ShouldBe(EffectParameterType.Single);
        (p.Rows, p.Columns, p.Elements).ShouldBe((1, 4, 0));
        p.DefaultValue.ShouldBe(defaults);
    }

    private static float[] Floats(byte[]? blob)
    {
        blob.ShouldNotBeNull("a default blob must be present");
        return Enumerable.Range(0, blob.Length / 4)
            .Select(i => BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(4 * i)))
            .ToArray();
    }

    private static IEnumerable<string> DescribeReflection(CompiledShader shader) =>
        shader.Parameters.Select(p =>
            $"{p.Name} {p.Class} {p.Type} {p.Rows}x{p.Columns}[{p.Elements}] = " +
            (p.DefaultValue is null ? "?" : string.Join(",", p.DefaultValue)));

    private static IEnumerable<string> DescribeParameters(IEnumerable<MgfxParameterRecord> parameters, bool withDefaults = true) =>
        parameters.Select(p =>
            $"{p.Name} sem='{p.Semantic}' class={p.Class} type={p.Type} {p.Rows}x{p.Columns} " +
            $"elems={p.ElementCount} members={p.MemberCount}" +
            (withDefaults ? $" default={(p.DefaultValue is null ? "-" : Convert.ToHexString(p.DefaultValue))}" : "") +
            (p.Elements.Count == 0 ? "" : " [" + string.Join("; ", DescribeParameters(p.Elements, withDefaults)) + "]") +
            (p.Members.Count == 0 ? "" : " {" + string.Join("; ", DescribeParameters(p.Members, withDefaults)) + "}"));

    /// <summary>
    /// The whole DirectX effect equals the twin's: the MGFX parameter table (defaults included),
    /// the constant-buffer records and every shader record; inside each DXBC every chunk but RDEF
    /// is byte-identical, and RDEF differs in nothing but the type-name strings, which spell
    /// <c>half…</c> where the twin spells <c>float…</c>.
    /// </summary>
    private static void AssertDxbcEffectEqualsTwinExceptRdefTypeNames(CompiledShader half, CompiledShader twin)
    {
        DescribeReflection(half).ShouldBe(DescribeReflection(twin));

        var h = MgfxBlobReader.Parse(half.Data);
        var t = MgfxBlobReader.Parse(twin.Data);
        DescribeParameters(h.Parameters).ShouldBe(DescribeParameters(t.Parameters));
        h.ConstantBuffers.Select(c => (c.Name, c.Size, string.Join(",", c.ParameterIndices), string.Join(",", c.ParameterOffsets)))
            .ShouldBe(t.ConstantBuffers.Select(c => (c.Name, c.Size, string.Join(",", c.ParameterIndices), string.Join(",", c.ParameterOffsets))));
        h.Samplers.Count.ShouldBe(t.Samplers.Count);
        h.Shaders.Count.ShouldBe(t.Shaders.Count);

        bool sawHalfName = false;
        for (int i = 0; i < h.Shaders.Count; i++)
        {
            h.Shaders[i].IsVertex.ShouldBe(t.Shaders[i].IsVertex);
            h.Shaders[i].ConstantBufferIndices.ShouldBe(t.Shaders[i].ConstantBufferIndices);

            Dictionary<string, byte[]> hc = DxbcChunks(h.Shaders[i].Bytecode);
            Dictionary<string, byte[]> tc = DxbcChunks(t.Shaders[i].Bytecode);
            hc.Keys.ShouldBe(tc.Keys);
            foreach (string tag in hc.Keys.Where(k => k != "RDEF"))
                hc[tag].ShouldBe(tc[tag], $"shader {i} chunk {tag} must be byte-identical to the float twin's");

            if (!hc.TryGetValue("RDEF", out byte[]? hr))
                continue;
            List<RdefVariable> hv = RdefVariables(hr);
            List<RdefVariable> tv = RdefVariables(tc["RDEF"]);
            hv.Select(v => v with { TypeName = "" }).ShouldBe(tv.Select(v => v with { TypeName = "" }),
                $"shader {i}: every RDEF variable descriptor but its type name equals the twin's");
            for (int v = 0; v < hv.Count; v++)
            {
                string expectedName = Regex.Replace(tv[v].TypeName, @"^float", "half");
                if (hv[v].TypeName != tv[v].TypeName)
                {
                    hv[v].TypeName.ShouldBe(expectedName, $"shader {i} variable {hv[v].Name}: only the half/float spelling may differ");
                    sawHalfName = true;
                }
            }
        }

        sawHalfName.ShouldBeTrue("the half effect's RDEF must keep the author's half type names, as fxc's blob does");
    }

    private sealed record RdefVariable(
        string Buffer, string Name, uint Offset, uint Size, uint Flags,
        ushort Class, ushort Type, ushort Rows, ushort Columns, ushort Elements, string Default, string TypeName);

    private static Dictionary<string, byte[]> DxbcChunks(byte[] dxbc)
    {
        Encoding.ASCII.GetString(dxbc, 0, 4).ShouldBe("DXBC");
        int count = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(28));
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(32 + 4 * i));
            string tag = Encoding.ASCII.GetString(dxbc, offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(offset + 4));
            chunks.Add(tag, dxbc.AsSpan(offset + 8, size).ToArray());
        }
        return chunks;
    }

    /// <summary>
    /// The constant-buffer variables of an SM5 (RD11) RDEF chunk: buffer, name, offset, size,
    /// flags, the type descriptor (class, type, rows, columns, elements), the default value
    /// blob and the type-name string.
    /// </summary>
    private static List<RdefVariable> RdefVariables(byte[] rdef)
    {
        string Str(uint at)
        {
            int end = Array.IndexOf(rdef, (byte)0, (int)at);
            return Encoding.ASCII.GetString(rdef, (int)at, end - (int)at);
        }
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(rdef.AsSpan(at));
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(rdef.AsSpan(at));

        Encoding.ASCII.GetString(rdef, 28, 4).ShouldBe("RD11", "both DXBC backends write the SM5 RDEF layout");
        int cbCount = (int)U32(0), cbOffset = (int)U32(4);
        var list = new List<RdefVariable>();
        for (int c = 0; c < cbCount; c++)
        {
            int cb = cbOffset + 24 * c;
            string buffer = Str(U32(cb));
            int varCount = (int)U32(cb + 4), varOffset = (int)U32(cb + 8);
            for (int v = 0; v < varCount; v++)
            {
                int at = varOffset + 40 * v;
                uint size = U32(at + 8), typeOffset = U32(at + 16), defaultOffset = U32(at + 20);
                int ty = (int)typeOffset;
                list.Add(new RdefVariable(
                    buffer, Str(U32(at)), U32(at + 4), size, U32(at + 12),
                    U16(ty), U16(ty + 2), U16(ty + 4), U16(ty + 6), U16(ty + 8),
                    defaultOffset == 0 ? "" : Convert.ToHexString(rdef, (int)defaultOffset, (int)size),
                    Str(U32(ty + 32))));
            }
        }
        return list;
    }

    private static string GoldenPath(string profile, string stem)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("repo root (ShadowDusk.slnx) not found");
        string path = Path.Combine(dir.FullName, "tests", "fixtures", "golden", profile, stem + ".mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc golden must exist at {path}");
        return path;
    }
}
