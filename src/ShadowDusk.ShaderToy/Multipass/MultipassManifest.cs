using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowDusk.ShaderToy.Multipass;

// ─────────────────────────────────────────────────────────────────────────────
// Emits the two consumer-facing artifacts of a multipass conversion:
//   * manifest.json — machine-readable: ordered passes, each pass's .fx file +
//     channel→source wiring + feedback flags + sampler modes.
//   * WIRING.md     — human-readable: the buffer graph + a concrete ~15-line
//     MonoGame RenderTarget2D wiring example (the Draw loop the CONSUMER writes;
//     this tool does NOT orchestrate it).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Serializes a <see cref="MultipassResult"/> to the <c>manifest.json</c> + <c>WIRING.md</c> artifacts.</summary>
public static class MultipassManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Build the machine-readable manifest JSON text for a conversion result.</summary>
    public static string ToJson(MultipassResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var manifest = new ManifestRoot
        {
            ProjectName = result.ProjectName,
            HasFeedback = result.HasFeedback,
            ExecutionOrder = result.Passes.Select(p => p.Name).ToList(),
            Passes = result.Passes.Select(ToManifestPass).ToList(),
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static ManifestPass ToManifestPass(MultipassPassResult pass) => new()
    {
        Name = pass.Name,
        OutputFile = pass.OutputFileName,
        Converted = pass.Success,
        HasFeedback = pass.HasFeedback,
        UsedUniforms = pass.UsedUniforms.ToList(),
        Channels = pass.Channels.Select(c => new ManifestChannel
        {
            Channel = c.Channel,
            Source = c.Kind switch
            {
                ChannelSourceKind.BufferPass => "buffer",
                ChannelSourceKind.Feedback => "feedback",
                ChannelSourceKind.Texture => "texture",
                _ => "unsupported",
            },
            SourcePass = c.SourcePassName,
            SourceFile = c.SourceOutputFile,
            TextureSrc = c.TextureSrc,
            Feedback = c.IsFeedback,
            Wrap = string.IsNullOrEmpty(c.Wrap) ? null : c.Wrap,
            Filter = string.IsNullOrEmpty(c.Filter) ? null : c.Filter,
            Note = c.Note,
        }).ToList(),
    };

    /// <summary>
    /// Build the human-readable <c>WIRING.md</c>: the buffer graph plus a concrete, ~15-line MonoGame
    /// <c>RenderTarget2D</c> wiring example the consumer drops into their own Draw loop. This is the
    /// "what you write in your game" doc — the render-graph is the consumer's job, by design.
    /// </summary>
    public static string ToWiringMarkdown(MultipassResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine($"# Multipass wiring{(result.ProjectName is null ? string.Empty : $" — {result.ProjectName}")}");
        sb.AppendLine();
        sb.AppendLine("This export was batch-converted to **one `.fx` per render tab**. ShadowDusk does");
        sb.AppendLine("**not** orchestrate or render the multipass graph for you, by design: the render");
        sb.AppendLine("graph is the consumer's job, the way MonoGame already works. Below is the resolved");
        sb.AppendLine("buffer graph and a concrete example of the ~15 lines you drop into your own `Draw`.");
        sb.AppendLine();

        AppendExecutionOrder(sb, result);
        AppendPassTable(sb, result);
        AppendExample(sb, result);

        return sb.ToString();
    }

    private static void AppendExecutionOrder(StringBuilder sb, MultipassResult result)
    {
        sb.AppendLine("## Execution order (each frame)");
        sb.AppendLine();
        sb.AppendLine("Run the passes in this order; the last one renders to the screen:");
        sb.AppendLine();
        int i = 1;
        foreach (MultipassPassResult p in result.Passes)
        {
            string fb = p.HasFeedback ? "  _(feedback: ping-pong this buffer)_" : string.Empty;
            string screen = ReferenceEquals(p, result.Passes[^1]) ? "  → **screen**" : string.Empty;
            sb.AppendLine($"{i}. `{p.Name}` → `{p.OutputFileName}`{screen}{fb}");
            i++;
        }

        sb.AppendLine();
    }

    private static void AppendPassTable(StringBuilder sb, MultipassResult result)
    {
        sb.AppendLine("## Channel wiring");
        sb.AppendLine();
        foreach (MultipassPassResult p in result.Passes)
        {
            sb.AppendLine($"### `{p.Name}` (`{p.OutputFileName}`)");
            sb.AppendLine();
            if (p.Channels.Count == 0)
            {
                sb.AppendLine("_No input channels._");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine("| Channel | Source | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (ChannelWiring c in p.Channels)
            {
                string source = c.Kind switch
                {
                    ChannelSourceKind.BufferPass => $"buffer `{c.SourcePassName}`",
                    ChannelSourceKind.Feedback => "**feedback** (self, prev frame)",
                    ChannelSourceKind.Texture => "external texture",
                    _ => "unsupported",
                };
                string detail = c.Note ?? string.Empty;
                string sampler = (c.Wrap, c.Filter) switch
                {
                    (null or "", null or "") => string.Empty,
                    _ => $" (wrap={c.Wrap}, filter={c.Filter})",
                };
                sb.AppendLine($"| `iChannel{c.Channel}` | {source} | {detail}{sampler} |");
            }

            sb.AppendLine();
        }
    }

    private static void AppendExample(StringBuilder sb, MultipassResult result)
    {
        sb.AppendLine("## Example: the Draw loop you write");
        sb.AppendLine();
        sb.AppendLine("A concrete ~15-line sketch. Allocate a `RenderTarget2D` per buffer once, then each");
        sb.AppendLine("frame run the passes in order, binding prior outputs as `iChannelN` via");
        sb.AppendLine("`ShaderToyEffect`, ping-ponging any feedback buffer, and drawing the last pass to the");
        sb.AppendLine("screen. (ShadowDusk converts the `.fx`; this loop is yours.)");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        foreach (string line in BuildExampleLines(result))
            sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine();
    }

    /// <summary>
    /// Builds the example Draw-loop lines, tailored to this graph (buffer names, feedback ping-pong,
    /// and the actual channel bindings). Returned as lines so callers can also assert on them.
    /// </summary>
    public static IReadOnlyList<string> BuildExampleLines(MultipassResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>();
        var buffers = result.Passes.Where(p => p != result.Passes[^1]).ToList();
        MultipassPassResult image = result.Passes[^1];

        lines.Add("// --- one-time setup (allocate a render target per buffer pass) ---");
        foreach (MultipassPassResult b in buffers)
        {
            string id = VarName(b.Name);
            lines.Add($"var {id}Rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);");
            if (b.HasFeedback)
                lines.Add($"var {id}Prev = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None); // ping-pong");
        }

        lines.Add("// effects loaded once: new Effect(gd, File.ReadAllBytes(\"<Pass>.mgfx\")) wrapped in ShaderToyEffect");
        lines.Add(string.Empty);
        lines.Add("// --- each frame, in execution order ---");

        foreach (MultipassPassResult b in buffers)
        {
            string id = VarName(b.Name);
            string target = b.HasFeedback ? $"{id}Rt" : $"{id}Rt";
            lines.Add($"gd.SetRenderTarget({target});");
            foreach (ChannelWiring c in b.Channels)
                lines.Add("    " + BindLine(id, c));
            lines.Add($"{id}.SetResolution(W, H); {id}.SetTime(t); {id}.Draw();");
            if (b.HasFeedback)
                lines.Add($"(({id}Rt, {id}Prev)) = ({id}Prev, {id}Rt); // ping-pong: this frame's output becomes next frame's feedback");
        }

        // The final image pass renders to the screen.
        string img = VarName(image.Name);
        lines.Add("gd.SetRenderTarget(null); // last pass → screen");
        foreach (ChannelWiring c in image.Channels)
            lines.Add("    " + BindLine(img, c));
        lines.Add($"{img}.SetResolution(W, H); {img}.SetTime(t); {img}.Draw();");

        return lines;
    }

    private static string BindLine(string effectVar, ChannelWiring c) => c.Kind switch
    {
        ChannelSourceKind.BufferPass =>
            $"{effectVar}.SetChannel({c.Channel}, {VarName(c.SourcePassName!)}Rt); // iChannel{c.Channel} = {c.SourcePassName}",
        ChannelSourceKind.Feedback =>
            $"{effectVar}.SetChannel({c.Channel}, {VarName(c.SourcePassName!)}Prev); // iChannel{c.Channel} = feedback (prev frame)",
        ChannelSourceKind.Texture =>
            $"{effectVar}.SetChannel({c.Channel}, yourTexture); // iChannel{c.Channel} = external texture (supply your own)",
        _ =>
            $"// iChannel{c.Channel}: unsupported source — leave unbound",
    };

    private static string VarName(string passName)
    {
        string n = MultipassConverter.NormalizePassName(passName);
        return n.Length == 0 ? n : char.ToLower(n[0], CultureInfo.InvariantCulture) + n[1..];
    }

    // ── manifest JSON DTOs ────────────────────────────────────────────────────

    private sealed class ManifestRoot
    {
        [JsonPropertyName("projectName")] public string? ProjectName { get; set; }
        [JsonPropertyName("hasFeedback")] public bool HasFeedback { get; set; }
        [JsonPropertyName("executionOrder")] public List<string> ExecutionOrder { get; set; } = new();
        [JsonPropertyName("passes")] public List<ManifestPass> Passes { get; set; } = new();
    }

    private sealed class ManifestPass
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("outputFile")] public string OutputFile { get; set; } = string.Empty;
        [JsonPropertyName("converted")] public bool Converted { get; set; }
        [JsonPropertyName("hasFeedback")] public bool HasFeedback { get; set; }
        [JsonPropertyName("usedUniforms")] public List<string> UsedUniforms { get; set; } = new();
        [JsonPropertyName("channels")] public List<ManifestChannel> Channels { get; set; } = new();
    }

    private sealed class ManifestChannel
    {
        [JsonPropertyName("channel")] public int Channel { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("sourcePass")] public string? SourcePass { get; set; }
        [JsonPropertyName("sourceFile")] public string? SourceFile { get; set; }
        [JsonPropertyName("textureSrc")] public string? TextureSrc { get; set; }
        [JsonPropertyName("feedback")] public bool Feedback { get; set; }
        [JsonPropertyName("wrap")] public string? Wrap { get; set; }
        [JsonPropertyName("filter")] public string? Filter { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
