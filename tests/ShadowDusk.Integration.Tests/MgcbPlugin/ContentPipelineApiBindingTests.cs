#nullable enable

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Shouldly;
using Xunit;
using ShadowDusk.ContentPipeline;

namespace ShadowDusk.Integration.Tests.MgcbPlugin;

/// <summary>
/// Phase 69: every MonoGame API the shipped content plugin calls must exist in every MonoGame
/// content pipeline it can be loaded into. The plugin is compiled once, against the
/// 3.8.2.1105 floor, and loaded by whatever <c>mgcb</c> or Content Builder the consumer runs, so
/// a member MonoGame later removes is a <see cref="MissingMethodException"/> in the consumer's
/// build, never a compile error here. MonoGame 3.8.6 did exactly that: it removed
/// <c>ContentIdentity(string, string)</c>, and every 3.8.6 content build through
/// <see cref="ShadowDuskEffectImporter"/> failed. The <c>ShadowDusk.ContentPipeline</c> package
/// compiles these same source files, so checking this assembly covers both packages.
/// <para>
/// Each release's pipeline assembly is loaded into its own collectible
/// <see cref="AssemblyLoadContext"/> together with a second copy of the plugin, and every member
/// reference in the plugin's metadata is resolved there. The test process itself has the
/// 3.8.2.1105 pipeline loaded, which is why the context must hand out its own copy: falling back
/// to the default context would quietly check every version against the floor.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContentPipelineApiBindingTests
{
    /// <summary>
    /// Every supported MonoGame content-pipeline release, oldest first. A new MonoGame release is
    /// appended here and to the PackageDownload in ShadowDusk.Integration.Tests.csproj.
    /// </summary>
    public static TheoryData<string> Versions => new() { "3.8.2.1105", "3.8.4.1", "3.8.5", "3.8.5.1", "3.8.6" };

    [Theory]
    [MemberData(nameof(Versions))]
    public void EveryMonoGameMemberThePluginCalls_ExistsInThatRelease(string version)
    {
        using var context = new PipelineContext(version);
        Assembly pipeline = context.Pipeline;
        pipeline.GetName().Version!.ToString().ShouldStartWith(
            version.Split('.').Length == 3 ? version + "." : version,
            customMessage: $"{context.Directory} holds the wrong pipeline release");

        Assembly plugin = context.LoadFromAssemblyPath(typeof(ShadowDuskEffectImporter).Assembly.Location);
        Module module = plugin.ManifestModule;

        var missing = new List<string>();
        int checkedMonoGame = 0;
        foreach (MemberReferenceHandle handle in MemberReferences(plugin.Location))
        {
            MemberInfo? member;
            try
            {
                member = module.ResolveMember(MetadataTokens.GetToken(handle));
            }
            catch (ArgumentException)
            {
                // A member of a generic type that needs a generic context to resolve. None of the
                // plugin's MonoGame calls is one (ContentImporter<EffectContent> is closed); the
                // count below would drop if that changed.
                continue;
            }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException or FileLoadException or FileNotFoundException)
            {
                missing.Add($"0x{MetadataTokens.GetToken(handle):X8}: {e.GetType().Name}: {e.Message}");
                continue;
            }

            Assembly? declaring = member?.DeclaringType?.Assembly;
            if (declaring?.GetName().Name?.StartsWith("MonoGame.Framework", StringComparison.Ordinal) == true)
            {
                if (declaring.GetName().Name == pipeline.GetName().Name)
                {
                    // Bound to THIS release's pipeline, not the floor the test process loaded.
                    declaring.ShouldBeSameAs(pipeline, $"{member} bound to a pipeline from another load context");
                }

                checkedMonoGame++;
            }
        }

        missing.ShouldBeEmpty(
            $"the plugin calls MonoGame members that MonoGame {version}'s content pipeline does not have; a consumer on that "
            + "release gets MissingMethodException from mgcb or the Content Builder");
        // 17 MonoGame members were measured on 2026-10-31 (importer, processor, attributes, context, logger).
        // A much lower count means members stopped being checked, not that the plugin got smaller.
        checkedMonoGame.ShouldBeGreaterThanOrEqualTo(10);
    }

    /// <summary>
    /// Positive control: the check really sees each release. 3.8.6 has no
    /// <c>ContentIdentity(string, string)</c> (the constructor the importer used to call) while the
    /// floor does, and both have the three-argument one the importer calls now.
    /// </summary>
    [Fact]
    public void ContentIdentityTwoArgumentConstructor_IsGoneIn386_AndTheThreeArgumentOneIsEverywhere()
    {
        foreach (string version in new[] { "3.8.2.1105", "3.8.6" })
        {
            using var context = new PipelineContext(version);
            Type identity = context.Pipeline.GetType("Microsoft.Xna.Framework.Content.Pipeline.ContentIdentity", throwOnError: true)!;

            bool hasTwo = identity.GetConstructor([typeof(string), typeof(string)]) is not null;
            hasTwo.ShouldBe(version != "3.8.6", $"ContentIdentity(string, string) on {version}");
            identity.GetConstructor([typeof(string), typeof(string), typeof(string)]).ShouldNotBeNull(
                $"ContentIdentity(string, string, string) is missing on {version}");
        }
    }

    private static List<MemberReferenceHandle> MemberReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        return [.. reader.GetMetadataReader().MemberReferences];
    }

    /// <summary>A collectible context that hands out one release's pipeline assembly.</summary>
    private sealed class PipelineContext : AssemblyLoadContext, IDisposable
    {
        public PipelineContext(string version)
            : base("content-pipeline-" + version, isCollectible: true)
        {
            Directory = Path.Combine(AppContext.BaseDirectory, "content-pipeline", version);
            string path = Path.Combine(Directory, "MonoGame.Framework.Content.Pipeline.dll");
            File.Exists(path).ShouldBeTrue(
                $"{path} is missing: the test project copies it from the NuGet global-packages folder "
                + "(MonoGame.Framework.Content.Pipeline PackageDownload). Restore and rebuild ShadowDusk.Integration.Tests.");
            Pipeline = LoadFromAssemblyPath(path);
        }

        public string Directory { get; }

        public Assembly Pipeline { get; }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            assemblyName.Name == Pipeline?.GetName().Name ? Pipeline : null;

        public void Dispose() => Unload();
    }
}
