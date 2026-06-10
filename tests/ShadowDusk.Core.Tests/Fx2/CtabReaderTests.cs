#nullable enable

using System.Text;
using FluentAssertions;
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

        result.IsSuccess.Should().BeTrue();
        var table = result.Value;
        table.VersionToken.Should().Be(0xFFFF0200);
        table.TargetProfile.Should().Be("ps_2_0");
        table.Creator.Should().Be(Fx2SyntheticShaders.Creator);
        table.Constants.Should().HaveCount(2);

        var tint = table.Constants[0];
        tint.Name.Should().Be("Tint");
        tint.RegisterSet.Should().Be(CtabRegisterSet.Float4);
        tint.RegisterIndex.Should().Be(5);
        tint.RegisterCount.Should().Be(1);
        tint.Class.Should().Be(1);  // VECTOR
        tint.Type.Should().Be(3);   // FLOAT
        tint.Rows.Should().Be(1);
        tint.Columns.Should().Be(4);
        tint.Elements.Should().Be(1);

        var sampler = table.Constants[1];
        sampler.Name.Should().Be("s0");
        sampler.RegisterSet.Should().Be(CtabRegisterSet.Sampler);
        sampler.RegisterIndex.Should().Be(2);
        sampler.RegisterCount.Should().Be(1);
        sampler.Class.Should().Be(4); // OBJECT
        sampler.Type.Should().Be(12); // SAMPLER2D
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

        result.IsSuccess.Should().BeTrue();
        result.Value.Constants.Should().ContainSingle()
            .Which.DefaultValue.Should().Equal(1f, 0.5f, 0.25f, 1f);
    }

    [Fact]
    public void Read_MatrixConstantWithDefault_LeavesDefaultValueNull()
    {
        // The CTAB majority of matrix defaults is the unverified F2 ambiguity — the
        // reader skips them by design rather than risking a silent wrong-major bake.
        var blob = Fx2SyntheticShaders.Ps20(
            Fx2SyntheticShaders.Float4x4("World", register: 0, defaultValue: new float[16]));

        var result = CtabReader.Read(blob, SourceFile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Constants.Should().ContainSingle().Which.DefaultValue.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Failure cases — Result failures with SD0301, never exceptions
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_BlobWithoutCtab_FailsSD0301()
    {
        var blob = Fx2SyntheticShaders.WithoutCtab(Fx2SyntheticShaders.Ps20VersionToken);

        var result = CtabReader.Read(blob, SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0301");
        result.Error.Message.Should().ContainEquivalentOf("no CTAB");
    }

    [Fact]
    public void Read_TruncatedCtab_Fails()
    {
        var blob = Fx2SyntheticShaders.Ps20(Fx2SyntheticShaders.Sampler2D("s0"));

        // Cut inside the comment payload: the comment token now claims more dwords than
        // the blob holds.
        var result = CtabReader.Read(blob.AsSpan(0, 16), SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0301");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0301");
        result.Error.Message.Should().ContainEquivalentOf("version");
    }

    [Fact]
    public void Read_NonD3D9Blob_Fails()
    {
        // A DXBC container (SM4+) is not a D3D9 token stream.
        var blob = new byte[16];
        Encoding.ASCII.GetBytes("DXBC").CopyTo(blob, 0);

        var result = CtabReader.Read(blob, SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0301");
        result.Error.Message.Should().ContainEquivalentOf("not a D3D9 token stream");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0301");
        result.Error.Message.Should().ContainEquivalentOf("no CTAB");
    }
}
