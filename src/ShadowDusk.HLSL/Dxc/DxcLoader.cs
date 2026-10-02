#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Makes DXC run as ShadowDusk's own pinned build, and only as that build: commit
/// <c>e043f4a1286f4e1026222ab1bc94e25de8d0e959</c>, FileVersion 1.7.2212.40 (the same pin as
/// the DXC-&gt;WASM build), so SPIR-V stays byte-identical across RIDs. The natives come from
/// the Vortice.Dxc package on Windows and Linux; Vortice.Dxc 3.3.4 ships no macOS or Android
/// native (the Phase 37 Finding A product gap), so there we load OUR OWN build of the same
/// commit, never a substitute compiler.
///
/// <para><b>Why Vortice's event and not <c>SetDllImportResolver</c>.</b> The
/// <c>dxcompiler.dll</c> P/Invokes live in the <b>Vortice.Dxc</b> assembly, whose <c>Dxc</c>
/// static constructor already calls <c>NativeLibrary.SetDllImportResolver</c> on it; a second
/// call there throws <see cref="InvalidOperationException"/>. Vortice instead exposes the public
/// <c>Dxc.ResolveLibrary</c> event, which its resolver polls (first non-zero answer wins) BEFORE
/// falling back to default loading.</para>
///
/// <para><b>Our resolver runs first, on every OS (issue #270).</b> Vortice's static constructor
/// adds its own handler to that event, so a plain <c>+=</c> would poll it before ours. Off
/// Windows that handler finds no <c>base\runtimes\win-*\native</c> directory (the path is
/// spelled with literal backslashes) and falls to
/// <c>NativeLibrary.TryLoad("dxil") &amp;&amp; NativeLibrary.TryLoad("dxcompiler")</c>, two
/// BARE-NAME loads, and returns that handle. Bare names resolve through the dynamic linker's
/// search path: <c>LD_LIBRARY_PATH</c> on Linux; <c>DYLD_LIBRARY_PATH</c>, the working directory
/// and <c>/usr/local/lib</c> on macOS. So a <c>libdxil</c> + <c>libdxcompiler</c> pair reachable
/// by name was loaded INSTEAD of ours, and OpenGL and Vulkan compiled with a substitute DXC
/// without a word (macOS did this through PR #268, which moved only Windows and Linux to the
/// front). <see cref="Register"/> therefore puts its handler at the FRONT of the invocation
/// list (the event's backing delegate, by reflection; plain <c>+=</c> if that field ever moves,
/// which <c>DxcLoaderTests</c> pins) and answers with a handle it loaded itself by absolute
/// path, so Vortice's handler never runs.</para>
///
/// <para><b>Windows, Linux and macOS: the pinned natives, by absolute path, before anything
/// else.</b> Loading is not left to name-based search. <c>dxcompiler.dll</c> binds its DXIL
/// validator/signer from its own <c>DllMain</c> with a bare <c>LoadLibrary("dxil.dll")</c>,
/// and a bare name resolves to an already-loaded module of that name first, then the
/// application directory, the system directories and finally <c>PATH</c>. ShadowDusk used to
/// call Vortice's <c>Dxc.LoadDxil()</c> (itself a bare <c>LoadLibrary("dxil.dll")</c>) before
/// the first DXC P/Invoke; in the normal layout our <c>dxil.dll</c> sits in
/// <c>runtimes/&lt;rid&gt;/native</c>, not the application directory, so that call loaded
/// whichever <c>dxil.dll</c> the OS search found on <c>PATH</c> (every VS Developer Command
/// Prompt puts the Windows SDK's 1.8 one there) and DXC then validated with it: every
/// DirectX 12 compile failed with "DXIL container mismatch for 'PSVRuntimeInfoSize'", and a
/// loadable non-validator decoy silently produced UNSIGNED DXIL. <see cref="Register"/> now
/// locates the pinned natives, loads <c>dxil.dll</c> then <c>dxcompiler.dll</c> by full path
/// (Linux: <c>libdxcompiler.so</c>; macOS: <c>libdxcompiler.dylib</c>) and verifies that the
/// <c>dxil.dll</c> a bare-name lookup returns is the pinned build.</para>
///
/// <para><b>Only the pinned build is ever loaded (issue #270).</b> A directory that holds a
/// file with the right NAME is not enough: the host application's own directories can carry a
/// different DXC (MonoGame's tools ship 1.8; the Windows SDK's 1.8 pair has no SPIR-V
/// backend). Measured through PR #268: a foreign pair in
/// <c>&lt;host&gt;\runtimes\win-x64\native</c> won over ShadowDusk's own beside its assemblies,
/// DirectX 12 compiled with it silently and OpenGL failed with the foreign DXC's "SPIR-V
/// CodeGen not available". So every candidate is checked against the pinned build identity
/// (<see cref="DxcNativeIdentity"/>) BEFORE it is loaded, a candidate that is not the pinned
/// build is skipped and named in the diagnostic, and the directories beside the ShadowDusk and
/// Vortice assemblies are probed ahead of the host's. If no candidate is the pinned build,
/// every DXC request fails with <see cref="LoadErrorCode"/>: ShadowDusk never compiles with a
/// different DXC.</para>
///
/// <para><b>A foreign DXIL validator refuses DirectX 12 only.</b> If a <c>dxil.dll</c> of
/// another build was loaded into the process first (a host tool that uses DXC itself), DXC
/// binds that one. Only requests whose output the validator decides (validated DXIL:
/// DirectX 12) are refused; SPIR-V, <c>-Vd</c> and preprocess requests never call the validator
/// and proceed (<see cref="CheckBoundValidator"/>). A copy of the pinned <c>dxil.dll</c> from
/// another directory, or the pinned file reached through a <c>\\?\</c> path, is the same build
/// and is accepted. Vortice's Linux <c>libdxcompiler.so</c> never opens a <c>libdxil</c>, so
/// Linux has no validator to bind and ShadowDusk loads none. Our macOS and Android builds do:
/// DXC's library constructor (<c>DllMain</c> -&gt; <c>InitMaybeFail</c> -&gt;
/// <c>DxilLibInitialize</c>, <c>tools/clang/tools/dxcompiler</c> at the pinned commit) calls
/// <c>dlopen("libdxil.dylib")</c> by leaf name ONCE, while <c>libdxcompiler</c> itself is being
/// loaded, and <c>DxilLibIsEnabled</c> never retries after a miss. We ship no <c>libdxil</c>
/// and cannot pre-empt a library we do not ship, so on macOS <see cref="Register"/> checks,
/// right after its own load, whether any <c>libdxil</c> image is in the process and fails
/// DirectX 12 loudly if so. Because that one <c>dlopen</c> happens inside
/// <see cref="Register"/>, nothing DXC does later loads a library; in particular nothing inside
/// the fork-gated <c>IDxcCompiler3::Compile</c> does (see <see cref="DxcForkGate"/>).</para>
///
/// <para><b>Where the natives are found.</b> Windows and Linux:
/// <see cref="GetPinnedPairDirectories"/>. macOS: <see cref="GetMacCandidates"/>; the dylib
/// ships packed into the ShadowDusk.HLSL NuGet under <c>runtimes/osx-{x64,arm64}/native</c> and
/// as a restored artifact under <c>tools/dxc/osx-{x64,arm64}/</c> for repo builds (see
/// tools/restore.ps1); both arches share one file name, so the copied layout is per-arch,
/// exactly like vkd3d's. Android (Phase 50): our <c>libdxcompiler.so</c> rides in the APK's
/// per-ABI <c>lib/&lt;abi&gt;/</c> dir and is loaded by bare SONAME, never by path (Android
/// W^X, and the APK is not a directory). The Android linker resolves an app's libraries only
/// inside the app's own namespace, so no search path exists for a foreign build to sit on, and
/// there is no file to read an identity from.</para>
///
/// <para>Known residual: if the host process drove Vortice.Dxc before ShadowDusk's first use,
/// the runtime has already cached Vortice's P/Invoke binding to whatever <c>dxcompiler</c> the
/// host loaded, and no resolver runs again.</para>
/// </summary>
internal static class DxcLoader
{
    /// <summary>The module name Vortice.Dxc's P/Invokes declare on every OS.</summary>
    internal const string DxcLibraryName = "dxcompiler.dll";

    /// <summary>The file name our macOS DXC build ships under (both arches).</summary>
    internal const string MacLibFileName = "libdxcompiler.dylib";

    /// <summary>
    /// The file name our Android DXC build ships under (Phase 50). It rides in the APK's
    /// per-ABI <c>lib/&lt;abi&gt;/</c> dir and the Android dynamic linker resolves it by this
    /// SONAME — a bare-name load, never the desktop path-probing (which would violate
    /// Android W^X and target the sandboxed app-data dir, not the read-only APK).
    /// </summary>
    internal const string AndroidLibFileName = "libdxcompiler.so";

    /// <summary>
    /// The diagnostic raised when ShadowDusk cannot guarantee DXC runs with its own pinned
    /// natives: they are missing, unloadable or only present as a different build, or a foreign
    /// DXIL validator (<c>dxil.dll</c> / <c>libdxil</c>) is the one DXC binds.
    /// </summary>
    internal const string LoadErrorCode = "SD0219";

    private static readonly object RegisterGate = new();
    private static volatile bool _registered;
    private static ShaderError? _loadError;
    private static ShaderError? _foreignValidatorError;
    private static IntPtr _pinnedDxcHandle;

    /// <summary>
    /// Idempotently makes DXC resolvable as ShadowDusk's own pinned build. Must run before the
    /// first DXC P/Invoke (<see cref="DxcShaderCompiler"/>'s constructor and
    /// <c>DxilReflectionExtractor</c> call it first thing).
    /// <list type="bullet">
    /// <item>Windows/Linux/macOS: loads the pinned natives by absolute path, after checking they
    ///   are the pinned build (Windows: <c>dxil</c> first, so <c>dxcompiler</c>'s own bare-name
    ///   validator load finds it), and answers <c>Dxc.ResolveLibrary</c> with that handle.</item>
    /// <item>Android: loads our <c>libdxcompiler.so</c> from the APK by bare SONAME, here, once.</item>
    /// <item>Any other OS: no DXC ships for it, so the result is <see cref="LoadErrorCode"/>.</item>
    /// </list>
    /// Either way the handler goes ahead of Vortice's own. The native load happens HERE, so it
    /// is always outside <see cref="DxcForkGate"/>, which wraps only the later compile call.
    /// A lock (not a lone CAS) so a concurrent second caller BLOCKS until the
    /// winner has finished — with CAS-then-subscribe the loser could
    /// return and P/Invoke before the resolver existed (observed once as an
    /// intermittent macOS <c>DllNotFoundException</c> under test parallelism).
    /// </summary>
    /// <returns>
    /// <c>null</c> when DXC may be used; otherwise a <see cref="LoadErrorCode"/> error the
    /// caller must surface instead of compiling. The outcome is computed once per process.
    /// </returns>
    public static ShaderError? Register()
    {
        if (_registered) return _loadError;
        lock (RegisterGate)
        {
            if (_registered) return _loadError;

            // Every outcome is either a handle ShadowDusk loaded itself or an SD0219 Result:
            // never "fall through and let Vortice's bare-name fallback try", which on a missing
            // or unsupported native ends in a raw DllNotFoundException at the first P/Invoke.
            _loadError =
                OperatingSystem.IsAndroid() ? LoadAndroid()
                : OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? LoadPinned()
                : LoadError(
                    $"ShadowDusk bundles no DXC for this operating system ({RuntimeInformation.OSDescription}, " +
                    $"{RuntimeInformation.RuntimeIdentifier}), so no DXC-backed compile (DirectX 12, OpenGL, " +
                    "Vulkan) can run here. It ships its pinned DXC for Windows, Linux, macOS and Android " +
                    "only, and will not use a DXC found elsewhere. DirectX 11 and FNA do not use DXC.");
            if (_loadError is null)
                SubscribeFirst(ResolvePinned);

            _registered = true;
            return _loadError;
        }
    }

    /// <summary>
    /// The error to return instead of running a compile whose output the DXIL validator decides
    /// (validated DXIL), or <c>null</c>. SPIR-V, <c>-Vd</c> and preprocess requests never reach
    /// the validator, so callers ask only for validated DXIL. DXC binds its validator exactly
    /// once, while its own library loads (Windows <c>DllMain</c>, Unix library constructor), so
    /// <see cref="Register"/> has already decided this: nothing a compile does can change it.
    /// </summary>
    internal static ShaderError? CheckBoundValidator() => _foreignValidatorError;

    /// <summary>True for the leaf names DXC's non-Windows builds <c>dlopen</c> as their validator.</summary>
    internal static bool IsDxilLeafName(string fileName) =>
        fileName.Equals("libdxil.dylib", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("libdxil.so", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts <paramref name="handler"/> at the front of <c>Dxc.ResolveLibrary</c>, ahead of the
    /// handler Vortice's static constructor added (see the class remarks). Falls back to a plain
    /// subscription if the event's backing field is not where the pinned Vortice.Dxc keeps it.
    /// Caller holds <see cref="RegisterGate"/>.
    /// </summary>
    private static void SubscribeFirst(DllImportResolver handler)
    {
        // Runs Vortice's static constructor, which adds its own handler and the assembly's
        // DllImportResolver, before we read the list.
        RuntimeHelpers.RunClassConstructor(typeof(Vortice.Dxc.Dxc).TypeHandle);

        FieldInfo? field = typeof(Vortice.Dxc.Dxc).GetField(
            "ResolveLibrary", BindingFlags.NonPublic | BindingFlags.Static);
        if (field is not null && field.FieldType == typeof(DllImportResolver))
        {
            var existing = (DllImportResolver?)field.GetValue(null);
            field.SetValue(null, Delegate.Combine(handler, existing));
            return;
        }

        Vortice.Dxc.Dxc.ResolveLibrary += handler;
    }

    private static IntPtr ResolvePinned(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == DxcLibraryName ? _pinnedDxcHandle : IntPtr.Zero;

    /// <summary>
    /// Android (Phase 50): the native rides in the APK's per-ABI <c>lib/&lt;abi&gt;/</c> dir and
    /// the Android dynamic linker resolves it by SONAME — a bare-name load, NOT path probing
    /// (Android W^X forbids loading executable code from a writable dir, and
    /// <c>AppContext.BaseDirectory</c> is the sandboxed app-data dir, not the APK). Loaded here,
    /// once, so a missing library is an <see cref="LoadErrorCode"/> Result rather than a
    /// <see cref="DllNotFoundException"/> at the first P/Invoke. No identity check: inside the
    /// APK there is no file to read one from; the app's own linker namespace is the only guard.
    /// </summary>
    private static ShaderError? LoadAndroid()
    {
        if (NativeLibrary.TryLoad(AndroidLibFileName, out IntPtr handle))
        {
            _pinnedDxcHandle = handle;
            return null;
        }

        return LoadError(
            $"ShadowDusk's DXC for Android ({AndroidLibFileName}, DXC 1.7.2212.40) is not in this " +
            $"app ({RuntimeInformation.RuntimeIdentifier}), so no DXC-backed compile (DirectX 12, " +
            "OpenGL, Vulkan) can run. It ships in the ShadowDusk.HLSL package for android-arm64 only " +
            "and must be packaged into the APK's lib/<abi>/ directory. DirectX 11 and FNA do not use DXC.");
    }

    private static ShaderError LoadError(string message) => new(
        File: "",
        Line: 0,
        Column: 0,
        Code: LoadErrorCode,
        Message: message);

    /// <summary>
    /// Finds the pinned natives, loads them by absolute path, and decides
    /// <see cref="_foreignValidatorError"/>. Windows, Linux and macOS.
    /// </summary>
    private static ShaderError? LoadPinned()
    {
        bool windows = OperatingSystem.IsWindows();
        bool mac = OperatingSystem.IsMacOS();
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        string rid = PinnedRid(windows ? "win" : mac ? "osx" : "linux", architecture);
        string dxcFile = windows ? "dxcompiler.dll" : mac ? MacLibFileName : "libdxcompiler.so";
        string natives = windows ? $"dxil.dll + {dxcFile}" : dxcFile;

        string? pinnedCompiler = DxcNativeIdentity.Expected(rid, DxcNativeKind.Compiler);
        string? pinnedValidator = DxcNativeIdentity.Expected(rid, DxcNativeKind.Validator);
        if (pinnedCompiler is null)
        {
            return LoadError(
                $"ShadowDusk bundles no DXC for '{rid}' (it ships the pinned DXC 1.7.2212.40 for " +
                "win-x64, win-arm64, linux-x64, osx-x64, osx-arm64 and android-arm64), so no " +
                "DXC-backed compile can run in this process. ShadowDusk will not use a DXC found " +
                "elsewhere on the machine, which would be a different compiler.");
        }

        string[] searchDirectories = GetNativeSearchDirectories();
        string[] assemblyDirectories = GetAssemblyDirectories().ToArray();
        List<string> candidates = mac
            ? GetMacCandidates(AppContext.BaseDirectory, searchDirectories, assemblyDirectories, architecture).ToList()
            : GetPinnedPairDirectories(AppContext.BaseDirectory, searchDirectories, assemblyDirectories, rid, windows)
                .Select(directory => Path.Combine(directory, dxcFile))
                .ToList();

        var rejected = new List<string>();
        var unloadable = new List<string>();
        foreach (string dxcPath in candidates)
        {
            if (!File.Exists(dxcPath)) continue;

            if (!DxcNativeIdentity.Matches(dxcPath, pinnedCompiler))
            {
                rejected.Add($"'{dxcPath}' is {DxcNativeIdentity.Describe(dxcPath)}, not the pinned {pinnedCompiler}");
                continue;
            }

            // Windows: DXC binds dxil.dll by bare name while it loads, so a dxcompiler.dll
            // without the pinned dxil.dll beside it would take whatever the OS search finds.
            string? dxilPath = windows ? Path.Combine(Path.GetDirectoryName(dxcPath)!, "dxil.dll") : null;
            if (dxilPath is not null)
            {
                if (!File.Exists(dxilPath))
                {
                    rejected.Add($"'{dxcPath}' has no dxil.dll beside it");
                    continue;
                }

                if (!DxcNativeIdentity.Matches(dxilPath, pinnedValidator))
                {
                    rejected.Add($"'{dxilPath}' is {DxcNativeIdentity.Describe(dxilPath)}, not the pinned {pinnedValidator}");
                    continue;
                }
            }

            // A pinned-version file can still be unloadable here: win-x64 and win-arm64 carry
            // the same version stamp, and the flat directories are probed whatever the RID, so
            // the other architecture's copy can come first. Skip it and keep looking; it is
            // reported only if no candidate loads.
            try
            {
                if (dxilPath is not null)
                    NativeLibrary.Load(dxilPath);
                _pinnedDxcHandle = NativeLibrary.Load(dxcPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                unloadable.Add($"'{dxcPath}' could not be loaded: {ex.Message}");
                continue;
            }

            // macOS: dyld searches DYLD_LIBRARY_PATH for the LEAF name of every load, absolute
            // paths included, before the path it was given; so what is mapped may not be the
            // file just checked. Ask dyld which image it really is.
            if (mac && VerifyMappedMacDxc(dxcPath, pinnedCompiler) is { } substituted)
                return substituted;

            _foreignValidatorError =
                dxilPath is not null ? VerifyBoundWindowsDxil(dxilPath, pinnedValidator)
                : mac ? VerifyNoMacDxil()
                : null;
            return null;
        }

        string package = mac ? "the ShadowDusk.HLSL package" : "the Vortice.Dxc 3.3.4 package";
        string pinned = $"{natives}, DXC {pinnedCompiler}, from {package}'s runtimes/{rid}/native";
        string searched = " Searched: " + string.Join("; ", candidates.Select(Path.GetDirectoryName).Distinct()) + ".";
        string unloaded = unloadable.Count == 0
            ? ""
            : " Pinned-version files that could not be loaded into this process: " + string.Join("; ", unloadable) + ".";

        if (rejected.Count > 0)
        {
            return LoadError(
                $"ShadowDusk found DXC natives, but not its pinned build ({pinned}), so no DXC-backed " +
                "compile (DirectX 12, OpenGL, Vulkan) can run: it will not compile with a different " +
                "DXC. Found instead: " + string.Join("; ", rejected) + "." + unloaded +
                $" This process resolved Vortice.Dxc {VorticeDxcVersion()}. The usual cause is another " +
                "package raising Vortice.Dxc above 3.3.4 (NuGet reports NU1608 at restore when that " +
                "happens): pin Vortice.Dxc to 3.3.4 in the application " +
                "(<PackageReference Include=\"Vortice.Dxc\" Version=\"3.3.4\" />) for DirectX 12 / " +
                "OpenGL / Vulkan targets. DirectX 11 and FNA do not use DXC." + searched);
        }

        if (unloadable.Count > 0)
        {
            return LoadError(
                $"ShadowDusk's pinned DXC ({pinned}) was found but could not be loaded into this " +
                $"{architecture} process, so no DXC-backed compile can run." + unloaded + searched);
        }

        return LoadError(
            $"ShadowDusk's pinned DXC native library ({pinned}) was not found, so no DXC-backed " +
            "compile can run. ShadowDusk will not fall back to a different DXC, whether it sits in " +
            "the application's own directories or on the system search path: that would be a " +
            "different compiler." + searched);
    }

    /// <summary>The Vortice.Dxc assembly version this process bound, for a diagnostic.</summary>
    private static string VorticeDxcVersion() =>
        typeof(Vortice.Dxc.Dxc).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "(unknown version)";

    /// <summary>
    /// Windows: the <c>dxil.dll</c> a bare-name lookup returns, the one DXC's <c>DllMain</c>
    /// just bound, must be the pinned build. It is ours unless another one was loaded first.
    /// </summary>
    private static ShaderError? VerifyBoundWindowsDxil(string pinnedDxilPath, string? pinnedValidator)
    {
        string? bound = WindowsModules.GetLoadedModulePath("dxil.dll");
        if (bound is null
            || SamePath(bound, pinnedDxilPath)
            || DxcNativeIdentity.Matches(bound, pinnedValidator))
        {
            return null;
        }

        return LoadError(
            $"A different dxil.dll ('{bound}', {DxcNativeIdentity.Describe(bound)}) was loaded into " +
            $"this process before ShadowDusk's pinned one ('{pinnedDxilPath}', {pinnedValidator}). " +
            "DXC binds its DXIL validator and signer to the dxil.dll already loaded under that " +
            "name, so it would validate and sign with a foreign build (a newer one rejects this " +
            "DXC's output outright). Compiles that need the validator (DirectX 12) are refused; " +
            "SPIR-V targets are unaffected. Whatever loaded it first must not share the process " +
            "with ShadowDusk's DirectX 12 compiles.");
    }

    /// <summary>
    /// macOS: the image dyld actually mapped for <see cref="_pinnedDxcHandle"/> must be the
    /// pinned build. dyld resolves even an absolute-path load against <c>DYLD_LIBRARY_PATH</c>
    /// first (by leaf name; measured on the macOS CI lane, issue #270), so a different
    /// <c>libdxcompiler.dylib</c> there would be loaded in place of the file
    /// <see cref="LoadPinned"/> checked. A byte copy of the pinned build is the same compiler
    /// and is accepted. Cannot ask dyld means no finding, never a guess.
    /// </summary>
    private static ShaderError? VerifyMappedMacDxc(string requestedPath, string pinnedCompiler)
    {
        string? mapped = LoadedImages.MacImagePathOf(_pinnedDxcHandle, "DxcCreateInstance");
        if (mapped is null || DxcNativeIdentity.Matches(mapped, pinnedCompiler))
            return null;

        return LoadError(
            $"ShadowDusk loaded its pinned DXC '{requestedPath}' ({pinnedCompiler}), but dyld " +
            $"mapped '{mapped}' ({DxcNativeIdentity.Describe(mapped)}) instead: DYLD_LIBRARY_PATH " +
            "names a directory holding a different libdxcompiler.dylib, and dyld searches it ahead " +
            "of any path it is given. ShadowDusk will not compile with a different DXC, so no " +
            "DXC-backed compile can run in this process. Remove that library from " +
            "DYLD_LIBRARY_PATH.");
    }

    /// <summary>
    /// macOS: our <c>libdxcompiler.dylib</c> has just run its one <c>dlopen("libdxil.dylib")</c>
    /// (see the class remarks). We ship no <c>libdxil</c>, so any such image in the process is a
    /// foreign validator DXC may have bound. Cannot enumerate images means no finding, never a
    /// false positive.
    /// </summary>
    private static ShaderError? VerifyNoMacDxil()
    {
        string? foreign = LoadedImages.MacImagePaths()
            .FirstOrDefault(path => IsDxilLeafName(Path.GetFileName(path)));
        return foreign is null
            ? null
            : LoadError(
                $"A DXIL validator library ('{foreign}') is loaded into this process, and " +
                "ShadowDusk's macOS DXC ships none: DXC picked it up from the dynamic-linker search " +
                "path (DYLD_LIBRARY_PATH, the working directory, or /usr/local/lib) and would " +
                "validate DXIL with a foreign build. Compiles that need the validator " +
                "(DirectX 12) are refused; SPIR-V targets are unaffected. Remove that library " +
                "from those paths.");
    }

    /// <summary>
    /// True when both strings name the same file path. <c>GetModuleFileNameW</c> reports a
    /// <c>\\?\</c>-prefixed path when the module was loaded through one.
    /// </summary>
    internal static bool SamePath(string a, string b) =>
        string.Equals(StripLongPathPrefix(a), StripLongPathPrefix(b), StringComparison.OrdinalIgnoreCase);

    private static string StripLongPathPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    /// <summary>
    /// The directories probed, in order, for the pinned Windows/Linux natives. Pure (no I/O),
    /// so the order is unit-testable. The natives that ship WITH ShadowDusk come first; the
    /// host's directories follow, for the layouts where the two are the same place:
    /// <list type="number">
    /// <item>beside the ShadowDusk.HLSL and Vortice.Dxc assemblies: their
    ///   <c>runtimes/&lt;rid&gt;/native</c> (a framework-dependent app, test host, dotnet tool,
    ///   or a plugin loaded into another host such as MGCB), the directory itself (a
    ///   RID-specific or self-contained publish flattens the natives there), and, when the
    ///   assembly was loaded straight from a NuGet package folder (<c>lib/&lt;tfm&gt;/</c>),
    ///   that package's own <c>runtimes/&lt;rid&gt;/native</c>;</item>
    /// <item><c>&lt;base&gt;/runtimes/&lt;rid&gt;/native</c> and the base directory;</item>
    /// <item>the host's native search directories (<c>NATIVE_DLL_SEARCH_DIRECTORIES</c>): the
    ///   NuGet cache for an app run without copying its natives, and the extraction directory
    ///   of a single-file bundle (whose assemblies have no location).</item>
    /// </list>
    /// A match by name is only a candidate: <see cref="LoadPinned"/> loads the first one that
    /// is the pinned build.
    /// </summary>
    internal static IEnumerable<string> GetPinnedPairDirectories(
        string baseDirectory,
        IEnumerable<string> nativeSearchDirectories,
        IEnumerable<string> assemblyDirectories,
        string rid,
        bool ignoreCase)
    {
        IEnumerable<string> All()
        {
            foreach (string dir in assemblyDirectories)
            {
                yield return Path.Combine(dir, "runtimes", rid, "native");
                yield return dir;
                if (PackageRootOf(dir) is { } package)
                    yield return Path.Combine(package, "runtimes", rid, "native");
            }

            yield return Path.Combine(baseDirectory, "runtimes", rid, "native");
            yield return baseDirectory;
            foreach (string dir in nativeSearchDirectories)
                yield return dir;
        }

        return Distinct(All(), ignoreCase);
    }

    /// <summary>
    /// The ordered <c>libdxcompiler.dylib</c> paths probed on macOS. Pure (no I/O). Same shape
    /// as <see cref="GetPinnedPairDirectories"/>, with the per-arch subdirectories the macOS
    /// layout needs (both arches share one file name):
    /// <list type="number">
    /// <item>beside the ShadowDusk.HLSL and Vortice.Dxc assemblies (per-arch subdirectory, flat,
    ///   <c>runtimes/&lt;rid&gt;/native</c>, and the NuGet package folder's own
    ///   <c>runtimes/&lt;rid&gt;/native</c>): where a plugin host finds them;</item>
    /// <item>the base directory and <c>tools/dxc/</c> above it (<see cref="GetProbeCandidates"/>);</item>
    /// <item>the host's native search directories (<see cref="GetSearchDirectoryCandidates"/>).</item>
    /// </list>
    /// Never a bare name: that is the dynamic linker's search path, where a foreign build sits.
    /// </summary>
    internal static IEnumerable<string> GetMacCandidates(
        string baseDirectory,
        IEnumerable<string> nativeSearchDirectories,
        IEnumerable<string> assemblyDirectories,
        Architecture processArchitecture)
    {
        string rid = PinnedRid("osx", processArchitecture);

        IEnumerable<string> All()
        {
            foreach (string dir in assemblyDirectories)
            {
                yield return Path.Combine(dir, rid, MacLibFileName);
                yield return Path.Combine(dir, MacLibFileName);
                yield return Path.Combine(dir, "runtimes", rid, "native", MacLibFileName);
                if (PackageRootOf(dir) is { } package)
                    yield return Path.Combine(package, "runtimes", rid, "native", MacLibFileName);
            }

            foreach (string candidate in GetProbeCandidates(baseDirectory, processArchitecture))
                yield return candidate;

            foreach (string dir in nativeSearchDirectories)
            {
                foreach (string candidate in GetSearchDirectoryCandidates(dir, processArchitecture))
                    yield return candidate;
            }
        }

        return Distinct(All(), ignoreCase: false);
    }

    /// <summary>
    /// The package root when <paramref name="assemblyDirectory"/> is a NuGet package's
    /// <c>lib/&lt;tfm&gt;</c> folder (the assembly was loaded from the package cache, so its
    /// natives are in the same package's <c>runtimes/</c>), else <c>null</c>.
    /// </summary>
    private static string? PackageRootOf(string assemblyDirectory)
    {
        string? lib = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(assemblyDirectory));
        return lib is not null && Path.GetFileName(lib).Equals("lib", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(lib)
            : null;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> paths, bool ignoreCase)
    {
        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string normalized = Path.TrimEndingDirectorySeparator(path);
            if (normalized.Length > 0 && seen.Add(normalized))
                yield return normalized;
        }
    }

    /// <summary>
    /// The RID naming the natives for the running process: <c>ProcessArchitecture</c>, never the
    /// OS's. Under Rosetta 2 (an osx-x64 process on an arm64 Mac, which GitHub's macOS runners
    /// do) the OS reports Arm64 while only the x64 dylib can load into the process.
    /// </summary>
    internal static string PinnedRid(string os, Architecture processArchitecture) =>
        os + "-" + processArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };

    private static IEnumerable<string> GetAssemblyDirectories()
    {
        foreach (Assembly assembly in new[] { typeof(DxcLoader).Assembly, typeof(Vortice.Dxc.Dxc).Assembly })
        {
            // Empty for single-file bundles (no location); the search directories cover those.
#pragma warning disable IL3000
            string location = assembly.Location;
#pragma warning restore IL3000
            string? directory = location.Length == 0 ? null : Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(directory))
                yield return directory;
        }
    }

    /// <summary>Win32 module lookups for <see cref="VerifyBoundWindowsDxil"/>.</summary>
    private static class WindowsModules
    {
        /// <summary>
        /// The full path of the module a bare-name <c>LoadLibrary(name)</c> would return right
        /// now (the already-loaded module of that base name), or <c>null</c> if none is loaded.
        /// </summary>
        internal static string? GetLoadedModulePath(string moduleName)
        {
            IntPtr module = GetModuleHandleW(moduleName);
            if (module == IntPtr.Zero) return null;

            char[] buffer = new char[32768];
            uint length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
            return length == 0 ? null : new string(buffer, 0, (int)length);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] fileName, uint size);
    }

    /// <summary>
    /// The native images mapped into this process, by path. Used by
    /// <see cref="VerifyNoMacDxil"/>, and by the integration tests that prove which
    /// <c>libdxcompiler</c> a compile really ran in.
    /// </summary>
    internal static class LoadedImages
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";

        /// <summary>
        /// macOS: the path of every image dyld has loaded, in load order. Empty off macOS or
        /// when dyld cannot be asked (never a guess).
        /// </summary>
        internal static IReadOnlyList<string> MacImagePaths()
        {
            var paths = new List<string>();
            if (!OperatingSystem.IsMacOS())
                return paths;

            try
            {
                uint count = _dyld_image_count();
                for (uint i = 0; i < count; i++)
                {
                    if (Marshal.PtrToStringUTF8(_dyld_get_image_name(i)) is { Length: > 0 } path)
                        paths.Add(path);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Cannot enumerate: report nothing rather than fail a compile on a guess.
            }

            return paths;
        }

        /// <summary>
        /// macOS: the path of the image that defines <paramref name="exportName"/> in the library
        /// <paramref name="handle"/> names, as dyld recorded it (<c>dladdr</c>), or <c>null</c>
        /// off macOS or when dyld cannot be asked.
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

        [DllImport(LibSystem)]
        private static extern int dladdr(IntPtr address, out DlInfo info);

        [DllImport(LibSystem)]
        private static extern uint _dyld_image_count();

        [DllImport(LibSystem)]
        private static extern IntPtr _dyld_get_image_name(uint imageIndex);
    }

    /// <summary>
    /// The ordered, fully-qualified file-path candidates probed under the base directory on
    /// macOS. Pure (no I/O) so the order is unit-testable:
    /// base-dir per-arch subdir, base-dir flat, the publish
    /// <c>runtimes/&lt;rid&gt;/native</c> layout, then <c>tools/dxc/</c> per-arch
    /// (and flat, for a manually-placed dylib) walking up to the filesystem root.
    /// </summary>
    internal static IEnumerable<string> GetProbeCandidates(
        string baseDirectory, Architecture processArchitecture)
    {
        string rid = PinnedRid("osx", processArchitecture);

        // 1. Next to the app binaries (csproj copy links; per-arch first, then flat).
        yield return Path.Combine(baseDirectory, rid, MacLibFileName);
        yield return Path.Combine(baseDirectory, MacLibFileName);

        // Self-contained publish keeps the package layout under the app base.
        yield return Path.Combine(baseDirectory, "runtimes", rid, "native", MacLibFileName);

        // 2. tools/dxc/ above the base directory (dev/test runs out of bin/).
        for (DirectoryInfo? dir = new(baseDirectory); dir is not null; dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "dxc", rid, MacLibFileName);
            yield return Path.Combine(dir.FullName, "tools", "dxc", MacLibFileName);
        }
    }

    /// <summary>
    /// The candidates probed inside ONE host native-search directory on macOS: the per-arch
    /// subdirectory the csproj Link paths produce (which single-file extraction
    /// preserves — bug-hunt 2026-07-27 C3: the dylib extracts to
    /// <c>&lt;extractionDir&gt;/osx-&lt;arch&gt;/libdxcompiler.dylib</c>, which a flat probe
    /// never sees), then flat (the NuGet <c>runtimes/&lt;rid&gt;/native</c> asset of a
    /// framework-dependent consumer). Pure (no I/O) so the order is unit-testable, like
    /// <see cref="GetProbeCandidates"/>.
    /// </summary>
    internal static IEnumerable<string> GetSearchDirectoryCandidates(
        string directory, Architecture processArchitecture)
    {
        string rid = PinnedRid("osx", processArchitecture);
        yield return Path.Combine(directory, rid, MacLibFileName);
        yield return Path.Combine(directory, MacLibFileName);
    }

    private static string[] GetNativeSearchDirectories()
    {
        // Set by the host from deps.json (includes each package's runtimes/<rid>/native).
        return AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];
    }
}
