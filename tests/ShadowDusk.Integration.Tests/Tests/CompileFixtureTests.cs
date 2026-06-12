#nullable enable

using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

[Trait("Category", "Integration")]
public sealed class CompileFixtureTests : IClassFixture<CliBinaryFixture>
{
    // ProfileId values from MgfxProfile enum: OpenGL=0, DirectX11=1, Vulkan=3
    private const byte ProfileOpenGL    = 0;
    private const byte ProfileDirectX11 = 1;
    private const byte ProfileVulkan    = 3;

    private readonly CliBinaryFixture _cli;

    public CompileFixtureTests(CliBinaryFixture cli) => _cli = cli;

    // Phase 27: each (fixture, profile, mode) cell is compiled exactly ONCE per test
    // run and memoized, so the header [Theory] (both modes) and the CLI-vs-pipeline
    // byte-identity [Theory] share results instead of tripling the heavyweight
    // compile/process-spawn count (the Phase 21 performance concern). Tests in this
    // class run sequentially (one xUnit collection), and the compiles are
    // deterministic by design (DeterminismTests asserts that independently), so
    // sharing results loses nothing.
    private static readonly ConcurrentDictionary<(string Fx, string Profile, InvocationMode Mode), Lazy<Task<CompileResult>>>
        CompileCache = new();

    private Task<CompileResult> GetOrCompileAsync(string fx, string profile, InvocationMode mode, CancellationToken ct)
    {
        string cliPath = _cli.ExecutablePath;
        return CompileCache.GetOrAdd(
            (fx, profile, mode),
            key => new Lazy<Task<CompileResult>>(() =>
                TestHelpers.CompileFixtureAsync(key.Fx, key.Profile, key.Mode, cliPath, ct))).Value;
    }

    private static readonly string[] Fixtures =
    {
        "Minimal.fx",
        "textured.fx",
        "cbuffer.fx",
        "multipass.fx",
        "multitechnique.fx",
        "render-states.fx",
        "annotations.fx",
        "platform-macros.fx",
        "basiceffect-mini.fx",
    };

    private static readonly (string Profile, byte ProfileId)[] Platforms =
    {
        ("OpenGL",     ProfileOpenGL),
        ("DirectX_11", ProfileDirectX11),
        ("Vulkan",     ProfileVulkan),
    };

    // Every (profile, mode) cell runs on every host: the library's DEFAULT DirectX
    // backend is now vkd3d (cross-platform, host-independent), so the CLI-process
    // DirectX rows are no longer Windows-only — both invocation modes use the same
    // backend everywhere, which is exactly what the byte-identity assertion needs.

    public static TheoryData<string, string, byte> AllFixturesAndPlatforms()
    {
        var data = new TheoryData<string, string, byte>();
        foreach (string fixture in Fixtures)
        foreach (var (profile, profileId) in Platforms)
            data.Add(fixture, profile, profileId);
        return data;
    }

    /// <summary>Phase 27 (Phase 15 deferral): the same matrix over BOTH invocation modes.</summary>
    public static TheoryData<string, string, byte, InvocationMode> AllFixturesPlatformsAndModes()
    {
        var data = new TheoryData<string, string, byte, InvocationMode>();
        foreach (string fixture in Fixtures)
        foreach (var (profile, profileId) in Platforms)
        foreach (InvocationMode mode in new[] { InvocationMode.DirectPipeline, InvocationMode.CliProcess })
            data.Add(fixture, profile, profileId, mode);
        return data;
    }

    /// <summary>The (fixture, platform) pairs of the CLI-vs-pipeline byte-identity matrix.</summary>
    public static TheoryData<string, string> CliComparablePairs()
    {
        var data = new TheoryData<string, string>();
        foreach (string fixture in Fixtures)
        foreach (var (profile, _) in Platforms)
            data.Add(fixture, profile);
        return data;
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX_11")]
    [Trait("Platform", "Vulkan")]
    [MemberData(nameof(AllFixturesPlatformsAndModes))]
    public async Task Compile_ProducesValidMgfxHeader(string fx, string profile, byte expectedProfileId, InvocationMode mode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await GetOrCompileAsync(fx, profile, mode, cts.Token);

        result.ExitCode.Should().Be(0, because: $"compilation of '{fx}' for '{profile}' via {mode} should succeed; stderr: {result.Stderr}");
        result.Mgfx.Should().NotBeEmpty(because: "successful compile must produce output bytes");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.MgfxVersion.Should().Be(10);
        reader.ProfileId.Should().Be(expectedProfileId, because: $"profile '{profile}' must produce ProfileId {expectedProfileId}");
    }

    /// <summary>
    /// Phase 27 (the Phase 15 deferral): the CLI is a delivery shape of the SAME library,
    /// so a CLI-process compile must be a transparent equivalent of the in-process
    /// pipeline — same exit code, same (empty) stderr, and BYTE-IDENTICAL .mgfx output.
    /// Asserted, not assumed.
    /// </summary>
    [Theory]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX_11")]
    [Trait("Platform", "Vulkan")]
    [MemberData(nameof(CliComparablePairs))]
    public async Task CliProcess_And_DirectPipeline_ProduceByteIdenticalMgfx(string fx, string profile)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var pipeline = await GetOrCompileAsync(fx, profile, InvocationMode.DirectPipeline, cts.Token);
        var cli      = await GetOrCompileAsync(fx, profile, InvocationMode.CliProcess, cts.Token);

        pipeline.ExitCode.Should().Be(0, because: $"pipeline stderr: {pipeline.Stderr}");
        cli.ExitCode.Should().Be(0, because: $"CLI stderr: {cli.Stderr}");
        cli.Stderr.Should().BeEmpty(because: "a successful CLI compile is silent (the mgfxc contract)");

        cli.Mgfx.Should().Equal(pipeline.Mgfx,
            because: $"the CLI is a delivery shape of the same library — '{fx}' for '{profile}' " +
                     "must produce byte-identical .mgfx through both invocation modes");
    }

    // -------------------------------------------------------------------------
    // minimal.fx — 1 technique, 1 pass, 2 shader blobs
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Minimal_OpenGL_OneTechniqueOnePassTwoBlobs()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("Minimal.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TechniqueCount.Should().Be(1);
        reader.Techniques[0].PassCount.Should().Be(1);
        reader.TotalShaderBlobCount.Should().Be(2);
    }

    // -------------------------------------------------------------------------
    // textured.fx — GLSL output contains sampler2D
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Textured_OpenGL_GlslContainsSampler2D()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("textured.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TotalShaderBlobCount.Should().Be(2);

        // GLSL shader blobs are UTF-8 encoded GLSL text; the combined sampler2D declaration
        // must appear in the pixel shader blob (SPIRV-Cross merges texture + sampler objects).
        bool anyBlobContainsSampler2D = reader.ShaderBlobs
            .Any(blob => Encoding.UTF8.GetString(blob).Contains("sampler2D", StringComparison.Ordinal));
        anyBlobContainsSampler2D.Should().BeTrue(because: "SPIRV-Cross must combine Texture2D+SamplerState into a sampler2D");
    }

    // -------------------------------------------------------------------------
    // cbuffer.fx — parameter reflection: WorldViewProj (64 bytes), Color
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CBuffer_OpenGL_ParameterReflection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("cbuffer.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.ParameterNames.Should().Contain("WorldViewProj");
        reader.ParameterNames.Should().Contain("DiffuseColor");

        // WorldViewProj is a float4x4: 4 columns × 4 rows × 4 bytes = 64 bytes.
        reader.ParameterSizes.Should().ContainKey("WorldViewProj");
        reader.ParameterSizes["WorldViewProj"].Should().Be(64);
    }

    // -------------------------------------------------------------------------
    // multipass.fx — 1 technique, 2 passes, 4 shader blobs (VS+PS per pass)
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Multipass_OpenGL_TwoPassesFourBlobs()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("multipass.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TechniqueCount.Should().Be(1);
        reader.Techniques[0].PassCount.Should().Be(2);
        reader.TotalShaderBlobCount.Should().Be(4);
    }

    // -------------------------------------------------------------------------
    // multitechnique.fx — 3 techniques in declaration order
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Multitechnique_OpenGL_ThreeTechniquesInOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("multitechnique.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TechniqueCount.Should().Be(3);
        reader.Techniques[0].Name.Should().Be("TechA");
        reader.Techniques[1].Name.Should().Be("TechB");
        reader.Techniques[2].Name.Should().Be("TechC");
    }

    // -------------------------------------------------------------------------
    // render-states.fx — CullMode=None, AlphaBlendEnable=true, DepthBufferEnable=false
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task RenderStates_OpenGL_StatesRoundTrip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("render-states.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TechniqueCount.Should().Be(1);
        reader.Techniques[0].PassCount.Should().Be(1);

        // CullMode.None = 1 in MonoGame's CullMode enum (mirrors D3D9: None=1, CW=2, CCW=3).
        reader.CullMode.Should().Be(1, because: "CullMode = None maps to integer value 1 (D3D9 convention)");
        reader.AlphaBlendEnable.Should().BeTrue(because: "AlphaBlendEnable = True was declared in the pass");
        reader.DepthBufferEnable.Should().BeFalse(because: "DepthBufferEnable = False was declared in the pass");
    }

    // -------------------------------------------------------------------------
    // annotations.fx — UIName annotation round-trips through MGFX
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Annotations_OpenGL_UiNameRoundTrips()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("annotations.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.ParameterNames.Should().Contain("TintColor");

        reader.ParameterAnnotations.Should().ContainKey("TintColor");
        var annotations = reader.ParameterAnnotations["TintColor"];
        var uiName = annotations.FirstOrDefault(a => a.Name == "UIName");
        uiName.Name.Should().Be("UIName");
        // The string value is stored verbatim from the HLSL source, including surrounding quotes.
        uiName.StringValue.Should().Contain("Tint Color");
    }

    // -------------------------------------------------------------------------
    // platform-macros.fx — macro injection selects correct code path per platform
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task PlatformMacros_OpenGL_Compiles()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The GLSL macro is injected for the OpenGL target; the #if GLSL branch must compile.
        var result = await TestHelpers.CompileFixtureAsync("platform-macros.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"GLSL macro branch should compile; stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TotalShaderBlobCount.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "DirectX_11")]
    public async Task PlatformMacros_DirectX11_Compiles()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // SM4 macro is injected for DirectX; the #elif SM4 branch must compile.
        var result = await TestHelpers.CompileFixtureAsync("platform-macros.fx", "DirectX_11", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"SM4 macro branch should compile; stderr: {result.Stderr}");
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task PlatformMacros_Vulkan_FallsBackToElseBranch()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Vulkan has neither GLSL nor SM4 defined, so the #else branch is selected.
        var result = await TestHelpers.CompileFixtureAsync("platform-macros.fx", "Vulkan", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"else fallback branch should compile; stderr: {result.Stderr}");
    }

    // -------------------------------------------------------------------------
    // basiceffect-mini.fx — 4 distinct techniques by index
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task BasicEffectMini_OpenGL_FourDistinctTechniques()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await TestHelpers.CompileFixtureAsync("basiceffect-mini.fx", "OpenGL", ct: cts.Token);
        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.TechniqueCount.Should().Be(4);
        reader.Techniques[0].Name.Should().Be("Tech0");
        reader.Techniques[1].Name.Should().Be("Tech1");
        reader.Techniques[2].Name.Should().Be("Tech2");
        reader.Techniques[3].Name.Should().Be("Tech3");

        for (int i = 0; i < 4; i++)
            reader.Techniques[i].PassCount.Should().Be(1, because: $"Tech{i} has exactly one pass");

        // All 4 techniques should produce distinct shader blobs (no deduplication).
        reader.TotalShaderBlobCount.Should().Be(8, because: "4 techniques × 1 pass × 2 shaders (VS+PS) = 8");
    }
}
