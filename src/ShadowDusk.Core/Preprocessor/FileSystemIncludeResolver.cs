#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// An <see cref="IIncludeResolver"/> that resolves <c>#include</c> directives from the file
/// system, searching first relative to the including file's directory and then the supplied
/// additional search paths.
/// </summary>
public sealed class FileSystemIncludeResolver : IIncludeResolver
{
    /// <inheritdoc/>
    public Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,
        IReadOnlyList<string> additionalSearchPaths)
    {
        // Windows-authored effects routinely write `#include "Shared\Macros.fxh"`; on
        // Unix a verbatim combine makes that a literal-backslash filename that never
        // resolves, while mgfxc (Windows/Wine) accepts both separators (bug-hunt
        // 2026-07-27 M12). Forward slashes work on every OS. The in-memory resolver
        // already normalizes the same way.
        includePath = includePath.Replace('\\', '/');

        var tried = new List<string>();

        if (includingFilePath is not null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(includingFilePath)) ?? string.Empty;
            string candidate = Path.GetFullPath(Path.Combine(dir, includePath));
            tried.Add(candidate);
            if (File.Exists(candidate))
                return ReadInclude(candidate, includePath, includingFilePath);
        }

        foreach (string searchPath in additionalSearchPaths)
        {
            string candidate = Path.GetFullPath(Path.Combine(searchPath, includePath));
            tried.Add(candidate);
            if (File.Exists(candidate))
                return ReadInclude(candidate, includePath, includingFilePath);
        }

        int includingLine = 0;
        string includingFile = includingFilePath ?? string.Empty;
        return Result<IncludeResolvedFile, ShaderError>.Fail(
            ShaderError.IncludeNotFound(includingFile, includingLine, includePath, tried));
    }

    /// <summary>
    /// Reads a resolved include, mapping I/O failures (a locked file, an ACL denial, a
    /// delete racing the <see cref="File.Exists(string)"/> probe) to a <c>Result</c> error
    /// instead of letting a raw exception escape the library's no-throw contract
    /// (bug-hunt 2026-07-27 M17).
    /// </summary>
    private static Result<IncludeResolvedFile, ShaderError> ReadInclude(
        string resolvedPath, string includePath, string? includingFilePath)
    {
        try
        {
            return Result<IncludeResolvedFile, ShaderError>.Ok(
                new IncludeResolvedFile(resolvedPath, File.ReadAllText(resolvedPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IncludeResolvedFile, ShaderError>.Fail(new ShaderError(
                File: includingFilePath ?? string.Empty,
                Line: 0,
                Column: 0,
                Code: "SD0004",
                Message: $"#include \"{includePath}\": file exists but could not be read ({resolvedPath}): {ex.Message}"));
        }
    }
}
