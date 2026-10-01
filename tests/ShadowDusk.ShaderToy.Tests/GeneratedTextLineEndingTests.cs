#nullable enable

using Shouldly;
using ShadowDusk.ShaderToy.Multipass;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Generated text is compiler input or shipped output, so it must be identical on every host:
/// AppendLine/Environment.NewLine emit CRLF on Windows and LF elsewhere.
/// </summary>
public sealed class GeneratedTextLineEndingTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void ShaderToyMode_Fx_HasNoCarriageReturn()
    {
        ConvertResult r = ShaderToyConverter.Convert(Lines(
            "struct S { float a; };",
            "float f(float x) { return x; }",
            "void mainImage(out vec4 fragColor, in vec2 fragCoord)",
            "{",
            "    S s; s.a = f(iTime);",
            "    fragColor = vec4(s.a, 0.0, 0.0, 1.0);",
            "}"));

        r.Success.ShouldBeTrue();
        r.Fx!.ShouldNotContain("\r", Case.Sensitive);
    }

    [Fact]
    public void MainMode_Fx_HasNoCarriageReturn()
    {
        ConvertResult r = ShaderToyConverter.Convert(Lines(
            "void main()",
            "{",
            "    gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0);",
            "}"));

        r.Success.ShouldBeTrue();
        r.Fx!.ShouldNotContain("\r", Case.Sensitive);
    }

    [Fact]
    public void MultipassManifest_JsonAndMarkdown_HaveNoCarriageReturn()
    {
        string dir = Path.Combine(CorpusLocator.CorpusDir, "multipass");
        MultipassResult result = MultipassConverter.Convert(
            ShaderToyProject.Parse(File.ReadAllText(Path.Combine(dir, "chain2.json"))));

        MultipassManifest.ToJson(result).ShouldNotContain("\r", Case.Sensitive);
        MultipassManifest.ToWiringMarkdown(result).ShouldNotContain("\r", Case.Sensitive);
    }
}
