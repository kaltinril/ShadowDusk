#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using Shouldly;
using ShadowDusk.Integration.Tests.Tests;
using ShadowDusk.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Cli;

/// <summary>
/// The OS library search path must never decide which vkd3d-shader or SPIRV-Cross compiles
/// (issue #350; <c>CliDxcPathHijackTest</c> is the DXC counterpart, issue #270).
///
/// <para><b>The defect this pins.</b> <c>Vkd3dLoader</c> and <c>SpvcLoader</c> ended their
/// probes with a bare-name <c>NativeLibrary.TryLoad("libvkd3d-shader-1.dll")</c> /
/// <c>TryLoad("spirv-cross.dll")</c> (<c>.so</c> / <c>.dylib</c> elsewhere), and returned
/// <c>IntPtr.Zero</c> to the runtime's default probing after that. A bare name is the OS
/// search: <c>PATH</c> on Windows, <c>LD_LIBRARY_PATH</c> on Linux,
/// <c>DYLD_FALLBACK_LIBRARY_PATH</c> and the working directory on macOS. Whenever the
/// app-local probe missed (a RID-specific publish, any plugin host such as MGCB) a library of
/// that name there was loaded instead of the pinned build: a different compiler, silently, or
/// a planted library running inside the process.</para>
///
/// <para>Every case runs the real CLI in a <b>fresh child process</b> (module state is
/// per-process) with a decoy directory first on every search variable and as the working
/// directory, on all three desktop OSes:</para>
/// <list type="bullet">
/// <item>pinned natives in place, foreign libraries of the same names on the search path: the
///   output is byte-identical to the clean-path compile;</item>
/// <item>the pinned native REMOVED from a copy of the CLI and a byte copy of it on the search
///   path, which the old bare-name fallback loaded and compiled with: refused with the loader's
///   code, never compiled;</item>
/// <item>a foreign library in place of the pinned file inside the CLI's own directory: refused
///   before it is loaded, naming the file;</item>
/// <item>macOS: dyld resolves even an absolute-path load against <c>DYLD_LIBRARY_PATH</c> by
///   leaf name, so the pinned file cannot keep a decoy there out. A byte copy is the same build
///   and compiles identically; a foreign library is refused.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliNativeSearchPathHijackTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;
    private readonly ITestOutputHelper _output;
    private static readonly string FixturesDir = FindFixturesDir();

    public CliNativeSearchPathHijackTest(CliBinaryFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>The libraries under test, by the CLI profile that runs each.</summary>
    public enum Library
    {
        /// <summary>vkd3d-shader: DirectX 11 and FNA.</summary>
        Vkd3d,

        /// <summary>SPIRV-Cross: OpenGL.</summary>
        SpirvCross,
    }

    private static string CodeOf(Library library) => library == Library.Vkd3d ? "SD0211" : "SD0103";

    [PinnedNativesCliTheory]
    [InlineData("DirectX_11")]
    [InlineData("FNA")]
    [InlineData("OpenGL")]
    public async Task ForeignLibrariesOnTheSearchPath_DoNotChangeTheOutput(string profile)
    {
        string decoyDir = CreateDecoyDirectory(foreign: true, CliDirectory());
        try
        {
            byte[] clean = await CompileOkAsync(_fixture.ExecutablePath, profile, decoyDir: null, dyld: false);
            byte[] withDecoys = await CompileOkAsync(_fixture.ExecutablePath, profile, decoyDir, dyld: false);
            withDecoys.ShouldBe(clean,
                $"a foreign vkd3d-shader/SPIRV-Cross on the search path changed the {profile} output");
        }
        finally
        {
            TryDelete(decoyDir);
        }
    }

    [PinnedNativesCliTheory]
    [InlineData(Library.Vkd3d, "DirectX_11")]
    [InlineData(Library.Vkd3d, "FNA")]
    [InlineData(Library.SpirvCross, "OpenGL")]
    public async Task PinnedNativeMissing_CopyOnTheSearchPath_IsRefusedNotLoaded(Library library, string profile)
    {
        string cliCopy = CopyCli();
        string decoyDir = Path.Combine(Path.GetTempPath(), "sd-native-decoy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(decoyDir);
        try
        {
            // A WORKING copy of the pinned build, reachable only by bare name: the old loader's
            // fallback took it and compiled, so success here would be the hijack.
            List<string> removed = NativeFiles(cliCopy, library).ToList();
            removed.ShouldNotBeEmpty($"the CLI copy holds no {library} native to remove");
            string pinned = PinnedFileFor(cliCopy, library);
            foreach (string name in LeafNames(library))
                File.Copy(pinned, Path.Combine(decoyDir, name), overwrite: true);
            foreach (string file in removed)
                File.Delete(file);

            (int exit, string output) = await RunCliAsync(
                Path.Combine(cliCopy, Path.GetFileName(_fixture.ExecutablePath)), profile, decoyDir, dyld: false);

            exit.ShouldNotBe(0, $"the {profile} compile succeeded with the pinned {library} removed: it loaded the copy on the search path\n{output}");
            output.ShouldContain(CodeOf(library), Case.Sensitive);
            output.ShouldContain("was not found", Case.Sensitive);
            output.ShouldContain("never loads it by bare name", Case.Sensitive);
        }
        finally
        {
            TryDelete(decoyDir);
            TryDelete(cliCopy);
        }
    }

    [PinnedNativesCliTheory]
    [InlineData(Library.Vkd3d, "DirectX_11")]
    [InlineData(Library.SpirvCross, "OpenGL")]
    public async Task ForeignLibraryInTheApplicationDirectory_IsRefusedBeforeLoading(Library library, string profile)
    {
        string cliCopy = CopyCli();
        try
        {
            string foreignSource = PinnedFileFor(cliCopy, library == Library.Vkd3d ? Library.SpirvCross : Library.Vkd3d);
            string foreignCopy = Path.Combine(Path.GetTempPath(), "sd-foreign-" + Guid.NewGuid().ToString("N"));
            File.Copy(foreignSource, foreignCopy);
            List<string> replaced = NativeFiles(cliCopy, library).ToList();
            replaced.ShouldNotBeEmpty();
            foreach (string file in replaced)
                File.Copy(foreignCopy, file, overwrite: true);
            File.Delete(foreignCopy);

            (int exit, string output) = await RunCliAsync(
                Path.Combine(cliCopy, Path.GetFileName(_fixture.ExecutablePath)), profile, decoyDir: null, dyld: false);

            exit.ShouldNotBe(0, $"the {profile} compile ran with a foreign {library} in place of the pinned one\n{output}");
            output.ShouldContain(CodeOf(library), Case.Sensitive);
            output.ShouldContain("but not its pinned build", Case.Sensitive);
        }
        finally
        {
            TryDelete(cliCopy);
        }
    }

    /// <summary>
    /// macOS only: <c>DYLD_LIBRARY_PATH</c> substitutes even absolute-path loads by leaf name.
    /// ShadowDusk checks the image dyld mapped: the same build (a byte copy) compiles to the
    /// same bytes, a foreign library is refused.
    /// </summary>
    [MacPinnedNativesCliTheory]
    [InlineData(Library.Vkd3d, "DirectX_11", false)]
    [InlineData(Library.Vkd3d, "DirectX_11", true)]
    [InlineData(Library.SpirvCross, "OpenGL", false)]
    [InlineData(Library.SpirvCross, "OpenGL", true)]
    public async Task MacDyldLibraryPath_OnlyThePinnedBuildCompiles(Library library, string profile, bool foreign)
    {
        string cli = CliDirectory();
        string decoyDir = Path.Combine(Path.GetTempPath(), "sd-native-dyld-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(decoyDir);
        try
        {
            string source = PinnedFileFor(cli, foreign
                ? (library == Library.Vkd3d ? Library.SpirvCross : Library.Vkd3d)
                : library);
            foreach (string name in LeafNames(library))
                File.Copy(source, Path.Combine(decoyDir, name), overwrite: true);

            byte[] clean = await CompileOkAsync(_fixture.ExecutablePath, profile, decoyDir: null, dyld: false);
            if (!foreign)
            {
                byte[] same = await CompileOkAsync(_fixture.ExecutablePath, profile, decoyDir, dyld: true);
                same.ShouldBe(clean, "a byte copy of the pinned build is the same compiler");
                return;
            }

            (int exit, string output) = await RunCliAsync(_fixture.ExecutablePath, profile, decoyDir, dyld: true);
            exit.ShouldNotBe(0, $"the {profile} compile ran with a foreign library dyld substituted\n{output}");
            output.ShouldContain(CodeOf(library), Case.Sensitive);
            output.ShouldContain("dyld mapped", Case.Sensitive);
        }
        finally
        {
            TryDelete(decoyDir);
        }
    }

    private async Task<byte[]> CompileOkAsync(string executable, string profile, string? decoyDir, bool dyld)
    {
        string output = Path.Combine(Path.GetTempPath(), $"sd-native-{profile}-{Guid.NewGuid():N}.out");
        try
        {
            (int exit, string text) = await RunCliAsync(executable, profile, decoyDir, dyld, output);
            exit.ShouldBe(0, $"{profile} compile failed (decoys: {decoyDir ?? "none"}):\n{text}");
            return await File.ReadAllBytesAsync(output);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private async Task<(int ExitCode, string Output)> RunCliAsync(
        string executable, string profile, string? decoyDir, bool dyld, string? outputFile = null)
    {
        string source = Path.Combine(FixturesDir, "shaders", "Grayscale.fx");
        string output = outputFile ?? Path.Combine(Path.GetTempPath(), $"sd-native-{profile}-{Guid.NewGuid():N}.out");
        try
        {
            ChildProcessResult run = await CliProcess.RunAsync(
                executable,
                [source, output, $"/Profile:{profile}"],
                TimeSpan.FromSeconds(120),
                psi =>
                {
                    if (decoyDir is null)
                        return;

                    // Every variable a bare-name load consults, and the working directory
                    // (macOS resolves a leaf-name dlopen there too).
                    psi.WorkingDirectory = decoyDir;
                    Prepend(psi, "PATH", decoyDir);
                    Prepend(psi, "LD_LIBRARY_PATH", decoyDir);
                    Prepend(psi, "DYLD_FALLBACK_LIBRARY_PATH", decoyDir, "/usr/local/lib:/usr/lib");
                    if (dyld)
                        Prepend(psi, "DYLD_LIBRARY_PATH", decoyDir);
                });

            string text = run.Stdout + "\n" + run.Stderr;
            _output.WriteLine($"{Path.GetFileName(executable)} /Profile:{profile} (decoys: {decoyDir ?? "none"}, dyld: {dyld}) -> {run.ExitCode}\n{text}");
            return (run.ExitCode, text);
        }
        finally
        {
            if (outputFile is null && File.Exists(output)) File.Delete(output);
        }
    }

    private static void Prepend(ProcessStartInfo psi, string variable, string directory, string? defaultValue = null)
    {
        string? inherited = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(inherited))
            inherited = defaultValue;
        psi.Environment[variable] = string.IsNullOrEmpty(inherited)
            ? directory
            : directory + Path.PathSeparator + inherited;
    }

    /// <summary>
    /// A directory holding a library under every leaf name either loader asks for. Foreign:
    /// each is the OTHER pinned library renamed, loadable but not the requested one.
    /// </summary>
    private static string CreateDecoyDirectory(bool foreign, string cliDirectory)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sd-native-decoy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (Library library in new[] { Library.Vkd3d, Library.SpirvCross })
        {
            Library sourceOf = foreign
                ? (library == Library.Vkd3d ? Library.SpirvCross : Library.Vkd3d)
                : library;
            string source = PinnedFileFor(cliDirectory, sourceOf);
            foreach (string name in LeafNames(library))
                File.Copy(source, Path.Combine(dir, name), overwrite: true);
        }

        return dir;
    }

    /// <summary>The leaf names a bare-name load of <paramref name="library"/> asked for on this OS.</summary>
    private static string[] LeafNames(Library library) => (library, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()) switch
    {
        (Library.Vkd3d, true, _) => ["libvkd3d-shader-1.dll"],
        (Library.Vkd3d, _, true) => ["libvkd3d-shader.1.dylib", "libvkd3d-shader.dylib"],
        (Library.Vkd3d, _, _) => ["libvkd3d-shader.so.1", "libvkd3d-shader.so"],
        (_, true, _) => ["spirv-cross.dll"],
        (_, _, true) => ["libspirv-cross.dylib"],
        _ => ["libspirv-cross.so"],
    };

    /// <summary>Every copy of <paramref name="library"/>'s file for this OS inside a CLI directory.</summary>
    private static IEnumerable<string> NativeFiles(string cliDirectory, Library library) =>
        Directory.EnumerateFiles(cliDirectory, "*", SearchOption.AllDirectories)
            .Where(f => LeafNames(library).Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));

    /// <summary>The file the pinned loader loads for this process's RID, from a CLI directory.</summary>
    private static string PinnedFileFor(string cliDirectory, Library library)
    {
        string rid = Rid();
        string[] candidates = library == Library.Vkd3d
            ? [Path.Combine(cliDirectory, rid, LeafNames(library)[0]), Path.Combine(cliDirectory, LeafNames(library)[0])]
            : [Path.Combine(cliDirectory, "runtimes", rid, "native", LeafNames(library)[0])];
        string? found = candidates.FirstOrDefault(File.Exists);
        found.ShouldNotBeNull($"no pinned {library} for {rid} in {cliDirectory}");
        return found;
    }

    private static string Rid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return os + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");
    }

    private string CliDirectory() => Path.GetDirectoryName(_fixture.ExecutablePath)!;

    /// <summary>
    /// A copy of the CLI's directory, without the natives of other RIDs (they are most of its
    /// 200+ MB and the host never touches them).
    /// </summary>
    private string CopyCli()
    {
        string source = CliDirectory();
        string target = Path.Combine(Path.GetTempPath(), "sd-cli-copy-" + Guid.NewGuid().ToString("N"));
        string rid = Rid();
        string[] ridDirectories = ["win-x64", "win-arm64", "win-x86", "linux-x64", "linux-arm64", "linux-arm",
            "osx-x64", "osx-arm64", "android-arm64", "android-x64", "android-arm"];

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            bool otherRid =
                (parts.Length > 1 && ridDirectories.Contains(parts[0]) && parts[0] != rid)
                || (parts.Length > 2 && parts[0] == "runtimes" && parts[1] != rid);
            if (otherRid)
                continue;

            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: a child that has just exited can hold a library briefly.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindFixturesDir()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return Path.Combine(dir.FullName, "tests", "fixtures");
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}

/// <summary>
/// A theory needing the restored vkd3d-shader (DirectX 11, FNA) and DXC (OpenGL starts at DXC;
/// on macOS it is a restored dylib). Skipped when either is absent, unless
/// <c>SHADOWDUSK_REQUIRE_VKD3D</c> / <c>SHADOWDUSK_REQUIRE_DXC</c> is set (CI): then it runs and fails.
/// </summary>
public sealed class PinnedNativesCliTheoryAttribute : TheoryAttribute
{
    public PinnedNativesCliTheoryAttribute()
    {
        if (NativeRequirement.ShouldSkip(
                FnaTestGate.Vkd3dAvailable, Environment.GetEnvironmentVariable(NativeRequirement.Vkd3dEnvVar)))
        {
            Skip = FnaTestGate.SkipReason;
        }
        else if (NativeRequirement.ShouldSkip(
                     DxcTestGate.DxcAvailable, Environment.GetEnvironmentVariable(NativeRequirement.DxcEnvVar)))
        {
            Skip = DxcTestGate.SkipReason;
        }
    }
}

/// <summary><see cref="PinnedNativesCliTheoryAttribute"/>, on macOS only (dyld's <c>DYLD_LIBRARY_PATH</c>).</summary>
public sealed class MacPinnedNativesCliTheoryAttribute : TheoryAttribute
{
    public MacPinnedNativesCliTheoryAttribute()
    {
        Skip = OperatingSystem.IsMacOS()
            ? new PinnedNativesCliTheoryAttribute().Skip
            : "DYLD_LIBRARY_PATH's substitution of absolute-path loads exists only on macOS.";
    }
}
