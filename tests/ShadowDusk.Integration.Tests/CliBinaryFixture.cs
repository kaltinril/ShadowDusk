#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// xUnit class fixture that exposes a runnable ShadowDusk.Cli executable to integration tests.
///
/// <para><b>Phase 21 (test-suite performance):</b> this fixture used to run a full
/// <c>dotnet publish -c Release</c> into a fresh temp directory on every construction. That
/// nested SDK build — a cold Release compile of the whole dependency tree, plus a copy of
/// large native binaries (<c>dxcompiler.dll</c>, SPIRV-Cross) into a brand-new directory the
/// antivirus had never scanned — is the structural cost that best fits the observed ~400×
/// non-determinism (seconds when the Release cache + AV cache were warm; many minutes when
/// cold). It now <b>reuses the CLI binary produced by the normal build</b> (the test project
/// has a <c>ReferenceOutputAssembly=false</c> ProjectReference to ShadowDusk.Cli, so the CLI
/// is always built alongside the tests). The publish path remains only as a fallback for an
/// environment where the build output is somehow absent.</para>
/// </summary>
public sealed class CliBinaryFixture : IDisposable
{
    /// <summary>Non-null only when we fell back to publishing; that temp dir is cleaned on dispose.</summary>
    private readonly string? _publishedTempDir;

    public string ExecutablePath { get; }

    public CliBinaryFixture()
    {
        string repoRoot = FindRepoRoot();

        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ShadowDuskCLI.exe"
            : "ShadowDuskCLI";

        // Fast path: reuse the binary the normal build already produced.
        string? built = LocateBuiltCli(repoRoot, exeName);
        if (built is not null)
        {
            ExecutablePath = built;
            _publishedTempDir = null;
            return;
        }

        // Fallback: publish (fresh checkout where the CLI wasn't built for some reason).
        _publishedTempDir = PublishCli(repoRoot);
        ExecutablePath = Path.Combine(_publishedTempDir, exeName);

        if (!File.Exists(ExecutablePath))
            throw new FileNotFoundException($"Published CLI binary not found at '{ExecutablePath}'.");
    }

    public void Dispose()
    {
        if (_publishedTempDir is null)
            return; // We reused build output — nothing to clean.

        try
        {
            if (Directory.Exists(_publishedTempDir))
                Directory.Delete(_publishedTempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — do not rethrow from Dispose.
        }
    }

    /// <summary>
    /// Probe the CLI project's standard build output for an already-built executable, preferring
    /// the most recently written across Debug/Release. Returns null if none exists.
    /// </summary>
    private static string? LocateBuiltCli(string repoRoot, string exeName)
    {
        string cliBin = Path.Combine(repoRoot, "src", "ShadowDusk.Cli", "bin");
        if (!Directory.Exists(cliBin))
            return null;

        string? newest = null;
        DateTime newestStamp = DateTime.MinValue;

        foreach (string config in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(cliBin, config, "net8.0", exeName);
            if (File.Exists(candidate))
            {
                DateTime stamp = File.GetLastWriteTimeUtc(candidate);
                if (stamp > newestStamp)
                {
                    newestStamp = stamp;
                    newest = candidate;
                }
            }
        }

        return newest;
    }

    private static string PublishCli(string repoRoot)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ShadowDuskCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string cliProjectPath = Path.Combine(repoRoot, "src", "ShadowDusk.Cli", "ShadowDusk.Cli.csproj");

        var psi = new ProcessStartInfo("dotnet")
        {
            Arguments              = $"publish \"{cliProjectPath}\" -o \"{tempDir}\" --no-self-contained -c Release",
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

        return tempDir;
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test assembly directory to the repo root (the dir holding the solution).
        DirectoryInfo dir = new(AppContext.BaseDirectory);

        while (dir.Parent is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repository root (ShadowDusk.slnx). " +
            "Ensure the test is running from a directory under the repository root.");
    }
}
