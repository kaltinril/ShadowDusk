#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.Slang;

/// <summary>
/// Keeps Windows from showing a MODAL system dialog when a child process cannot start.
/// </summary>
/// <remarks>
/// <para>On an interactive Windows desktop, <c>CreateProcess</c> on a file that is not a valid
/// image for this machine (a damaged or truncated download, the wrong architecture) raises a
/// system hard error: an "Unsupported 16-Bit Application" (or "is not a valid Win32
/// application") message box that blocks the starting thread until a person clicks OK. A
/// consumer with a damaged slangc would get that dialog instead of ShadowDusk's loud
/// <c>SD0622</c>, and an unattended host would simply hang (measured on Windows 11 by
/// <c>SlangNativeDeploymentTests.BinaryTheOsCannotStart_SD0622_WithTheOsReason</c>; a desktop-less
/// CI runner never shows it).</para>
/// <para><c>SetThreadErrorMode</c> with <c>SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX</c> OR'd into the thread's current mode makes the
/// system return the error to the caller instead of asking the user, so <c>Process.Start</c>
/// throws its <c>Win32Exception</c> at once (the OS's reason intact). It is set for the calling
/// THREAD only, around the one call, and the previous mode is restored: nothing else in the host
/// process changes. A no-op on every other OS.</para>
/// </remarks>
internal static class WindowsHardErrorSuppression
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);

    [DllImport("kernel32.dll")]
    private static extern uint GetThreadErrorMode();

    /// <summary>Runs <paramref name="start"/> with the calling thread's hard-error dialogs off.</summary>
    public static T Run<T>(Func<T> start)
    {
        if (!OperatingSystem.IsWindows())
            return start();

        // OR'd into the thread's current mode, so a bit the host already set (for example
        // SEM_NOGPFAULTERRORBOX) stays set for the call; the previous mode comes back after.
        uint current = GetThreadErrorMode();
        bool changed = SetThreadErrorMode(current | SemFailCriticalErrors | SemNoOpenFileErrorBox, out uint previous);
        try
        {
            return start();
        }
        finally
        {
            if (changed)
                SetThreadErrorMode(previous, out _);
        }
    }
}
