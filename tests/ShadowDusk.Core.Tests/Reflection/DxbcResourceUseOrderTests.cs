#nullable enable

using Shouldly;
using ShadowDusk.Core.Reflection;
using Xunit;
using static ShadowDusk.Core.Tests.Reflection.DxbcSyntheticBlobs;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Pure unit tests for <see cref="DxbcResourceUseOrder"/> over hand-assembled SM4 token streams
/// (D3D10 tokenized program format). Real-compiler coverage is
/// <c>DirectX11TextureSlotOrderTests</c> (Integration), which reads the order back out of
/// vkd3d's DXBC.
/// </summary>
public sealed class DxbcResourceUseOrderTests
{
    private const uint Ps40Version = 0x00000040;

    // Operand tokens (num components | selection mode | swizzle/mask | type << 12 | index dim << 20).
    private static uint Temp4Mask() => 2u | (0u << 2) | (0xFu << 4) | (0u << 12) | (1u << 20);
    private static uint Input4Swizzle() => 2u | (1u << 2) | (0xE4u << 4) | (1u << 12) | (1u << 20);
    private static uint Resource4Swizzle() => 2u | (1u << 2) | (0xE4u << 4) | (7u << 12) | (1u << 20);
    private static uint Sampler() => (6u << 12) | (1u << 20);

    private static uint Opcode(int opcode, int length, bool extended = false) =>
        (uint)opcode | ((uint)length << 24) | (extended ? 0x80000000u : 0u);

    private static uint[] DclResource(int t) => [Opcode(88, 4), (7u << 12) | (1u << 20), (uint)t, 0x5555];

    private static uint[] Sample(int t, int s) =>
        [Opcode(69, 9), Temp4Mask(), 0, Input4Swizzle(), 0, Resource4Swizzle(), (uint)t, Sampler(), (uint)s];

    private static byte[] Shader(params uint[][] instructions)
    {
        var tokens = new List<uint> { Ps40Version, 0 };
        foreach (uint[] i in instructions)
            tokens.AddRange(i);
        tokens[1] = (uint)tokens.Count;
        byte[] payload = new byte[tokens.Count * 4];
        for (int i = 0; i < tokens.Count; i++)
            BitConverter.GetBytes(tokens[i]).CopyTo(payload, i * 4);
        return Container(("SHDR", payload));
    }

    [Fact]
    public void Order_IsTheFirstReadOrder_NotTheDeclarationOrder()
    {
        byte[] dxbc = Shader(
            DclResource(0), DclResource(1), DclResource(2),
            [Opcode(104, 2), 2],          // dcl_temps 2: a raw count, never decoded as an operand
            Sample(t: 2, s: 0),
            Sample(t: 0, s: 1),
            Sample(t: 2, s: 1),
            Sample(t: 1, s: 2),
            [Opcode(62, 1)]);             // ret

        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(dxbc).ShouldBe([2, 0, 1]);
    }

    [Fact]
    public void ExtendedOpcodeTokens_AndCustomData_AreSkipped()
    {
        // sample_aoffimmi: the opcode token's bit 31 announces one extended token (the texel offset).
        uint[] sampleWithOffset =
            [Opcode(69, 10, extended: true), 0x00000001, Temp4Mask(), 0, Input4Swizzle(), 0, Resource4Swizzle(), 3, Sampler(), 0];
        // An immediate constant buffer (customdata): its length is the next dword, and its payload
        // happens to look like a resource operand, which must not be read as one.
        uint[] customData = [53u, 5, Resource4Swizzle(), 7, 0];

        byte[] dxbc = Shader(DclResource(1), DclResource(3), customData, sampleWithOffset, Sample(t: 1, s: 0), [Opcode(62, 1)]);

        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(dxbc).ShouldBe([3, 1]);
    }

    [Fact]
    public void ALoadAndAnImmediateOperand_AreWalked()
    {
        // ld r0, l(0, 0, 0, 0), t4: an immediate32 x4 operand ahead of the resource.
        uint immediate4 = 2u | (4u << 12);
        uint[] ld = [Opcode(45, 9), Temp4Mask(), 0, immediate4, 0, 0, 0, 0, Resource4Swizzle(), 4];
        ld[0] = Opcode(45, ld.Length);

        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(Shader(DclResource(4), ld, [Opcode(62, 1)])).ShouldBe([4]);
    }

    [Fact]
    public void NoTextureRead_IsAnEmptyOrder()
    {
        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(Shader([Opcode(62, 1)])).ShouldBeEmpty();
    }

    [Fact]
    public void AnInstructionRunningPastTheProgram_IsUnreadable()
    {
        uint[] truncated = [Opcode(69, 40), Temp4Mask(), 0];
        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(Shader(truncated)).ShouldBeNull();
    }

    [Fact]
    public void AnOperandRunningPastItsInstruction_IsUnreadable()
    {
        // Length 4 cuts the resource operand's index off.
        uint[] cut = [Opcode(69, 4), Temp4Mask(), 0, Resource4Swizzle()];
        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(Shader(cut, [Opcode(62, 1)])).ShouldBeNull();
    }

    [Fact]
    public void NotADxbcContainer_OrNoShaderChunk_IsUnreadable()
    {
        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(new byte[64]).ShouldBeNull();
        DxbcResourceUseOrder.TextureRegistersInFirstReadOrder(Container(("RDEF", new byte[28]))).ShouldBeNull();
    }
}
