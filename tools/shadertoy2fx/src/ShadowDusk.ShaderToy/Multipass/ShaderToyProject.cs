using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowDusk.ShaderToy.Multipass;

// ─────────────────────────────────────────────────────────────────────────────
// ShaderToy multi-tab EXPORT model (the ShaderToy API JSON).
//
// Top level: { "ver": string, "info": {...}, "renderpass": [ ... ] }
// Each renderpass: name, type (image|buffer|common|sound|cubemap), description?, code,
//                  inputs[], outputs[].
// Each input:  id, src, ctype (texture|buffer|keyboard|music|...), channel (0..3), sampler{...}.
// Each output: id, channel.
//
// This is a parse-only model: it accepts the export shape and exposes a strongly-typed
// ShaderToyProject. It performs NO GLSL translation (that is MultipassConverter's job, which
// delegates to the EXISTING single-pass ShaderToyConverter for each tab's code).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The kind of a ShaderToy render pass (the <c>type</c> field of a renderpass).</summary>
public enum ShaderToyPassType
{
    /// <summary>The final <c>Image</c> pass (renders to the screen).</summary>
    Image,

    /// <summary>A <c>Buffer A</c>..<c>Buffer D</c> offscreen pass (renders to a render target).</summary>
    Buffer,

    /// <summary>The shared <c>Common</c> tab (code prepended to every other pass; never rendered).</summary>
    Common,

    /// <summary>A <c>Sound</c> pass (audio; out of v1 scope).</summary>
    Sound,

    /// <summary>A <c>Cubemap</c> pass (out of v1 scope).</summary>
    Cubemap,
}

/// <summary>The kind of an input channel (the <c>ctype</c> field of an input).</summary>
public enum ShaderToyChannelType
{
    /// <summary>An external image/media texture (<c>texture</c>).</summary>
    Texture,

    /// <summary>The output of another (or this) render pass (<c>buffer</c>).</summary>
    Buffer,

    /// <summary>The keyboard input texture (<c>keyboard</c>) — unsupported in v1.</summary>
    Keyboard,

    /// <summary>A music track (<c>music</c>) — unsupported in v1.</summary>
    Music,

    /// <summary>A music stream / SoundCloud (<c>musicstream</c>) — unsupported in v1.</summary>
    MusicStream,

    /// <summary>Microphone input (<c>mic</c>) — unsupported in v1.</summary>
    Mic,

    /// <summary>A webcam feed (<c>webcam</c>) — unsupported in v1.</summary>
    Webcam,

    /// <summary>A 3D volume texture (<c>volume</c>) — unsupported in v1.</summary>
    Volume,

    /// <summary>A cubemap texture (<c>cubemap</c>) — unsupported in v1.</summary>
    Cubemap,

    /// <summary>A video texture (<c>video</c>) — unsupported in v1.</summary>
    Video,
}

/// <summary>Per-channel sampler state (the <c>sampler</c> object of an input).</summary>
/// <param name="Filter">Filter mode (e.g. <c>nearest</c>, <c>linear</c>, <c>mipmap</c>).</param>
/// <param name="Wrap">Wrap mode (e.g. <c>clamp</c>, <c>repeat</c>).</param>
/// <param name="VFlip">Whether the texture is vertically flipped.</param>
/// <param name="Srgb">Whether the texture is sampled as sRGB.</param>
/// <param name="Internal">ShaderToy's internal sampler tag (e.g. <c>byte</c>).</param>
public sealed record ShaderToySampler(
    string Filter,
    string Wrap,
    bool VFlip,
    bool Srgb,
    string Internal);

/// <summary>An input channel binding on a render pass (the entries of <c>inputs[]</c>).</summary>
/// <param name="Id">
/// The input id. For a <c>buffer</c> input this EQUALS some renderpass's <c>outputs[].id</c> —
/// that pass is the source buffer for this channel. For a <c>texture</c> input it is the media id.
/// </param>
/// <param name="Src">The source URL / path of the media (for a <c>texture</c> input), if any.</param>
/// <param name="Ctype">The channel kind.</param>
/// <param name="Channel">The channel index 0..3 this input binds to (<c>iChannel0..3</c>).</param>
/// <param name="Sampler">The sampler state, if present.</param>
public sealed record ShaderToyInput(
    string Id,
    string? Src,
    ShaderToyChannelType Ctype,
    int Channel,
    ShaderToySampler? Sampler);

/// <summary>An output of a render pass (the entries of <c>outputs[]</c>).</summary>
/// <param name="Id">The output id (matched by a downstream pass's <c>buffer</c> input <c>id</c>).</param>
/// <param name="Channel">The output channel index.</param>
public sealed record ShaderToyOutput(string Id, int Channel);

/// <summary>One render pass (one entry of <c>renderpass[]</c>).</summary>
/// <param name="Name">The pass name (e.g. <c>Image</c>, <c>Buffer A</c>, <c>Common</c>).</param>
/// <param name="Type">The pass kind.</param>
/// <param name="Description">The pass description, if any.</param>
/// <param name="Code">The pass GLSL source.</param>
/// <param name="Inputs">The input channel bindings.</param>
/// <param name="Outputs">The pass outputs.</param>
public sealed record ShaderToyPass(
    string Name,
    ShaderToyPassType Type,
    string? Description,
    string Code,
    IReadOnlyList<ShaderToyInput> Inputs,
    IReadOnlyList<ShaderToyOutput> Outputs);

/// <summary>
/// A parsed ShaderToy multi-tab export. Use <see cref="Parse(string)"/> /
/// <see cref="TryParse(string, out ShaderToyProject?, out string?)"/> to build one from the export JSON.
/// </summary>
/// <param name="Version">The export <c>ver</c> field, if any.</param>
/// <param name="Name">The shader name (from <c>info.name</c>), if any.</param>
/// <param name="Passes">The render passes, in the order they appear in the export.</param>
public sealed record ShaderToyProject(
    string? Version,
    string? Name,
    IReadOnlyList<ShaderToyPass> Passes)
{
    /// <summary>The single <c>Common</c> pass, or <c>null</c> when the export has none.</summary>
    public ShaderToyPass? Common =>
        Passes.FirstOrDefault(p => p.Type == ShaderToyPassType.Common);

    /// <summary>
    /// Parse an export JSON into a <see cref="ShaderToyProject"/>. Throws
    /// <see cref="JsonException"/> on malformed JSON or a structurally-invalid export.
    /// </summary>
    /// <param name="json">The ShaderToy export JSON text.</param>
    public static ShaderToyProject Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ExportRoot? root = JsonSerializer.Deserialize<ExportRoot>(json, SerializerOptions)
            ?? throw new JsonException("ShaderToy export JSON deserialized to null.");

        if (root.RenderPass is null || root.RenderPass.Count == 0)
            throw new JsonException("ShaderToy export has no 'renderpass' array (or it is empty).");

        var passes = new List<ShaderToyPass>(root.RenderPass.Count);
        foreach (ExportPass p in root.RenderPass)
            passes.Add(MapPass(p));

        return new ShaderToyProject(root.Ver, root.Info?.Name, passes);
    }

    /// <summary>
    /// Try to parse an export JSON. Returns <c>false</c> with an <paramref name="error"/> message
    /// instead of throwing on malformed input.
    /// </summary>
    public static bool TryParse(string json, out ShaderToyProject? project, out string? error)
    {
        try
        {
            project = Parse(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentNullException or NotSupportedException)
        {
            project = null;
            error = ex.Message;
            return false;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static ShaderToyPass MapPass(ExportPass p)
    {
        ShaderToyPassType type = ParsePassType(p.Type);

        var inputs = new List<ShaderToyInput>();
        if (p.Inputs is not null)
        {
            foreach (ExportInput input in p.Inputs)
                inputs.Add(MapInput(input));
        }

        var outputs = new List<ShaderToyOutput>();
        if (p.Outputs is not null)
        {
            foreach (ExportOutput output in p.Outputs)
                outputs.Add(new ShaderToyOutput(output.Id ?? string.Empty, output.Channel));
        }

        return new ShaderToyPass(
            p.Name ?? type.ToString(),
            type,
            p.Description,
            p.Code ?? string.Empty,
            inputs,
            outputs);
    }

    private static ShaderToyInput MapInput(ExportInput input)
    {
        ShaderToyChannelType ctype = ParseChannelType(input.Ctype);
        ShaderToySampler? sampler = input.Sampler is null
            ? null
            : new ShaderToySampler(
                input.Sampler.Filter ?? string.Empty,
                input.Sampler.Wrap ?? string.Empty,
                ParseBool(input.Sampler.VFlip),
                ParseBool(input.Sampler.Srgb),
                input.Sampler.Internal ?? string.Empty);

        return new ShaderToyInput(input.Id ?? string.Empty, input.Src, ctype, input.Channel, sampler);
    }

    // ShaderToy's sampler booleans are exported as the strings "true"/"false" (sometimes as bools).
    private static bool ParseBool(string? value) =>
        value is not null && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static ShaderToyPassType ParsePassType(string? type) =>
        (type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "image" => ShaderToyPassType.Image,
            "buffer" => ShaderToyPassType.Buffer,
            "common" => ShaderToyPassType.Common,
            "sound" => ShaderToyPassType.Sound,
            "cubemap" => ShaderToyPassType.Cubemap,
            var other => throw new JsonException($"Unknown renderpass type '{other}'."),
        };

    private static ShaderToyChannelType ParseChannelType(string? ctype) =>
        (ctype ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "texture" => ShaderToyChannelType.Texture,
            "buffer" => ShaderToyChannelType.Buffer,
            "keyboard" => ShaderToyChannelType.Keyboard,
            "music" => ShaderToyChannelType.Music,
            "musicstream" => ShaderToyChannelType.MusicStream,
            "mic" => ShaderToyChannelType.Mic,
            "webcam" => ShaderToyChannelType.Webcam,
            "volume" => ShaderToyChannelType.Volume,
            "cubemap" => ShaderToyChannelType.Cubemap,
            "video" => ShaderToyChannelType.Video,
            var other => throw new JsonException($"Unknown input ctype '{other}'."),
        };

    // ── Wire DTOs (the raw JSON shapes; mapped to the public model above) ──────

    private sealed class ExportRoot
    {
        [JsonPropertyName("ver")] public string? Ver { get; set; }
        [JsonPropertyName("info")] public ExportInfo? Info { get; set; }
        [JsonPropertyName("renderpass")] public List<ExportPass>? RenderPass { get; set; }
    }

    private sealed class ExportInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class ExportPass
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("inputs")] public List<ExportInput>? Inputs { get; set; }
        [JsonPropertyName("outputs")] public List<ExportOutput>? Outputs { get; set; }
    }

    private sealed class ExportInput
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("src")] public string? Src { get; set; }
        [JsonPropertyName("ctype")] public string? Ctype { get; set; }
        [JsonPropertyName("channel")] public int Channel { get; set; }
        [JsonPropertyName("sampler")] public ExportSampler? Sampler { get; set; }
    }

    private sealed class ExportSampler
    {
        [JsonPropertyName("filter")] public string? Filter { get; set; }
        [JsonPropertyName("wrap")] public string? Wrap { get; set; }
        [JsonPropertyName("vflip")] public string? VFlip { get; set; }
        [JsonPropertyName("srgb")] public string? Srgb { get; set; }
        [JsonPropertyName("internal")] public string? Internal { get; set; }
    }

    private sealed class ExportOutput
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("channel")] public int Channel { get; set; }
    }
}
