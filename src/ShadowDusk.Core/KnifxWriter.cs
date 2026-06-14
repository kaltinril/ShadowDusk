#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShadowDusk.Core;

/// <summary>
/// The KNI graphics backend a KNIFX body targets. The value IS the on-disk
/// <c>GraphicsBackend</c> enum integer KNI's runtime matches against its adapter
/// (verified from <c>Xna.Framework.Graphics/Graphics/GraphicsBackend.cs</c> @ KNI main).
/// Desktop SDL2.GL reports <see cref="OpenGL"/>; the browser reports <see cref="WebGL"/>.
/// </summary>
public enum KnifxBackend : short
{
    OpenGL    = 0x0011,
    GLES      = 0x0012,
    WebGL     = 0x0014,
    DirectX11 = 0x0021,
    DirectX12 = 0x0022,
    Vulkan    = 0x0081,
    Metal     = 0x0101,
}

/// <summary>Options for <see cref="KnifxWriter"/>.</summary>
public sealed record KnifxWriterOptions(KnifxBackend Backend);

/// <summary>
/// Serializes a ShadowDusk <see cref="ShaderIR"/> into KNI's <b>KNIFX v11</b> effect
/// container, the additive newer format KNI v4.02+ loads (signature <c>"KNIF"</c>). This
/// is the KNIFX analogue of <see cref="MgfxWriter"/>: it carries the <b>same</b>
/// MojoShader-dialect GLSL body ShadowDusk already produces for MGFX v10 (KNIFX is a
/// container over a still-MojoShader body), re-serialized into KNIFX's binary layout.
///
/// Byte format reverse-engineered from KNI's own <c>KNIFXWriter11</c> + the runtime
/// <c>Effect</c> reader (full spec: <c>plan/PHASE-35-appendix/knifx-format-spec.md</c>).
/// The headline deltas vs MGFX v10:
/// <list type="bullet">
///   <item>A <b>multi-backend directory</b> header (one <c>.knifx</c> can carry several
///   backend bodies; the runtime picks the match). This writer emits one backend.</item>
///   <item>Counts and most indices are written as <b>packed ints</b> (zigzag + 7-bit).</item>
///   <item>New fields: a per-shader <c>ShaderVersion</c>, a <c>Stage</c> byte (with a
///   compute slot), and a <c>columnsActual</c> byte on parameters.</item>
/// </list>
/// The pass render-state blocks are byte-identical to v10, so <see cref="MgfxWriter"/>'s
/// writers are reused verbatim. Default product output stays MGFX v10; KNIFX is additive.
/// </summary>
public sealed class KnifxWriter
{
    /// <summary>KNIFX 4-byte signature (ASCII "KNIF"), per KNI's <c>KNIFXWriter11</c>.</summary>
    public const string KnifxSignature = "KNIF";

    /// <summary>The only KNIFX container version KNI defines today.</summary>
    public const short KnifxVersion = 11;

    public Result<byte[], ShaderError> Write(ShaderIR ir, KnifxWriterOptions options)
    {
        // KNIFX writes a leading bool the runtime uses to decode integer-typed parameter
        // defaults as floats; KNI sets it true for the OpenGL (MojoShader) profile.
        bool integersAsFloats = options.Backend is
            KnifxBackend.OpenGL or KnifxBackend.GLES or KnifxBackend.WebGL;

        // Serialize the single backend body first so the directory's effectKey + offsets
        // can be computed from it.
        byte[] body = SerializeBody(ir, options.Backend, integersAsFloats);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // ---- Header: "KNIF" + version + reserved + a 1-entry backend directory --------
        bw.Write(KnifxSignature.ToCharArray()); // 4 bytes (no length prefix; matches KNI)
        bw.Write(KnifxVersion);                 // int16 = 11
        bw.Write((short)0);                      // reserved
        bw.Write((short)1);                      // backendCount (single backend)

        const int headerSize = 10;               // 4 + 2 + 2 + 2
        const int entrySize  = 10;               // backend(2) + effectKey(4) + fxOffset(4)
        int fxOffset = headerSize + entrySize;   // body's length prefix starts here (=20)

        bw.Write((short)options.Backend);        // GraphicsBackend enum value
        bw.Write(ComputeEffectKey(body));        // int32 FNV-1a (cache key; not validated)
        bw.Write(fxOffset);                       // int32 absolute offset of the body

        // ---- Body: int32 length prefix then the serialized effect ---------------------
        bw.Write(body.Length);                    // int32
        bw.Write(body);

        bw.Flush();
        return Result<byte[], ShaderError>.Ok(ms.ToArray());
    }

    private static byte[] SerializeBody(ShaderIR ir, KnifxBackend backend, bool integersAsFloats)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(integersAsFloats);           // bool (KNIFXWriter11.WriteEffect head)
            WriteConstantBuffers(bw, ir);
            WriteShaders(bw, ir, backend);
            WriteParameterList(bw, ir.Parameters);
            WriteTechniques(bw, ir);
            bw.Flush();
        }
        return ms.ToArray();
    }

    private static bool IsGlBackend(KnifxBackend backend) =>
        backend is KnifxBackend.OpenGL or KnifxBackend.GLES or KnifxBackend.WebGL;

    // ---- Packed-int (zigzag + 7-bit) — the pervasive KNIFX count/index encoding -------
    // Matches KNI's KNIFXWriter11.WritePackedInt: zzint = (v << 1) ^ (v >> 31) then a
    // standard LEB128 7-bit-encoded uint.
    private static void WritePacked(BinaryWriter bw, int value)
    {
        uint zz = unchecked((uint)((value << 1) ^ (value >> 31)));
        while (zz >= 0x80)
        {
            bw.Write((byte)(zz | 0x80));
            zz >>= 7;
        }
        bw.Write((byte)zz);
    }

    // effectKey = FNV-1a/32 over the body + KNI's avalanche mix (HashHelpers.ComputeHash).
    // The KNIFX reader uses it only as an EffectCache key (it does NOT re-hash/validate the
    // body), so a load+render works regardless; we replicate it for byte-faithfulness.
    private static int ComputeEffectKey(byte[] body)
    {
        unchecked
        {
            int hash = (int)2166136261;
            const int prime = 16777619;
            for (int i = 0; i < body.Length; i++)
                hash = (hash ^ body[i]) * prime;
            hash += hash << 13;
            hash ^= hash >> 7;
            hash += hash << 3;
            hash ^= hash >> 17;
            hash += hash << 5;
            return hash;
        }
    }

    private static void WriteConstantBuffers(BinaryWriter bw, ShaderIR ir)
    {
        WritePacked(bw, ir.ConstantBuffers.Count);
        foreach (var cb in ir.ConstantBuffers)
        {
            bw.Write(cb.Name);
            WritePacked(bw, cb.SizeInBytes);
            WritePacked(bw, cb.ParameterIndices.Count);
            for (int i = 0; i < cb.ParameterIndices.Count; i++)
            {
                WritePacked(bw, cb.ParameterIndices[i]);
                bw.Write(cb.ParameterOffsets[i]); // ushort (unchanged from v10)
            }
        }
    }

    private static void WriteShaders(BinaryWriter bw, ShaderIR ir, KnifxBackend backend)
    {
        bool isGl = IsGlBackend(backend);
        WritePacked(bw, ir.Shaders.Count);
        foreach (var blob in ir.Shaders)
        {
            bw.Write(KniStage(blob.Stage));                  // byte (Pixel=0, Vertex=1)
            WritePacked(bw, blob.ShaderModel.Major);          // NEW vs v10 (also selects the
            WritePacked(bw, blob.ShaderModel.Minor);          // GL directory-parse path below)

            // CRITICAL: with a non-default ShaderVersion, KNI's GL runtime parses ShaderCode
            // as a GLSL-version bytecode DIRECTORY (ConcreteShader.FindShaderByteCode), NOT as
            // raw GLSL. So the GL ShaderCode must be the directory ShaderProfileGL.CreateGLSL
            // emits (NOT ShadowDusk's raw GLSL bytes), or KNI throws "Invalid shader bytecode".
            // The DXBC (DX) path takes ShaderCode verbatim.
            byte[] shaderCode = isGl ? BuildGlShaderCode(backend, blob.Bytes) : blob.Bytes;
            bw.Write(shaderCode.Length);                      // int32 (plain, unchanged)
            bw.Write(shaderCode);

            WritePacked(bw, blob.Samplers.Count);
            foreach (var s in blob.Samplers)
            {
                bw.Write(s.Type);
                bw.Write(s.TextureSlot);
                bw.Write(s.SamplerSlot);
                if (s.State is { } st)
                {
                    bw.Write(true);
                    WriteSamplerState(bw, st);
                }
                else
                {
                    bw.Write(false);
                }
                bw.Write(s.Name);
                WritePacked(bw, s.Parameter);                 // was a byte in v10
            }

            WritePacked(bw, blob.ConstantBufferIndices.Count);
            foreach (var cbi in blob.ConstantBufferIndices)
                WritePacked(bw, cbi);

            WritePacked(bw, blob.Attributes.Count);
            foreach (var a in blob.Attributes)
            {
                bw.Write(a.Name);
                bw.Write(a.Usage);                            // byte
                WritePacked(bw, a.Index);                     // was a byte in v10
                bw.Write(a.Location);                         // int16 (unchanged)
            }
        }
    }

    // Same field order as MGFX v10's sampler state, but MaxAnisotropy/MaxMipLevel are
    // packed ints in KNIFX (they were plain int32 in v10).
    private static void WriteSamplerState(BinaryWriter bw, MgfxSamplerStateInfo st)
    {
        bw.Write(st.AddressU);
        bw.Write(st.AddressV);
        bw.Write(st.AddressW);
        bw.Write(st.BorderColorR);
        bw.Write(st.BorderColorG);
        bw.Write(st.BorderColorB);
        bw.Write(st.BorderColorA);
        bw.Write(st.Filter);
        WritePacked(bw, st.MaxAnisotropy);
        WritePacked(bw, st.MaxMipLevel);
        bw.Write(st.MipMapLevelOfDetailBias);                 // single
    }

    // The GL ShaderCode is a GLSL-version bytecode DIRECTORY, exactly as KNI's
    // ShaderProfileGL.CreateGLSL emits it: a reserved int16, an int16 entry count, then
    // per entry {byte Major, byte Minor, bool ES, int32 offset} (HeaderSize 4, EntrySize 7),
    // then each blob as {int32 length, bytes}. The KNI runtime picks the entry matching its
    // GL context. ShadowDusk's GL output is MojoShader-dialect GLSL 1.10, which IS KNI's
    // OpenGL desktop entry (Major=1, Minor=1, ES=false) — the validated SDL2.GL target.
    // (GLES/WebGL would carry converted 300es/100 entries; a documented future refinement.)
    private static byte[] BuildGlShaderCode(KnifxBackend backend, byte[] glsl)
    {
        var entries = new List<(byte Major, byte Minor, bool Es, byte[] Code)>
        {
            (1, 1, false, glsl),
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write((short)0);                       // reserved
        bw.Write((short)entries.Count);           // directory entry count
        const int headerSize = 4;                  // reserved(2) + count(2)
        const int entrySize = 7;                   // major(1) + minor(1) + es(1) + offset(4)
        int blobOffset = headerSize + entrySize * entries.Count;
        foreach (var e in entries)
        {
            bw.Write(e.Major);
            bw.Write(e.Minor);
            bw.Write(e.Es);
            bw.Write(blobOffset);                  // int32 absolute offset of the blob's length
            blobOffset += 4 + e.Code.Length;
        }
        foreach (var e in entries)
        {
            bw.Write(e.Code.Length);               // int32
            bw.Write(e.Code);
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteParameterList(BinaryWriter bw, IReadOnlyList<EffectParameterInfo> parameters)
    {
        WritePacked(bw, parameters.Count);
        foreach (var p in parameters)
            WriteParameter(bw, p);
    }

    private static void WriteParameter(BinaryWriter bw, EffectParameterInfo p)
    {
        bw.Write(p.Class);
        bw.Write(p.Type);
        bw.Write(p.Name);
        bw.Write(p.Semantic ?? "");
        WriteAnnotations(bw, p.Annotations);
        bw.Write(p.RowCount);
        bw.Write(p.ColumnCount);
        bw.Write(p.ColumnCount);                              // columnsActual (NEW); = columns

        // Elements first, then members (mirrors KNI's WriteParameters(element_count) then
        // WriteParameters(member_count); same order as MGFX v10).
        WriteParameterList(bw, p.Elements);
        WriteParameterList(bw, p.Members);

        // A value-typed leaf carries a raw default-value blob of rows*cols*4 bytes (no
        // length prefix). ShadowDusk has no defaults; emit zeros (runtime sets by name).
        bool isValueType = p.Class <= 2;                      // Scalar=0, Vector=1, Matrix=2
        if (isValueType && p.Members.Count == 0 && p.Elements.Count == 0)
        {
            int dataLen = p.RowCount * p.ColumnCount * 4;
            for (int b = 0; b < dataLen; b++)
                bw.Write((byte)0);
        }
    }

    private static void WriteTechniques(BinaryWriter bw, ShaderIR ir)
    {
        WritePacked(bw, ir.Techniques.Count);
        foreach (var tech in ir.Techniques)
        {
            bw.Write(tech.Name);
            WriteAnnotations(bw, tech.Annotations);
            WritePacked(bw, tech.Passes.Count);
            foreach (var pass in tech.Passes)
            {
                bw.Write(pass.Name);
                WriteAnnotations(bw, pass.Annotations);
                WritePacked(bw, pass.VertexShaderIndex);
                WritePacked(bw, pass.PixelShaderIndex);
                WritePacked(bw, NoComputeShaderIndex);        // compute slot (NEW); we have none
                // Render-state blocks are byte-identical to v10 — reuse MgfxWriter's writers.
                MgfxWriter.WriteRenderStateBlock(bw, pass.RenderState);
            }
        }
    }

    // KNI's EffectObject.GetShaderIndex returns -1 for a stage a pass does not bind; we
    // never emit a compute shader, so every pass writes -1 here.
    private const int NoComputeShaderIndex = -1;

    private static void WriteAnnotations(BinaryWriter bw, IReadOnlyList<AnnotationInfo> annotations)
    {
        // KNIFX (like MGFX v10) writes only the annotation COUNT and no bodies
        // (KNIFXWriter11.WriteAnnotations asserts count == 0; ShadowDusk's are empty).
        WritePacked(bw, annotations.Count);
    }

    // ShadowDusk ShaderStage (Vertex=0, Pixel=1) -> KNI ShaderStage (Pixel=0, Vertex=1).
    private static byte KniStage(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => 1,
        ShaderStage.Pixel  => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported shader stage for KNIFX"),
    };
}
