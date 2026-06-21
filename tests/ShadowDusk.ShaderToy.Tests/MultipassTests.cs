using System.Text.Json;
using FluentAssertions;
using ShadowDusk.ShaderToy.Multipass;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit + golden coverage for the BATCH multipass-export mode (Phase 46): parsing the ShaderToy
/// multi-tab export JSON, converting each render tab via the EXISTING single-pass converter, resolving
/// channel wiring (buffer / feedback / texture / unsupported), the canonical execution order, and
/// the emitted per-pass <c>.fx</c> + <c>manifest.json</c> goldens. Two hand-authored OWN export
/// fixtures (<c>chain2.json</c>, <c>feedback.json</c>) drive this — no third-party shader is used.
///
/// This suite is pure / in-memory (no child process). The companion assertion that each per-pass
/// <c>.fx</c> COMPILES on OpenGL via the real ShadowDusk CLI lives in
/// <c>ShadowDusk.Integration.Tests.Cli.CliMultipassCompileTest</c> (Phase 47, Decision F), keeping this
/// project child-process-free.
/// </summary>
public sealed class MultipassTests
{
    private static string MultipassDir => Path.Combine(CorpusLocator.CorpusDir, "multipass");
    private static string Chain2Json => File.ReadAllText(Path.Combine(MultipassDir, "chain2.json"));
    private static string FeedbackJson => File.ReadAllText(Path.Combine(MultipassDir, "feedback.json"));

    private static MultipassResult ConvertChain2() =>
        MultipassConverter.Convert(ShaderToyProject.Parse(Chain2Json));

    private static MultipassResult ConvertFeedback() =>
        MultipassConverter.Convert(ShaderToyProject.Parse(FeedbackJson));

    // ── Parsing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Chain2_ReadsAllPassesAndFields()
    {
        ShaderToyProject project = ShaderToyProject.Parse(Chain2Json);

        project.Name.Should().Contain("chain2");
        // Common + Buffer A + Image + Sound = 4 raw passes.
        project.Passes.Should().HaveCount(4);
        project.Common.Should().NotBeNull();
        project.Common!.Type.Should().Be(ShaderToyPassType.Common);

        ShaderToyPass image = project.Passes.Single(p => p.Name == "Image");
        image.Type.Should().Be(ShaderToyPassType.Image);
        image.Inputs.Should().HaveCount(2);
        ShaderToyInput ch0 = image.Inputs.Single(i => i.Channel == 0);
        ch0.Ctype.Should().Be(ShaderToyChannelType.Buffer);
        ch0.Id.Should().Be("bufferA-out");
        ch0.Sampler.Should().NotBeNull();
        ch0.Sampler!.Wrap.Should().Be("clamp");
        ch0.Sampler.Filter.Should().Be("linear");
    }

    [Fact]
    public void Parse_InvalidJson_FailsGracefully()
    {
        bool ok = ShaderToyProject.TryParse("{ not valid json", out ShaderToyProject? project, out string? error);
        ok.Should().BeFalse();
        project.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_UnknownPassType_Throws()
    {
        const string json = """
        { "ver": "0.1", "renderpass": [ { "name": "X", "type": "raytrace", "code": "" } ] }
        """;
        Action act = () => ShaderToyProject.Parse(json);
        act.Should().Throw<JsonException>();
    }

    // ── Pass count / order ──────────────────────────────────────────────────────

    [Fact]
    public void Convert_Chain2_RenderedPassesAreBufferThenImage()
    {
        MultipassResult result = ConvertChain2();

        // Only the buffer + image passes are rendered; Common and Sound are not.
        result.Passes.Select(p => p.Name).Should().Equal("Buffer A", "Image");
        result.Passes.Last().Name.Should().Be("Image", "the Image pass is always last (renders to screen)");
    }

    [Fact]
    public void Convert_MultipleBuffers_OrderedByNameThenImageLast()
    {
        // Buffer B before Image, both buffers (out of source order) sorted A,B; Image last.
        const string json = """
        {
          "ver": "0.1",
          "renderpass": [
            { "name": "Image", "type": "image", "code": "void mainImage(out vec4 c, in vec2 f){ c = vec4(1.0); }", "inputs": [], "outputs": [] },
            { "name": "Buffer B", "type": "buffer", "code": "void mainImage(out vec4 c, in vec2 f){ c = vec4(0.5); }", "inputs": [], "outputs": [] },
            { "name": "Buffer A", "type": "buffer", "code": "void mainImage(out vec4 c, in vec2 f){ c = vec4(0.0); }", "inputs": [], "outputs": [] }
          ]
        }
        """;
        MultipassResult result = MultipassConverter.Convert(ShaderToyProject.Parse(json));
        result.Passes.Select(p => p.Name).Should().Equal("Buffer A", "Buffer B", "Image");
    }

    // ── Common is prepended to passes ──────────────────────────────────────────

    [Fact]
    public void Convert_Chain2_CommonHelperIsPrependedToEachPass()
    {
        MultipassResult result = ConvertChain2();

        // Image calls tint() which is declared ONLY in the Common tab; conversion must succeed because
        // the Common code is prepended to the Image pass before translation.
        MultipassPassResult image = result.Passes.Single(p => p.Name == "Image");
        image.Success.Should().BeTrue(
            "Common is prepended so tint() resolves; diagnostics: {0}",
            string.Join("; ", image.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
        image.Fx!.Should().Contain("tint", "the Common helper is emitted into the pass .fx");
    }

    [Fact]
    public void Convert_WithoutCommon_HelperReferenceRejectsLoudly()
    {
        // Same Image code, but no Common tab: tint() is undeclared -> the single-pass converter rejects.
        const string json = """
        {
          "ver": "0.1",
          "renderpass": [
            { "name": "Image", "type": "image",
              "code": "void mainImage(out vec4 fragColor, in vec2 fragCoord){ fragColor = vec4(tint(vec3(1.0)), 1.0); }",
              "inputs": [], "outputs": [] }
          ]
        }
        """;
        MultipassResult result = MultipassConverter.Convert(ShaderToyProject.Parse(json));
        result.Success.Should().BeFalse();
        result.Passes.Single().Success.Should().BeFalse();
    }

    // ── Buffer wiring A -> Image ───────────────────────────────────────────────

    [Fact]
    public void Convert_Chain2_ImageChannel0WiresToBufferA()
    {
        MultipassResult result = ConvertChain2();

        MultipassPassResult image = result.Passes.Single(p => p.Name == "Image");
        ChannelWiring ch0 = image.Channels.Single(c => c.Channel == 0);
        ch0.Kind.Should().Be(ChannelSourceKind.BufferPass);
        ch0.IsFeedback.Should().BeFalse();
        ch0.SourcePassName.Should().Be("Buffer A");
        ch0.SourceOutputFile.Should().Be("BufferA.fx");
        ch0.Wrap.Should().Be("clamp");
        ch0.Filter.Should().Be("linear");
    }

    // ── Feedback channel detection ─────────────────────────────────────────────

    [Fact]
    public void Convert_Feedback_SelfReferencingBufferIsFeedback()
    {
        MultipassResult result = ConvertFeedback();

        MultipassPassResult bufferA = result.Passes.Single(p => p.Name == "Buffer A");
        bufferA.HasFeedback.Should().BeTrue();
        ChannelWiring ch0 = bufferA.Channels.Single(c => c.Channel == 0);
        ch0.Kind.Should().Be(ChannelSourceKind.Feedback);
        ch0.IsFeedback.Should().BeTrue();
        ch0.SourcePassName.Should().Be("Buffer A", "feedback source is the pass itself");

        result.HasFeedback.Should().BeTrue();

        // The Image's iChannel0 also reads Buffer A but is NOT feedback (different pass).
        MultipassPassResult image = result.Passes.Single(p => p.Name == "Image");
        image.Channels.Single(c => c.Channel == 0).Kind.Should().Be(ChannelSourceKind.BufferPass);
    }

    [Fact]
    public void Convert_Feedback_ExternalTextureChannelResolved()
    {
        MultipassResult result = ConvertFeedback();
        MultipassPassResult image = result.Passes.Single(p => p.Name == "Image");
        ChannelWiring ch1 = image.Channels.Single(c => c.Channel == 1);
        ch1.Kind.Should().Be(ChannelSourceKind.Texture);
        ch1.TextureSrc.Should().Be("/media/a/rocks.jpg");
        ch1.Note.Should().Contain("supply your own");
    }

    // ── Sound / cubemap skipped with a warning ─────────────────────────────────

    [Fact]
    public void Convert_Chain2_SoundPassSkippedWithWarning()
    {
        MultipassResult result = ConvertChain2();

        result.Passes.Should().NotContain(p => p.Name == "Sound");
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Sound", StringComparison.OrdinalIgnoreCase) &&
            d.Message.Contains("out of v1 scope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Convert_CubemapPassSkippedWithWarning()
    {
        const string json = """
        {
          "ver": "0.1",
          "renderpass": [
            { "name": "Image", "type": "image", "code": "void mainImage(out vec4 c, in vec2 f){ c = vec4(1.0); }", "inputs": [], "outputs": [] },
            { "name": "Cubemap A", "type": "cubemap", "code": "void mainCubemap(out vec4 c, in vec2 f, in vec3 d){ c = vec4(0.0); }", "inputs": [], "outputs": [] }
          ]
        }
        """;
        MultipassResult result = MultipassConverter.Convert(ShaderToyProject.Parse(json));
        result.Passes.Should().NotContain(p => p.Name == "Cubemap A");
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("Cubemap A", StringComparison.Ordinal));
    }

    // ── Unsupported channel ctype warned ───────────────────────────────────────

    [Fact]
    public void Convert_Chain2_KeyboardChannelWarnedAndUnbound()
    {
        MultipassResult result = ConvertChain2();

        MultipassPassResult image = result.Passes.Single(p => p.Name == "Image");
        ChannelWiring ch1 = image.Channels.Single(c => c.Channel == 1);
        ch1.Kind.Should().Be(ChannelSourceKind.Unsupported);
        image.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("keyboard", StringComparison.OrdinalIgnoreCase) &&
            d.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    // ── Each emitted .fx is non-null + success ─────────────────────────────────

    [Theory]
    [InlineData("chain2")]
    [InlineData("feedback")]
    public void Convert_EveryRenderedPassEmitsFx(string fixture)
    {
        MultipassResult result = fixture == "chain2" ? ConvertChain2() : ConvertFeedback();
        result.Success.Should().BeTrue();
        result.Passes.Should().NotBeEmpty();
        foreach (MultipassPassResult pass in result.Passes)
        {
            pass.Success.Should().BeTrue("'{0}' must convert", pass.Name);
            pass.Fx.Should().NotBeNullOrWhiteSpace();
            pass.Fx!.Should().Contain("technique", "each pass .fx is a complete effect");
        }
    }

    // ── Goldens: per-pass .fx + manifest.json ──────────────────────────────────

    public static IEnumerable<object[]> Fixtures()
    {
        yield return new object[] { "chain2" };
        yield return new object[] { "feedback" };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EmittedFx_MatchGoldens(string fixture)
    {
        MultipassResult result = fixture == "chain2" ? ConvertChain2() : ConvertFeedback();

        foreach (MultipassPassResult pass in result.Passes)
        {
            string actual = CorpusLocator.NormalizeNewlines(pass.Fx!);
            string goldenPath = Path.Combine(GoldenDir(fixture), pass.OutputFileName);
            AssertGolden(goldenPath, actual, $"{fixture}/{pass.OutputFileName}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Manifest_MatchesGolden(string fixture)
    {
        MultipassResult result = fixture == "chain2" ? ConvertChain2() : ConvertFeedback();
        string actual = CorpusLocator.NormalizeNewlines(MultipassManifest.ToJson(result));
        string goldenPath = Path.Combine(GoldenDir(fixture), "manifest.json");
        AssertGolden(goldenPath, actual, $"{fixture}/manifest.json");
    }

    private static string GoldenDir(string fixture) =>
        Path.Combine(CorpusLocator.GoldenDir, "multipass", fixture);

    private static void AssertGolden(string goldenPath, string actual, string label)
    {
        if (CorpusLocator.UpdateGoldens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual);
            return;
        }

        File.Exists(goldenPath).Should().BeTrue(
            "golden '{0}' must exist; run once with SHADERTOY2FX_UPDATE_GOLDENS=1 to generate it", goldenPath);
        string expected = CorpusLocator.NormalizeNewlines(File.ReadAllText(goldenPath));
        actual.Should().Be(expected, "the emitted output for '{0}' must match its committed golden", label);
    }
}
