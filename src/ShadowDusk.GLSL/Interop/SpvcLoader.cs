#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using ShadowDusk.Core;

namespace ShadowDusk.GLSL.Interop;

/// <summary>
/// Makes SPIRV-Cross run as ShadowDusk's own pinned build, and only as that build (issue #350,
/// the counterpart of issue #270's DXC fix).
///
/// <para><b>Desktop (Windows, Linux, macOS).</b> The native comes from
/// <c>Silk.NET.SPIRV.Cross.Native</c> 2.23.0 (a dependency of ShadowDusk.GLSL) under
/// <c>runtimes/&lt;rid&gt;/native</c>. <see cref="PinnedNativeLibrary"/> probes the places that
/// package lands by absolute path (beside the ShadowDusk assemblies first, so a plugin host such
/// as MGCB finds it, then the application and the host's native search directories), checks each
/// file's SHA-256 against <see cref="Sha256ByRid"/> BEFORE loading it, and on macOS checks the
/// image dyld really mapped. The loader used to fall back to a bare
/// <c>NativeLibrary.TryLoad("spirv-cross.dll")</c> (<c>libspirv-cross.so</c>,
/// <c>libspirv-cross.dylib</c>), the OS search: <c>PATH</c>, <c>LD_LIBRARY_PATH</c>,
/// <c>DYLD_LIBRARY_PATH</c>, so a different SPIRV-Cross was loaded whenever the app-local probe
/// missed, which it always did in a RID-specific publish and inside MGCB. A missing or foreign
/// library is now <see cref="LoadErrorCode"/>, and the import resolver throws rather than return
/// <see cref="IntPtr.Zero"/> (which would hand the request to the runtime's default probing and
/// through it to the same OS search).</para>
///
/// <para><b>Android (Phase 50).</b> The native is OUR OWN arm64-v8a build, packed by
/// ShadowDusk.GLSL under <c>runtimes/android-arm64/native</c>, which .NET for Android places in
/// the APK's <c>lib/&lt;abi&gt;/</c>. It is loaded by its SONAME, which the Android linker
/// resolves only inside the app's own linker namespace (the APK's libraries; no
/// <c>LD_LIBRARY_PATH</c> reaches an app), and never by path (Android W^X; the APK is not a
/// directory). Known residual: no build identity is checked there yet (the APK holds no file to
/// hash, unlike <c>DxcLoader</c>, which reads the mapped image's build id).</para>
/// </summary>
internal static class SpvcLoader
{
    /// <summary>The diagnostic for a missing, unloadable or foreign SPIRV-Cross.</summary>
    internal const string LoadErrorCode = "SD0103";

    /// <summary>The Silk.NET.SPIRV.Cross.Native release whose natives <see cref="Sha256ByRid"/> pins.</summary>
    internal const string PinnedSilkVersion = "2.23.0";

    /// <summary>The file name ShadowDusk's own Android build ships under (resolved by SONAME).</summary>
    internal const string AndroidLibFileName = "libspirv-cross.so";

    /// <summary>
    /// The SHA-256 of every SPIRV-Cross file <c>Silk.NET.SPIRV.Cross.Native</c>
    /// <see cref="PinnedSilkVersion"/> ships, by RID. <c>SpvcLoaderTests</c> fails if they differ
    /// from the files the build actually copies (a Silk.NET bump must re-pin them here).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Sha256ByRid = new Dictionary<string, string>
    {
        ["win-x64"] = "1b00f8c042d4eb4060b1f4b2c1ee1ef2f0935c7857efe60c62d9cea236ea834e",
        ["win-arm64"] = "4d6143fd7ae2dd75edf22ddcc8085749614ca36cbdc968af3d2f073c8272b9f1",
        ["win-x86"] = "ba05b2bc1cae1bdc19a85efbd1a4c65d7ea3427e1c4e7d3097564c8befe3bc5e",
        ["linux-x64"] = "513de25783914c2193577e36c5623543eaf970b4e8c5abb1ed97e002aa5f874c",
        ["linux-arm64"] = "7c78355e34d3953364770652818cb8bd76ea8dd6621c00dbda55994a6f79e9e4",
        ["linux-arm"] = "b761047bb33a53ca78c8a7dca9b99fab9c1c814603e91e0c0d4ae45e9be74668",
        ["osx-x64"] = "b8a6ce9bc707d15b7bbbee139fb2363cec116bf8aad58e181642a39ef4fed189",
        ["osx-arm64"] = "071c4ef2a38c5fd78ccdda419cb79ec4fded6f85ed3bdeeae5bb09c3401eb093",
    };

    private static readonly object RegisterGate = new();
    private static volatile bool _registered;
    private static readonly Lazy<PinnedNativeLibrary.LoadResult> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Idempotently installs the import resolver. Cheap: the library itself is found, verified
    /// and loaded on first use (<see cref="EnsureLoaded"/>), so constructing a transpiler that
    /// never runs costs nothing. A lock (not a lone CAS) so a concurrent second caller BLOCKS
    /// until the winner has finished installing the resolver (the DxcLoader race class).
    /// </summary>
    public static void Register()
    {
        if (_registered) return;
        lock (RegisterGate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(SpvcLoader).Assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>
    /// Loads the pinned library once per process; <c>null</c> when SPIRV-Cross may be used,
    /// otherwise the <see cref="LoadErrorCode"/> error the caller must return instead.
    /// </summary>
    public static ShaderError? EnsureLoaded()
    {
        Register();
        return Loaded.Value.Error;
    }

    /// <summary>The file the pinned library was loaded from (desktop), or <c>null</c>.</summary>
    internal static string? LoadedPath => Loaded.IsValueCreated ? Loaded.Value.Path : null;

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Must match the DllImport name in SpvcNative (`LibName = "spirv-cross"`); it once
        // tested a name no P/Invoke declares and was silently dead.
        if (name != SpvcNative.LibName) return IntPtr.Zero;

        // Never IntPtr.Zero for our library: that would let the runtime's default probing load
        // whatever the OS search finds. Callers check EnsureLoaded() first, so this only fires
        // for a P/Invoke that skipped it.
        PinnedNativeLibrary.LoadResult result = Loaded.Value;
        return result.Error is null ? result.Handle : throw new DllNotFoundException(result.Error.Message);
    }

    private static PinnedNativeLibrary.LoadResult Load()
    {
        if (OperatingSystem.IsAndroid())
            return LoadAndroid();

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return Fail(
                $"ShadowDusk bundles no SPIRV-Cross for this operating system ({RuntimeInformation.OSDescription}, " +
                $"{RuntimeInformation.RuntimeIdentifier}), so no SPIR-V to GLSL transpile (OpenGL) can run here. It " +
                "ships its pinned SPIRV-Cross for Windows, Linux, macOS and Android only, and will not use one found " +
                "elsewhere.");
        }

        var native = new PinnedNative(
            DisplayName: "SPIRV-Cross",
            ErrorCode: LoadErrorCode,
            Origin: $"the Silk.NET.SPIRV.Cross.Native {PinnedSilkVersion} package's runtimes/{PinnedNativeLibrary.CurrentRid()}/native",
            Consequence: "no SPIR-V to GLSL transpile (OpenGL) can run",
            ProbeExport: "spvc_context_create",
            FileNames: [GetLibFileName()],
            Sha256ByRid: Sha256ByRid,
            MismatchHint: $"The usual cause is another package raising Silk.NET.SPIRV.Cross.Native above {PinnedSilkVersion}, " +
                          "which ships a different SPIRV-Cross: pin it in the application " +
                          $"(<PackageReference Include=\"Silk.NET.SPIRV.Cross.Native\" Version=\"{PinnedSilkVersion}\" />).");

        return PinnedNativeLibrary.Load(native, [typeof(SpvcLoader).Assembly], []);
    }

    /// <summary>
    /// Android: the APK's own <see cref="AndroidLibFileName"/>, by SONAME (see the class remarks).
    /// Loaded here, once, so a missing library is a <see cref="LoadErrorCode"/> Result.
    /// </summary>
    private static PinnedNativeLibrary.LoadResult LoadAndroid()
    {
        if (NativeLibrary.TryLoad(AndroidLibFileName, out IntPtr handle))
            return new PinnedNativeLibrary.LoadResult(handle, AndroidLibFileName, null);

        return Fail(
            $"ShadowDusk's SPIRV-Cross for Android ({AndroidLibFileName}) is not in this app " +
            $"({RuntimeInformation.RuntimeIdentifier}), so no SPIR-V to GLSL transpile (OpenGL) can run. It ships in " +
            "the ShadowDusk.GLSL package for android-arm64 only and must be packaged into the APK's lib/<abi>/ directory.");
    }

    private static PinnedNativeLibrary.LoadResult Fail(string message) =>
        new(IntPtr.Zero, null, new ShaderError(File: "<spirv-cross>", Line: 0, Column: 0, Code: LoadErrorCode, Message: message));

    // Kept for SpvcLoaderTests (Phase 50): the pure RID map, now shared with every pinned native.
    internal static string MapRid(bool isWindows, bool isOsx, bool isAndroid, Architecture arch) =>
        PinnedNativeLibrary.MapRid(isWindows, isOsx, isAndroid, arch);

    private static string GetLibFileName() =>
        OperatingSystem.IsWindows() ? "spirv-cross.dll"
        : OperatingSystem.IsMacOS() ? "libspirv-cross.dylib"
        : "libspirv-cross.so"; // Linux
}
