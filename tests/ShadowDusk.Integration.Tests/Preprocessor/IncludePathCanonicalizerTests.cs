#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;
using HlslPreprocessor = ShadowDusk.Core.Preprocessor.Preprocessor;

namespace ShadowDusk.Integration.Tests.Preprocessor;

/// <summary>
/// Bug-hunt 2026-07-27 N17 (Android / case-sensitive-APFS half), against a REAL file system.
///
/// <para>Every assertion here is written against what this host's file system <b>measurably
/// does</b>, never against which OS it is. That is the point: the same test body is correct on
/// Windows, on Linux, on either APFS flavour, and on Android, so the fix is verifiable on
/// hardware we do have. The pure unit-test half (both branches driven through an injected
/// <see cref="IIncludePathCanonicalizer"/>) lives in
/// <c>ShadowDusk.Core.Tests/Preprocessor/IncludePathCaseSensitivityTests.cs</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncludePathCanonicalizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shadowdusk-include-case-" + Guid.NewGuid().ToString("N"));

    public IncludePathCanonicalizerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// Measures what THIS volume does, by creating a file and asking for it under a flipped
    /// spelling. No OS check.
    /// </summary>
    private bool VolumeIgnoresCase()
    {
        string probe = Path.Combine(_dir, "CaseProbe.tmp");
        File.WriteAllText(probe, "x");
        return File.Exists(Path.Combine(_dir, "caseprobe.tmp"));
    }

    [Fact]
    public void TryGetOnDiskPath_ReportsTheRealSpelling_ForAMiscasedRequest()
    {
        bool ignoresCase = VolumeIgnoresCase();
        string real = Path.Combine(_dir, "Common.fxh");
        File.WriteAllText(real, "float helper;\n");

        var canonicalizer = new FileSystemIncludePathCanonicalizer();

        canonicalizer.TryGetOnDiskPath(real).ShouldBe(
            real, customMessage: "an exactly-spelled existing path canonicalizes to itself");

        string miscased = Path.Combine(_dir, "common.fxh");
        string? resolved = canonicalizer.TryGetOnDiskPath(miscased);

        if (ignoresCase)
        {
            resolved.ShouldBe(real, customMessage:
                "on a case-insensitive volume the miscased spelling names the same file, so it "
                + "must canonicalize onto the real name");
        }
        else
        {
            resolved.ShouldBeNull(
                "on a case-sensitive volume there is no such file, so there is no spelling to report");
        }
    }

    [Fact]
    public void TryGetOnDiskPath_KeepsTwoGenuineCaseTwinsDistinct()
    {
        if (VolumeIgnoresCase())
            return; // Two case twins cannot exist here; the other test covers this volume.

        string upper = Path.Combine(_dir, "Twin.fxh");
        string lower = Path.Combine(_dir, "twin.fxh");
        File.WriteAllText(upper, "float upper;\n");
        File.WriteAllText(lower, "float lower;\n");

        var canonicalizer = new FileSystemIncludePathCanonicalizer();

        canonicalizer.TryGetOnDiskPath(upper).ShouldBe(upper);
        canonicalizer.TryGetOnDiskPath(lower).ShouldBe(
            lower, customMessage:
            "an exact match must win over the case-insensitive lookup, or two real files collapse "
            + "onto whichever the directory happened to list first");
    }

    [Fact]
    public void TryGetOnDiskPath_ReturnsNull_ForAPathThatDoesNotExist()
        => new FileSystemIncludePathCanonicalizer()
            .TryGetOnDiskPath(Path.Combine(_dir, "nope", "missing.fxh"))
            .ShouldBeNull("nothing on disk means no on-disk spelling");

    [Fact]
    public void Flatten_MiscasedInclude_MatchesWhatThisVolumeActuallyDoes()
    {
        bool ignoresCase = VolumeIgnoresCase();
        File.WriteAllText(Path.Combine(_dir, "Common.fxh"), "float helper_decl;\n");

        string rootPath = Path.Combine(_dir, "root.fx");
        var result = new HlslPreprocessor().Flatten(
            "#include \"common.fxh\"\nfloat root;\n",
            originalFilePath: rootPath,
            macros: PlatformMacros.For(PlatformTarget.OpenGL),
            includeResolver: new FileSystemIncludeResolver(),
            additionalPaths: []);

        if (ignoresCase)
        {
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
            result.Value.Text.ShouldContain("helper_decl", Case.Sensitive);

            ShaderError warning = result.Value.Warnings.ShouldHaveSingleItem();
            warning.Code.ShouldBe("SD0008");
            warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
            warning.Message.ShouldContain("Common.fxh", Case.Sensitive);
        }
        else
        {
            // This is exactly what an Android device (or a case-sensitive APFS volume) does with
            // the shader that compiled on the author's Windows box, and why SD0008 exists.
            result.IsFailure.ShouldBeTrue("there is no file named common.fxh on this volume");
            result.Error.Code.ShouldBe("SD0001");
        }
    }

    [Fact]
    public void Flatten_CorrectlyCasedInclude_NeverWarns()
    {
        File.WriteAllText(Path.Combine(_dir, "Common.fxh"), "float helper_decl;\n");

        var result = new HlslPreprocessor().Flatten(
            "#include \"Common.fxh\"\nfloat root;\n",
            originalFilePath: Path.Combine(_dir, "root.fx"),
            macros: PlatformMacros.For(PlatformTarget.OpenGL),
            includeResolver: new FileSystemIncludeResolver(),
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Text.ShouldContain("helper_decl", Case.Sensitive);
        result.Value.Warnings.ShouldBeEmpty(
            "the spelling matches the file, on every file system");
    }

    [Fact]
    public void Flatten_TwoGenuineCaseTwins_AreBothExpanded_DespitePragmaOnce()
    {
        if (VolumeIgnoresCase())
            return; // Two case twins cannot exist here.

        File.WriteAllText(Path.Combine(_dir, "Twin.fxh"), "#pragma once\nfloat upper_decl;\n");
        File.WriteAllText(Path.Combine(_dir, "twin.fxh"), "#pragma once\nfloat lower_decl;\n");

        var result = new HlslPreprocessor().Flatten(
            "#include \"Twin.fxh\"\n#include \"twin.fxh\"\nfloat root;\n",
            originalFilePath: Path.Combine(_dir, "root.fx"),
            macros: PlatformMacros.For(PlatformTarget.OpenGL),
            includeResolver: new FileSystemIncludeResolver(),
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Text.ShouldContain("upper_decl", Case.Sensitive);
        result.Value.Text.ShouldContain(
            "lower_decl", Case.Sensitive,
            "these are two distinct files here, so one's #pragma once must not suppress the other");
    }
}
