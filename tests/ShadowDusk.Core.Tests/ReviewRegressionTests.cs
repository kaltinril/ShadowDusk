#nullable enable

using System;
using Shouldly;
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

        copy.Target.ShouldBe(PlatformTarget.OpenGL);
        copy.Defines.ShouldBe(options.Defines, ignoreOrder: true);
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

        // Shouldly has no "compare every member except this one", so the guard is kept
        // reflective on purpose: it enumerates CompilerOptions' public properties at
        // runtime, so a property added later is compared automatically (the whole point
        // of this test) rather than needing to be listed here.
        foreach (System.Reflection.PropertyInfo property in
                 typeof(CompilerOptions).GetProperties().Where(p => p.Name != nameof(CompilerOptions.Target)))
        {
            object? expected = property.GetValue(options);
            object? actual = property.GetValue(copy);

            if (expected is System.Collections.IEnumerable expectedItems and not string)
                ((IEnumerable<object?>)actual!.ShouldBeAssignableTo<System.Collections.IEnumerable>()!.Cast<object?>())
                    .ShouldBe(expectedItems.Cast<object?>(), customMessage: property.Name);
            else
                actual.ShouldBe(expected, customMessage: property.Name);
        }
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

        Should.Throw<ArgumentOutOfRangeException>(act).Message.ShouldMatch("(?s)Profile.*null.*Target");
    }

    [Fact]
    public void Recommend_StillMapsTheModelledTargets()
    {
        RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.OpenGL)
            .GraphicsTarget.ShouldBe(PlatformTarget.OpenGL);
        RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.DirectX)
            .GraphicsTarget.ShouldBe(PlatformTarget.DirectX);
        // FNA short-circuits ahead of the switch and is deliberately target-independent.
        RuntimeProfileDetector.Recommend(DetectedRuntime.Fna, PlatformTarget.Vulkan)
            .GraphicsTarget.ShouldBe(PlatformTarget.Fna);
    }

    // ── SD0005: undetectable input format ───────────────────────────────────────────

    [Fact]
    public void InputFormatDetector_NoTechniqueAndNoMainImage_FailsWithSd0005()
    {
        // SD0005 exists because this condition used to be mis-filed under SD0002
        // ("circular #include"). Nothing asserted that it actually fires.
        var result = InputFormatDetector.Detect(
            "Mystery.txt", "float x = 1.0;\n", InputFormat.Auto);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0005");
        result.Error.Message.ShouldContain("--input-format", Case.Sensitive);
    }

    [Fact]
    public void InputFormatDetector_StillDetectsTheTwoRealFormats()
    {
        // The over-fire direction: SD0005 must not start rejecting valid input.
        InputFormatDetector.Detect("a.fx", "technique T { pass P { } }", InputFormat.Auto)
            .IsSuccess.ShouldBeTrue();
        InputFormatDetector.Detect(
                "a.glsl", "void mainImage(out vec4 c, in vec2 p) { c = vec4(1); }", InputFormat.Auto)
            .IsSuccess.ShouldBeTrue();
    }
}
