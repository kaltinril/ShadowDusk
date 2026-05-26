#nullable enable

using FluentAssertions;
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

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Resolve_KnownPath_ReturnsCorrectText()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("common.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Value.Text.Should().Be("float4 color;");
    }

    [Fact]
    public void Resolve_KnownPath_ReturnsFilePathInResult()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("common.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Value.FilePath.Should().NotBeNullOrEmpty();
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

        resultA.IsSuccess.Should().BeTrue();
        resultA.Value.Text.Should().Be("int a;");

        resultC.IsSuccess.Should().BeTrue();
        resultC.Value.Text.Should().Be("int c;");
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

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Resolve_UnknownPath_ReturnsIncludeNotFoundKind()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Error.Kind.Should().Be(ShaderErrorKind.IncludeNotFound);
    }

    [Fact]
    public void Resolve_UnknownPath_ErrorContainsRequestedPath()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Error.RequestedPath.Should().Be("missing.fxh");
    }

    [Fact]
    public void Resolve_UnknownPath_ErrorContainsNonEmptySearchedPaths()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float4 color;"
        });

        var result = resolver.Resolve("missing.fxh", includingFilePath: "root/main.fx", additionalSearchPaths: []);

        result.Error.SearchedPaths.Should().NotBeNull();
        result.Error.SearchedPaths.Should().NotBeEmpty();
    }

    [Fact]
    public void Resolve_EmptyDictionary_ReturnsIncludeNotFound()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = resolver.Resolve("anything.fxh", includingFilePath: null, additionalSearchPaths: []);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ShaderErrorKind.IncludeNotFound);
    }

    [Fact]
    public void Resolve_NullIncludingFilePath_StillReturnsSuccessWhenKnown()
    {
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["standalone.fxh"] = "bool flag;"
        });

        var result = resolver.Resolve("standalone.fxh", includingFilePath: null, additionalSearchPaths: []);

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("bool flag;");
    }
}
