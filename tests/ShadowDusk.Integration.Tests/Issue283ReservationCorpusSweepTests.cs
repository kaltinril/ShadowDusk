#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #283 corpus sweep. The OpenGL sampler-register reservation is now read from a managed
/// preprocessed view of every effect, so that view must be buildable for every fixture in the
/// repository (an <c>SD0009</c> here would be a new compile failure for a shader that compiled
/// before), and it must agree with the old raw-token reading on every fixture that does not
/// contain one of the shapes the fix exists for. The allow-list names those shapes; anything else
/// that moved would be an unintended output change.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue283ReservationCorpusSweepTests(ITestOutputHelper output)
{
    /// <summary>
    /// Fixtures (by path relative to the fixture root) whose reservation set is EXPECTED to change,
    /// because they contain the issue's shape. The two <c>SamplerReservation*</c> fixtures are the issue's own goldens. The other
    /// three carry <c>SamplerState X : register(sN)</c> only in their <c>#if SM6</c> arm, which
    /// OpenGL does not compile, so the raw reading reserved registers the preprocessed one does
    /// not. Their OpenGL <c>.mgfx</c> bytes were measured IDENTICAL before and after the fix
    /// (2026-10-02): their OpenGL arm declares the same sampler name through the legacy syntax, and
    /// the pre-parser's explicit-slot map pins the pair onto that register in pass 1 of
    /// <c>ResolveSlots</c>, where reservations do not apply.
    /// </summary>
    private static readonly HashSet<string> ExpectedToMove = new(StringComparer.Ordinal)
    {
        "SamplerReservationIfBranch.fx",
        "SamplerReservationMacro.fx",
        "VsTransformColorTexture.fx",
        "VsWaveQuadIntrinsics.fx",
        "third-party/Apos.Shapes/apos-shapes-sm6.fx",

        // Issue #368: the vendored XnaFiddle examples write `register(tN)` / `register(sN)` only in
        // their `#if SM6` arm and use the legacy syntax in the arm OpenGL compiles (the
        // apos-shapes-sm6 shape): raw [0] (or [0,1]), preprocessed [].
        "third-party/XnaFiddle/XnaFiddle-Fading.fx",
        "third-party/XnaFiddle/XnaFiddle-Grayscale.fx",
        "third-party/XnaFiddle/XnaFiddle-Invert.fx",
        "third-party/XnaFiddle/XnaFiddle-Mask.fx",
        "third-party/XnaFiddle/XnaFiddle-Pixelated.fx",
        "third-party/XnaFiddle/XnaFiddle-Tint.fx",

        // Issue #309 (2026-10-02): EVERY sampler type keyword with an explicit register reserves,
        // so a LEGACY sampler's register now moves the reservation set too wherever the raw text
        // and the preprocessed text disagree about it: a register only in a dead branch
        // (SamplerLegacyRegisterIfBranch: raw [0,1], preprocessed []), one spelled through a macro
        // (SamplerLegacyRegisterMacro, SamplerLegacyMacroDecl: raw [], preprocessed [2,3]), one in
        // an #include'd file (SamplerLegacyInclude), and MonoGame's DECLARE_TEXTURE macro, whose
        // body the raw lexer never sees (the stock effects and the vendored MonoGame test effects:
        // raw [], preprocessed [0] or [0,1] or [0,1,2]). The compile reads the preprocessed set
        // only, and the OpenGL corpus sweep measured no byte moving on any fixture that compiled
        // before (the stock effects still fail with SD0010; the vendored MonoGame effects started
        // compiling through the #308 recovery, on mgfxc's units).
        "SamplerLegacyRegisterIfBranch.fx",
        "SamplerLegacyRegisterMacro.fx",
        "SamplerLegacyInclude.fx",
        "SamplerLegacyMacroDecl.fx",
        "AlphaTestEffect.fx",
        "BasicEffect.fx",
        "DualTextureEffect.fx",
        "EnvironmentMapEffect.fx",
        "SkinnedEffect.fx",
        "SpriteEffect.fx",
        "third-party/MonoGame/Bevels.fx",
        "third-party/MonoGame/BlackOut.fx",
        "third-party/MonoGame/ColorFlip.fx",
        "third-party/MonoGame/CustomSpriteBatchEffect.fx",
        "third-party/MonoGame/Grayscale.fx",
        "third-party/MonoGame/HighContrast.fx",
        "third-party/MonoGame/Invert.fx",
        "third-party/MonoGame/NoEffect.fx",
        "third-party/MonoGame/ParameterTypes.fx",
        "third-party/MonoGame/RainbowH.fx",
    };

    /// <summary>
    /// Fixtures that are deliberately malformed for the preprocessor. DXC rejects them too
    /// (measured: <c>PreprocessorTest.fx</c>'s <c>#if foo(TEST)</c> is DXC's "token is not a valid
    /// binary operator in a preprocessor subexpression"), and the pipeline defers SD0009 until DXC
    /// has accepted the source, so the user still sees DXC's own error for these.
    /// </summary>
    private static readonly HashSet<string> MalformedForEveryPreprocessor = new(StringComparer.Ordinal)
    {
        "third-party/MonoGame/PreprocessorTest.fx",
    };

    [Fact]
    [Trait("Platform", "OpenGL")]
    public void EveryFixture_BuildsAPreprocessedView_AndOnlyTheIssueShapesMove()
    {
        string root = TestHelpers.FixturePath("");
        string[] files = Directory.GetFiles(root, "*.fx", SearchOption.AllDirectories);
        files.Length.ShouldBeGreaterThan(100, "the fixture corpus was not copied next to the test assembly");

        MacroSet macros = PlatformMacros.For(PlatformTarget.OpenGL);
        var failures = new List<string>();
        var moved = new List<string>();
        int swept = 0;

        foreach (string path in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            string raw = File.ReadAllText(path);

            // A fixture whose includes do not resolve, or that the pre-parser rejects, never
            // reaches the reservation step in a compile either.
            var flattened = new ShadowDusk.Core.Preprocessor.Preprocessor().Flatten(raw, path, macros, new FileSystemIncludeResolver(), []);
            var parsed = FxPreParser.Parse(raw, path);
            if (flattened.IsFailure || parsed.IsFailure)
                continue;
            swept++;

            var view = FxPreParser.CollectReservedGlSamplerSlots(flattened.Value.Text, path);
            if (view.IsFailure != MalformedForEveryPreprocessor.Contains(name))
            {
                failures.Add(view.IsFailure
                    ? $"{name}: {view.Error.Code} line {view.Error.Line}: {view.Error.Message}"
                    : $"{name}: expected SD0009 (the fixture is malformed) but the view was built");
                continue;
            }
            if (view.IsFailure)
            {
                view.Error.Code.ShouldBe("SD0009");
                continue;
            }

            int[] before = parsed.Value.ReservedGlSamplerSlots.OrderBy(x => x).ToArray();
            int[] after = view.Value.OrderBy(x => x).ToArray();
            if (!before.SequenceEqual(after))
            {
                string line = $"{name}: raw [{string.Join(",", before)}] -> preprocessed [{string.Join(",", after)}]";
                output.WriteLine(line);
                if (!ExpectedToMove.Contains(name))
                    moved.Add(line);
            }
        }

        output.WriteLine($"swept {swept} of {files.Length} fixtures");
        failures.ShouldBeEmpty();
        moved.ShouldBeEmpty();
    }
}
