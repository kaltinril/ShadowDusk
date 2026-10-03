#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Reports which loaded native image owns the current handler of each signal LLVM's
/// <c>RegisterHandlers()</c> touches, so a test can assert DXC's handlers are not left
/// installed over the .NET runtime's. macOS and glibc Linux only (<c>sa_handler</c> is the
/// first field of <c>struct sigaction</c> there); empty elsewhere.
/// </summary>
internal static class SignalHandlerOwnership
{
    // Same lists as ShadowDusk.HLSL's DxcSignalIsolation (LLVM 3.7 IntSigs + KillSigs).
    private static readonly int[] MacOsSignals = [1, 2, 13, 15, 30, 31, 4, 5, 6, 8, 10, 11, 3, 12, 24, 25, 7];
    private static readonly int[] LinuxSignals = [1, 2, 13, 15, 10, 12, 4, 5, 6, 8, 7, 11, 3, 31, 24, 25];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SigactionFn(int signal, nint action, nint oldAction);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DladdrFn(nint address, nint info);

    /// <summary>Whether this host can run the check.</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>
    /// The signals whose current handler lives in a native image whose path contains
    /// <paramref name="imageNameFragment"/>, as "signal: image" strings.
    /// </summary>
    public static IReadOnlyList<string> SignalsHandledBy(string imageNameFragment)
    {
        if (!IsSupported) return [];

        nint self = NativeLibrary.GetMainProgramHandle();
        var sigaction = Marshal.GetDelegateForFunctionPointer<SigactionFn>(NativeLibrary.GetExport(self, "sigaction"));
        var dladdr = Marshal.GetDelegateForFunctionPointer<DladdrFn>(NativeLibrary.GetExport(self, "dladdr"));

        // Opaque struct sigaction (glibc 152 bytes, macOS 16) and Dl_info (4 pointers).
        nint action = Marshal.AllocHGlobal(512);
        nint info = Marshal.AllocHGlobal(4 * nint.Size);
        try
        {
            var owned = new List<string>();
            foreach (int signal in OperatingSystem.IsMacOS() ? MacOsSignals : LinuxSignals)
            {
                if (sigaction(signal, 0, action) != 0) continue;

                nint handler = Marshal.ReadIntPtr(action);
                if (handler is 0 or 1) continue; // SIG_DFL / SIG_IGN

                if (dladdr(handler, info) == 0) continue;
                nint fileName = Marshal.ReadIntPtr(info);
                if (fileName == 0) continue;

                string image = Marshal.PtrToStringUTF8(fileName) ?? "";
                if (image.Contains(imageNameFragment, StringComparison.Ordinal))
                    owned.Add($"{signal}: {image}");
            }

            return owned;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(action);
        }
    }
}
