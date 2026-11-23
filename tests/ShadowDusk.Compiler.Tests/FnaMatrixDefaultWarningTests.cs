#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for <c>SD0306</c> (issue #505). The CTAB image of a column-major matrix holds
/// one register per column, so the declared <c>float2x2(1, 2, 3, 4)</c> is <c>1, 3, _, _, 2, 4</c>.
/// </summary>
public sealed class FnaMatrixDefaultWarningTests
{
    private const int MatrixRows = 2;
    private const int MatrixColumns = 3;
    private const int FloatType = 3;

    private static CtabConstant Matrix(string name, int matrixClass, int size, int elements, params float[] image) =>
        new(name, CtabRegisterSet.Float4, 0, image.Length / 4, matrixClass, FloatType, size, size, elements, image);

    private static CtabTable Table(params CtabConstant[] constants) => new(0xFFFF0300, "test", "ps_3_0", constants);

    private static ShaderError? Check(params CtabTable[] tables) => FnaMatrixDefaultWarning.Check(tables, "effect.fx");

    [Fact]
    public void NonSymmetricColumnMajorDefault_Warns_NamingTheMatrix()
    {
        ShaderError warning = Check(Table(Matrix("M", MatrixColumns, 2, 1, 1, 3, 0, 0, 2, 4, 0, 0))).ShouldNotBeNull();

        warning.Code.ShouldBe("SD0306");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.File.ShouldBe("effect.fx");
        warning.Message.ShouldContain("'M'", Case.Sensitive);
        warning.Message.ShouldContain("SetValue(Matrix)", Case.Sensitive);
    }

    [Fact]
    public void SymmetricDefault_DoesNotWarn() =>
        Check(Table(Matrix("Identity", MatrixColumns, 2, 1, 1, 0, 0, 0, 0, 1, 0, 0))).ShouldBeNull();

    [Fact]
    public void RowMajorDefault_DoesNotWarn() =>
        Check(Table(Matrix("M", MatrixRows, 2, 1, 1, 2, 0, 0, 3, 4, 0, 0))).ShouldBeNull();

    [Fact]
    public void MatrixWithNoDefault_DoesNotWarn() =>
        Check(Table(new CtabConstant("M", CtabRegisterSet.Float4, 0, 2, MatrixColumns, FloatType, 2, 2, 1, null))).ShouldBeNull();

    [Fact]
    public void ArrayWithOneNonSymmetricElement_Warns() =>
        Check(Table(Matrix("A", MatrixColumns, 2, 2,
            1, 0, 0, 0, 0, 1, 0, 0,
            1, 3, 0, 0, 2, 4, 0, 0))).ShouldNotBeNull();

    [Fact]
    public void NaNOnBothSidesOfTheDiagonal_IsSymmetric() =>
        Check(Table(Matrix("M", MatrixColumns, 2, 1, 1, float.NaN, 0, 0, float.NaN, 4, 0, 0))).ShouldBeNull();

    [Fact]
    public void TheLongestStageImageIsTheOneChecked()
    {
        // The first stage reads only column 0, which alone looks symmetric. The second stage
        // reads both columns, and its image shows the asymmetry.
        CtabConstant trimmed = Matrix("M", MatrixColumns, 2, 1, 1, 0, 0, 0);
        CtabConstant full = Matrix("M", MatrixColumns, 2, 1, 1, 0, 0, 0, 2, 4, 0, 0);

        Check(Table(trimmed), Table(full)).ShouldNotBeNull();
    }

    [Fact]
    public void SeveralMatrices_ProduceOneWarningNamingOnlyTheAffectedOnes()
    {
        ShaderError warning = Check(
            Table(Matrix("A", MatrixColumns, 2, 1, 1, 3, 0, 0, 2, 4, 0, 0)),
            Table(
                Matrix("B", MatrixColumns, 2, 1, 5, 7, 0, 0, 6, 8, 0, 0),
                Matrix("C", MatrixRows, 2, 1, 1, 2, 0, 0, 3, 4, 0, 0))).ShouldNotBeNull();

        warning.Message.ShouldContain("'A', 'B'", Case.Sensitive);
        warning.Message.ShouldNotContain("'C'", Case.Sensitive);
    }
}
