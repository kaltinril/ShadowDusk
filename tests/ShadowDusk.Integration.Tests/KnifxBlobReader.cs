#nullable enable

using System.Text;

namespace ShadowDusk.Integration.Tests;

/// <summary>One sampler record of a KNIFX shader: its GLSL name and the effect parameter it binds (-1 = none).</summary>
public sealed record KnifxSamplerRecord(string Name, int TextureSlot, int SamplerSlot, int Parameter);

/// <summary>One KNIFX technique and the names of its passes.</summary>
public sealed record KnifxTechniqueRecord(string Name, IReadOnlyList<string> PassNames);

/// <summary>
/// Reads the parts of a KNIFX container the structural tests assert on: the first backend body's
/// sampler records, top-level parameter names and technique/pass names. The layout is the one
/// <c>ShadowDusk.Core.KnifxWriter</c> writes (KNI's <c>KNIFXWriter11</c>); passes with render-state
/// blocks are not modelled and throw, so a test can never silently read past one.
/// </summary>
public sealed class KnifxBlobReader
{
    public IReadOnlyList<IReadOnlyList<KnifxSamplerRecord>> ShaderSamplers { get; }
    public IReadOnlyList<string> ParameterNames { get; }
    public IReadOnlyList<KnifxTechniqueRecord> Techniques { get; }

    private KnifxBlobReader(
        IReadOnlyList<IReadOnlyList<KnifxSamplerRecord>> shaderSamplers,
        IReadOnlyList<string> parameterNames,
        IReadOnlyList<KnifxTechniqueRecord> techniques)
    {
        ShaderSamplers = shaderSamplers;
        ParameterNames = parameterNames;
        Techniques = techniques;
    }

    public static KnifxBlobReader Parse(byte[] data)
    {
        if (data.Length < 10 || Encoding.ASCII.GetString(data, 0, 4) != "KNIF")
            throw new InvalidDataException("not a KNIFX container");

        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        r.ReadBytes(4);
        r.ReadInt16();                       // version
        r.ReadInt16();                       // reserved
        short backends = r.ReadInt16();
        if (backends < 1)
            throw new InvalidDataException("no backend body");
        r.ReadInt16();                       // first backend
        r.ReadInt32();                       // effect key
        int bodyOffset = r.ReadInt32();

        r.BaseStream.Position = bodyOffset;
        r.ReadInt32();                       // body length
        r.ReadBoolean();                     // integers as floats

        int cbuffers = Packed(r);
        for (int c = 0; c < cbuffers; c++)
        {
            r.ReadString();
            Packed(r);
            int members = Packed(r);
            for (int m = 0; m < members; m++)
            {
                Packed(r);
                r.ReadUInt16();
            }
        }

        var shaderSamplers = new List<IReadOnlyList<KnifxSamplerRecord>>();
        int shaders = Packed(r);
        for (int s = 0; s < shaders; s++)
        {
            r.ReadByte();                    // stage
            Packed(r);                       // shader model major
            Packed(r);                       // shader model minor
            r.ReadBytes(r.ReadInt32());      // shader code
            var samplers = new List<KnifxSamplerRecord>();
            int samplerCount = Packed(r);
            for (int k = 0; k < samplerCount; k++)
            {
                r.ReadByte();                // type
                byte textureSlot = r.ReadByte();
                byte samplerSlot = r.ReadByte();
                if (r.ReadBoolean())
                {
                    r.ReadBytes(3 + 4 + 1);  // address U/V/W, border RGBA, filter
                    Packed(r);               // max anisotropy
                    Packed(r);               // max mip level
                    r.ReadSingle();          // mip LOD bias
                }
                string name = r.ReadString();
                samplers.Add(new KnifxSamplerRecord(name, textureSlot, samplerSlot, Packed(r)));
            }
            shaderSamplers.Add(samplers);

            int cbufferIndices = Packed(r);
            for (int c = 0; c < cbufferIndices; c++)
                Packed(r);
            int attributes = Packed(r);
            for (int a = 0; a < attributes; a++)
            {
                r.ReadString();
                r.ReadByte();
                Packed(r);
                r.ReadInt16();
            }
        }

        List<string> parameterNames = ReadParameters(r);

        var techniques = new List<KnifxTechniqueRecord>();
        int techniqueCount = Packed(r);
        for (int t = 0; t < techniqueCount; t++)
        {
            string name = r.ReadString();
            SkipAnnotations(r);
            var passNames = new List<string>();
            int passes = Packed(r);
            for (int p = 0; p < passes; p++)
            {
                passNames.Add(r.ReadString());
                SkipAnnotations(r);
                Packed(r);                   // vertex shader
                Packed(r);                   // pixel shader
                Packed(r);                   // compute shader
                for (int b = 0; b < 3; b++)
                    if (r.ReadBoolean())
                        throw new NotSupportedException("KnifxBlobReader does not model pass render-state blocks");
            }
            techniques.Add(new KnifxTechniqueRecord(name, passNames));
        }

        return new KnifxBlobReader(shaderSamplers, parameterNames, techniques);
    }

    private static List<string> ReadParameters(BinaryReader r)
    {
        var names = new List<string>();
        int count = Packed(r);
        for (int i = 0; i < count; i++)
        {
            byte cls = r.ReadByte();
            r.ReadByte();                    // type
            names.Add(r.ReadString());
            r.ReadString();                  // semantic
            SkipAnnotations(r);
            byte rows = r.ReadByte();
            byte columns = r.ReadByte();
            r.ReadByte();                    // columns actual
            int elements = ReadParameters(r).Count;
            int members = ReadParameters(r).Count;
            if (elements == 0 && members == 0 && cls <= 2)
                r.ReadBytes(rows * columns * 4);   // default-value blob of a value leaf
        }
        return names;
    }

    private static void SkipAnnotations(BinaryReader r)
    {
        if (Packed(r) != 0)
            throw new NotSupportedException("KNIFX annotations are always written as a zero count");
    }

    private static int Packed(BinaryReader r)
    {
        uint value = 0;
        int shift = 0;
        while (true)
        {
            byte b = r.ReadByte();
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return (int)(value >> 1) ^ -(int)(value & 1);
    }
}
