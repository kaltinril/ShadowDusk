#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Slang;

/// <summary>
/// Locates and runs the pinned <c>slangc</c> compiler (the <c>.slang → HLSL</c> half of the
/// Slang input route). Location order:
/// <list type="number">
///   <item><c>SHADOWDUSK_SLANGC</c> — an explicit path to the executable (CI, unusual layouts).</item>
///   <item><c>tools/slang/bin/slangc(.exe)</c> found by walking up from the app base directory to
///   a directory carrying a repo marker — the same bounded dev-convenience walk the native
///   loaders use. `tools/setup-local-testing.ps1 -WithSlang` provisions this, SHA-256 verified.</item>
///   <item><c>slangc</c> on <c>PATH</c>.</item>
/// </list>
///
/// <para><b>Packaging status (honest):</b> the Slang native does not yet ride inside the NuGet
/// packages, so on a consumer machine with none of the above the route fails loudly with
/// <c>SD0600</c> and the exact provisioning command. Owner direction 2026-08-13 is that Slang
/// input ultimately works everywhere the library works, which means owning the pinned per-RID
/// native the way DXC/SPIRV-Cross/vkd3d already ship — that packaging is the tracked remainder
/// of Phase 61 (A3), not a hidden gap.</para>
/// </summary>
internal static class SlangToolchain
{
    /// <summary>Environment variable naming the <c>slangc</c> executable explicitly.</summary>
    public const string ToolEnvVar = "SHADOWDUSK_SLANGC";

    // error[E20001]: unexpected token        <- headline line
    //  --> file.slang:12:5                   <- location line (optional, follows)
    private static readonly Regex DiagnosticHeadline = new(
        @"^(?<sev>error|warning)\[(?<code>[A-Z]?\d+)\]:\s*(?<msg>.*)$", RegexOptions.Compiled);

    private static readonly Regex DiagnosticLocation = new(
        @"^\s*-->\s*(?<file>.+?):(?<line>\d+):(?<col>\d+)\s*$", RegexOptions.Compiled);

    /// <summary>Finds <c>slangc</c>, or null with the places probed (for the SD0600 message).</summary>
    public static (string? Path, IReadOnlyList<string> Probed) Locate()
    {
        var probed = new List<string>();

        string? explicitPath = Environment.GetEnvironmentVariable(ToolEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            probed.Add($"{ToolEnvVar}={explicitPath}");
            if (File.Exists(explicitPath))
                return (explicitPath, probed);
        }

        string exe = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";

        // Bounded dev-convenience walk-up: stop at a directory carrying a repo marker, the same
        // rule the native loaders follow (recorded in project_decisions.md).
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            bool isRepoRoot = File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx"))
                              || Directory.Exists(Path.Combine(dir.FullName, ".git"));
            if (!isRepoRoot)
                continue;

            string candidate = Path.Combine(dir.FullName, "tools", "slang", "bin", exe);
            probed.Add(candidate);
            if (File.Exists(candidate))
                return (candidate, probed);
            break;
        }

        // PATH probe: defer existence to the OS resolver by attempting nothing here — returning
        // the bare name makes Process.Start use PATH; a launch failure is caught by the runner.
        probed.Add($"'{exe}' on PATH");
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), exe)))
                    return (Path.Combine(dir.Trim(), exe), probed);
            }
            catch (ArgumentException)
            {
                // A malformed PATH segment (illegal chars) is the environment's problem, not a
                // reason to fail location while other segments may still resolve.
            }
        }

        return (null, probed);
    }

    /// <summary>
    /// Runs <c>slangc</c> on <paramref name="sourcePath"/> emitting HLSL for every entry point in
    /// one invocation (multi-entry to stdout — measured: per-entry <c>-o</c> is refused by the
    /// pinned build, while stdout concatenates all kernels and exits 0/non-0 faithfully).
    /// Returns the concatenated HLSL, or slangc's own diagnostics <b>verbatim</b> — file, line,
    /// column and text unmodified, per the fail-loudly rule.
    /// </summary>
    public static async Task<Result<string, ShaderError[]>> EmitHlslAsync(
        string toolPath,
        string sourcePath,
        IReadOnlyList<SlangEntryPoint> entryPoints,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(toolPath)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
        };

        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-target");
        psi.ArgumentList.Add("hlsl");
        foreach (SlangEntryPoint entry in entryPoints)
        {
            // Ordering is load-bearing: slangc requires -stage to FOLLOW its -entry (E00034).
            psi.ArgumentList.Add("-entry");
            psi.ArgumentList.Add(entry.Name);
            psi.ArgumentList.Add("-stage");
            psi.ArgumentList.Add(entry.Stage == SlangStage.Vertex ? "vertex" : "fragment");
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result<string, ShaderError[]>.Fail(
            [
                new ShaderError(
                    File: sourcePath, Line: 0, Column: 0, Code: "SD0600",
                    Message: $"failed to start slangc at '{toolPath}': {ex.Message}"),
            ]);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            return Result<string, ShaderError[]>.Fail(ParseDiagnostics(stderr, sourcePath));

        return Result<string, ShaderError[]>.Ok(stdout);
    }

    /// <summary>
    /// Parses slangc's rustc-style stderr into located <see cref="ShaderError"/>s, keeping
    /// slangc's own code and message text verbatim. Anything unparseable becomes one
    /// <c>SD0601</c> carrying the full raw text — never dropped, never reworded.
    /// </summary>
    internal static ShaderError[] ParseDiagnostics(string stderr, string sourcePath)
    {
        var errors = new List<ShaderError>();
        string[] lines = stderr.Split('\n');
        var unclaimed = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            Match headline = DiagnosticHeadline.Match(lines[i].TrimEnd('\r'));
            if (!headline.Success)
            {
                unclaimed.AppendLine(lines[i].TrimEnd('\r'));
                continue;
            }

            string file = sourcePath;
            int line = 0, column = 0;
            if (i + 1 < lines.Length)
            {
                Match location = DiagnosticLocation.Match(lines[i + 1].TrimEnd('\r'));
                if (location.Success)
                {
                    file = location.Groups["file"].Value;
                    line = int.Parse(location.Groups["line"].Value);
                    column = int.Parse(location.Groups["col"].Value);
                }
            }

            errors.Add(new ShaderError(
                File: file, Line: line, Column: column,
                Code: headline.Groups["code"].Value,
                Message: headline.Groups["msg"].Value,
                Severity: headline.Groups["sev"].Value == "warning"
                    ? ShaderErrorSeverity.Warning
                    : ShaderErrorSeverity.Error));
        }

        if (errors.Count == 0)
        {
            string raw = unclaimed.ToString().Trim();
            errors.Add(new ShaderError(
                File: sourcePath, Line: 0, Column: 0, Code: "SD0601",
                Message: raw.Length > 0
                    ? $"slangc failed:{Environment.NewLine}{raw}"
                    : "slangc failed and emitted no diagnostic text at all."));
        }

        return errors.ToArray();
    }

    /// <summary>The loud, actionable SD0600 for "no slangc anywhere".</summary>
    public static ShaderError NotFound(string sourcePath, IReadOnlyList<string> probed) => new(
        File: sourcePath, Line: 0, Column: 0, Code: "SD0600",
        Message: "the Slang compiler (slangc) was not found. Probed: "
                 + string.Join("; ", probed)
                 + ". In this repository run `pwsh tools/setup-local-testing.ps1 -WithSlang`; "
                 + $"elsewhere set {ToolEnvVar} to the slangc executable or put slangc on PATH.");
}
