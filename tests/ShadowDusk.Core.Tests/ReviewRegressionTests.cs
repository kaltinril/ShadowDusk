#nullable enable

using System;
using FluentAssertions;
using ShadowDusk.Cli;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Regression guards for defects found in the 2026-07-27 full-project review. Each test
/// names the silent-wrong-output or drop-in-parity break it pins.
/// </summary>
public sealed class ReviewRegressionTests
{
    // ── CompilerOptions.WithGraphicsTarget must copy EVERY property ──────────────────

    [Fact]
    public void WithGraphicsTarget_PreservesDefines()
    {
        // The pipeline normalizes a set Profile's GraphicsTarget over Target through this
        // method, and ValidateAsync calls it once per target. Dropping Defines meant
        // `--target-runtime monogame-gl /Defines:HIGH_QUALITY=1` compiled with the macro
        // UNDEFINED — the #ifdef branch vanished and the wrong artifact shipped, exit 0.
        var options = new CompilerOptions
        {
            Target  = PlatformTarget.DirectX,
            Defines = [new UserDefine("HIGH_QUALITY", "1"), new UserDefine("DEBUG_VIS")],
        };

        var copy = options.WithGraphicsTarget(PlatformTarget.OpenGL);

        copy.Target.Should().Be(PlatformTarget.OpenGL);
        copy.Defines.Should().BeEquivalentTo(options.Defines);
    }

    [Fact]
    public void WithGraphicsTarget_PreservesEveryOtherSetting()
    {
        // Broad guard so the NEXT property added to CompilerOptions cannot be silently
        // dropped the way Defines was.
        var options = new CompilerOptions
        {
            Target                 = PlatformTarget.OpenGL,
            Profile                = CapabilityProfile.MonoGameDX_SM5,
            AdditionalIncludePaths = ["/inc/a", "/inc/b"],
            SourceFileName         = "Thing.fx",
            Debug                  = true,
            MgfxVersion            = 11,
            Container              = EffectContainer.Knifx,
            DxbcBackend            = DxbcBackend.D3DCompiler,
            Defines                = [new UserDefine("X")],
        };

        var copy = options.WithGraphicsTarget(PlatformTarget.DirectX);

        copy.Should().BeEquivalentTo(options, o => o.Excluding(x => x.Target));
    }

    // ── RuntimeProfileDetector must never substitute a different backend ─────────────

    [Theory]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Metal)]
    public void Recommend_UnmodelledTarget_RefusesInsteadOfDowngradingToOpenGL(PlatformTarget target)
    {
        // These used to fall through to the OpenGL profile. Because a set Profile's
        // GraphicsTarget overrides Target, a DesktopVK or WindowsDX12 consumer following
        // the documented flow got a MojoShader-GLSL .mgfx their runtime cannot load —
        // compiled with exit 0 and no diagnostic — and Metal bypassed the pipeline's own
        // loud SD0200 rejection.
        Action act = () => RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, target);

        act.Should().Throw<ArgumentOutOfRangeException>()
           .WithMessage("*Profile*null*Target*");
    }

    [Fact]
    public void Recommend_StillMapsTheModelledTargets()
    {
        RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.OpenGL)
            .GraphicsTarget.Should().Be(PlatformTarget.OpenGL);
        RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.DirectX)
            .GraphicsTarget.Should().Be(PlatformTarget.DirectX);
        // FNA short-circuits ahead of the switch and is deliberately target-independent.
        RuntimeProfileDetector.Recommend(DetectedRuntime.Fna, PlatformTarget.Vulkan)
            .GraphicsTarget.Should().Be(PlatformTarget.Fna);
    }

    // ── SD0005: undetectable input format ───────────────────────────────────────────

    [Fact]
    public void InputFormatDetector_NoTechniqueAndNoMainImage_FailsWithSd0005()
    {
        // SD0005 exists because this condition used to be mis-filed under SD0002
        // ("circular #include"). Nothing asserted that it actually fires.
        var result = InputFormatDetector.Detect(
            "Mystery.txt", "float x = 1.0;\n", InputFormat.Auto);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0005");
        result.Error.Message.Should().Contain("--input-format");
    }

    [Fact]
    public void InputFormatDetector_StillDetectsTheTwoRealFormats()
    {
        // The over-fire direction: SD0005 must not start rejecting valid input.
        InputFormatDetector.Detect("a.fx", "technique T { pass P { } }", InputFormat.Auto)
            .IsSuccess.Should().BeTrue();
        InputFormatDetector.Detect(
                "a.glsl", "void mainImage(out vec4 c, in vec2 p) { c = vec4(1); }", InputFormat.Auto)
            .IsSuccess.Should().BeTrue();
    }
}
