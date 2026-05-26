#nullable enable

namespace ShadowDusk.HLSL.Dxc;

public sealed class PlatformBlob
{
    public BlobKind Kind { get; }
    public ReadOnlyMemory<byte> Bytes { get; }

    public PlatformBlob(BlobKind kind, ReadOnlyMemory<byte> bytes)
    {
        Kind = kind;
        Bytes = bytes;
    }
}
