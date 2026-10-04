#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.Integration.Tests.Cli;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Issue #332: a SPIR-V compile with debug information (<c>-Zi</c>, the OpenGL and Vulkan
/// targets) makes DXC load <c>libdxcompiler</c> a second time, by LEAF name, from inside the
/// compile (<c>clang::spirv::ReadSourceCode</c> -&gt; <c>DxcDllSupport::Initialize</c>), to
/// read the source for <c>OpSource</c>. A leaf name is a name search: whatever the dynamic
/// linker answers is loaded and initialized inside the compile. The product rule this pins:
/// <b>that answer is the pinned image, or the debug SPIR-V compile is refused (<c>SD0223</c>);
/// a library that is not the pinned build is never loaded.</b> Release compiles never make the
/// load and must keep compiling whatever sits on the search path.
///
/// <para>Each scenario runs in a fresh child process with the dynamic linker's own trace on
/// (<c>LD_DEBUG=libs</c>; <c>DYLD_PRINT_SEARCHING</c> + <c>DYLD_PRINT_LIBRARIES</c>), so the
/// evidence is what the linker says it searched and loaded, not what ShadowDusk believes. The
/// decoy sits where ONLY a leaf-name load looks: <c>LD_LIBRARY_PATH</c> on Linux (an
/// absolute-path load never consults it), and on macOS the working directory plus
/// <c>DYLD_FALLBACK_LIBRARY_PATH</c> (a <c>DYLD_LIBRARY_PATH</c> decoy substitutes for the
/// absolute-path load itself and is <c>DxcLibraryPathDecoyTests</c>' case). The positive
/// controls: a canary loads by bare name from the decoy directory, and a byte copy of the
/// pinned build placed there IS reached by the compile-time load on Linux (the trace shows
/// glibc initializing it), which proves a foreign build in the same spot would have been loaded
/// had it not been refused. On macOS 14+ dyld answers the leaf with the already-loaded pinned
/// image (install name <c>@rpath/libdxcompiler.dylib</c>) and the trace says so.</para>
///
/// <para>Bytes must not move: every scenario's output for every target is hashed and must equal
/// the no-decoy scenario's, release and debug alike.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcDebugSpirvLeafNameTests
{
    /// <summary>The probe argument; then the canary file name (or <c>-</c>) and a library to preload (or <c>-</c>).</summary>
    public const string ProbeArgument = "--dxc-leaf-name-probe";

    /// <summary>
    /// A literal that reaches DXC in the in-memory source and survives in <c>OpSource</c> text,
    /// but is not an identifier (identifiers also appear as <c>OpName</c> debug names).
    /// </summary>
    private const string InMemoryMarker = "0.31415926";

    /// <summary>What the file DXC's source read would open (<c>hlsl.hlsl</c> in the working directory) contains.</summary>
    private const string DiskMarker = "SdOpSourceFromDiskMarker";

    private const string Fx = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif
        sampler s;
        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(s, uv) * 0.31415926; }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static readonly PlatformTarget[] Targets =
        [PlatformTarget.OpenGL, PlatformTarget.Vulkan, PlatformTarget.DirectX, PlatformTarget.DirectX12];

    /// <summary>What sits at the leaf-name search position.</summary>
    public enum Decoy
    {
        None,
        ForeignBuild,
        CopyOfPinned,
    }

    private readonly ITestOutputHelper _output;

    public DxcDebugSpirvLeafNameTests(ITestOutputHelper output) => _output = output;

    [UnixDxcFact]
    public async Task DebugSpirvCompile_NeverHandsDxcAForeignLibraryFromTheLeafNameSearchPath()
    {
        bool mac = OperatingSystem.IsMacOS();
        var reports = new Dictionary<Decoy, Report>();
        foreach (Decoy decoy in new[] { Decoy.None, Decoy.ForeignBuild, Decoy.CopyOfPinned })
            reports[decoy] = await RunScenarioAsync(decoy);

        foreach ((Decoy decoy, Report report) in reports)
        {
            string tag = $"[{decoy}]";
            report.Values["canary"].ShouldBe(["True"], $"{tag} the decoy directory is not reachable by a bare-name load, so nothing here is tested");
            report.Values["dxcMapped"].ShouldHaveSingleItem().ShouldNotContain(report.DecoyName, Case.Sensitive,
                $"{tag} the load-time path substituted the decoy; that is DxcLibraryPathDecoyTests' case, not this one");

            // Release compiles never make the leaf-name load: nothing on the search path may touch them.
            foreach (string target in new[] { "OpenGL", "Vulkan", "DirectX", "DirectX12" })
                report.Values[$"{target}.release"].ShouldBe(["OK"], $"{tag} {target} release: {report.Message(target, "release")}");
            report.Values["DirectX12.debug"].ShouldBe(["OK"], $"{tag} DXIL debug information is built in-process; only SPIR-V makes the load");
            report.Values["DirectX.debug"].ShouldBe(["OK"], tag);

            string leafResolved = report.Values["leaf.resolved"].ShouldHaveSingleItem();
            string dxcMapped = report.Values["dxcMapped"][0];
            _output.WriteLine($"{tag} leaf-name lookup: resolved={leafResolved} foundUnloadedFile={report.Values["leaf.foundUnloadedFile"][0]} " +
                              $"linkerError={report.Values["leaf.linkerError"][0]} error={report.Values["leaf.error"][0]} " +
                              $"cost={report.Values["leaf.costMicros"][0]} us; Vulkan debug OpSource from {report.Values.GetValueOrDefault("Vulkan.debug.opsource")?[0] ?? "-"}");

            if (mac)
            {
                // dyld 1122+ (macOS 14): a leaf-name dlopen means "@rpath/<leaf>" when an image with
                // that install name is loaded, and ours is @rpath/libdxcompiler.dylib. The
                // runner's dyld must say so, in the lookup and in its own trace of the compile.
                leafResolved.ShouldBe(dxcMapped,
                    $"{tag} dyld did not answer the leaf name with the already-loaded pinned image; the macOS-12/13 fallback " +
                    "(a stat of the directories dyld-940/1042 search) is now the live path on this runner, which these tests " +
                    "cannot measure, so the CI lane needs a look");
                report.Trace.ShouldContain(l => l.Contains("already-loaded-by-rpath", StringComparison.Ordinal) && l.Contains("libdxcompiler", StringComparison.Ordinal),
                    $"{tag} DYLD_PRINT_SEARCHING did not show dyld resolving libdxcompiler.dylib to the loaded image:\n{string.Join("\n", report.Trace)}");
                report.Values["OpenGL.debug"].ShouldBe(["OK"], $"{tag} {report.Message("OpenGL", "debug")}");
                report.Values["Vulkan.debug"].ShouldBe(["OK"], $"{tag} {report.Message("Vulkan", "debug")}");
            }
            else
            {
                leafResolved.ShouldBe("-", $"{tag} glibc matched the leaf 'libdxcompiler.so' to a loaded object; the pinned SONAME is libdxcompiler.so.3.7, so which one?");
            }

            // The decoy is never loaded unless it is the pinned build itself.
            List<string> decoyLoads = report.Trace.Where(l => IsLoadReport(l, mac) && l.Contains(report.DecoyName, StringComparison.Ordinal)
                                                             && l.Contains("libdxcompiler", StringComparison.Ordinal)).ToList();
            List<string> decoyImages = report.Values.GetValueOrDefault("image", []).Where(i => i.Contains(report.DecoyName, StringComparison.Ordinal)
                                                             && Path.GetFileName(i).StartsWith("libdxcompiler", StringComparison.Ordinal)).ToList();
            switch (decoy)
            {
                case Decoy.None:
                    report.Values["OpenGL.debug"].ShouldBe(["OK"], $"{tag} {report.Message("OpenGL", "debug")}");
                    report.Values["Vulkan.debug"].ShouldBe(["OK"], $"{tag} {report.Message("Vulkan", "debug")}");
                    report.Values["leaf.error"].ShouldBe(["-"], tag);
                    break;

                case Decoy.ForeignBuild:
                    decoyLoads.ShouldBeEmpty($"{tag} the dynamic linker loaded the foreign build inside the compile");
                    decoyImages.ShouldBeEmpty($"{tag} the foreign build is mapped into the process");
                    if (mac)
                    {
                        // Nothing to refuse: dyld hands DXC the pinned image before searching anywhere.
                        report.Values["OpenGL.debug"].ShouldBe(["OK"], tag);
                        report.Values["Vulkan.debug"].ShouldBe(["OK"], tag);
                    }
                    else
                    {
                        // glibc would load it (RTLD_NOLOAD found the file), so the compile is refused, naming it.
                        report.Values["leaf.foundUnloadedFile"].ShouldBe(["True"], $"{tag} glibc did not report the file on LD_LIBRARY_PATH");
                        foreach (string target in new[] { "OpenGL", "Vulkan" })
                        {
                            report.Values[$"{target}.debug"].ShouldBe(["SD0223"], $"{tag} {target}: {report.Message(target, "debug")}");
                            report.Message(target, "debug").ShouldContain(report.DecoyName, Case.Sensitive);
                            report.Message(target, "debug").ShouldContain("LD_LIBRARY_PATH", Case.Sensitive);
                        }
                    }
                    break;

                case Decoy.CopyOfPinned:
                    // The same build is the same compiler: compiles, and the bytes are below.
                    report.Values["OpenGL.debug"].ShouldBe(["OK"], $"{tag} {report.Message("OpenGL", "debug")}");
                    report.Values["Vulkan.debug"].ShouldBe(["OK"], $"{tag} {report.Message("Vulkan", "debug")}");
                    if (!mac)
                    {
                        // The positive control for the ForeignBuild case: the compile-time load from
                        // inside DXC really reaches LD_LIBRARY_PATH (glibc initializes the copy there).
                        decoyLoads.ShouldNotBeEmpty($"{tag} glibc never loaded the copy on LD_LIBRARY_PATH during the debug compiles, so the " +
                            $"ForeignBuild refusal above protects against nothing this test can see:\n{string.Join("\n", report.Trace)}");
                    }
                    break;
            }
        }

        // Issue #343: wherever DXC's leaf-name load lands, its source read never does. The input
        // name ShadowDusk passes cannot be opened on any host, so every scenario embeds the
        // in-memory source (before the fix the hlsl.hlsl in the working directory won on macOS
        // and with the copy on LD_LIBRARY_PATH; DxcDebugSourceWorkingDirectoryTests keeps that
        // control).
        // Every debug Vulkan compile that produced a module must say where its OpSource came from;
        // a refused one (SD0223, the Linux foreign-build scenario, asserted above) has no module.
        bool anyCompiled = false;
        foreach ((Decoy decoy, Report report) in reports)
        {
            if (report.Values["Vulkan.debug"] is not ["OK"])
            {
                report.Values.ContainsKey("Vulkan.debug.opsource").ShouldBeFalse($"[{decoy}] a refused compile reported an OpSource");
                continue;
            }

            anyCompiled = true;
            report.Values.ContainsKey("Vulkan.debug.opsource").ShouldBeTrue($"[{decoy}] the probe did not report where the debug Vulkan OpSource came from");
            report.Values["Vulkan.debug.opsource"].ShouldBe(["memory"], $"[{decoy}] the debug Vulkan OpSource came from a file, not the compiled source");
        }

        anyCompiled.ShouldBeTrue("no scenario produced a debug Vulkan module, so the OpSource check above checked nothing");

        // No emitted byte moves with what sits on the search path, release or debug.
        Report baseline = reports[Decoy.None];
        foreach (string key in baseline.Values.Keys.Where(k => k.EndsWith(".sha256", StringComparison.Ordinal)))
        {
            foreach ((Decoy decoy, Report report) in reports)
            {
                if (report.Values.TryGetValue(key, out List<string>? hash))
                    hash.ShouldBe(baseline.Values[key], $"[{decoy}] {key} differs from the no-decoy compile");
            }
        }
    }

    [WindowsDxcFact]
    public async Task Windows_TheLeafNameLoadReturnsThePinnedModule_AndDebugSpirvCompiles()
    {
        Report report = await RunProbeAsync(NewTempDir("sd-dxc-leaf-"), preload: null, canary: null);

        string mapped = report.Values["dxcMapped"].ShouldHaveSingleItem();
        DxcLoader.SamePath(report.Values["leaf.resolved"].ShouldHaveSingleItem(), mapped).ShouldBeTrue(
            $"LoadLibrary(\"dxcompiler.dll\") would not return ShadowDusk's module: {report.Values["leaf.resolved"][0]} vs {mapped}");
        report.Values["leaf.error"].ShouldBe(["-"]);
        foreach (string target in new[] { "OpenGL", "Vulkan" })
            report.Values[$"{target}.debug"].ShouldBe(["OK"], report.Message(target, "debug"));
        report.Values["Vulkan.debug.opsource"].ShouldBe(["memory"], "issue #343: the hlsl.hlsl in the working directory must not become the OpSource text");
        _output.WriteLine($"lookup cost {report.Values["leaf.costMicros"][0]} us");
    }

    [ForeignDxcBuildFact]
    public async Task AForeignDxcompilerLoadedFirst_RefusesOnlyTheDebugSpirvCompiles()
    {
        // The real foreign build (DXC 1.9) loaded into the process before ShadowDusk's first use:
        // on Windows LoadLibrary("dxcompiler.dll") returns the FIRST module of that base name.
        string rid = ForeignDxc.Rid;
        string foreign = Path.Combine(ForeignDxc.Dxc19Directory(rid),
            OperatingSystem.IsWindows() ? "dxcompiler.dll" : "libdxcompiler.so");
        File.Exists(foreign).ShouldBeTrue(ForeignDxc.MissingFixture(foreign));

        Report report = await RunProbeAsync(NewTempDir("sd-dxc-leaf-"), preload: foreign, canary: null);

        foreach (string target in new[] { "OpenGL", "Vulkan", "DirectX", "DirectX12" })
            report.Values[$"{target}.release"].ShouldBe(["OK"], $"{target} release: {report.Message(target, "release")}");
        string resolved = report.Values["leaf.resolved"].ShouldHaveSingleItem();
        _output.WriteLine($"leaf resolved to {resolved}; error {report.Values["leaf.error"][0]}");
        if (OperatingSystem.IsWindows())
        {
            DxcLoader.SamePath(resolved, foreign).ShouldBeTrue($"the first-loaded module must be the foreign one: {resolved}");
            foreach (string target in new[] { "OpenGL", "Vulkan" })
            {
                report.Values[$"{target}.debug"].ShouldBe(["SD0223"], $"{target}: {report.Message(target, "debug")}");
                report.Message(target, "debug").ShouldContain(Path.GetFileName(Path.GetDirectoryName(foreign))!, Case.Sensitive);
            }
        }
        else
        {
            // glibc matches the leaf against the preloaded object's SONAME and names: refused iff it does.
            bool matchesForeign = resolved != "-" && !DxcNativeIdentity.Matches(resolved, DxcNativeIdentity.Expected(rid, DxcNativeKind.Compiler));
            foreach (string target in new[] { "OpenGL", "Vulkan" })
                report.Values[$"{target}.debug"].ShouldBe([matchesForeign ? "SD0223" : "OK"], $"{target}: {report.Message(target, "debug")}");
        }
    }

    [WindowsDxcFact]
    public async Task ACopyOfThePinnedDxcompilerLoadedFirst_IsTheSameCompiler()
    {
        string dir = NewTempDir("sd-dxc-leaf-copy-");
        string copy = Path.Combine(dir, "dxcompiler.dll");
        File.Copy(Path.Combine(ForeignDxc.NativeDirectory(ForeignDxc.Rid), "dxcompiler.dll"), copy);
        File.Copy(Path.Combine(ForeignDxc.NativeDirectory(ForeignDxc.Rid), "dxil.dll"), Path.Combine(dir, "dxil.dll"));

        Report report = await RunProbeAsync(NewTempDir("sd-dxc-leaf-"), preload: copy, canary: null);

        DxcLoader.SamePath(report.Values["leaf.resolved"].ShouldHaveSingleItem(), copy).ShouldBeTrue("the copy loaded first is what the base name returns");
        report.Values["leaf.error"].ShouldBe(["-"], "a byte copy of the pinned build is the same compiler");
        foreach (string target in new[] { "OpenGL", "Vulkan" })
            report.Values[$"{target}.debug"].ShouldBe(["OK"], report.Message(target, "debug"));
    }

    /// <summary>
    /// Child-process body. Preloads a library if asked, loads the canary by bare name if asked,
    /// then compiles every target in release and debug, printing <c>Target.mode=OK|CODE</c>,
    /// <c>message.Target.mode=</c>, <c>Target.mode.sha256=</c>, the Vulkan debug build's
    /// <c>OpSource</c> provenance, the loader's leaf-name lookup, the mapped DXC and the
    /// DXC/DXIL/canary images in the process.
    /// </summary>
    public static int RunProbe(string canaryFileName, string preload)
    {
        if (preload != "-")
        {
            NativeLibrary.Load(preload);
            Console.WriteLine($"preloaded={preload}");
        }

        if (canaryFileName != "-")
            Console.WriteLine($"canary={NativeLibrary.TryLoad(canaryFileName, out _)}");

        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in Targets)
        {
            foreach (bool debug in new[] { false, true })
            {
                string mode = debug ? "debug" : "release";
                var result = compiler.CompileAsync(Fx, new CompilerOptions
                {
                    Target = target,
                    Debug = debug,
                    SourceFileName = "leaf.fx",
                }).GetAwaiter().GetResult();

                Console.WriteLine(result.IsSuccess ? $"{target}.{mode}=OK" : $"{target}.{mode}={result.Error[0].Code}");
                if (result.IsFailure)
                {
                    Console.WriteLine($"message.{target}.{mode}={result.Error[0].Message.ReplaceLineEndings(" ")}");
                    continue;
                }

                Console.WriteLine($"{target}.{mode}.sha256={Convert.ToHexString(SHA256.HashData(result.Value.Data))}");
                if (target == PlatformTarget.Vulkan && debug)
                {
                    bool disk = result.Value.Data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(DiskMarker)) >= 0;
                    bool memory = result.Value.Data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(InMemoryMarker)) >= 0;
                    Console.WriteLine($"Vulkan.debug.opsource={(disk, memory) switch { (true, true) => "both", (true, false) => "disk", (false, true) => "memory", _ => "neither" }}");
                }
            }
        }

        DxcLeafNameLookup.Outcome? lookup = DxcLoader.LeafNameLookup;
        Console.WriteLine($"leaf.resolved={lookup?.ResolvedImage ?? "-"}");
        Console.WriteLine($"leaf.foundUnloadedFile={lookup?.FoundUnloadedFile ?? false}");
        Console.WriteLine($"leaf.linkerError={(lookup?.LinkerError is { Length: > 0 } e ? e.ReplaceLineEndings(" ") : "-")}");
        Console.WriteLine($"leaf.error={lookup?.Error?.Code ?? "-"}");
        Console.WriteLine($"leaf.costMicros={(lookup is null ? 0 : (long)lookup.Cost.TotalMicroseconds)}");
        foreach (DxcLeafNameLookup.Candidate candidate in lookup?.Candidates ?? [])
            Console.WriteLine($"leaf.candidate={candidate.Path}|{(candidate.IsPinnedBuild ? "pinned" : "foreign")}|{candidate.Description}");

        if (DxcLoader.MappedDxcImagePath() is { } dxcMapped)
            Console.WriteLine($"dxcMapped={dxcMapped}");

        foreach (string image in MappedImages())
        {
            string leaf = Path.GetFileName(image);
            if (leaf.Contains("dxcompiler", StringComparison.OrdinalIgnoreCase)
                || leaf.Contains("dxil", StringComparison.OrdinalIgnoreCase)
                || leaf == canaryFileName)
            {
                Console.WriteLine($"image={image}");
            }
        }

        return 0;
    }

    private static IEnumerable<string> MappedImages()
    {
        if (OperatingSystem.IsMacOS())
            return DxcLoader.LoadedImages.MacImagePaths();
        if (OperatingSystem.IsWindows())
            return Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName).ToList();

        return File.ReadLines("/proc/self/maps")
            .Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 6 && fields[5].StartsWith('/'))
            .Select(fields => fields[5].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A line of the linker's trace that reports a library being loaded (not merely tried).</summary>
    private static bool IsLoadReport(string line, bool mac) =>
        mac
            ? !line.Contains("possible path", StringComparison.Ordinal) && !line.Contains("find path", StringComparison.Ordinal)
              && !line.Contains("found:", StringComparison.Ordinal) && !line.Contains("tried:", StringComparison.Ordinal)
            : line.Contains("calling init:", StringComparison.Ordinal);

    private sealed record Report(string DecoyName, Dictionary<string, List<string>> Values, List<string> Trace)
    {
        public string Message(string target, string mode) =>
            Values.GetValueOrDefault($"message.{target}.{mode}")?[0] ?? "";
    }

    private async Task<Report> RunScenarioAsync(Decoy decoy)
    {
        bool mac = OperatingSystem.IsMacOS();
        string extension = mac ? ".dylib" : ".so";
        string rid = DxcLoader.PinnedRid(mac ? "osx" : "linux", RuntimeInformation.ProcessArchitecture);
        string pinnedDxc = mac
            ? Path.Combine(AppContext.BaseDirectory, rid, "libdxcompiler.dylib")
            : Path.Combine(ForeignDxc.NativeDirectory(rid), "libdxcompiler.so");
        string spirvCross = Path.Combine(ForeignDxc.NativeDirectory(rid), "libspirv-cross" + extension);
        File.Exists(pinnedDxc).ShouldBeTrue($"{pinnedDxc} is not beside the test assembly");
        File.Exists(spirvCross).ShouldBeTrue($"{spirvCross} is not beside the test assembly");

        string decoyDir = NewTempDir("sd-dxc-leaf-");
        try
        {
            string canary = "libsdcanary" + extension;
            File.Copy(spirvCross, Path.Combine(decoyDir, canary));

            if (decoy != Decoy.None)
            {
                string decoyDxc = Path.Combine(decoyDir, "libdxcompiler" + extension);
                File.Copy(pinnedDxc, decoyDxc);
                if (decoy == Decoy.ForeignBuild)
                {
                    ForeignDxc.PlaceWorkingForeignBuild(decoyDxc);
                    if (mac)
                        await AdHocSignAsync(decoyDxc);
                }
            }

            return await RunProbeAsync(decoyDir, preload: null, canary: canary);
        }
        finally
        {
            TryDelete(decoyDir);
        }
    }

    /// <summary>
    /// Runs the probe with <paramref name="workingDirectory"/> as the child's working directory,
    /// first on the leaf-name-only search path (<c>LD_LIBRARY_PATH</c>; <c>DYLD_FALLBACK_LIBRARY_PATH</c>),
    /// holding a <c>hlsl.hlsl</c> whose text tells a disk read from the in-memory fallback, with the
    /// dynamic linker's trace on.
    /// </summary>
    private async Task<Report> RunProbeAsync(string workingDirectory, string? preload, string? canary)
    {
        File.WriteAllText(Path.Combine(workingDirectory, "hlsl.hlsl"), $"// {DiskMarker}\nfloat4 main() : SV_Target {{ return 0; }}\n");

        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet) { WorkingDirectory = workingDirectory };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(ProbeArgument);
        psi.ArgumentList.Add(canary ?? "-");
        psi.ArgumentList.Add(preload ?? "-");

        if (OperatingSystem.IsMacOS())
        {
            string inherited = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH") is { Length: > 0 } f ? f : "/usr/local/lib:/usr/lib";
            psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = workingDirectory + ":" + inherited;
            psi.Environment["DYLD_PRINT_SEARCHING"] = "1";
            psi.Environment["DYLD_PRINT_LIBRARIES"] = "1";
        }
        else if (OperatingSystem.IsLinux())
        {
            string? inherited = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(inherited) ? workingDirectory : workingDirectory + ":" + inherited;
            psi.Environment["LD_DEBUG"] = "libs";
        }

        ChildProcessResult run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(180), "leaf-name probe", captureHangEvidence: true);

        List<string> trace = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("dxcompiler", StringComparison.OrdinalIgnoreCase) || l.Contains("sdcanary", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _output.WriteLine($"cwd={workingDirectory} preload={preload ?? "-"}\n{run.Stdout}\n--- linker trace (dxcompiler lines) ---\n{string.Join("\n", trace)}");
        run.ExitCode.ShouldBe(0, $"leaf-name probe crashed:\n{run.Stdout}\n{run.Stderr}");

        Dictionary<string, List<string>> values = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], kv => kv[1], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return new Report(Path.GetFileName(workingDirectory), values, trace);
    }

    /// <summary>macOS: <c>codesign --force --sign - path</c>, so a restamped (foreign) build is loadable on Apple silicon.</summary>
    private async Task AdHocSignAsync(string path)
    {
        var psi = new ProcessStartInfo("codesign");
        foreach (string argument in new[] { "--force", "--sign", "-", path })
            psi.ArgumentList.Add(argument);

        ChildProcessResult run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(60));
        _output.WriteLine($"codesign {path}: {run.Output}");
        run.ExitCode.ShouldBe(0, $"codesign failed: {run.Output}");
    }

    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A fact for Linux and macOS with the restored DXC natives. Reported as SKIPPED, never passed,
/// on Windows and where DXC is not restored (unless <c>SHADOWDUSK_REQUIRE_DXC</c> is set, as in
/// CI: then it runs and fails).
/// </summary>
public sealed class UnixDxcFactAttribute : FactAttribute
{
    public UnixDxcFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip = "The dynamic-linker search path (LD_LIBRARY_PATH / DYLD_FALLBACK_LIBRARY_PATH) only exists on Linux and macOS.";
        }
        else if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                     Tests.DxcTestGate.DxcAvailable,
                     Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = Tests.DxcTestGate.SkipReason;
        }
    }
}
