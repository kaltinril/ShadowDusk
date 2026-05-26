#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// xUnit class fixture that publishes the ShadowDusk.Cli project to a temporary directory
/// and exposes the path to the produced executable for integration tests.
/// </summary>
public sealed class CliBinaryFixture : IDisposable
{
    private readonly string _tempDir;

    public string ExecutablePath { get; }

    public CliBinaryFixture()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ShadowDuskCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        string cliProjectPath = FindCliProjectPath();

        var psi = new ProcessStartInfo("dotnet")
        {
            Arguments              = $"publish \"{cliProjectPath}\" -o \"{_tempDir}\" --no-self-contained -c Release",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetDirectoryName(cliProjectPath)!,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish process.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed with exit code {process.ExitCode}.\n" +
                $"stdout: {stdout}\n" +
                $"stderr: {stderr}");
        }

        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ShadowDusk.Cli.exe"
            : "ShadowDusk.Cli";

        ExecutablePath = Path.Combine(_tempDir, exeName);

        if (!File.Exists(ExecutablePath))
            throw new FileNotFoundException($"Published CLI binary not found at '{ExecutablePath}'.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — do not rethrow from Dispose.
        }
    }

    private static string FindCliProjectPath()
    {
        // Walk up from the test assembly directory to find the repo root,
        // then navigate to the CLI project file.
        DirectoryInfo dir = new(AppContext.BaseDirectory);

        while (dir.Parent is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ShadowDusk.slnx");
            if (File.Exists(candidate))
            {
                string cliProjectPath = Path.Combine(
                    dir.FullName,
                    "src",
                    "ShadowDusk.Cli",
                    "ShadowDusk.Cli.csproj");

                if (File.Exists(cliProjectPath))
                    return cliProjectPath;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate ShadowDusk.Cli.csproj. " +
            "Ensure the test is running from a directory under the repository root.");
    }
}
