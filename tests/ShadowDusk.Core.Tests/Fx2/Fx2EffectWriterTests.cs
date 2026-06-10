#nullable enable

using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Core.Tests.Fx2;

// ===========================================================================
// Shared synthetic-blob helper (also the CtabReaderTests input source)
// ===========================================================================

/// <summary>One constant for a synthetic CTAB (raw on-disk u16 field values, §11 of
/// docs/fx2-binary-format.md).</summary>
internal sealed record SyntheticCtabConstant(
    string Name,
    ushort RegisterSet,
    ushort RegisterIndex,
    ushort RegisterCount,
    ushort Class,
    ushort Type,
    ushort Rows,
    ushort Columns,
    ushort Elements = 1,
    float[]? DefaultValue = null);

/// <summary>
/// Builds minimal-but-valid D3D9 SM2 token streams for tests: version token, a CTAB
/// comment laid out per docs/fx2-binary-format.md §11 (header size 28, version echo,
/// creator/target strings AFTER the type-infos), a trivial `mov oC0, c0`, and the
/// 0x0000FFFF end token. The instruction dwords are the ones fxc emitted in the
/// minimal.fxb golden.
/// </summary>
internal static class Fx2SyntheticShaders
{
    public const uint Ps20VersionToken = 0xFFFF0200;
    public const uint Vs20VersionToken = 0xFFFE0200;
    public const string Creator = "ShadowDusk synthetic test shader";

    private const uint CtabFourcc = 0x42415443; // 'CTAB'
    private const uint EndToken = 0x0000FFFF;

    /// <summary>A sampler constant (register set SAMPLER, class OBJECT, type SAMPLER2D).</summary>
    public static SyntheticCtabConstant Sampler2D(string name, ushort register = 0) =>
        new(name, RegisterSet: 3, RegisterIndex: register, RegisterCount: 1,
            Class: 4, Type: 12, Rows: 1, Columns: 1);

    /// <summary>A float4 constant (register set FLOAT4, class VECTOR).</summary>
    public static SyntheticCtabConstant Float4(string name, ushort register, float[]? defaultValue = null) =>
        new(name, RegisterSet: 2, RegisterIndex: register, RegisterCount: 1,
            Class: 1, Type: 3, Rows: 1, Columns: 4, DefaultValue: defaultValue);

    /// <summary>A float4x4 constant (class MATRIX_ROWS).</summary>
    public static SyntheticCtabConstant Float4x4(string name, ushort register, float[]? defaultValue = null) =>
        new(name, RegisterSet: 2, RegisterIndex: register, RegisterCount: 4,
            Class: 2, Type: 3, Rows: 4, Columns: 4, DefaultValue: defaultValue);

    public static byte[] Ps20(params SyntheticCtabConstant[] constants) =>
        Build(Ps20VersionToken, constants);

    public static byte[] Vs20(params SyntheticCtabConstant[] constants) =>
        Build(Vs20VersionToken, constants);

    /// <summary>Builds a full token stream. <paramref name="ctabVersionOverride"/> lets a
    /// test desynchronize the CTAB's version echo from the shader's version token.</summary>
    public static byte[] Build(
        uint versionToken, SyntheticCtabConstant[] constants, uint? ctabVersionOverride = null)
    {
        byte[] region = BuildCtabRegion(ctabVersionOverride ?? versionToken, versionToken, constants);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(versionToken);
        int commentDwords = 1 + region.Length / 4; // fourcc + region
        writer.Write((uint)((commentDwords << 16) | 0xFFFE));
        writer.Write(CtabFourcc);
        writer.Write(region);
        WriteMovOC0C0(writer);
        writer.Write(EndToken);
        return stream.ToArray();
    }

    /// <summary>A valid SM2 stream with NO CTAB comment (just version, mov, end).</summary>
    public static byte[] WithoutCtab(uint versionToken)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(versionToken);
        WriteMovOC0C0(writer);
        writer.Write(EndToken);
        return stream.ToArray();
    }

    /// <summary>
    /// A stream whose ONLY CTAB-shaped bytes come AFTER real instructions: a `def c0`
    /// whose first float operand bit-patterns as a comment token (0x0042FFFE), followed
    /// by a complete well-formed CTAB comment block. A reader that scans past the leading
    /// comments would "find" that CTAB; the real reader must stop at the first
    /// instruction and report no CTAB.
    /// </summary>
    public static byte[] WithCtabOnlyAfterInstructions(params SyntheticCtabConstant[] constants)
    {
        byte[] full = Ps20(constants);
        int commentDwords = (int)(BitConverter.ToUInt32(full, 4) >> 16);
        byte[] ctabCommentBlock = full.AsSpan(4, 4 + commentDwords * 4).ToArray();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Ps20VersionToken);
        // def c0, <float with comment-token bit pattern>, 1.0, 0.0, 1.0
        writer.Write(0x05000051u); // def (opcode 0x51), as fxc emitted it in minimal.fxb
        writer.Write(0xA00F0000u); // c0 destination
        writer.Write(0x0042FFFEu); // float operand that bit-patterns as a comment token
        writer.Write(0x3F800000u);
        writer.Write(0x00000000u);
        writer.Write(0x3F800000u);
        WriteMovOC0C0(writer);
        writer.Write(ctabCommentBlock);
        writer.Write(EndToken);
        return stream.ToArray();
    }

    private static void WriteMovOC0C0(BinaryWriter writer)
    {
        writer.Write(0x02000001u); // mov
        writer.Write(0x800F0800u); // oC0
        writer.Write(0xA0E40000u); // c0
    }

    private static byte[] BuildCtabRegion(
        uint ctabVersion, uint shaderVersionToken, SyntheticCtabConstant[] constants)
    {
        const int headerSize = 28;
        const int constantInfoSize = 20;
        const int typeInfoSize = 16;

        int count = constants.Length;
        int constantInfoOffset = headerSize;
        int typeInfoBase = constantInfoOffset + count * constantInfoSize;
        int cursor = typeInfoBase + count * typeInfoSize;

        var defaultOffsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (constants[i].DefaultValue is { } defaults)
            {
                defaultOffsets[i] = cursor;
                cursor += defaults.Length * 4;
            }
        }

        // Strings AFTER the type-infos (§11: a type-info may not end flush against the
        // region end, and this is fxc's natural layout anyway).
        var nameOffsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            nameOffsets[i] = cursor;
            cursor += constants[i].Name.Length + 1;
        }
        string target = ((shaderVersionToken >> 16) == 0xFFFF ? "ps" : "vs") +
                        $"_{(shaderVersionToken >> 8) & 0xFF}_{shaderVersionToken & 0xFF}";
        int targetOffset = cursor;
        cursor += target.Length + 1;
        int creatorOffset = cursor;
        cursor += Creator.Length + 1;

        var region = new byte[(cursor + 3) & ~3];

        WriteU32(region, 0, headerSize);
        WriteU32(region, 4, (uint)creatorOffset);
        WriteU32(region, 8, ctabVersion);
        WriteU32(region, 12, (uint)count);
        WriteU32(region, 16, (uint)constantInfoOffset);
        WriteU32(region, 20, 0); // Flags — not read
        WriteU32(region, 24, (uint)targetOffset);

        for (int i = 0; i < count; i++)
        {
            SyntheticCtabConstant c = constants[i];
            int info = constantInfoOffset + i * constantInfoSize;
            WriteU32(region, info, (uint)nameOffsets[i]);
            WriteU16(region, info + 4, c.RegisterSet);
            WriteU16(region, info + 6, c.RegisterIndex);
            WriteU16(region, info + 8, c.RegisterCount);
            WriteU16(region, info + 10, 0); // Reserved
            WriteU32(region, info + 12, (uint)(typeInfoBase + i * typeInfoSize));
            WriteU32(region, info + 16, (uint)defaultOffsets[i]);

            int type = typeInfoBase + i * typeInfoSize;
            WriteU16(region, type, c.Class);
            WriteU16(region, type + 2, c.Type);
            WriteU16(region, type + 4, c.Rows);
            WriteU16(region, type + 6, c.Columns);
            WriteU16(region, type + 8, c.Elements);
            WriteU16(region, type + 10, 0); // StructMembers
            WriteU32(region, type + 12, 0); // StructMemberInfo

            if (c.DefaultValue is { } defaults)
                for (int d = 0; d < defaults.Length; d++)
                    WriteU32(region, defaultOffsets[i] + d * 4, BitConverter.SingleToUInt32Bits(defaults[d]));

            Encoding.ASCII.GetBytes(c.Name).CopyTo(region, nameOffsets[i]);
        }

        Encoding.ASCII.GetBytes(target).CopyTo(region, targetOffset);
        Encoding.ASCII.GetBytes(Creator).CopyTo(region, creatorOffset);
        return region;
    }

    private static void WriteU32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteU16(byte[] buffer, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}

// ===========================================================================
// Fx2EffectWriter tests — hand-built descs, cross-checked by Fx2BinaryValidator
// ===========================================================================

public sealed class Fx2EffectWriterTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Fx2Parameter TextureParam(string name) =>
        new() { Name = name, Class = 4, Type = 5 };

    private static Fx2Parameter SamplerParam(string name, params Fx2SamplerState[] states) =>
        new() { Name = name, Class = 4, Type = 12, SamplerStates = states };

    private static Fx2SamplerState TextureState(string textureParameterName) =>
        new() { Operation = 164, TextureParameterName = textureParameterName };

    private static Fx2SamplerState IntState(int operation, int value) =>
        new() { Operation = operation, IntValue = value };

    /// <summary>A desc equivalent to the textured.fxb golden's source.</summary>
    private static Fx2EffectDesc TexturedDesc() => new()
    {
        Parameters =
        [
            TextureParam("t"),
            SamplerParam("s0", TextureState("t"), IntState(171, 2)), // MipFilter = LINEAR
        ],
        Techniques =
        [
            new Fx2Technique("T", [new Fx2Pass("P", VertexShaderIndex: -1, PixelShaderIndex: 0, [])]),
        ],
        Shaders =
        [
            new Fx2Shader(ShaderStage.Pixel, Fx2SyntheticShaders.Ps20(Fx2SyntheticShaders.Sampler2D("s0"))),
        ],
    };

    private static byte[] WriteOk(Fx2EffectDesc desc)
    {
        var result = new Fx2EffectWriter().Write(desc);
        result.IsSuccess.Should().BeTrue(
            because: "the desc is valid{0}", result.IsFailure ? $" but failed: {result.Error.Message}" : "");
        return result.Value;
    }

    private static ShaderError WriteFails(Fx2EffectDesc desc)
    {
        var result = new Fx2EffectWriter().Write(desc);
        result.IsFailure.Should().BeTrue(because: "the desc violates an fx_2_0 constraint");
        result.Error.Code.Should().Be("SD0302");
        return result.Error;
    }

    // -------------------------------------------------------------------------
    // Happy path — written binary passes the independent MojoShader-rule validator
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_TexturedDesc_ValidatesAndModelMatches()
    {
        var bytes = WriteOk(TexturedDesc());

        var effect = Fx2BinaryValidator.Parse(bytes);

        effect.Parameters.Should().HaveCount(2);
        effect.Parameters[0].Name.Should().Be("t");
        effect.Parameters[0].Class.Should().Be(4);
        effect.Parameters[0].Type.Should().Be(5);
        effect.Parameters[1].Name.Should().Be("s0");
        effect.Parameters[1].Class.Should().Be(4);
        effect.Parameters[1].Type.Should().Be(12);

        var samplerStates = effect.Parameters[1].SamplerStates;
        samplerStates.Should().HaveCount(2);
        samplerStates.Should().ContainSingle(s => s.Operation == 164)
            .Which.ObjectIndex.Should().NotBeNull();
        samplerStates.Should().ContainSingle(s => s.Operation == 171)
            .Which.DwordValue.Should().Be(2);

        effect.SamplerTextureMap.Should().HaveCount(1).And.ContainKey("s0").WhoseValue.Should().Be("t");

        var technique = effect.Techniques.Should().ContainSingle().Subject;
        technique.Name.Should().Be("T");
        var pass = technique.Passes.Should().ContainSingle().Subject;
        pass.Name.Should().Be("P");
        var state = pass.States.Should().ContainSingle().Subject;
        state.Operation.Should().Be(147);

        var shader = effect.Shaders.Should().ContainSingle().Subject;
        shader.Stage.Should().Be(ShaderStage.Pixel);
        shader.VersionToken.Should().Be(0xFFFF0200);
        shader.CtabConstantNames.Should().Equal("s0");
    }

    [Fact]
    public void Write_SameDescTwice_IsByteIdentical()
    {
        var desc = TexturedDesc();

        var first = WriteOk(desc);
        var second = WriteOk(desc);

        second.Should().Equal(first); // deterministic output is a core design constraint
    }

    [Fact]
    public void Write_AbsentVertexShader_EmitsNo146State()
    {
        // F4 policy: VertexShaderIndex = -1 must OMIT the VertexShader state entirely,
        // never emit a "NULL shader" state.
        var bytes = WriteOk(TexturedDesc());

        var pass = Fx2BinaryValidator.Parse(bytes).Techniques[0].Passes[0];

        pass.States.Should().NotContain(s => s.Operation == 146);
        pass.States.Should().ContainSingle(s => s.Operation == 147);
    }

    [Fact]
    public void Write_ShaderTaggedVertexButPixelTokenStream_Fails()
    {
        // Choke-point stage check: a producer that compiles a pass's VertexShader with
        // a ps_* profile tags the blob Vertex while its version token says pixel — fxc
        // rejects this at compile time; shipping it breaks inside FNA at load/draw.
        var desc = new Fx2EffectDesc
        {
            Parameters = [],
            Techniques =
            [
                new Fx2Technique("T", [new Fx2Pass("P", VertexShaderIndex: 0, PixelShaderIndex: -1, [])]),
            ],
            Shaders =
            [
                new Fx2Shader(ShaderStage.Vertex, Fx2SyntheticShaders.Ps20()),
            ],
        };

        ShaderError error = WriteFails(desc);

        error.Message.Should().Contain("version token",
            because: "the diagnostic must say the blob's actual kind contradicts its stage tag");
    }

    [Fact]
    public void Write_Float4x4Parameter_RoundTripsThroughValidator()
    {
        // Square matrices are the one numeric shape with the typedef dword5=columns /
        // dword6=rows quirk (docs/fx2-binary-format.md §7) — round-trip it through the
        // independent validator so the on-disk encoding is pinned, not just accepted.
        var desc = new Fx2EffectDesc
        {
            Parameters =
            [
                new Fx2Parameter { Name = "m", Class = 2, Type = 3, Rows = 4, Columns = 4 },
            ],
            Techniques =
            [
                new Fx2Technique("T", [new Fx2Pass("P", VertexShaderIndex: -1, PixelShaderIndex: 0, [])]),
            ],
            Shaders =
            [
                new Fx2Shader(ShaderStage.Pixel, Fx2SyntheticShaders.Ps20(Fx2SyntheticShaders.Float4x4("m", 0))),
            ],
        };

        var effect = Fx2BinaryValidator.Parse(WriteOk(desc));

        var p = effect.Parameters.Should().ContainSingle().Subject;
        p.Name.Should().Be("m");
        p.Class.Should().Be(2, because: "MATRIX_ROWS class must survive the round trip");
        p.Type.Should().Be(3);
        p.Rows.Should().Be(4, because: "the validator reads rows from typedef dword6 (the MojoShader order)");
        p.Columns.Should().Be(4, because: "the validator reads columns from typedef dword5");
        effect.Shaders.Should().ContainSingle().Which.CtabConstantNames.Should().Equal("m");
    }

    // -------------------------------------------------------------------------
    // Render-state value encoding round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_PassRenderStates_RoundTripExactOpsAndValues()
    {
        uint depthBiasBits = BitConverter.SingleToUInt32Bits(0.25f);
        var desc = TexturedDesc() with
        {
            Techniques =
            [
                new Fx2Technique("T",
                [
                    new Fx2Pass("P", -1, 0,
                    [
                        new Fx2RenderState(13, 1),                       // ALPHABLENDENABLE = TRUE
                        new Fx2RenderState(6, 5),                        // SRCBLEND = SRCALPHA
                        new Fx2RenderState(98, depthBiasBits, IsFloat: true), // DEPTHBIAS = 0.25
                    ]),
                ]),
            ],
        };

        var pass = Fx2BinaryValidator.Parse(WriteOk(desc)).Techniques[0].Passes[0];

        pass.States.Should().ContainSingle(s => s.Operation == 13).Which.DwordValue.Should().Be(1);
        pass.States.Should().ContainSingle(s => s.Operation == 6).Which.DwordValue.Should().Be(5);
        pass.States.Should().ContainSingle(s => s.Operation == 98).Which.DwordValue.Should().Be(depthBiasBits);
        pass.States.Should().ContainSingle(s => s.Operation == 147);
    }

    // -------------------------------------------------------------------------
    // Error cases — every one SD0302, no exception-as-control-flow
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_ZeroTechniques_FailsSD0302()
    {
        var error = WriteFails(TexturedDesc() with { Techniques = [] });

        error.Message.Should().ContainEquivalentOf("technique");
    }

    [Fact]
    public void Write_NonSquareMatrixParameter_FailsSD0302()
    {
        // F1: MojoShader and fxc disagree on the dims order for non-square matrices, so
        // the writer must reject them until a golden settles the question.
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
                new Fx2Parameter { Name = "m", Class = 2, Type = 3, Rows = 4, Columns = 3 },
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("matrix");
    }

    [Fact]
    public void Write_SamplerStateBorderColor_FailsSD0302()
    {
        // Op 168 (BorderColor) makes FNA's runtime throw NotImplementedException.
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t"), IntState(168, 0)),
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("168");
    }

    [Fact]
    public void Write_PassRenderStateNotFnaHonored_FailsSD0302()
    {
        // Op 4 (ALPHATESTENABLE) is a real D3D9 render state but not in FNA's honored set.
        var desc = TexturedDesc() with
        {
            Techniques =
            [
                new Fx2Technique("T", [new Fx2Pass("P", -1, 0, [new Fx2RenderState(4, 1)])]),
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("4");
    }

    [Fact]
    public void Write_TextureDeclaredAfterReferencingSampler_FailsSD0302()
    {
        // FNA builds the sampler map front-to-back: the texture MUST precede the sampler.
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                SamplerParam("s0", TextureState("t")),
                TextureParam("t"),
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("s0");
        error.Message.Should().ContainEquivalentOf("t");
    }

    [Fact]
    public void Write_ShaderIndexOutOfRange_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Techniques = [new Fx2Technique("T", [new Fx2Pass("P", -1, 5, [])])],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("references shader");
    }

    [Fact]
    public void Write_PixelBlobBoundAsVertexShader_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Techniques = [new Fx2Technique("T", [new Fx2Pass("P", VertexShaderIndex: 0, PixelShaderIndex: -1, [])])],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("vertex");
    }

    [Fact]
    public void Write_DuplicateParameterNames_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("t");
    }

    [Fact]
    public void Write_NumericParameterWithSamplerStates_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
                new Fx2Parameter
                {
                    Name = "f", Class = 0, Type = 3, Rows = 1, Columns = 1,
                    SamplerStates = [IntState(171, 2)],
                },
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("sampler");
    }

    // -------------------------------------------------------------------------
    // Numeric default-value encoding — the value blob's dword encoding follows
    // the parameter TYPE: floats as IEEE-754, ints as raw integer dwords, bools
    // as 0/1 (what MojoShader's valuesI/valuesB read back). CTAB defaults arrive
    // as floats even for int/bool globals, so the conversion is load-bearing.
    // -------------------------------------------------------------------------

    /// <summary>A desc with exactly one scalar numeric parameter and a shaderless pass
    /// (both indices -1), so the parameter's value blob is the only pool value.</summary>
    private static Fx2EffectDesc ScalarOnlyDesc(int type, float defaultValue) => new()
    {
        Parameters =
        [
            new Fx2Parameter
            {
                Name = "p", Class = 0, Type = type, Rows = 1, Columns = 1,
                DefaultValue = [defaultValue],
            },
        ],
        Techniques = [new Fx2Technique("T", [new Fx2Pass("P", -1, -1, [])])],
        Shaders = [],
    };

    /// <summary>
    /// Locates the first parameter's value blob by walking the on-disk structure
    /// (docs/fx2-binary-format.md §2/§4/§5), independently of the writer's internals:
    /// header → pool_size dword at +4 → counts header at 8+pool_size → first parameter
    /// record (4 dwords at counts+16) → its value_offset (second dword) → absolute
    /// 8+value_offset.
    /// </summary>
    private static uint FirstParameterValueDword(byte[] fxb)
    {
        uint poolSize = BitConverter.ToUInt32(fxb, 4);
        int countsHeader = 8 + (int)poolSize;
        uint valueOffset = BitConverter.ToUInt32(fxb, countsHeader + 16 + 4);
        return BitConverter.ToUInt32(fxb, 8 + (int)valueOffset);
    }

    [Theory]
    [InlineData(2, 7f, 7u)]            // INT: raw integer dword 7, NOT IEEE 0x40E00000
    [InlineData(1, 1f, 1u)]            // BOOL: normalized 0/1 dword
    [InlineData(3, 0.5f, 0x3F000000u)] // FLOAT: IEEE-754 bits of 0.5
    public void Write_ScalarDefaultValue_EncodesDwordPerParameterType(
        int type, float defaultValue, uint expectedDword)
    {
        var bytes = WriteOk(ScalarOnlyDesc(type, defaultValue));

        // Sanity: the file must still satisfy every MojoShader parse rule.
        Fx2BinaryValidator.Parse(bytes).Parameters.Should().ContainSingle()
            .Which.Name.Should().Be("p");

        FirstParameterValueDword(bytes).Should().Be(expectedDword,
            because: $"a type-{type} scalar default of {defaultValue} must be stored as " +
                     $"dword 0x{expectedDword:X8} (type-aware encoding, not always float bits)");
    }

    // -------------------------------------------------------------------------
    // Validation — the Phase 39 hardening rules (negative elements, blob cap,
    // ASCII-only strings, CTAB↔parameter cross-check). Each fails SD0302.
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_NegativeElements_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
                new Fx2Parameter { Name = "f", Class = 0, Type = 3, Rows = 1, Columns = 1, Elements = -1 },
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("negative");
        error.Message.Should().Contain("f");
    }

    [Fact]
    public void Write_ValueBlobOver65536Dwords_FailsSD0302()
    {
        // 1x1 scalar with 65537 elements = 65537 dwords — just past the cap that guards
        // the value-blob int arithmetic (D3D9 has only 256 float registers anyway).
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
                new Fx2Parameter { Name = "huge", Class = 0, Type = 3, Rows = 1, Columns = 1, Elements = 65537 },
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("implausibly large");
        error.Message.Should().Contain("huge");
    }

    [Fact]
    public void Write_NonAsciiParameterName_FailsSD0302()
    {
        // fx_2_0 strings are ASCII; a lossy re-encode would break MojoShader's
        // exact-strcmp CTAB→parameter binding.
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
                new Fx2Parameter { Name = "Tönung", Class = 1, Type = 3, Rows = 1, Columns = 4 },
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("non-ASCII");
        error.Message.Should().Contain("Tönung");
    }

    [Fact]
    public void Write_NonAsciiSemantic_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Parameters =
            [
                TextureParam("t"),
                SamplerParam("s0", TextureState("t")),
                new Fx2Parameter
                {
                    Name = "f", Semantic = "FARBTÖNUNG", Class = 0, Type = 3, Rows = 1, Columns = 1,
                },
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("non-ASCII");
    }

    [Fact]
    public void Write_NonAsciiTechniqueName_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Techniques = [new Fx2Technique("Tönung", [new Fx2Pass("P", -1, 0, [])])],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("non-ASCII");
    }

    [Fact]
    public void Write_NonAsciiPassName_FailsSD0302()
    {
        var desc = TexturedDesc() with
        {
            Techniques = [new Fx2Technique("T", [new Fx2Pass("Päss", -1, 0, [])])],
        };

        var error = WriteFails(desc);

        error.Message.Should().ContainEquivalentOf("non-ASCII");
    }

    [Fact]
    public void Write_CtabConstantWithoutMatchingParameter_FailsSD0302NamingTheConstant()
    {
        // The shader's CTAB declares 'Ghost', which no effect parameter matches —
        // MojoShader binds by exact strcmp and a miss corrupts memory in release FNA.
        var desc = TexturedDesc() with
        {
            Shaders =
            [
                new Fx2Shader(ShaderStage.Pixel,
                    Fx2SyntheticShaders.Ps20(Fx2SyntheticShaders.Float4("Ghost", 0))),
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().Contain("Ghost");
        error.Message.Should().ContainEquivalentOf("no matching effect");
    }

    [Fact]
    public void Write_ShaderWithoutCtab_FailsSD0302MentioningCtab()
    {
        // A structurally valid SM2 token stream with no CTAB comment binds nothing —
        // the writer must reject it rather than emit an effect FNA cannot bind.
        var desc = TexturedDesc() with
        {
            Shaders =
            [
                new Fx2Shader(ShaderStage.Pixel,
                    Fx2SyntheticShaders.WithoutCtab(Fx2SyntheticShaders.Ps20VersionToken)),
            ],
        };

        var error = WriteFails(desc);

        error.Message.Should().Contain("CTAB");
    }

    // -------------------------------------------------------------------------
    // Multi-technique / multi-pass round-trip — per-pass state sets survive, and
    // every pass-stage reference embeds its own shader object (no sharing).
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_MultiTechniqueMultiPass_RoundTripsStatesAndEmbedsOneShaderPerPassStage()
    {
        byte[] psBlob = Fx2SyntheticShaders.Ps20(Fx2SyntheticShaders.Float4("Tint", 0));
        var desc = new Fx2EffectDesc
        {
            Parameters = [new Fx2Parameter { Name = "Tint", Class = 1, Type = 3, Rows = 1, Columns = 4 }],
            Techniques =
            [
                new Fx2Technique("First",
                [
                    new Fx2Pass("A", -1, 0, [new Fx2RenderState(13, 1)]), // ALPHABLENDENABLE = TRUE
                ]),
                new Fx2Technique("Second",
                [
                    new Fx2Pass("B", -1, 0, [new Fx2RenderState(6, 5)]),  // SRCBLEND = SRCALPHA
                    new Fx2Pass("C", -1, 0, [new Fx2RenderState(8, 1)]),  // CULLMODE = NONE
                ]),
            ],
            Shaders = [new Fx2Shader(ShaderStage.Pixel, psBlob)],
        };

        var effect = Fx2BinaryValidator.Parse(WriteOk(desc));

        effect.Techniques.Select(t => t.Name).Should().Equal("First", "Second");
        effect.Techniques[0].Passes.Select(p => p.Name).Should().Equal("A");
        effect.Techniques[1].Passes.Select(p => p.Name).Should().Equal("B", "C");

        Fx2ParsedPass a = effect.Techniques[0].Passes[0];
        a.States.Select(s => s.Operation).Should().BeEquivalentTo(new[] { 13, 147 });
        a.States.Should().ContainSingle(s => s.Operation == 13).Which.DwordValue.Should().Be(1);

        Fx2ParsedPass b = effect.Techniques[1].Passes[0];
        b.States.Select(s => s.Operation).Should().BeEquivalentTo(new[] { 6, 147 });
        b.States.Should().ContainSingle(s => s.Operation == 6).Which.DwordValue.Should().Be(5);

        Fx2ParsedPass c = effect.Techniques[1].Passes[1];
        c.States.Select(s => s.Operation).Should().BeEquivalentTo(new[] { 8, 147 });
        c.States.Should().ContainSingle(s => s.Operation == 8).Which.DwordValue.Should().Be(1);

        // All three passes bind shader index 0, but each pass-stage reference owns a
        // distinct object with its own embedded copy of the bytecode.
        effect.Shaders.Should().HaveCount(3,
            because: "one shader object is embedded per pass-stage reference, never shared");
        effect.Shaders.Should().OnlyContain(s =>
            s.Stage == ShaderStage.Pixel && s.VersionToken == Fx2SyntheticShaders.Ps20VersionToken);
        foreach (Fx2ParsedShader shader in effect.Shaders)
            shader.CtabConstantNames.Should().Equal("Tint");
    }
}
