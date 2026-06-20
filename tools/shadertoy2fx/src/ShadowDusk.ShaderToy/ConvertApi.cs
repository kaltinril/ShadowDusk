namespace ShadowDusk.ShaderToy;

// ─────────────────────────────────────────────────────────────────────────────
// FROZEN CROSS-AGENT CONTRACT (Phase 46).
//
// These public types are the ONLY API the CLI and the tests bind to. The
// transpiler implementation (lexer/parser/AST/type-inference/emitter/harness)
// lives behind the single static entry point:
//
//     ShadowDusk.ShaderToy.ShaderToyConverter.Convert(string glsl, ConvertOptions? options = null)
//         → ConvertResult
//
// Implementers: do NOT change the shapes below without reconciling every caller.
// Prefer additive members (new optional init-only properties) over breaking changes.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Severity of a <see cref="ConvertDiagnostic"/>.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Non-fatal; conversion may still succeed.</summary>
    Warning,

    /// <summary>Fatal; conversion fails (e.g. an unsupported construct — fail loudly).</summary>
    Error,
}

/// <summary>A single diagnostic produced while converting ShaderToy GLSL to HLSL <c>.fx</c>.</summary>
/// <param name="Severity">Whether this is fatal.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Line">1-based source line in the ShaderToy GLSL (0 if not applicable).</param>
/// <param name="Column">1-based source column (0 if not applicable).</param>
/// <param name="Construct">The offending source construct, when relevant (for reject messages).</param>
public sealed record ConvertDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    int Line,
    int Column,
    string? Construct = null);

/// <summary>Options controlling a ShaderToy → <c>.fx</c> conversion.</summary>
public sealed record ConvertOptions
{
    /// <summary>Effect name used in comments / metadata of the emitted <c>.fx</c>.</summary>
    public string EffectName { get; init; } = "ShaderToyEffect";

    /// <summary>Name of the emitted <c>technique</c>.</summary>
    public string TechniqueName { get; init; } = "ShaderToy";

    /// <summary>
    /// Optional ShaderToy "Common" tab source, prepended (after translation) before the image tab.
    /// </summary>
    public string? CommonSource { get; init; }

    /// <summary>When true, stop at the first error; default false (collect all diagnostics).</summary>
    public bool StopOnFirstError { get; init; }
}

/// <summary>The outcome of a ShaderToy → <c>.fx</c> conversion.</summary>
public sealed record ConvertResult
{
    /// <summary>True when a valid <c>.fx</c> was produced (no <see cref="DiagnosticSeverity.Error"/>).</summary>
    public required bool Success { get; init; }

    /// <summary>The emitted HLSL <c>.fx</c> text, or <c>null</c> when <see cref="Success"/> is false.</summary>
    public string? Fx { get; init; }

    /// <summary>All diagnostics emitted during conversion (errors and warnings).</summary>
    public required IReadOnlyList<ConvertDiagnostic> Diagnostics { get; init; }

    /// <summary>
    /// The drivable effect parameters: the ShaderToy built-in uniforms the shader actually referenced
    /// (e.g. <c>iTime</c>, <c>iChannel0</c>) PLUS every accepted custom <c>uniform</c> the source
    /// declared (e.g. <c>u_roughness</c>, a custom <c>sampler2D</c>), so a runtime helper knows which
    /// effect parameters to drive each frame.
    /// </summary>
    public required IReadOnlyList<string> UsedUniforms { get; init; }
}
