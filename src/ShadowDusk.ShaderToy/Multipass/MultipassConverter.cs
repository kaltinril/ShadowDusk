namespace ShadowDusk.ShaderToy.Multipass;

// ─────────────────────────────────────────────────────────────────────────────
// Batch multipass-EXPORT converter (Phase 46).
//
// EXPLICIT SCOPE: this is NOT a ShaderToy runtime / orchestrator / emulator. It
// "accepts the syntax": it converts each render tab with the EXISTING single-pass
// ShaderToyConverter (no converter behavior changes) and RESOLVES the channel
// wiring (which pass feeds which iChannelN, which channels are feedback). The
// consumer writes the actual Draw loop; the CLI hands them a documented example.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Converts a ShaderToy multi-tab export (<see cref="ShaderToyProject"/>) into one <c>.fx</c> per
/// render tab plus the resolved channel wiring, by delegating each tab to the EXISTING single-pass
/// <see cref="ShaderToyConverter"/>. It does not orchestrate or render anything.
/// </summary>
public static class MultipassConverter
{
    /// <summary>
    /// Convert every <c>buffer</c> and <c>image</c> pass of <paramref name="project"/> to a <c>.fx</c>,
    /// prepending the single <c>common</c> tab (if any) to each. Resolves each pass's input channels to
    /// their source (buffer pass / feedback / external texture / unsupported), records sampler modes, and
    /// orders the rendered passes canonically (buffers A..D in name order, then Image). <c>sound</c> and
    /// <c>cubemap</c> passes are skipped with a warning.
    /// </summary>
    /// <param name="project">The parsed export.</param>
    /// <param name="options">
    /// Base convert options. <see cref="ConvertOptions.CommonSource"/> is overridden with the export's
    /// <c>common</c> tab; <see cref="ConvertOptions.EffectName"/>/<see cref="ConvertOptions.TechniqueName"/>
    /// are derived per pass from the pass name.
    /// </param>
    public static MultipassResult Convert(ShaderToyProject project, ConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new ConvertOptions();

        var diagnostics = new List<ConvertDiagnostic>();

        // The single Common tab's code is prepended (after translation) to every rendered pass.
        ShaderToyPass? common = project.Common;
        if (project.Passes.Count(p => p.Type == ShaderToyPassType.Common) > 1)
        {
            diagnostics.Add(new ConvertDiagnostic(
                DiagnosticSeverity.Warning,
                "Export has more than one 'common' pass; using the first.",
                0, 0));
        }

        // Build a lookup from an output id -> the pass that produces it, so a 'buffer' input id can be
        // resolved to its source pass. (WIRING RULE: a buffer input's id equals a pass's outputs[].id.)
        Dictionary<string, ShaderToyPass> outputIdToPass = BuildOutputIndex(project, diagnostics);

        // Warn + skip the out-of-v1-scope pass types up front (sound / cubemap).
        foreach (ShaderToyPass p in project.Passes)
        {
            if (p.Type is ShaderToyPassType.Sound or ShaderToyPassType.Cubemap)
            {
                diagnostics.Add(new ConvertDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"Skipping '{p.Name}' ({p.Type.ToString().ToLowerInvariant()} pass): out of v1 scope (only buffer/image passes are converted).",
                    0, 0, p.Name));
            }
        }

        // Canonical execution order: buffers (by name) first, then the single Image, last.
        var rendered = project.Passes
            .Where(p => p.Type is ShaderToyPassType.Buffer or ShaderToyPassType.Image)
            .OrderBy(p => p.Type == ShaderToyPassType.Image ? 1 : 0) // buffers before image
            .ThenBy(p => p.Name, StringComparer.Ordinal)            // Buffer A, B, C, D in name order
            .ToList();

        var passResults = new List<MultipassPassResult>(rendered.Count);
        bool allOk = true;

        foreach (ShaderToyPass pass in rendered)
        {
            MultipassPassResult result = ConvertPass(pass, common, outputIdToPass, options);
            passResults.Add(result);
            if (!result.Success)
                allOk = false;
        }

        if (passResults.Count == 0)
        {
            diagnostics.Add(new ConvertDiagnostic(
                DiagnosticSeverity.Error,
                "Export has no 'buffer' or 'image' render passes to convert.",
                0, 0));
            allOk = false;
        }

        return new MultipassResult(allOk, project.Name, passResults, diagnostics);
    }

    private static MultipassPassResult ConvertPass(
        ShaderToyPass pass,
        ShaderToyPass? common,
        IReadOnlyDictionary<string, ShaderToyPass> outputIdToPass,
        ConvertOptions baseOptions)
    {
        string fileBase = NormalizePassName(pass.Name);

        var perPassOptions = baseOptions with
        {
            CommonSource = common?.Code,
            EffectName = fileBase,
            TechniqueName = fileBase,
        };

        ConvertResult conv = ShaderToyConverter.Convert(pass.Code, perPassOptions);

        var channels = ResolveChannels(pass, outputIdToPass, out IReadOnlyList<ConvertDiagnostic> channelDiags);

        var diagnostics = new List<ConvertDiagnostic>(conv.Diagnostics);
        diagnostics.AddRange(channelDiags);

        return new MultipassPassResult(
            pass.Name,
            fileBase + ".fx",
            conv.Success ? conv.Fx : null,
            channels,
            conv.UsedUniforms,
            diagnostics);
    }

    private static IReadOnlyList<ChannelWiring> ResolveChannels(
        ShaderToyPass pass,
        IReadOnlyDictionary<string, ShaderToyPass> outputIdToPass,
        out IReadOnlyList<ConvertDiagnostic> diagnostics)
    {
        var channels = new List<ChannelWiring>();
        var diags = new List<ConvertDiagnostic>();

        foreach (ShaderToyInput input in pass.Inputs.OrderBy(i => i.Channel))
        {
            string? wrap = input.Sampler?.Wrap;
            string? filter = input.Sampler?.Filter;

            switch (input.Ctype)
            {
                case ShaderToyChannelType.Buffer:
                {
                    if (outputIdToPass.TryGetValue(input.Id, out ShaderToyPass? source))
                    {
                        // FEEDBACK when the matched source pass IS this pass (reads its own prev frame).
                        bool feedback = ReferenceEquals(source, pass);
                        channels.Add(new ChannelWiring(
                            input.Channel,
                            feedback ? ChannelSourceKind.Feedback : ChannelSourceKind.BufferPass,
                            source.Name,
                            NormalizePassName(source.Name) + ".fx",
                            null,
                            wrap,
                            filter,
                            feedback
                                ? "feedback: bind THIS pass's previous-frame target (ping-pong)"
                                : $"bind the output of '{source.Name}'"));
                    }
                    else
                    {
                        // A buffer input whose id matches no pass output: unresolved (warn, leave unbound).
                        diags.Add(new ConvertDiagnostic(
                            DiagnosticSeverity.Warning,
                            $"iChannel{input.Channel} of '{pass.Name}' is a buffer input whose id '{input.Id}' matches no render pass output; leave it unbound.",
                            0, 0, pass.Name));
                        channels.Add(new ChannelWiring(
                            input.Channel, ChannelSourceKind.Unsupported, null, null, null, wrap, filter,
                            "unresolved buffer input; leave unbound"));
                    }

                    break;
                }

                case ShaderToyChannelType.Texture:
                {
                    channels.Add(new ChannelWiring(
                        input.Channel, ChannelSourceKind.Texture, null, null, input.Src, wrap, filter,
                        "external texture: supply your own texture"));
                    break;
                }

                default:
                {
                    // keyboard / music / musicstream / mic / webcam / volume / cubemap / video.
                    diags.Add(new ConvertDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"iChannel{input.Channel} of '{pass.Name}' is an unsupported channel type '{input.Ctype.ToString().ToLowerInvariant()}'; leave it unbound.",
                        0, 0, pass.Name));
                    channels.Add(new ChannelWiring(
                        input.Channel, ChannelSourceKind.Unsupported, null, null, null, wrap, filter,
                        $"unsupported channel type ({input.Ctype.ToString().ToLowerInvariant()}); leave unbound"));
                    break;
                }
            }
        }

        diagnostics = diags;
        return channels;
    }

    private static Dictionary<string, ShaderToyPass> BuildOutputIndex(
        ShaderToyProject project, List<ConvertDiagnostic> diagnostics)
    {
        var map = new Dictionary<string, ShaderToyPass>(StringComparer.Ordinal);
        foreach (ShaderToyPass pass in project.Passes)
        {
            foreach (ShaderToyOutput output in pass.Outputs)
            {
                if (string.IsNullOrEmpty(output.Id))
                    continue;

                // First writer wins; a duplicated output id across passes is malformed but tolerated.
                if (!map.ContainsKey(output.Id))
                    map[output.Id] = pass;
            }
        }

        return map;
    }

    /// <summary>
    /// Normalize a ShaderToy pass name into a file/identifier-safe base (e.g. <c>"Buffer A"</c> →
    /// <c>"BufferA"</c>, <c>"Image"</c> → <c>"Image"</c>). Non-alphanumeric characters are dropped.
    /// </summary>
    public static string NormalizePassName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }

        string normalized = sb.ToString();
        if (normalized.Length == 0)
            normalized = "Pass";

        // An identifier cannot start with a digit (it feeds EffectName/TechniqueName).
        if (char.IsDigit(normalized[0]))
            normalized = "Pass" + normalized;

        return normalized;
    }
}
