#nullable enable

using System.Text;

namespace ShadowDusk.Core;

/// <summary>
/// One target's slice of a <see cref="ShaderValidationReport"/>: whether the shader
/// compiles for <see cref="Target"/>, the errors when it does not, and the non-fatal
/// warnings when it does.
/// </summary>
/// <param name="Target">The platform backend that was validated.</param>
/// <param name="Succeeded">Whether the shader compiles for <see cref="Target"/>.</param>
/// <param name="Errors">
/// The compile errors, verbatim from the underlying compiler (empty when
/// <see cref="Succeeded"/> is <see langword="true"/>).
/// </param>
/// <param name="Warnings">
/// Non-fatal diagnostics: the underlying compiler's own warnings plus ShadowDusk's GL
/// portability findings (<c>SD0400</c>–<c>SD0402</c>) — constructs that compile but are
/// known to fail or misbehave at runtime on some GL stacks.
/// </param>
public sealed record ShaderTargetValidation(
    PlatformTarget Target,
    bool Succeeded,
    IReadOnlyList<ShaderError> Errors,
    IReadOnlyList<ShaderError> Warnings);

/// <summary>
/// Everything wrong — and everything worth knowing — about one shader, across one or
/// more targets. Produced by <see cref="ShaderCompilerValidationExtensions.ValidateAsync(IShaderCompiler, string, CompilerOptions?, CancellationToken)"/>
/// / <see cref="ShaderCompilerValidationExtensions.Validate(IShaderCompiler, string, CompilerOptions?, CancellationToken)"/>.
/// The simplest use is to print it: <see cref="ToString"/> renders the complete
/// human-readable report (per-target status, every error with its source location and
/// the compiler's verbatim text, every warning). For structured access walk
/// <see cref="Targets"/>.
/// </summary>
public sealed class ShaderValidationReport
{
    /// <summary>Per-target results, in the order they were validated.</summary>
    public IReadOnlyList<ShaderTargetValidation> Targets { get; }

    /// <summary>Creates a report over the given per-target results.</summary>
    public ShaderValidationReport(IReadOnlyList<ShaderTargetValidation> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Targets = targets;
    }

    /// <summary>True when the shader compiled for every validated target.</summary>
    public bool IsValid
    {
        get
        {
            foreach (ShaderTargetValidation t in Targets)
            {
                if (!t.Succeeded)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// True when there is nothing to report at all — every target compiled and no
    /// target produced a single warning.
    /// </summary>
    public bool IsClean
    {
        get
        {
            foreach (ShaderTargetValidation t in Targets)
            {
                if (!t.Succeeded || t.Warnings.Count > 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Every diagnostic from every target, errors first within each target, in target
    /// order. Convenient for logging pipelines that want one flat list.
    /// </summary>
    public IReadOnlyList<ShaderError> AllDiagnostics
    {
        get
        {
            var all = new List<ShaderError>();
            foreach (ShaderTargetValidation t in Targets)
            {
                all.AddRange(t.Errors);
                all.AddRange(t.Warnings);
            }
            return all;
        }
    }

    /// <summary>
    /// The complete human-readable report: a summary line, then each target's status
    /// with every error and warning — source location when known, and the underlying
    /// compiler's full verbatim text when it adds information. Designed so that
    /// printing this one string tells a shader author what is wrong and where.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();

        int failed = 0;
        foreach (ShaderTargetValidation t in Targets)
        {
            if (!t.Succeeded)
                failed++;
        }

        sb.Append("Shader validation: ");
        if (Targets.Count == 0)
            sb.AppendLine("no targets validated.");
        else if (failed == 0)
            sb.AppendLine(IsClean
                ? $"OK on all {Targets.Count} target(s), no warnings."
                : $"OK on all {Targets.Count} target(s), with warnings.");
        else
            sb.AppendLine($"{failed} of {Targets.Count} target(s) FAILED.");

        foreach (ShaderTargetValidation t in Targets)
        {
            sb.AppendLine();
            sb.Append('[').Append(t.Target).Append("] ");
            if (t.Succeeded)
                sb.AppendLine(t.Warnings.Count == 0
                    ? "OK"
                    : $"OK, {t.Warnings.Count} warning(s)");
            else
                sb.AppendLine($"FAILED, {t.Errors.Count} error(s)" +
                              (t.Warnings.Count > 0 ? $", {t.Warnings.Count} warning(s)" : ""));

            foreach (ShaderError e in t.Errors)
                AppendDiagnostic(sb, e);
            foreach (ShaderError w in t.Warnings)
                AppendDiagnostic(sb, w);
        }

        return sb.ToString();
    }

    private static void AppendDiagnostic(StringBuilder sb, ShaderError e)
    {
        string severity = e.Severity switch
        {
            ShaderErrorSeverity.Warning => "warning",
            ShaderErrorSeverity.Note    => "note",
            _                           => "error",
        };

        sb.Append("  ").Append(severity).Append(' ').Append(e.Code);
        if (e.Line > 0)
            sb.Append(" at ").Append(e.File).Append('(').Append(e.Line).Append(',').Append(e.Column).Append(')');
        else if (!string.IsNullOrEmpty(e.File))
            // File-scoped but line-less (the GL portability lint reads emitted GLSL, which has
            // no line mapping back to the .fx). Still name the file: a report covering several
            // targets is otherwise full of unattributed "warning SD0401: ... 'MainPS' ..." lines.
            sb.Append(" in ").Append(e.File);
        sb.Append(": ");

        // A verbatim multi-line message keeps its lines, indented under the entry.
        string[] messageLines = e.Message.Replace("\r\n", "\n").Split('\n');
        sb.AppendLine(messageLines[0]);
        for (int i = 1; i < messageLines.Length; i++)
            sb.Append("      ").AppendLine(messageLines[i]);

        // The underlying compiler's complete output, when it says more than the
        // message already did (constraint 5: show the compiler's own words).
        if (e.HasAdditionalRawDiagnostics)
        {
            foreach (string raw in e.RawDiagnostics!.TrimEnd().Replace("\r\n", "\n").Split('\n'))
                sb.Append("      | ").AppendLine(raw);
        }
    }
}
