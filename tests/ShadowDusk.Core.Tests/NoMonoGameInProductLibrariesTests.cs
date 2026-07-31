#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Standing guard (Phase 47): no shipped <c>ShadowDusk.*</c> product library under <c>src/</c> may take
/// a MonoGame dependency. MonoGame is a consumer's runtime, not ours — pulling it into a product package
/// would bloat every consumer's graph and pin a MonoGame version into the product, violating the
/// "do not bump / do not couple MonoGame" directive. The ShaderToy converter is pure-managed; its
/// MonoGame runtime helper + sample live under <c>samples/</c>, never <c>src/</c>. This test fails loudly
/// if a future edit quietly adds a <c>MonoGame.Framework.*</c> reference to any <c>src/*.csproj</c>.
/// </summary>
public sealed class NoMonoGameInProductLibrariesTests
{
    /// <summary>
    /// The one project allowed to name MonoGame, with the reason (Phase 29).
    /// <c>ShadowDusk.MgcbPlugin</c> is not a runtime library: it is the MGCB content-processor
    /// delivery shape, and an MGCB plugin is BY DEFINITION compiled against
    /// <c>MonoGame.Framework.Content.Pipeline</c> — the <c>ContentImporter</c>/<c>ContentProcessor</c>
    /// contract is the whole plugin seam, and there is no way to implement one without it.
    ///
    /// <para>The guard's actual concern still holds and is enforced separately by
    /// <see cref="MgcbPluginTakesNoRuntimeMonoGameAssets"/>: the reference is <c>compile</c>-only
    /// and <c>PrivateAssets="all"</c>, so no MonoGame assembly is copied to the plugin's output and
    /// no MonoGame dependency edge reaches a consumer. Nothing flows into the runtime libraries
    /// (<c>Core</c>, <c>HLSL</c>, <c>GLSL</c>, <c>Compiler</c>, <c>ShaderToy</c>, <c>Wasm</c>,
    /// <c>Cli</c>, <c>Metal</c>) — the plugin references THEM, never the other way round.</para>
    /// </summary>
    private const string MgcbPluginProject = "ShadowDusk.MgcbPlugin.csproj";

    [Fact]
    public void NoSrcProjectReferencesMonoGame()
    {
        string srcDir = Path.Combine(FindRepoRoot(), "src");
        Directory.Exists(srcDir).ShouldBeTrue($"the product source tree must exist at {srcDir}");

        var offenders = new List<string>();
        foreach (string csproj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(csproj).Equals(MgcbPluginProject, StringComparison.OrdinalIgnoreCase))
                continue;

            string text = File.ReadAllText(csproj);
            if (Regex.IsMatch(text, @"MonoGame\.Framework", RegexOptions.IgnoreCase))
                offenders.Add(Path.GetFileName(csproj));
        }

        offenders.ShouldBeEmpty(
            "no shipped ShadowDusk.* product library may depend on MonoGame; the runtime helper + " +
            "sample belong under samples/ (see the ShaderToy sample-migration item in plan/PHASE-51). Offending projects: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// The MGCB plugin's exemption is narrow, and this pins it: its MonoGame reference must stay
    /// <b>compile-only and private</b>. Drop either attribute and the plugin starts shipping
    /// MonoGame's content-pipeline assembly (plus its FreeImage/Assimp/mojoshader natives) beside
    /// itself — a second <c>CompiledEffectContent</c> type MGCB has no <c>ContentTypeWriter</c> for,
    /// and a MonoGame dependency edge on a published ShadowDusk package.
    /// </summary>
    [Fact]
    public void MgcbPluginTakesNoRuntimeMonoGameAssets()
    {
        string csproj = Path.Combine(
            FindRepoRoot(), "src", "ShadowDusk.MgcbPlugin", MgcbPluginProject);
        File.Exists(csproj).ShouldBeTrue($"the MGCB plugin project must exist at {csproj}");

        string text = File.ReadAllText(csproj);

        // The single PackageReference element, attributes and all (it spans lines).
        Match reference = Regex.Match(
            text,
            @"<PackageReference\s+Include=""MonoGame\.Framework\.Content\.Pipeline""[\s\S]*?/>",
            RegexOptions.IgnoreCase);

        reference.Success.ShouldBeTrue(
            "the MGCB plugin must reference MonoGame.Framework.Content.Pipeline - the " +
            "ContentImporter/ContentProcessor contract IS the plugin seam");

        reference.Value.ShouldContain(@"IncludeAssets=""compile""", Case.Sensitive,
            "the MonoGame reference must be compile-only, or MonoGame's content-pipeline assembly " +
            "and its natives ship beside the plugin");
        reference.Value.ShouldContain(@"PrivateAssets=""all""", Case.Sensitive,
            "the MonoGame reference must be private, or the published ShadowDusk.MgcbPlugin package " +
            "gains a MonoGame dependency edge");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir.Parent is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root (the directory containing ShadowDusk.slnx).");
    }
}
