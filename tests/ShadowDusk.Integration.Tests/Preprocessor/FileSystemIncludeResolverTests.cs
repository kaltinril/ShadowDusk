#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Preprocessor;

/// <summary>
/// Phase 3 §4.4 (closed by Phase 27): <see cref="FileSystemIncludeResolver"/> resolving a
/// REAL <c>.fxh</c> from disk — both directly and end-to-end through
/// <see cref="ShadowDusk.Core.Preprocessor.Preprocessor.Flatten"/>. Functional coverage only:
/// the path-traversal / <c>../</c>-escape SECURITY cases are owned by Phase 25
/// (security hardening) and are deliberately not duplicated here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FileSystemIncludeResolverTests
{
    private static string ShadersDir  => Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders");
    private static string IncludesDir => Path.Combine(ShadersDir, "includes");
    private static string RootFxPath  => Path.Combine(ShadersDir, "MinimalWithInclude.fx");

    [Fact]
    public void Resolve_RealFxhFromDisk_ViaAdditionalSearchPath()
    {
        var resolver = new FileSystemIncludeResolver();

        var result = resolver.Resolve(
            "TestHelper.fxh",
            includingFilePath: RootFxPath,
            additionalSearchPaths: [IncludesDir]);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "the header exists on disk");
        result.Value.Text.ShouldContain("ApplyIdentity", Case.Sensitive, "the resolved text must be the real on-disk header contents");
        Path.GetFullPath(result.Value.FilePath).ShouldBe(
            Path.GetFullPath(Path.Combine(IncludesDir, "TestHelper.fxh")), customMessage: "the resolved path must be the canonicalized on-disk location");
    }

    [Fact]
    public void Resolve_RealFxhFromDisk_ViaIncludingFileSiblingDirectory()
    {
        // Sibling-directory rule: a file inside includes/ can include TestHelper.fxh
        // with no additional search paths at all.
        string includingFile = Path.Combine(IncludesDir, "SomeShader.fx"); // need not exist
        var resolver = new FileSystemIncludeResolver();

        var result = resolver.Resolve(
            "TestHelper.fxh",
            includingFilePath: includingFile,
            additionalSearchPaths: []);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "sibling-directory resolution must find the header");
        result.Value.Text.ShouldContain("ApplyIdentity", Case.Sensitive);
    }

    [Fact]
    public void Resolve_MissingFxh_ReturnsIncludeNotFoundWithSearchedPaths()
    {
        var resolver = new FileSystemIncludeResolver();

        var result = resolver.Resolve(
            "DoesNotExist.fxh",
            includingFilePath: RootFxPath,
            additionalSearchPaths: [IncludesDir]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(ShaderErrorKind.IncludeNotFound);
        result.Error.RequestedPath.ShouldBe("DoesNotExist.fxh");
        result.Error.SearchedPaths.ShouldNotBeEmpty("the diagnostic must say where it looked (Core Design Constraint 5)");
        result.Error.Message.ShouldContain("DoesNotExist.fxh", Case.Sensitive, "the diagnostic must name the unresolvable include");
    }

    [Fact]
    public async Task Flatten_RealFxWithDiskInclude_InlinesHeaderText()
    {
        // End-to-end through the preprocessor, exactly as CompilationPipeline drives it:
        // the #include "TestHelper.fxh" in MinimalWithInclude.fx is resolved from disk via
        // the additional include path and its text is inlined into the flattened output.
        string source = await File.ReadAllTextAsync(RootFxPath);
        var preprocessor = new ShadowDusk.Core.Preprocessor.Preprocessor();

        var result = preprocessor.Flatten(
            source,
            RootFxPath,
            PlatformMacros.For(PlatformTarget.OpenGL),
            new FileSystemIncludeResolver(),
            [IncludesDir]);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "the include resolves from disk");
        result.Value.Text.ShouldContain("ApplyIdentity", Case.Sensitive, "the header body must be inlined");
        result.Value.Text.ShouldNotContain("#include", Case.Sensitive, "flattening must leave no unresolved #include directives");
    }
}
