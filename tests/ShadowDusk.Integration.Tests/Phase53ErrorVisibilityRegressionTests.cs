#nullable enable

using System.Text.RegularExpressions;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Tests.Fx2;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Permanent regression coverage for Phase 53 (error visibility &amp; GL lowering
/// completeness) — the classes behind the 2026-07 field reports ("shader compilation
/// failed with no detail on GL, works on DX" / "an error on the SpriteBatch call"):
///
/// <list type="bullet">
/// <item>issue #137 — VS body lowerings (round(), early-return do-while) now run on the
/// vertex stage;</item>
/// <item>issue #139 — derivative-using fragment shaders ship the
/// GL_OES_standard_derivatives header (mgfxc parity, fwidth included);</item>
/// <item>issue #140 — a round() nested in another round()'s argument is fully
/// lowered;</item>
/// <item>issue #141 / Phase 53 lint — SD0400 (gradient in divergent loop) and SD0401
/// (SpriteBatch-incompatible interpolant on a PS-only pass) surface as compile-time
/// warnings on stderr with exit 0;</item>
/// <item>the one-call <c>ValidateAsync</c> report shows a GL-only failure next to a DX
/// success — the exact field-report shape.</item>
/// </list>
///
/// Compile-level pins (each fixed emission class also renders through the Windows
/// render gates; these tests keep the class from silently returning).
/// </summary>
[Trait("Category", "Integration")]
public sealed class Phase53ErrorVisibilityRegressionTests
{
    private const byte ProfileOpenGL = 0; // MgfxProfile.OpenGL

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Printable-ASCII view of an effect binary, for structural scans of the embedded GLSL.</summary>
    private static string AsciiOf(byte[] blob) =>
        new(blob.Select(b => (b >= 9 && b <= 126) ? (char)b : ' ').ToArray());

    // -------------------------------------------------------------------------
    // Issue #137 — VS body lowerings.
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Issue137_VsRound_LowersToFloor_NoRoundEvenInVertexGlsl()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue137VsRound.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");
        string ascii = AsciiOf(result.Mgfx);

        ascii.Should().NotContain("roundEven",
            "a VS round() shipping roundEven() is the issue-#137 Mesa/WebGL1 load failure");
        Regex.IsMatch(ascii, @"floor\(\([^)]{0,60}\) \+ 0\.5\)").Should().BeTrue(
            "the VS round() must be lowered to the floor(x + 0.5) form Rule 8 emits");
        ascii.Should().Contain("posFixup",
            "the VS lowering must not disturb the posFixup contract");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Issue137_VsEarlyReturnHelper_NoDoWhileInVertexGlsl()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue137VsEarlyReturn.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");
        string ascii = AsciiOf(result.Mgfx);

        Regex.IsMatch(ascii, @"\bdo\s*\{").Should().BeFalse(
            "a raw do{{...}}while(false) in the VS is the issue-#107/#137 WebGL1/Reach load failure");
        ascii.Should().NotContain("while(false)");
        ascii.Should().NotContain("while (false)");
        Regex.IsMatch(ascii, @"for \(int \w+ = 0; \w+ < 1; \w+\+\+\)").Should().BeTrue(
            "Rule 9b must lower the wrapper to the Appendix-A one-shot for form in the VS too");
        ascii.Should().Contain("posFixup");
        result.Stderr.Should().NotContain("SD0402",
            "the Rule-9b loop is Appendix-A-conformant and must not self-flag the lint");
    }

    // -------------------------------------------------------------------------
    // Issue #140 — nested round().
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Issue140_NestedRound_BothCallsLowered()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue140NestedRound.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");
        string ascii = AsciiOf(result.Mgfx);

        ascii.Should().NotContain("roundEven",
            "the INNER nested round surviving as roundEven() is the issue-#140 load failure");
        ascii.Should().Contain("floor((floor((",
            "both nested calls must lower to the floor(x + 0.5) form");
    }

    // -------------------------------------------------------------------------
    // Issue #139 — GL_OES_standard_derivatives header.
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Issue139_DerivativePs_ShipsStandardDerivativesHeaderBeforePrecisionBlock()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue139DerivativeExtension.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"stderr: {result.Stderr}");
        string ascii = AsciiOf(result.Mgfx);

        int header = ascii.IndexOf("#extension GL_OES_standard_derivatives : enable", StringComparison.Ordinal);
        header.Should().BeGreaterThan(0,
            "mgfxc emits the derivatives extension header and strict ESSL 1.00 requires it");
        int precision = ascii.IndexOf("#ifdef GL_ES", StringComparison.Ordinal);
        precision.Should().BeGreaterThan(header,
            "the header goes FIRST, before the precision block — mgfxc's position");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Issue139_NonDerivativePs_OmitsTheHeader()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Issue140NestedRound.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0);
        AsciiOf(result.Mgfx).Should().NotContain("GL_OES_standard_derivatives",
            "the header is conditional on a derivative builtin being present (mgfxc parity)");
    }

    // -------------------------------------------------------------------------
    // Phase 53 lint — SD0400 / SD0401 warnings on stderr, exit 0, GL only.
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Sd0400_UserGradientInDivergentLoop_WarnsAndStillCompiles()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Sd0400GradientInDivergentLoop.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: "the lint warns, never rejects; stderr: " + result.Stderr);
        result.Mgfx.Should().NotBeEmpty();
        result.Stderr.Should().Contain("warning SD0400",
            "fxc warns X3553 on this shape; staying silent was issue #141");
        result.Stderr.Should().Contain("dFdx");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Sd0401_PsOnlyPassReadingTexCoord1_WarnsAndStillCompiles()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/Sd0401SpriteBatchInterpolant.fx", "OpenGL", ct: cts.Token);

        result.ExitCode.Should().Be(0,
            because: "the lint warns, never rejects; stderr: " + result.Stderr);
        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.Signature.Should().Be("MGFX");
        reader.ProfileId.Should().Be(ProfileOpenGL);

        result.Stderr.Should().Contain("warning SD0401");
        result.Stderr.Should().Contain("TEXCOORD1");
        result.Stderr.Should().Contain("SpriteBatch");
    }

    [Theory]
    [Trait("Platform", "DirectX_11")]
    [InlineData("examples/Sd0400GradientInDivergentLoop.fx")]
    [InlineData("examples/Sd0401SpriteBatchInterpolant.fx")]
    [InlineData("examples/Issue137VsRound.fx")]
    [InlineData("examples/Issue137VsEarlyReturn.fx")]
    [InlineData("examples/Issue140NestedRound.fx")]
    [InlineData("examples/Issue139DerivativeExtension.fx")]
    public async Task Phase53Fixtures_DirectX11_CompileCleanly_NoGlLintOnDx(string fx)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        var result = await TestHelpers.CompileFixtureAsync(fx, "DirectX_11", ct: cts.Token);

        result.ExitCode.Should().Be(0, because: $"'{fx}' must stay DX-compiling; stderr: {result.Stderr}");
        result.Stderr.Should().NotContain("SD040",
            "the GL portability lint is a MonoGame-GL-dialect concept and must not fire on DirectX");
    }

    // -------------------------------------------------------------------------
    // Warnings from an earlier, already-compiled technique must survive a LATER
    // technique's hard failure in the same effect (post-review follow-up: these used
    // to be silently dropped because CompilationPipeline.Fail(error) had no slot to
    // carry along the warnings already accumulated in runWarnings/fnaWarnings).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EarlierTechniquesWarning_SurvivesALaterTechniquesHardFailure()
    {
        // "Warns" compiles cleanly but trips SD0401 (PS-only pass reading TEXCOORD1,
        // an interpolant SpriteBatch's built-in VS never writes). "Broken" — processed
        // SECOND — then hard-fails on a bogus render-state VALUE (SD0011), a failure
        // class that genuinely occurs AFTER earlier stages compiled. (An HLSL-level
        // error would not exercise this: DXC semantically checks the WHOLE file on
        // every entry-point compile, so a bad function body fails the FIRST compile
        // before any warning exists.) The SD0401 warning gathered while compiling
        // "Warns" must still reach the caller alongside the fatal SD0011.
        const string fx = """
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler = sampler_state
            {
                Texture = <SpriteTexture>;
            };

            struct PixelInput
            {
                float4 Color     : COLOR0;
                float2 TexCoord  : TEXCOORD0;
                float2 TexCoord1 : TEXCOORD1;
            };

            float4 WarnsPS(PixelInput input) : COLOR0
            {
                float4 a = tex2D(SpriteTextureSampler, input.TexCoord);
                float4 b = tex2D(SpriteTextureSampler, input.TexCoord1);
                return lerp(a, b, 0.5) * input.Color;
            }

            float4 PlainPS(PixelInput input) : COLOR0
            {
                return tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
            }

            technique Warns
            {
                pass P0
                {
                    PixelShader = compile ps_3_0 WarnsPS();
                }
            }

            technique Broken
            {
                pass P0
                {
                    AlphaBlendEnable = NotAValidValue;
                    PixelShader = compile ps_3_0 PlainPS();
                }
            }
            """;

        IShaderCompiler compiler = new EffectCompiler();
        var options = new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "warnings-then-fail.fx",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await compiler.CompileAsync(fx, options, cts.Token);

        result.IsFailure.Should().BeTrue("the Broken technique has a bogus render-state value");
        result.Error.Should().Contain(
            e => e.Severity == ShaderErrorSeverity.Error && e.Code == "SD0011",
            "the fatal render-state error from the Broken technique must still be reported");
        result.Error.Should().Contain(e => e.Code == "SD0401",
            "the Warns technique's warning, gathered before the later failure, must not be silently dropped");
        result.Error[0].Severity.Should().Be(ShaderErrorSeverity.Error,
            "the fatal error stays FIRST in the array — the actionable line leads");
    }

    // -------------------------------------------------------------------------
    // ValidateAsync — one call, all issues, across targets (the field-report shape:
    // "compiles for DirectX, fails for OpenGL").
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidateAsync_GlOnlyFailure_ReportShowsBothTargets()
    {
        // An int uniform: SM5 DXBC compiles it fine; the MonoGame-GL dialect rewrite
        // rejects it loudly (SD0210, MojoShader ivec4 register sets not modelled) —
        // the canonical "DX works, GL doesn't" reporter shape.
        const string fx = """
            int Mode;

            float4 MainPS() : COLOR0
            {
                return Mode > 0 ? float4(1, 1, 1, 1) : float4(0, 0, 0, 1);
            }

            technique T
            {
                pass P0
                {
                    PixelShader = compile ps_3_0 MainPS();
                }
            }
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        IShaderCompiler compiler = new EffectCompiler();

        ShaderValidationReport report = await compiler.ValidateAsync(fx, cancellationToken: cts.Token);

        report.IsValid.Should().BeFalse();
        report.Targets.Should().HaveCount(2, "the default validation set is OpenGL + DirectX");

        var gl = report.Targets.Single(t => t.Target == PlatformTarget.OpenGL);
        var dx = report.Targets.Single(t => t.Target == PlatformTarget.DirectX);

        gl.Succeeded.Should().BeFalse("int uniforms are unmodelled in the MonoGame-GL dialect");
        gl.Errors.Should().Contain(e => e.Code == "SD0210");
        dx.Succeeded.Should().BeTrue($"errors: {string.Join(" | ", dx.Errors.Select(e => e.Message))}");

        string rendered = report.ToString();
        rendered.Should().Contain("[OpenGL] FAILED");
        rendered.Should().Contain("[DirectX] OK");
        rendered.Should().Contain("integer/boolean uniforms",
            "the report carries the actionable rewriter message verbatim");
    }

    [Fact]
    public async Task ValidateAsync_WarningsSurfaceInTheReport()
    {
        // The SD0401 SpriteBatch-interpolant shape: valid on both targets, but the
        // GL result must carry the warning — one Validate call shows the issue that
        // otherwise appears only as a draw-time link failure in the field.
        string fx = await File.ReadAllTextAsync(
            TestHelpers.FixturePath("examples/Sd0401SpriteBatchInterpolant.fx"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        IShaderCompiler compiler = new EffectCompiler();

        ShaderValidationReport report = await compiler.ValidateAsync(fx, cancellationToken: cts.Token);

        report.IsValid.Should().BeTrue("the shader compiles everywhere — the lint never rejects");
        report.IsClean.Should().BeFalse("the GL target must carry the SD0401 warning");

        var gl = report.Targets.Single(t => t.Target == PlatformTarget.OpenGL);
        gl.Warnings.Should().Contain(w => w.Code == "SD0401");

        report.ToString().Should().Contain("SD0401").And.Contain("SpriteBatch");
    }
}
