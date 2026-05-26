#nullable enable

namespace ShadowDusk.Compiler.Internal;

internal sealed record PassCompilationResult
{
    public required string PassName { get; init; }
    public required ReadOnlyMemory<byte> VertexPrimaryBlob { get; init; }
    public required ReadOnlyMemory<byte> PixelPrimaryBlob { get; init; }
    public required ReadOnlyMemory<byte> VertexDxilBlob { get; init; }
    public required ReadOnlyMemory<byte> PixelDxilBlob { get; init; }
    public required ReadOnlyMemory<byte> VertexSpirvBlob { get; init; }
    public required ReadOnlyMemory<byte> PixelSpirvBlob { get; init; }
}
