#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ImageTests.GlContext;
using ShadowDusk.ImageTests.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Visual regression suite for the 9 Phase 15 fixture shaders. Each theory
/// row compiles a single fixture with <see cref="EffectCompiler"/> for
/// OpenGL, extracts the GLSL from the resulting .mgfx, renders the standard
/// scene offscreen, and compares the rendered RGBA buffer against the
/// checked-in reference PNG at
/// <c>tests/fixtures/reference-images/OpenGL/&lt;stem&gt;&lt;suffix&gt;.png</c>.
///
/// <para>
/// When no OpenGL 3.3 context is available (e.g., headless CI, missing GLFW
/// native), tests skip cleanly via <see cref="GlContextFixture.IsSkipped"/> —
/// they return without failing so the rest of the suite isn't blocked.
/// </para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
public sealed class ImageRegressionTests : IClassFixture<GlContextFixture>
{
    private readonly GlContextFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public ImageRegressionTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    /// <summary>
    /// One row per (fixture stem, scene index) pair so xunit prints which
    /// shader failed if any assertion fails.
    /// </summary>
    public static IEnumerable<object[]> FixtureScenes()
    {
        foreach (var (stem, descriptor) in SceneCatalog.All)
        {
            for (int i = 0; i < descriptor.Renders.Count; i++)
                yield return new object[] { stem, i };
        }
    }

    [Theory]
    [MemberData(nameof(FixtureScenes))]
    public async Task FixtureRenderMatchesReference(string fixtureStem, int sceneIndex)
    {
        if (_fixture.IsSkipped)
        {
            // Pass with a clearly labelled message rather than fail. xUnit
            // doesn't have a built-in skip mechanism in plain Fact/Theory
            // attributes without Skip; returning early is the lightest option.
            _output.WriteLine($"Skipped (no GL context): {_fixture.SkipReason}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        SceneCatalog.All.Should().ContainKey(fixtureStem, "MemberData was driven by SceneCatalog.All");
        SceneDescriptor descriptor = SceneCatalog.All[fixtureStem];
        sceneIndex.Should().BeInRange(0, descriptor.Renders.Count - 1);

        SceneRender render = descriptor.Renders[sceneIndex];

        // 1. Compile the .fx source fresh with ShadowDusk. All async file IO
        //    and DXC/SPIRV-Cross work happens before we touch GL — once we
        //    enter the GL section below, no awaits, so the thread-local GLFW
        //    context stays valid.
        string repoRoot = TestPaths.FindRepoRoot();
        string fxPath   = Path.Combine(repoRoot, "tests", "fixtures", "shaders", fixtureStem + ".fx");
        File.Exists(fxPath).Should().BeTrue($"fixture source must exist at {fxPath}");

        byte[] mgfx = await CompileFixtureToMgfxAsync(fxPath, ct);

        // 3. Load the reference PNG (also pre-GL).
        string referencePath = ResolveReferenceImagePath(repoRoot, fixtureStem, render.OutputStemSuffix);
        if (!File.Exists(referencePath))
        {
            throw new FileNotFoundException(
                $"Reference image not found at '{referencePath}'. " +
                $"Bootstrap missing PNGs with:  SHADOWDUSK_UPDATE_GOLDEN=1 dotnet test " +
                $"--filter \"FullyQualifiedName~ReferenceImageGenerator\"");
        }

        byte[] expected = LoadPngAsRgba(referencePath);

        // 2. Extract GLSL and render. From here down, no awaits.
        GlslShaderPair shaders  = GlslShaderExtractor.Extract(mgfx, render.TechniqueIndex, render.PassIndex);
        bool           blending = string.Equals(fixtureStem, "render-states", StringComparison.Ordinal);

        // GLFW contexts are thread-local; xUnit may run InitializeAsync and
        // the test body on different threads. Claim the context for this row.
        using var ctxGuard = _fixture.MakeContextCurrent();
        using var fbo      = _fixture.CreateRenderer();
        var renderer       = new ShaderSceneRenderer(_fixture.Gl, fbo);
        byte[] actual      = renderer.Render(shaders, render, enableBlending: blending);

        // 4. Compare.
        ImageComparison comparison = ImageComparer.Compare(expected, actual, render.Tolerance);

        comparison.Matches.Should().BeTrue(
            $"rendered image for '{fixtureStem}{render.OutputStemSuffix}' must match reference within tolerance {render.Tolerance}. " +
            $"Different pixels: {comparison.DifferentPixels}/{comparison.TotalPixels}, " +
            $"max channel delta: {comparison.MaxChannelDelta}. " +
            $"Reference: {referencePath}");
    }

    private static async Task<byte[]> CompileFixtureToMgfxAsync(string fxPath, CancellationToken ct)
    {
        string hlslSource = await File.ReadAllTextAsync(fxPath, ct).ConfigureAwait(false);

        var options = new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        };

        var compiler = new EffectCompiler();
        var result   = await compiler.CompileAsync(hlslSource, options, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Failed to compile fixture: {fxPath}");
            foreach (var e in result.Error)
                sb.AppendLine($"  {e.File}({e.Line},{e.Column}): {e.Code}: {e.Message}");
            throw new InvalidOperationException(sb.ToString());
        }

        return result.Value.Data;
    }

    private static string ResolveReferenceImagePath(string repoRoot, string stem, string suffix)
    {
        // Prefer the source-tree location so devs can also point a viewer at
        // the same PNGs the test reads.
        return Path.Combine(repoRoot, "tests", "fixtures", "reference-images", "OpenGL", stem + suffix + ".png");
    }

    private static byte[] LoadPngAsRgba(string path)
    {
        using var image = Image.Load<Rgba32>(path);

        if (image.Width != OffscreenRenderer.Width || image.Height != OffscreenRenderer.Height)
            throw new InvalidDataException(
                $"Reference image '{path}' has dimensions {image.Width}x{image.Height}, " +
                $"expected {OffscreenRenderer.Width}x{OffscreenRenderer.Height}.");

        var buffer = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(buffer);
        return buffer;
    }
}
