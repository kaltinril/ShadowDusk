#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The per-shader <c>ShaderVersion</c> bytes a KNIFX container records for each profile, written
/// literally and through a macro, on the DirectX 11 body and the desktop OpenGL body. KNI's own
/// effect processor (<c>nkast.Xna.Framework.Content.Pipeline.Builder</c>, <c>/platform:Windows</c>
/// on 4.2.9001 and 4.3.9001 and <c>/platform:DesktopGL</c> on 4.3.9001, measured 2026-10-28) writes
/// exactly these zigzag-packed pairs: <c>*_4_0_level_9_1</c> is 2.0 (<c>04 00</c>), <c>*_4_0_level_9_3</c>
/// is 3.0 (<c>06 00</c>), <c>*_4_0</c> is 4.0 (<c>08 00</c>), <c>*_5_0</c> is 5.0 (<c>0a 00</c>).
/// ShadowDusk wrote 4.0 for both feature-level-9 forms, which KNI's runtime refuses on the HiDef
/// profile ("Shader model 4.0 is not supported by the current graphics profile 'HiDef'").
/// <c>*_4_0_level_9_0</c>, which KNI's processor refuses, keeps ShadowDusk's existing 4.0 (a known
/// divergence: that output loads on KNI GL from FL10_0 up, so it is not changed here).
/// </summary>
[Trait("Category", "Integration")]
public sealed class KnifxLevel9ShaderModelTests
{
    private static CancellationTokenSource Cts() => new(TestBudget.Compile);

    private const string Functions = """
        float4 V(float4 p : POSITION0) : SV_POSITION { return p; }
        float4 P(float4 c : COLOR0) : SV_TARGET { return c; }

        """;

    private static string Literal(string suffix) => Functions +
        $"technique T {{ pass {{ VertexShader = compile vs_{suffix} V(); PixelShader = compile ps_{suffix} P(); }} }}\n";

    private static string Macro(string suffix) =>
        $"#define VS_SM vs_{suffix}\n#define PS_SM ps_{suffix}\n" + Functions +
        "technique T { pass { VertexShader = compile VS_SM V(); PixelShader = compile PS_SM P(); } }\n";

    public static TheoryData<PlatformTarget, string, string> Cases()
    {
        var data = new TheoryData<PlatformTarget, string, string>();
        foreach (PlatformTarget target in new[] { PlatformTarget.DirectX, PlatformTarget.OpenGL })
        {
            data.Add(target, "4_0_level_9_1", "0400");
            data.Add(target, "4_0_level_9_3", "0600");
            data.Add(target, "4_0", "0800");
            data.Add(target, "5_0", "0a00");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Knifx_RecordsTheShaderModelKnisOwnPipelineRecords(PlatformTarget target, string suffix, string bytes)
    {
        byte[] literal = (await Compile(Literal(suffix), target, EffectContainer.Knifx)).Value.Data;
        byte[] macro = (await Compile(Macro(suffix), target, EffectContainer.Knifx)).Value.Data;

        // Two shaders (VS then PS), each carrying the same version pair.
        FirstBodyVersionBytes(literal).ShouldBe([bytes, bytes]);
        FirstBodyVersionBytes(macro).ShouldBe([bytes, bytes]);
    }

    [Fact]
    public async Task Knifx_OpenGl_Level90_KeepsItsExistingShaderModel()
    {
        // KNI's own processor refuses *_4_0_level_9_0, but ShadowDusk's GL KNIFX output for it
        // (desktop body 4.0) loads on KNI SDL2.GL at FL10_0 and up, and its GLES/WebGL body (0.0)
        // at every profile, so it stays as it was before the feature-level-9 fix.
        foreach (string source in new[] { Literal("4_0_level_9_0"), Macro("4_0_level_9_0") })
        {
            var result = await Compile(source, PlatformTarget.OpenGL, EffectContainer.Knifx);

            result.IsSuccess.ShouldBeTrue(
                result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
            FirstBodyVersionBytes(result.Value.Data).ShouldBe(["0800", "0800"]);
        }
    }

    [Fact]
    public async Task Knifx_DirectX_Level90_StaysTheDirectX11FloorError()
    {
        var result = await Compile(Literal("4_0_level_9_0"), PlatformTarget.DirectX, EffectContainer.Knifx);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldHaveSingleItem().Code.ShouldBe("SD0015");
    }

    [Fact]
    public async Task Mgfx_OpenGl_Level90_IsUnchanged()
    {
        // An MGFX OpenGL compile of the same profile records no shader model and is untouched.
        var result = await Compile(Literal("4_0_level_9_0"), PlatformTarget.OpenGL, EffectContainer.Mgfx);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> Compile(
        string source, PlatformTarget target, EffectContainer container)
    {
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "KnifxLevel9.fx",
        }, cts.Token);
        // Only DirectX refuses *_4_0_level_9_0 (SD0015, below mgfxc's DirectX_11 floor).
        bool expectsFailure = target == PlatformTarget.DirectX && source.Contains("level_9_0", StringComparison.Ordinal);
        if (!expectsFailure)
            result.IsSuccess.ShouldBeTrue(
                result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result;
    }

    /// <summary>
    /// The raw version bytes (hex, Major then Minor, zigzag-packed) of every shader in the
    /// container's FIRST body: the DirectX 11 body for a DirectX target, the desktop OpenGL body
    /// (backend 17) for an OpenGL one. Only the shapes these tests compile are modelled.
    /// </summary>
    private static List<string> FirstBodyVersionBytes(byte[] data)
    {
        Encoding.ASCII.GetString(data, 0, 4).ShouldBe("KNIF");
        int bodyOffset = BitConverter.ToInt32(data, 10 + 2 + 4);
        int i = bodyOffset + 4 + 1; // body length prefix, integersAsFloats bool

        Packed(data, ref i).ShouldBe(0, "no constant buffers in these shapes");

        var versions = new List<string>();
        int shaders = Packed(data, ref i);
        for (int s = 0; s < shaders; s++)
        {
            i++; // stage
            int start = i;
            Packed(data, ref i);
            Packed(data, ref i);
            versions.Add(Convert.ToHexString(data, start, i - start).ToLowerInvariant());
            i += 4 + BitConverter.ToInt32(data, i);
            Packed(data, ref i).ShouldBe(0, "no samplers in these shapes");
            int cbufferIndices = Packed(data, ref i);
            for (int c = 0; c < cbufferIndices; c++)
                Packed(data, ref i);
            int attributes = Packed(data, ref i);
            for (int a = 0; a < attributes; a++)
            {
                int length = (int)Leb128(data, ref i);
                i += length;    // name
                i++;            // usage
                Packed(data, ref i);
                i += 2;         // location
            }
        }
        return versions;
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
