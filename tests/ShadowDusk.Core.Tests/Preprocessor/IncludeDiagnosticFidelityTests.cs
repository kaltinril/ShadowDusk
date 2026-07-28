#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Core.Tests.Preprocessor;

/// <summary>
/// The preprocessor stamps the <c>#include</c> line number onto a resolver's failure (the
/// resolver is handed only a path and cannot know it), but it must DECORATE, not REPLACE.
/// Rewriting every resolver failure into <c>SD0001</c> "cannot find include" made
/// <c>SD0004</c> ("exists but could not be read") unreachable — a locked or ACL-denied
/// header told the user a file that is right there was not found — and silently discarded
/// whatever diagnostic a consumer's own <c>IIncludeResolver</c> returned.
/// </summary>
public sealed class IncludeDiagnosticFidelityTests
{
    /// <summary>A resolver that always fails with the caller-supplied diagnostic.</summary>
    private sealed class FailingResolver(ShaderError error) : IIncludeResolver
    {
        public Result<IncludeResolvedFile, ShaderError> Resolve(
            string includePath, string? includingFilePath, IReadOnlyList<string> additionalSearchPaths)
            => Result<IncludeResolvedFile, ShaderError>.Fail(error);
    }

    private static Result<PreprocessedSource, ShaderError> Flatten(
        IIncludeResolver resolver,
        string source = "#include \"Common.fxh\"\nfloat4 main() : SV_Target { return 0; }\n",
        string path = "Shader.fx") =>
        new Core.Preprocessor.Preprocessor().Flatten(
            source, path, PlatformMacros.For(PlatformTarget.OpenGL), resolver, []);

    [Fact]
    public void ResolverSd0004_IsForwardedVerbatim_NotRewrittenToIncludeNotFound()
    {
        var sd0004 = new ShaderError(
            File: string.Empty,
            Line: 0,
            Column: 0,
            Code: "SD0004",
            Message: "#include \"Common.fxh\": file exists but could not be read "
                     + "(C:/proj/Common.fxh): Access to the path is denied.");

        var result = Flatten(new FailingResolver(sd0004));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0004",
            "an unreadable-but-present include must not be reported as 'cannot find'");
        result.Error.Message.Should().Contain("could not be read");
        // The one thing the preprocessor legitimately adds: the location the resolver
        // could not know.
        result.Error.File.Should().Be("Shader.fx");
        result.Error.Line.Should().Be(1);
    }

    [Fact]
    public void CustomResolverDiagnostic_IsNotOverwritten()
    {
        // A consumer-supplied IIncludeResolver is a public extension point; its code and
        // message are its own to report ("never swallow or reformat another compiler's
        // message").
        var custom = new ShaderError(
            File: string.Empty, Line: 0, Column: 0,
            Code: "MYAPP0042",
            Message: "virtual include store is offline");

        var result = Flatten(new FailingResolver(custom));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MYAPP0042");
        result.Error.Message.Should().Be("virtual include store is offline");
    }

    [Fact]
    public void GenuineNotFound_StillReportsSd0001WithSearchedPaths()
    {
        // The re-minting path must survive: a real miss keeps SD0001 and its search list.
        var notFound = ShaderError.IncludeNotFound(
            includingFile: string.Empty, line: 0, requested: "Common.fxh",
            searched: ["/a", "/b"]);

        var result = Flatten(new FailingResolver(notFound));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0001");
        result.Error.Kind.Should().Be(ShaderErrorKind.IncludeNotFound);
        result.Error.SearchedPaths.Should().BeEquivalentTo(["/a", "/b"]);
        result.Error.Line.Should().Be(1);
    }

    [Fact]
    public void FileSystemResolver_UnreadableInclude_ReportsSd0004()
    {
        // End-to-end through the shipping resolver: the file EXISTS but cannot be opened.
        string dir = Path.Combine(Path.GetTempPath(), "sd-inc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string header = Path.Combine(dir, "Common.fxh");
        string source = Path.Combine(dir, "Shader.fx");
        try
        {
            File.WriteAllText(header, "#define X 1\n");
            File.WriteAllText(source, "#include \"Common.fxh\"\n");

            // Hold it open with no sharing — the same observable condition as an ACL denial.
            using var _ = new FileStream(header, FileMode.Open, FileAccess.Read, FileShare.None);

            var result = new Core.Preprocessor.Preprocessor().Flatten(
                File.ReadAllText(source), source, PlatformMacros.For(PlatformTarget.OpenGL),
                new FileSystemIncludeResolver(), []);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("SD0004",
                "the header is present — reporting SD0001 would send the user hunting a missing file");
            result.Error.Message.Should().Contain("could not be read");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
