#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Core.Tests.Preprocessor;

public sealed class InMemoryIncludeResolverTests
{
    // -------------------------------------------------------------------------
    // 3.3 — Resolves a file present in the dictionary
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_KnownPath_ReturnsSuccess()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("common.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_KnownPath_ReturnsCorrectText()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("common.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Value.Text.ShouldBe("float4 color;");
    }

    [Fact]
    public void Resolve_KnownPath_ReturnsFilePathInResult()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("common.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Value.FilePath.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Resolve_MultipleEntries_ResolvesCorrectEntry()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["a.fxh"] = "int a;",
            ["b.fxh"] = "int b;",
            ["c.fxh"] = "int c;"
        });

        var resultA = resolver.Resolve("a.fxh", includingFilePath: null, additionalSearchPaths: []);
        var resultC = resolver.Resolve("c.fxh", includingFilePath: null, additionalSearchPaths: []);

        resultA.IsSuccess.ShouldBeTrue();
        resultA.Value.Text.ShouldBe("int a;");

        resultC.IsSuccess.ShouldBeTrue();
        resultC.Value.Text.ShouldBe("int c;");
    }

    // -------------------------------------------------------------------------
    // 3.4 — Returns IncludeNotFound with correct SearchedPaths when absent
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_UnknownPath_ReturnsFailure()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_UnknownPath_ReturnsIncludeNotFoundKind()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Error.Kind.ShouldBe(ShaderErrorKind.IncludeNotFound);
    }

    [Fact]
    public void Resolve_UnknownPath_ErrorContainsRequestedPath()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Error.RequestedPath.ShouldBe("missing.fxh");
    }

    [Fact]
    public void Resolve_UnknownPath_ErrorContainsNonEmptySearchedPaths()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Error.SearchedPaths.ShouldNotBeNull();
        result.Error.SearchedPaths.ShouldNotBeEmpty();
    }

    [Fact]
    public void Resolve_EmptyDictionary_ReturnsIncludeNotFound()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = resolver.Resolve("anything.fxh", includingFilePath: null, additionalSearchPaths: []);

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(ShaderErrorKind.IncludeNotFound);
    }

    [Fact]
    public void Resolve_NullIncludingFilePath_StillReturnsSuccessWhenKnown()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["standalone.fxh"] = "bool flag;"
        });

        var result = resolver.Resolve("standalone.fxh", includingFilePath: null, additionalSearchPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBe("bool flag;");
    }
}
