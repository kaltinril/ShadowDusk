#nullable enable

using System.Diagnostics;
using Shouldly;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.Integration.Tests.Dxc;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Cli;

/// <summary>
/// Which DXC a compile uses must not depend on what ELSE sits in the application's directories
/// (issue #270). ShadowDusk loads only its own pinned DXC build, wherever it finds it; a
/// different build, in whichever directory, is refused with <c>SD0219</c>, never compiled with.
///
/// <para><b>What this pins.</b> Through PR #268 the loader took the first directory holding
/// files with the right NAMES, and probed the host application's <c>runtimes/&lt;rid&gt;/native</c>
/// before the directories beside its own assemblies. Measured: the Windows SDK's
/// <c>dxil.dll</c> + <c>dxcompiler.dll</c> (1.8) placed there compiled DirectX 12 silently with
/// the foreign DXC (4307 bytes against 4295) and failed OpenGL with that DXC's "SPIR-V CodeGen
/// not available"; with ShadowDusk's pair absent, a foreign pair in the application directory
/// was accepted the same way. The missing-natives path (<c>SD0219</c>) had no automated test
/// at all.</para>
///
/// <para>Each case runs the real CLI from a <b>private copy</b> of its build output whose DXC
/// natives have been removed, replaced or shadowed, in a fresh process (module state is
/// per-process). They run on every OS; <see cref="ForeignDxc"/> builds the foreign DXC from
/// files every host has, and the real Windows SDK pair is used as well where installed.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(CliDxcNativeLayoutCollection.Name)]
public sealed class CliDxcNativeLayoutTests : IClassFixture<CliBinaryFixture>
{
    /// <summary>The targets whose pipeline starts at DXC.</summary>
    private static readonly string[] DxcBackedProfiles = ["OpenGL", "Vulkan", "DirectX_12"];

    private static readonly string FixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "shaders", "Grayscale.fx");

    private readonly CliBinaryFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CliDxcNativeLayoutTests(CliBinaryFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [DxcFact]
    public async Task PinnedNativesMissing_EveryDxcBackedTargetFailsWithSD0219()
    {
        using CliClone clone = CliClone.Create(_fixture.ExecutablePath);
        clone.DeletePinnedDxc();

        foreach (string profile in DxcBackedProfiles)
        {
            CliRun run = await clone.CompileAsync(FixtureSource, profile);
            _output.WriteLine($"{profile}: exit {run.ExitCode}\n{run.Output}");

            run.ExitCode.ShouldNotBe(0, $"{profile} compiled without ShadowDusk's DXC natives:\n{run.Output}");
            run.Output.ShouldContain("SD0219", Case.Sensitive);
            run.Output.ShouldContain("was not found", Case.Sensitive);
            run.Output.ShouldNotContain("Unhandled exception", Case.Sensitive);
            run.OutputFileExists.ShouldBeFalse($"{profile} wrote an artifact without ShadowDusk's DXC natives");
        }
    }

    /// <summary>
    /// DirectX 11 and FNA ship vkd3d-shader bytecode and reflect it with the managed reader, so
    /// missing DXC natives must not cost the consumer those targets.
    /// </summary>
    [FnaFact]
    public async Task PinnedNativesMissing_DirectX11AndFnaStillCompileTheSameBytes()
    {
        using CliClone intact = CliClone.Create(_fixture.ExecutablePath);
        using CliClone missing = CliClone.Create(_fixture.ExecutablePath);
        missing.DeletePinnedDxc();

        foreach (string profile in new[] { "DirectX_11", "FNA" })
        {
            CliRun expected = await intact.CompileAsync(FixtureSource, profile);
            expected.ExitCode.ShouldBe(0, $"{profile} failed with the natives intact:\n{expected.Output}");

            CliRun actual = await missing.CompileAsync(FixtureSource, profile);
            actual.ExitCode.ShouldBe(0, $"{profile} does not use DXC and must compile without it:\n{actual.Output}");
            actual.Bytes.ShouldBe(expected.Bytes, $"{profile} output changed when the DXC natives were removed");
        }
    }

    /// <summary>
    /// ShadowDusk's own natives absent and a different DXC build in their place: refused, and
    /// the diagnostic names the file and both identities. Before the fix this compiled.
    /// </summary>
    [DxcFact]
    public async Task OnlyAForeignDxcBuildPresent_EveryDxcBackedTargetFailsWithSD0219()
    {
        using CliClone clone = CliClone.Create(_fixture.ExecutablePath);
        string pinnedDirectory = clone.PinnedDxcDirectory;

        // Linux/macOS: the pinned DXC itself with a different build id, i.e. a foreign build
        // that WORKS (on Linux, accepting it compiles every target without a word). Windows:
        // a library that is not DXC under both names; the real foreign pair is the next test.
        if (OperatingSystem.IsWindows())
            ForeignDxc.PlaceNonDxc(clone.DxcFileNames, pinnedDirectory);
        else
            ForeignDxc.PlaceWorkingForeignBuild(Path.Combine(pinnedDirectory, clone.DxcFileName));

        await AssertRefusedAsForeignAsync(clone, Path.Combine(pinnedDirectory, clone.DxcFileName));
    }

    /// <summary>
    /// The measured case: the Windows SDK's real 1.8 pair where ShadowDusk's own should be.
    /// Before the fix DirectX 12 compiled with it (4307 bytes against 4295) and OpenGL failed
    /// with the foreign DXC's own "SPIR-V CodeGen not available".
    /// </summary>
    [WindowsSdkDxilFact]
    public async Task OnlyTheWindowsSdkPairPresent_EveryDxcBackedTargetFailsWithSD0219()
    {
        using CliClone clone = CliClone.Create(_fixture.ExecutablePath);
        string pinnedDirectory = clone.PinnedDxcDirectory;
        clone.DeletePinnedDxc();
        ForeignDxc.CopyWindowsSdkPair(pinnedDirectory);

        await AssertRefusedAsForeignAsync(clone, Path.Combine(pinnedDirectory, clone.DxcFileName));
    }

    /// <summary>
    /// A foreign build in the directory the old order probed FIRST, and ShadowDusk's own pinned
    /// natives beside its assemblies: the pinned ones compile, and the output is byte-identical
    /// to an untouched layout's. Before the fix the foreign file won. It is deliberately NOT a
    /// DXC here: a loader that takes it cannot produce these bytes at all, whereas a working
    /// foreign build of the same source would compile the same output and prove nothing.
    /// </summary>
    [DxcFact]
    public async Task ForeignDxcInTheFirstProbedDirectory_ThePinnedNativesStillCompile()
    {
        using CliClone intact = CliClone.Create(_fixture.ExecutablePath);
        using CliClone shadowed = CliClone.Create(_fixture.ExecutablePath);
        string firstProbed = shadowed.PinnedDxcDirectory;
        shadowed.MovePinnedDxcTo(shadowed.Directory);
        ForeignDxc.PlaceNonDxc(shadowed.DxcFileNames, firstProbed);

        await AssertSameOutputAsync(intact, shadowed);
    }

    /// <summary>The same, with the Windows SDK's real pair as the foreign build.</summary>
    [WindowsSdkDxilFact]
    public async Task WindowsSdkPairInTheFirstProbedDirectory_ThePinnedNativesStillCompile()
    {
        using CliClone intact = CliClone.Create(_fixture.ExecutablePath);
        using CliClone shadowed = CliClone.Create(_fixture.ExecutablePath);
        string firstProbed = shadowed.PinnedDxcDirectory;
        shadowed.MovePinnedDxcTo(shadowed.Directory);
        ForeignDxc.CopyWindowsSdkPair(firstProbed);

        await AssertSameOutputAsync(intact, shadowed);
    }

    /// <summary>
    /// The case a consumer actually hits (measured): another package raises Vortice.Dxc above
    /// 3.3.4, so the natives beside ShadowDusk are a newer, WORKING DXC (1.9.2602.17). Before
    /// issue #270 that compiled without a word. Now it is SD0219, and the message must let the
    /// consumer act on it: it names the build found, the build pinned, the Vortice.Dxc the
    /// process resolved, and the fix, and it does not claim the natives were missing.
    /// </summary>
    [ForeignDxcBuildFact]
    public async Task OnlyANewerWorkingDxcPresent_SD0219NamesBothBuildsAndTheFix()
    {
        using CliClone clone = CliClone.Create(_fixture.ExecutablePath);
        string pinnedDirectory = clone.PinnedDxcDirectory;
        ForeignDxc.PlaceDxc19(clone.DxcFileNames, pinnedDirectory);

        string foundPath = Path.Combine(pinnedDirectory, clone.DxcFileName);
        string found = DxcNativeIdentity.Describe(foundPath);
        string pinned = DxcNativeIdentity.Expected(ForeignDxc.Rid, DxcNativeKind.Compiler)!;
        found.ShouldNotBe(pinned, "the DXC 1.9 fixture reads as the pinned build");

        await AssertRefusedAsForeignAsync(clone, foundPath);
        foreach (string profile in DxcBackedProfiles)
        {
            CliRun run = await clone.CompileAsync(FixtureSource, profile);
            run.Output.ShouldContain(found, Case.Sensitive);
            run.Output.ShouldContain(pinned, Case.Sensitive);
            run.Output.ShouldContain("Vortice.Dxc 3.3.4", Case.Sensitive);
            run.Output.ShouldContain("NU1608", Case.Sensitive);
            run.Output.ShouldContain("pin Vortice.Dxc to 3.3.4", Case.Sensitive);
            run.Output.ShouldNotContain("was not found", Case.Sensitive);
        }
    }

    /// <summary>
    /// win-x64 and win-arm64 carry the same version stamp, and the flat directories are probed
    /// whatever the RID, so the OTHER architecture's pinned pair can be the first candidate.
    /// It cannot load into this process; the search must skip it and use the right copy that
    /// comes later, with the same output as an untouched layout. Before the fix the first
    /// unloadable candidate ended the search with SD0219.
    /// </summary>
    [WindowsDxcFact]
    public async Task OtherArchitecturesPinnedPairProbedFirst_TheRightOneStillCompiles()
    {
        using CliClone intact = CliClone.Create(_fixture.ExecutablePath);
        using CliClone mixed = CliClone.Create(_fixture.ExecutablePath);
        string firstProbed = mixed.PinnedDxcDirectory;
        mixed.MovePinnedDxcTo(mixed.Directory);

        string otherRid = ForeignDxc.Rid == "win-arm64" ? "win-x64" : "win-arm64";
        string otherPair = Path.Combine(
            Path.GetDirectoryName(_fixture.ExecutablePath)!, "runtimes", otherRid, "native");
        foreach (string name in mixed.DxcFileNames)
        {
            string from = Path.Combine(otherPair, name);
            File.Exists(from).ShouldBeTrue($"the CLI build output has no {from}");
            File.Copy(from, Path.Combine(firstProbed, name), overwrite: true);
        }

        await AssertSameOutputAsync(intact, mixed);
    }

    private async Task AssertRefusedAsForeignAsync(CliClone clone, string foreignDxcPath)
    {
        foreach (string profile in DxcBackedProfiles)
        {
            CliRun run = await clone.CompileAsync(FixtureSource, profile);
            _output.WriteLine($"{profile}: exit {run.ExitCode}\n{run.Output}");

            run.ExitCode.ShouldNotBe(0,
                $"{profile} compiled with a DXC that is not ShadowDusk's pinned build ({foreignDxcPath}):\n{run.Output}");
            run.Output.ShouldContain("SD0219", Case.Sensitive);
            run.Output.ShouldContain("not the pinned", Case.Sensitive);

            // The diagnostic names the rejected file. Compared from the clone's own folder
            // down: the child reports its directory through the OS (8.3 names on Windows,
            // /private/var for /var on macOS), so the prefix can be spelled differently.
            run.Output.ShouldContain(
                Path.GetRelativePath(Path.GetDirectoryName(clone.Directory)!, foreignDxcPath), Case.Sensitive);
            run.Output.ShouldNotContain("Unhandled exception", Case.Sensitive);
            run.OutputFileExists.ShouldBeFalse($"{profile} wrote an artifact compiled by a foreign DXC");
        }
    }

    private async Task AssertSameOutputAsync(CliClone intact, CliClone shadowed)
    {
        foreach (string profile in DxcBackedProfiles)
        {
            CliRun expected = await intact.CompileAsync(FixtureSource, profile);
            expected.ExitCode.ShouldBe(0, $"{profile} failed in the untouched layout:\n{expected.Output}");

            CliRun actual = await shadowed.CompileAsync(FixtureSource, profile);
            _output.WriteLine($"{profile}: exit {actual.ExitCode}\n{actual.Output}");
            actual.ExitCode.ShouldBe(0,
                $"{profile} failed although ShadowDusk's pinned DXC sits beside its assemblies:\n{actual.Output}");
            actual.Bytes.ShouldBe(expected.Bytes,
                $"{profile}: a foreign DXC in the application's runtimes directory changed the output");
        }
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    /// <summary>The outcome of one CLI compile.</summary>
    private sealed record CliRun(int ExitCode, string Output, byte[]? Bytes)
    {
        public bool OutputFileExists => Bytes is not null;
    }

    /// <summary>
    /// A private, runnable copy of the CLI's build output holding only the running RID's
    /// natives, so a test can rearrange them without touching the shared build output.
    /// </summary>
    private sealed class CliClone : IDisposable
    {
        private readonly string _executable;

        private CliClone(string directory, string executable)
        {
            Directory = directory;
            _executable = executable;
        }

        public string Directory { get; }

        public string DxcFileName =>
            OperatingSystem.IsWindows() ? "dxcompiler.dll"
            : OperatingSystem.IsMacOS() ? "libdxcompiler.dylib"
            : "libdxcompiler.so";

        /// <summary>
        /// Where the build puts the running RID's DXC natives, which is also the directory the
        /// loader probed first before issue #270: <c>runtimes/&lt;rid&gt;/native</c> on Windows
        /// and Linux, the per-arch subdirectory on macOS.
        /// </summary>
        public string PinnedDxcDirectory => OperatingSystem.IsMacOS()
            ? Path.Combine(Directory, Rid)
            : Path.Combine(Directory, "runtimes", Rid, "native");

        /// <summary>The DXC natives ShadowDusk loads on this OS, by file name.</summary>
        public IReadOnlyList<string> DxcFileNames =>
            OperatingSystem.IsWindows() ? ["dxcompiler.dll", "dxil.dll"] : [DxcFileName];

        private IEnumerable<string> PinnedDxcFiles =>
            DxcFileNames.Select(name => Path.Combine(PinnedDxcDirectory, name));

        private static string Rid => ForeignDxc.Rid;

        public static CliClone Create(string cliExecutable)
        {
            string source = Path.GetDirectoryName(cliExecutable)!;
            string clone = Path.Combine(Path.GetTempPath(), "sd-cli-layout-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(clone);

            foreach (string file in System.IO.Directory.GetFiles(source))
            {
                if (Path.GetExtension(file) is not (".pdb" or ".xml"))
                    File.Copy(file, Path.Combine(clone, Path.GetFileName(file)));
            }

            // Only the running RID's natives: the other RIDs' are never probed.
            foreach (string relative in new[] { Path.Combine("runtimes", Rid, "native"), Rid })
            {
                string from = Path.Combine(source, relative);
                if (!System.IO.Directory.Exists(from)) continue;

                string to = Path.Combine(clone, relative);
                System.IO.Directory.CreateDirectory(to);
                foreach (string file in System.IO.Directory.GetFiles(from))
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }

            var result = new CliClone(clone, Path.Combine(clone, Path.GetFileName(cliExecutable)));
            foreach (string file in result.PinnedDxcFiles)
                File.Exists(file).ShouldBeTrue($"the CLI build output has no {file}: nothing to remove or shadow");
            return result;
        }

        public void DeletePinnedDxc()
        {
            foreach (string file in PinnedDxcFiles.ToList())
                File.Delete(file);
        }

        public void MovePinnedDxcTo(string directory)
        {
            foreach (string file in PinnedDxcFiles.ToList())
                File.Move(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        public async Task<CliRun> CompileAsync(string source, string profile)
        {
            string output = Path.Combine(Directory, $"out_{profile}.bin");
            if (File.Exists(output)) File.Delete(output);

            var psi = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add(output);
            psi.ArgumentList.Add($"/Profile:{profile}");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the CLI clone.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return new CliRun(
                process.ExitCode,
                await stdout + await stderr,
                File.Exists(output) ? await File.ReadAllBytesAsync(output) : null);
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort: a child that has just exited can hold a native briefly.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// Runs <see cref="CliDxcNativeLayoutTests"/> on its own, after the parallel collections.
/// Each case copies the CLI's output (DXC natives included) to a fresh directory and starts
/// the copy several times; on a Windows CI runner every new copy is scanned on first load,
/// and running that alongside the other CLI tests pushed one of their 30-second compiles
/// past its timeout (measured on the first CI run of issue #270).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliDxcNativeLayoutCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "CliDxcNativeLayout";
}

/// <summary>
/// A fact that needs the DXC 1.9 fixture for the running RID (Windows x64/arm64, Linux x64;
/// none ships for macOS, which is covered by the restamped-build cases instead). Skipped
/// elsewhere; where the fixture is expected but missing it runs and fails.
/// </summary>
public sealed class ForeignDxcBuildFactAttribute : FactAttribute
{
    public ForeignDxcBuildFactAttribute()
    {
        if (OperatingSystem.IsMacOS() || (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()))
        {
            Skip = "No DXC 1.9 native ships for this OS (Vortice.Dxc.Native 1.0.5 covers win-x64, win-arm64, linux-x64).";
        }
        else if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                     != System.Runtime.InteropServices.Architecture.X64)
        {
            Skip = "Vortice.Dxc.Native 1.0.5 ships no linux-arm64 native.";
        }
        else if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                     DxcTestGate.DxcAvailable,
                     Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = DxcTestGate.SkipReason;
        }
    }
}

/// <summary>A <see cref="DxcFactAttribute"/> that runs on Windows only.</summary>
public sealed class WindowsDxcFactAttribute : FactAttribute
{
    public WindowsDxcFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Only Windows ships the pinned DXC for two architectures (win-x64, win-arm64).";
        }
        else if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                     DxcTestGate.DxcAvailable,
                     Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = DxcTestGate.SkipReason;
        }
    }
}
