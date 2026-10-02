#nullable enable

using Xunit;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Skip reasons for the tests that exercise the Win32 DLL search order. They report SKIPPED, never
/// passed, where the condition they need does not exist.
/// </summary>
internal static class WindowsDllSearch
{
    public const string NotWindows =
        "Exercises the Win32 DLL search order (bare-name LoadLibrary of dxil.dll); Windows only.";

    public const string NoWindowsSdk =
        "No Windows SDK dxil.dll under Program Files (x86)\\Windows Kits\\10\\bin on this machine.";

    /// <summary>
    /// The newest installed Windows SDK <c>bin\&lt;version&gt;\x64</c> directory holding a
    /// <c>dxil.dll</c> (the folder every VS Developer Command Prompt puts on <c>PATH</c>), or null.
    /// </summary>
    public static string? FindWindowsSdkBinWithDxil()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string kits = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        if (!Directory.Exists(kits))
            return null;

        return Directory.GetDirectories(kits)
            .Select(d => Path.Combine(d, "x64"))
            .Where(d => File.Exists(Path.Combine(d, "dxil.dll")))
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

/// <summary>A fact that runs only on Windows and is reported as skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = WindowsDllSearch.NotWindows;
    }
}

/// <summary>A theory that runs only on Windows and is reported as skipped elsewhere.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = WindowsDllSearch.NotWindows;
    }
}

/// <summary>
/// A fact that needs the Windows SDK's own <c>dxil.dll</c> (the reported real-world foreign
/// validator, 1.8.x). Skipped, not passed, on a machine without one.
/// </summary>
public sealed class WindowsSdkDxilFactAttribute : FactAttribute
{
    public WindowsSdkDxilFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = WindowsDllSearch.NotWindows;
        else if (WindowsDllSearch.FindWindowsSdkBinWithDxil() is null)
            Skip = WindowsDllSearch.NoWindowsSdk;
    }
}
