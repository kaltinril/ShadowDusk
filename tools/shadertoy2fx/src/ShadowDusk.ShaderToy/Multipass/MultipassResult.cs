namespace ShadowDusk.ShaderToy.Multipass;

// ─────────────────────────────────────────────────────────────────────────────
// Result of converting a ShaderToy multi-tab EXPORT into one .fx per render tab
// PLUS the wiring needed to drive the render graph.
//
// SCOPE: this is NOT a render-graph engine. MultipassConverter converts each tab
// with the EXISTING single-pass ShaderToyConverter and RESOLVES the channel wiring
// (which pass feeds which iChannelN, and which channels are feedback). The consumer
// writes the actual Draw loop (allocate a RenderTarget2D per buffer, bind prior
// outputs, run in order). The CLI emits a documented ~15-line example.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>How an input channel of a pass is wired to its source.</summary>
public enum ChannelSourceKind
{
    /// <summary>The channel reads the output of ANOTHER render pass (see <see cref="ChannelWiring.SourcePassName"/>).</summary>
    BufferPass,

    /// <summary>The channel reads THIS pass's own previous-frame output (ping-pong feedback).</summary>
    Feedback,

    /// <summary>The channel reads an external media texture the consumer must supply (see <see cref="ChannelWiring.TextureSrc"/>).</summary>
    Texture,

    /// <summary>The channel is an unsupported ctype (keyboard/music/mic/webcam/volume/cubemap/video); leave unbound.</summary>
    Unsupported,
}

/// <summary>
/// The resolved binding of one input channel (<c>iChannel0..3</c>) of a pass: where its texture comes
/// from, plus the sampler wrap/filter the export recorded.
/// </summary>
/// <param name="Channel">The channel index 0..3 (binds to <c>iChannelN</c>).</param>
/// <param name="Kind">What feeds this channel.</param>
/// <param name="SourcePassName">
/// For <see cref="ChannelSourceKind.BufferPass"/> / <see cref="ChannelSourceKind.Feedback"/>: the
/// source pass's name (for Feedback this equals the owning pass). Null otherwise.
/// </param>
/// <param name="SourceOutputFile">
/// For a buffer/feedback channel: the normalized <c>.fx</c> file name of the source pass. Null otherwise.
/// </param>
/// <param name="TextureSrc">For <see cref="ChannelSourceKind.Texture"/>: the media source path, if any.</param>
/// <param name="Wrap">The sampler wrap mode (e.g. <c>clamp</c>, <c>repeat</c>), if recorded.</param>
/// <param name="Filter">The sampler filter mode (e.g. <c>linear</c>, <c>nearest</c>), if recorded.</param>
/// <param name="Note">A human-readable note (e.g. "supply your own texture").</param>
public sealed record ChannelWiring(
    int Channel,
    ChannelSourceKind Kind,
    string? SourcePassName,
    string? SourceOutputFile,
    string? TextureSrc,
    string? Wrap,
    string? Filter,
    string? Note)
{
    /// <summary>True when this channel reads the owning pass's own previous frame (ping-pong).</summary>
    public bool IsFeedback => Kind == ChannelSourceKind.Feedback;
}

/// <summary>The converted result for one render pass (a <c>buffer</c> or <c>image</c> tab).</summary>
/// <param name="Name">The pass name (e.g. <c>Buffer A</c>, <c>Image</c>).</param>
/// <param name="OutputFileName">The normalized <c>.fx</c> file name (e.g. <c>BufferA.fx</c>, <c>Image.fx</c>).</param>
/// <param name="Fx">The emitted HLSL <c>.fx</c> text, or <c>null</c> when this pass failed to convert.</param>
/// <param name="Channels">The resolved input-channel wiring for this pass.</param>
/// <param name="UsedUniforms">The drivable uniforms this pass references (forwarded from the single-pass convert).</param>
/// <param name="Diagnostics">Diagnostics from converting this pass.</param>
public sealed record MultipassPassResult(
    string Name,
    string OutputFileName,
    string? Fx,
    IReadOnlyList<ChannelWiring> Channels,
    IReadOnlyList<string> UsedUniforms,
    IReadOnlyList<ConvertDiagnostic> Diagnostics)
{
    /// <summary>True when this pass produced a valid <c>.fx</c>.</summary>
    public bool Success => Fx is not null;

    /// <summary>True when any channel of this pass reads its own previous frame (needs ping-pong).</summary>
    public bool HasFeedback => Channels.Any(c => c.IsFeedback);
}

/// <summary>
/// The outcome of converting a ShaderToy multi-tab export. Exposes one <see cref="MultipassPassResult"/>
/// per rendered pass (in canonical execution order: buffers A..D, then Image; Common is never rendered),
/// plus project-level diagnostics (e.g. warnings for skipped <c>sound</c>/<c>cubemap</c> passes).
/// </summary>
/// <param name="Success">True when EVERY rendered pass converted to a valid <c>.fx</c>.</param>
/// <param name="ProjectName">The shader name from the export, if any.</param>
/// <param name="Passes">The rendered passes in canonical execution order.</param>
/// <param name="Diagnostics">Project-level diagnostics (skipped passes, parse warnings).</param>
public sealed record MultipassResult(
    bool Success,
    string? ProjectName,
    IReadOnlyList<MultipassPassResult> Passes,
    IReadOnlyList<ConvertDiagnostic> Diagnostics)
{
    /// <summary>True when any pass declares a feedback (self-reading) channel.</summary>
    public bool HasFeedback => Passes.Any(p => p.HasFeedback);
}
