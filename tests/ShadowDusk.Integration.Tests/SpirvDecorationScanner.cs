#nullable enable

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// A minimal, dependency-free SPIR-V scanner for the handful of module-level facts the
/// Vulkan regression tests assert directly on the shipped bytes: matrix majorness, declared
/// extensions, and the entry-point name. Deliberately independent of
/// <c>ShadowDusk.Core.Reflection.Spirv</c> so a bug in the production parser cannot make a
/// test agree with it.
/// </summary>
public static class SpirvDecorationScanner
{
    private const uint Magic          = 0x07230203;
    private const int  OpExtension    = 10;
    private const int  OpEntryPoint   = 15;
    private const int  OpMemberDecorate = 72;
    private const int  DecorationRowMajor = 4;
    private const int  DecorationColMajor = 5;

    /// <summary>Every <c>OpExtension</c> string declared by the module.</summary>
    public static IReadOnlyList<string> Extensions(byte[] spirv)
    {
        var result = new List<string>();
        foreach ((int opcode, uint[] ops) in Instructions(spirv))
            if (opcode == OpExtension)
                result.Add(DecodeString(ops, 0));
        return result;
    }

    /// <summary>The <c>OpEntryPoint</c> name (SPIR-V allows several; the first is returned).</summary>
    public static string? EntryPointName(byte[] spirv)
    {
        foreach ((int opcode, uint[] ops) in Instructions(spirv))
            if (opcode == OpEntryPoint && ops.Length > 2)
                return DecodeString(ops, 2);
        return null;
    }

    /// <summary>
    /// True when the module carries at least one matrix member and EVERY such member is
    /// decorated SPIR-V <c>RowMajor</c>.
    ///
    /// <para><b>The term is inverted relative to HLSL.</b> DXC's SPIR-V backend emits
    /// <c>RowMajor</c> for an HLSL <b>column-major</b> matrix (SPIR-V stores matrices as
    /// column vectors), so this is the "matches fxc/mgfxc's default packing, and therefore
    /// matches how MonoGame uploads a Matrix parameter" assertion — issue #145's bug 1.</para>
    /// </summary>
    /// <summary>True when the module decorates at least one struct member as a matrix.</summary>
    public static bool HasMatrixMember(byte[] spirv)
    {
        foreach ((int opcode, uint[] ops) in Instructions(spirv))
            if (opcode == OpMemberDecorate && ops.Length >= 3 &&
                ((int)ops[2] == DecorationRowMajor || (int)ops[2] == DecorationColMajor))
                return true;
        return false;
    }

    public static bool AllMatrixMembersAreSpirvRowMajor(byte[] spirv)
    {
        bool sawAny = false;

        foreach ((int opcode, uint[] ops) in Instructions(spirv))
        {
            if (opcode != OpMemberDecorate || ops.Length < 3)
                continue;

            int decoration = (int)ops[2];
            if (decoration == DecorationColMajor)
                return false;
            if (decoration == DecorationRowMajor)
                sawAny = true;
        }

        return sawAny;
    }

    private static IEnumerable<(int Opcode, uint[] Operands)> Instructions(byte[] spirv)
    {
        if (spirv.Length < 20 || BitConverter.ToUInt32(spirv, 0) != Magic)
            yield break;

        int wordCount = spirv.Length / 4;
        int i = 5; // skip the 5-word header

        while (i < wordCount)
        {
            uint word = BitConverter.ToUInt32(spirv, i * 4);
            int opcode = (int)(word & 0xFFFF);
            int count  = (int)(word >> 16);

            if (count <= 0 || i + count > wordCount)
                yield break;

            var operands = new uint[count - 1];
            for (int k = 0; k < operands.Length; k++)
                operands[k] = BitConverter.ToUInt32(spirv, (i + 1 + k) * 4);

            yield return (opcode, operands);
            i += count;
        }
    }

    private static string DecodeString(uint[] ops, int start)
    {
        var bytes = new List<byte>();
        for (int i = start; i < ops.Length; i++)
        {
            uint w = ops[i];
            for (int b = 0; b < 4; b++)
            {
                byte c = (byte)((w >> (8 * b)) & 0xFF);
                if (c == 0)
                    return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(c);
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
