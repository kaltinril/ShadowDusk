using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Drives every <c>corpus/reject/*.glsl</c> through the converter and asserts it fails loudly: no
/// <c>.fx</c>, at least one <see cref="DiagnosticSeverity.Error"/>, and a plausible located error.
/// Where the reject README names a specific reason, the diagnostic must mention it (so a regression
/// that rejects for the wrong reason is also caught).
/// </summary>
public sealed class RejectCorpusTests
{
    public static IEnumerable<object[]> RejectShaders() =>
        CorpusLocator.GlslFiles(CorpusLocator.RejectDir)
            .Select(p => new object[] { Path.GetFileName(p), p });

    [Theory]
    [MemberData(nameof(RejectShaders))]
    public void RejectShader_FailsWithLocatedError(string fileName, string path)
    {
        _ = fileName; // surfaced in the test display name only.
        string glsl = File.ReadAllText(path);

        ConvertResult result = ShaderToyConverter.Convert(glsl);

        result.Success.Should().BeFalse("'{0}' contains an out-of-scope construct", fileName);
        result.Fx.Should().BeNull();

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        errors.Should().NotBeEmpty("a rejected shader must emit at least one Error diagnostic");

        // At least one error must carry a plausible 1-based source location.
        errors.Should().Contain(
            e => e.Line > 0 && e.Column > 0,
            "at least one error should point at a real line/column in the source");
    }

    [Theory]
    [MemberData(nameof(RejectShaders))]
    public void RejectReason_MentionsTheNamedConstruct(string fileName, string path)
    {
        string glsl = File.ReadAllText(path);
        ConvertResult result = ShaderToyConverter.Convert(glsl);

        string allText = string.Join(
            " ",
            result.Diagnostics.Select(d => $"{d.Message} {d.Construct}"));

        // Per the reject README, each shader's only out-of-scope construct has a specific reason.
        // Map the filename to a keyword the diagnostic message/construct must mention.
        string expectedKeyword = Path.GetFileNameWithoutExtension(fileName) switch
        {
            "user_struct" => "struct",
            "user_array" => "array",
            "second_entry_cubemap" => "Cubemap",
            "switch_statement" => "switch",
            "macro_paste" => "##",
            "unknown_intrinsic" => "texelFetch",
            "unknown_global" => "RENDERSIZE",
            _ => string.Empty,
        };

        if (expectedKeyword.Length > 0)
        {
            allText.Should().ContainEquivalentOf(
                expectedKeyword,
                "the diagnostic for '{0}' should mention its specific rejection reason", fileName);
        }
    }
}
