#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Decides what DXC's OWN load of its library by leaf name would hand it, and refuses the one
/// kind of compile that makes that load when the answer is not ShadowDusk's pinned image
/// (issue #332).
/// </summary>
/// <remarks>
/// <para>
/// <b>The load.</b> Every SPIR-V compile with debug information (<c>-Zi</c> on the OpenGL and
/// Vulkan targets) reads the shader source for <c>OpSource</c> through
/// <c>clang::spirv::ReadSourceCode</c>, which does <c>DxcDllSupport::Initialize()</c>:
/// <c>dlopen("libdxcompiler.so" | "libdxcompiler.dylib", RTLD_LAZY)</c> or
/// <c>LoadLibraryW(L"dxcompiler.dll")</c>, a LEAF name (<c>dxcapi.use.h</c> at the pinned
/// commit), then <c>DxcCreateInstance(CLSID_DxcLibrary)</c> on whatever came back,
/// <c>CreateBlobFromFile</c> on the input name (<c>hlsl.hlsl</c>, relative to the working
/// directory, since ShadowDusk compiles from memory) and <c>dlclose</c>. A failed load, or a
/// missing file, falls back to the in-memory source. Nothing else DXC does during a compile
/// loads a library: the validator is bound once while the library itself loads
/// (<see cref="DxcLoader"/>), and release compiles never reach <c>ReadSourceCode</c>.
/// </para>
/// <para>
/// <b>What the leaf name resolves to, by dynamic linker.</b> Windows returns the module
/// already loaded under that base name, the first one loaded if there are two. glibc matches
/// a leaf name against the names each loaded object was found under and its <c>SONAME</c>;
/// the pinned <c>libdxcompiler.so</c> is loaded by absolute path and its <c>SONAME</c> is
/// <c>libdxcompiler.so.3.7</c>, so the leaf never matches it, and glibc searches
/// <c>LD_LIBRARY_PATH</c>, <c>ld.so.cache</c> and the default directories (measured with
/// <c>LD_DEBUG=libs</c> on the ubuntu lane: <c>LD_LIBRARY_PATH</c>, then
/// <c>/lib/x86_64-linux-gnu</c>, <c>/usr/lib/x86_64-linux-gnu</c>, <c>/lib</c>, <c>/usr/lib</c>;
/// the pinned build's <c>RUNPATH</c> <c>$ORIGIN/../lib</c> did not appear), where a different
/// DXC (the Vulkan SDK's, when its <c>setup-env.sh</c> is sourced) would be loaded and
/// initialized inside the compile, as a byte copy placed there measurably is.
/// dyld from macOS 14 (dyld-1122) treats a leaf-name <c>dlopen</c> as <c>@rpath/leaf</c> when
/// an image with that install name is already loaded, and the pinned dylib's install name is
/// <c>@rpath/libdxcompiler.dylib</c>, so there the leaf resolves to the pinned image before any
/// directory is searched; dyld on macOS 12 and 13 (dyld-940, dyld-1042) has no such rule and
/// searches <c>DYLD_LIBRARY_PATH</c>, the rpath expansion, <c>/usr/lib</c>, the working
/// directory and (dyld-940) the fallback directories.
/// </para>
/// <para>
/// <b>How it is asked.</b> The dynamic linker itself is asked first, without loading anything:
/// <c>dlopen(leaf, RTLD_LAZY | RTLD_NOLOAD)</c> returns the already-loaded object a plain
/// <c>dlopen</c> of that leaf would return, or null. On glibc a null with no <c>dlerror</c>
/// text means the search found a file that is not loaded (the one a plain <c>dlopen</c> would
/// load); a null with an error means nothing was found and DXC's own load will fail as it
/// does today. dyld reports nothing about files under <c>RTLD_NOLOAD</c>, so a miss there is
/// followed by a stat of the directories dyld-940/1042 would search. Windows is asked with
/// <c>GetModuleHandleW</c>. A candidate that carries the pinned build identity (the same file
/// through another path, or a byte copy) is the same compiler and is not a finding, the same
/// rule <see cref="DxcLoader"/> applies at load time; anything else is reported with
/// <see cref="ErrorCode"/> for the debug SPIR-V compiles only, since no other request makes
/// the load. Cannot ask the linker means no finding, never a guess.
/// </para>
/// </remarks>
internal static unsafe class DxcLeafNameLookup
{
    /// <summary>
    /// The diagnostic raised instead of running a debug SPIR-V compile whose source read would
    /// load a <c>libdxcompiler</c> that is not ShadowDusk's pinned build.
    /// </summary>
    internal const string ErrorCode = "SD0221";

    /// <summary>
    /// The leaf name DXC's SPIR-V emitter loads (<c>dxc::kDxCompilerLib</c>, built from
    /// <c>CMAKE_SHARED_LIBRARY_PREFIX "dxcompiler" CMAKE_SHARED_LIBRARY_SUFFIX</c>).
    /// </summary>
    internal static string LeafName =>
        OperatingSystem.IsWindows() ? "dxcompiler.dll"
        : OperatingSystem.IsMacOS() ? DxcLoader.MacLibFileName
        : "libdxcompiler.so";

    /// <summary>The DXC arguments whose compile performs the leaf-name load.</summary>
    internal static bool CompileReadsSourceThroughLeafNameLoad(IReadOnlyList<string> arguments) =>
        arguments.Contains("-spirv") && arguments.Contains("-Zi");

    /// <summary>What the dynamic linker answered, for diagnostics and the integration probes.</summary>
    /// <param name="ResolvedImage">The image a leaf-name load returns without loading anything, or null.</param>
    /// <param name="FoundUnloadedFile">glibc only: the search found a file that is not loaded.</param>
    /// <param name="LinkerError">The <c>dlerror</c> text after a miss, or null.</param>
    /// <param name="Candidates">The files found where a leaf-name search looks, when the linker had to be seconded.</param>
    /// <param name="Error">The <see cref="ErrorCode"/> error, or null when the pinned image is what resolves.</param>
    /// <param name="Cost">Wall time of the whole evaluation.</param>
    internal sealed record Outcome(
        string? ResolvedImage,
        bool FoundUnloadedFile,
        string? LinkerError,
        IReadOnlyList<Candidate> Candidates,
        ShaderError? Error,
        TimeSpan Cost)
    {
        /// <summary>True when a plain leaf-name load is known to return the pinned image itself.</summary>
        public bool ResolvesToPinned => ResolvedImage is not null && Error is null;
    }

    /// <summary>A file at a position the leaf-name search would try.</summary>
    internal readonly record struct Candidate(string Path, bool IsPinnedBuild, string Description);

    /// <summary>
    /// Evaluates the lookup for the running process. <paramref name="pinnedMappedPath"/> is the
    /// image the linker mapped for ShadowDusk's handle; <paramref name="pinnedIdentity"/> the
    /// pinned build identity for this RID; <paramref name="pinnedDirectory"/> the directory
    /// that image was loaded from.
    /// </summary>
    public static Outcome Evaluate(string pinnedMappedPath, string pinnedIdentity, string pinnedDirectory)
    {
        var clock = Stopwatch.StartNew();
        Outcome outcome;
        try
        {
            outcome = OperatingSystem.IsWindows()
                ? EvaluateWindows(pinnedMappedPath, pinnedIdentity)
                : EvaluateUnix(pinnedMappedPath, pinnedIdentity, pinnedDirectory);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
        {
            // Cannot ask the dynamic linker: no finding, never a guess.
            outcome = new Outcome(null, false, $"linker not askable: {ex.Message}", [], null, TimeSpan.Zero);
        }

        return outcome with { Cost = clock.Elapsed };
    }

    private static Outcome EvaluateWindows(string pinnedMappedPath, string pinnedIdentity)
    {
        string? bound = DxcLoader.WindowsModules.GetLoadedModulePath(LeafName);
        if (bound is null)
            return new Outcome(null, false, "no module of that name is loaded", [], null, TimeSpan.Zero);

        var candidate = new Candidate(
            bound,
            DxcLoader.SamePath(bound, pinnedMappedPath) || DxcNativeIdentity.Matches(bound, pinnedIdentity),
            DxcNativeIdentity.Describe(bound));
        return new Outcome(bound, false, null, [candidate],
            Decide(resolved: candidate, foundUnloadedFile: false, [candidate], pinnedMappedPath, pinnedIdentity),
            TimeSpan.Zero);
    }

    private static Outcome EvaluateUnix(string pinnedMappedPath, string pinnedIdentity, string pinnedDirectory)
    {
        bool mac = OperatingSystem.IsMacOS();
        nint program = NativeLibrary.GetMainProgramHandle();
        var dlopen = (delegate* unmanaged<byte*, int, nint>)NativeLibrary.GetExport(program, "dlopen");
        var dlerror = (delegate* unmanaged<byte*>)NativeLibrary.GetExport(program, "dlerror");
        var dlclose = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(program, "dlclose");

        const int rtldLazy = 1;
        int rtldNoLoad = mac ? 0x10 : 0x4;

        byte[] leaf = Encoding.UTF8.GetBytes(LeafName + "\0");
        nint handle;
        string? error;
        fixed (byte* p = leaf)
        {
            dlerror();
            handle = dlopen(p, rtldLazy | rtldNoLoad);
            byte* message = dlerror();
            error = message == null ? null : Marshal.PtrToStringUTF8((nint)message);
        }

        if (handle != 0)
        {
            string? resolved = mac
                ? DxcLoader.LoadedImages.MacImagePathOf(handle, "DxcCreateInstance")
                : DxcLoader.LoadedImages.ElfImageOf(handle, "DxcCreateInstance")?.Path;
            dlclose(handle);

            Candidate candidate = resolved is null
                ? new Candidate($"an already-loaded {LeafName} without DxcCreateInstance", false, "not a DXC library")
                : new Candidate(resolved, resolved == pinnedMappedPath || DxcNativeIdentity.Matches(resolved, pinnedIdentity), DxcNativeIdentity.Describe(resolved));
            return new Outcome(resolved ?? candidate.Path, false, null, [candidate],
                Decide(candidate, foundUnloadedFile: false, [candidate], pinnedMappedPath, pinnedIdentity),
                TimeSpan.Zero);
        }

        // glibc: a miss with no error text is a file it found but did not load (RTLD_NOLOAD);
        // a miss with an error text means nothing is there to load. dyld never looks at the
        // file system under RTLD_NOLOAD and reports an empty error either way.
        bool foundUnloadedFile = !mac && error is null;
        List<Candidate> candidates = (mac
                ? MacLeafNameSearchPaths(
                    Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH"),
                    Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH"),
                    Environment.CurrentDirectory,
                    Environment.ProcessPath is { } exe ? Path.GetDirectoryName(exe) : null)
                : LinuxLeafNameSearchPaths(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"), pinnedDirectory))
            .Where(File.Exists)
            .Select(path => new Candidate(
                path,
                IsSameFile(path, pinnedMappedPath) || DxcNativeIdentity.Matches(path, pinnedIdentity),
                DxcNativeIdentity.Describe(path)))
            .ToList();

        return new Outcome(null, foundUnloadedFile, error, candidates,
            Decide(resolved: null, foundUnloadedFile, candidates, pinnedMappedPath, pinnedIdentity),
            TimeSpan.Zero);
    }

    /// <summary>
    /// The verdict, pure: the error for the debug SPIR-V compiles, or null when every answer
    /// is the pinned build. <paramref name="resolved"/> is what an already-loaded match
    /// returned (null for a miss); <paramref name="foundUnloadedFile"/> is glibc's report that
    /// the search found a file it did not load; <paramref name="candidates"/> are the files
    /// found where the search looks.
    /// </summary>
    internal static ShaderError? Decide(
        Candidate? resolved,
        bool foundUnloadedFile,
        IReadOnlyList<Candidate> candidates,
        string pinnedMappedPath,
        string pinnedIdentity)
    {
        string leaf = LeafName;
        string fix = OperatingSystem.IsWindows()
            ? "Whatever loaded it first must not share the process with ShadowDusk's debug OpenGL/Vulkan compiles, or compile those effects without debug information."
            : OperatingSystem.IsMacOS()
                ? "Remove it from DYLD_LIBRARY_PATH, DYLD_FALLBACK_LIBRARY_PATH and the working directory, or compile without debug information."
                : "Remove it from LD_LIBRARY_PATH (the Vulkan SDK's setup-env.sh puts its own libdxcompiler.so there), or compile without debug information.";
        string why =
            $"ShadowDusk refuses this SPIR-V compile with debug information (-Zi, the OpenGL and Vulkan targets): " +
            $"DXC's SPIR-V emitter reads the source for OpSource through a second load of its own library by " +
            $"leaf name ('{leaf}'), and on this machine the dynamic linker would answer that name with ";

        if (resolved is { } r)
        {
            return r.IsPinnedBuild
                ? null
                : Error(why + $"'{r.Path}' ({r.Description}), a library already loaded into this process, not " +
                        $"ShadowDusk's pinned DXC '{pinnedMappedPath}' ({pinnedIdentity}). A different library would run " +
                        "inside the compile. " + fix + " Release compiles never make this load and are unaffected.");
        }

        List<Candidate> foreign = candidates.Where(c => !c.IsPinnedBuild).ToList();
        if (foreign.Count > 0)
        {
            return Error(why + string.Join("; ", foreign.Select(c => $"'{c.Path}' ({c.Description})")) +
                         $", not ShadowDusk's pinned DXC '{pinnedMappedPath}' ({pinnedIdentity}): that library would be " +
                         "loaded and initialized inside the compile. " + fix +
                         " Release compiles never make this load and are unaffected.");
        }

        if (foundUnloadedFile && candidates.Count == 0)
        {
            return Error(why + $"a {leaf} on its search path that is not the loaded one (glibc found a file ShadowDusk " +
                         $"could not locate: ld.so.cache or a default library directory), not ShadowDusk's pinned DXC " +
                         $"'{pinnedMappedPath}' ({pinnedIdentity}). Run `ldconfig -p | grep dxcompiler` to find it. " +
                         fix + " Release compiles never make this load and are unaffected.");
        }

        return null;
    }

    /// <summary>
    /// The file paths dyld-940 and dyld-1042 (macOS 12 and 13, which have no already-loaded rule
    /// for leaf names) try for a leaf-name <c>dlopen</c> of <see cref="DxcLoader.MacLibFileName"/>,
    /// in order: <c>DYLD_LIBRARY_PATH</c>, the implicit <c>@rpath</c> expansion (the pinned
    /// dylib's one <c>LC_RPATH</c> is <c>@executable_path/../lib</c>), <c>/usr/lib</c>, the
    /// working directory, then <c>DYLD_FALLBACK_LIBRARY_PATH</c> or its defaults
    /// (<c>/usr/local/lib</c>, <c>/usr/lib</c>). Pure (no I/O).
    /// </summary>
    internal static IEnumerable<string> MacLeafNameSearchPaths(
        string? dyldLibraryPath, string? dyldFallbackLibraryPath, string workingDirectory, string? executableDirectory)
    {
        string leaf = DxcLoader.MacLibFileName;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<string> All()
        {
            foreach (string dir in ColonList(dyldLibraryPath))
                yield return Join(dir, leaf);
            if (executableDirectory is { Length: > 0 })
                yield return Join(executableDirectory, "../lib/" + leaf);
            yield return Join("/usr/lib", leaf);
            yield return Join(workingDirectory, leaf);
            foreach (string dir in dyldFallbackLibraryPath is { Length: > 0 } ? ColonList(dyldFallbackLibraryPath) : ["/usr/local/lib", "/usr/lib"])
                yield return Join(dir, leaf);
        }

        foreach (string path in All())
        {
            if (seen.Add(path))
                yield return path;
        }
    }

    /// <summary>
    /// The file paths glibc tries for a leaf-name <c>dlopen("libdxcompiler.so")</c> made from
    /// inside the pinned library that ShadowDusk can name: <c>LD_LIBRARY_PATH</c>, then the
    /// caller's <c>RUNPATH</c>, which is <c>$ORIGIN/../lib</c> in the pinned build. The
    /// remaining legs (<c>ld.so.cache</c>, <c>/lib</c>, <c>/usr/lib</c> and their multiarch
    /// forms) are covered by glibc's own <c>RTLD_NOLOAD</c> answer. Pure (no I/O).
    /// </summary>
    internal static IEnumerable<string> LinuxLeafNameSearchPaths(string? ldLibraryPath, string pinnedDirectory)
    {
        const string leaf = "libdxcompiler.so";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in ColonList(ldLibraryPath))
        {
            string path = Join(dir, leaf);
            if (seen.Add(path))
                yield return path;
        }

        string runpath = Join(pinnedDirectory, "../lib/" + leaf);
        if (seen.Add(runpath))
            yield return runpath;
    }

    private static IEnumerable<string> ColonList(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(':', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Joins with <c>/</c>: these are the dynamic linker's paths, the same on every host the tests run on.</summary>
    private static string Join(string directory, string relative) =>
        directory.Length == 0 || directory.EndsWith('/') ? directory + relative : directory + "/" + relative;

    /// <summary>
    /// True when both paths name the same file (the working-directory candidate is usually the
    /// pinned file itself when an app runs from its own directory): the full paths agree after
    /// symlinks are resolved. Hard links are left to the identity check.
    /// </summary>
    private static bool IsSameFile(string candidate, string pinned)
    {
        try
        {
            string a = Path.GetFullPath(new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate);
            string b = Path.GetFullPath(new FileInfo(pinned).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? pinned);
            return string.Equals(a, b, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ShaderError Error(string message) => new(
        File: "",
        Line: 0,
        Column: 0,
        Code: ErrorCode,
        Message: message);
}
