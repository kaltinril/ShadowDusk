#nullable enable

using System.Text;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using ShadowDusk.ContentPipeline;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.MgcbPlugin;

/// <summary>
/// <b>Issue #274: a content build must not write the build machine's path into the effect.</b>
///
/// <para>MGFX v11 stores a source-file string per shader, and DirectX 12 and Vulkan are always
/// v11. MGCB and the 3.8.5 Content Builder hand the processor the effect's <b>absolute</b> path,
/// which the processor used to pass straight through as the embedded name: every DirectX 12 and
/// Vulkan <c>.xnb</c> carried the builder's directory (user name included) and its bytes changed
/// with the checkout location. MonoGame's stock <c>EffectProcessor</c> writes
/// <c>&lt;unknown&gt;</c> in that field (measured on <c>dotnet-mgcb</c> 3.8.5), so that is what
/// the processor writes now, while diagnostics keep the real path.</para>
///
/// <para>The same source file compiles into both delivery shapes
/// (<c>ShadowDusk.MgcbPlugin</c> and <c>ShadowDusk.ContentPipeline</c>), so driving the
/// processor here covers both. The end-to-end half, on a real <c>dotnet mgcb</c> 3.8.5 and a
/// real 3.8.5 <c>ContentBuilder</c> with the field compared against the stock build's, is
/// <c>validation/MgcbPlugin</c> and <c>validation/ContentBuilder</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MgcbPluginSourcePathTests : IClassFixture<CliBinaryFixture>
{
    /// <summary>What MonoGame's stock <c>EffectProcessor</c> writes in the MGFX v11 source-file field.</summary>
    private const string StockSourceFile = "<unknown>";

    private readonly CliBinaryFixture _cli;

    public MgcbPluginSourcePathTests(CliBinaryFixture cli) => _cli = cli;

    /// <summary>
    /// The two always-v11 targets, on a pixel-only effect and on one with a vertex shader (two
    /// shader records, so "every record" is not vacuously "the only record").
    /// </summary>
    public static TheoryData<string, string> V11Cases => new()
    {
        { "Grayscale.fx",      "Vulkan" },
        { "Grayscale.fx",      "DirectX_12" },
        { "VertexAndPixel.fx", "Vulkan" },
        { "VertexAndPixel.fx", "DirectX_12" },
    };

    /// <summary>
    /// The defect itself: the same effect built from two different directories must produce
    /// the same bytes, and those bytes must not name either directory.
    /// </summary>
    [Theory]
    [MemberData(nameof(V11Cases))]
    public void V11OutputDoesNotChangeWithTheSourceDirectory(string fixture, string profile)
    {
        using var first  = new RelocatedFixture(fixture, "checkout-one");
        using var second = new RelocatedFixture(fixture, Path.Combine("a", "much", "deeper", "checkout-two"));

        byte[] fromFirst  = RunProcessor(first.EffectPath, p => p.ShaderProfile = profile);
        byte[] fromSecond = RunProcessor(second.EffectPath, p => p.ShaderProfile = profile);

        fromSecond.ShouldBe(
            fromFirst,
            $"{fixture} for {profile} must compile to the same bytes wherever the source file lives - " +
            "a content build's output must not depend on the checkout location");

        foreach (RelocatedFixture relocated in new[] { first, second })
        {
            ContainsUtf8(fromFirst, relocated.Root).ShouldBeFalse(
                $"the {profile} effect must not carry the build machine's directory ({relocated.Root})");
        }

        ContainsUtf8(fromFirst, fixture).ShouldBeFalse(
            $"the {profile} effect must not carry the source file name either: stock MGCB writes only <unknown>");
    }

    /// <summary>
    /// Exactly what MonoGame's stock <c>EffectProcessor</c> writes, in every shader record.
    /// </summary>
    [Theory]
    [MemberData(nameof(V11Cases))]
    public void V11ShaderRecordsCarryTheStockUnknownSourceFile(string fixture, string profile)
    {
        using var relocated = new RelocatedFixture(fixture, "checkout");

        var reader = MgfxBlobReader.Parse(RunProcessor(relocated.EffectPath, p => p.ShaderProfile = profile));

        reader.MgfxVersion.ShouldBe((byte)11, customMessage: $"{profile} is always MGFX v11");
        reader.Shaders.ShouldNotBeEmpty();
        foreach (MgfxShaderRecord shader in reader.Shaders)
        {
            shader.SourceFile.ShouldBe(
                StockSourceFile,
                $"shader #{shader.Index} of {fixture} for {profile} must carry the stock processor's source-file string");
        }
    }

    /// <summary>
    /// The opt-in MGFX v11 container on an otherwise-v10 target has the same field, so it gets
    /// the same value.
    /// </summary>
    [Fact]
    public void TheOptInV11ContainerCarriesTheStockUnknownSourceFileToo()
    {
        using var relocated = new RelocatedFixture("Grayscale.fx", "checkout");

        byte[] bytes = RunProcessor(relocated.EffectPath, p => p.MgfxVersion = 11);

        var reader = MgfxBlobReader.Parse(bytes);
        reader.MgfxVersion.ShouldBe((byte)11);
        reader.Shaders.ShouldNotBeEmpty();
        reader.Shaders.ShouldAllBe(s => s.SourceFile == StockSourceFile);
        ContainsUtf8(bytes, relocated.Root).ShouldBeFalse();
    }

    /// <summary>
    /// A Debug content build still writes <c>&lt;unknown&gt;</c> in the container's own field.
    /// (Compiler debug info is a separate thing and is deliberately not asserted path-free here:
    /// with debug on, DXC records the source name inside the SPIR-V / DXIL debug info, as the
    /// ShadowDusk CLI and <c>mgfxc /Debug</c> both do.)
    /// </summary>
    [Theory]
    [InlineData("Vulkan")]
    [InlineData("DirectX_12")]
    public void ADebugBuildStillWritesUnknownInTheSourceFileField(string profile)
    {
        using var relocated = new RelocatedFixture("Grayscale.fx", "checkout");

        var reader = MgfxBlobReader.Parse(RunProcessor(relocated.EffectPath, p =>
        {
            p.ShaderProfile = profile;
            p.DebugMode     = EffectProcessorDebugMode.Debug;
        }));

        reader.Shaders.ShouldNotBeEmpty();
        reader.Shaders.ShouldAllBe(s => s.SourceFile == StockSourceFile);
    }

    /// <summary>
    /// The other half of the contract: hiding the path from the OUTPUT must not hide it from
    /// the DIAGNOSTICS. A build error still names the real file, at the real line and column,
    /// in the <c>file(line,col)</c> form MGCB, MSBuild and IDEs turn into a clickable location.
    /// </summary>
    [Theory]
    [InlineData("Vulkan")]
    [InlineData("DirectX_12")]
    public void AShaderErrorStillNamesTheRealFileLineAndColumn(string profile)
    {
        // Line 5, column 12 is the `n` of `notADeclaredThing`: three known lines above it,
        // four spaces of indent, then `return `.
        const string source =
            "Texture2D SpriteTexture;\n" +
            "SamplerState SpriteTextureSampler;\n" +
            "float4 MainPS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target\n" +
            "{\n" +
            "    return notADeclaredThing;\n" +
            "}\n" +
            "technique T { pass P { PixelShader = compile ps_6_0 MainPS(); } }\n";

        using var relocated = new RelocatedFixture("Broken.fx", "checkout", source);

        var exception = Should.Throw<InvalidContentException>(
            () => RunProcessor(relocated.EffectPath, p => p.ShaderProfile = profile));

        exception.Message.ShouldContain("notADeclaredThing", Case.Sensitive);
        // DXC echoes the file name with forward slashes on every host, so the comparison is
        // separator-insensitive; the directory, file, line and column are all exact.
        exception.Message.Replace('\\', '/').ShouldContain(
            $"{relocated.EffectPath.Replace('\\', '/')}(5,12", Case.Sensitive);
        exception.Message.ShouldNotContain(StockSourceFile, Case.Sensitive);
    }

    /// <summary>
    /// What "the plugin adds no compilation logic" means for a v11 target: the processor's bytes
    /// are the real CLI's bytes for the same file, with only the embedded source name (and the
    /// effect key derived from it) different. The CLI keeps writing the path it was given, which
    /// is what <c>mgfxc</c> does, so this also pins that the CLI did NOT change.
    /// </summary>
    [Theory]
    [MemberData(nameof(V11Cases))]
    public async Task V11OutputIsTheClisWithOnlyTheSourceNameReplaced(string fixture, string profile)
    {
        using var relocated = new RelocatedFixture(fixture, "checkout");

        byte[] pluginBytes = RunProcessor(relocated.EffectPath, p => p.ShaderProfile = profile);

        string cliOutput = Path.Combine(relocated.Root, "cli.mgfx");
        var cliResult = await TestHelpers.CompileViaCliAsync(
            relocated.EffectPath, cliOutput, profile, CancellationToken.None, _cli.ExecutablePath);
        cliResult.ExitCode.ShouldBe(0, cliResult.Stderr);

        var cliReader = MgfxBlobReader.Parse(cliResult.Mgfx);
        cliReader.Shaders.ShouldAllBe(
            s => s.SourceFile == relocated.EffectPath,
            "the CLI writes the source path exactly as it was passed (mgfxc parity) - it must not change");

        pluginBytes.ShouldBe(
            MgfxEmbeddedSourceName.Replace(cliResult.Mgfx, relocated.EffectPath, StockSourceFile),
            $"the processor's {profile} bytes for {fixture} must be the CLI's with the embedded source name " +
            "replaced by <unknown> and nothing else changed");
    }

    /// <summary>Runs the real processor over a file on disk and returns the <c>.mgfx</c> bytes.</summary>
    private static byte[] RunProcessor(string effectPath, Action<ShadowDuskEffectProcessor> configure)
    {
        var processor = new ShadowDuskEffectProcessor();
        configure(processor);

        var input = new EffectContent
        {
            // The absolute path, exactly as MGCB's and the Content Builder's importer supply it.
            Identity   = new ContentIdentity(effectPath, "ShadowDusk"),
            EffectCode = File.ReadAllText(effectPath),
        };

        // DesktopGL on purpose: the 3.8.2.1105 TargetPlatform this project compiles against
        // cannot name DesktopVK / WindowsDX12, so the target comes from ShaderProfile (or, for
        // the opt-in v11 case, stays OpenGL).
        CompiledEffectContent output = processor.Process(
            input, new FakeContentProcessorContext(TargetPlatform.DesktopGL));

        return output.GetEffectCode();
    }

    private static bool ContainsUtf8(byte[] haystack, string text)
        => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    /// <summary>
    /// A corpus fixture (and the headers beside it) copied into a unique directory, so a test
    /// controls exactly which absolute path the processor is handed.
    /// </summary>
    private sealed class RelocatedFixture : IDisposable
    {
        private readonly string _base;

        public RelocatedFixture(string fixture, string relativeDirectory, string? source = null)
        {
            _base = Path.Combine(Path.GetTempPath(), "shadowdusk_issue274_" + Guid.NewGuid().ToString("N"));
            Root  = Path.Combine(_base, relativeDirectory);
            Directory.CreateDirectory(Root);

            EffectPath = Path.Combine(Root, fixture);
            if (source is not null)
            {
                File.WriteAllText(EffectPath, source);
                return;
            }

            string fixtures = Path.GetDirectoryName(TestHelpers.FixturePath(fixture))!;
            File.Copy(Path.Combine(fixtures, fixture), EffectPath);
            foreach (string header in Directory.GetFiles(fixtures, "*.fxh"))
                File.Copy(header, Path.Combine(Root, Path.GetFileName(header)));
        }

        /// <summary>The directory the effect was copied into.</summary>
        public string Root { get; }

        /// <summary>The effect's absolute path.</summary>
        public string EffectPath { get; }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { /* non-fatal */ }
        }
    }
}
