#nullable enable

using Shouldly;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Fx2;

/// <summary>
/// <see cref="CtabConstant.LogicalDefaultValue"/> turns a CTAB default (a register image)
/// into the dense declaration-order blob fxc writes into an fx_2_0 effect parameter. Every
/// register image below is the one <c>D3DCompile fx_2_0</c> emitted for the stated HLSL
/// initializer (probe 2026-10-28), and every expected array is the effect-parameter value
/// blob fxc wrote for the same source: the mapping is measured, not derived.
/// </summary>
public sealed class CtabLogicalDefaultValueTests
{
    private static CtabConstant Constant(
        int @class, int rows, int columns, int elements, int registerCount, float[]? image, int type = 3) =>
        new("X", CtabRegisterSet.Float4, 0, registerCount, @class, type, rows, columns, elements, image);

    // float2x2 M = {1,2,3,4}: MATRIX_COLUMNS, one register per column, rows in the lanes.
    [Fact]
    public void Float2x2_ColumnMajorImage_BecomesRowMajorBlob() =>
        Constant(3, 2, 2, 1, 2, [1, 3, 0, 0, 2, 4, 0, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4 });

    [Fact]
    public void Float3x3_ColumnMajorImage_BecomesRowMajorBlob() =>
        Constant(3, 3, 3, 1, 3, [1, 4, 7, 0, 2, 5, 8, 0, 3, 6, 9, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

    [Fact]
    public void Float4x4_ColumnMajorImage_BecomesRowMajorBlob() =>
        Constant(3, 4, 4, 1, 4, [1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 4, 8, 12, 16]).LogicalDefaultValue()
            .ShouldBe(Enumerable.Range(1, 16).Select(i => (float)i).ToArray());

    // Non-square: float2x3 (2 rows, 3 columns) has 3 column registers of 2 rows each.
    [Fact]
    public void Float2x3_ColumnMajorImage_BecomesRowMajorBlob() =>
        Constant(3, 2, 3, 1, 3, [1, 4, 0, 0, 2, 5, 0, 0, 3, 6, 0, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4, 5, 6 });

    // float3x2 (3 rows, 2 columns): 2 column registers of 3 rows each.
    [Fact]
    public void Float3x2_ColumnMajorImage_BecomesRowMajorBlob() =>
        Constant(3, 3, 2, 1, 2, [1, 3, 5, 0, 2, 4, 6, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4, 5, 6 });

    // row_major float4x4 is MATRIX_ROWS: one register per row, already declaration order.
    [Fact]
    public void RowMajorFloat4x4_RowRegisters_AreAlreadyDeclarationOrder() =>
        Constant(2, 4, 4, 1, 4, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]).LogicalDefaultValue()
            .ShouldBe(Enumerable.Range(1, 16).Select(i => (float)i).ToArray());

    // float4 Colors[2]: one register per element.
    [Fact]
    public void Float4Array_OneRegisterPerElement() =>
        Constant(1, 1, 4, 2, 2, [1, 0, 0, 1, 0, 1, 0, 1]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 0, 0, 1, 0, 1, 0, 1 });

    // float2 A[3]: the two used lanes of each register, padding dropped.
    [Fact]
    public void Float2Array_DropsRegisterPadding() =>
        Constant(1, 1, 2, 3, 3, [1, 2, 0, 0, 3, 4, 0, 0, 5, 6, 0, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4, 5, 6 });

    // float A[4]: a scalar array burns a whole register per element.
    [Fact]
    public void ScalarArray_TakesLaneZeroOfEachRegister() =>
        Constant(0, 1, 1, 4, 4, [1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4 });

    // float2x2 A[2]: each element is its own two column registers.
    [Fact]
    public void Float2x2Array_EachElementKeepsItsOwnRegisters() =>
        Constant(3, 2, 2, 2, 4, [1, 3, 0, 0, 2, 4, 0, 0, 5, 7, 0, 0, 6, 8, 0, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 });

    [Fact]
    public void Float4x4Array_EachElementKeepsItsOwnRegisters()
    {
        float[] image =
        [
            1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 4, 8, 12, 16,
            17, 21, 25, 29, 18, 22, 26, 30, 19, 23, 27, 31, 20, 24, 28, 32,
        ];

        Constant(3, 4, 4, 2, 8, image).LogicalDefaultValue()
            .ShouldBe(Enumerable.Range(1, 32).Select(i => (float)i).ToArray());
    }

    // The CTAB trims the image to the registers the shader reads, always a prefix: the
    // unreachable tail has no data and stays 0 (fxc keeps the declared value there, but no
    // shader can observe it).
    [Fact]
    public void TrimmedMatrixImage_ZeroFillsTheUnreadColumns() =>
        Constant(3, 4, 4, 1, 2, [1, 5, 9, 13, 2, 6, 10, 14]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 0, 0, 5, 6, 0, 0, 9, 10, 0, 0, 13, 14, 0, 0 });

    [Fact]
    public void TrimmedArrayImage_ZeroFillsTheUnreadElements() =>
        Constant(0, 1, 1, 4, 3, [1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0]).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 2, 3, 0 });

    [Fact]
    public void NoDefault_ReturnsNull() =>
        Constant(3, 4, 4, 1, 4, null).LogicalDefaultValue().ShouldBeNull();

    [Theory]
    [InlineData(4)]  // OBJECT
    [InlineData(5)]  // STRUCT
    public void NonNumericClass_ReturnsNull(int @class) =>
        Constant(@class, 1, 1, 1, 1, [1, 0, 0, 0]).LogicalDefaultValue().ShouldBeNull();

    [Fact]
    public void ImplausiblyLargeShape_ReturnsNullInsteadOfAllocating() =>
        Constant(3, 4, 4, ushort.MaxValue, 4, new float[16]).LogicalDefaultValue().ShouldBeNull();

    [Fact]
    public void IntAndBoolImages_AreCarriedAsTheFloatsTheCtabHolds() =>
        Constant(0, 1, 1, 3, 3, [1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0], type: 1).LogicalDefaultValue()
            .ShouldBe(new float[] { 1, 0, 1 });
}
