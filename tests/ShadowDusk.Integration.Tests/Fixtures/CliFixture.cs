#nullable enable

using System.Runtime.InteropServices;
using Xunit;

namespace ShadowDusk.Integration.Tests.Fixtures;

/// <summary>
/// xUnit IAsyncLifetime fixture that locates the CLI binary once per test class.
/// Skips the test collection when the binary has not been built yet rather than failing,
/// because CLI binary availability depends on the build environment.
/// </summary>
public sealed class CliFixture : IAsyncLifetime
{
    public string CliPath { get; private set; } = string.Empty;

    public Task InitializeAsync()
    {
        CliPath = LocateCliBinary();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string LocateCliBinary()
    {
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ShadowDusk.Cli.exe"
            : "ShadowDusk.Cli";

        string mgfxcName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "mgfxc.exe"
            : "mgfxc";

        string baseDir = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(baseDir, exeName),
            Path.Combine(baseDir, mgfxcName),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Walk up to find a published output directory alongside the solution.
        DirectoryInfo dir = new(baseDir);
        while (dir.Parent is not null)
        {
            string slnCandidate = Path.Combine(dir.FullName, "ShadowDusk.slnx");
            if (File.Exists(slnCandidate))
            {
                // Check common publish output locations.
                string[] publishCandidates =
                [
                    Path.Combine(dir.FullName, "artifacts", exeName),
                    Path.Combine(dir.FullName, "artifacts", mgfxcName),
                ];
                foreach (string pc in publishCandidates)
                {
                    if (File.Exists(pc))
                        return pc;
                }
                break;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"CLI binary not found at any expected path. " +
            $"Run 'dotnet publish src/ShadowDusk.Cli/ShadowDusk.Cli.csproj' first.");
    }
}
