#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// <c>*_2_0</c> and <c>*_3_0</c> on the KNIFX container for DirectX. KNI's own DirectX pipeline
/// (<c>ShaderProfileDX11.CreateShader</c>, KNI 4.2.9001 and 4.3.9001) accepts them and hands fxc
/// <c>*_4_0_level_9_1</c> and <c>*_4_0_level_9_3</c> instead, recording 2.0 and 3.0. Measured
/// 2026-10-28 with KNI's own MGCB (<c>/platform:Windows</c>, both versions): the <c>.xnb</c> of a
/// <c>vs_2_0</c>/<c>ps_2_0</c> effect is byte-identical to the same effect at
/// <c>vs_4_0_level_9_1</c>/<c>ps_4_0_level_9_1</c>, literal or through a macro, and <c>_3_0</c> to
/// <c>_4_0_level_9_3</c>. ShadowDusk refused all four with <c>SD0015</c> (mgfxc's DirectX floor),
/// so no XNA-era effect could target KNIFX on DirectX. MGFX on DirectX keeps mgfxc's refusal, and
/// every profile KNI's processor refuses stays refused.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KnifxDirectXSm2Sm3Tests
{
    private static CancellationTokenSource Cts() => new(TestBudget.Compile);

    private const string Functions = """
        float4 Tint;
        float4 V(float4 p : POSITION0) : SV_POSITION { return p; }
        float4 P(float4 c : COLOR0) : SV_TARGET { return c * Tint; }

        """;

    // The OpenGL row has no constant buffer, so the walker below only models the DirectX one.
    private const string PlainFunctions = """
        float4 V(float4 p : POSITION0) : SV_POSITION { return p; }
        float4 P(float4 c : COLOR0) : SV_TARGET { return c; }

        """;

    private static string Literal(string vs, string ps, string functions = Functions) => functions +
        $"technique T {{ pass {{ VertexShader = compile {vs} V(); PixelShader = compile {ps} P(); }} }}\n";

    private static string Macro(string vs, string ps) =>
        $"#define VS_SM {vs}\n#define PS_SM {ps}\n" + Functions +
        "technique T { pass { VertexShader = compile VS_SM V(); PixelShader = compile PS_SM P(); } }\n";

    /// <summary>
    /// A KNIFX DirectX compile of each SM2/SM3 pair gives exactly the bytes its feature-level-9
    /// twin gives, as KNI's own build does: the same DXBC and the same recorded shader models.
    /// </summary>
    [Theory]
    [InlineData("vs_2_0", "ps_2_0", "vs_4_0_level_9_1", "ps_4_0_level_9_1", "0400", "0400")]
    [InlineData("vs_3_0", "ps_3_0", "vs_4_0_level_9_3", "ps_4_0_level_9_3", "0600", "0600")]
    [InlineData("vs_2_0", "ps_3_0", "vs_4_0_level_9_1", "ps_4_0_level_9_3", "0400", "0600")]
    [InlineData("vs_3_0", "ps_2_0", "vs_4_0_level_9_3", "ps_4_0_level_9_1", "0600", "0400")]
    public async Task Knifx_DirectX_Sm2Sm3_CompilesAsTheKniFeatureLevel9Profile(
        string vs, string ps, string vsTwin, string psTwin, string vsBytes, string psBytes)
    {
        byte[] twin = await CompileOk(Literal(vsTwin, psTwin), PlatformTarget.DirectX, EffectContainer.Knifx);

        foreach (string source in new[] { Literal(vs, ps), Macro(vs, ps) })
        {
            byte[] data = await CompileOk(source, PlatformTarget.DirectX, EffectContainer.Knifx);

            data.ShouldBe(twin, $"KNI's own build of {vs}/{ps} is byte-identical to its {vsTwin}/{psTwin} build");
            FirstBodyShaders(data).ShouldBe([("vs", vsBytes), ("ps", psBytes)]);
        }
    }

    /// <summary>
    /// The DXBC in that body is SM 5 DXBC, the model ShadowDusk compiles every DirectX 11 shader at,
    /// so a <c>ps_2_0</c> shader carries the same bytecode an MGFX build of its
    /// <c>ps_4_0_level_9_1</c> twin carries.
    /// </summary>
    [Fact]
    public async Task Knifx_DirectX_Ps20_CarriesTheSameDxbcAsMgfxLevel91()
    {
        byte[] knifx = await CompileOk(Literal("vs_2_0", "ps_2_0"), PlatformTarget.DirectX, EffectContainer.Knifx);
        byte[] mgfx = await CompileOk(Literal("vs_4_0_level_9_1", "ps_4_0_level_9_1"), PlatformTarget.DirectX, EffectContainer.Mgfx);

        List<byte[]> dxbc = FirstBodyShaderCode(knifx);
        dxbc.Count.ShouldBe(2);
        foreach (byte[] code in dxbc)
        {
            Encoding.ASCII.GetString(code, 0, 4).ShouldBe("DXBC");
            ContainsSubsequence(mgfx, code).ShouldBeTrue("the MGFX ps_4_0_level_9_1 build carries the same DXBC blob");
        }
    }

    /// <summary>MGFX on DirectX follows mgfxc, which refuses every SM1-3 profile.</summary>
    [Theory]
    [InlineData("vs_2_0", "ps_4_0_level_9_1")]
    [InlineData("vs_4_0_level_9_1", "ps_2_0")]
    [InlineData("vs_3_0", "ps_4_0_level_9_1")]
    [InlineData("vs_4_0_level_9_1", "ps_3_0")]
    public async Task Mgfx_DirectX_Sm2Sm3_StaysTheMgfxcFloorError(string vs, string ps)
    {
        foreach (string source in new[] { Literal(vs, ps), Macro(vs, ps) })
        {
            var result = await Compile(source, PlatformTarget.DirectX, EffectContainer.Mgfx);

            result.IsFailure.ShouldBeTrue();
            ShaderError error = result.Error.ShouldHaveSingleItem();
            error.Code.ShouldBe("SD0015");
            error.Message.ShouldContain("mgfxc rejects it", Case.Sensitive);
        }
    }

    /// <summary>
    /// Every recognized profile KNI's own processor refuses on DirectX (measured with KNI's MGCB
    /// 4.2.9001 and 4.3.9001: "Invalid profile 'ps_2_a'. Pixel shader 'P'." and, for SM1, "... must be
    /// at least SM 2.0.") stays refused on KNIFX DirectX, as <c>SD0015</c> naming KNI.
    /// </summary>
    [Theory]
    [InlineData("vs_2_0", "ps_2_a")]
    [InlineData("vs_2_0", "ps_2_b")]
    [InlineData("vs_2_0", "ps_2_sw")]
    [InlineData("vs_3_0", "ps_3_sw")]
    [InlineData("vs_2_0", "ps_1_1")]
    [InlineData("vs_2_0", "ps_1_4")]
    [InlineData("vs_1_1", "ps_2_0")]
    [InlineData("vs_2_a", "ps_2_0")]
    [InlineData("vs_2_sw", "ps_2_0")]
    [InlineData("vs_3_sw", "ps_3_0")]
    [InlineData("vs_4_0_level_9_0", "ps_2_0")]
    [InlineData("vs_2_0", "ps_4_0_level_9_0")]
    [InlineData("vs_2_0", "ps_6_0")]
    public async Task Knifx_DirectX_ProfilesKniRefuses_StayRefused(string vs, string ps)
    {
        string refused = KniAccepts(vs) ? ps : vs;
        foreach (string source in new[] { Literal(vs, ps), Macro(vs, ps) })
        {
            var result = await Compile(source, PlatformTarget.DirectX, EffectContainer.Knifx);

            result.IsFailure.ShouldBeTrue();
            ShaderError error = result.Error.ShouldHaveSingleItem();
            error.Code.ShouldBe("SD0015");
            error.Message.ShouldContain($"'{refused}'", Case.Sensitive);
            error.Message.ShouldContain("KNI's own effect processor refuses it", Case.Sensitive);
        }
    }

    /// <summary>The KNIFX OpenGL route already accepted SM2/SM3; its output does not depend on this change.</summary>
    [Theory]
    [InlineData("vs_2_0", "ps_2_0", "0400")]
    [InlineData("vs_3_0", "ps_3_0", "0600")]
    public async Task Knifx_OpenGl_Sm2Sm3_RecordsTheSameShaderModels(string vs, string ps, string bytes)
    {
        byte[] data = await CompileOk(Literal(vs, ps, PlainFunctions), PlatformTarget.OpenGL, EffectContainer.Knifx);

        FirstBodyShaders(data).ShouldBe([("vs", bytes), ("ps", bytes)]);
    }

    private static bool KniAccepts(string profile) =>
        profile is "vs_2_0" or "vs_3_0" or "ps_2_0" or "ps_3_0";

    private static async Task<byte[]> CompileOk(string source, PlatformTarget target, EffectContainer container)
    {
        var result = await Compile(source, target, container);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value.Data;
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> Compile(
        string source, PlatformTarget target, EffectContainer container)
    {
        using var cts = Cts();
        return await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "KnifxDirectXSm2Sm3.fx",
        }, cts.Token);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    /// <summary>(stage, version bytes as hex) of every shader in the container's FIRST body.</summary>
    private static List<(string Stage, string Version)> FirstBodyShaders(byte[] data) =>
        WalkFirstBody(data).Select(s => (s.Stage, s.Version)).ToList();

    private static List<byte[]> FirstBodyShaderCode(byte[] data) =>
        WalkFirstBody(data).Select(s => s.Code).ToList();

    /// <summary>
    /// Walks the shader records of the container's FIRST body (the DirectX 11 body for a DirectX
    /// target, the desktop OpenGL body for an OpenGL one). Models the shapes these tests compile:
    /// no samplers, and on the DirectX body one constant buffer at most.
    /// </summary>
    private static List<(string Stage, string Version, byte[] Code)> WalkFirstBody(byte[] data)
    {
        Encoding.ASCII.GetString(data, 0, 4).ShouldBe("KNIF");
        int bodyOffset = BitConverter.ToInt32(data, 10 + 2 + 4);
        int i = bodyOffset + 4 + 1; // body length prefix, integersAsFloats bool

        int cbuffers = Packed(data, ref i);
        for (int c = 0; c < cbuffers; c++)
        {
            int nameLength = (int)Leb128(data, ref i);
            i += nameLength;                // name
            Packed(data, ref i);            // size
            int parameters = Packed(data, ref i);
            for (int p = 0; p < parameters; p++)
            {
                Packed(data, ref i);        // parameter index
                i += 2;                     // offset (ushort)
            }
        }

        var shaders = new List<(string, string, byte[])>();
        int count = Packed(data, ref i);
        for (int s = 0; s < count; s++)
        {
            string stage = data[i++] == 1 ? "vs" : "ps";   // KNI's stage byte: Pixel 0, Vertex 1
            int start = i;
            Packed(data, ref i);
            Packed(data, ref i);
            string version = Convert.ToHexString(data, start, i - start).ToLowerInvariant();
            int length = BitConverter.ToInt32(data, i);
            byte[] code = data.AsSpan(i + 4, length).ToArray();
            i += 4 + length;
            Packed(data, ref i).ShouldBe(0, "no samplers in these shapes");
            int cbufferIndices = Packed(data, ref i);
            for (int c = 0; c < cbufferIndices; c++)
                Packed(data, ref i);
            int attributes = Packed(data, ref i);
            for (int a = 0; a < attributes; a++)
            {
                int nameLength = (int)Leb128(data, ref i);
                i += nameLength;                // name
                i++;                            // usage
                Packed(data, ref i);
                i += 2;                         // location
            }
            shaders.Add((stage, version, code));
        }
        return shaders;
    }

    private static int Packed(byte[] data, ref int i)
    {
        uint zz = Leb128(data, ref i);
        return (int)(zz >> 1) ^ -(int)(zz & 1);
    }

    private static uint Leb128(byte[] data, ref int i)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[i++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
    }
}
