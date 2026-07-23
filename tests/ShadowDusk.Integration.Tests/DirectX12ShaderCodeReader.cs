#nullable enable

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Structurally parses a DirectX12-profile shader's <c>ShaderCode</c> field (the value
/// <see cref="ShadowDusk.Core.DirectX12ShaderCodeWrapper.Wrap"/> produces): the small
/// magic-marker header MonoGame's real DirectX12 writer prepends, plus the raw SM6 DXIL
/// bytecode it wraps.
/// </summary>
public sealed class DirectX12ShaderCodeReader
{
    private const uint MagicMarker = 0xB00B00;
    private const uint DxbcContainerMagic = 0x43425844; // ASCII "DXBC", little-endian

    public int    SamplerMaxSlot { get; }
    public int    TextureMaxSlot { get; }
    public byte[] Dxil           { get; }

    public bool DxilMagicOk => Dxil.Length >= 4 && BitConverter.ToUInt32(Dxil, 0) == DxbcContainerMagic;

    private DirectX12ShaderCodeReader(int samplerMaxSlot, int textureMaxSlot, byte[] dxil)
    {
        SamplerMaxSlot = samplerMaxSlot;
        TextureMaxSlot = textureMaxSlot;
        Dxil           = dxil;
    }

    public static DirectX12ShaderCodeReader Parse(byte[] shaderCode)
    {
        using var ms = new MemoryStream(shaderCode);
        using var br = new BinaryReader(ms);

        uint marker = br.ReadUInt32();
        if (marker != MagicMarker)
            throw new InvalidDataException($"Expected DirectX12 magic marker 0x{MagicMarker:X}, got 0x{marker:X}");

        int samplerMaxSlot = br.ReadInt32();
        int textureMaxSlot = br.ReadInt32();
        byte[] dxil = br.ReadBytes((int)(ms.Length - ms.Position));

        return new DirectX12ShaderCodeReader(samplerMaxSlot, textureMaxSlot, dxil);
    }
}
