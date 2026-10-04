#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ShadowDusk.Core;

/// <summary>
/// One native library ShadowDusk ships pinned and loads only as that exact build: what it is
/// called, which files carry it, and the SHA-256 of every shipped file per RID.
/// </summary>
/// <param name="DisplayName">How a diagnostic names it, e.g. <c>vkd3d-shader 2.1</c>.</param>
/// <param name="ErrorCode">The registered diagnostic every refusal carries.</param>
/// <param name="Origin">Where the pinned files come from, for a diagnostic.</param>
/// <param name="Consequence">What cannot run without it, for a diagnostic.</param>
/// <param name="ProbeExport">An export of the library, used to ask dyld which image it really mapped.</param>
/// <param name="FileNames">The file names the library ships under on this OS, in probe order.</param>
/// <param name="Sha256ByRid">The lowercase-hex SHA-256 of each shipped file, keyed by RID.</param>
/// <param name="MismatchHint">The usual cause of a different build and its fix, for a diagnostic.</param>
/// <param name="NativePackage">
/// The NuGet package that carries the native when it is NOT the package of the assembly that
/// loads it (SPIRV-Cross ships in Silk.NET.SPIRV.Cross.Native, not ShadowDusk.GLSL), or
/// <c>null</c> when they are the same package. See <see cref="PinnedNativeLibrary.CandidatePaths"/>.
/// </param>
internal sealed record PinnedNative(
    string DisplayName,
    string ErrorCode,
    string Origin,
    string Consequence,
    string ProbeExport,
    IReadOnlyList<string> FileNames,
    IReadOnlyDictionary<string, string> Sha256ByRid,
    string MismatchHint,
    NuGetPackageIdentity? NativePackage = null);

/// <summary>A NuGet package id and exact version, as the global packages folder lays it out.</summary>
internal sealed record NuGetPackageIdentity(string Id, string Version);

/// <summary>
/// Loads a <see cref="PinnedNative"/> by ABSOLUTE PATH from the places ShadowDusk's packages
/// deliver it, after checking the file is the pinned build, and never by bare name (issue #350,
/// the vkd3d-shader and SPIRV-Cross counterpart of issue #270's DXC fix).
///
/// <para><b>Why never a bare name.</b> A bare-name load is the operating system's search:
/// <c>PATH</c> (and the executable's directory, which for <c>dotnet app.dll</c> is the .NET
/// install) on Windows, <c>LD_LIBRARY_PATH</c> and the system library directories on Linux,
/// <c>DYLD_LIBRARY_PATH</c>, <c>DYLD_FALLBACK_LIBRARY_PATH</c> and the working directory on
/// macOS. A <c>libvkd3d-shader</c> or <c>spirv-cross</c> sitting there (Wine and the Vulkan SDK
/// both ship one) was loaded in place of the pinned build whenever the app-local probe missed,
/// which it always did inside a plugin host such as MGCB: a different compiler, silently, or a
/// planted library executing inside the process.</para>
///
/// <para><b>Where it looks</b> (<see cref="CandidatePaths"/>): beside the ShadowDusk assemblies
/// (a plugin host's only usable anchor), the application's base directory, and the host's native
/// search directories (<c>NATIVE_DLL_SEARCH_DIRECTORIES</c>: the NuGet cache for a
/// framework-dependent app, the extraction directory of a single-file bundle), each as
/// <c>runtimes/&lt;rid&gt;/native</c>, a per-RID subdirectory, and flat (a RID-specific or
/// self-contained publish).</para>
///
/// <para><b>Only the pinned build.</b> A file with the right name is only a candidate: its
/// SHA-256 must be the pin for this process's RID BEFORE it is loaded. A copy of another
/// architecture's pinned file is skipped quietly (it is reported only when nothing loads); any
/// other file is skipped and named in the diagnostic. On macOS dyld resolves even an
/// absolute-path load against <c>DYLD_LIBRARY_PATH</c> by leaf name first, so the image dyld
/// really mapped is checked after the load as well, the same check <c>DxcLoader</c> makes.</para>
///
/// <para>If nothing qualifies the result is an error carrying <see cref="PinnedNative.ErrorCode"/>:
/// ShadowDusk never compiles with a different build.</para>
/// </summary>
internal static class PinnedNativeLibrary
{
    /// <summary>The outcome of <see cref="Load"/>: a handle and the file it came from, or the refusal.</summary>
    internal sealed record LoadResult(IntPtr Handle, string? Path, ShaderError? Error);

    /// <summary>
    /// A load that is cached once it SUCCEEDS and retried on the next request while it fails.
    /// A failure can be transient (a file another process briefly holds open, an antivirus scan
    /// mid-copy), and caching it would refuse every compile for the life of the process; a
    /// success is final, because the handle the runtime binds P/Invokes to cannot change.
    /// </summary>
    internal sealed class RetryableLoad
    {
        private readonly Func<LoadResult> _load;
        private readonly object _gate = new();
        private volatile LoadResult? _succeeded;

        internal RetryableLoad(Func<LoadResult> load) => _load = load;

        /// <summary>The cached success, or a fresh attempt (cached only if it succeeds).</summary>
        internal LoadResult Value
        {
            get
            {
                if (_succeeded is { } done) return done;
                lock (_gate)
                {
                    if (_succeeded is { } raced) return raced;
                    LoadResult result = _load();
                    if (result.Error is null)
                        _succeeded = result;
                    return result;
                }
            }
        }

        /// <summary>The successful load, or <c>null</c> while none has succeeded.</summary>
        internal LoadResult? Succeeded => _succeeded;
    }

    /// <summary>
    /// Finds, verifies and loads <paramref name="native"/> for this process. Windows, Linux and
    /// macOS only: the caller decides what Android and other operating systems get.
    /// </summary>
    /// <param name="native">The library.</param>
    /// <param name="assemblies">The assemblies whose directories ship it (the P/Invoking assembly first).</param>
    /// <param name="extraCandidates">Absolute paths probed after every package location (a repo checkout's restored copy).</param>
    internal static LoadResult Load(
        PinnedNative native, IEnumerable<Assembly> assemblies, IEnumerable<string> extraCandidates)
    {
        string rid = CurrentRid();
        if (!native.Sha256ByRid.TryGetValue(rid, out string? pinned))
        {
            return Fail(native,
                $"ShadowDusk ships no {native.DisplayName} for '{rid}' (it ships it for " +
                $"{string.Join(", ", native.Sha256ByRid.Keys.Order(StringComparer.Ordinal))}), so {native.Consequence}. " +
                "It will not load one found elsewhere on the machine, which would be a different build.");
        }

        bool ignoreCase = OperatingSystem.IsWindows();
        List<string> candidates = CandidatePaths(
                AppContext.BaseDirectory,
                AssemblyDirectories(assemblies),
                NativeSearchDirectories(),
                rid,
                native.FileNames,
                ignoreCase,
                native.NativePackage)
            .Concat(extraCandidates)
            .ToList();

        var rejected = new List<string>();
        var otherArchitecture = new List<string>();
        var unloadable = new List<string>();
        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;

            string? hash = Sha256OfFile(path);
            if (hash != pinned)
            {
                string? otherRid = hash is null
                    ? null
                    : native.Sha256ByRid.FirstOrDefault(p => p.Value == hash).Key;
                if (otherRid is not null)
                    otherArchitecture.Add($"'{path}' (the {otherRid} build)");
                else
                    rejected.Add($"'{path}' ({(hash is null ? "unreadable" : "SHA-256 " + hash)})");
                continue;
            }

            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                unloadable.Add($"'{path}': {ex.Message}");
                continue;
            }

            bool exported = NativeLibrary.TryGetExport(handle, native.ProbeExport, out _);

            // macOS: dyld may have mapped a DYLD_LIBRARY_PATH file of the same leaf name instead
            // (one that may not even be this library, so a missing export is the same finding).
            if (OperatingSystem.IsMacOS())
            {
                string? mapped = exported ? MacImagePathOf(handle, native.ProbeExport) : null;
                if (!exported
                    || (mapped is not null && !SamePath(mapped, path, ignoreCase: false) && Sha256OfFile(mapped) != pinned))
                {
                    string what = mapped is null
                        ? $"a different library (one without {native.ProbeExport})"
                        : $"'{mapped}'";
                    return Fail(native,
                        $"ShadowDusk loaded its pinned {native.DisplayName} '{path}', but dyld mapped {what} " +
                        "instead: DYLD_LIBRARY_PATH names a directory holding a different library of that name, " +
                        "and dyld searches it ahead of any path it is given. ShadowDusk will not run a different " +
                        $"build, so {native.Consequence}. Remove that library from DYLD_LIBRARY_PATH.");
                }
            }
            else if (!exported)
            {
                return Fail(native,
                    $"ShadowDusk loaded its pinned {native.DisplayName} '{path}', but the image the dynamic " +
                    $"linker mapped for it does not export {native.ProbeExport}, so it is not that build and " +
                    $"{native.Consequence}.");
            }

            return new LoadResult(handle, path, null);
        }

        string pinnedText = $"{native.FileNames[0]}, SHA-256 {pinned}, from {native.Origin}";
        string searched = " Searched: " + string.Join("; ",
            candidates.Select(System.IO.Path.GetDirectoryName).Distinct(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) + ".";
        string never = " ShadowDusk never loads it by bare name from PATH, LD_LIBRARY_PATH or " +
                       "DYLD_LIBRARY_PATH, where a different build can sit.";
        string others = otherArchitecture.Count == 0
            ? ""
            : " Pinned files for another architecture (cannot load into this " +
              $"{RuntimeInformation.ProcessArchitecture} process): {string.Join("; ", otherArchitecture)}.";
        string failed = unloadable.Count == 0
            ? ""
            : " Pinned files that could not be loaded: " + string.Join("; ", unloadable) + ".";

        if (rejected.Count > 0)
        {
            return Fail(native,
                $"ShadowDusk found {native.DisplayName} libraries, but not its pinned build ({pinnedText}), " +
                $"so {native.Consequence}: it will not run a different build. Found instead: " +
                string.Join("; ", rejected) + "." + others + failed + " " + native.MismatchHint + searched);
        }

        if (unloadable.Count > 0)
        {
            return Fail(native,
                $"ShadowDusk's pinned {native.DisplayName} ({pinnedText}) was found but could not be loaded " +
                $"into this {RuntimeInformation.ProcessArchitecture} process, so {native.Consequence}." +
                failed + others + searched);
        }

        return Fail(native,
            $"ShadowDusk's pinned {native.DisplayName} native library ({pinnedText}) was not found " +
            "beside the ShadowDusk assemblies, in the application directory or in the host's native " +
            $"search directories, so {native.Consequence}.{never}{others}{searched}");
    }

    /// <summary>
    /// The ordered absolute paths probed for the files <paramref name="fileNames"/>. Pure (no
    /// I/O), so the order is unit-testable:
    /// <list type="number">
    /// <item>beside each of <paramref name="assemblyDirectories"/>: <c>runtimes/&lt;rid&gt;/native</c>
    ///   (a framework-dependent app, a dotnet tool, a plugin directory), <c>&lt;rid&gt;/</c> (the
    ///   per-arch macOS build-output layout, whose arches share one file name), the directory
    ///   itself (a RID-specific or self-contained publish), and, when the assembly was loaded
    ///   straight from a NuGet package's <c>lib/&lt;tfm&gt;/</c> (<c>dotnet fsi</c> and .NET
    ///   Interactive notebooks load packages in place from the global packages folder), that
    ///   package's own <c>runtimes/&lt;rid&gt;/native</c> and, when the native ships in another
    ///   package (<paramref name="nativePackage"/>), that package's
    ///   <c>&lt;packages&gt;/&lt;id&gt;/&lt;version&gt;/runtimes/&lt;rid&gt;/native</c> in the same
    ///   folder (ids are lowercase there);</item>
    /// <item>the same three under <paramref name="baseDirectory"/>;</item>
    /// <item>each host native search directory, as <c>&lt;rid&gt;/</c> (a single-file bundle's
    ///   extraction keeps the per-arch subdirectory) and flat (the NuGet cache's
    ///   <c>runtimes/&lt;rid&gt;/native</c>).</item>
    /// </list>
    /// Never a bare name.
    /// </summary>
    internal static IEnumerable<string> CandidatePaths(
        string baseDirectory,
        IEnumerable<string> assemblyDirectories,
        IEnumerable<string> nativeSearchDirectories,
        string rid,
        IReadOnlyList<string> fileNames,
        bool ignoreCase,
        NuGetPackageIdentity? nativePackage = null)
    {
        IEnumerable<string> Directories()
        {
            foreach (string dir in assemblyDirectories)
            {
                yield return System.IO.Path.Combine(dir, "runtimes", rid, "native");
                yield return System.IO.Path.Combine(dir, rid);
                yield return dir;
                if (PackageRootOf(dir) is { } package)
                {
                    yield return System.IO.Path.Combine(package, "runtimes", rid, "native");

                    // <packages>/<id>/<version>/lib/<tfm>: the native's own package sits beside it.
                    string? packagesFolder = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(package));
                    if (nativePackage is not null && packagesFolder is not null)
                    {
                        yield return System.IO.Path.Combine(
                            packagesFolder, nativePackage.Id.ToLowerInvariant(),
                            nativePackage.Version.ToLowerInvariant(), "runtimes", rid, "native");
                    }
                }
            }

            yield return System.IO.Path.Combine(baseDirectory, "runtimes", rid, "native");
            yield return System.IO.Path.Combine(baseDirectory, rid);
            yield return baseDirectory;

            foreach (string dir in nativeSearchDirectories)
            {
                yield return System.IO.Path.Combine(dir, rid);
                yield return dir;
            }
        }

        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string dir in Directories())
        {
            string normalized = System.IO.Path.TrimEndingDirectorySeparator(dir);
            if (normalized.Length == 0 || !seen.Add(normalized))
                continue;

            foreach (string fileName in fileNames)
                yield return System.IO.Path.Combine(normalized, fileName);
        }
    }

    /// <summary>
    /// The RID naming the pinned file for the running process. <c>ProcessArchitecture</c>, never
    /// the OS's: under Rosetta 2 (or x64 emulation on Windows on Arm) only the process's own
    /// architecture can load.
    /// </summary>
    internal static string CurrentRid() => MapRid(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        OperatingSystem.IsAndroid(),
        RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Pure RID mapping (no <see cref="RuntimeInformation"/>), so it is unit-testable. If more than
    /// one OS flag is ever set the order is Windows, macOS, Android, then Linux.
    /// </summary>
    internal static string MapRid(bool isWindows, bool isOsx, bool isAndroid, Architecture arch)
    {
        string os = isWindows ? "win" : isOsx ? "osx" : isAndroid ? "android" : "linux";
        string cpu = arch switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return os + "-" + cpu;
    }

    /// <summary>The lowercase-hex SHA-256 of a file, or <c>null</c> if it cannot be read.</summary>
    internal static string? Sha256OfFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static LoadResult Fail(PinnedNative native, string message) =>
        new(IntPtr.Zero, null, new ShaderError(File: "", Line: 0, Column: 0, Code: native.ErrorCode, Message: message));

    private static bool SamePath(string a, string b, bool ignoreCase) =>
        string.Equals(
            System.IO.Path.GetFullPath(a),
            System.IO.Path.GetFullPath(b),
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The package root when <paramref name="assemblyDirectory"/> is a NuGet package's
    /// <c>lib/&lt;tfm&gt;</c> folder, else <c>null</c>.
    /// </summary>
    private static string? PackageRootOf(string assemblyDirectory)
    {
        string? lib = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(assemblyDirectory));
        return lib is not null && System.IO.Path.GetFileName(lib).Equals("lib", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetDirectoryName(lib)
            : null;
    }

    private static IEnumerable<string> AssemblyDirectories(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            // Empty for single-file bundles (no location); the search directories cover those.
#pragma warning disable IL3000
            string location = assembly.Location;
#pragma warning restore IL3000
            string? directory = location.Length == 0 ? null : System.IO.Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(directory))
                yield return directory;
        }
    }

    private static string[] NativeSearchDirectories() =>
        // Set by the host from deps.json (each package's runtimes/<rid>/native, the app directory).
        AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];

    /// <summary>
    /// macOS: the path dyld recorded for the image that defines <paramref name="exportName"/> in
    /// the library <paramref name="handle"/> names (<c>dladdr</c>), or <c>null</c> when dyld
    /// cannot be asked (never a guess).
    /// </summary>
    internal static string? MacImagePathOf(IntPtr handle, string exportName)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero)
            return null;

        try
        {
            return NativeLibrary.TryGetExport(handle, exportName, out IntPtr address)
                && dladdr(address, out DlInfo info) != 0
                && Marshal.PtrToStringUTF8(info.FileName) is { Length: > 0 } path
                    ? path
                    : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary><c>Dl_info</c> from <c>&lt;dlfcn.h&gt;</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr FileBase;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int dladdr(IntPtr address, out DlInfo info);
}
