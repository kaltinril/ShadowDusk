#nullable enable

using System.Reflection;
using ShadowDusk.Compiler;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Standing guard (Phase 63, issue #203): every assembly <c>ShadowDusk.Compiler</c> reaches
/// transitively must survive an unguarded <see cref="Assembly.GetTypes"/>.
///
/// <para><b>Why this exists.</b> MonoGame 3.8.5's Content Builder
/// (<c>ContentBuilderHelper.LoadAssemblies</c>) discovers importers and processors by taking
/// every assembly in the AppDomain, recursively <see cref="Assembly.Load(AssemblyName)"/>ing
/// each referenced assembly whose name does not start with <c>System.</c>, and calling
/// <c>GetTypes()</c> on all of them with <b>no</b> try/catch. One type anywhere in a consumer's
/// dependency graph that the CLR refuses to load (<c>ReflectionTypeLoadException</c>) takes
/// the consumer's whole content build down before it touches a shader. That is exactly what
/// <c>Vortice.Direct3D12</c>'s <c>VersionedDeviceRemovedExtendedData+Union</c> did, and why
/// that package was dropped from <c>ShadowDusk.HLSL</c>. MGCB's own scanner is guarded, so no
/// <c>.mgcb</c> build and no <c>dotnet test</c> could see it; this test reproduces the
/// Builder's exact walk so the failure can never come back silently.</para>
///
/// <para>Runs in the test host, whose probing path holds the same assemblies a consumer's
/// build output holds. It is not a pure unit test in the "no disk" sense (the loader reads
/// assemblies), but it spawns nothing and is deterministic, so it stays in the default lane
/// like <c>NoMonoGameInProductLibrariesTests</c>.</para>
/// </summary>
public sealed class DependencyGraphScanTests
{
    [Fact]
    public void EveryAssemblyReachableFromShadowDuskCompilerSurvivesAnUnguardedGetTypes()
    {
        Assembly root = typeof(EffectCompiler).Assembly;

        // The same walk ContentBuilderHelper.LoadAssemblies performs: breadth-first over
        // GetReferencedAssemblies(), skipping only names that start with "System.", load
        // failures swallowed (the Builder's empty catch), then GetTypes() on every assembly
        // that loaded - unguarded, which is the whole point.
        var visited = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
        {
            [root.GetName().Name!] = root,
        };
        var queue = new Queue<Assembly>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            Assembly current = queue.Dequeue();
            foreach (AssemblyName reference in current.GetReferencedAssemblies())
            {
                string name = reference.Name ?? string.Empty;
                if (name.StartsWith("System.", StringComparison.Ordinal) || visited.ContainsKey(name))
                    continue;

                Assembly loaded;
                try
                {
                    loaded = Assembly.Load(reference);
                }
                catch (Exception)
                {
                    // The Builder swallows load failures too; an assembly that does not load
                    // is never GetTypes()'d and so cannot crash the scan.
                    continue;
                }

                visited[name] = loaded;
                queue.Enqueue(loaded);
            }
        }

        // The walk must actually have reached the native-interop layer, or a refactor that
        // broke the traversal would turn this into a test that cannot fail.
        visited.Keys.ShouldContain("ShadowDusk.HLSL");
        visited.Keys.ShouldContain("Vortice.Dxc");
        visited.Keys.ShouldContain("SharpGen.Runtime");

        var failures = new List<string>();
        foreach ((string name, Assembly assembly) in visited)
        {
            try
            {
                _ = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                string detail = string.Join("; ",
                    (ex.LoaderExceptions ?? []).Where(e => e is not null).Select(e => e!.Message).Distinct());
                failures.Add($"{name}: {detail}");
            }
        }

        failures.ShouldBeEmpty(
            "a shipped dependency has a type the CLR cannot load; MonoGame 3.8.5's Content Builder " +
            "GetTypes()-scans every consumer dependency unguarded and would crash the consumer's " +
            "whole content build (Phase 63 / issue #203). Offending assemblies: " +
            string.Join(" | ", failures));

        // The specific package that caused issue #203 must stay out of the graph entirely -
        // a future bump that quietly reintroduces it (e.g. through another Vortice.* package)
        // is caught here by name, not just by the scan above.
        visited.Keys.ShouldNotContain("Vortice.Direct3D12",
            "Vortice.Direct3D12 was dropped from ShadowDusk.HLSL in Phase 63 because the CLR cannot " +
            "load VersionedDeviceRemovedExtendedData+Union; the ID3D12ShaderReflection interop is ShadowDusk-owned");
    }
}
