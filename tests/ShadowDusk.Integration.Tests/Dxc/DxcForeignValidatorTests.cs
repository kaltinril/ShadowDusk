#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// What a <c>dxil.dll</c> that some OTHER code loaded into the process first does to a compile
/// (Windows). DXC binds its validator/signer to the module already loaded under that name, so
/// <c>DxcLoader</c> must:
/// <list type="bullet">
/// <item>refuse (<c>SD0219</c>) only the compiles whose output the validator decides, validated
///   DXIL, which is DirectX 12; OpenGL and Vulkan (SPIR-V) and DirectX 11 (vkd3d bytecode, its
///   DXC reflection companion runs with <c>-Vd</c>) never call it and must keep compiling, as they
///   did before the PATH-hijack fix, because the consumer cannot unload a host's DLL;</item>
/// <item>recognize its own pinned validator by CONTENT, not by path string: a byte-identical copy
///   from another directory, or the pinned file loaded through a <c>\\?\</c> path, is ours.</item>
/// </list>
/// Each case runs in a fresh child process (<see cref="DxcConcurrencyProbe"/>'s entry point), since
/// module state is per-process and the preload must come before ShadowDusk's first DXC use.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcForeignValidatorTests
{
    /// <summary>The probe argument; the next argument is the DLL to preload.</summary>
    public const string PreloadProbeArgument = "--dxil-preload-probe";

    private static readonly PlatformTarget[] Targets =
        [PlatformTarget.OpenGL, PlatformTarget.Vulkan, PlatformTarget.DirectX, PlatformTarget.DirectX12];

    private const string Fx = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif
        sampler s;
        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(s, uv) * 0.5; }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private readonly ITestOutputHelper _output;

    public DxcForeignValidatorTests(ITestOutputHelper output) => _output = output;

    [WindowsFact]
    public async Task ForeignDxilPreloaded_RefusesOnlyDirectX12()
    {
        // A loadable library that is not a validator (spirv-cross.dll), named dxil.dll.
        string decoyDir = NewTempDir();
        try
        {
            string decoy = Path.Combine(decoyDir, "dxil.dll");
            File.Copy(PinnedNative("spirv-cross.dll"), decoy);

            Dictionary<string, string> results = await RunPreloadProbeAsync(decoy);

            AssertForeignValidatorScoping(results);
        }
        finally
        {
            TryDelete(decoyDir);
        }
    }

    [WindowsSdkDxilFact]
    public async Task WindowsSdkDxilPreloaded_RefusesOnlyDirectX12()
    {
        // The real foreign validator from the report: the Windows SDK's dxil.dll 1.8.x.
        string sdkDxil = Path.Combine(WindowsDllSearch.FindWindowsSdkBinWithDxil()!, "dxil.dll");

        Dictionary<string, string> results = await RunPreloadProbeAsync(sdkDxil);

        AssertForeignValidatorScoping(results);
    }

    [WindowsFact]
    public async Task ByteIdenticalCopyOfPinnedDxilPreloaded_CompilesEveryTargetSigned()
    {
        string copyDir = NewTempDir();
        try
        {
            string copy = Path.Combine(copyDir, "dxil.dll");
            File.Copy(PinnedNative("dxil.dll"), copy);

            Dictionary<string, string> results = await RunPreloadProbeAsync(copy);

            AssertEveryTargetCompilesSigned(results);
        }
        finally
        {
            TryDelete(copyDir);
        }
    }

    [WindowsFact]
    public async Task PinnedDxilPreloadedThroughLongPathPrefix_CompilesEveryTargetSigned()
    {
        Dictionary<string, string> results =
            await RunPreloadProbeAsync(@"\\?\" + Path.GetFullPath(PinnedNative("dxil.dll")));

        AssertEveryTargetCompilesSigned(results);
    }

    private static void AssertForeignValidatorScoping(Dictionary<string, string> results)
    {
        results["OpenGL"].ShouldBe("OK", "SPIR-V never calls the validator");
        results["Vulkan"].ShouldBe("OK", "SPIR-V never calls the validator");
        results["DirectX"].ShouldBe("OK", "DX11 ships vkd3d bytecode; its DXC companion compile runs with -Vd");
        results["DirectX12"].ShouldBe("SD0219",
            "validated DXIL would be validated and signed by the foreign dxil.dll: refuse it loudly");
    }

    private static void AssertEveryTargetCompilesSigned(Dictionary<string, string> results)
    {
        foreach (PlatformTarget target in Targets)
            results[target.ToString()].ShouldBe("OK", $"{target}: the pinned validator, by content, is ours");
        results["DirectX12.signed"].ShouldBe("True", "the pinned dxil.dll must still sign DirectX 12 DXIL");
    }

    /// <summary>
    /// Child-process body: preload <paramref name="dllPath"/>, then compile every target and print
    /// one <c>Target=OK|CODE</c> line each (plus whether the DirectX 12 DXIL is signed).
    /// </summary>
    public static int RunPreloadProbe(string dllPath)
    {
        NativeLibrary.Load(dllPath);

        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in Targets)
        {
            var result = compiler.CompileAsync(Fx, new CompilerOptions
            {
                Target = target,
                SourceFileName = "preload.fx",
            }).GetAwaiter().GetResult();

            Console.WriteLine(result.IsSuccess
                ? $"{target}=OK"
                : $"{target}={result.Error[0].Code}");
            if (result.IsFailure)
            {
                foreach (ShaderError e in result.Error)
                    Console.Error.WriteLine($"{target}: {e.Code} {e.Message}");
            }

            if (target == PlatformTarget.DirectX12 && result.IsSuccess)
                Console.WriteLine($"DirectX12.signed={EveryDxilContainerIsSigned(result.Value.Data)}");
        }

        return 0;
    }

    private static bool EveryDxilContainerIsSigned(byte[] effect)
    {
        byte[] magic = "DXBC"u8.ToArray();
        int containers = 0;
        for (int i = effect.AsSpan().IndexOf(magic); i >= 0;)
        {
            containers++;
            if (effect.AsSpan(i + 4, 16).IndexOfAnyExcept((byte)0) < 0)
                return false;
            int next = effect.AsSpan(i + 4).IndexOf(magic);
            i = next < 0 ? -1 : i + 4 + next;
        }
        return containers > 0;
    }

    private async Task<Dictionary<string, string>> RunPreloadProbeAsync(string dllPath)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(PreloadProbeArgument);
        psi.ArgumentList.Add(dllPath);

        ChildProcessResult run = await ChildProcess.RunAsync(
            psi, TimeSpan.FromSeconds(120), "preload probe", captureHangEvidence: true);

        string output = run.Stdout;
        _output.WriteLine($"preload {dllPath}:\n{output}\n{run.Stderr}");
        run.ExitCode.ShouldBe(0, $"preload probe crashed:\n{output}\n{run.Stderr}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
    }

    private static string PinnedNative(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", fileName);
        File.Exists(path).ShouldBeTrue($"{path} is not beside the test assembly");
        return path;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sd-dxil-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
            // Best-effort: a child that has just exited can hold the DLL briefly.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
