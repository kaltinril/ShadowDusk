#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Issue #413: the reflection default is the default blob the writers get (issue #422), read back
/// in the documented layout: logical row-major, array element after array element, int by value,
/// bool as 0 or 1. Pure: no native compiler.
/// </summary>
public sealed class ParameterDefaultValuesTests
{
    private static byte[] Blob(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] IntBlob(params int[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static EffectParameterInfo Info(
        EffectParameterClass cls, EffectParameterType type, int rows, int cols,
        byte[]? blob, IReadOnlyList<EffectParameterInfo>? elements = null) =>
        new((byte)cls, (byte)type, "p", null, [], (byte)rows, (byte)cols, [], elements ?? [], blob);

    private static ParameterReflection Param(
        EffectParameterClass cls, EffectParameterType type, int rows, int cols, int elements = 0) =>
        new() { Name = "p", Class = cls, Type = type, Rows = rows, Columns = cols, Elements = elements };

    private static IReadOnlyList<float>? Default(ParameterReflection p, EffectParameterInfo i) =>
        ParameterDefaultValues.Attach([p], [i])[0].DefaultValue;

    [Fact]
    public void Scalar_AndVector_AreTheirComponents()
    {
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, Blob(2.5f)))
            .ShouldBe(new[] { 2.5f });
        Default(Param(EffectParameterClass.Vector, EffectParameterType.Single, 1, 3),
                Info(EffectParameterClass.Vector, EffectParameterType.Single, 1, 3, Blob(1, 2, 3)))
            .ShouldBe(new[] { 1f, 2f, 3f });
    }

    [Fact]
    public void Matrix_BlobIsColumnByColumn_ReflectionIsRowMajor()
    {
        // float2x3 M = float2x3(1,2,3,4,5,6): rows (1 2 3) (4 5 6); the blob is Data[c*2 + r].
        byte[] blob = Blob(1, 4, 2, 5, 3, 6);
        Default(Param(EffectParameterClass.Matrix, EffectParameterType.Single, 2, 3),
                Info(EffectParameterClass.Matrix, EffectParameterType.Single, 2, 3, blob))
            .ShouldBe(new[] { 1f, 2f, 3f, 4f, 5f, 6f });
    }

    [Fact]
    public void IntAndBool_AreConvertedFromTheirWords()
    {
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, IntBlob(-3)))
            .ShouldBe(new[] { -3f });
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Bool, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Bool, 1, 1, IntBlob(-1)))   // 0xFFFFFFFF
            .ShouldBe(new[] { 1f });
    }

    [Fact]
    public void Array_ConcatenatesItsElementBlobs()
    {
        var elements = new[]
        {
            Info(EffectParameterClass.Vector, EffectParameterType.Single, 1, 2, Blob(1, 2)),
            Info(EffectParameterClass.Vector, EffectParameterType.Single, 1, 2, Blob(3, 4)),
        };
        Default(Param(EffectParameterClass.Vector, EffectParameterType.Single, 1, 2, elements: 2),
                Info(EffectParameterClass.Vector, EffectParameterType.Single, 1, 2, null, elements))
            .ShouldBe(new[] { 1f, 2f, 3f, 4f });
    }

    [Fact]
    public void NoBlob_NoDefault()
    {
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, null))
            .ShouldBeNull();
    }

    [Fact]
    public void ArrayWithAnElementThatHasNoBlob_NoDefault()
    {
        var elements = new[]
        {
            Info(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, Blob(1)),
            Info(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, null),
        };
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, elements: 2),
                Info(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, null, elements))
            .ShouldBeNull();
    }

    [Fact]
    public void ObjectParameter_NoDefault()
    {
        Default(Param(EffectParameterClass.Object, EffectParameterType.Texture2D, 0, 0),
                Info(EffectParameterClass.Object, EffectParameterType.Texture2D, 0, 0, null))
            .ShouldBeNull();
    }

    [Fact]
    public void WrongBlobLength_NoDefault()
    {
        Default(Param(EffectParameterClass.Vector, EffectParameterType.Single, 1, 3),
                Info(EffectParameterClass.Vector, EffectParameterType.Single, 1, 3, Blob(1, 2)))
            .ShouldBeNull();
    }

    [Fact]
    public void ResultAndDefaults_CannotBeCastAndMutated()
    {
        IReadOnlyList<ParameterReflection> result = ParameterDefaultValues.Attach(
            [Param(EffectParameterClass.Vector, EffectParameterType.Single, 1, 2)],
            [Info(EffectParameterClass.Vector, EffectParameterType.Single, 1, 2, Blob(1, 2))]);

        (result is ParameterReflection[]).ShouldBeFalse();
        ((IList<ParameterReflection>)result).IsReadOnly.ShouldBeTrue();
        IReadOnlyList<float> defaults = result[0].DefaultValue.ShouldNotBeNull();
        (defaults is float[]).ShouldBeFalse();
        ((IList<float>)defaults).IsReadOnly.ShouldBeTrue();
    }

    // ---- issue #427: the declared kind decides how an Int32 word reads ---------------------

    private static GlobalInitializerDefault Declared(GlobalInitializerKind kind, byte mgfxType) =>
        new("p", 0, mgfxType, 1, 1, 0, [new byte[4]], "f.fx", 1, 1) { Kind = kind };

    private static IReadOnlyList<float>? DefaultDeclared(
        ParameterReflection p, EffectParameterInfo i, GlobalInitializerDefault declared) =>
        ParameterDefaultValues.Attach([p], [i], new Dictionary<string, GlobalInitializerDefault> { ["p"] = declared })[0].DefaultValue;

    [Fact]
    public void BoolReflectedAsInt32_ReadsAsOne_NotMinusOne()
    {
        // Vulkan: SPIR-V has no bool in a uniform block, so `bool Flag = true;` is an Int32
        // parameter (as in real mgfxc's Vulkan build) carrying the D3D true word 0xFFFFFFFF.
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, IntBlob(-1)))
            .ShouldBe(new[] { -1f }, "without the declaration it is indistinguishable from `int X = -1;`");
        DefaultDeclared(Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, IntBlob(-1)),
                Declared(GlobalInitializerKind.Bool, 1))
            .ShouldBe(new[] { 1f });
        DefaultDeclared(Param(EffectParameterClass.Vector, EffectParameterType.Int32, 1, 2),
                Info(EffectParameterClass.Vector, EffectParameterType.Int32, 1, 2, IntBlob(0, -1)),
                Declared(GlobalInitializerKind.Bool, 1))
            .ShouldBe(new[] { 0f, 1f });
    }

    [Fact]
    public void IntDeclared_KeepsItsSign()
    {
        DefaultDeclared(Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, IntBlob(-1)),
                Declared(GlobalInitializerKind.Int, 2))
            .ShouldBe(new[] { -1f });
    }

    [Fact]
    public void UintAboveTwoToThe31_ReadsUnsigned()
    {
        byte[] word = BitConverter.GetBytes(3000000000u);   // 0xB2D05E00, -1294967296 as an int
        DefaultDeclared(Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, word),
                Declared(GlobalInitializerKind.UInt, 2))
            .ShouldBe(new[] { 3000000000f });
        DefaultDeclared(Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, IntBlob(-1)),
                Declared(GlobalInitializerKind.UInt, 2))
            .ShouldBe(new[] { 4294967295f });
    }

    [Fact]
    public void ADeclarationForAnotherName_ChangesNothing()
    {
        IReadOnlyList<ParameterReflection> result = ParameterDefaultValues.Attach(
            [Param(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1)],
            [Info(EffectParameterClass.Scalar, EffectParameterType.Int32, 1, 1, IntBlob(-1))],
            new Dictionary<string, GlobalInitializerDefault> { ["other"] = Declared(GlobalInitializerKind.Bool, 1) });
        result[0].DefaultValue.ShouldBe(new[] { -1f });
    }

    [Fact]
    public void ZeroBlob_IsAZeroDefault_NotUnknown()
    {
        // `float X = 0;` writes a blob of zeros; no initializer writes no blob at all.
        Default(Param(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1),
                Info(EffectParameterClass.Scalar, EffectParameterType.Single, 1, 1, Blob(0)))
            .ShouldBe(new[] { 0f });
    }
}
