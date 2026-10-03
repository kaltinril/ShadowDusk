#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL.Preprocessing;

/// <summary>
/// Maps a physical line of a text that carries <c>#line N "file"</c> directives (an
/// <c>#include</c>-flattened source, or its preprocessed form) back to the file and line the
/// author wrote, the way a compiler reading those directives does.
///
/// <para>The pre-parser reports positions in the text it was handed. When that text is the
/// preprocessed source of the legacy-sampler recovery (issue #308), a position there is off by
/// the prepended macro block and by every inlined <c>#include</c>, so its errors and spans go
/// through this map before anything shows them to the user.</para>
/// </summary>
internal sealed partial class SourceLineMap
{
    [GeneratedRegex(@"^\s*#\s*line\s+(\d+)(?:\s+""((?:[^""\\]|\\.)*)"")?", RegexOptions.CultureInvariant)]
    private static partial Regex LineDirective();

    private readonly string[] _files;
    private readonly int[] _lines;

    /// <param name="text">The text whose physical lines are to be mapped.</param>
    /// <param name="sourceFile">The file the text starts in, until a directive says otherwise.</param>
    public SourceLineMap(string text, string sourceFile)
    {
        string[] physical = text.Split('\n');
        _files = new string[physical.Length];
        _lines = new int[physical.Length];

        string file = sourceFile;
        int line = 1;
        for (int i = 0; i < physical.Length; i++)
        {
            _files[i] = file;
            _lines[i] = line;

            Match directive = LineDirective().Match(physical[i]);
            if (directive.Success &&
                int.TryParse(directive.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int next))
            {
                // '#line N "file"': the NEXT line is line N of that file.
                line = next;
                if (directive.Groups[2].Success)
                    file = directive.Groups[2].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            }
            else
            {
                line++;
            }
        }
    }

    /// <summary>The author's file and line for a 1-based physical line of the text.</summary>
    public (string File, int Line) Resolve(int physicalLine)
    {
        if (physicalLine < 1 || physicalLine > _lines.Length)
            return (_files.Length > 0 ? _files[0] : string.Empty, physicalLine);
        return (_files[physicalLine - 1], _lines[physicalLine - 1]);
    }

    private SourceSpan Remap(SourceSpan span) =>
        span == SourceSpan.Unknown
            ? span
            : new SourceSpan(Resolve(span.StartLine).Line, span.StartColumn, Resolve(span.EndLine).Line, span.EndColumn);

    private SourceSpan? Remap(SourceSpan? span) => span is { } value ? Remap(value) : null;

    private IReadOnlyList<AnnotationEntry> Remap(IReadOnlyList<AnnotationEntry> entries) =>
        entries.Select(e => e with { Span = Remap(e.Span) }).ToList();

    /// <summary>
    /// <paramref name="parsed"/> with every span moved from the text's physical lines onto the
    /// author's lines. A span carries no file, so one inside an <c>#include</c>d file keeps that
    /// file's line number.
    /// </summary>
    public FxParseResult Remap(FxParseResult parsed) => parsed with
    {
        Techniques = parsed.Techniques.Select(t => t with
        {
            Span = Remap(t.Span),
            Annotations = Remap(t.Annotations),
            Passes = t.Passes.Select(p => p with
            {
                Span = Remap(p.Span),
                VertexProfileSpan = Remap(p.VertexProfileSpan),
                PixelProfileSpan = Remap(p.PixelProfileSpan),
                Annotations = Remap(p.Annotations),
                RenderStates = p.RenderStates.Select(r => r with { Span = Remap(r.Span) }).ToList(),
            }).ToList(),
        }).ToList(),
        Samplers = parsed.Samplers.Select(s => s with
        {
            Span = Remap(s.Span),
            StateEntries = s.StateEntries.Select(e => e with { Span = Remap(e.Span) }).ToList(),
        }).ToList(),
        ParameterAnnotations = parsed.ParameterAnnotations.Select(a => a with
        {
            Span = Remap(a.Span),
            Entries = Remap(a.Entries),
        }).ToList(),
    };
}
