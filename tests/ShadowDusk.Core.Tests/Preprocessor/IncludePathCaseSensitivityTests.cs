#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;
using HlslPreprocessor = ShadowDusk.Core.Preprocessor.Preprocessor;

namespace ShadowDusk.Core.Tests.Preprocessor;

/// <summary>
/// Bug-hunt 2026-07-27 N17 (Android / case-sensitive-APFS half). The preprocessor used to pick
/// its include comparer from the host OS (<c>OperatingSystem.IsLinux() ? Ordinal :
/// OrdinalIgnoreCase</c>), which is wrong on two hosts ShadowDusk really ships to: Android's
/// file system is case-SENSITIVE (and <c>IsLinux()</c> is false there), and APFS can be
/// formatted case-sensitive.
///
/// <para>The comparer now asks an <see cref="IIncludePathCanonicalizer"/> instead, which is
/// what makes both branches drivable from a pure unit test on any host — no Android device and
/// no re-formatted volume required.</para>
/// </summary>
public sealed class IncludePathCaseSensitivityTests
{
    private static MacroSet Macros => PlatformMacros.For(PlatformTarget.OpenGL);

    // ---------------------------------------------------------------------
    // Test doubles: the two file-system behaviours, and "cannot tell".
    // ---------------------------------------------------------------------

    /// <summary>A case-sensitive volume: every spelling is its own file (Android, Linux, case-sensitive APFS).</summary>
    private sealed class CaseSensitiveFileSystem : IIncludePathCanonicalizer
    {
        private readonly HashSet<string> _files;

        public CaseSensitiveFileSystem(params string[] files)
            => _files = new HashSet<string>(files, StringComparer.Ordinal);

        public string? TryGetOnDiskPath(string path) => _files.Contains(path) ? path : null;
    }

    /// <summary>A case-insensitive volume: every spelling collapses onto the one real name (default Windows/macOS).</summary>
    private sealed class CaseInsensitiveFileSystem : IIncludePathCanonicalizer
    {
        private readonly Dictionary<string, string> _files;

        public CaseInsensitiveFileSystem(params string[] files)
        {
            _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
                _files[file] = file;
        }

        public string? TryGetOnDiskPath(string path) => _files.TryGetValue(path, out string? real) ? real : null;
    }

    /// <summary>A store that cannot report a spelling at all (a virtual/in-memory file set).</summary>
    private sealed class UnknowableFileSystem : IIncludePathCanonicalizer
    {
        public string? TryGetOnDiskPath(string path) => null;
    }

    private static Result<PreprocessedSource, ShaderError> Flatten(
        IIncludePathCanonicalizer canonicalizer,
        string rootSource,
        Dictionary<string, string> files,
        string rootPath = "root.fx")
        => new HlslPreprocessor(canonicalizer).Flatten(
            rootSource,
            originalFilePath: rootPath,
            macros: Macros,
            includeResolver: new InMemoryIncludeResolver(files),
            additionalPaths: []);

    // ---------------------------------------------------------------------
    // The defect: two DISTINCT headers differing only by case, on a
    // case-sensitive file system.
    // ---------------------------------------------------------------------

    [Fact]
    public void CaseSensitiveFileSystem_PragmaOnceInOneHeader_DoesNotSuppressItsCaseTwin()
    {
        // Common.fxh and common.fxh are two different files on Android. The old comparer
        // folded them together, so the first one's `#pragma once` silently swallowed the
        // second one's declarations.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Common.fxh"] = "#pragma once\nfloat upper_case_decl;\n",
            ["common.fxh"] = "#pragma once\nfloat lower_case_decl;\n",
        };

        var result = Flatten(
            new CaseSensitiveFileSystem("Common.fxh", "common.fxh"),
            "#include \"Common.fxh\"\n#include \"common.fxh\"\nfloat root;\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Text.ShouldContain("upper_case_decl", Case.Sensitive);
        result.Value.Text.ShouldContain(
            "lower_case_decl",
            Case.Sensitive,
            "on a case-sensitive file system these are two distinct headers, so the first one's "
            + "#pragma once must not suppress the second");
    }

    [Fact]
    public void CaseInsensitiveFileSystem_PragmaOnce_StillSuppressesTheSameFileSpelledDifferently()
    {
        // The behaviour a Windows/macOS consumer has today, unchanged: one file, two spellings.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Common.fxh"] = "#pragma once\nfloat only_once_decl;\n",
        };

        var result = Flatten(
            new CaseInsensitiveFileSystem("Common.fxh"),
            "#include \"Common.fxh\"\n#include \"common.fxh\"\nfloat root;\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        CountOccurrences(result.Value.Text, "only_once_decl").ShouldBe(
            1,
            "on a case-insensitive volume both spellings are one file, so #pragma once still applies");
    }

    [Fact]
    public void CaseSensitiveFileSystem_ChainThroughACaseTwin_IsNotReportedAsACycle()
    {
        // root -> Helper.fxh -> helper.fxh is a legal three-file chain on Android; the old
        // comparer saw the second edge re-entering the include stack and failed SD0002.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Helper.fxh"] = "#include \"helper.fxh\"\nfloat upper_helper;\n",
            ["helper.fxh"] = "float lower_helper;\n",
        };

        var result = Flatten(
            new CaseSensitiveFileSystem("Helper.fxh", "helper.fxh"),
            "#include \"Helper.fxh\"\nfloat root;\n",
            files);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? result.Error.Code + ": " + result.Error.Message : "");
        result.Value.Text.ShouldContain("upper_helper", Case.Sensitive);
        result.Value.Text.ShouldContain("lower_helper", Case.Sensitive);
    }

    [Fact]
    public void CaseInsensitiveFileSystem_SelfIncludeSpelledDifferently_IsStillACycle()
    {
        // The Phase-3 regression this whole comparer exists for: on a case-insensitive volume
        // a differently-cased self-include IS a cycle and must stay SD0002.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Loop.fxh"] = "#include \"loop.fxh\"\nfloat body;\n",
        };

        var result = Flatten(
            new CaseInsensitiveFileSystem("Loop.fxh"),
            "#include \"Loop.fxh\"\nfloat root;\n",
            files);

        result.IsFailure.ShouldBeTrue("a self-include through a case variant is a real cycle here");
        result.Error.Code.ShouldBe("SD0002");
    }

    [Fact]
    public void UnknowableFileSystem_FallsBackToOrdinal_AndKeepsBothSpellings()
    {
        // An in-memory/virtual file set reports no on-disk spelling. Ordinal is the
        // conservative answer: never merge two paths that might be different files.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Common.fxh"] = "#pragma once\nfloat upper_case_decl;\n",
            ["common.fxh"] = "#pragma once\nfloat lower_case_decl;\n",
        };

        var result = Flatten(
            new UnknowableFileSystem(),
            "#include \"Common.fxh\"\n#include \"common.fxh\"\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Text.ShouldContain("upper_case_decl", Case.Sensitive);
        result.Value.Text.ShouldContain("lower_case_decl", Case.Sensitive);
    }

    // ---------------------------------------------------------------------
    // SD0008 — the portability warning for the shape that only works here.
    // ---------------------------------------------------------------------

    [Fact]
    public void CaseInsensitiveFileSystem_MisspelledIncludeCase_WarnsSd0008()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Shared/Macros.fxh"] = "float helper;\n",
        };

        var result = Flatten(
            new CaseInsensitiveFileSystem("Shared/Macros.fxh"),
            "#include \"shared/macros.fxh\"\nfloat root;\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");

        ShaderError warning = result.Value.Warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe("SD0008");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.File.ShouldBe("root.fx");
        warning.Line.ShouldBe(1, "the warning must point at the #include directive itself");
        warning.RequestedPath.ShouldBe("shared/macros.fxh");
        warning.Message.ShouldContain("Shared/Macros.fxh", Case.Sensitive, "the on-disk spelling is the fix");
        warning.Message.ShouldContain("Android", Case.Sensitive, "the message must say where it will break");
    }

    [Fact]
    public void CaseInsensitiveFileSystem_CorrectlyCasedInclude_DoesNotWarn()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Shared/Macros.fxh"] = "float helper;\n",
        };

        var result = Flatten(
            new CaseInsensitiveFileSystem("Shared/Macros.fxh"),
            "#include \"Shared/Macros.fxh\"\nfloat root;\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void CaseSensitiveFileSystem_NeverWarns_BecauseTheSpellingAlreadyMatched()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Common.fxh"] = "float helper;\n",
        };

        var result = Flatten(
            new CaseSensitiveFileSystem("Common.fxh"),
            "#include \"Common.fxh\"\nfloat root;\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Warnings.ShouldBeEmpty(
            "on a case-sensitive volume a resolved include is already spelled the way the file is");
    }

    [Fact]
    public void CaseOnlyDifferenceAboveTheIncludeSpelling_DoesNotWarn()
    {
        // Only the segments the directive itself spells are portable. The absolute prefix is
        // the author's own machine layout and never ships, so a case difference there is noise.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Project/Shaders/Common.fxh"] = "float helper;\n",
        };

        var result = Flatten(
            new CaseInsensitiveFileSystem("PROJECT/Shaders/Common.fxh"),
            "#include \"Common.fxh\"\nfloat root;\n",
            files,
            rootPath: "Project/Shaders/root.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Warnings.ShouldBeEmpty(
            "the include directive spelled only 'Common.fxh', which matches the file exactly");
    }

    [Fact]
    public void DiamondInclude_WithACaseMismatch_WarnsOnce()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Left.fxh"]   = "#include \"common.fxh\"\nfloat left;\n",
            ["Right.fxh"]  = "#include \"Common.fxh\"\nfloat right;\n",
            ["Common.fxh"] = "float shared_decl;\n",
        };

        var result = Flatten(
            new CaseInsensitiveFileSystem("Left.fxh", "Right.fxh", "Common.fxh"),
            "#include \"Left.fxh\"\n#include \"Right.fxh\"\n",
            files);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Warnings.Count.ShouldBe(1, "one directive is miscased, and it is reported once");
        result.Value.Warnings[0].File.ShouldBe("Left.fxh");
    }

    // ---------------------------------------------------------------------
    // The equality contract itself.
    // ---------------------------------------------------------------------

    [Fact]
    public void Flatten_WithoutAnInjectedCanonicalizer_StillWorks()
    {
        // The parameterless constructor must keep working for every existing caller; with an
        // in-memory resolver the disk canonicalizer simply reports "unknown" for every path.
        var result = new HlslPreprocessor().Flatten(
            "#include \"a.fxh\"\nfloat root;\n",
            originalFilePath: "root.fx",
            macros: Macros,
            includeResolver: new InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["a.fxh"] = "float a;\n" }),
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        result.Value.Text.ShouldContain("float a;", Case.Sensitive);
        result.Value.Warnings.ShouldBeEmpty();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
