#nullable enable

using System.Text;

namespace ShadowDusk.Integration.Tests;

public sealed record PassInfo(
    string Name,
    int VertexShaderIndex,
    int PixelShaderIndex,
    MgfxBlendStateRecord? BlendState = null,
    MgfxDepthStencilStateRecord? DepthStencilState = null,
    MgfxRasterizerStateRecord? RasterizerState = null);

public sealed record TechniqueInfo(string Name, int PassCount, IReadOnlyList<PassInfo> Passes);

// ---------------------------------------------------------------------------
// Pass render-state records, field-for-field as MonoGame 3.8.2 Effect.ReadPasses
// reads them (fixed alphabetical layout — Phase 43, F1).
// ---------------------------------------------------------------------------

public sealed record MgfxBlendStateRecord(
    byte AlphaBlendFunction,
    byte AlphaDestinationBlend,
    byte AlphaSourceBlend,
    byte BlendFactorR, byte BlendFactorG, byte BlendFactorB, byte BlendFactorA,
    byte ColorBlendFunction,
    byte ColorDestinationBlend,
    byte ColorSourceBlend,
    byte ColorWriteChannels,
    byte ColorWriteChannels1,
    byte ColorWriteChannels2,
    byte ColorWriteChannels3,
    int  MultiSampleMask);

public sealed record MgfxDepthStencilStateRecord(
    byte CounterClockwiseStencilDepthBufferFail,
    byte CounterClockwiseStencilFail,
    byte CounterClockwiseStencilFunction,
    byte CounterClockwiseStencilPass,
    bool DepthBufferEnable,
    byte DepthBufferFunction,
    bool DepthBufferWriteEnable,
    int  ReferenceStencil,
    byte StencilDepthBufferFail,
    bool StencilEnable,
    byte StencilFail,
    byte StencilFunction,
    int  StencilMask,
    byte StencilPass,
    int  StencilWriteMask,
    bool TwoSidedStencilMode);

public sealed record MgfxRasterizerStateRecord(
    byte  CullMode,
    float DepthBias,
    byte  FillMode,
    bool  MultiSampleAntiAlias,
    bool  ScissorTestEnable,
    float SlopeScaleDepthBias);

/// <summary>
/// One constant-buffer record (Phase 43C): the name MonoGame's GL runtime keys
/// glUniform4fv on, the buffer size, and the interleaved parameter index/offset table.
/// </summary>
public sealed record MgfxConstantBufferRecord(
    string Name,
    int Size,
    IReadOnlyList<int> ParameterIndices,
    IReadOnlyList<int> ParameterOffsets);

/// <summary>
/// One shader record's identity + bindings (Phase 43C): its stage, raw bytecode/GLSL,
/// and the indices into the effect's constant-buffer list it binds.
/// </summary>
public sealed record MgfxShaderRecord(
    int Index,
    bool IsVertex,
    byte[] Bytecode,
    IReadOnlyList<int> ConstantBufferIndices);

/// <summary>One shader-record sampler entry, including its baked state (Phase 43, F9).</summary>
public sealed record MgfxSamplerRecord(
    int    ShaderIndex,
    byte   Type,
    byte   TextureSlot,
    byte   SamplerSlot,
    string Name,
    byte   Parameter,
    MgfxSamplerStateRecord? State);

/// <summary>The baked sampler state, field-for-field as MonoGame 3.8.2's Shader reader consumes it.</summary>
public sealed record MgfxSamplerStateRecord(
    byte  AddressU,
    byte  AddressV,
    byte  AddressW,
    byte  BorderColorR, byte BorderColorG, byte BorderColorB, byte BorderColorA,
    byte  Filter,
    int   MaxAnisotropy,
    int   MaxMipLevel,
    float MipMapLevelOfDetailBias);

/// <summary>
/// One parameter record as stored in the .mgfx parameter block — the reflection metadata
/// MonoGame's <c>EffectReader</c> builds <c>EffectParameter</c> from. Captured for the
/// Phase 27 <c>MgfxParameterMatchTests</c> golden comparison (Phase 5 §9.3.1/§9.3.2).
/// <see cref="Elements"/>/<see cref="Members"/> are the RECURSIVE sub-parameter
/// collections MonoGame 3.8.2 reads (elements first, then struct members — Phase 43
/// F6); an array parameter's elements are full nested records, not indices.
/// </summary>
public sealed record MgfxParameterRecord(
    string Name,
    string Semantic,
    byte   Class,
    byte   Type,
    byte   Rows,
    byte   Columns,
    int    MemberCount,
    int    ElementCount,
    IReadOnlyList<MgfxParameterRecord> Elements,
    IReadOnlyList<MgfxParameterRecord> Members);

public sealed class MgfxBlobReader
{
    // The four bytes "MGFX" read little-endian as a uint32.
    private const uint ExpectedSignature = 0x5846474Du;

    public string   Signature            { get; }
    public byte     MgfxVersion          { get; }
    public byte     ProfileId            { get; }
    public int      TechniqueCount       { get; }
    public IReadOnlyList<TechniqueInfo>  Techniques           { get; }
    public int      TotalShaderBlobCount { get; }
    public IReadOnlyList<string>         ParameterNames       { get; }

    // Full per-parameter reflection metadata, in parameter-block order.
    public IReadOnlyList<MgfxParameterRecord> Parameters { get; }

    // Raw shader blob bytes indexed by shader slot.
    public IReadOnlyList<byte[]> ShaderBlobs { get; }

    // Full constant-buffer records and per-shader stage/bindings (Phase 43C).
    public IReadOnlyList<MgfxConstantBufferRecord> ConstantBuffers { get; private set; } = [];
    public IReadOnlyList<MgfxShaderRecord>         Shaders         { get; private set; } = [];

    // Every sampler entry across all shader records, with baked state (Phase 43, F9).
    public IReadOnlyList<MgfxSamplerRecord> Samplers { get; }

    // Render state records read from the LAST pass (in any technique) that carried them.
    // Per-pass values are on PassInfo; these are conveniences for single-pass fixtures.
    public MgfxBlendStateRecord?        BlendState        { get; }
    public MgfxDepthStencilStateRecord? DepthStencilState { get; }
    public MgfxRasterizerStateRecord?   RasterizerState   { get; }

    // Convenience views over the records above.
    public bool? DepthBufferEnable => DepthStencilState?.DepthBufferEnable;
    public int?  CullMode          => RasterizerState?.CullMode;

    // paramName -> annotation count. MGFX v10 stores ONLY the count — MonoGame's
    // ReadAnnotations never reads bodies, and mgfxc never writes them (Phase 43, F2).
    public IReadOnlyDictionary<string, int> ParameterAnnotationCounts { get; }

    // paramName -> variable size in bytes computed from constant-buffer offset data.
    public IReadOnlyDictionary<string, int> ParameterSizes { get; }

    // paramName -> constant-buffer byte offset (the value stored verbatim in
    // the mgfx cbuffer offset table). For uniform-array-indexed dialects like
    // MojoShader (ps_uniforms_vec4[N]), the array index is Offset / 16.
    public IReadOnlyDictionary<string, int> ParameterOffsets { get; }

    private MgfxBlobReader(
        string signature,
        byte version,
        byte profileId,
        IReadOnlyList<TechniqueInfo> techniques,
        IReadOnlyList<byte[]> shaderBlobs,
        IReadOnlyList<MgfxSamplerRecord> samplers,
        IReadOnlyList<string> parameterNames,
        IReadOnlyList<MgfxParameterRecord> parameters,
        MgfxBlendStateRecord? blendState,
        MgfxDepthStencilStateRecord? depthStencilState,
        MgfxRasterizerStateRecord? rasterizerState,
        IReadOnlyDictionary<string, int> parameterAnnotationCounts,
        IReadOnlyDictionary<string, int> parameterSizes,
        IReadOnlyDictionary<string, int> parameterOffsets)
    {
        Signature            = signature;
        MgfxVersion          = version;
        ProfileId            = profileId;
        Techniques           = techniques;
        TechniqueCount       = techniques.Count;
        ShaderBlobs          = shaderBlobs;
        Samplers             = samplers;
        TotalShaderBlobCount = shaderBlobs.Count;
        ParameterNames       = parameterNames;
        Parameters           = parameters;
        BlendState           = blendState;
        DepthStencilState    = depthStencilState;
        RasterizerState      = rasterizerState;
        ParameterAnnotationCounts = parameterAnnotationCounts;
        ParameterSizes       = parameterSizes;
        ParameterOffsets     = parameterOffsets;
    }

    public static MgfxBlobReader Parse(byte[] blob)
    {
        using var ms = new MemoryStream(blob);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

        // Header
        long sigOffset = ms.Position;
        uint sig = br.ReadUInt32();
        if (sig != ExpectedSignature)
            throw new InvalidDataException($"Invalid MGFX signature 0x{sig:X8} at offset {sigOffset}. Expected 0x{ExpectedSignature:X8}.");

        byte version   = br.ReadByte();
        byte profileId = br.ReadByte();
        br.ReadInt32(); // EffectKey (MonoGame effect-cache key)

        // Constant buffers — parse to build per-variable sizes and offsets.
        // Variable size = (nextVariable.offset - thisVariable.offset), or (cbSize - thisVariable.offset) for the last variable.
        // paramIndex -> sizeInBytes; paramIndex -> startOffset.
        var cbParamIndexToSize   = new Dictionary<int, int>();
        var cbParamIndexToOffset = new Dictionary<int, int>();
        var cbRecords            = new List<MgfxConstantBufferRecord>();
        int cbCount = br.ReadInt32();
        for (int i = 0; i < cbCount; i++)
        {
            string cbName     = br.ReadString();
            short  cbSize     = br.ReadInt16();
            int    paramCount = br.ReadInt32();

            // Interleaved: per parameter int32 index then uint16 offset.
            var paramIndices = new int[paramCount];
            var offsets      = new int[paramCount];
            for (int j = 0; j < paramCount; j++)
            {
                paramIndices[j] = br.ReadInt32();
                offsets[j]      = br.ReadUInt16();
            }

            cbRecords.Add(new MgfxConstantBufferRecord(cbName, cbSize, paramIndices, offsets));

            for (int j = 0; j < paramCount; j++)
            {
                int varSize = j < paramCount - 1
                    ? offsets[j + 1] - offsets[j]
                    : cbSize - offsets[j];
                cbParamIndexToSize[paramIndices[j]]   = varSize;
                cbParamIndexToOffset[paramIndices[j]] = offsets[j];
            }
        }

        // Shaders
        int shaderCount = br.ReadInt32();
        var shaderBlobs = new List<byte[]>(shaderCount);
        var shaderRecords = new List<MgfxShaderRecord>(shaderCount);
        var samplerRecords = new List<MgfxSamplerRecord>();
        for (int i = 0; i < shaderCount; i++)
        {
            bool isVertex = br.ReadBoolean();
            int byteLen = br.ReadInt32();
            shaderBlobs.Add(br.ReadBytes(byteLen));

            // Sampler table.
            int samplerCount = br.ReadByte();
            for (int s = 0; s < samplerCount; s++)
            {
                byte sType       = br.ReadByte();
                byte textureSlot = br.ReadByte();
                byte samplerSlot = br.ReadByte();
                MgfxSamplerStateRecord? state = null;
                if (br.ReadBoolean())  // hasState (Phase 43, F9)
                {
                    state = new MgfxSamplerStateRecord(
                        AddressU: br.ReadByte(),
                        AddressV: br.ReadByte(),
                        AddressW: br.ReadByte(),
                        BorderColorR: br.ReadByte(), BorderColorG: br.ReadByte(),
                        BorderColorB: br.ReadByte(), BorderColorA: br.ReadByte(),
                        Filter: br.ReadByte(),
                        MaxAnisotropy: br.ReadInt32(),
                        MaxMipLevel: br.ReadInt32(),
                        MipMapLevelOfDetailBias: br.ReadSingle());
                }
                string sName = br.ReadString();
                byte sParam  = br.ReadByte();
                samplerRecords.Add(new MgfxSamplerRecord(
                    ShaderIndex: i, Type: sType, TextureSlot: textureSlot,
                    SamplerSlot: samplerSlot, Name: sName, Parameter: sParam, State: state));
            }

            // Constant-buffer index list.
            int cbIndexCount = br.ReadByte();
            var cbIndices = new List<int>(cbIndexCount);
            for (int c = 0; c < cbIndexCount; c++)
                cbIndices.Add(br.ReadByte());

            // Vertex-attribute table.
            int attrCount = br.ReadByte();
            for (int a = 0; a < attrCount; a++)
            {
                br.ReadString();       // name
                br.ReadByte();         // usage
                br.ReadByte();         // index
                br.ReadInt16();        // location
            }

            shaderRecords.Add(new MgfxShaderRecord(i, isVertex, shaderBlobs[i], cbIndices));
        }

        // Parameters — MonoGame 3.8.2's recursive layout: elements then struct
        // members, each a full nested parameter record (Phase 43 F6).
        var paramAnnotationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var parameters     = ReadParameterList(br, paramAnnotationCounts);
        var parameterNames = parameters.Select(p => p.Name).ToList();

        // Build ParameterSizes and ParameterOffsets from cb tables.
        var paramSizes   = new Dictionary<string, int>(StringComparer.Ordinal);
        var paramOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < parameterNames.Count; i++)
        {
            if (cbParamIndexToSize.TryGetValue(i, out int cbSz))
                paramSizes[parameterNames[i]] = cbSz;
            if (cbParamIndexToOffset.TryGetValue(i, out int cbOff))
                paramOffsets[parameterNames[i]] = cbOff;
        }

        // Techniques
        MgfxBlendStateRecord?        blendState        = null;
        MgfxDepthStencilStateRecord? depthStencilState = null;
        MgfxRasterizerStateRecord?   rasterizerState   = null;

        int techniqueCount = br.ReadInt32();
        var techniques     = new List<TechniqueInfo>(techniqueCount);

        for (int t = 0; t < techniqueCount; t++)
        {
            string techName = br.ReadString();
            ReadAnnotations(br); // technique annotations (count only)

            int passCount = br.ReadInt32();
            var passes    = new List<PassInfo>(passCount);
            for (int p = 0; p < passCount; p++)
            {
                string passName = br.ReadString();
                ReadAnnotations(br); // pass annotations (count only)

                int vsIndex = br.ReadInt32();
                int psIndex = br.ReadInt32();

                var (blend, depth, raster) = ReadRenderStateBlock(br);
                passes.Add(new PassInfo(passName, vsIndex, psIndex, blend, depth, raster));

                if (blend  is not null) blendState        = blend;
                if (depth  is not null) depthStencilState = depth;
                if (raster is not null) rasterizerState   = raster;
            }

            techniques.Add(new TechniqueInfo(techName, passCount, passes));
        }

        // Tail signature — the same desync guard MonoGame's Effect ctor applies. If a
        // writer change ever desyncs the stream (the F1/F2 failure mode), this throws
        // here instead of silently mis-parsing.
        uint tail = br.ReadUInt32();
        if (tail != ExpectedSignature)
            throw new InvalidDataException(
                $"MGFX tail signature mismatch (0x{tail:X8}) — the body was not parsed correctly.");

        return new MgfxBlobReader(
            signature: "MGFX",
            version: version,
            profileId: profileId,
            techniques: techniques,
            shaderBlobs: shaderBlobs,
            samplers: samplerRecords,
            parameterNames: parameterNames,
            parameters: parameters,
            blendState: blendState,
            depthStencilState: depthStencilState,
            rasterizerState: rasterizerState,
            parameterAnnotationCounts: paramAnnotationCounts,
            parameterSizes: paramSizes,
            parameterOffsets: paramOffsets)
        {
            ConstantBuffers = cbRecords,
            Shaders         = shaderRecords,
        };
    }

    // Mirrors MonoGame 3.8.2 Effect.ReadPasses: each state object is a boolean
    // presence flag followed by a FIXED alphabetical field sequence (Phase 43, F1).
    private static (MgfxBlendStateRecord?, MgfxDepthStencilStateRecord?, MgfxRasterizerStateRecord?)
        ReadRenderStateBlock(BinaryReader br)
    {
        MgfxBlendStateRecord? blend = null;
        if (br.ReadBoolean())
        {
            blend = new MgfxBlendStateRecord(
                AlphaBlendFunction:    br.ReadByte(),
                AlphaDestinationBlend: br.ReadByte(),
                AlphaSourceBlend:      br.ReadByte(),
                BlendFactorR: br.ReadByte(), BlendFactorG: br.ReadByte(),
                BlendFactorB: br.ReadByte(), BlendFactorA: br.ReadByte(),
                ColorBlendFunction:    br.ReadByte(),
                ColorDestinationBlend: br.ReadByte(),
                ColorSourceBlend:      br.ReadByte(),
                ColorWriteChannels:    br.ReadByte(),
                ColorWriteChannels1:   br.ReadByte(),
                ColorWriteChannels2:   br.ReadByte(),
                ColorWriteChannels3:   br.ReadByte(),
                MultiSampleMask:       br.ReadInt32());
        }

        MgfxDepthStencilStateRecord? depth = null;
        if (br.ReadBoolean())
        {
            depth = new MgfxDepthStencilStateRecord(
                CounterClockwiseStencilDepthBufferFail: br.ReadByte(),
                CounterClockwiseStencilFail:            br.ReadByte(),
                CounterClockwiseStencilFunction:        br.ReadByte(),
                CounterClockwiseStencilPass:            br.ReadByte(),
                DepthBufferEnable:      br.ReadBoolean(),
                DepthBufferFunction:    br.ReadByte(),
                DepthBufferWriteEnable: br.ReadBoolean(),
                ReferenceStencil:       br.ReadInt32(),
                StencilDepthBufferFail: br.ReadByte(),
                StencilEnable:          br.ReadBoolean(),
                StencilFail:            br.ReadByte(),
                StencilFunction:        br.ReadByte(),
                StencilMask:            br.ReadInt32(),
                StencilPass:            br.ReadByte(),
                StencilWriteMask:       br.ReadInt32(),
                TwoSidedStencilMode:    br.ReadBoolean());
        }

        MgfxRasterizerStateRecord? raster = null;
        if (br.ReadBoolean())
        {
            raster = new MgfxRasterizerStateRecord(
                CullMode:             br.ReadByte(),
                DepthBias:            br.ReadSingle(),
                FillMode:             br.ReadByte(),
                MultiSampleAntiAlias: br.ReadBoolean(),
                ScissorTestEnable:    br.ReadBoolean(),
                SlopeScaleDepthBias:  br.ReadSingle());
        }

        return (blend, depth, raster);
    }

    /// <summary>
    /// MGFX v10 annotations are an int32 count and NOTHING else — MonoGame's
    /// ReadAnnotations reads only the count ("TODO: Annotations are not implemented!")
    /// and mgfxc writes no bodies (Phase 43, F2).
    /// </summary>
    private static int ReadAnnotations(BinaryReader br) => br.ReadInt32();

    /// <summary>
    /// Mirrors MonoGame 3.8.2 <c>Effect.ReadParameters</c>: an int32 count of full
    /// parameter records; per record the ELEMENTS sub-collection is read first, then
    /// the struct MEMBERS, recursively; a value-typed leaf (Scalar/Vector/Matrix,
    /// no elements/members) carries a rows*cols*4-byte default-value blob.
    /// </summary>
    private static List<MgfxParameterRecord> ReadParameterList(
        BinaryReader br, Dictionary<string, int>? annotationCounts = null)
    {
        int count = br.ReadInt32();
        var list = new List<MgfxParameterRecord>(count);
        for (int i = 0; i < count; i++)
        {
            byte paramClass = br.ReadByte();
            byte paramType  = br.ReadByte();
            string name     = br.ReadString();
            string semantic = br.ReadString();

            int annotationCount = ReadAnnotations(br);
            if (annotationCounts is not null && annotationCount > 0)
                annotationCounts[name] = annotationCount;

            byte rowCount    = br.ReadByte();
            byte columnCount = br.ReadByte();

            var elements = ReadParameterList(br);
            var members  = ReadParameterList(br);

            if (paramClass <= 2 && elements.Count == 0 && members.Count == 0)
                br.ReadBytes(rowCount * columnCount * 4);

            list.Add(new MgfxParameterRecord(
                Name: name,
                Semantic: semantic,
                Class: paramClass,
                Type: paramType,
                Rows: rowCount,
                Columns: columnCount,
                MemberCount: members.Count,
                ElementCount: elements.Count,
                Elements: elements,
                Members: members));
        }

        return list;
    }
}
