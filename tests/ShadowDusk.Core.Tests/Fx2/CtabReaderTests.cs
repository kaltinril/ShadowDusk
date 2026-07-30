#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Fx2;

/// <summary>
/// Tests <see cref="CtabReader"/> against synthetic SM2 token streams built by
/// <see cref="Fx2SyntheticShaders"/> (laid out per docs/fx2-binary-format.md §11).
/// </summary>
public sealed class CtabReaderTests
{
    private const string SourceFile = "Test.fx";

    // -------------------------------------------------------------------------
    // Happy path — field-by-field
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_VectorAndSamplerConstants_ParsesAllFields()
    {
        var blob = Fx2SyntheticShaders.Ps20(
            Fx2SyntheticShaders.Float4("Tint", register: 5),
            Fx2SyntheticShaders.Sampler2D("s0", register: 2));

        var result = CtabReader.Read(blob, SourceFile);

        result.IsSuccess.ShouldBeTrue();
        var table = result.Value;
        table.VersionToken.ShouldBe(0xFFFF0200);
        table.TargetProfile.ShouldBe("ps_2_0");
        table.Creator.ShouldBe(Fx2SyntheticShaders.Creator);
        table.Constants.Count().ShouldBe(2);

        var tint = table.Constants[0];
        tint.Name.ShouldBe("Tint");
        tint.RegisterSet.ShouldBe(CtabRegisterSet.Float4);
        tint.RegisterIndex.ShouldBe(5);
        tint.RegisterCount.ShouldBe(1);
        tint.Class.ShouldBe(1);  // VECTOR
        tint.Type.ShouldBe(3);   // FLOAT
        tint.Rows.ShouldBe(1);
        tint.Columns.ShouldBe(4);
        tint.Elements.ShouldBe(1);

        var sampler = table.Constants[1];
        sampler.Name.ShouldBe("s0");
        sampler.RegisterSet.ShouldBe(CtabRegisterSet.Sampler);
        sampler.RegisterIndex.ShouldBe(2);
        sampler.RegisterCount.ShouldBe(1);
        sampler.Class.ShouldBe(4); // OBJECT
        sampler.Type.ShouldBe(12); // SAMPLER2D
    }

    // -------------------------------------------------------------------------
    // Default values
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_VectorConstantWithDefault_PopulatesDefaultValue()
    {
        var blob = Fx2SyntheticShaders.Ps20(
            Fx2SyntheticShaders.Float4("Tint", register: 0, defaultValue: [1f, 0.5f, 0.25f, 1f]));

        var result = CtabReader.Read(blob, SourceFile);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Constants.ShouldHaveSingleItem().DefaultValue.ShouldBe(new[] {1f, 0.5f, 0.25f, 1f});
    }

    [Fact]
    public void Read_MatrixConstantWithDefault_LeavesDefaultValueNull()
    {
        // The CTAB majority of matrix defaults is the unverified F2 ambiguity — the
        // reader skips them by design rather than risking a silent wrong-major bake.
        var blob = Fx2SyntheticShaders.Ps20(
            Fx2SyntheticShaders.Float4x4("World", register: 0, defaultValue: new float[16]));

        var result = CtabReader.Read(blob, SourceFile);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Constants.ShouldHaveSingleItem().DefaultValue.ShouldBeNull();
    }

    // -------------------------------------------------------------------------
    // Failure cases — Result failures with SD0301, never exceptions
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_BlobWithoutCtab_FailsSD0301()
    {
        var blob = Fx2SyntheticShaders.WithoutCtab(Fx2SyntheticShaders.Ps20VersionToken);

        var result = CtabReader.Read(blob, SourceFile);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0301");
        result.Error.Message.ShouldContain("no CTAB", Shouldly.Case.Insensitive);
    }

    [Fact]
    public void Read_TruncatedCtab_Fails()
    {
        var blob = Fx2SyntheticShaders.Ps20(Fx2SyntheticShaders.Sampler2D("s0"));

        // Cut inside the comment payload: the comment token now claims more dwords than
        // the blob holds.
        var result = CtabReader.Read(blob.AsSpan(0, 16), SourceFile);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0301");
    }

    [Fact]
    public void Read_CtabVersionMismatch_Fails()
    {
        // MojoShader hard-requires the CTAB header's version to echo the shader's version
        // token; CtabReader asserts the same.
        var blob = Fx2SyntheticShaders.Build(
            Fx2SyntheticShaders.Ps20VersionToken,
            [Fx2SyntheticShaders.Sampler2D("s0")],
            ctabVersionOverride: 0xFFFF0300); // ps_3_0 echo inside a ps_2_0 blob

        var result = CtabReader.Read(blob, SourceFile);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0301");
        result.Error.Message.ShouldContain("version", Shouldly.Case.Insensitive);
    }

    [Fact]
    public void Read_NonD3D9Blob_Fails()
    {
        // A DXBC container (SM4+) is not a D3D9 token stream.
        var blob = new byte[16];
        Encoding.ASCII.GetBytes("DXBC").CopyTo(blob, 0);

        var result = CtabReader.Read(blob, SourceFile);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0301");
        result.Error.Message.ShouldContain("not a D3D9 token stream", Shouldly.Case.Insensitive);
    }

    // -------------------------------------------------------------------------
    // Leading-comments-only scan
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_CtabPatternAfterRealInstruction_IsNotMistakenForConstantTable()
    {
        // The blob contains a def-instruction float operand that bit-patterns like a
        // comment token (0x0042FFFE) and a complete well-formed CTAB block — both AFTER
        // the first real instruction. The reader scans only the leading comment blocks,
        // so it must report "no CTAB" instead of misreading instruction data.
        var blob = Fx2SyntheticShaders.WithCtabOnlyAfterInstructions(
            Fx2SyntheticShaders.Sampler2D("s0"));

        var result = CtabReader.Read(blob, SourceFile);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0301");
        result.Error.Message.ShouldContain("no CTAB", Shouldly.Case.Insensitive);
    }
}
