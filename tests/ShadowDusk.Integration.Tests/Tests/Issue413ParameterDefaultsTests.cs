#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #413: <see cref="CompiledShader.Parameters"/> carries each parameter's declared default
/// (<c>float Scale = 2.0;</c>) on every effect-producing target. On the MGFX and KNIFX targets it
/// is the very default blob issue #422 writes into the file (pinned here against the bytes), on
/// FNA the compiled constant table's value. Measured (2026-10-28, macOS arm64): DXC's DXIL
/// reflection returns a null default and DXC drops the initializer from the SPIR-V, so the value
/// can only come from the source; vkd3d keeps it in the DXBC table (DirectX) and in the CTAB (FNA).
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue413ParameterDefaultsTests
{
    // Declared on every target. The order is the one SPIRV-Cross can lay out for OpenGL (Dir before
    // Scale so they share a register, the array last); other orders fail there with SD0100/SD0210.
    private const string Common = """
        float3 Dir = float3(1.25, 2.25, 3.25);
        float Scale = 2.5;
        float4 Tint = float4(1.5, 0.5, 0.25, 0.125);
        float4x4 World = float4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
        float Computed = 1.0 / 4.0;
        float NoInit;
        float Weights[3] = { 0.75, 1.75, 2.75 };
        """;

    private const string CommonUse = "Tint * Scale + float4(Dir, 0) + World[1][0] + Weights[1] + Computed + NoInit";

    // Not every target takes these: OpenGL models neither int/bool uniforms (SD0210) nor a matrix
    // other than float4x4 (SD0210), and the fx_2_0 writer refuses a non-square matrix.
    private const string IntBool = """
        int Count = 3;
        bool Flag = true;
        """;
    private const string IntBoolUse = "* Count * (Flag ? 1 : 0)";
    private const string Square = "float2x2 Rot = float2x2(1.5, 2.5, 3.5, 4.5);";
    private const string SquareUse = "+ Rot[1][0]";
    private const string Wide = "float2x3 Wide = float2x3(1, 2, 3, 4, 5, 6);";
    private const string WideUse = "+ Wide[1][2]";

    private enum Shape { OpenGl, Modern, Fna }

    private static string Source(Shape shape, bool withInitializers = true)
    {
        string declarations = Common;
        string use = CommonUse;
        if (shape != Shape.OpenGl)
        {
            declarations += "\n" + IntBool + "\n" + Square;
            use = "(" + use + ")" + IntBoolUse + " " + SquareUse;
        }
        if (shape == Shape.Modern)
        {
            declarations += "\n" + Wide;
            use += " " + WideUse;
        }

        string text = shape == Shape.Fna
            ? declarations + $"\nfloat4 MainPS() : COLOR0 {{ return {use}; }}\n" +
              "technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }\n"
            : "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#elif SM4\n#define PS_SHADERMODEL ps_4_0\n#else\n" +
              "#define PS_SHADERMODEL ps_3_0\n#endif\n" + declarations +
              $"\nfloat4 MainPS(float4 pos : SV_Position, float4 color : COLOR0) : SV_Target0 {{ return {use} + color; }}\n" +
              "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";
        return withInitializers ? text : StripInitializers(text);
    }

    /// <summary>The same shader with the ` = value` of every numeric declaration removed.</summary>
    private static string StripInitializers(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
            (line.StartsWith("float", StringComparison.Ordinal)
                || line.StartsWith("int ", StringComparison.Ordinal)
                || line.StartsWith("bool ", StringComparison.Ordinal))
            && line.Contains(" = ", StringComparison.Ordinal) && line.TrimEnd().EndsWith(';')
                ? line[..line.IndexOf(" = ", StringComparison.Ordinal)] + ";"
                : line));

    private static async Task<CompiledShader> CompileAsync(
        string source, PlatformTarget target, EffectContainer container = EffectContainer.Mgfx)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "Defaults.fx",
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
            : "must compile");
        return result.Value;
    }

    private static ParameterReflection Find(CompiledShader shader, string name) =>
        shader.Parameters.Single(p => p.Name == name);

    private static readonly float[] Identity16 = Enumerable.Range(1, 16).Select(i => (float)i).ToArray();

    private static void AssertCommonDefaults(CompiledShader shader)
    {
        Find(shader, "Dir").DefaultValue.ShouldBe(new[] { 1.25f, 2.25f, 3.25f });
        Find(shader, "Scale").DefaultValue.ShouldBe(new[] { 2.5f });
        Find(shader, "Tint").DefaultValue.ShouldBe(new[] { 1.5f, 0.5f, 0.25f, 0.125f });
        // Logical row-major: row 0 is 1 2 3 4, whatever the packing in the file.
        Find(shader, "World").DefaultValue.ShouldBe(Identity16);
        Find(shader, "Weights").DefaultValue.ShouldBe(new[] { 0.75f, 1.75f, 2.75f });

        // Constant folding is the initializer reader's (#422), so the folded value is known.
        Find(shader, "Computed").DefaultValue.ShouldBe(new[] { 0.25f });

        // No initializer: unknown, never zero.
        Find(shader, "NoInit").DefaultValue.ShouldBeNull();
    }

    private static void AssertIntBoolAndSquare(CompiledShader shader, PlatformTarget target)
    {
        Find(shader, "Count").DefaultValue.ShouldBe(new[] { 3f });
        // SPIR-V has no bool in a uniform block, so Vulkan reports Flag as an Int32 and its true
        // (0xFFFFFFFF) reads as that int, -1; every other target reports the bool.
        Find(shader, "Flag").DefaultValue.ShouldBe(new[] { target == PlatformTarget.Vulkan ? -1f : 1f });
        Find(shader, "Rot").DefaultValue.ShouldBe(new[] { 1.5f, 2.5f, 3.5f, 4.5f });
    }

    [Fact]
    public async Task OpenGl_ReportsDefaultsFromTheSource()
    {
        CompiledShader shader = await CompileAsync(Source(Shape.OpenGl), PlatformTarget.OpenGL);

        AssertCommonDefaults(shader);
    }

    [Fact]
    public async Task Knifx_ReportsTheSameDefaults()
    {
        CompiledShader shader = await CompileAsync(
            Source(Shape.OpenGl), PlatformTarget.OpenGL, EffectContainer.Knifx);

        AssertCommonDefaults(shader);
    }

    [Theory]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task DxilAndSpirvTargets_ReportDefaultsFromTheSource(PlatformTarget target)
    {
        CompiledShader shader = await CompileAsync(Source(Shape.Modern), target);

        AssertCommonDefaults(shader);
        AssertIntBoolAndSquare(shader, target);
        if (target == PlatformTarget.Vulkan)
        {
            // Vulkan reflects a non-square matrix transposed (#422): no default is preserved
            // (SD0017, a zero blob) and so none is reported.
            Find(shader, "Wide").DefaultValue.ShouldBeNull();
            shader.Warnings.ShouldContain(w => w.Code == "SD0017" && w.Message.Contains("'Wide'", StringComparison.Ordinal));
        }
        else
        {
            Find(shader, "Wide").DefaultValue.ShouldBe(new[] { 1f, 2f, 3f, 4f, 5f, 6f });
        }
    }

    [FnaFact]
    public async Task DirectX11_ReportsDefaultsFromTheSource()
    {
        CompiledShader shader = await CompileAsync(Source(Shape.Modern), PlatformTarget.DirectX);

        AssertCommonDefaults(shader);
        AssertIntBoolAndSquare(shader, PlatformTarget.DirectX);
        Find(shader, "Wide").DefaultValue.ShouldBe(new[] { 1f, 2f, 3f, 4f, 5f, 6f });
    }

    // ---- the reflection default IS the blob #422 writes ------------------------------------

    private static float[] Logical(MgfxParameterRecord record)
    {
        IReadOnlyList<MgfxParameterRecord> leaves = record.Elements.Count > 0 ? record.Elements : [record];
        var values = new List<float>();
        foreach (MgfxParameterRecord leaf in leaves)
        {
            byte[] blob = leaf.DefaultValue!;
            for (int r = 0; r < leaf.Rows; r++)
            {
                for (int c = 0; c < leaf.Columns; c++)
                {
                    // A matrix blob is column by column; a vector or scalar is its components.
                    int index = leaf.Class == (byte)EffectParameterClass.Matrix ? c * leaf.Rows + r : r * leaf.Columns + c;
                    values.Add(leaf.Type == (byte)EffectParameterType.Single
                        ? BitConverter.ToSingle(blob, index * 4)
                        : leaf.Type == (byte)EffectParameterType.Bool
                            ? (BitConverter.ToInt32(blob, index * 4) != 0 ? 1f : 0f)
                            : BitConverter.ToInt32(blob, index * 4));
                }
            }
        }
        return values.ToArray();
    }

    private static void AssertReflectionEqualsWrittenBlobs(CompiledShader shader)
    {
        MgfxBlobReader file = MgfxBlobReader.Parse(shader.Data);
        shader.Parameters.Select(p => p.Name).ShouldBe(file.Parameters.Select(p => p.Name));

        int withDefault = 0;
        foreach (MgfxParameterRecord record in file.Parameters.Where(r => r.Class <= (byte)EffectParameterClass.Matrix))
        {
            ParameterReflection reflected = Find(shader, record.Name);
            IReadOnlyList<MgfxParameterRecord> leaves = record.Elements.Count > 0 ? record.Elements : [record];
            if (leaves.All(l => l.DefaultValue!.Any(b => b != 0)))
            {
                reflected.DefaultValue.ShouldBe(Logical(record), $"'{record.Name}': reflection vs the blob in the file");
                withDefault++;
            }
            else if (reflected.DefaultValue is null)
            {
                // No initializer: the file carries zeros and the reflection says "unknown".
                leaves.All(l => l.DefaultValue!.All(b => b == 0)).ShouldBeTrue(record.Name);
            }
        }
        withDefault.ShouldBeGreaterThan(4);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task Mgfx_ReflectionDefaultEqualsTheBlobInTheFile(PlatformTarget target)
    {
        Shape shape = target == PlatformTarget.OpenGL ? Shape.OpenGl : Shape.Modern;
        AssertReflectionEqualsWrittenBlobs(await CompileAsync(Source(shape), target));
    }

    [FnaFact]
    public async Task DirectX11_ReflectionDefaultEqualsTheBlobInTheFile()
    {
        AssertReflectionEqualsWrittenBlobs(await CompileAsync(Source(Shape.Modern), PlatformTarget.DirectX));
    }

    [Theory]
    [InlineData("Scale", new byte[] { 0, 3 }, new[] { 2.5f })]
    [InlineData("Tint", new byte[] { 1, 3 }, new[] { 1.5f, 0.5f, 0.25f, 0.125f })]
    public async Task Knifx_ReflectionDefaultEqualsTheBlobInTheFile(string name, byte[] classAndType, float[] expected)
    {
        CompiledShader shader = await CompileAsync(
            Source(Shape.OpenGl), PlatformTarget.OpenGL, EffectContainer.Knifx);

        Find(shader, name).DefaultValue.ShouldBe(expected);

        // KNIFX packs counts, so find the record by its bytes: class, type, name, "" semantic,
        // then rows, columns, columnsActual, elements, members, and the blob.
        byte[] needle = [.. classAndType, (byte)name.Length, .. System.Text.Encoding.ASCII.GetBytes(name), 0, 0];
        int at = IndexOf(shader.Data, needle);
        at.ShouldBeGreaterThanOrEqualTo(0, $"the {name} parameter record must be in the KNIFX bytes");
        int blob = at + needle.Length + 3 + 2;
        float[] written = new float[expected.Length];
        Buffer.BlockCopy(shader.Data, blob, written, 0, expected.Length * 4);
        Find(shader, name).DefaultValue.ShouldBe(written);
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

    [FnaFact]
    public async Task Fna_ReportsDefaults_EvenForConstantsTheShaderReadsPartly()
    {
        CompiledShader shader = await CompileAsync(Source(Shape.Fna), PlatformTarget.Fna);

        AssertCommonDefaults(shader);
        AssertIntBoolAndSquare(shader, PlatformTarget.Fna);
    }
}
