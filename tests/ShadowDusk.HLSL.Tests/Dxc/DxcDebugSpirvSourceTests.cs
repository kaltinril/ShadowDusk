#nullable enable

using System.Buffers.Binary;
using System.Text;
using Shouldly;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Issue #343, the pure half: <see cref="DxcDebugSpirvSource.DropFileReadText"/> removes the
/// source text DXC read from disk for every <c>OpSource</c> but the main input's, and nothing
/// else. The integration half (real DXC, decoys in the working directory) is
/// <c>DxcDebugSourceWorkingDirectoryTests</c>.
/// </summary>
public sealed class DxcDebugSpirvSourceTests
{
    private const ushort OpSourceContinued = 2;
    private const ushort OpSource = 3;
    private const ushort OpString = 7;
    private const ushort OpNop = 0;
    private const uint Hlsl = 5;

    [Fact]
    public void KeepsTheMainSourceText_DropsEveryOtherFilesTextAndItsContinuations()
    {
        byte[] module = Module(
            Inst(OpString, [1], Literal(DxcDebugSpirvSource.InputName)),
            Inst(OpSource, [Hlsl, 600, 1], Literal("in-memory main text")),
            Inst(OpSourceContinued, [], Literal("main continued")),
            Inst(OpString, [2], Literal("cwd-probe.fx")),
            Inst(OpSource, [Hlsl, 600, 2], Literal("read from disk")),
            Inst(OpSourceContinued, [], Literal("disk continued")),
            Inst(OpSourceContinued, [], Literal("disk continued again")),
            Inst(OpString, [3], Literal("hlsl.hlsl")),
            Inst(OpSource, [Hlsl, 600, 3], []),
            Inst(OpNop, [], []));

        byte[] expected = Module(
            Inst(OpString, [1], Literal(DxcDebugSpirvSource.InputName)),
            Inst(OpSource, [Hlsl, 600, 1], Literal("in-memory main text")),
            Inst(OpSourceContinued, [], Literal("main continued")),
            Inst(OpString, [2], Literal("cwd-probe.fx")),
            Inst(OpSource, [Hlsl, 600, 2], []),
            Inst(OpString, [3], Literal("hlsl.hlsl")),
            Inst(OpSource, [Hlsl, 600, 3], []),
            Inst(OpNop, [], []));

        DxcDebugSpirvSource.DropFileReadText(module).ShouldBe(expected);
    }

    [Fact]
    public void ReturnsTheSameArray_WhenNoFileTextIsPresent()
    {
        byte[] module = Module(
            Inst(OpString, [1], Literal(DxcDebugSpirvSource.InputName)),
            Inst(OpSource, [Hlsl, 600, 1], Literal("main")),
            Inst(OpString, [2], Literal("cwd-probe.fx")),
            Inst(OpSource, [Hlsl, 600, 2], []),
            Inst(OpSource, [Hlsl, 600], []));

        DxcDebugSpirvSource.DropFileReadText(module).ShouldBeSameAs(module);
    }

    [Fact]
    public void ReturnsNull_ForAModuleItCannotWalk()
    {
        DxcDebugSpirvSource.DropFileReadText(new byte[20]).ShouldBeNull("wrong magic");
        DxcDebugSpirvSource.DropFileReadText(new byte[7]).ShouldBeNull("not whole words");

        byte[] overrun = Module(Inst(OpNop, [], []));
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(20), (9u << 16) | OpNop);
        DxcDebugSpirvSource.DropFileReadText(overrun).ShouldBeNull("an instruction runs past the end");

        byte[] zero = Module(Inst(OpNop, [], []));
        BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(20), 0);
        DxcDebugSpirvSource.DropFileReadText(zero).ShouldBeNull("a zero word count");
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Normalize_TouchesOnlyDebugSpirv(bool spirv, bool debug, bool untouched)
    {
        var arguments = new List<string> { "-E", "PS" };
        if (spirv)
            arguments.Add("-spirv");
        if (debug)
            arguments.Add("-Zi");

        // DXIL or release bytes are never walked: garbage passes through as is.
        byte[] garbage = [1, 2, 3];
        var result = DxcDebugSpirvSource.Normalize(arguments, garbage, "a.fx");
        if (untouched)
        {
            result.IsSuccess.ShouldBeTrue();
            result.Value.ShouldBeSameAs(garbage);
        }
        else
        {
            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe(DxcDebugSpirvSource.ErrorCode);
            result.Error.File.ShouldBe("a.fx");
        }
    }

    private static uint[] Literal(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var words = new uint[bytes.Length / 4 + 1];
        var padded = new byte[words.Length * 4];
        bytes.CopyTo(padded, 0);
        for (int i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i * 4));
        return words;
    }

    private static uint[] Inst(ushort opcode, uint[] operands, uint[] literal)
    {
        int length = 1 + operands.Length + literal.Length;
        return [((uint)length << 16) | opcode, .. operands, .. literal];
    }

    private static byte[] Module(params uint[][] instructions)
    {
        uint[] words = [0x07230203, 0x00010000, 14u << 16, 10, 0, .. instructions.SelectMany(i => i)];
        var bytes = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
}
