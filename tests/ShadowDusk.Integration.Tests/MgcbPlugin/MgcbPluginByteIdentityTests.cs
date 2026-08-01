#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using ShadowDusk.MgcbPlugin;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.MgcbPlugin;

/// <summary>
/// <b>Phase 29's acceptance bar.</b> The MGCB content-processor plugin is a delivery shape of
/// the ShadowDusk library, not a second compiler: the <c>.mgfx</c> bytes it wraps in
/// <see cref="CompiledEffectContent"/> must be <b>byte-for-byte</b> what the ShadowDusk CLI
/// writes for the same source and target.
///
/// <para>The comparison arm is the <b>real CLI executable</b> (via <see cref="CliBinaryFixture"/>),
/// run as a separate process, not another in-process call to <c>EffectCompiler</c> - otherwise
/// the test would only be comparing the library to itself and would stay green if the plugin's
/// MGCB-context-to-<c>CompilerOptions</c> mapping (platform, debug mode, defines) silently
/// changed what gets compiled.</para>
///
/// <para>This runs under <c>dotnet test</c> with no MGCB installed. The end-to-end proof that a
/// REAL <c>dotnet mgcb</c> build produces an <c>.xnb</c> carrying these same bytes lives in the
/// <c>validation/MgcbPlugin</c> driver (see <c>docs/validation-matrix.md</c> section 6) - it
/// needs the <c>dotnet-mgcb</c> tool, which <c>dotnet test</c> does not have.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MgcbPluginByteIdentityTests : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _cli;

    public MgcbPluginByteIdentityTests(CliBinaryFixture cli) => _cli = cli;

    /// <summary>
    /// The fixtures span the shapes that stress the mapping differently: a PS-only sprite
    /// effect, one with a vertex shader, one with multiple textures/samplers, and one with
    /// multiple techniques.
    /// </summary>
    public static TheoryData<string, TargetPlatform, string> Cases => new()
    {
        { "Grayscale.fx",      TargetPlatform.DesktopGL, "OpenGL" },
        { "Grayscale.fx",      TargetPlatform.Windows,   "DirectX_11" },
        { "VertexAndPixel.fx", TargetPlatform.DesktopGL, "OpenGL" },
        { "VertexAndPixel.fx", TargetPlatform.Windows,   "DirectX_11" },
        { "MultiTexture.fx",   TargetPlatform.DesktopGL, "OpenGL" },
        { "MultiTexture.fx",   TargetPlatform.Windows,   "DirectX_11" },
        { "ForwardLighting.fx", TargetPlatform.DesktopGL, "OpenGL" },
        { "SpriteEffect.fx",   TargetPlatform.Windows,   "DirectX_11" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PluginOutputIsByteIdenticalToTheCli(
        string fixture, TargetPlatform platform, string cliProfile)
    {
        byte[] pluginBytes = RunProcessor(fixture, platform);

        var cliResult = await TestHelpers.CompileFixtureAsync(
            fixture, cliProfile, InvocationMode.CliProcess, _cli.ExecutablePath);

        cliResult.ExitCode.ShouldBe(0, $"the CLI must compile {fixture} for {cliProfile}");

        pluginBytes.ShouldBe(
            cliResult.Mgfx,
            $"the MGCB plugin's .mgfx for {fixture} on /platform:{platform} must be byte-for-byte " +
            $"the CLI's for /Profile:{cliProfile} - the plugin adds no compilation logic");
    }

    /// <summary>
    /// The <c>ShaderProfile</c> escape hatch must select the target instead of
    /// <c>/platform:</c>, and still produce the CLI's bytes for that profile. This is the only
    /// way an MGCB consumer can reach DirectX 12 or Vulkan, whose runtimes MGCB's
    /// <see cref="TargetPlatform"/> enum cannot name.
    /// </summary>
    [Theory]
    [InlineData("OpenGL")]
    [InlineData("DirectX_11")]
    [InlineData("Vulkan")]
    public async Task ShaderProfileOverridesThePlatformAndStillMatchesTheCli(string profile)
    {
        // DesktopGL as the platform on purpose: without the override every case here would
        // compile OpenGL, so a broken override would show up as a byte mismatch.
        byte[] pluginBytes = RunProcessor(
            "Grayscale.fx", TargetPlatform.DesktopGL, p => p.ShaderProfile = profile);

        var cliResult = await TestHelpers.CompileFixtureAsync(
            "Grayscale.fx", profile, InvocationMode.CliProcess, _cli.ExecutablePath);

        cliResult.ExitCode.ShouldBe(0);
        pluginBytes.ShouldBe(cliResult.Mgfx);
    }

    /// <summary>
    /// A console platform ShadowDusk has no backend for must fail the build loudly, never
    /// silently emit an OpenGL artifact the console runtime cannot load.
    /// </summary>
    [Fact]
    public void AnUnsupportedPlatformFailsLoudly()
    {
        var exception = Should.Throw<InvalidContentException>(
            () => RunProcessor("Grayscale.fx", TargetPlatform.PlayStation4));

        exception.Message.ShouldContain("SD0501", Case.Sensitive);
        exception.Message.ShouldContain("PlayStation4", Case.Sensitive);
    }

    /// <summary>
    /// A shader error must reach MGCB with the file, line, column, code and the underlying
    /// compiler's own words - the <c>fxc</c>/<c>mgfxc</c> form MSBuild and IDEs parse. This is
    /// the throw-at-the-edge translation of ShadowDusk's <c>Result</c> contract; the text is
    /// produced by the very same formatter the CLI prints through.
    /// </summary>
    [Fact]
    public void AShaderErrorSurfacesWithFileLineAndColumn()
    {
        string source = File.ReadAllText(TestHelpers.FixturePath("Grayscale.fx"))
            .Replace("col.r + col.g + col.b", "col.r + col.g + notADeclaredThing", StringComparison.Ordinal);

        var exception = Should.Throw<InvalidContentException>(
            () => RunProcessor("Grayscale.fx", TargetPlatform.DesktopGL, source: source));

        exception.Message.ShouldContain("notADeclaredThing", Case.Sensitive);
        // file(line,col-col): severity code: message
        exception.Message.ShouldContain("Grayscale.fx(", Case.Sensitive);
        exception.Message.ShouldContain("): error ", Case.Sensitive);
    }

    /// <summary>
    /// <c>#include</c>d files must be registered with MGCB, or an edit to an <c>.fxh</c> would
    /// not trigger a rebuild and the consumer would ship a stale effect.
    /// </summary>
    [Fact]
    public void ResolvedIncludesAreRegisteredAsBuildDependencies()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "shadowdusk_mgcbplugin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string headerPath = Path.Combine(directory, "Shared.fxh");
            File.WriteAllText(headerPath, "#define TINT 0.5f\n");

            string effectPath = Path.Combine(directory, "WithInclude.fx");
            File.WriteAllText(
                effectPath,
                File.ReadAllText(TestHelpers.FixturePath("Grayscale.fx"))
                    .Replace("Texture2D SpriteTexture;", "#include \"Shared.fxh\"\nTexture2D SpriteTexture;",
                             StringComparison.Ordinal));

            var context = new FakeContentProcessorContext(TargetPlatform.DesktopGL);
            var processor = new ShadowDuskEffectProcessor();
            var input = new EffectContent
            {
                Identity   = new ContentIdentity(effectPath, "ShadowDusk"),
                EffectCode = File.ReadAllText(effectPath),
            };

            processor.Process(input, context);

            context.Dependencies.ShouldContain(headerPath);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* non-fatal */ }
        }
    }

    /// <summary>
    /// Runs the real processor over a fixture and returns the <c>.mgfx</c> bytes it produced.
    /// </summary>
    private static byte[] RunProcessor(
        string fixture,
        TargetPlatform platform,
        Action<ShadowDuskEffectProcessor>? configure = null,
        string? source = null)
    {
        string path = TestHelpers.FixturePath(fixture);

        var processor = new ShadowDuskEffectProcessor();
        configure?.Invoke(processor);

        var input = new EffectContent
        {
            Identity   = new ContentIdentity(path, "ShadowDusk"),
            EffectCode = source ?? File.ReadAllText(path),
        };

        CompiledEffectContent output = processor.Process(
            input, new FakeContentProcessorContext(platform));

        return output.GetEffectCode();
    }
}
