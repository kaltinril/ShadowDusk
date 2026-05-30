#nullable enable

using System.Text;

namespace ShadowDusk.Integration.Tests;

public sealed record PassInfo(string Name, int VertexShaderIndex, int PixelShaderIndex);

public sealed record TechniqueInfo(string Name, int PassCount, IReadOnlyList<PassInfo> Passes);

public sealed class MgfxBlobReader
{
    // The four bytes "MGFX" read little-endian as a uint32.
    private const uint ExpectedSignature = 0x5846474Du;

    private const byte TypeSingle = 3;
    private const byte TypeInt32  = 2;
    private const byte TypeBool   = 1;

    // Blend state field IDs
    private const byte BlendAlphaBlendEnable = 0;

    // Depth-stencil state field IDs
    private const byte DepthDepthBufferEnable = 0;

    // Rasterizer state field IDs
    private const byte RasterizerCullMode = 0;

    public string   Signature            { get; }
    public byte     MgfxVersion          { get; }
    public byte     ProfileId            { get; }
    public int      TechniqueCount       { get; }
    public IReadOnlyList<TechniqueInfo>  Techniques           { get; }
    public int      TotalShaderBlobCount { get; }
    public IReadOnlyList<string>         ParameterNames       { get; }

    // Raw shader blob bytes indexed by shader slot.
    public IReadOnlyList<byte[]> ShaderBlobs { get; }

    // Render state values read from any pass in any technique.
    public bool? AlphaBlendEnable  { get; }
    public bool? DepthBufferEnable { get; }
    public int?  CullMode          { get; }

    // paramName -> list of (annotationName, annotationStringValue) for string-type annotations.
    public IReadOnlyDictionary<string, IReadOnlyList<(string Name, string StringValue)>> ParameterAnnotations { get; }

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
        IReadOnlyList<string> parameterNames,
        bool? alphaBlendEnable,
        bool? depthBufferEnable,
        int? cullMode,
        IReadOnlyDictionary<string, IReadOnlyList<(string, string)>> parameterAnnotations,
        IReadOnlyDictionary<string, int> parameterSizes,
        IReadOnlyDictionary<string, int> parameterOffsets)
    {
        Signature            = signature;
        MgfxVersion          = version;
        ProfileId            = profileId;
        Techniques           = techniques;
        TechniqueCount       = techniques.Count;
        ShaderBlobs          = shaderBlobs;
        TotalShaderBlobCount = shaderBlobs.Count;
        ParameterNames       = parameterNames;
        AlphaBlendEnable     = alphaBlendEnable;
        DepthBufferEnable    = depthBufferEnable;
        CullMode             = cullMode;
        ParameterAnnotations = parameterAnnotations;
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
        for (int i = 0; i < shaderCount; i++)
        {
            br.ReadBoolean();          // isVertexShader
            int byteLen = br.ReadInt32();
            shaderBlobs.Add(br.ReadBytes(byteLen));

            // Sampler table.
            int samplerCount = br.ReadByte();
            for (int s = 0; s < samplerCount; s++)
            {
                br.ReadByte();         // type
                br.ReadByte();         // textureSlot
                br.ReadByte();         // samplerSlot
                if (br.ReadBoolean())  // hasState
                {
                    br.ReadBytes(3);   // AddressU/V/W
                    br.ReadBytes(4);   // BorderColor RGBA
                    br.ReadByte();     // Filter
                    br.ReadInt32();    // MaxAnisotropy
                    br.ReadInt32();    // MaxMipLevel
                    br.ReadSingle();   // MipMapLevelOfDetailBias
                }
                br.ReadString();       // name
                br.ReadByte();         // parameter index
            }

            // Constant-buffer index list.
            int cbIndexCount = br.ReadByte();
            for (int c = 0; c < cbIndexCount; c++)
                br.ReadByte();

            // Vertex-attribute table.
            int attrCount = br.ReadByte();
            for (int a = 0; a < attrCount; a++)
            {
                br.ReadString();       // name
                br.ReadByte();         // usage
                br.ReadByte();         // index
                br.ReadInt16();        // location
            }
        }

        // Parameters
        int parameterCount = br.ReadInt32();
        var parameterNames = new List<string>(parameterCount);
        var paramAnnotationMap = new Dictionary<string, IReadOnlyList<(string, string)>>(StringComparer.Ordinal);

        for (int i = 0; i < parameterCount; i++)
        {
            byte paramClass = br.ReadByte(); // class
            br.ReadByte(); // type
            string name     = br.ReadString();
            string semantic = br.ReadString();

            var annotations = ReadAnnotations(br);
            parameterNames.Add(name);

            // Include all annotations that were stored as strings (any non-numeric type).
            var stringAnnotations = annotations
                .Where(a => a.Type != TypeSingle && a.Type != TypeInt32 && a.Type != TypeBool)
                .Select(a => (a.Name, a.StringValue ?? string.Empty))
                .ToList();

            if (stringAnnotations.Count > 0)
                paramAnnotationMap[name] = stringAnnotations;

            byte rowCount    = br.ReadByte();
            byte columnCount = br.ReadByte();
            int memberCount  = ReadInt32List(br); // memberIndices
            int elementCount = ReadInt32List(br); // elementIndices

            // Value-typed params (Scalar/Vector/Matrix, no members/elements) carry
            // a raw default-value blob of rowCount*columnCount*4 bytes, no prefix.
            if (paramClass <= 2 && memberCount == 0 && elementCount == 0)
                br.ReadBytes(rowCount * columnCount * 4);
        }

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
        bool? alphaBlendEnable  = null;
        bool? depthBufferEnable = null;
        int?  cullMode          = null;

        int techniqueCount = br.ReadInt32();
        var techniques     = new List<TechniqueInfo>(techniqueCount);

        for (int t = 0; t < techniqueCount; t++)
        {
            string techName = br.ReadString();
            ReadAnnotations(br); // technique annotations (consume)

            int passCount = br.ReadInt32();
            var passes    = new List<PassInfo>(passCount);
            for (int p = 0; p < passCount; p++)
            {
                string passName = br.ReadString();
                ReadAnnotations(br); // pass annotations

                int vsIndex = br.ReadInt32();
                int psIndex = br.ReadInt32();
                passes.Add(new PassInfo(passName, vsIndex, psIndex));

                var (ab, db, cm) = ReadRenderStateBlock(br);
                if (ab.HasValue) alphaBlendEnable  = ab;
                if (db.HasValue) depthBufferEnable = db;
                if (cm.HasValue) cullMode          = cm;
            }

            techniques.Add(new TechniqueInfo(techName, passCount, passes));
        }

        return new MgfxBlobReader(
            signature: "MGFX",
            version: version,
            profileId: profileId,
            techniques: techniques,
            shaderBlobs: shaderBlobs,
            parameterNames: parameterNames,
            alphaBlendEnable: alphaBlendEnable,
            depthBufferEnable: depthBufferEnable,
            cullMode: cullMode,
            parameterAnnotations: paramAnnotationMap,
            parameterSizes: paramSizes,
            parameterOffsets: paramOffsets);
    }

    private static (bool? AlphaBlendEnable, bool? DepthBufferEnable, int? CullMode) ReadRenderStateBlock(BinaryReader br)
    {
        bool? alphaBlend = null;
        bool? depthBuf   = null;
        int?  cull       = null;

        // BlendState
        byte blendPresent = br.ReadByte();
        if (blendPresent == 1)
        {
            while (true)
            {
                byte fieldId = br.ReadByte();
                if (fieldId == 0xFF) break;
                int value = br.ReadInt32();
                if (fieldId == BlendAlphaBlendEnable)
                    alphaBlend = value != 0;
            }
        }

        // DepthStencilState
        byte depthPresent = br.ReadByte();
        if (depthPresent == 1)
        {
            while (true)
            {
                byte fieldId = br.ReadByte();
                if (fieldId == 0xFF) break;
                int value = br.ReadInt32();
                if (fieldId == DepthDepthBufferEnable)
                    depthBuf = value != 0;
            }
        }

        // RasterizerState
        byte rastPresent = br.ReadByte();
        if (rastPresent == 1)
        {
            while (true)
            {
                byte fieldId = br.ReadByte();
                if (fieldId == 0xFF) break;
                int value = br.ReadInt32();
                if (fieldId == RasterizerCullMode)
                    cull = value;
            }
        }

        return (alphaBlend, depthBuf, cull);
    }

    private sealed record ParsedAnnotation(string Name, byte Type, string? StringValue);

    private static List<ParsedAnnotation> ReadAnnotations(BinaryReader br)
    {
        int count = br.ReadInt32();
        var list  = new List<ParsedAnnotation>(count);

        for (int i = 0; i < count; i++)
        {
            string name = br.ReadString();
            byte   type = br.ReadByte();

            string? stringValue = null;
            switch (type)
            {
                case TypeSingle:
                    br.ReadSingle();
                    break;
                case TypeInt32:
                    br.ReadInt32();
                    break;
                case TypeBool:
                    br.ReadInt32();
                    break;
                default:
                    stringValue = br.ReadString();
                    break;
            }

            list.Add(new ParsedAnnotation(name, type, stringValue));
        }

        return list;
    }

    private static int ReadInt32List(BinaryReader br)
    {
        int count = br.ReadInt32();
        for (int i = 0; i < count; i++)
            br.ReadInt32();
        return count;
    }
}
