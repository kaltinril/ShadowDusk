#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// End-to-end Phase 61 (issue #198): a real <c>.slang</c> through the frontend and on through
/// the unchanged pipeline to real <c>.mgfx</c> bytes, for a rung-4-proven GL target and a
/// rung-4-proven DX target. Ungated: the frontend is a pure managed text transform and the body
/// compiles through the same DXC every fixture uses, so this runs wherever the suite runs — no
/// Slang toolchain exists anywhere in the product or its tests (owner direction 2026-08-13:
/// HLSL-compatible Slang, nothing extra to ship on any platform).
///
/// What this proves is §5.2's reduced claim exactly: the product of the route is an ordinary
/// effect for already-proven targets. It is never called `mgfxc`-equivalent — `mgfxc` cannot
/// read Slang at all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangRouteTests
{
    private static readonly string FixturePath = FindFixture();

    [Fact]
    public async Task SlangFixture_CompilesToMgfx_OnOpenGlAndDirectX_WithTheUsersNames()
    {
        string slang = await File.ReadAllTextAsync(FixturePath);

        var converted = SlangFrontend.ConvertToFx(slang, new SlangConvertOptions
        {
            SourceName    = "Desaturate.slang",
            TechniqueName = "Desaturate",
        });
        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        string fx = converted.Value.FxText;

        // The body is the user's text verbatim: their names ARE the parameter names.
        fx.ShouldContain("WorldViewProjection", Case.Sensitive);
        fx.ShouldContain("Desaturation", Case.Sensitive);
        fx.ShouldContain("SpriteTexture", Case.Sensitive);
        fx.ShouldContain("technique Desaturate", Case.Sensitive);
        fx.ShouldNotContain("[shader(", Case.Sensitive);

        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX })
        {
            var compiled = await compiler.CompileAsync(fx, new CompilerOptions
            {
                Target = target,
                SourceFileName = "Desaturate.generated.fx",
            });

            compiled.IsSuccess.ShouldBeTrue(target + ": " +
                (compiled.IsFailure ? string.Join(" | ", compiled.Error.Select(e => $"{e.Code}: {e.Message}")) : ""));
            compiled.Value.Data.Length.ShouldBeGreaterThan(0);
        }
    }

    private static string FindFixture()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(
                dir.FullName, "tests", "fixtures", "shaders", "slang", "Desaturate.slang");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("tests/fixtures/shaders/slang/Desaturate.slang not found");
    }
}
