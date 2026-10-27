#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Tests.Fx2;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// GitHub issue #407: techniques were read from the raw main file, so a technique inside a false
/// <c>#if</c> was still compiled, a technique in an <c>#include</c>d file was never found, and a
/// macro used as the <c>compile</c> target (<c>compile VS_SM V()</c>) was recorded as shader
/// model 3.0 in a KNIFX container. They now come from the preprocessed translation unit on every
/// target.
///
/// <para>Every expectation was measured against the reference compilers (2026-10-27): KNI's own
/// content pipeline (<c>nkast.Xna.Framework.Content.Pipeline.Builder</c> 4.2.9001,
/// <c>/platform:DesktopGL</c>) and <c>mgfxc</c> 3.8.4.1 (<c>dotnet mgcb /platform:DesktopGL</c>).
/// Both drop a technique in a false <c>#if</c>, keep one guarded by a define that is set, find
/// one in an <c>#include</c>d file (alongside the main file's own), and KNI records shader model
/// 4.0 for <c>compile VS_SM</c> with <c>#define VS_SM vs_4_0</c>, the same as for a literal
/// <c>vs_4_0</c>; <c>mgfxc</c>'s OpenGL profile refuses that expansion ("must be SM 3.0 or
/// lower"), which shows it expands the macro too.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue407PreprocessedTechniqueTests
{
    private static CancellationTokenSource Cts() => new(TestBudget.Compile);

    // The SM4 shapes of the issue (OpenGL, KNIFX, DirectX 11/12, Vulkan) and an SM3 twin for FNA,
    // whose MojoShader ceiling is SM3.
    private static string Functions(bool sm3) => sm3
        ? "float4 P(float4 c : COLOR0) : COLOR0 { return c; }\nfloat4 Q(float4 c : COLOR0) : COLOR0 { return c * 2; }\n"
        : "float4 P(float4 c : COLOR0) : SV_TARGET { return c; }\nfloat4 Q(float4 c : COLOR0) : SV_TARGET { return c * 2; }\n";

    private static string Ps(bool sm3) => sm3 ? "ps_3_0" : "ps_4_0";

    private static string IfZero(bool sm3) => Functions(sm3) + $$"""
        #if 0
        technique A { pass { PixelShader = compile {{Ps(sm3)}} P(); } }
        #endif
        technique B { pass { PixelShader = compile {{Ps(sm3)}} Q(); } }
        """;

    private static string IfTechnique(bool sm3) => Functions(sm3) + $$"""
        #if TECHNIQUE == 1
        technique A { pass { PixelShader = compile {{Ps(sm3)}} P(); } }
        #endif
        technique B { pass { PixelShader = compile {{Ps(sm3)}} Q(); } }
        """;

    private static string Header(bool sm3) =>
        (sm3 ? "float4 P(float4 c : COLOR0) : COLOR0 { return c; }\n" : "float4 P(float4 c : COLOR0) : SV_TARGET { return c; }\n") +
        $"technique Inc {{ pass {{ PixelShader = compile {Ps(sm3)} P(); }} }}\n";

    private const string IncludeOnly = "#include \"tech.fxh\"\n";

    private static string IncludeWithOwn(bool sm3) => "#include \"tech.fxh\"\n" +
        (sm3 ? "float4 Q(float4 c : COLOR0) : COLOR0 { return c * 2; }\n" : "float4 Q(float4 c : COLOR0) : SV_TARGET { return c * 2; }\n") +
        $"technique Own {{ pass {{ PixelShader = compile {Ps(sm3)} Q(); }} }}\n";

    private const string MacroCompileTarget = """
        #define VS_SM vs_4_0
        #define PS_SM ps_4_0
        float4 V(float4 p : POSITION0) : SV_POSITION { return p; }
        float4 P(float4 c : COLOR0) : SV_TARGET { return c; }
        technique T { pass { VertexShader = compile VS_SM V(); PixelShader = compile PS_SM P(); } }
        """;

    private const string LiteralCompileTarget = """
        float4 V(float4 p : POSITION0) : SV_POSITION { return p; }
        float4 P(float4 c : COLOR0) : SV_TARGET { return c; }
        technique T { pass { VertexShader = compile vs_4_0 V(); PixelShader = compile ps_4_0 P(); } }
        """;

    private static async Task<CompiledShader> CompileOk(
        string source, PlatformTarget target, bool sm3 = false, string? define = null,
        EffectContainer container = EffectContainer.Mgfx)
    {
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "Issue407.fx",
            IncludeResolver = new InMemoryIncludeResolver(new Dictionary<string, string> { ["tech.fxh"] = Header(sm3) }),
            Defines = define is null ? [] : [new UserDefine(define, "1")],
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value;
    }

    private static string[] TechniqueNames(CompiledShader shader, PlatformTarget target) =>
        target == PlatformTarget.Fna
            ? Fx2BinaryValidator.Parse(shader.Data).Techniques.Select(t => t.Name).ToArray()
            : MgfxBlobReader.Parse(shader.Data).Techniques.Select(t => t.Name).ToArray();

    public static TheoryData<PlatformTarget> Targets() => new()
    {
        PlatformTarget.OpenGL,
        PlatformTarget.DirectX,
        PlatformTarget.DirectX12,
        PlatformTarget.Vulkan,
        PlatformTarget.Fna,
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TechniqueInsideIfZero_IsNotCompiled(PlatformTarget target)
    {
        bool sm3 = target == PlatformTarget.Fna;
        CompiledShader shader = await CompileOk(IfZero(sm3), target, sm3);

        TechniqueNames(shader, target).ShouldBe(["B"]);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TechniqueBehindATechniqueDefine_FollowsTheDefine(PlatformTarget target)
    {
        bool sm3 = target == PlatformTarget.Fna;

        TechniqueNames(await CompileOk(IfTechnique(sm3), target, sm3, define: "TECHNIQUE"), target).ShouldBe(["A", "B"]);
        TechniqueNames(await CompileOk(IfTechnique(sm3), target, sm3), target).ShouldBe(["B"]);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TechniqueInAnInclude_IsCompiled(PlatformTarget target)
    {
        bool sm3 = target == PlatformTarget.Fna;

        CompiledShader only = await CompileOk(IncludeOnly, target, sm3);
        TechniqueNames(only, target).ShouldBe(["Inc"]);

        CompiledShader both = await CompileOk(IncludeWithOwn(sm3), target, sm3);
        TechniqueNames(both, target).ShouldBe(["Inc", "Own"]);
        both.Warnings.ShouldNotContain(w => w.Message.Contains("effects-syntax", StringComparison.Ordinal),
            "the included technique block is stripped from the compiler's input, so DXC never sees effect syntax");
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task Knifx_MacroCompileTarget_RecordsTheExpandedShaderModel(PlatformTarget target)
    {
        CompiledShader macro = await CompileOk(MacroCompileTarget, target, container: EffectContainer.Knifx);
        CompiledShader literal = await CompileOk(LiteralCompileTarget, target, container: EffectContainer.Knifx);

        // KNI's own pipeline records 4.0 (version byte 08) for both.
        KnifxShaderVersions(macro.Data).ShouldBe([(4, 0), (4, 0)]);
        KnifxShaderVersions(literal.Data).ShouldBe([(4, 0), (4, 0)]);
    }

    [Fact]
    public async Task Fna_MacroCompileTarget_InTheSm2To3Range_IsHonored()
    {
        const string source = """
            #define PS_SM ps_2_0
            float4 P(float4 c : COLOR0) : COLOR0 { return c; }
            technique T { pass { PixelShader = compile PS_SM P(); } }
            """;

        CompiledShader shader = await CompileOk(source, PlatformTarget.Fna);

        // fxc /T fx_2_0 compiles PS_SM as the ps_2_0 it expands to; it used to fall back to ps_3_0.
        Fx2BinaryValidator.Parse(shader.Data).Shaders.ShouldHaveSingleItem().VersionToken.ShouldBe(0xFFFF0200u);
    }

    [Fact]
    public async Task Fna_MonoGameHeaderSm4Arm_KeepsTheSm3Ceiling()
    {
        // MonoGame's standard header defines vs_4_0_level_9_1 in the arm FNA takes. It compiled
        // at the SM3 ceiling before issue #407 and still does: refusing it would break the header.
        const string source = """
            #if OPENGL
            #define PS_SHADERMODEL ps_3_0
            #else
            #define PS_SHADERMODEL ps_4_0_level_9_1
            #endif
            float4 P(float4 c : COLOR0) : COLOR0 { return c; }
            technique T { pass { PixelShader = compile PS_SHADERMODEL P(); } }
            """;

        CompiledShader shader = await CompileOk(source, PlatformTarget.Fna);

        Fx2BinaryValidator.Parse(shader.Data).Shaders.ShouldHaveSingleItem().VersionToken.ShouldBe(0xFFFF0300u);
    }

    /// <summary>
    /// The per-shader <c>ShaderVersion</c> of a KNIFX container's FIRST backend body (the desktop
    /// OpenGL body for a GL target, the only body for DirectX), in shader order. Only the shapes
    /// these tests compile are modelled: no samplers.
    /// </summary>
    private static List<(int Major, int Minor)> KnifxShaderVersions(byte[] data)
    {
        Encoding.ASCII.GetString(data, 0, 4).ShouldBe("KNIF");
        int bodyOffset = BitConverter.ToInt32(data, 10 + 2 + 4);
        int i = bodyOffset + 4 + 1; // body length prefix, integersAsFloats bool

        int cbuffers = Packed(data, ref i);
        for (int c = 0; c < cbuffers; c++)
        {
            SkipString(data, ref i);
            Packed(data, ref i);
            int parameters = Packed(data, ref i);
            for (int p = 0; p < parameters; p++)
            {
                Packed(data, ref i);
                i += 2;
            }
        }

        var versions = new List<(int, int)>();
        int shaders = Packed(data, ref i);
        for (int s = 0; s < shaders; s++)
        {
            i++; // stage
            int major = Packed(data, ref i);
            int minor = Packed(data, ref i);
            versions.Add((major, minor));
            i += 4 + BitConverter.ToInt32(data, i);
            Packed(data, ref i).ShouldBe(0, "no samplers in these shapes");
            int cbufferIndices = Packed(data, ref i);
            for (int c = 0; c < cbufferIndices; c++)
                Packed(data, ref i);
            int attributes = Packed(data, ref i);
            for (int a = 0; a < attributes; a++)
            {
                SkipString(data, ref i);
                i++;
                Packed(data, ref i);
                i += 2;
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

    private static void SkipString(byte[] data, ref int i)
    {
        int length = (int)Leb128(data, ref i);
        i += length;
    }
}
