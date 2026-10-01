#nullable enable

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #231, answered by measurement: how does MonoGame's own effect macro header
/// (<c>Macros.fxh</c>: <c>BEGIN_CONSTANTS</c>, <c>DECLARE_TEXTURE</c>, <c>TECHNIQUE</c>) fare
/// through the real-slangc route?
///
/// <para><b>Source pinned:</b> <c>MonoGame.Framework/Platform/Graphics/Effect/Resources/</c> at
/// MonoGame tag <c>v3.8.5</c>, commit <c>4f9e37276977996547de01510249279ddcc67eb7</c>
/// (BasicEffect, AlphaTestEffect, DualTextureEffect, EnvironmentMapEffect, SkinnedEffect,
/// SpriteEffect plus Macros.fxh, Common.fxh, Lighting.fxh, Structures.fxh), kept byte-identical
/// in <c>tests/fixtures/shaders/</c> and pinned by SHA-256 below.</para>
///
/// <para><b>Measured</b> (osx-arm64 slangc 2026.14.1, 66 distinct VS/PS entries across the six
/// effects, each macro branch): 0 compile as shipped, on any target. Two blockers, in order.
/// First the <c>technique</c> block that <c>TECHNIQUE(...)</c> expands to (<c>E20001</c>): Slang
/// has no techniques. Then, with techniques removed, the legacy <c>sampler</c> type in
/// <c>DECLARE_TEXTURE</c> on the SM4/SM6 branches and <c>sampler2D</c>/<c>tex2D</c> on the DX9
/// branch that OpenGL and FNA select (<c>E30015</c>). With both removed (and <c>sampler</c>
/// spelled <c>SamplerState</c>) all 66 entries compile on the SM4 and SM6 branches, so there is
/// no third blocker. These are HLSL Effect files, not Slang: they compile through the
/// <c>.fx</c> route, where they are already render-proven. The Slang route rejects them with
/// <c>SD0626</c>; the guard is covered in <c>SlangEffectFrameworkGuardTests</c> (pure) and here
/// against the real files.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangMonoGameEffectsTests
{
    private static readonly string ShadersDir = Path.Combine(FindRepoRoot(), "tests", "fixtures", "shaders");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }

    private static readonly Dictionary<string, string> PinnedSha256 = new()
    {
        ["AlphaTestEffect.fx"] = "d3293a3ef97216ff3513d02787234b3619f88ee6de399fee238db90688091dae",
        ["BasicEffect.fx"] = "8fc5136d09d2dc2eaf29a0c48681311d6efff66f6c37409388f91a4fce0f9d71",
        ["DualTextureEffect.fx"] = "5ea172905ad9676559c655daea7d271aac977a57610549045e63426e996186c0",
        ["EnvironmentMapEffect.fx"] = "d8256ae06ea8cefc2dd33ffe670d8e39c69323c9e6c78433101ed8f04969ab93",
        ["SkinnedEffect.fx"] = "f54ee01fac567743c1e443b62b2053d9528f1efe39605fdbcfae0a40010eb6b2",
        ["SpriteEffect.fx"] = "896207f7acdecc38119078aef77b63420acfd856037e72b039df4a1991aaa541",
        ["Common.fxh"] = "fde7c72047f7294cb859024071fc3eede01554c38b2fd674aad5441dd78b506d",
        ["Lighting.fxh"] = "3952f29411983301039241c14a2442f90f7b0f5f98027d3abd3a387d013ae268",
        ["Macros.fxh"] = "978a6d5fb1e8833fdba6dd4c854a046d591e905924fb17433aec9777ee393c4d",
        ["Structures.fxh"] = "37387a7dbdb06ce26ff15ff97f013433122bc907219b94726c511820944f44a9",
    };

    private static readonly string[] Effects =
    [
        "AlphaTestEffect.fx", "BasicEffect.fx", "DualTextureEffect.fx",
        "EnvironmentMapEffect.fx", "SkinnedEffect.fx", "SpriteEffect.fx",
    ];

    private static readonly PlatformTarget[] Targets =
    [
        PlatformTarget.DirectX, PlatformTarget.OpenGL, PlatformTarget.Vulkan,
        PlatformTarget.DirectX12, PlatformTarget.Fna,
    ];

    public static IEnumerable<object[]> EffectFiles() => Effects.Select(e => new object[] { e });

    public static IEnumerable<object[]> EffectTargets()
    {
        foreach (string e in Effects)
        {
            foreach (PlatformTarget t in Targets)
                yield return [e, t];
        }
    }

    // The three macro branches Macros.fxh has: DX9 (OpenGL and FNA define neither SM4 nor SM6),
    // SM4 (DirectX), SM6/VULKAN (Vulkan; DirectX12 selects the same branch through SM6).
    public static IEnumerable<object[]> EffectBranches()
    {
        foreach (string e in Effects)
        {
            foreach (PlatformTarget t in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.Vulkan })
                yield return [e, t];
        }
    }

    private static string Flatten(string effect, PlatformTarget target)
    {
        string path = Path.Combine(ShadersDir, effect);
        var result = new Preprocessor().Flatten(
            File.ReadAllText(path), path, PlatformMacros.For(target), new FileSystemIncludeResolver(), []);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "");
        return result.Value.Text;
    }

    // Independent of the guard: the first non-comment line spelling an Effect-framework word.
    private static int FirstEffectLine(string text)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (t.StartsWith("DECLARE_TEXTURE(", StringComparison.Ordinal) ||
                t.StartsWith("DECLARE_CUBEMAP(", StringComparison.Ordinal) ||
                t.StartsWith("BEGIN_CONSTANTS", StringComparison.Ordinal) ||
                t.StartsWith("TECHNIQUE(", StringComparison.Ordinal) ||
                t.StartsWith("technique", StringComparison.Ordinal))
                return i + 1;
        }
        return -1;
    }

    [Fact]
    public void Fixtures_AreByteIdenticalToMonoGameV385()
    {
        foreach ((string file, string expected) in PinnedSha256)
        {
            string text = File.ReadAllText(Path.Combine(ShadersDir, file));
            string actual = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            actual.ShouldBe(expected, file + " drifted from MonoGame v3.8.5 (commit 4f9e3727); this measurement is pinned to that source");
        }
    }

    [Theory]
    [MemberData(nameof(EffectTargets))]
    public async Task OfficialEffect_AsShipped_RejectedWithSD0626_AtTheFirstEffectConstruct(
        string effect, PlatformTarget target)
    {
        string source = await File.ReadAllTextAsync(Path.Combine(ShadersDir, effect));

        var result = await new SlangCompiler().CompileAsync(
            source, new CompilerOptions { Target = target, SourceFileName = effect });

        result.IsFailure.ShouldBeTrue(effect + " on " + target + " was accepted");
        ShaderError error = result.Error.Single();
        error.Code.ShouldBe("SD0626");
        error.File.ShouldBe(effect);
        error.Line.ShouldBe(FirstEffectLine(source));
        error.Column.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(EffectTargets))]
    public async Task OfficialEffect_IncludesFlattened_RejectedWithSD0626(string effect, PlatformTarget target)
    {
        string flattened = Flatten(effect, target);

        var result = await new SlangCompiler().CompileAsync(
            flattened, new CompilerOptions { Target = target, SourceFileName = effect });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0626");
    }

    // ---------------------------------------------------------------------------
    // The measurement behind SD0626, pinned: real slangc, bypassing the guard. If a future
    // slangc learns techniques or the legacy sampler type, these fail and the rejection's
    // premise has to be revisited, instead of the guard silently turning stale.
    // ---------------------------------------------------------------------------

    private static readonly Regex TechniqueInvocation = new(
        @"^TECHNIQUE\(\s*\w+\s*,\s*(\w+)\s*,\s*(\w+)", RegexOptions.Multiline | RegexOptions.Compiled);

    private static (string Entry, string Stage)[] Entries(string flattened)
    {
        var entries = new List<(string, string)>();
        foreach (Match m in TechniqueInvocation.Matches(flattened))
        {
            if (!entries.Contains((m.Groups[1].Value, "vertex")))
                entries.Add((m.Groups[1].Value, "vertex"));
            if (!entries.Contains((m.Groups[2].Value, "fragment")))
                entries.Add((m.Groups[2].Value, "fragment"));
        }
        return entries.ToArray();
    }

    private static string StripTechniques(string flattened) =>
        Regex.Replace(flattened, @"^TECHNIQUE\(.*$", "", RegexOptions.Multiline);

    private static (int Exit, string Stderr) RunRealSlangc(string source, string entry, string stage, PlatformTarget target)
    {
        string slangc = SlangNativeCache.EnsureRunnableSlangc(SlangToolPath.Resolve().ShouldNotBeNull());
        var (code, _, stderr) = SlangCompiler.RunSlangc(
            slangc, Path.GetDirectoryName(slangc)!, source, entry, stage, PlatformMacros.For(target).Macros, []);
        return (code, stderr);
    }

    [Theory]
    [MemberData(nameof(EffectBranches))]
    public void RealSlangc_RejectsTheTechniqueBlock_ThenTheLegacySamplerType(string effect, PlatformTarget branch)
    {
        string flattened = Flatten(effect, branch);
        (string Entry, string Stage)[] entries = Entries(flattened);
        entries.Length.ShouldBeGreaterThan(0);
        string noTechniques = StripTechniques(flattened);

        // Both stages of the first technique are enough: the blockers are declarations, not
        // anything entry-specific.
        foreach ((string entry, string stage) in entries.Take(2))
        {
            var asShipped = RunRealSlangc(flattened, entry, stage, branch);
            asShipped.Exit.ShouldNotBe(0, effect + "/" + entry + " compiled with its technique block");
            asShipped.Stderr.ShouldContain("E20001", Case.Sensitive);

            var noTech = RunRealSlangc(noTechniques, entry, stage, branch);
            noTech.Exit.ShouldNotBe(0, effect + "/" + entry + " compiled without techniques but with the legacy sampler");
            noTech.Stderr.ShouldContain("E30015", Case.Sensitive);
        }
    }

    [Theory]
    [MemberData(nameof(EffectFiles))]
    public void RealSlangc_CompilesEveryEntry_OnceTechniquesAreRemovedAndSamplerIsSamplerState(string effect)
    {
        // The control: nothing else in MonoGame's effects stops slangc. SM4 (DirectX) and
        // SM6/VULKAN (Vulkan) are the branches whose DECLARE_TEXTURE is a Texture2D plus the
        // legacy sampler; the DX9 branch needs tex2D/sampler2D, which has no such one-word fix.
        foreach (PlatformTarget branch in new[] { PlatformTarget.DirectX, PlatformTarget.Vulkan })
        {
            string flattened = Flatten(effect, branch);
            string patched = Regex.Replace(StripTechniques(flattened), @"^(\s*)sampler\b", "$1SamplerState", RegexOptions.Multiline);

            foreach ((string entry, string stage) in Entries(flattened))
            {
                var run = RunRealSlangc(patched, entry, stage, branch);
                run.Exit.ShouldBe(0, effect + "/" + entry + " on " + branch + ": " + run.Stderr);
            }
        }
    }
}
