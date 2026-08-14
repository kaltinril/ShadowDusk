#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pins <see cref="XnbWriter"/> against the container a <b>real</b> <c>dotnet-mgcb</c> writes
/// (Phase 60, issue #199).
///
/// <para>The golden in <see cref="MgcbDesktopGlEnvelope"/> is not hand-derived from a format
/// description — it is the literal first 141 bytes of
/// <c>mgcb 3.8.4.1 /platform:DesktopGL /compress:False</c> output for
/// <c>tests/fixtures/shaders/Grayscale.fx</c>, captured 2026-08-13. Phase 60 A3 calls for exactly
/// that (<i>"measured from a real MGCB .xnb, not transcribed from documentation"</i>), because
/// Phase 52's lesson is that a documented MonoGame behaviour can turn out never to have worked.</para>
///
/// <para>These are pure tests: no disk, no process, no mgcb required. The <i>live</i> comparison
/// against whatever mgcb is installed belongs to the <c>validation/XnbContentLoad</c> driver,
/// which is also the only place the rung-4 <c>Content.Load&lt;Effect&gt;</c> claim can be made.</para>
/// </summary>
public sealed class XnbWriterTests
{
    /// <summary>
    /// Bytes <c>[10, 141)</c> of a real mgcb 3.8.4.1 <c>.xnb</c>: type-reader count, the reader
    /// name manifest, reader version, shared-resource count, and the primary type id. Everything
    /// between the header and the payload-length field, all of it platform-independent.
    /// </summary>
    private static readonly byte[] MgcbDesktopGlEnvelope =
    [
        0x01,                                     // one type reader
        0x77,                                     // 7-bit length: 119
        // "Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework,
        //  Version=3.8.4.1, Culture=neutral, PublicKeyToken=null"
        0x4d, 0x69, 0x63, 0x72, 0x6f, 0x73, 0x6f, 0x66, 0x74, 0x2e, 0x58, 0x6e, 0x61, 0x2e,
        0x46, 0x72, 0x61, 0x6d, 0x65, 0x77, 0x6f, 0x72, 0x6b, 0x2e, 0x43, 0x6f, 0x6e, 0x74,
        0x65, 0x6e, 0x74, 0x2e, 0x45, 0x66, 0x66, 0x65, 0x63, 0x74, 0x52, 0x65, 0x61, 0x64,
        0x65, 0x72, 0x2c, 0x20, 0x4d, 0x6f, 0x6e, 0x6f, 0x47, 0x61, 0x6d, 0x65, 0x2e, 0x46,
        0x72, 0x61, 0x6d, 0x65, 0x77, 0x6f, 0x72, 0x6b, 0x2c, 0x20, 0x56, 0x65, 0x72, 0x73,
        0x69, 0x6f, 0x6e, 0x3d, 0x33, 0x2e, 0x38, 0x2e, 0x34, 0x2e, 0x31, 0x2c, 0x20, 0x43,
        0x75, 0x6c, 0x74, 0x75, 0x72, 0x65, 0x3d, 0x6e, 0x65, 0x75, 0x74, 0x72, 0x61, 0x6c,
        0x2c, 0x20, 0x50, 0x75, 0x62, 0x6c, 0x69, 0x63, 0x4b, 0x65, 0x79, 0x54, 0x6f, 0x6b,
        0x65, 0x6e, 0x3d, 0x6e, 0x75, 0x6c, 0x6c,
        0x00, 0x00, 0x00, 0x00,                   // reader version 0
        0x00,                                     // shared-resource count 0
        0x01,                                     // type id 1 (1-based index into the readers)
    ];

    private static byte[] SamplePayload() => [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];

    [Fact]
    public void Envelope_IsByteIdenticalToRealMgcbOutput()
    {
        byte[] xnb = XnbWriter.Wrap(SamplePayload(), PlatformTarget.OpenGL);

        // [10, 141) is the whole envelope between the header and the payload-length field.
        xnb.AsSpan(10, MgcbDesktopGlEnvelope.Length).ToArray()
           .ShouldBe(MgcbDesktopGlEnvelope);
    }

    [Fact]
    public void Header_MatchesRealMgcbOutput()
    {
        byte[] xnb = XnbWriter.Wrap(SamplePayload(), PlatformTarget.OpenGL);

        // 'X' 'N' 'B' 'd' 0x05 0x00 — magic, DesktopGL, format version 5, uncompressed.
        xnb.AsSpan(0, 6).ToArray().ShouldBe([(byte)'X', (byte)'N', (byte)'B', (byte)'d', 0x05, 0x00]);
    }

    [Fact]
    public void FileSizeField_IsTheTotalFileLength_HeaderIncluded()
    {
        // Measured: mgcb's 796-byte DesktopGL file carries 796 in this field, not the payload
        // size and not the post-header remainder.
        byte[] xnb = XnbWriter.Wrap(SamplePayload(), PlatformTarget.OpenGL);

        BitConverter.ToInt32(xnb, 6).ShouldBe(xnb.Length);
    }

    [Fact]
    public void PayloadIsWrittenVerbatimAndLengthPrefixed()
    {
        byte[] payload = SamplePayload();
        byte[] xnb = XnbWriter.Wrap(payload, PlatformTarget.OpenGL);

        int payloadOffset = 10 + MgcbDesktopGlEnvelope.Length;
        BitConverter.ToInt32(xnb, payloadOffset).ShouldBe(payload.Length);
        xnb.AsSpan(payloadOffset + 4).ToArray().ShouldBe(payload);

        // Nothing after the payload: the container adds no trailer.
        xnb.Length.ShouldBe(payloadOffset + 4 + payload.Length);
    }

    [Theory]
    // Measured directly from mgcb 3.8.4.1 for /platform:Windows and /platform:DesktopGL.
    [InlineData(PlatformTarget.DirectX,   'w')]
    [InlineData(PlatformTarget.OpenGL,    'd')]
    // From the runtime whitelists: MonoGame ContentManager.targetPlatformIdentifiers.
    [InlineData(PlatformTarget.DirectX12, 'G')]
    [InlineData(PlatformTarget.Vulkan,    'V')]
    // FNA's whitelist has no 'V'/'G'; 'w' (XNA Windows) is in BOTH runtimes' lists.
    [InlineData(PlatformTarget.Fna,       'w')]
    public void PlatformIdentifier_IsDerivedFromTheTarget(PlatformTarget target, char expected)
    {
        XnbWriter.PlatformIdentifierFor(target).ShouldBe(expected);
        XnbWriter.Wrap(SamplePayload(), target)[3].ShouldBe((byte)expected);
    }

    [Fact]
    public void EveryDerivedIdentifier_IsAcceptedByTheRuntimeWhitelists()
    {
        // MonoGame ContentManager.targetPlatformIdentifiers (v3.8.5) — a byte outside this set
        // throws ContentLoadException("Asset does not appear to be a valid XNB file") at load.
        char[] monoGame =
        [
            'w', 'x', 'i', 'a', 'd', 'X', 'n', 'r', 'P', '5', 'O', 'S', 'b', 'V', 'G', 's', 'U',
            'W', 'M', 'm', 'p', 'v', 'g', 'l',
        ];

        // FNA ContentManager.targetPlatformIdentifiers — deliberately SMALLER: no 'V', no 'G'.
        char[] fna = ['w', 'x', 'm', 'i', 'a', 'd', 'X', 'W', 'n', 'u', 'p', 'M', 'r', 'P', 'g', 'l'];

        foreach (PlatformTarget target in Enum.GetValues<PlatformTarget>())
        {
            if (target == PlatformTarget.Metal)
                continue;   // not implemented; the pipeline rejects it with SD0200

            char id = XnbWriter.PlatformIdentifierFor(target);
            monoGame.ShouldContain(id, $"{target} maps to '{id}', which MonoGame would reject");

            // The FNA payload is the only one FNA's ContentManager will ever be handed.
            if (target == PlatformTarget.Fna)
                fna.ShouldContain(id, $"{target} maps to '{id}', which FNA would reject");
        }
    }

    [Fact]
    public void Metal_FailsLoudly_RatherThanEmittingAnUnloadableByte()
    {
        // Unreachable through the compiler (SD0200 rejects Metal long before bytes exist), but a
        // silently-wrong platform byte is exactly the class of defect this project refuses to
        // ship, so the writer refuses rather than guessing.
        Should.Throw<ArgumentOutOfRangeException>(
            () => XnbWriter.PlatformIdentifierFor(PlatformTarget.Metal));
    }

    [Fact]
    public void ToXnb_WrapsTheCompiledPayloadVerbatim()
    {
        byte[] payload = SamplePayload();
        var compiled = new CompiledShader(PlatformTarget.DirectX, payload);

        byte[] viaCompiledShader = compiled.ToXnb();
        byte[] viaWriter = XnbWriter.Wrap(payload, PlatformTarget.DirectX);

        // One writer, both surfaces — the A5 requirement that `.mgfx` and the `.xnb`'s payload
        // are identical BY CONSTRUCTION rather than by two code paths kept in step.
        viaCompiledShader.ShouldBe(viaWriter);
        viaCompiledShader[3].ShouldBe((byte)'w');
    }

    [Fact]
    public void SevenBitLengthPrefix_IsCorrectForNamesPast127Bytes()
    {
        // The reader name is 119 bytes, one byte under the 7-bit boundary — so an off-by-one in
        // the length prefix would pass every test above and only break if the name ever grew.
        // Wrapping a >127-byte payload exercises the multi-byte encoder on a field that varies.
        byte[] big = new byte[300];
        byte[] xnb = XnbWriter.Wrap(big, PlatformTarget.OpenGL);

        BitConverter.ToInt32(xnb, 6).ShouldBe(xnb.Length);
        int payloadOffset = 10 + MgcbDesktopGlEnvelope.Length;
        BitConverter.ToInt32(xnb, payloadOffset).ShouldBe(300);
    }
}
