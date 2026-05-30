#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Integration.Tests;

public enum InvocationMode { CliProcess, DirectPipeline }

public sealed record CompileResult(int ExitCode, byte[] Mgfx, string Stderr);

public static class TestHelpers
{
    public static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", fileName);

    public static async Task<CompileResult> CompileFixtureAsync(
        string fx,
        string profile,
        InvocationMode mode = InvocationMode.DirectPipeline,
        CancellationToken ct = default)
    {
        var inputPath  = FixturePath(fx);
        var outputDir  = Path.Combine(Path.GetTempPath(), $"shadowdusk_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        // Flatten the output filename so fixtures referenced by a nested path
        // (e.g. "examples/Foo.fx") still write to the temp dir root rather than a
        // non-existent sub-directory. Input resolution above keeps the sub-path.
        var outputPath = Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(fx), ".mgfx"));

        try
        {
            return mode switch
            {
                InvocationMode.CliProcess     => await CompileViaCliAsync(inputPath, outputPath, profile, ct),
                InvocationMode.DirectPipeline => await CompileViaPipelineAsync(inputPath, outputPath, profile, ct),
                _                             => throw new ArgumentOutOfRangeException(nameof(mode))
            };
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* non-fatal */ }
        }
    }

    public static async Task<CompileResult> CompileViaCliAsync(
        string inputPath,
        string outputPath,
        string profile,
        CancellationToken ct)
    {
        string cliBinary = FindCliBinary();

        string arguments = BuildArgString(inputPath, outputPath, $"/Profile:{profile}");

        var psi = new ProcessStartInfo(cliBinary)
        {
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetTempPath(),
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start CLI process at '{cliBinary}'.");

        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        byte[] mgfx = File.Exists(outputPath) ? await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false) : Array.Empty<byte>();

        return new CompileResult(process.ExitCode, mgfx, stderr);
    }

    public static async Task<CompileResult> CompileViaPipelineAsync(
        string inputPath,
        string outputPath,
        string profile,
        CancellationToken ct)
    {
        PlatformTarget? target = profile switch
        {
            "OpenGL"      => PlatformTarget.OpenGL,
            "DirectX_11"  => PlatformTarget.DirectX,
            "Vulkan"      => PlatformTarget.Vulkan,
            _             => null,
        };

        if (target is null)
        {
            string errorMsg = $"error X0004: Unknown profile '{profile}'. Valid profiles: DirectX_11, OpenGL, Vulkan";
            return new CompileResult(1, Array.Empty<byte>(), errorMsg);
        }

        string hlslSource;
        try
        {
            hlslSource = await File.ReadAllTextAsync(inputPath, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new CompileResult(1, Array.Empty<byte>(), $"error X0001: {ex.Message}");
        }

        var options = new CompilerOptions
        {
            Target          = target.Value,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = inputPath,
        };

        var compiler      = new EffectCompiler();
        var compileResult = await compiler.CompileAsync(hlslSource, options, ct).ConfigureAwait(false);

        if (compileResult.IsFailure)
        {
            string stderr = FormatErrors(compileResult.Error);
            return new CompileResult(1, Array.Empty<byte>(), stderr);
        }

        byte[] mgfxBytes = compileResult.Value.Data;
        await File.WriteAllBytesAsync(outputPath, mgfxBytes, ct).ConfigureAwait(false);

        return new CompileResult(0, mgfxBytes, string.Empty);
    }

    private static string FormatErrors(IEnumerable<ShaderError> errors)
    {
        var sb = new StringBuilder();
        foreach (ShaderError e in errors)
        {
            string fileName = Path.GetFileName(e.File);
            if (e.Line > 0)
                sb.AppendLine($"{fileName}({e.Line},{e.Column}): error {e.Code}: {e.Message}");
            else
                sb.AppendLine($"error {e.Code}: {e.Message}");
        }
        return sb.ToString();
    }

    private static string FindCliBinary()
    {
        // Check the test assembly output directory first (e.g. after dotnet publish).
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ShadowDusk.Cli.exe"
            : "ShadowDusk.Cli";

        string candidate = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(candidate))
            return candidate;

        // Also check for the mgfxc tool name used by PackAsTool.
        string mgfxcName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "mgfxc.exe"
            : "mgfxc";

        string mgfxcCandidate = Path.Combine(AppContext.BaseDirectory, mgfxcName);
        if (File.Exists(mgfxcCandidate))
            return mgfxcCandidate;

        throw new FileNotFoundException(
            $"CLI binary not found. Searched: '{candidate}', '{mgfxcCandidate}'. " +
            "Build the CLI project before running CLI-mode integration tests.");
    }

    private static string BuildArgString(params string[] parts)
    {
        return string.Join(" ", parts.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
    }
}
