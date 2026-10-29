#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// A global initializer (<c>float R = 1;</c>) is the parameter's default in EVERY MGFX and KNIFX
/// output (owner directive 2026-10-28; <c>project_decisions.md</c>). DXC and vkd3d drop the
/// initializer, and <c>mgfxc</c> itself stores zeros on OpenGL and loses arrays and half of a
/// <c>float2x2</c> on DirectX, so the value is read off the source and written into the blob
/// MonoGame/KNI read before any <c>Set</c> call. The real-engine proof is the
/// <c>validation/InitializerDefaults*</c> render drivers; these pin the bytes on every target.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InitializerDefaultsTests
{
    private static readonly byte[] OneF = [0x00, 0x00, 0x80, 0x3F];

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "initializer-defaults", name);

    private static async Task<CompiledShader> CompileAsync(
        string fixture, PlatformTarget target, EffectContainer container = EffectContainer.Mgfx,
        CancellationToken ct = default)
    {
        string path = Fixture(fixture);
        string source = await File.ReadAllTextAsync(path, ct);
        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = target, SourceFileName = path, Container = container },
            ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : $"{fixture} must compile for {target}");
        return result.Value;
    }

    private static MgfxParameterRecord Param(CompiledShader compiled, string name) =>
        MgfxBlobReader.Parse(compiled.Data).Parameters.Single(p => p.Name == name);

    private static float[] Floats(byte[] blob)
    {
        var f = new float[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, f, 0, blob.Length);
        return f;
    }

    private static int[] Ints(byte[] blob)
    {
        var i = new int[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, i, 0, blob.Length);
        return i;
    }

    // ---- the minimal shader, on every MGFX target ---------------------------------------

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task FloatRIsOne_IsTheDefaultBlob_OnEveryMgfxTarget(PlatformTarget target)
    {
        CompiledShader compiled = await CompileAsync("RedDefault.fx", target);

        Param(compiled, "R").DefaultValue.ShouldBe(OneF);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task FloatRIsOne_IsTheDefaultBlob_InKnifx(PlatformTarget target)
    {
        CompiledShader compiled = await CompileAsync("RedDefault.fx", target, EffectContainer.Knifx);

        // KNIFX packs counts, so find the record by its bytes: class 0, type 3, name "R", "" semantic.
        byte[] needle = [0, 3, 1, (byte)'R', 0, 0];
        int at = IndexOf(compiled.Data, needle);
        at.ShouldBeGreaterThanOrEqualTo(0, "the R parameter record must be in the KNIFX bytes");
        int blob = at + needle.Length + 3 + 2;   // rows, cols, columnsActual, elements, members
        compiled.Data[blob..(blob + 4)].ShouldBe(OneF);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    [Fact]
    public async Task OnlyTheDefaultBlobBytesChange_NothingElseInTheFile()
    {
        // Backwards compatibility: same source without the initializer differs from the initialized
        // build only in the 4 blob bytes (and the body-hash effect key in the header).
        string src = await File.ReadAllTextAsync(Fixture("RedDefault.fx"));
        string without = src.Replace("float R = 1;", "float R;");
        without.ShouldNotBe(src);

        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "RedDefault.fx" };
        byte[] with = (await new EffectCompiler().CompileAsync(src, options)).Value.Data;
        byte[] bare = (await new EffectCompiler().CompileAsync(without, options)).Value.Data;

        with.Length.ShouldBe(bare.Length);
        var differing = Enumerable.Range(0, with.Length).Where(i => with[i] != bare[i]).ToList();
        // Effect key (bytes 6..9) plus the blob's two non-zero bytes (0x80 0x3F; the zeros match),
        // which sit next to each other: nothing else in the file moved.
        var outsideKey = differing.Where(i => i is < 6 or > 9).ToList();
        outsideKey.Count.ShouldBe(2);
        outsideKey[1].ShouldBe(outsideKey[0] + 1);
        with[outsideKey[0]].ShouldBe((byte)0x80);
        with[outsideKey[1]].ShouldBe((byte)0x3F);
    }

    // ---- the A..F shapes ------------------------------------------------------------------

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task A_Vector_And_B_Scalar(PlatformTarget target)
    {
        Floats(Param(await CompileAsync("A_Tint.fx", target), "Tint").DefaultValue!)
            .ShouldBe(new[] { 1f, 0f, 0f, 1f });
        Floats(Param(await CompileAsync("B_Scale.fx", target), "Scale").DefaultValue!)
            .ShouldBe(new[] { 0.5f });
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task C_Vec2_And_FullMat2_And_D_IntBool(PlatformTarget target)
    {
        CompiledShader c = await CompileAsync("C_Vec2Mat2.fx", target);
        Floats(Param(c, "Offset").DefaultValue!).ShouldBe(new[] { 0.25f, 0.75f });
        // float2x2 0.5 * I, written column by column and COMPLETE (mgfxc keeps only the first half).
        Floats(Param(c, "M").DefaultValue!).ShouldBe(new[] { 0.5f, 0f, 0f, 0.5f });

        CompiledShader d = await CompileAsync("D_IntBool.fx", target);
        Ints(Param(d, "Mode").DefaultValue!).ShouldBe(new[] { 2 });
        Ints(Param(d, "Flag").DefaultValue!).ShouldBe(new[] { -1 });   // D3D TRUE, as mgfxc stores it
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task E_Array_EveryElementCarriesItsOwnDefault(PlatformTarget target)
    {
        MgfxParameterRecord colors = Param(await CompileAsync("E_Array.fx", target), "Colors");

        colors.DefaultValue.ShouldBeNull("an array parent carries no blob; its element leaves do");
        colors.Elements.Count.ShouldBe(2);
        Floats(colors.Elements[0].DefaultValue!).ShouldBe(new[] { 1f, 0f, 0f, 1f });
        Floats(colors.Elements[1].DefaultValue!).ShouldBe(new[] { 0f, 1f, 0f, 1f });
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task F_NoInitializer_StaysZero(PlatformTarget target)
    {
        Param(await CompileAsync("F_NoInit.fx", target), "Tint").DefaultValue.ShouldBe(new byte[16]);
    }

    [Fact]
    public async Task Knifx_CarriesTheSameBlobsAsMgfx_ForArraysToo()
    {
        CompiledShader mgfx = await CompileAsync("E_Array.fx", PlatformTarget.OpenGL);
        CompiledShader knifx = await CompileAsync("E_Array.fx", PlatformTarget.OpenGL, EffectContainer.Knifx);

        byte[] red = BitConverter.GetBytes(1f).Concat(new byte[4]).Concat(new byte[4]).Concat(BitConverter.GetBytes(1f)).ToArray();
        IndexOf(knifx.Data, red).ShouldBeGreaterThanOrEqualTo(0, "the red element blob must be in the KNIFX body");
        IndexOf(mgfx.Data, red).ShouldBeGreaterThanOrEqualTo(0, "the red element blob must be in the MGFX body");
    }

    // ---- the refusals next to this change are unchanged ------------------------------------

    [Fact]
    public async Task OpenGl_StillRefusesMat2_AndIntBool_WithSd0210()
    {
        foreach (string fixture in new[] { "C_Vec2Mat2.fx", "D_IntBool.fx" })
        {
            string path = Fixture(fixture);
            var result = await new EffectCompiler().CompileAsync(
                await File.ReadAllTextAsync(path),
                new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = path });
            result.IsFailure.ShouldBeTrue(fixture);
            result.Error.Any(e => e.Code == "SD0210").ShouldBeTrue(fixture);
        }
    }

    // ---- against the real mgfxc ------------------------------------------------------------

    private static MgfxParameterRecord Golden(string stem, string name)
    {
        byte[] bytes = File.ReadAllBytes(Fixture(Path.Combine("mgfxc-3.8.4.1-DirectX_11", stem + ".mgfx")));
        return MgfxBlobReader.Parse(bytes).Parameters.Single(p => p.Name == name);
    }

    [Theory]
    [InlineData("A_Tint", "Tint")]
    [InlineData("B_Scale", "Scale")]
    [InlineData("C_Vec2Mat2", "Offset")]
    [InlineData("D_IntBool", "Mode")]
    [InlineData("D_IntBool", "Flag")]
    public async Task DirectX11_ScalarVectorIntBool_DefaultsAreByteIdenticalToRealMgfxc(string stem, string name)
    {
        CompiledShader ours = await CompileAsync(stem + ".fx", PlatformTarget.DirectX);

        Param(ours, name).DefaultValue.ShouldBe(Golden(stem, name).DefaultValue);
        Golden(stem, name).DefaultValue!.Any(b => b != 0).ShouldBeTrue(
            "the mgfxc golden holds a non-zero default here, or this comparison proves nothing");
    }

    [Fact]
    public async Task DirectX11_WhereMgfxcLosesTheDefault_ShadowDuskKeepsIt()
    {
        // mgfxc DX11: the second half of a float2x2 and every float4[] element come out zero.
        Floats(Golden("C_Vec2Mat2", "M").DefaultValue!).ShouldBe(new[] { 0.5f, 0f, 0f, 0f });
        Golden("E_Array", "Colors").Elements.ShouldAllBe(e => e.DefaultValue!.All(b => b == 0));

        CompiledShader c = await CompileAsync("C_Vec2Mat2.fx", PlatformTarget.DirectX);
        Floats(Param(c, "M").DefaultValue!).ShouldBe(new[] { 0.5f, 0f, 0f, 0.5f });
    }

    // ---- mixed shapes, macros, folding, matrices, and what cannot be evaluated --------------

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task KitchenSink_EveryShapeIsEvaluated(PlatformTarget target)
    {
        CompiledShader compiled = await CompileAsync("KitchenSink.fx", target);

        Floats(Param(compiled, "Tint").DefaultValue!).ShouldBe(new[] { 0.25f, 0.5f, 0.75f, 1f });   // macro
        Floats(Param(compiled, "Gain").DefaultValue!).ShouldBe(new[] { 1.25f });                    // const + folding
        Floats(Param(compiled, "Axis").DefaultValue!).ShouldBe(new[] { -1f, 0f, 1f });             // unary minus
        // 1..16 row-major in HLSL, column by column in MonoGame's Data.
        Floats(Param(compiled, "World").DefaultValue!)
            .ShouldBe(new float[] { 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 4, 8, 12, 16 });
        Param(compiled, "NoDefault").DefaultValue.ShouldBe(new byte[16]);
    }

    [Fact]
    public async Task AnInitializerItCannotEvaluate_IsAnSd0017Warning_AndAZeroDefault()
    {
        CompiledShader compiled = await CompileAsync("Unmodelled.fx", PlatformTarget.OpenGL);

        Floats(Param(compiled, "Good").DefaultValue!).ShouldBe(new[] { 0.5f });
        Param(compiled, "Unmodelled").DefaultValue.ShouldBe(new byte[4]);

        ShaderError warning = compiled.Warnings.Single(w => w.Code == "SD0017");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("'Unmodelled'", Case.Sensitive);
        warning.Message.ShouldContain("Twice", Case.Sensitive);
        warning.File.ShouldEndWith("Unmodelled.fx", Case.Sensitive);
        warning.Line.ShouldBe(8);                  // the line of `float Unmodelled = Twice(0.25);`
        warning.Column.ShouldBe(7);
    }

    [Fact]
    public async Task TheMisleadingDxcInitializerIgnoredWarning_GoesAway_ForPreservedDefaults()
    {
        // DXC says `variable 'Good' will be placed in $Globals so initializer ignored` for each global.
        // It is untrue once the default is preserved; it stays for the one that was NOT preserved.
        CompiledShader compiled = await CompileAsync("Unmodelled.fx", PlatformTarget.OpenGL);

        var ignored = compiled.Warnings.Where(w => w.Message.Contains("initializer ignored", StringComparison.Ordinal)).ToList();
        ignored.ShouldAllBe(w => w.Line == 8, "only the declaration whose default was not preserved may keep DXC's warning");
        ignored.Count.ShouldBe(1);

        CompiledShader red = await CompileAsync("RedDefault.fx", PlatformTarget.OpenGL);
        red.Warnings.ShouldNotContain(w => w.Message.Contains("initializer ignored", StringComparison.Ordinal));
        red.Warnings.ShouldNotContain(w => w.Code == "SD0017");
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task DxcsInitializerIgnoredWarning_IsGone_OnEveryDxcPath(PlatformTarget target)
    {
        // The SPIR-V compiles word it "will be placed in $Globals so initializer ignored"; the DXIL
        // (DirectX 12) one "Initializer of external global will be ignored". Both are untrue once
        // the default is stored.
        foreach (string fixture in new[] { "A_Tint.fx", "E_Array.fx" })
        {
            CompiledShader compiled = await CompileAsync(fixture, target);
            compiled.Warnings.ShouldNotContain(
                w => w.Message.Contains("ignored", StringComparison.Ordinal) && w.Message.Contains("nitializer", StringComparison.Ordinal),
                $"{fixture} {target}");
        }
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task DxcsInitializerIgnoredWarning_IsGone_WhenTheBraceListStartsOnTheNextLine(PlatformTarget target)
    {
        // KNI's OptimizedMatrix.fx (validation/KniEffectTest*): `float4x4 ColorMatrix =` on one line and
        // the brace list on the next. DXC's SPIR-V warning points at the `{` line, not the declaration,
        // so a line match alone kept the now-untrue warning although the default was preserved.
        const string src = """
            #if defined(OPENGL)
            #define SV_POSITION POSITION
            #define PS_SHADERMODEL ps_3_0
            #else
            #define PS_SHADERMODEL ps_6_0
            #endif
            float4x4 ColorMatrix =
            {
                11, 21, 31, 41,
                12, 22, 32, 42,
                13, 23, 33, 43,
                14, 24, 34, 44
            };
            float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
            {
                return float4(color.r * ColorMatrix._11, color.g * ColorMatrix._12, 0, 1);
            }
            technique SpriteDrawing { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } };
            """;
        var result = await new EffectCompiler().CompileAsync(
            src, new CompilerOptions { Target = target, SourceFileName = "NextLineBrace.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.Message)) : "");
        CompiledShader compiled = result.Value;

        Floats(Param(compiled, "ColorMatrix").DefaultValue!)[0].ShouldBe(11f);
        compiled.Warnings.ShouldNotContain(
            w => w.Message.Contains("ignored", StringComparison.Ordinal) && w.Message.Contains("nitializer", StringComparison.Ordinal),
            $"{target}: the preserved default must not keep DXC's warning");
    }

    [Fact]
    public async Task AShaderWithNoInitializers_AddsNothing()
    {
        // F_NoInit has no initializer: no warnings added, blob zeros. (That its bytes are
        // unchanged is enforced by the byte-identity manifests, which cover fixtures without
        // initializers.)
        CompiledShader compiled = await CompileAsync("F_NoInit.fx", PlatformTarget.OpenGL);
        compiled.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        Param(compiled, "Tint").DefaultValue.ShouldBe(new byte[16]);
    }

    [Fact]
    public async Task AConditionalOnACompilerPredefinedMacro_IsAnSd0017Warning()
    {
        const string src = """
            #define PS ps_3_0
            #if __HLSL_VERSION >= 2018
            float R = 1;
            #else
            float R = 2;
            #endif
            float4 main() : COLOR0 { return float4(R, 0, 0, 1); }
            technique T { pass P { PixelShader = compile PS main(); } }
            """;
        var result = await new EffectCompiler().CompileAsync(
            src, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Predefined.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        ShaderError warning = result.Value.Warnings.Single(w => w.Code == "SD0017");
        warning.Message.ShouldContain("__HLSL_VERSION", Case.Sensitive);
    }

    [Fact]
    public async Task InitializersInsideAnInactiveConditionalBranch_AreNotRead()
    {
        const string src = """
            #define PS ps_4_0_level_9_1
            #if 0
            float R = 7;
            #else
            float R = 3;
            #endif
            float4 main() : COLOR0 { return float4(R, 0, 0, 1); }
            technique T { pass P { PixelShader = compile PS main(); } }
            """;
        var result = await new EffectCompiler().CompileAsync(
            src, new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "Branch.fx" });
        result.IsSuccess.ShouldBeTrue();
        Floats(Param(result.Value, "R").DefaultValue!).ShouldBe(new[] { 3f });
    }

    [Fact]
    public async Task UserDefines_FlowIntoTheInitializer()
    {
        const string src = """
            #define PS ps_4_0_level_9_1
            float R = START;
            float4 main() : COLOR0 { return float4(R, 0, 0, 1); }
            technique T { pass P { PixelShader = compile PS main(); } }
            """;
        var result = await new EffectCompiler().CompileAsync(
            src,
            new CompilerOptions
            {
                Target = PlatformTarget.DirectX,
                SourceFileName = "Define.fx",
                Defines = [new ShadowDusk.Core.Preprocessor.UserDefine("START", "0.75")],
            });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");
        Floats(Param(result.Value, "R").DefaultValue!).ShouldBe(new[] { 0.75f });
    }
}
