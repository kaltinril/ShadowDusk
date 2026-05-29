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
/// One-shot helper that bootstraps the reference PNGs from ShadowDusk's own
/// current compiled output. The plan originally called for rendering the
/// original-mgfxc-produced golden .mgfx files, but mgfxc rejects all 9 Phase
/// 15 fixtures (SM 4.0/5.0 + DepthBufferEnable parser error), so we anchor on
/// our own current output. Future regressions in ShadowDusk's GLSL emission
/// will be caught by <see cref="ImageRegressionTests"/>.
///
/// <para>
/// Skipped by default. Set the environment variable
/// <c>SHADOWDUSK_UPDATE_GOLDEN=1</c> before running tests to (re-)generate
/// the PNGs. The PNGs are written to the actual source tree at
/// <c>tests/fixtures/reference-images/OpenGL/&lt;stem&gt;&lt;suffix&gt;.png</c>
/// (NOT into the test bin output) so they can be checked into git.
/// </para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
public sealed class ReferenceImageGenerator : IClassFixture<GlContextFixture>
{
    private readonly GlContextFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public ReferenceImageGenerator(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    [Fact]
    public async Task UpdateReferenceImages()
    {
        if (Environment.GetEnvironmentVariable("SHADOWDUSK_UPDATE_GOLDEN") != "1")
        {
            _output.WriteLine("Skipped: set SHADOWDUSK_UPDATE_GOLDEN=1 to regenerate reference images.");
            return;
        }

        if (_fixture.IsSkipped)
        {
            _output.WriteLine($"Skipped: {_fixture.SkipReason}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        CancellationToken ct = cts.Token;

        string repoRoot       = TestPaths.FindRepoRoot();
        string fixtureSrcDir  = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string referencesDir  = Path.Combine(repoRoot, "tests", "fixtures", "reference-images", "OpenGL");
        Directory.CreateDirectory(referencesDir);

        _output.WriteLine($"Repo root:       {repoRoot}");
        _output.WriteLine($"Fixture source:  {fixtureSrcDir}");
        _output.WriteLine($"References out:  {referencesDir}");

        // 1. Async phase: compile every fixture's .fx -> .mgfx upfront. No GL
        //    work happens here, so thread-hopping is fine.
        var compiledFixtures = new List<(string Stem, byte[] Mgfx, SceneDescriptor Descriptor)>();
        foreach (var (stem, descriptor) in SceneCatalog.All)
        {
            string fxPath = Path.Combine(fixtureSrcDir, stem + ".fx");
            if (!File.Exists(fxPath))
            {
                _output.WriteLine($"[skip] {stem}: fixture source not found at {fxPath}");
                continue;
            }

            byte[] mgfx = await CompileFixtureToMgfxAsync(fxPath, ct);
            compiledFixtures.Add((stem, mgfx, descriptor));
        }

        // 2. Sync phase: make the GL context current, do all rendering, save
        //    PNGs. No awaits — the thread stays put so the GLFW context stays
        //    valid.
        using var ctxGuard = _fixture.MakeContextCurrent();
        using var fbo      = _fixture.CreateRenderer();
        var renderer       = new ShaderSceneRenderer(_fixture.Gl, fbo);

        int totalGenerated = 0;
        foreach (var (stem, mgfx, descriptor) in compiledFixtures)
        {
            foreach (var render in descriptor.Renders)
            {
                GlslShaderPair shaders;
                try
                {
                    shaders = GlslShaderExtractor.Extract(mgfx, render.TechniqueIndex, render.PassIndex);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[err]  {stem}: GLSL extraction failed: {ex.Message}");
                    throw;
                }

                bool   blending = string.Equals(stem, "render-states", StringComparison.Ordinal);
                byte[] rgba;
                try
                {
                    rgba = renderer.Render(shaders, render, enableBlending: blending);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[err]  {stem}{render.OutputStemSuffix}: render failed: {ex.Message}");
                    throw;
                }

                string outPath = Path.Combine(referencesDir, stem + render.OutputStemSuffix + ".png");
                SavePng(rgba, outPath);
                long sizeBytes = new FileInfo(outPath).Length;
                _output.WriteLine($"[ok]   {stem}{render.OutputStemSuffix} -> {outPath} ({sizeBytes} bytes)");
                totalGenerated++;
            }
        }

        _output.WriteLine($"Generated {totalGenerated} reference image(s).");
        totalGenerated.Should().BeGreaterThan(0, "at least one fixture should produce a reference PNG");
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

    private static void SavePng(byte[] rgba, string path)
    {
        // rgba is already top-left origin (OffscreenRenderer flipped rows).
        using var image = Image.LoadPixelData<Rgba32>(rgba, OffscreenRenderer.Width, OffscreenRenderer.Height);
        image.SaveAsPng(path);
    }
}
