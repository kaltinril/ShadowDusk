using ShadowDusk.HLSL.Dxc;

/// <summary>Finds DXBC containers inside an .mgfx and zeroes or signs their container hash in place.</summary>
static class DxilContainers
{
    public static int ZeroHashes(byte[] mgfx) => Walk(mgfx, off => { Array.Clear(mgfx, off + 4, 16); return true; });

    public static void FlipHashBit(byte[] mgfx) => Walk(mgfx, off => { mgfx[off + 4] ^= 1; return true; });

    public static int SignAll(byte[] mgfx) => Walk(mgfx, off =>
    {
        // SignInPlace takes the container alone: sign a copy of exactly its extent, write the hash back.
        int size = (int)BitConverter.ToUInt32(mgfx, off + 24);
        byte[] c = mgfx.AsSpan(off, size).ToArray();
        if (!DxilContainerHash.SignInPlace(c)) return false;
        Array.Copy(c, 4, mgfx, off + 4, 16);
        return true;
    });

    private static int Walk(byte[] b, Func<int, bool> act)
    {
        int count = 0;
        for (int i = 0; i + 32 <= b.Length; i++)
        {
            if (b[i] != 'D' || b[i + 1] != 'X' || b[i + 2] != 'B' || b[i + 3] != 'C') continue;
            uint size = BitConverter.ToUInt32(b, i + 24);
            uint parts = BitConverter.ToUInt32(b, i + 28);
            if (size < 32 || i + size > b.Length || parts == 0 || parts > 64) continue;
            if (act(i)) count++;
            i += (int)size - 1;
        }
        return count;
    }
}
