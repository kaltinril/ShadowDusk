#nullable enable

using System.IO;
using System.Text;
using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// The default-value blob of a value-typed parameter record, in MGFX (v10 and v11) and KNIFX:
/// <see cref="EffectParameterInfo.DefaultValue"/> reaches the file byte for byte, an unset one is
/// zeros exactly as before, array elements each carry their own, and a blob of the wrong length
/// never reaches the file (a short one would desync MonoGame's reader).
/// </summary>
public sealed class ParameterDefaultValueWriterTests
{
    private static readonly byte[] One = [0x00, 0x00, 0x80, 0x3F];   // 1.0f

    private static EffectParameterInfo Leaf(
        string name, byte cls, byte type, byte rows, byte cols, byte[]? blob) =>
        new(cls, type, name, null, [], rows, cols, [], [], blob);

    private static ShaderIR IR(params EffectParameterInfo[] parameters) => new() { Parameters = parameters };

    // ---- MGFX -----------------------------------------------------------------------------

    // The body of an effect with no constant buffers and no shaders starts with two zero int32s,
    // then the parameter list. Returns each top-level parameter's blob (null for a record that
    // carries none) and each element blob of an array parameter.
    private static List<(string Name, byte[]? Blob, List<byte[]> ElementBlobs)> ReadMgfxParameters(byte[] file)
    {
        using var br = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
        br.ReadBytes(10);                 // "MGFX", version, profile, effect key
        br.ReadInt32().ShouldBe(0);       // constant buffers
        br.ReadInt32().ShouldBe(0);       // shaders
        return ReadList(br);

        static List<(string, byte[]?, List<byte[]>)> ReadList(BinaryReader br)
        {
            int count = br.ReadInt32();
            var list = new List<(string, byte[]?, List<byte[]>)>();
            for (int i = 0; i < count; i++)
            {
                byte cls = br.ReadByte();
                br.ReadByte();                      // type
                string name = br.ReadString();
                br.ReadString();                    // semantic
                br.ReadInt32();                     // annotation count
                byte rows = br.ReadByte();
                byte cols = br.ReadByte();
                var elements = ReadList(br);
                var members = ReadList(br);
                byte[]? blob = null;
                if (cls <= 2 && elements.Count == 0 && members.Count == 0)
                    blob = br.ReadBytes(rows * cols * 4);
                list.Add((name, blob, elements.Select(e => e.Item2!).ToList()));
            }
            return list;
        }
    }

    [Fact]
    public void Mgfx_WritesTheDefaultBlobVerbatim()
    {
        var file = new MgfxWriter().Write(
            IR(Leaf("R", 0, 3, 1, 1, One)), new MgfxWriterOptions(MgfxProfile.OpenGL));
        file.IsSuccess.ShouldBeTrue();

        ReadMgfxParameters(file.Value).Single().Blob.ShouldBe(One);
    }

    [Fact]
    public void Mgfx_NoDefault_IsZeros_ExactlyAsBefore()
    {
        var file = new MgfxWriter().Write(
            IR(Leaf("V", 1, 3, 1, 4, null)), new MgfxWriterOptions(MgfxProfile.OpenGL));

        ReadMgfxParameters(file.Value).Single().Blob.ShouldBe(new byte[16]);
    }

    [Fact]
    public void Mgfx_AnExplicitDefault_ChangesOnlyThoseBytes()
    {
        byte[] zeros = new MgfxWriter().Write(
            IR(Leaf("R", 0, 3, 1, 1, null)), new MgfxWriterOptions(MgfxProfile.OpenGL)).Value;
        byte[] withDefault = new MgfxWriter().Write(
            IR(Leaf("R", 0, 3, 1, 1, One)), new MgfxWriterOptions(MgfxProfile.OpenGL)).Value;

        withDefault.Length.ShouldBe(zeros.Length);
        // The 4 bytes of the blob differ; the header's effect key is a hash of the body, so it differs too.
        int differing = 0;
        for (int i = 14; i < zeros.Length - 4; i++)    // skip the header, the footer
            if (zeros[i] != withDefault[i]) differing++;
        differing.ShouldBe(2);                        // 0x00 0x00 0x80 0x3F vs zeros: two non-zero bytes
    }

    [Fact]
    public void Mgfx_V11_AndEveryProfile_CarryTheSameBlob()
    {
        foreach (MgfxProfile profile in new[] { MgfxProfile.OpenGL, MgfxProfile.DirectX11 })
        {
            var file = new MgfxWriter().Write(
                IR(Leaf("R", 0, 3, 1, 1, One)), new MgfxWriterOptions(profile, MgfxVersion: 11));
            file.IsSuccess.ShouldBeTrue();
            ReadMgfxParameters(file.Value).Single().Blob.ShouldBe(One, profile.ToString());
        }
    }

    [Fact]
    public void Mgfx_ArrayElements_EachCarryTheirOwnBlob_AndTheParentCarriesNone()
    {
        byte[] red = BitConverter.GetBytes(1f).Concat(new byte[12]).ToArray();
        byte[] green = new byte[4].Concat(BitConverter.GetBytes(1f)).Concat(new byte[8]).ToArray();
        var parent = new EffectParameterInfo(
            1, 3, "Colors", null, [], 1, 4, [],
            [Leaf("", 1, 3, 1, 4, red), Leaf("", 1, 3, 1, 4, green)]);

        var file = new MgfxWriter().Write(IR(parent), new MgfxWriterOptions(MgfxProfile.DirectX11));
        file.IsSuccess.ShouldBeTrue();

        var read = ReadMgfxParameters(file.Value).Single();
        read.Blob.ShouldBeNull();
        read.ElementBlobs.Count.ShouldBe(2);
        read.ElementBlobs[0].ShouldBe(red);
        read.ElementBlobs[1].ShouldBe(green);
    }

    [Fact]
    public void Mgfx_ABlobOfTheWrongLength_IsRefused_NeverWritten()
    {
        var file = new MgfxWriter().Write(
            IR(Leaf("V", 1, 3, 1, 4, [1, 2, 3, 4])), new MgfxWriterOptions(MgfxProfile.OpenGL));

        file.IsFailure.ShouldBeTrue();
        file.Error.Code.ShouldBe("SD0012");
        file.Error.Message.ShouldContain("'V'", Case.Sensitive);
    }

    [Fact]
    public void Mgfx_ABlobOnARecordThatCarriesNone_IsRefused()
    {
        // An array parent has element records, so it carries no blob of its own.
        var parent = new EffectParameterInfo(
            1, 3, "Colors", null, [], 1, 4, [], [Leaf("", 1, 3, 1, 4, null)], new byte[16]);

        new MgfxWriter().Write(IR(parent), new MgfxWriterOptions(MgfxProfile.OpenGL))
            .Error.Code.ShouldBe("SD0012");

        // ...and so is an Object-class parameter (a texture).
        var texture = new EffectParameterInfo(3, 7, "Tex", null, [], 0, 0, [], [], new byte[4]);
        new MgfxWriter().Write(IR(texture), new MgfxWriterOptions(MgfxProfile.OpenGL))
            .Error.Code.ShouldBe("SD0012");
    }

    // ---- KNIFX ----------------------------------------------------------------------------

    // KNIFX packs counts, so locate a record by its bytes: class, type, name "R" (length-prefixed),
    // "" semantic, packed 0 annotations, rows, cols, columnsActual, packed 0 elements, packed 0 members,
    // then the blob.
    private static byte[] KnifxBlob(byte[] file, byte cls, byte type, string name, int blobLength)
    {
        byte[] needle = [cls, type, (byte)name.Length, .. Encoding.UTF8.GetBytes(name), 0x00, 0x00];
        for (int i = 0; i + needle.Length < file.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < needle.Length; k++)
                if (file[i + k] != needle[k]) { match = false; break; }
            if (!match) continue;

            int at = i + needle.Length + 3 /* rows, cols, columnsActual */ + 2 /* elements, members */;
            return file[at..(at + blobLength)];
        }
        throw new InvalidOperationException("parameter record not found in the KNIFX bytes");
    }

    [Theory]
    [InlineData(KnifxBackend.OpenGL)]
    [InlineData(KnifxBackend.DirectX11)]
    public void Knifx_WritesTheDefaultBlobVerbatim(KnifxBackend backend)
    {
        var file = new KnifxWriter().Write(IR(Leaf("R", 0, 3, 1, 1, One)), new KnifxWriterOptions(backend));
        file.IsSuccess.ShouldBeTrue();

        KnifxBlob(file.Value, 0, 3, "R", 4).ShouldBe(One);
    }

    [Fact]
    public void Knifx_NoDefault_IsZeros_AndAWrongLengthIsRefused()
    {
        var zeros = new KnifxWriter().Write(
            IR(Leaf("V", 1, 3, 1, 4, null)), new KnifxWriterOptions(KnifxBackend.OpenGL));
        KnifxBlob(zeros.Value, 1, 3, "V", 16).ShouldBe(new byte[16]);

        var bad = new KnifxWriter().Write(
            IR(Leaf("V", 1, 3, 1, 4, [1, 2])), new KnifxWriterOptions(KnifxBackend.OpenGL));
        bad.IsFailure.ShouldBeTrue();
        bad.Error.Code.ShouldBe("SD0012");
    }

    [Fact]
    public void Knifx_Matrix_BlobIsTheFullRowsByColumns()
    {
        byte[] m = Enumerable.Range(0, 16).SelectMany(i => BitConverter.GetBytes((float)i)).ToArray();
        var file = new KnifxWriter().Write(
            IR(Leaf("W", 2, 3, 4, 4, m)), new KnifxWriterOptions(KnifxBackend.OpenGL));

        KnifxBlob(file.Value, 2, 3, "W", 64).ShouldBe(m);
    }
}
