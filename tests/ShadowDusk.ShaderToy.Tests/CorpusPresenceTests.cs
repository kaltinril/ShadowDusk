using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Guards that the corpus folders are non-empty. Without this, a future copy/glob mistake (or a
/// renamed/emptied folder) would silently make the data-driven theories run zero cases and stay
/// green — these tests fail loudly instead.
/// </summary>
public sealed class CorpusPresenceTests
{
    [Fact]
    public void AuthoredCorpus_IsNonEmpty()
    {
        CorpusLocator.GlslFiles(CorpusLocator.AuthoredDir)
            .Should().NotBeEmpty("the authored corpus must contain in-subset shaders");
    }

    [Fact]
    public void RejectCorpus_IsNonEmpty()
    {
        CorpusLocator.GlslFiles(CorpusLocator.RejectDir)
            .Should().NotBeEmpty("the reject corpus must contain out-of-scope shaders");
    }

    [Fact]
    public void Cc0Corpus_IsNonEmpty()
    {
        CorpusLocator.GlslFiles(CorpusLocator.Cc0Dir)
            .Should().NotBeEmpty("the cc0 corpus must contain at least one real CC0 shader");
    }
}
