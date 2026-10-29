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

    [Fact]
    public async Task C_Vec2Mat2_VulkanBufferHoldsTheWholeFloat2x2()
    {
        // The float2x2 at offset 16 spans 24 bytes (two registers, the second half-used), so the
        // buffer is 48 bytes. ShadowDusk's Vulkan reflection used to say 32 (the packed 16-byte matrix
        // size), and MonoGame DesktopVK's upload of the second register then ran past the end and threw
        // at the first draw. DirectX 12 (DXIL) has always said 48, and so does real mgfxc 3.8.5's Vulkan
        // build of the same globals (measured, validation/SharedNonSquareMatrix G_V2M2).
        MgfxBlobReader vulkan = MgfxBlobReader.Parse((await CompileAsync("C_Vec2Mat2.fx", PlatformTarget.Vulkan)).Data);
        MgfxBlobReader dx12 = MgfxBlobReader.Parse((await CompileAsync("C_Vec2Mat2.fx", PlatformTarget.DirectX12)).Data);

        vulkan.ConstantBuffers.Single().Size.ShouldBe(48);
        vulkan.ConstantBuffers.Single().ParameterOffsets.ShouldBe(new[] { 0, 16 });
        dx12.ConstantBuffers.Single().Size.ShouldBe(48);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task G_NonSquare_HeaderIsTheHlslShape_AndTheBlobIsColumnByColumn(PlatformTarget target)
    {
        // Issue #423: Vulkan reflected these transposed (3x2 for a float2x3), so the default was
        // dropped with SD0017. MonoGame uploads ColumnCount registers of RowCount floats from
        // Data[c * Rows + r], so the header must be the HLSL shape for the blob to land right.
        CompiledShader g = await CompileAsync("G_NonSquare.fx", target);
        g.Warnings.ShouldNotContain(w => w.Code == "SD0017");

        MgfxParameterRecord wide = Param(g, "Wide");
        (wide.Rows, wide.Columns).ShouldBe(((byte)2, (byte)3));
        Floats(wide.DefaultValue!).ShouldBe(new[] { 0.125f, 0.375f, 0.875f, 0.5f, 0.75f, 0.625f });

        MgfxParameterRecord tall = Param(g, "Tall");
        (tall.Rows, tall.Columns).ShouldBe(((byte)3, (byte)2));
        Floats(tall.DefaultValue!).ShouldBe(new[] { 0.1f, 0.3f, 0.25f, 0.2f, 0.4f, 0.6f });
    }

    [Fact]
    public async Task RowMajorNonSquare_OnVulkan_KeepsMgfxcsHeader_FitsItsBuffer_AndKeepsItsDefault()
    {
        // Probe case I (issue #423): a row_major float2x3 LAST in the globals. MonoGame uploads
        // ColumnCount registers of RowCount floats; the row_major buffer holds one register per HLSL
        // row, so the header is 3x2 (what real mgfxc 3.8.5's Vulkan build writes), the upload is the
        // buffer exactly, and the buffer (48 bytes) holds it. With the HLSL-shape 2x3 header the
        // runtime wrote 3 registers from offset 16, past a 48-byte buffer, and threw at the first Apply.
        const string src = """
            float4 Pad;
            row_major float2x3 M = float2x3(1, 2, 3, 4, 5, 6);
            float4 MainPS(float4 pos : SV_Position) : SV_Target0 { return Pad + M[1].z; }
            technique T { pass P { PixelShader = compile ps_6_0 MainPS(); } }
            """;
        var result = await new EffectCompiler().CompileAsync(
            src, new CompilerOptions { Target = PlatformTarget.Vulkan, SourceFileName = "RowMajor.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        MgfxBlobReader file = MgfxBlobReader.Parse(result.Value.Data);
        MgfxParameterRecord m = file.Parameters.Single(p => p.Name == "M");
        (m.Rows, m.Columns).ShouldBe(((byte)3, (byte)2));
        MgfxConstantBufferRecord cb = file.ConstantBuffers.Single();
        cb.ParameterOffsets.ShouldBe(new[] { 0, 16 });
        cb.Size.ShouldBe(48);
        cb.Size.ShouldBeGreaterThanOrEqualTo(16 + (m.Columns - 1) * 16 + m.Rows * 4);

        // The 3x2 header uploads two registers of three floats from Data, which is the two rows in
        // order, so the default is the rows in order, and the reflection reports the declaration.
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        Floats(m.DefaultValue!).ShouldBe(new[] { 1f, 2f, 3f, 4f, 5f, 6f });
        result.Value.Parameters.Single(p => p.Name == "M").DefaultValue.ShouldBe(new[] { 1f, 2f, 3f, 4f, 5f, 6f });
    }

    // ---- row_major matrix defaults (issue #423 follow-up) ----------------------------------

    private static readonly float[] ColumnByColumnM01 = [0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly float[] RowsInOrderM01 = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Theory]
    [InlineData("H_RowMajorMatrix.fx", PlatformTarget.DirectX, EffectContainer.Mgfx)]
    [InlineData("H_RowMajorMatrix.fx", PlatformTarget.DirectX12, EffectContainer.Mgfx)]
    [InlineData("H_RowMajorMatrix.fx", PlatformTarget.Vulkan, EffectContainer.Mgfx)]
    [InlineData("H_RowMajorMatrix.fx", PlatformTarget.DirectX, EffectContainer.Knifx)]
    [InlineData("I_PackMatrixPragma.fx", PlatformTarget.DirectX, EffectContainer.Mgfx)]
    [InlineData("I_PackMatrixPragma.fx", PlatformTarget.DirectX12, EffectContainer.Mgfx)]
    [InlineData("I_PackMatrixPragma.fx", PlatformTarget.Vulkan, EffectContainer.Mgfx)]
    public async Task RowMajorMatrixDefault_IsTheRowsInOrder(string fixture, PlatformTarget target, EffectContainer container)
    {
        // A row_major buffer holds one register per row and MonoGame copies Data into the registers
        // in order, so the default must be M's rows in order. It used to be the column-by-column blob
        // of a column-major matrix, which renders transposed in the real engine, with no warning.
        CompiledShader c = await CompileAsync(fixture, target, container);
        c.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        c.Parameters.Single(p => p.Name == "M").DefaultValue.ShouldBe(RowsInOrderM01, "reflection is the declaration");
        if (container == EffectContainer.Mgfx)
            Floats(Param(c, "M").DefaultValue!).ShouldBe(RowsInOrderM01);
        else
            IndexOf(c.Data, RowsInOrderM01.SelectMany(BitConverter.GetBytes).ToArray()).ShouldBeGreaterThanOrEqualTo(0);

        // The column-major twin is unchanged.
        Floats(Param(await CompileAsync("MatrixDefault.fx", target), "M").DefaultValue!).ShouldBe(ColumnByColumnM01);
    }

    [Theory]
    [InlineData("H_RowMajorMatrix.fx")]
    [InlineData("I_PackMatrixPragma.fx")]
    public async Task RowMajorMatrixDefault_OnOpenGl_IsUnchanged(string fixture)
    {
        // Every OpenGL compile is row-major already (-Zpr) and ShadowDusk's GL rewriter rebuilds a
        // float4x4 from the column-by-column blob (render-proven by MatrixDefault.fx), so row_major
        // changes nothing there.
        CompiledShader c = await CompileAsync(fixture, PlatformTarget.OpenGL);
        Floats(Param(c, "M").DefaultValue!).ShouldBe(ColumnByColumnM01);
        c.Parameters.Single(p => p.Name == "M").DefaultValue.ShouldBe(RowsInOrderM01, "reflection is the declaration");
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task RowMajorFloat2x2Default_IsTheRowsInOrder(PlatformTarget target)
    {
        CompiledShader c = await CompileAsync("J_RowMajor2x2.fx", target);
        c.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        Floats(Param(c, "M").DefaultValue!).ShouldBe(new[] { 0f, 1f, 0f, 0f });
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task TallRowMajorDefault_OnDirectX_FillsTheRowsTheUploadReaches(PlatformTarget target)
    {
        // DirectX reflects a row_major float4x3 as 4x3: MonoGame uploads 3 registers of 4 floats,
        // register j from Data[4j .. 4j+3], over a buffer of 4 one-per-row registers. So rows 0..2 go
        // in register order with a zero pad, and the zero row 3 needs nothing (an affine matrix).
        CompiledShader l = await CompileAsync("L_RowMajorTallAsymmetric.fx", target);
        l.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        Floats(Param(l, "M").DefaultValue!).ShouldBe(new[] { 0f, 1f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, 0f, 1f, 0f });
        l.Parameters.Single(p => p.Name == "M").DefaultValue
            .ShouldBe(new[] { 0f, 1f, 0f, 0.5f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }, "reflection is the declaration");

        CompiledShader k = await CompileAsync("K_RowMajorTallSymmetric.fx", target);
        Floats(Param(k, "M").DefaultValue!).ShouldBe(new[] { 0.25f, 0f, 0f, 0f, 0f, 0.5f, 0f, 0f, 0f, 0f, 0.75f, 0f });

        // A non-zero row 3 is never written there: SD0017, naming the row.
        CompiledShader m = await CompileAsync("M_RowMajorTallFourthRow.fx", target);
        ShaderError w = m.Warnings.Single(x => x.Code == "SD0017");
        w.Message.ShouldContain("row 3", Case.Sensitive);
        w.Message.ShouldContain("A Set cannot fill those registers either", Case.Sensitive);
        w.Message.ShouldNotContain("Set the parameter by name", Case.Sensitive);
        Param(m, "M").DefaultValue.ShouldBe(new byte[48]);
    }

    [Fact]
    public async Task TallRowMajorDefault_OnVulkan_IsEveryRowInOrder()
    {
        // Vulkan's 3x4 header uploads 4 registers of 3 floats: every row, the non-zero fourth included.
        CompiledShader m = await CompileAsync("M_RowMajorTallFourthRow.fx", PlatformTarget.Vulkan);
        m.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        Floats(Param(m, "M").DefaultValue!).ShouldBe(new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0.25f, 0.5f, 0.75f });
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX, "float2x3", "float2x3(1, 2, 3, 4, 5, 6)")]
    [InlineData(PlatformTarget.DirectX12, "float3x4", "float3x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)")]
    public async Task WideRowMajorDefault_OnDirectX_IsALoudSd0017(PlatformTarget target, string type, string value)
    {
        // A WIDE row_major matrix (more columns than rows) is uploaded as C registers over a matrix of R:
        // no default lands right and the upload runs past it, so it is refused, even all of it in range.
        string src = $$"""
            #if SM6
            #define PS ps_6_0
            #else
            #define PS ps_4_0
            #endif
            float4 Pad;
            row_major {{type}} M = {{value}};
            float4 MainPS(float4 pos : SV_Position) : SV_Target0 { return Pad + M[1].x; }
            technique T { pass P { PixelShader = compile PS MainPS(); } }
            """;
        var result = await new EffectCompiler().CompileAsync(
            src, new CompilerOptions { Target = target, SourceFileName = "Wide.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");
        ShaderError w = result.Value.Warnings.Single(x => x.Code == "SD0017");
        w.Message.ShouldContain("past the matrix", Case.Sensitive);
        w.Message.ShouldNotContain("Set the parameter by name", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task RowMajorNonSquareDefault_OnDirectX_IsALoudSd0017(PlatformTarget target)
    {
        // DirectX reflects a row_major float4x3 as 4x3, which MonoGame uploads as 3 registers of 4
        // floats over a buffer of 4 registers of 3: no blob lands right (a Set does not either), so the
        // default is refused loudly instead of being written scrambled.
        const string src = """
            #if SM6
            #define PS ps_6_0
            #else
            #define PS ps_4_0
            #endif
            row_major float4x3 M = float4x3(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            float4 MainPS(float4 pos : SV_Position) : SV_Target0 { return M[3].xyzz; }
            technique T { pass P { PixelShader = compile PS MainPS(); } }
            """;
        var result = await new EffectCompiler().CompileAsync(
            src, new CompilerOptions { Target = target, SourceFileName = "RowMajor43.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : "");

        ShaderError w = result.Value.Warnings.Single(x => x.Code == "SD0017");
        w.Message.ShouldContain("row_major 4x3", Case.Sensitive);
        Param(result.Value, "M").DefaultValue.ShouldBe(new byte[48]);
        result.Value.Parameters.Single(p => p.Name == "M").DefaultValue.ShouldBeNull();
    }

    [Fact]
    public async Task G_NonSquare_VulkanParameterTableEqualsDirectX12s()
    {
        // The SPIR-V reflection and the DXIL reflection describe one HLSL declaration, so the
        // parameter records (shape and default) must be the same bytes.
        MgfxBlobReader vulkan = MgfxBlobReader.Parse((await CompileAsync("G_NonSquare.fx", PlatformTarget.Vulkan)).Data);
        MgfxBlobReader dx12 = MgfxBlobReader.Parse((await CompileAsync("G_NonSquare.fx", PlatformTarget.DirectX12)).Data);

        vulkan.Parameters.Count.ShouldBe(dx12.Parameters.Count);
        for (int i = 0; i < vulkan.Parameters.Count; i++)
        {
            MgfxParameterRecord v = vulkan.Parameters[i];
            MgfxParameterRecord d = dx12.Parameters[i];
            (v.Name, v.Class, v.Type, v.Rows, v.Columns).ShouldBe((d.Name, d.Class, d.Type, d.Rows, d.Columns));
            v.DefaultValue.ShouldBe(d.DefaultValue, v.Name);
        }
        vulkan.ConstantBuffers.Single().ParameterOffsets.ShouldBe(dx12.ConstantBuffers.Single().ParameterOffsets);
        vulkan.ConstantBuffers.Single().Size.ShouldBe(dx12.ConstantBuffers.Single().Size);
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
        foreach (string fixture in new[] { "C_Vec2Mat2.fx", "D_IntBool.fx", "G_NonSquare.fx" })
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
