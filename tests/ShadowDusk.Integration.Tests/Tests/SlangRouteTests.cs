#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using ShadowDusk.Tests.Shared;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Skip gate for the Slang-route tests: the frontend invokes the pinned <c>slangc</c>, a
/// provisioned tool (<c>tools/setup-local-testing.ps1 -WithSlang</c>), not a package-shipped
/// native yet. Absent slangc → skip with the provisioning command — unless
/// <c>SHADOWDUSK_REQUIRE_SLANGC</c> is set, in which case the test runs and fails loudly at the
/// SD0600 boundary instead of skipping green (the <see cref="NativeRequirement"/> pattern).
/// </summary>
internal sealed class SlangFactAttribute : FactAttribute
{
    internal const string EnvVar = "SHADOWDUSK_REQUIRE_SLANGC";

    public SlangFactAttribute()
    {
        bool available = SlangAvailable();
        bool required = NativeRequirement.IsRequired(Environment.GetEnvironmentVariable(EnvVar));
        if (!available && !required)
        {
            Skip = "slangc not found (provision it via `pwsh tools/setup-local-testing.ps1 -WithSlang`, "
                 + "or set SHADOWDUSK_SLANGC).";
        }
    }

    private static bool SlangAvailable()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("SHADOWDUSK_SLANGC");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return true;

        string exe = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return File.Exists(Path.Combine(dir.FullName, "tools", "slang", "bin", exe));
        }
        return false;
    }
}

/// <summary>
/// End-to-end Phase 61 (issue #198): a real <c>.slang</c> through the real frontend
/// (<c>slangc</c> included) and on through the unchanged pipeline to a real <c>.mgfx</c>, for
/// both a rung-4-proven GL target and a rung-4-proven DX target. What this proves is §5.2's
/// reduced claim exactly: the product of the route is an ordinary effect for an
/// already-proven target — the only new link is Slang's own HLSL emission.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangRouteTests
{
    private static readonly string FixturePath = FindFixture();

    [SlangFact]
    public async Task SlangFixture_ConvertsToFx_WithUserNamesAndSynthesizedTechnique()
    {
        string slang = await File.ReadAllTextAsync(FixturePath);

        var converted = await SlangFrontend.ConvertToFxAsync(slang, new SlangConvertOptions
        {
            SourceFilePath = FixturePath,
            SourceName     = "Desaturate.slang",
            TechniqueName  = "Desaturate",
        });

        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        string fx = converted.Value.FxText;

        // The names the user wrote survive to the .fx — and therefore to the effect
        // parameters — with slangc's mangling and parameter-group wrapping gone.
        fx.ShouldContain("WorldViewProjection", Case.Sensitive);
        fx.ShouldContain("Desaturation", Case.Sensitive);
        fx.ShouldContain("SpriteTexture", Case.Sensitive);
        fx.ShouldNotContain("SLANG_ParameterGroup", Case.Sensitive);
        // The mangled forms specifically — a blanket "_0" would trip on vs_4_0_level_9_1.
        fx.ShouldNotContain("Desaturation_0", Case.Sensitive);
        fx.ShouldNotContain("WorldViewProjection_0", Case.Sensitive);
        fx.ShouldNotContain("SpriteTexture_0", Case.Sensitive);
        fx.ShouldNotContain("Params_0", Case.Sensitive);

        // The synthesized technique holds exactly the attribute-marked entry points.
        fx.ShouldContain("technique Desaturate", Case.Sensitive);
        fx.ShouldContain("VertexShader = compile VS_SHADERMODEL MainVS();", Case.Sensitive);
        fx.ShouldContain("PixelShader = compile PS_SHADERMODEL MainPS();", Case.Sensitive);
    }

    [SlangFact]
    public async Task SlangRoute_CompilesToMgfx_OnOpenGlAndDirectX()
    {
        string slang = await File.ReadAllTextAsync(FixturePath);
        var converted = await SlangFrontend.ConvertToFxAsync(slang, new SlangConvertOptions
        {
            SourceFilePath = FixturePath,
            SourceName     = "Desaturate.slang",
        });
        converted.IsSuccess.ShouldBeTrue(
            converted.IsFailure ? string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX })
        {
            var compiled = await compiler.CompileAsync(converted.Value.FxText, new CompilerOptions
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
