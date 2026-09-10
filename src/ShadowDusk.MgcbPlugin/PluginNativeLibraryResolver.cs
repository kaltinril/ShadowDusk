#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ShadowDusk.MgcbPlugin;

/// <summary>
/// Makes ShadowDusk's bundled native compilers resolvable when this assembly is loaded as an
/// MGCB plugin.
///
/// <para><b>Why this is needed at all.</b> MGCB loads a <c>/reference:</c>d plugin with
/// <c>Assembly.LoadFrom</c> into MGCB's own process. Every native-resolution mechanism
/// ShadowDusk's loaders use is anchored to the <i>host process</i>, not to us:
/// <c>AppContext.BaseDirectory</c> is MGCB's directory, and
/// <c>NATIVE_DLL_SEARCH_DIRECTORIES</c> comes from MGCB's <c>deps.json</c>, which knows
/// nothing about our packages. So DXC, SPIRV-Cross and vkd3d-shader sit right beside this
/// DLL and are never found. (Measured: without this shim a real <c>dotnet mgcb</c> build
/// fails with <c>SD0103 SPIRV-Cross native library not found</c>.)</para>
///
/// <para>Two hooks, because DXC needs an earlier one than the other two natives
/// (Phase 63, issue #203 - measured on a real <c>dotnet-mgcb</c> 3.8.5 build):</para>
/// <list type="number">
/// <item><see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/>, which the runtime raises only
///   <i>after</i> every other mechanism has failed, probes the plugin's own install directory
///   for <c>spirv-cross</c> and <c>vkd3d-shader</c>. It cannot displace a native the existing
///   loaders resolved, and it changes nothing outside an MGCB host.</item>
/// <item><c>Vortice.Dxc.Dxc.ResolveLibrary</c> for <c>dxcompiler.dll</c>. Vortice's own
///   resolver probes <c>AppContext.BaseDirectory/runtimes/&lt;rid&gt;/native</c> (MGCB's
///   directory - a miss), consults this event, and only then falls back to a <b>bare-name</b>
///   load, which walks the OS <c>PATH</c>. That fallback is a substitute-compiler hole: a
///   developer box with the Vulkan SDK on <c>PATH</c> carries its own <c>dxcompiler.dll</c>,
///   and every MGCB build through the plugin silently compiled with <i>that</i> DXC (the
///   DirectX 12 bytes differed from the CLI's; the GL/Vulkan SPIR-V happened to match for the
///   corpus). And even when the pinned DXC did load through hook 1, DXC's own internal
///   <c>LoadLibrary("dxil.dll")</c> is not a .NET load and never reached it, so DirectX 12
///   output came out <b>unsigned</b> (DXC warns "DXIL signing library not found"; retail
///   D3D12 rejects the artifact). This hook pre-loads the plugin directory's <c>dxil.dll</c>
///   and returns its <c>dxcompiler.dll</c>, exactly what Vortice does from an ordinary app's
///   base directory - the pinned pair, never a substitute.</item>
/// </list>
///
/// <para>Both load <b>the same pinned natives</b> the CLI and the runtime library use, so the
/// compiler is identical: lookup paths, never a substitute compiler. The probe is deliberately
/// narrow: only the library names ShadowDusk P/Invokes, and only inside this assembly's own
/// directory. It never widens the process's search path and never resolves a request on
/// another component's behalf. <c>validation/MgcbPlugin</c> proves both hooks: it puts a decoy
/// <c>dxcompiler.dll</c> first on the child MGCB's <c>PATH</c> (a bare-name load would take
/// it and die at <c>DxcCreateInstance</c>) and requires the DirectX 12 payload to equal the
/// CLI's signed bytes.</para>
/// </summary>
internal static class PluginNativeLibraryResolver
{
    /// <summary>
    /// The only library names this resolver will answer for - exactly the module names
    /// ShadowDusk's <c>DllImport</c>s declare. Anything else is somebody else's problem and
    /// is left to the runtime.
    /// </summary>
    private static readonly string[] KnownLibraryNames =
    [
        "spirv-cross",       // ShadowDusk.GLSL.Interop.SpvcNative.LibName
        "vkd3d-shader-1",    // ShadowDusk.HLSL.Vkd3d.Vkd3dNative.LibName
        "dxcompiler.dll",    // Vortice.Dxc's module name on every OS
        "dxcompiler",
        "dxil",
    ];

    /// <summary>The module name Vortice.Dxc's P/Invokes declare on every OS (<c>DxcLoader.DxcLibraryName</c>).</summary>
    private const string DxcModuleName = "dxcompiler.dll";

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// Idempotently subscribes the fallback resolver. Called from the static constructors of
    /// <see cref="ShadowDuskEffectImporter"/> and <see cref="ShadowDuskEffectProcessor"/> - the
    /// only two entry points MGCB has into this assembly - so it always runs before the first
    /// P/Invoke. (A <c>[ModuleInitializer]</c> would be the obvious alternative, but CA2255
    /// forbids it in library code, and the static constructors are the same guarantee stated
    /// where a reader will look for it.)
    /// </summary>
    internal static void Register()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += Resolve;
            // Touching the event runs Vortice's Dxc static ctor first (its own resolver), so
            // this subscriber is consulted after Vortice's base-directory probe and BEFORE its
            // bare-name fallback. Subscribers are polled in order, first non-zero wins, and
            // ours returns zero whenever the plugin directory holds no DXC - so it can never
            // shadow ShadowDusk's own macOS/Android DxcLoader handler.
            Vortice.Dxc.Dxc.ResolveLibrary += ResolveDxc;
            _registered = true;
        }
    }

    /// <summary>
    /// The <c>Vortice.Dxc.Dxc.ResolveLibrary</c> handler: the plugin directory's pinned
    /// <c>dxcompiler.dll</c>, with its sibling <c>dxil.dll</c> loaded FIRST so DXC's internal
    /// <c>LoadLibrary("dxil.dll")</c> (which never consults .NET) finds the already-loaded
    /// module by name and validates + signs DirectX 12 output. Zero for anything else, and
    /// zero when the plugin directory holds no DXC, which hands resolution back to Vortice.
    /// </summary>
    private static IntPtr ResolveDxc(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DxcModuleName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string? pluginDirectory = GetPluginDirectory();
        if (pluginDirectory is null)
            return IntPtr.Zero;

        string rid = CurrentRid();

        // dxil.dll first. It ships only for the win-* RIDs; where it is absent (macOS, Linux
        // DXC builds carry no signer) nothing is loaded and DXC itself reports unsigned output.
        foreach (string candidate in GetProbeCandidates(pluginDirectory, rid, FileNamesFor("dxil")))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
                break;
        }

        foreach (string candidate in GetProbeCandidates(pluginDirectory, rid, FileNamesFor("dxcompiler")))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IntPtr Resolve(Assembly requesting, string libraryName)
    {
        if (!KnownLibraryNames.Contains(libraryName, StringComparer.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string? pluginDirectory = GetPluginDirectory();
        if (pluginDirectory is null)
            return IntPtr.Zero;

        foreach (string candidate in GetProbeCandidates(
                     pluginDirectory, CurrentRid(), FileNamesFor(libraryName)))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The directory this assembly was loaded from. Null in the (theoretical for a plugin)
    /// single-file/in-memory case, where <c>Assembly.Location</c> is empty and there is
    /// nothing beside us to probe.
    /// </summary>
    private static string? GetPluginDirectory()
    {
        string location = typeof(PluginNativeLibraryResolver).Assembly.Location;
        if (location.Length == 0)
            return null;

        string? directory = Path.GetDirectoryName(location);
        return string.IsNullOrEmpty(directory) ? null : directory;
    }

    /// <summary>
    /// The ordered candidate paths, relative to the plugin directory. Pure (no I/O), matching
    /// the convention of ShadowDusk's other native loaders.
    /// <list type="number">
    /// <item>the NuGet <c>runtimes/&lt;rid&gt;/native</c> layout (how DXC and SPIRV-Cross arrive),</item>
    /// <item>a per-arch subdirectory (the macOS DXC/vkd3d layout, where both arches share a file name),</item>
    /// <item>flat beside the plugin (how vkd3d-shader arrives on Windows and Linux).</item>
    /// </list>
    /// </summary>
    private static IEnumerable<string> GetProbeCandidates(
        string pluginDirectory, string rid, IReadOnlyList<string> fileNames)
    {
        foreach (string fileName in fileNames)
            yield return Path.Combine(pluginDirectory, "runtimes", rid, "native", fileName);

        foreach (string fileName in fileNames)
            yield return Path.Combine(pluginDirectory, rid, fileName);

        foreach (string fileName in fileNames)
            yield return Path.Combine(pluginDirectory, fileName);
    }

    /// <summary>
    /// The concrete file names a module name can appear under, in probe order: the name
    /// verbatim (Vortice already asks for <c>dxcompiler.dll</c>), then the platform's
    /// decorated spellings, then vkd3d's versioned SONAMEs. Pure (no I/O).
    /// </summary>
    private static IReadOnlyList<string> FileNamesFor(string libraryName) =>
        FileNamesFor(
            libraryName,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

    /// <inheritdoc cref="FileNamesFor(string)"/>
    private static IReadOnlyList<string> FileNamesFor(string libraryName, bool isWindows, bool isOsx)
    {
        var names = new List<string> { libraryName };

        void Add(string name)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        if (isWindows)
        {
            Add(libraryName + ".dll");
            // vkd3d ships as libvkd3d-shader-1.dll; the P/Invoke module name is vkd3d-shader-1.
            Add("lib" + libraryName + ".dll");
        }
        else if (isOsx)
        {
            Add("lib" + libraryName + ".dylib");
            Add(libraryName + ".dylib");
            // libvkd3d-shader-1 -> libvkd3d-shader.1.dylib (the versioned install name).
            if (libraryName.EndsWith("-1", StringComparison.Ordinal))
                Add("lib" + libraryName[..^2] + ".1.dylib");
        }
        else
        {
            Add("lib" + libraryName + ".so");
            Add(libraryName + ".so");
            // libvkd3d-shader-1 -> libvkd3d-shader.so.1 (the versioned SONAME).
            if (libraryName.EndsWith("-1", StringComparison.Ordinal))
                Add("lib" + libraryName[..^2] + ".so.1");
        }

        return names;
    }

    /// <summary>
    /// The RID naming the <c>runtimes/&lt;rid&gt;/native</c> subdirectory to probe. Keyed on
    /// <c>ProcessArchitecture</c>, never <c>OSArchitecture</c>: under Rosetta 2 the OS reports
    /// Arm64 while only x64 binaries can load into the process.
    /// </summary>
    private static string CurrentRid() => MapRid(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        RuntimeInformation.ProcessArchitecture);

    /// <inheritdoc cref="CurrentRid"/>
    private static string MapRid(bool isWindows, bool isOsx, Architecture arch) =>
        (isWindows, isOsx, arch) switch
        {
            (true, _, Architecture.Arm64) => "win-arm64",
            (true, _, _)                  => "win-x64",
            (_, true, Architecture.Arm64) => "osx-arm64",
            (_, true, _)                  => "osx-x64",
            (_, _, Architecture.Arm64)    => "linux-arm64",
            (_, _, Architecture.Arm)      => "linux-arm",
            _                             => "linux-x64",
        };
}
