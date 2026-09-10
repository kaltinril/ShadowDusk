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
    /// The MGCB content-processor delivery shape (Phase 29). An MGCB plugin is BY DEFINITION
    /// compiled against <c>MonoGame.Framework.Content.Pipeline</c> — the
    /// <c>ContentImporter</c>/<c>ContentProcessor</c> contract is the whole plugin seam, and there
    /// is no way to implement one without it. Its reference is <c>compile</c>-only and
    /// <c>PrivateAssets="all"</c> (pinned by <see cref="MgcbPluginTakesNoRuntimeMonoGameAssets"/>),
    /// so no MonoGame assembly ships beside it and no MonoGame dependency edge reaches a consumer.
    /// </summary>
    private const string MgcbPluginProject = "ShadowDusk.MgcbPlugin.csproj";

    /// <summary>
    /// The Content Builder delivery shape (Phase 63, issue #203): the SAME importer/processor
    /// source files as a normal library, for MonoGame 3.8.5's code-centric Content Builder
    /// project, which is C# the consumer owns and needs <c>new ShadowDuskEffectImporter()</c> at
    /// compile time. Its reference is deliberately the OPPOSITE of the plugin's — a plain, real
    /// dependency (pinned by <see cref="ContentPipelinePackageDeclaresMonoGameDependency"/>),
    /// because a Builder project needs the dependency edge and the runtime assets.
    /// </summary>
    private const string ContentPipelineProject = "ShadowDusk.ContentPipeline.csproj";

    /// <summary>
    /// The two build-time delivery-shape projects, exempt BY NAME. The guard's real concern was
    /// MonoGame reaching a <i>game</i> through a ShadowDusk <i>compiler</i> package; both of
    /// these are build-time tooling (like MGCB itself), and the shipped compiler libraries
    /// (<c>Core</c>, <c>HLSL</c>, <c>GLSL</c>, <c>Compiler</c>, <c>ShaderToy</c>, <c>Wasm</c>,
    /// <c>Cli</c>, <c>Metal</c>) stay MonoGame-free — these two reference THEM, never the other
    /// way round.
    /// </summary>
    private static readonly IReadOnlySet<string> ExemptProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MgcbPluginProject,
        ContentPipelineProject,
    };

    [Fact]
    public void NoSrcProjectReferencesMonoGame()
    {
        string srcDir = Path.Combine(FindRepoRoot(), "src");
        Directory.Exists(srcDir).ShouldBeTrue($"the product source tree must exist at {srcDir}");

        var offenders = new List<string>();
        foreach (string csproj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            if (ExemptProjects.Contains(Path.GetFileName(csproj)))
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
        Match reference = MonoGamePipelineReference("ShadowDusk.MgcbPlugin", MgcbPluginProject);

        reference.Value.ShouldContain(@"IncludeAssets=""compile""", Case.Sensitive,
            "the MonoGame reference must be compile-only, or MonoGame's content-pipeline assembly " +
            "and its natives ship beside the plugin");
        reference.Value.ShouldContain(@"PrivateAssets=""all""", Case.Sensitive,
            "the MonoGame reference must be private, or the published ShadowDusk.MgcbPlugin package " +
            "gains a MonoGame dependency edge");
    }

    /// <summary>
    /// The library shape pins the OPPOSITE: a plain reference with <b>neither</b>
    /// <c>PrivateAssets="all"</c> <b>nor</b> <c>IncludeAssets="compile"</c>. A Content Builder
    /// consumer needs the <c>MonoGame.Framework.Content.Pipeline</c> dependency edge (so NuGet
    /// unifies the version with the Builder's own 3.8.5+ reference) and the runtime assets; a
    /// private or compile-only edge would hand it a package whose types it cannot resolve.
    /// </summary>
    [Fact]
    public void ContentPipelinePackageDeclaresMonoGameDependency()
    {
        Match reference = MonoGamePipelineReference("ShadowDusk.ContentPipeline", ContentPipelineProject);

        reference.Value.ShouldNotContain("PrivateAssets", Case.Sensitive,
            "ShadowDusk.ContentPipeline's MonoGame reference must be a real dependency edge - a Content " +
            "Builder project needs it to flow");
        reference.Value.ShouldNotContain("IncludeAssets", Case.Sensitive,
            "ShadowDusk.ContentPipeline's MonoGame reference must carry every asset group, not compile only");
        reference.Value.ShouldNotContain("ExcludeAssets", Case.Sensitive,
            "ShadowDusk.ContentPipeline's MonoGame reference must carry every asset group");

        string text = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "ShadowDusk.ContentPipeline", ContentPipelineProject));
        text.ShouldNotContain("<IncludeBuildOutput>", Case.Sensitive,
            "the library package must ship lib/net8.0 - that is the whole reason it exists beside the tools-only plugin");
        text.ShouldNotContain("<SuppressDependenciesWhenPacking>", Case.Sensitive,
            "the library package must declare its dependencies so ShadowDusk.Compiler and the natives flow transitively");
    }

    /// <summary>
    /// "One copy of the logic": the library compiles EXACTLY the plugin's <c>.cs</c> file set
    /// (source-linked), so the importer/processor a Content Builder gets is the one the MGCB
    /// gate and the byte-identity suite prove. A file added to the plugin and forgotten here, or
    /// a library-only file, would silently split the two delivery shapes.
    /// </summary>
    [Fact]
    public void ContentPipelineCompilesExactlyThePluginSourceFiles()
    {
        string repoRoot = FindRepoRoot();
        string pluginDir = Path.Combine(repoRoot, "src", "ShadowDusk.MgcbPlugin");
        string libraryCsproj = Path.Combine(repoRoot, "src", "ShadowDusk.ContentPipeline", ContentPipelineProject);
        string libraryDir = Path.GetDirectoryName(libraryCsproj)!;

        // The plugin's own sources: every .cs in its directory (it compiles them by SDK glob).
        var pluginFiles = Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        pluginFiles.ShouldNotBeEmpty("the plugin must have source files to share");

        // The library's explicit <Compile Include> links that point back into the plugin directory.
        var linkedPluginFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherLinks = new List<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(libraryCsproj), @"<Compile\s+Include=""([^""]+)"""))
        {
            string include = m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(libraryDir, include));
            if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(pluginDir), StringComparison.OrdinalIgnoreCase))
                linkedPluginFiles.Add(Path.GetFileName(full));
            else
                otherLinks.Add(include);
        }

        linkedPluginFiles.ShouldBe(pluginFiles, ignoreOrder: true, customMessage:
            "ShadowDusk.ContentPipeline must source-link EXACTLY the plugin's .cs files - " +
            "one copy of the importer/processor logic across both delivery shapes");

        // The only non-plugin link is the CLI's formatter, which the plugin links too.
        otherLinks.ShouldBe(new[] { Path.Combine("..", "ShadowDusk.Cli", "MgcbErrorFormatter.cs") }, Case.Sensitive,
            "the library links the CLI's MgcbErrorFormatter (as the plugin does) and nothing else");

        // And the library must have no sources of its own beside the links.
        Directory.EnumerateFiles(libraryDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ShouldBeEmpty("ShadowDusk.ContentPipeline must have no source files of its own; every type is the plugin's");
    }

    private static Match MonoGamePipelineReference(string projectDirectory, string projectFile)
    {
        string csproj = Path.Combine(FindRepoRoot(), "src", projectDirectory, projectFile);
        File.Exists(csproj).ShouldBeTrue($"the project must exist at {csproj}");

        string text = File.ReadAllText(csproj);

        // The single PackageReference element, attributes and all (it may span lines).
        Match reference = Regex.Match(
            text,
            @"<PackageReference\s+Include=""MonoGame\.Framework\.Content\.Pipeline""[\s\S]*?/>",
            RegexOptions.IgnoreCase);

        reference.Success.ShouldBeTrue(
            $"{projectFile} must reference MonoGame.Framework.Content.Pipeline - the " +
            "ContentImporter/ContentProcessor contract IS the seam");

        return reference;
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
