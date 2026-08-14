#nullable enable

using System;
using System.IO;
using System.Text;

namespace ShadowDusk.Core;

/// <summary>
/// Wraps a compiled effect payload in the XNA/MonoGame <b>XNB content container</b>, so a
/// consumer can drop the file where their <c>mgfxc</c>-built <c>.xnb</c> used to sit and keep
/// calling <c>Content.Load&lt;Effect&gt;("Foo")</c> <b>without changing a line of code</b>
/// (issue #199, Phase 60).
///
/// <para>This writer is deliberately <b>only</b> a container. The payload it wraps is the exact
/// <c>.mgfx</c> / <c>.knifx</c> / <c>.fxb</c> ShadowDusk already produces and has render-proven,
/// so no shader-compilation behaviour changes and no existing output byte moves. It is pure
/// managed code with no native dependency, so it runs on every host including WASM and Android.</para>
///
/// <para><b>Byte layout</b> (uncompressed single-effect asset), measured from real
/// <c>dotnet-mgcb</c> 3.8.4.1 output for <c>/platform:</c> Windows, DesktopGL, Android, iOS and
/// MacOSX — not transcribed from documentation:</para>
/// <code>
/// 'X' 'N' 'B'                 magic
/// &lt;platform&gt;            one of the runtime's accepted identifiers (see PlatformIdentifierFor)
/// 0x05                        format version
/// 0x00                        flags (bit 0x80 = LZX, 0x40 = LZ4; this writer emits uncompressed)
/// int32                       TOTAL file size, header included
/// 7-bit                       type-reader count (always 1 for an effect)
///   7-bit                     reader name length
///   bytes                     reader name (EffectReaderTypeName)
///   int32                     reader version (0)
/// 7-bit                       shared-resource count (0)
/// 7-bit                       type id of the primary object (1 = 1-based index into the readers)
/// int32                       payload length
/// bytes                       the effect payload, verbatim
/// </code>
/// </summary>
public static class XnbWriter
{
    /// <summary>
    /// XNB container format version. MonoGame's <c>ContentManager</c> accepts 4 or 5 and throws
    /// <c>"Invalid XNB version"</c> otherwise; <c>mgcb</c> writes 5.
    /// </summary>
    public const byte FormatVersion = 5;

    /// <summary>
    /// The type-reader manifest entry for an effect, <b>byte-identical to what <c>mgcb</c>
    /// writes</b>.
    ///
    /// <para><b>The embedded assembly version is deliberate and is deliberately ignored by every
    /// consumer runtime.</b> All three strip it before resolving the reader, but they do not
    /// strip it the same way, and the intersection of their rules is exactly this string:</para>
    /// <list type="bullet">
    ///   <item><b>MonoGame</b> (<c>ContentTypeReaderManager.PrepareType</c>) only runs its
    ///   version-stripping regex when the name <i>contains <c>PublicKeyToken</c></i> — so the
    ///   token must be present even though its value is irrelevant.</item>
    ///   <item><b>FNA</b> matches one compiled regex requiring the <i>full</i>
    ///   <c>, &lt;assembly&gt;, Version=…, Culture=…, PublicKeyToken=…</c> triple, and requires
    ///   the assembly to be one of <c>Microsoft.Xna.Framework[.Graphics|.Video]</c> or
    ///   <c>MonoGame.Framework</c>. A bare <c>…, MonoGame.Framework</c> with no version does
    ///   <b>not</b> match, and FNA would then fail to resolve the reader.</item>
    ///   <item><b>KNI</b> is a MonoGame fork and consumes stock <c>mgcb</c> output, which is
    ///   this string.</item>
    /// </list>
    /// So the version number here is inert, but the <i>shape</i> around it is load-bearing.
    /// Emitting exactly what <c>mgcb</c> emits is the choice that cannot be wrong: every
    /// <c>.xnb</c> in every shipped MonoGame game already carries it.
    /// </summary>
    public const string EffectReaderTypeName =
        "Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, "
        + "Version=3.8.4.1, Culture=neutral, PublicKeyToken=null";

    /// <summary>
    /// Returns the XNB platform identifier byte for <paramref name="target"/>.
    ///
    /// <para><b>Derived from the target the consumer already picked — never asked for.</b> The
    /// standing seamlessness directive forbids making a consumer opt in to get correct output,
    /// and measurement showed no opt-in is needed: MonoGame's <c>ContentManager</c> and FNA's
    /// both validate this byte only for <i>membership in a whitelist</i>, never against the
    /// platform actually running, so a correctly-derived byte always loads.</para>
    ///
    /// <para><b>FNA's whitelist is the binding constraint</b> and is smaller than MonoGame's: it
    /// has no <c>'V'</c> (DesktopVK) and no <c>'G'</c> (DirectX 12), which is why
    /// <see cref="PlatformTarget.Fna"/> maps to <c>'w'</c> — the XNA Windows identifier, present
    /// in both lists.</para>
    /// </summary>
    /// <param name="target">The platform backend the payload was compiled for.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="target"/> has no XNB platform identifier. Unreachable through the
    /// compiler: <see cref="PlatformTarget.Metal"/> is the only such target and the pipeline
    /// already rejects it with <c>SD0200</c> long before any bytes exist to wrap.
    /// </exception>
    public static char PlatformIdentifierFor(PlatformTarget target) => target switch
    {
        // Measured: mgcb /platform:Windows   -> 'w'
        PlatformTarget.DirectX   => 'w',
        // Measured: mgcb /platform:DesktopGL -> 'd'
        PlatformTarget.OpenGL    => 'd',
        // MonoGame's whitelist, "Windows DirectX 12". Not in FNA's list, which is correct:
        // FNA has no DX12 backend.
        PlatformTarget.DirectX12 => 'G',
        // MonoGame's whitelist, "DesktopVK". Likewise absent from FNA's list.
        PlatformTarget.Vulkan    => 'V',
        // FNA loads a .fxb; 'w' is the XNA Windows identifier and is in BOTH whitelists.
        PlatformTarget.Fna       => 'w',
        _ => throw new ArgumentOutOfRangeException(
            nameof(target), target,
            "no XNB platform identifier is defined for this target (Metal is not implemented; "
            + "the pipeline rejects it with SD0200 before an effect payload exists)"),
    };

    /// <summary>
    /// Wraps <paramref name="effectPayload"/> in an XNB container for <paramref name="target"/>,
    /// deriving the platform identifier with <see cref="PlatformIdentifierFor"/>.
    /// </summary>
    /// <param name="effectPayload">
    /// The compiled effect bytes — a <c>.mgfx</c>, <c>.knifx</c>, or FNA <c>.fxb</c>. Written
    /// verbatim; this writer never inspects or rewrites the payload.
    /// </param>
    /// <param name="target">The platform backend the payload was compiled for.</param>
    /// <returns>The complete <c>.xnb</c> file bytes.</returns>
    public static byte[] Wrap(ReadOnlySpan<byte> effectPayload, PlatformTarget target) =>
        Wrap(effectPayload, PlatformIdentifierFor(target));

    /// <summary>
    /// Wraps <paramref name="effectPayload"/> in an XNB container using an explicit platform
    /// identifier. Prefer the <see cref="PlatformTarget"/> overload; this one exists for the
    /// validation drivers, which must be able to produce a byte the derivation would not choose
    /// in order to prove the runtime's acceptance rules.
    /// </summary>
    /// <param name="effectPayload">The compiled effect bytes, written verbatim.</param>
    /// <param name="platformIdentifier">The XNB platform identifier character.</param>
    /// <returns>The complete <c>.xnb</c> file bytes.</returns>
    public static byte[] Wrap(ReadOnlySpan<byte> effectPayload, char platformIdentifier)
    {
        byte[] readerName = Encoding.UTF8.GetBytes(EffectReaderTypeName);

        // Header (10) + reader count (1) + name length prefix + name + reader version (4)
        // + shared-resource count (1) + type id (1) + payload length (4) + payload.
        int totalLength =
            10
            + SevenBitLength(1)
            + SevenBitLength(readerName.Length) + readerName.Length + 4
            + SevenBitLength(0)
            + SevenBitLength(1)
            + 4
            + effectPayload.Length;

        var buffer = new byte[totalLength];
        using var stream = new MemoryStream(buffer, writable: true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write((byte)'X');
        writer.Write((byte)'N');
        writer.Write((byte)'B');
        writer.Write((byte)platformIdentifier);
        writer.Write(FormatVersion);
        writer.Write((byte)0);              // flags: uncompressed
        writer.Write(totalLength);          // int32 TOTAL file size, header included

        Write7BitEncodedInt(writer, 1);     // one type reader
        Write7BitEncodedInt(writer, readerName.Length);
        writer.Write(readerName);
        writer.Write(0);                    // reader version

        Write7BitEncodedInt(writer, 0);     // no shared resources
        Write7BitEncodedInt(writer, 1);     // type id: 1-based index of the effect reader

        writer.Write(effectPayload.Length);
        writer.Write(effectPayload);

        return buffer;
    }

    /// <summary>
    /// Writes a .NET 7-bit-encoded int. Hand-rolled rather than using
    /// <c>BinaryWriter.Write7BitEncodedInt</c> so the encoding is pinned by this file and cannot
    /// drift with the BCL — the container's framing depends on it exactly.
    /// </summary>
    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }
        writer.Write((byte)v);
    }

    /// <summary>Byte count <see cref="Write7BitEncodedInt"/> would emit for <paramref name="value"/>.</summary>
    private static int SevenBitLength(int value)
    {
        int count = 1;
        for (uint v = (uint)value; v >= 0x80; v >>= 7)
            count++;
        return count;
    }
}
