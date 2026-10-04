#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The dynamic linker's search path must never decide which DXC compiles (Linux and macOS;
/// <c>CliDxcPathHijackTest</c> is the Windows counterpart).
///
/// <para><b>The defect this pins (issue #270).</b> Off Windows, Vortice.Dxc's own
/// <c>Dxc.ResolveLibrary</c> handler resolves DXC with
/// <c>NativeLibrary.TryLoad("dxil") &amp;&amp; NativeLibrary.TryLoad("dxcompiler")</c>: two
/// bare-name loads, which the dynamic linker answers from <c>LD_LIBRARY_PATH</c> (Linux) or
/// <c>DYLD_LIBRARY_PATH</c>, the working directory and <c>/usr/local/lib</c> (macOS). On macOS
/// ShadowDusk subscribed AFTER that handler, so with a <c>libdxil.dylib</c> +
/// <c>libdxcompiler.dylib</c> pair reachable by name, OpenGL and Vulkan compiled with that
/// pair instead of the pinned build, silently. ShadowDusk's handler now runs first on every
/// OS and answers with the library it loaded by absolute path.</para>
///
/// <para>The probe runs in a <b>fresh child process</b> with a decoy directory first on the
/// library path and as the working directory. The decoy <c>libdxcompiler</c> is a byte-for-byte
/// copy of the pinned native, so a loader that takes it compiles successfully and nothing but the
/// MAPPED PATH can tell: the probe reports every DXC image mapped into the process
/// (<c>/proc/self/maps</c> on Linux, dyld's image list on macOS) and the test requires the
/// pinned one, and never a decoy. A canary library that only the decoy directory holds, loaded
/// by bare name, proves the search path really reaches it, so the test cannot pass because the
/// decoys were simply out of reach.</para>
///
/// <para><b>Two host layouts.</b> A bare-name load made through .NET first tries the host's
/// native search directories (<c>NATIVE_DLL_SEARCH_DIRECTORIES</c>, from the application's
/// <c>deps.json</c>), and only then the dynamic linker's path. In an ordinary app those
/// directories hold the Vortice natives, so on Linux even Vortice's handler finds the right
/// files. The layout that IS hijackable is a host whose <c>deps.json</c> knows nothing about
/// ShadowDusk's natives: any plugin host (MGCB), and every macOS layout in which our dylib
/// sits in the per-arch subdirectory. The probe therefore also runs with a
/// <c>--depsfile</c> stripped of every native asset, which is that host.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcLibraryPathDecoyTests
{
    /// <summary>The probe argument; the next argument is the canary's file name.</summary>
    public const string ProbeArgument = "--dxc-library-path-probe";

    private static readonly PlatformTarget[] Targets =
        [PlatformTarget.OpenGL, PlatformTarget.Vulkan, PlatformTarget.DirectX, PlatformTarget.DirectX12];

    private const string Fx = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif
        sampler s;
        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(s, uv) * 0.5; }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private readonly ITestOutputHelper _output;

    public DxcLibraryPathDecoyTests(ITestOutputHelper output) => _output = output;

    [UnixDxcTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecoyDxcPairOnTheLibraryPath_ThePinnedLibraryIsTheOneMapped(bool hostKnowsTheNatives)
    {
        bool mac = OperatingSystem.IsMacOS();
        string extension = mac ? ".dylib" : ".so";
        string rid = DxcLoader.PinnedRid(mac ? "osx" : "linux", RuntimeInformation.ProcessArchitecture);

        // The pinned natives as the build lays them out beside this assembly.
        string pinnedDxc = mac
            ? Path.Combine(AppContext.BaseDirectory, rid, "libdxcompiler.dylib")
            : Path.Combine(ForeignDxc.NativeDirectory(rid), "libdxcompiler.so");
        string spirvCross = Path.Combine(ForeignDxc.NativeDirectory(rid), "libspirv-cross" + extension);
        File.Exists(pinnedDxc).ShouldBeTrue($"{pinnedDxc} is not beside the test assembly");
        File.Exists(spirvCross).ShouldBeTrue($"{spirvCross} is not beside the test assembly");

        string decoyName = "sd-dxc-libpath-" + Guid.NewGuid().ToString("N");
        string decoyDir = Path.Combine(Path.GetTempPath(), decoyName);
        Directory.CreateDirectory(decoyDir);
        string? depsFile = null;
        try
        {
            // What Vortice's bare-name loads ask for: libdxil + libdxcompiler. The libdxcompiler
            // decoy is a working copy of the pinned DXC, so a loader that takes it compiles and
            // only the mapped path can tell. The libdxil decoy only has to be loadable, which is
            // all Vortice's TryLoad("dxil") asks: Linux uses Vortice's own libdxil.so; macOS ships
            // none, so it is a library that is not DXC. NOT a second copy of DXC: our macOS
            // DXC dlopens "libdxil.dylib" from its own constructor, and two DXC images in one
            // process overflow the stack inside malloc on the first compile (measured on the
            // macOS CI lane).
            File.Copy(pinnedDxc, Path.Combine(decoyDir, "libdxcompiler" + extension));
            File.Copy(
                mac ? spirvCross : Path.Combine(ForeignDxc.NativeDirectory(rid), "libdxil.so"),
                Path.Combine(decoyDir, "libdxil" + extension));

            string canary = "libsdcanary" + extension;
            File.Copy(spirvCross, Path.Combine(decoyDir, canary));

            depsFile = hostKnowsTheNatives ? null : WriteDepsFileWithoutNativeAssets(decoyName);
            // Linux: LD_LIBRARY_PATH. macOS: DYLD_FALLBACK_LIBRARY_PATH, the variable behind the
            // default /usr/local/lib lookup, which (unlike DYLD_LIBRARY_PATH, see
            // MacDyldLibraryPathDecoy_OnlyThePinnedBuildCompiles) a leaf-name load consults but
            // an absolute-path load of an existing file does not.
            Dictionary<string, List<string>> report = await RunProbeAsync(
                decoyDir, canary, depsFile, mac ? "DYLD_FALLBACK_LIBRARY_PATH" : "LD_LIBRARY_PATH");

            // Positive controls first: without them a green result proves nothing.
            report["canary"].ShouldBe(["True"],
                "a library that exists only in the decoy directory did not load by bare name, so " +
                "the dynamic linker never searched it and this test cannot see a hijack");
            if (!hostKnowsTheNatives)
            {
                report["hostSearchesRuntimes"].ShouldBe(["False"],
                    "the stripped deps file did not take the natives out of the host's search " +
                    "directories, so this is not the plugin-host layout the case is about");
            }

            List<string> images = report.GetValueOrDefault("image") ?? [];
            images.ShouldContain(i => Path.GetFileName(i) == canary && i.Contains(decoyName, StringComparison.Ordinal),
                "the image listing does not show the canary that was just loaded from the decoy " +
                "directory, so it cannot be trusted to show a decoy DXC either");

            List<string> dxcImages = images
                .Where(i => Path.GetFileName(i).StartsWith("libdxcompiler", StringComparison.Ordinal))
                .ToList();
            dxcImages.ShouldNotBeEmpty("no libdxcompiler image is mapped although DXC compiled");
            dxcImages.ShouldAllBe(i => !i.Contains(decoyName, StringComparison.Ordinal),
                "DXC was loaded from the library search path instead of ShadowDusk's pinned copy");
            dxcImages.ShouldAllBe(i => Path.GetFileName(Path.GetDirectoryName(i)) == (mac ? rid : "native"),
                "the mapped libdxcompiler is not the one the build placed beside the assemblies");

            // The image the OS names for ShadowDusk's own DXC handle (dladdr on macOS,
            // dl_iterate_phdr on Linux): the one DxcLoader verified, and never a decoy.
            // (Compared by its last two path parts: /proc/self/maps resolves symlinks, the linker's
            // own record keeps the path it was given.)
            string dxcMapped = report["dxcMapped"].ShouldHaveSingleItem();
            dxcMapped.ShouldNotContain(decoyName, Case.Sensitive);
            Path.GetFileName(Path.GetDirectoryName(dxcMapped)).ShouldBe(mac ? rid : "native", dxcMapped);
            Path.GetFileName(dxcMapped).ShouldBe("libdxcompiler" + extension, dxcMapped);

            // SPIR-V codegen and DirectX 11 never call the DXIL validator: they compile on
            // every host, whatever libdxil is lying around.
            report["OpenGL"].ShouldBe(["OK"]);
            report["Vulkan"].ShouldBe(["OK"]);
            report["DirectX"].ShouldBe(["OK"]);

            List<string> dxilImages = images
                .Where(i => DxcLoader.IsDxilLeafName(Path.GetFileName(i)))
                .ToList();
            if (mac)
            {
                // Our macOS DXC dlopens "libdxil.dylib" by leaf name while it loads, so it
                // reaches the decoy; finding no DxcCreateInstance there, DXC dlcloses it and runs
                // without a validator. Whether dyld really unmaps it is dyld's business, so the
                // invariant is the one ShadowDusk owns: a libdxil image in the process refuses
                // validated DXIL (DirectX 12) loudly, and no such image means DX12 compiles
                // (unsigned, as every macOS DX12 compile is).
                report["DirectX12"].ShouldBe(dxilImages.Count > 0 ? ["SD0219"] : ["OK"],
                    $"libdxil images mapped: [{string.Join(", ", dxilImages)}]");
            }
            else
            {
                // Vortice's Linux DXC never opens a libdxil, and ShadowDusk loads none.
                dxilImages.ShouldBeEmpty("a libdxil was loaded on Linux, where DXC has no use for one");
                report["DirectX12"].ShouldBe(["OK"]);
            }
        }
        finally
        {
            TryDelete(decoyDir);
            if (depsFile is not null)
                File.Delete(depsFile);
        }
    }

    /// <summary>
    /// macOS only: dyld resolves EVERY load against <c>DYLD_LIBRARY_PATH</c> by leaf name first,
    /// absolute paths included, so loading the pinned dylib by path cannot keep a
    /// <c>libdxcompiler.dylib</c> there out (measured on the macOS CI lane). What ShadowDusk
    /// owns is the outcome: the image dyld really mapped is checked, a byte copy of the pinned
    /// build is the same compiler and compiles, and a different build is refused with
    /// <c>SD0219</c> for every DXC-backed target (DirectX 11 does not use DXC and compiles).
    /// </summary>
    [MacDxcTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MacDyldLibraryPathDecoy_OnlyThePinnedBuildCompiles(bool foreignBuild)
    {
        string rid = DxcLoader.PinnedRid("osx", RuntimeInformation.ProcessArchitecture);
        string pinnedDxc = Path.Combine(AppContext.BaseDirectory, rid, "libdxcompiler.dylib");
        string spirvCross = Path.Combine(ForeignDxc.NativeDirectory(rid), "libspirv-cross.dylib");
        string pinnedId = DxcNativeIdentity.Expected(rid, DxcNativeKind.Compiler)!;
        File.Exists(pinnedDxc).ShouldBeTrue($"{pinnedDxc} is not beside the test assembly");

        string decoyName = "sd-dxc-dyld-" + Guid.NewGuid().ToString("N");
        string decoyDir = Path.Combine(Path.GetTempPath(), decoyName);
        Directory.CreateDirectory(decoyDir);
        try
        {
            string decoyDxc = Path.Combine(decoyDir, "libdxcompiler.dylib");
            File.Copy(pinnedDxc, decoyDxc);
            if (foreignBuild)
            {
                ForeignDxc.PlaceWorkingForeignBuild(decoyDxc);

                // Issue #289: restamping the LC_UUID invalidates the ad-hoc code signature, and
                // on Apple silicon dyld then refuses to map the decoy and falls back to the path
                // it was given: the test could not tell "dyld mapped the foreign build and
                // ShadowDusk refused it" from "dyld never mapped it". Re-signing makes the
                // foreign build loadable on every arch, so dyld's substitution really happens and
                // the assertions below can require it.
                await AdHocSignAsync(decoyDxc);
            }

            const string canary = "libsdcanary.dylib";
            File.Copy(spirvCross, Path.Combine(decoyDir, canary));

            Dictionary<string, List<string>> report =
                await RunProbeAsync(decoyDir, canary, depsFile: null, "DYLD_LIBRARY_PATH");
            report["canary"].ShouldBe(["True"], "DYLD_LIBRARY_PATH did not reach the decoy directory");

            List<string> dxcImages = (report.GetValueOrDefault("image") ?? [])
                .Where(i => Path.GetFileName(i).StartsWith("libdxcompiler", StringComparison.Ordinal))
                .ToList();
            List<string> foreignImages = dxcImages.Where(i => !DxcNativeIdentity.Matches(i, pinnedId)).ToList();

            // The image dladdr names for DxcCreateInstance in the handle ShadowDusk loaded: what
            // DxcLoader itself checked. It must be the DECOY in both cases, because dyld searches
            // DYLD_LIBRARY_PATH by leaf name ahead of the absolute path (the measured behavior
            // this test exists for); anything else means the case was not exercised.
            string mappedByDladdr = report["dxcMapped"].ShouldHaveSingleItem();
            string mapped = $"dladdr: {mappedByDladdr}; libdxcompiler images mapped: [{string.Join(", ", dxcImages)}]";
            _output.WriteLine(mapped);
            // (By directory NAME: dyld may report the temp directory through /private.)
            mappedByDladdr.ShouldContain(decoyName, Case.Sensitive,
                $"dyld did not substitute the DYLD_LIBRARY_PATH copy for the absolute-path load ({mapped})");

            report["DirectX"].ShouldBe(["OK"], "DirectX 11 does not use DXC and must compile");
            if (!foreignBuild)
            {
                // The same build from another directory is the same compiler.
                foreignImages.ShouldBeEmpty(mapped);
                report["OpenGL"].ShouldBe(["OK"], mapped);
                report["Vulkan"].ShouldBe(["OK"], mapped);
                return;
            }

            // A different build, really mapped: ShadowDusk must refuse it for every DXC-backed
            // target, and say that dyld substituted it (not that nothing was found).
            foreignImages.ShouldContain(mappedByDladdr, mapped);
            foreach (string target in new[] { "OpenGL", "Vulkan", "DirectX12" })
            {
                report[target].ShouldBe(["SD0219"], $"{target}: {mapped}");
                report[$"message.{target}"].ShouldHaveSingleItem().ShouldContain("but dyld mapped", Case.Sensitive);
            }
        }
        finally
        {
            TryDelete(decoyDir);
        }
    }

    /// <summary>
    /// Child-process body. Prints <c>canary=True|False</c>, whether the host's native search
    /// directories include a <c>runtimes</c> directory, one <c>Target=OK|CODE</c> line per
    /// target, then one <c>image=path</c> line per mapped DXC, DXIL or canary image.
    /// </summary>
    public static int RunProbe(string canaryFileName)
    {
        // A bare name goes straight to dlopen: the dynamic linker's search path, nothing else.
        Console.WriteLine($"canary={NativeLibrary.TryLoad(canaryFileName, out _)}");

        string searchDirectories = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "";
        bool searchesRuntimes = searchDirectories
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => d.Contains($"{Path.DirectorySeparatorChar}runtimes{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Console.WriteLine($"hostSearchesRuntimes={searchesRuntimes}");

        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in Targets)
        {
            var result = compiler.CompileAsync(Fx, new CompilerOptions
            {
                Target = target,
                SourceFileName = "libpath.fx",
            }).GetAwaiter().GetResult();

            Console.WriteLine(result.IsSuccess ? $"{target}=OK" : $"{target}={result.Error[0].Code}");
            if (result.IsFailure)
            {
                Console.WriteLine($"message.{target}={result.Error[0].Message.ReplaceLineEndings(" ")}");
                foreach (ShaderError e in result.Error)
                    Console.Error.WriteLine($"{target}: {e.Code} {e.Message}");
            }
        }

        // Which DXC image the OS says ShadowDusk's handle really is (dladdr on macOS,
        // dl_iterate_phdr on Linux), when DXC was loaded at all.
        if (DxcLoader.MappedDxcImagePath() is { } dxcMapped)
            Console.WriteLine($"dxcMapped={dxcMapped}");

        foreach (string image in MappedImages())
        {
            string leaf = Path.GetFileName(image);
            if (leaf.Contains("dxcompiler", StringComparison.Ordinal)
                || leaf.Contains("dxil", StringComparison.Ordinal)
                || leaf == canaryFileName)
            {
                Console.WriteLine($"image={image}");
            }
        }

        return 0;
    }

    /// <summary>Every native image mapped into this process: dyld's list, or <c>/proc/self/maps</c>.</summary>
    private static IEnumerable<string> MappedImages()
    {
        if (OperatingSystem.IsMacOS())
            return DxcLoader.LoadedImages.MacImagePaths();

        // "address perms offset dev inode   /path/to/file": the path is the sixth field.
        return File.ReadLines("/proc/self/maps")
            .Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 6 && fields[5].StartsWith('/'))
            .Select(fields => fields[5].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// This assembly's <c>deps.json</c> with every native asset removed, so the child host's
    /// native search directories are its application directory alone: what a plugin sees
    /// inside a host that has never heard of its packages. Written BESIDE the assembly under a
    /// unique name: the host resolves the deps file's app-local assemblies against the deps
    /// file's own directory, so one written elsewhere cannot load ShadowDusk at all.
    /// </summary>
    private static string WriteDepsFileWithoutNativeAssets(string uniqueName)
    {
        string assembly = typeof(DxcConcurrencyProbe).Assembly.Location;
        JsonNode deps = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(assembly, ".deps.json")))!;

        int removed = 0;
        foreach ((string _, JsonNode? target) in deps["targets"]!.AsObject())
        {
            foreach ((string _, JsonNode? library) in target!.AsObject())
            {
                JsonObject entry = library!.AsObject();
                removed += entry.Remove("native") ? 1 : 0;
                if (entry["runtimeTargets"] is not JsonObject runtimeTargets)
                    continue;

                foreach (string asset in runtimeTargets
                             .Where(a => (string?)a.Value?["assetType"] == "native")
                             .Select(a => a.Key)
                             .ToList())
                {
                    runtimeTargets.Remove(asset);
                    removed++;
                }
            }
        }

        removed.ShouldBeGreaterThan(0, "the test assembly's deps.json lists no native assets to strip");

        string path = Path.Combine(Path.GetDirectoryName(assembly)!, uniqueName + ".deps.json");
        File.WriteAllText(path, deps.ToJsonString());
        return path;
    }

    private async Task<Dictionary<string, List<string>>> RunProbeAsync(
        string decoyDir, string canary, string? depsFile, string variable)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

        var psi = new ProcessStartInfo(dotnet)
        {
            // macOS also resolves a leaf-name dlopen against the working directory.
            WorkingDirectory = decoyDir,
        };
        psi.ArgumentList.Add("exec");
        if (depsFile is not null)
        {
            psi.ArgumentList.Add("--depsfile");
            psi.ArgumentList.Add(depsFile);
        }
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(ProbeArgument);
        psi.ArgumentList.Add(canary);

        // Setting DYLD_FALLBACK_LIBRARY_PATH replaces dyld's default, so keep that default after
        // the decoy directory.
        string? inherited = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(inherited) && variable == "DYLD_FALLBACK_LIBRARY_PATH")
            inherited = "/usr/local/lib:/usr/lib";
        psi.Environment[variable] = string.IsNullOrEmpty(inherited)
            ? decoyDir
            : decoyDir + Path.PathSeparator + inherited;

        ChildProcessResult run = await ChildProcess.RunAsync(
            psi, TimeSpan.FromSeconds(120), "library-path probe", captureHangEvidence: true);

        string output = run.Stdout;
        _output.WriteLine($"{variable}={decoyDir}\n{output}\n{run.Stderr}");
        run.ExitCode.ShouldBe(0, $"library-path probe crashed:\n{output}\n{run.Stderr}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], kv => kv[1], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>macOS: <c>codesign --force --sign - path</c> (an ad-hoc signature).</summary>
    private async Task AdHocSignAsync(string path)
    {
        var psi = new ProcessStartInfo("codesign");
        foreach (string argument in new[] { "--force", "--sign", "-", path })
            psi.ArgumentList.Add(argument);

        ChildProcessResult run = await ChildProcess.RunAsync(psi, TestBudget.Compile);

        _output.WriteLine($"codesign {path}: {run.Output}");
        run.ExitCode.ShouldBe(0, $"codesign failed: {run.Output}");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: a child that has just exited can hold a library briefly.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A theory for Linux and macOS, where the dynamic linker's search path is an environment
/// variable. Reported as SKIPPED, never passed, on Windows (covered by
/// <c>CliDxcPathHijackTest</c>) and where the macOS DXC dylib has not been restored (unless
/// <c>SHADOWDUSK_REQUIRE_DXC</c> is set, as in CI: then it runs and fails).
/// </summary>
public sealed class UnixDxcTheoryAttribute : TheoryAttribute
{
    public UnixDxcTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip = "The dynamic-linker search path (LD_LIBRARY_PATH / DYLD_LIBRARY_PATH) only exists on Linux and macOS.";
        }
        else if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                     Tests.DxcTestGate.DxcAvailable,
                     Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = Tests.DxcTestGate.SkipReason;
        }
    }
}

/// <summary>
/// A theory for macOS with the restored DXC dylib. Reported as SKIPPED, never passed,
/// elsewhere, and where the dylib has not been restored unless <c>SHADOWDUSK_REQUIRE_DXC</c>
/// is set (CI): then it runs and fails.
/// </summary>
public sealed class MacDxcTheoryAttribute : TheoryAttribute
{
    public MacDxcTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "DYLD_LIBRARY_PATH's substitution of absolute-path loads exists only on macOS.";
        }
        else if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                     Tests.DxcTestGate.DxcAvailable,
                     Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = Tests.DxcTestGate.SkipReason;
        }
    }
}
