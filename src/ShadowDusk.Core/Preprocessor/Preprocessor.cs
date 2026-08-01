#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Flattens an HLSL source tree by expanding all <c>#include</c> directives (honoring
/// <c>#pragma once</c> and detecting circular includes) and prepending the platform macros,
/// producing a single self-contained <see cref="PreprocessedSource"/> for the compiler.
/// </summary>
public sealed class Preprocessor
{
    private static readonly Regex IncludePattern =
        new(@"^\s*#\s*include\s*[""<]([^"">]+)["">]", RegexOptions.Compiled);

    private static readonly Regex PragmaOncePattern =
        new(@"^\s*#\s*pragma\s+once\s*$", RegexOptions.Compiled);

    private readonly IIncludePathCanonicalizer _pathCanonicalizer;

    /// <summary>
    /// Creates a preprocessor that asks the real file system how it spells resolved include
    /// paths.
    /// </summary>
    public Preprocessor()
        : this(FileSystemIncludePathCanonicalizer.Instance)
    {
    }

    /// <summary>
    /// Creates a preprocessor with an explicit <see cref="IIncludePathCanonicalizer"/>.
    /// </summary>
    /// <param name="pathCanonicalizer">
    /// Decides whether two case-only path variants name the same file, and supplies the on-disk
    /// spelling the <c>SD0008</c> portability warning reports. Injectable so both file-system
    /// behaviours (case-sensitive, as on Android and Linux and case-sensitive APFS; and
    /// case-insensitive, as on a default Windows or macOS volume) can be driven from a pure
    /// unit test on any host.
    /// </param>
    public Preprocessor(IIncludePathCanonicalizer pathCanonicalizer)
        => _pathCanonicalizer = pathCanonicalizer;

    /// <summary>
    /// Expands all includes in the source and prepends the platform macros.
    /// </summary>
    /// <param name="cleanedHlsl">The HLSL entry source (comments may still be present; the
    /// include scanner is comment-aware and ignores directives inside them).</param>
    /// <param name="originalFilePath">The entry source's path, used for diagnostics and relative includes.</param>
    /// <param name="macros">The platform macros to prepend.</param>
    /// <param name="includeResolver">The resolver used to fetch <c>#include</c> targets.</param>
    /// <param name="additionalPaths">Extra include search directories.</param>
    /// <returns>
    /// The flattened source on success (carrying any non-fatal
    /// <see cref="PreprocessedSource.Warnings"/>), or a <see cref="ShaderError"/> on a missing
    /// or circular include.
    /// </returns>
    public Result<PreprocessedSource, ShaderError> Flatten(
        string cleanedHlsl,
        string originalFilePath,
        MacroSet macros,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalPaths)
    {
        var ctx = new PreprocessorContext(_pathCanonicalizer);
        string prepend = macros.ToTextPrepend(originalFilePath);

        var bodyBuilder = new StringBuilder();
        var flattenResult = FlattenFile(cleanedHlsl, originalFilePath, ctx, includeResolver, additionalPaths, bodyBuilder);
        if (flattenResult.IsFailure)
            return Result<PreprocessedSource, ShaderError>.Fail(flattenResult.Error);

        string fullText = prepend + bodyBuilder.ToString();
        return Result<PreprocessedSource, ShaderError>.Ok(
            new PreprocessedSource(fullText, macros.ToDxcFlags(), originalFilePath, ctx.Warnings));
    }

    private Result<Unit, ShaderError> FlattenFile(
        string text,
        string filePath,
        PreprocessorContext ctx,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalPaths,
        StringBuilder output)
    {
        // Cycle detection uses an include STACK (push on entry, pop on exit), not a
        // visited set: a DIAMOND include (a → {b, c} → common) is legal — fxc/mgfxc
        // accept it (header guards / #pragma once neutralize the duplication; our
        // flatten leaves #if/#define lines for DXC to evaluate) — while a true cycle
        // (a → b → a, or a self-include) must still fail SD0002.
        ctx.IncludeStack.Add(filePath);
        try
        {
            // Tracks an open /* ... */ block comment across lines so a directive inside
            // one (e.g. '/* #include "ghost.fxh" */') is never honored.
            bool inBlockComment = false;

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i];

                string trimmedLine = line.TrimEnd('\r');

                // Directive DETECTION runs on the comment-stripped view of the line;
                // OUTPUT always uses the original line text, so non-directive lines
                // (including commented-out directives) pass through verbatim.
                string scanLine = StripCommentsForScan(trimmedLine, ref inBlockComment);

                var pragmaMatch = PragmaOncePattern.Match(scanLine);
                if (pragmaMatch.Success)
                {
                    ctx.PragmaOnceFiles.Add(filePath);
                    // BLANK the directive, do not delete it. The line slot has to survive
                    // or every later line of this file is one lower than the `#line`
                    // anchor says, and DXC reports the whole file's diagnostics at the
                    // wrong line — which the CLI, MGCB, and IDE jump-to-line all trust
                    // verbatim. Same convention as Vkd3dShaderCompiler and FxPreParser.
                    output.Append('\n');
                    continue;
                }

                var includeMatch = IncludePattern.Match(scanLine);
                if (includeMatch.Success)
                {
                    string includePath = includeMatch.Groups[1].Value;
                    var resolveResult = includeResolver.Resolve(includePath, filePath, additionalPaths);
                    if (resolveResult.IsFailure)
                    {
                        var err = resolveResult.Error;
                        // Only a genuine "not found" is re-minted here. The resolver cannot
                        // know the #include's line number, so this stamps the location —
                        // but it must DECORATE, not REPLACE. Rewriting every failure into
                        // SD0001 made FileSystemIncludeResolver's SD0004 ("exists but could
                        // not be read") unreachable, so a locked or ACL-denied header told
                        // the user a file that is right there was not found; and it silently
                        // overwrote whatever diagnostic a consumer's own IIncludeResolver
                        // returned, against the "never swallow another compiler's message"
                        // rule.
                        if (err.Kind != ShaderErrorKind.IncludeNotFound)
                            return Result<Unit, ShaderError>.Fail(
                                err with { File = filePath, Line = lineNumber });

                        IReadOnlyList<string> searched = err.SearchedPaths ?? [];
                        return Result<Unit, ShaderError>.Fail(
                            ShaderError.IncludeNotFound(filePath, lineNumber, includePath, searched));
                    }

                    string resolvedPath = resolveResult.Value.FilePath;

                    WarnOnCaseOnlyIncludeMismatch(includePath, resolvedPath, filePath, lineNumber, ctx);

                    if (ctx.PragmaOnceFiles.Contains(resolvedPath))
                    {
                        // Same reason as the `#pragma once` blank above: this suppressed
                        // #include still occupied a line in the INCLUDING file, and unlike
                        // the taken branch below there is no `#line {lineNumber + 1}`
                        // re-anchor afterwards to absorb the drift.
                        output.Append('\n');
                        continue;
                    }

                    if (ctx.IncludeStack.Contains(resolvedPath))
                        return Result<Unit, ShaderError>.Fail(
                            ShaderError.CircularInclude(filePath, lineNumber, includePath));

                    // '\n', not AppendLine: body lines below join with '\n', and an
                    // Environment.NewLine here made the flattened compiler input differ
                    // by build OS (CRLF-mixed on Windows), which leaks into debug-mode
                    // artifacts via embedded source (bug-hunt 2026-07-27 N17).
                    output.Append($"#line 1 \"{resolvedPath.Replace('\\', '/')}\"\n");

                    var recurseResult = FlattenFile(
                        resolveResult.Value.Text,
                        resolvedPath,
                        ctx,
                        includeResolver,
                        additionalPaths,
                        output);

                    if (recurseResult.IsFailure)
                        return recurseResult;

                    output.Append($"#line {lineNumber + 1} \"{filePath.Replace('\\', '/')}\"\n");
                    continue;
                }

                output.Append(trimmedLine);
                output.Append('\n');
            }

            return Result<Unit, ShaderError>.Ok(default);
        }
        finally
        {
            ctx.IncludeStack.Remove(filePath);
        }
    }

    /// <summary>
    /// Raises <c>SD0008</c> when an <c>#include</c> resolved <b>only</b> because this host's
    /// file system ignores case — the spelling in the directive differs from the file's real
    /// name by case alone.
    /// </summary>
    /// <remarks>
    /// <para>This is the shape that compiles on the author's Windows or macOS box and then
    /// fails with <c>SD0001</c> on a player's Android device, on Linux, or on a case-sensitive
    /// APFS volume — a build break the author has no way to see locally. Silently accepting it
    /// is exactly the pass-through the "fail loudly" rule forbids, so it is reported, but as a
    /// <b>warning</b>: the include did resolve, mgfxc on Windows accepts it, and rejecting it
    /// would be a reject-set change that breaks working shaders.</para>
    /// <para>Only the segments the directive itself spells are checked. The absolute prefix
    /// above them is the author's own machine layout, never shipped, so a case difference there
    /// is not a portability signal and warning about it would be pure noise. A rooted spelling
    /// or one containing <c>.</c>/<c>..</c> is skipped, because its segments cannot be aligned
    /// against the tail of the resolved path.</para>
    /// </remarks>
    private void WarnOnCaseOnlyIncludeMismatch(
        string includeSpelling,
        string resolvedPath,
        string includingFilePath,
        int lineNumber,
        PreprocessorContext ctx)
    {
        string normalizedSpelling = includeSpelling.Replace('\\', '/');
        if (normalizedSpelling.Length == 0 || Path.IsPathRooted(normalizedSpelling))
            return;

        string[] requested = normalizedSpelling.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0)
            return;

        foreach (string segment in requested)
        {
            if (segment is "." or "..")
                return;
        }

        string? onDisk = _pathCanonicalizer.TryGetOnDiskPath(resolvedPath);
        if (onDisk is null || string.Equals(onDisk, resolvedPath, StringComparison.Ordinal))
            return;

        string[] actual = onDisk.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (actual.Length < requested.Length)
            return;

        string[] actualTail = actual[^requested.Length..];
        if (actualTail.SequenceEqual(requested, StringComparer.Ordinal))
            return;

        // Not a pure case difference: the resolver reached the file some other way (a symlink,
        // a search path with a different shape). Nothing portable to say about it.
        if (!actualTail.SequenceEqual(requested, StringComparer.OrdinalIgnoreCase))
            return;

        string onDiskSpelling = string.Join('/', actualTail);

        ctx.AddWarning(new ShaderError(
            File: includingFilePath,
            Line: lineNumber,
            Column: 0,
            Code: "SD0008",
            Message: $"#include \"{includeSpelling}\" differs from the file's actual name "
                   + $"\"{onDiskSpelling}\" by case only. It resolved because this host's file "
                   + "system ignores case; Android, Linux, and case-sensitive APFS do not, so "
                   + "this include will fail there. Match the on-disk spelling.",
            Severity: ShaderErrorSeverity.Warning,
            IncludingFilePath: includingFilePath,
            IncludingLineNumber: lineNumber,
            RequestedPath: includeSpelling));
    }

    /// <summary>
    /// Returns the line with comment text blanked out (for directive scanning only),
    /// updating <paramref name="inBlockComment"/> for <c>/* ... */</c> comments that span
    /// lines. String literals are skipped over so a <c>/*</c> or <c>//</c> inside one can
    /// never toggle comment state (e.g. <c>string s = "a /* b";</c>).
    /// </summary>
    private static string StripCommentsForScan(string line, ref bool inBlockComment)
    {
        // Fast path: nothing comment- or string-like on this line.
        if (!inBlockComment && line.IndexOf('/') < 0 && line.IndexOf('"') < 0)
            return line;

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (inBlockComment)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    sb.Append("  ");
                    i += 2;
                }
                else
                {
                    sb.Append(' ');
                    i++;
                }
                continue;
            }

            char c = line[i];

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                // Line comment — blank the rest of the line.
                sb.Append(' ', line.Length - i);
                break;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                inBlockComment = true;
                sb.Append("  ");
                i += 2;
                continue;
            }

            if (c == '"')
            {
                // Copy the string literal verbatim (honoring \" escapes) so its
                // contents can neither open a comment nor end the scan early.
                sb.Append(c);
                i++;
                while (i < line.Length)
                {
                    sb.Append(line[i]);
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        sb.Append(line[i]);
                    }
                    else if (line[i] == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private sealed class PreprocessorContext
    {
        // Paths are compared the way the storage they came from spells them, NOT by an
        // OS guess. The old rule here was `OperatingSystem.IsLinux() ? Ordinal :
        // OrdinalIgnoreCase`, which was wrong on two real hosts ShadowDusk ships to:
        // Android is case-SENSITIVE (it is Linux, but IsLinux() is false there), and APFS
        // can be formatted case-sensitive. On both, two genuinely distinct headers whose
        // names differed only by case were treated as one file — a `#pragma once` in one
        // suppressed the other, and a legal include chain could be rejected as SD0002.
        // See IncludePathEqualityComparer for why ordinal is the default.
        private readonly IncludePathEqualityComparer _pathComparer;

        private readonly List<ShaderError> _warnings = [];

        private readonly HashSet<(string File, int Line, string Code)> _seenWarnings = [];

        public PreprocessorContext(IIncludePathCanonicalizer pathCanonicalizer)
        {
            _pathComparer = new IncludePathEqualityComparer(pathCanonicalizer);
            IncludeStack = new HashSet<string>(_pathComparer);
            PragmaOnceFiles = new HashSet<string>(_pathComparer);
        }

        /// <summary>The chain of files currently being flattened (push/pop), for cycle detection.</summary>
        public HashSet<string> IncludeStack { get; }

        public HashSet<string> PragmaOnceFiles { get; }

        /// <summary>Non-fatal diagnostics raised while flattening.</summary>
        public IReadOnlyList<ShaderError> Warnings => _warnings;

        /// <summary>
        /// Records a warning once per (file, line, code). A diamond include re-visits the same
        /// directive from two parents, which must not double-report it.
        /// </summary>
        public void AddWarning(ShaderError warning)
        {
            if (_seenWarnings.Add((warning.File, warning.Line, warning.Code)))
                _warnings.Add(warning);
        }
    }
}
