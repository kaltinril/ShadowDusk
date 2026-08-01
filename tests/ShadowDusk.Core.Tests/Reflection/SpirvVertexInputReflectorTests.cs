#nullable enable

using Shouldly;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Bug-hunt 2026-07-27 M11: a vertex-attribute reflection failure must be a compile-time
/// <c>Result</c> error, never an empty table. The old empty-table fallback shipped a
/// <c>.mgfx</c> whose zero-element input layout failed at the consumer's first Draw with
/// an unattributed <c>E_INVALIDARG</c> (the exact Phase-54 crash class).
/// </summary>
public sealed class SpirvVertexInputReflectorTests
{
    [Fact]
    public void Read_GarbageBytes_FailsWithReflectionError()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8];

        var result = SpirvVertexInputReflector.Read(garbage);

        result.IsFailure.ShouldBeTrue(
            "unparseable SPIR-V must fail the compile, not silently produce an empty attribute table");
        result.Error.Code.ShouldBe("SD0101");
        result.Error.Message.ShouldContain("SPIR-V", Case.Sensitive);
    }

    [Fact]
    public void Read_EmptyBlob_FailsWithReflectionError()
    {
        var result = SpirvVertexInputReflector.Read(System.ReadOnlyMemory<byte>.Empty);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0101");
    }

    // ---- SD0104: the unknown-semantic default warns (bug-hunt 2026-07-27 N5) ----

    [Fact]
    public void Read_UnrecognizedSemantic_StillEmitsTheFallbackAttribute_AndWarns()
    {
        // POSITION0 is in the table; TEXCORD0 is the classic typo, which mgfxc (and we)
        // default to TextureCoordinate — minting a phantom TEXCOORD attribute the vertex
        // declaration must then supply. mgfxc prints a warning; so must we.
        byte[] spirv = MinimalVertexInputModule(("in.var.POSITION0", 0), ("in.var.TEXCORD0", 1));

        var result = SpirvVertexInputReflector.Read(spirv, out var warnings);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2, "the fallback attribute is STILL emitted; the warning never gates output");
        result.Value[0].Usage.ShouldBe((byte)0);   // POSITION
        result.Value[1].Usage.ShouldBe((byte)2);   // TextureCoordinate fallback
        result.Value[1].Index.ShouldBe((byte)0);

        warnings.Count.ShouldBe(1, "exactly the one unrecognized semantic warns");
        warnings[0].Code.ShouldBe("SD0104");
        warnings[0].Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warnings[0].Message.ShouldContain("TEXCORD0", Case.Sensitive);
    }

    [Fact]
    public void Read_AllSemanticsRecognized_ProducesNoWarnings()
    {
        byte[] spirv = MinimalVertexInputModule(("in.var.POSITION0", 0), ("in.var.TEXCOORD0", 1));

        var result = SpirvVertexInputReflector.Read(spirv, out var warnings);

        result.IsSuccess.ShouldBeTrue();
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Read_BothOverloads_ProduceTheSameAttributeTable()
    {
        // The proof that the warning half of N5 moved no output bytes: the table the
        // warning-reporting overload builds is element-for-element the old one.
        byte[] spirv = MinimalVertexInputModule(("in.var.POSITION0", 0), ("in.var.TEXCORD0", 1));

        var withoutWarnings = SpirvVertexInputReflector.Read(spirv);
        var withWarnings    = SpirvVertexInputReflector.Read(spirv, out _);

        withWarnings.IsSuccess.ShouldBeTrue();
        withoutWarnings.IsSuccess.ShouldBeTrue();
        withWarnings.Value.ShouldBe(withoutWarnings.Value);
    }

    /// <summary>
    /// Hand-builds the smallest SPIR-V module <c>SpirvReflectionParser.ReflectVertexInputs</c>
    /// reads: a <c>float4</c> input variable per semantic, each with an <c>OpName</c> carrying
    /// the DXC-style <c>in.var.&lt;SEMANTIC&gt;</c> spelling and a <c>Location</c> decoration.
    /// Pure (no disk, no native), so this stays a unit test.
    /// </summary>
    private static byte[] MinimalVertexInputModule(params (string Name, int Location)[] inputs)
    {
        const ushort OpName = 5, OpTypeFloat = 22, OpTypeVector = 23, OpTypePointer = 32,
                     OpVariable = 59, OpDecorate = 71;
        const uint LocationDecoration = 30;
        const uint InputStorageClass = 1;

        var words = new List<uint>();

        // Ids: 1 = float, 2 = float4, 3 = ptr(Input, float4), 4.. = the variables.
        uint firstVarId = 4;
        uint bound = firstVarId + (uint)inputs.Length;

        words.AddRange([0x07230203u, 0x00010000u, 0u, bound, 0u]); // magic, version, generator, bound, schema

        void Emit(ushort opcode, params uint[] operands)
        {
            words.Add((uint)(((operands.Length + 1) << 16) | opcode));
            words.AddRange(operands);
        }

        void EmitName(uint target, string name)
        {
            var literal = new List<uint>();
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
            for (int i = 0; i < utf8.Length + 1; i += 4)
            {
                uint word = 0;
                for (int b = 0; b < 4; b++)
                {
                    int index = i + b;
                    byte value = index < utf8.Length ? utf8[index] : (byte)0;
                    word |= (uint)value << (b * 8);
                }
                literal.Add(word);
            }
            Emit(OpName, [target, .. literal]);
        }

        for (int i = 0; i < inputs.Length; i++)
        {
            EmitName(firstVarId + (uint)i, inputs[i].Name);
            Emit(OpDecorate, firstVarId + (uint)i, LocationDecoration, (uint)inputs[i].Location);
        }

        Emit(OpTypeFloat, 1, 32);
        Emit(OpTypeVector, 2, 1, 4);
        Emit(OpTypePointer, 3, InputStorageClass, 2);

        for (int i = 0; i < inputs.Length; i++)
            Emit(OpVariable, 3, firstVarId + (uint)i, InputStorageClass);

        var bytes = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++)
            System.BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }
}
