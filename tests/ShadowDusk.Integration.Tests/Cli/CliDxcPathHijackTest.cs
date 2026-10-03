#nullable enable

using System.Diagnostics;
using Shouldly;
using ShadowDusk.Integration.Tests.Dxc;
using Xunit;

namespace ShadowDusk.Integration.Tests.Cli;

/// <summary>
/// <c>PATH</c> must never change which DXC or DXIL validator a compile uses.
///
/// <para><b>The defect this pins (2026-10-01).</b> <c>dxcompiler.dll</c> binds its DXIL
/// validator/signer from its <c>DllMain</c> with a bare <c>LoadLibrary("dxil.dll")</c>, and
/// ShadowDusk used to call Vortice's <c>Dxc.LoadDxil()</c> (another bare load) before DXC was
/// loaded. Our <c>dxil.dll</c> lives in <c>runtimes/win-x64/native</c>, not the application
/// directory, so that bare load walked the OS search order down to <c>PATH</c>. With the Windows
/// SDK's <c>bin</c> directory on <c>PATH</c> (every VS Developer Command Prompt) every DirectX 12
/// compile failed with "DXIL container mismatch for 'PSVRuntimeInfoSize'"; with a loadable
/// library that is not a validator, DirectX 12 output silently came out unsigned.</para>
///
/// <para>Each case runs the real CLI in a <b>fresh child process</b> (module state is
/// per-process, so an in-process test could neither reproduce nor be polluted by it) with a
/// decoy directory first on <c>PATH</c>, and requires the output to be byte-identical to the same
/// compile with a clean <c>PATH</c>, for every DXC-backed target. DirectX 12 additionally must
/// be signed. Windows only: the hijack is the Win32 DLL search order (Linux and macOS are covered
/// by <c>DxcLoader</c>'s absolute-path load and foreign-validator check, not by this test).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliDxcPathHijackTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;
    private static readonly string FixturesDir = FindFixturesDir();

    public CliDxcPathHijackTest(CliBinaryFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The decoy is a loadable library that is neither DXC nor a validator (the CLI's own
    /// <c>spirv-cross.dll</c>, renamed), placed as both <c>dxil.dll</c> and
    /// <c>dxcompiler.dll</c>. Before the fix DirectX 12 output came out unsigned with it on
    /// <c>PATH</c>.
    /// </summary>
    [WindowsTheory]
    [InlineData("DirectX_12")]
    [InlineData("DirectX_11")]
    [InlineData("OpenGL")]
    [InlineData("Vulkan")]
    public async Task DecoyDxcOnPath_DoesNotChangeTheOutput(string profile)
    {
        string decoyDir = CreateDecoyDirectory(
            Path.Combine(CliDirectory(), "runtimes", "win-x64", "native", "spirv-cross.dll"));
        try
        {
            await AssertPathDoesNotChangeOutputAsync(profile, decoyDir);
        }
        finally
        {
            TryDelete(decoyDir);
        }
    }

    /// <summary>
    /// The exact reported failure: the Windows SDK's own (newer) <c>dxil.dll</c> first on
    /// <c>PATH</c>, as in any VS Developer Command Prompt. Skipped where no SDK is installed.
    /// </summary>
    [WindowsSdkDxilFact]
    public async Task WindowsSdkBinOnPath_DirectX12StillCompilesAndSigns()
    {
        await AssertPathDoesNotChangeOutputAsync("DirectX_12", WindowsDllSearch.FindWindowsSdkBinWithDxil()!);
    }

    private async Task AssertPathDoesNotChangeOutputAsync(string profile, string pathPrefix)
    {
        string source = Path.Combine(FixturesDir, "shaders", "Grayscale.fx");
        string id = Guid.NewGuid().ToString("N");
        string clean = Path.Combine(Path.GetTempPath(), $"Grayscale_{profile}_{id}_clean.mgfx");
        string hijacked = Path.Combine(Path.GetTempPath(), $"Grayscale_{profile}_{id}_decoy.mgfx");

        try
        {
            (int cleanExit, string cleanOut, string cleanErr) =
                await RunCliAsync(source, clean, profile, pathPrefix: null);
            cleanExit.ShouldBe(0, $"clean-PATH compile failed:\n{cleanOut}\n{cleanErr}");

            (int exit, string stdout, string stderr) =
                await RunCliAsync(source, hijacked, profile, pathPrefix);
            exit.ShouldBe(0,
                $"a foreign dxil.dll/dxcompiler.dll on PATH ({pathPrefix}) broke the {profile} compile:\n{stdout}\n{stderr}");

            byte[] expected = await File.ReadAllBytesAsync(clean);
            byte[] actual = await File.ReadAllBytesAsync(hijacked);
            actual.ShouldBe(expected,
                $"PATH must not change which DXC/dxil.dll compiles {profile}: the output differs from the clean-PATH compile");

            if (profile == "DirectX_12")
                AssertEveryDxilContainerIsSigned(actual);
        }
        finally
        {
            foreach (string f in new[] { clean, hijacked })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    /// <summary>
    /// Every DXIL container in the effect carries a non-zero 16-byte hash after its <c>DXBC</c>
    /// magic: zero means <c>dxil.dll</c> never signed it, and retail D3D12 rejects it.
    /// </summary>
    private static void AssertEveryDxilContainerIsSigned(byte[] mgfx)
    {
        byte[] magic = "DXBC"u8.ToArray();
        int containers = 0;
        for (int i = mgfx.AsSpan().IndexOf(magic); i >= 0;)
        {
            containers++;
            mgfx.AsSpan(i + 4, 16).ToArray().ShouldNotBe(new byte[16],
                $"DXIL container #{containers} is unsigned: the pinned dxil.dll did not validate it");

            int next = mgfx.AsSpan(i + 4).IndexOf(magic);
            i = next < 0 ? -1 : i + 4 + next;
        }

        containers.ShouldBeGreaterThan(0, "a DirectX 12 effect must carry at least one DXIL container");
    }

    private static string CreateDecoyDirectory(string decoySource)
    {
        File.Exists(decoySource).ShouldBeTrue($"cannot build the decoy: {decoySource} not found");

        string decoyDir = Path.Combine(Path.GetTempPath(), "sd-dxc-decoy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(decoyDir);
        File.Copy(decoySource, Path.Combine(decoyDir, "dxil.dll"));
        File.Copy(decoySource, Path.Combine(decoyDir, "dxcompiler.dll"));
        return decoyDir;
    }

    private string CliDirectory() => Path.GetDirectoryName(_fixture.ExecutablePath)!;

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string sourceFile, string outputFile, string profile, string? pathPrefix)
    {
        ChildProcessResult run = await CliProcess.RunAsync(
            _fixture.ExecutablePath,
            [sourceFile, outputFile, $"/Profile:{profile}"],
            TimeSpan.FromSeconds(120),
            psi =>
            {
                if (pathPrefix is not null)
                {
                    psi.Environment["PATH"] = pathPrefix + Path.PathSeparator
                        + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
                }
            });
        return (run.ExitCode, run.Stdout, run.Stderr);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: a child that has just exited can hold the decoy briefly.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindFixturesDir()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return Path.Combine(dir.FullName, "tests", "fixtures");
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
