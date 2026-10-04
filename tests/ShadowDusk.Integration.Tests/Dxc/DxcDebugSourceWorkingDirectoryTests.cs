#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.Integration.Tests.Tests;
using Vortice.Dxc;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Issue #343: a compile's output must not depend on the files in the working directory, on any
/// host. DXC's SPIR-V emitter fills <c>OpSource</c> by reading the file each name points at
/// (the main input, DXC's default <c>hlsl.hlsl</c>, and every file a <c>#line</c> directive
/// names, which for ShadowDusk includes the consumer's <c>SourceFileName</c>) wherever its
/// leaf-name self-load of <c>libdxcompiler</c> succeeds (Windows, macOS 14+, Linux with a copy
/// of the pinned build on <c>LD_LIBRARY_PATH</c>). <see cref="DxcDebugSpirvSource"/> closes it.
///
/// <para>Each scenario is a fresh child process whose working directory is either empty or
/// holds a decoy <c>hlsl.hlsl</c> and a decoy file under the exact relative
/// <c>SourceFileName</c> the compile passes. Every target's output, release and debug, must be
/// byte-identical to the clean directory's and carry no decoy text; the debug Vulkan module
/// must carry the compiled (in-memory) text. The positive control compiles the same source
/// straight through DXC with the pre-fix arguments (no input name, no normalization) and must
/// show the decoys being read wherever the leaf-name load succeeds: on Windows and macOS as the
/// host is, on Linux once a byte copy of the pinned build sits on <c>LD_LIBRARY_PATH</c>
/// (without it glibc finds nothing for the leaf and the control reads nothing, which it must
/// also report).</para>
///
/// <para>Cross-host: the debug Vulkan output's hash is pinned to the value measured on win-x64,
/// so the ubuntu and macOS lanes prove debug bytes are the same on every host (release bytes are
/// <see cref="CrossHostByteIdentityTests"/>' manifest).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcDebugSourceWorkingDirectoryTests
{
    /// <summary>The probe argument (dispatched by <see cref="DxcConcurrencyProbe"/>).</summary>
    public const string ProbeArgument = "--dxc-debug-source-cwd-probe";

    /// <summary>The relative source name every compile passes, so <c>#line</c> names a working-directory file.</summary>
    private const string SourceFileName = "cwd-probe.fx";

    /// <summary>A literal of the compiled source that survives into the debug <c>OpSource</c> text.</summary>
    private const string InMemoryMarker = "0.27182818";

    private const string HlslDecoyMarker = "SdDecoyHlslHlslText";
    private const string FxDecoyMarker = "SdDecoySourceFileText";

    /// <summary>SHA-256 of the debug Vulkan <c>.mgfx</c>, measured on win-x64; every host must match it.</summary>
    private const string ExpectedVulkanDebugSha256 = "905D1FE6969180FDC942EAE164BB7EABEDE85E0AD4503A7DDA7F285B2ACBF054";

    private const string Fx = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif
        sampler s;
        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(s, uv) * 0.27182818; }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    /// <summary>The control's source: what the pipeline hands DXC, a <c>#line</c> naming the source file first.</summary>
    private const string ControlHlsl = $$"""
        #line 1 "{{SourceFileName}}"
        float4 PS(float4 p : SV_Position) : SV_Target { return p * 0.27182818; }
        """;

    private static readonly PlatformTarget[] Targets =
        [PlatformTarget.OpenGL, PlatformTarget.Vulkan, PlatformTarget.DirectX, PlatformTarget.DirectX12];

    private readonly ITestOutputHelper _output;

    public DxcDebugSourceWorkingDirectoryTests(ITestOutputHelper output) => _output = output;

    [DxcFact]
    public async Task DebugAndReleaseOutput_DoNotDependOnFilesInTheWorkingDirectory()
    {
        Dictionary<string, List<string>> clean = await RunScenarioAsync(decoys: false, pinnedCopyOnLibraryPath: false);
        Dictionary<string, List<string>> decoy = await RunScenarioAsync(decoys: true, pinnedCopyOnLibraryPath: false);
        var scenarios = new List<(string Name, Dictionary<string, List<string>> Values)> { ("clean", clean), ("decoy", decoy) };
        if (OperatingSystem.IsLinux())
            scenarios.Add(("decoy+pinned-copy-on-LD_LIBRARY_PATH", await RunScenarioAsync(decoys: true, pinnedCopyOnLibraryPath: true)));

        foreach ((string name, Dictionary<string, List<string>> values) in scenarios)
        {
            foreach (PlatformTarget target in Targets)
            {
                foreach (string mode in new[] { "release", "debug" })
                {
                    string key = $"{target}.{mode}";
                    values[key].ShouldBe(["OK"], $"[{name}] {key}: {values.GetValueOrDefault("message." + key)?[0]}");
                    values[key + ".decoy"].ShouldBe(["none"], $"[{name}] {key} carries text read from a file in the working directory");
                    values[key + ".sha256"].ShouldBe(clean[key + ".sha256"], $"[{name}] {key} differs from the clean working directory's output");
                }
            }

            values["Vulkan.debug.memory"].ShouldBe(["True"], $"[{name}] the debug Vulkan OpSource must be the compiled (in-memory) source");
            values["Vulkan.debug.inputName"].ShouldBe(["True"], $"[{name}] the debug Vulkan module does not name the unopenable input");
        }

        // The positive control: the pre-fix compile really reads both decoys where the leaf-name
        // load succeeds, so the assertions above guard against something this host does.
        bool leafLoadSucceedsAsIs = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        clean["control"].ShouldBe(["none"], "[clean] nothing to read, nothing read");
        decoy["control"].ShouldBe([leafLoadSucceedsAsIs ? "hlsl+fx" : "none"],
            leafLoadSucceedsAsIs
                ? "[decoy] DXC's pre-fix compile did not read hlsl.hlsl and the #line file from the working directory, so this test no longer shows the mechanism it guards"
                : "[decoy] glibc should find nothing for DXC's leaf-name load, so DXC cannot read any file");
        if (OperatingSystem.IsLinux())
        {
            scenarios[2].Values["control"].ShouldBe(["hlsl+fx"],
                "[decoy+pinned-copy] with the pinned build reachable by leaf name, DXC's pre-fix compile must read both decoys");
        }

        // Each half of the fix is needed: the input name alone stops the hlsl.hlsl read but not
        // the read of the file the #line directive names, which only the normalization removes.
        foreach ((string name, Dictionary<string, List<string>> values) in scenarios)
        {
            string expected = values["control"][0] == "hlsl+fx" ? "fx" : "none";
            values["control.inputNameOnly"].ShouldBe([expected], $"[{name}] the input name alone");
        }

        string vulkanDebug = clean["Vulkan.debug.sha256"].ShouldHaveSingleItem();
        _output.WriteLine($"Vulkan debug sha256 = {vulkanDebug}");
        vulkanDebug.ShouldBe(ExpectedVulkanDebugSha256, "the debug Vulkan bytes differ from win-x64's: debug output is not host-independent");
    }

    /// <summary>
    /// A DXC diagnostic located in the macro prelude (before its <c>#line 1</c>) names the
    /// input file. Debug SPIR-V compiles pass DXC <see cref="DxcDebugSpirvSource.InputName"/>,
    /// and the reported location must still be the one every other compile reports
    /// (<c>hlsl.hlsl</c>), with DXC's own text kept verbatim in <c>RawDiagnostics</c>. The
    /// repro is the review's: <c>/Defines:1X=2;FOO=1</c>, so DXC rejects <c>#define 1X 2</c>.
    /// </summary>
    [DxcFact]
    public async Task PreludeDiagnostic_ReportsTheSameLocation_InDebugAndRelease()
    {
        var compiler = new EffectCompiler();
        var errors = new Dictionary<(PlatformTarget, bool), ShaderError>();
        foreach (PlatformTarget target in new[] { PlatformTarget.Vulkan, PlatformTarget.OpenGL, PlatformTarget.DirectX12 })
        {
            foreach (bool debug in new[] { false, true })
            {
                var result = await compiler.CompileAsync(Fx, new CompilerOptions
                {
                    Target = target,
                    Debug = debug,
                    SourceFileName = "usedef.fx",
                    Defines = [new ShadowDusk.Core.Preprocessor.UserDefine("1X", "2"), new ShadowDusk.Core.Preprocessor.UserDefine("FOO", "1")],
                });
                result.IsFailure.ShouldBeTrue($"{target} debug={debug}: an invalid macro name must fail");
                errors[(target, debug)] = result.Error[0];
            }
        }

        // Each target's prelude differs (its platform macros), so each debug compile is compared
        // with its own target's release compile.
        foreach (PlatformTarget target in new[] { PlatformTarget.Vulkan, PlatformTarget.OpenGL, PlatformTarget.DirectX12 })
        {
            ShaderError release = errors[(target, false)];
            ShaderError debug = errors[(target, true)];
            _output.WriteLine($"{target}: release {release.File}({release.Line},{release.Column}), debug {debug.File}({debug.Line},{debug.Column}): {debug.Code}: {debug.Message}");
            release.File.ShouldBe(DxcDebugSpirvSource.DefaultInputName, $"{target} release");
            release.Message.ShouldContain("macro name must be an identifier", Case.Sensitive);
            (debug.File, debug.Line, debug.Column, debug.Code, debug.Message)
                .ShouldBe((release.File, release.Line, release.Column, release.Code, release.Message), $"{target}: debug reports a different location than release");
        }

        // The compiler's own words stay verbatim: on Vulkan the failing compile IS the debug SPIR-V
        // one (OpenGL fails earlier, in a compile that keeps DXC's default name), so its raw text
        // names the input DXC saw, and the location above was mapped from it.
        errors[(PlatformTarget.Vulkan, true)].RawDiagnostics.ShouldNotBeNull().ShouldContain(DxcDebugSpirvSource.InputName, Case.Sensitive);
        errors[(PlatformTarget.Vulkan, false)].RawDiagnostics.ShouldNotBeNull().ShouldNotContain(DxcDebugSpirvSource.InputName, Case.Sensitive);
    }

    /// <summary>
    /// Child-process body: compiles every target in release and debug from the process's
    /// working directory, printing <c>Target.mode=OK|CODE</c>, its hash, which decoy texts the
    /// output carries, whether the debug Vulkan output carries the in-memory text, then the
    /// pre-fix control compile's reads.
    /// </summary>
    public static int RunProbe()
    {
        var compiler = new EffectCompiler();
        foreach (PlatformTarget target in Targets)
        {
            foreach (bool debug in new[] { false, true })
            {
                string key = $"{target}.{(debug ? "debug" : "release")}";
                var result = compiler.CompileAsync(Fx, new CompilerOptions
                {
                    Target = target,
                    Debug = debug,
                    SourceFileName = SourceFileName,
                }).GetAwaiter().GetResult();

                Console.WriteLine(result.IsSuccess ? $"{key}=OK" : $"{key}={result.Error[0].Code}");
                if (result.IsFailure)
                {
                    Console.WriteLine($"message.{key}={result.Error[0].Message.ReplaceLineEndings(" ")}");
                    continue;
                }

                byte[] data = result.Value.Data;
                Console.WriteLine($"{key}.sha256={Convert.ToHexString(SHA256.HashData(data))}");
                Console.WriteLine($"{key}.decoy={DecoysIn(data)}");
                if (target == PlatformTarget.Vulkan && debug)
                {
                    Console.WriteLine($"{key}.memory={Contains(data, InMemoryMarker)}");
                    Console.WriteLine($"{key}.inputName={Contains(data, DxcDebugSpirvSource.InputName)}");
                }
            }
        }

        Console.WriteLine($"control={DecoysIn(DebugSpirv(withInputName: false))}");
        Console.WriteLine($"control.inputNameOnly={DecoysIn(DebugSpirv(withInputName: true))}");
        return 0;
    }

    /// <summary>
    /// The pipeline's Vulkan pixel compile with debug information straight through DXC, its bytes
    /// never normalized: without the input name it is exactly the pre-fix compile; with it, it
    /// shows what the input name alone leaves (the <c>#line</c> file's read).
    /// </summary>
    private static byte[] DebugSpirv(bool withInputName)
    {
        DxcLoader.Register().ShouldBeNull("the pinned DXC must load");
        List<string> arguments = DxcFlagBuilder.Build(
            PlatformTarget.Vulkan, ShaderStage.Pixel, "PS", [], new DxcCompileOptions { EmbedDebugInfo = true }).ToList();
        arguments.ShouldContain(DxcDebugSpirvSource.InputName, "the debug SPIR-V arguments no longer carry the input name");
        if (!withInputName)
            arguments.Remove(DxcDebugSpirvSource.InputName);

        using IDxcCompiler3 compiler = Vortice.Dxc.Dxc.CreateDxcCompiler<IDxcCompiler3>();
        using IDxcResult result = DxcNativeInterop.Compile(compiler, ControlHlsl, arguments, includeHandler: null);
        result.GetStatus().Success.ShouldBeTrue(result.GetErrors());
        using IDxcBlob blob = result.GetOutput(DxcOutKind.Object);
        return blob.AsBytes();
    }

    private static string DecoysIn(byte[] data) =>
        (Contains(data, HlslDecoyMarker), Contains(data, FxDecoyMarker)) switch
        {
            (true, true) => "hlsl+fx",
            (true, false) => "hlsl",
            (false, true) => "fx",
            _ => "none",
        };

    private static bool Contains(byte[] data, string marker) =>
        data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0;

    private async Task<Dictionary<string, List<string>>> RunScenarioAsync(bool decoys, bool pinnedCopyOnLibraryPath)
    {
        string workingDirectory = Path.Combine(Path.GetTempPath(), "sd-dxc-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        string? libraryDirectory = null;
        try
        {
            if (decoys)
            {
                // Valid HLSL in each, so DXC's read succeeds wherever its load does.
                File.WriteAllText(Path.Combine(workingDirectory, "hlsl.hlsl"),
                    $"// {HlslDecoyMarker}\nfloat4 main() : SV_Target {{ return 0; }}\n");
                File.WriteAllText(Path.Combine(workingDirectory, SourceFileName),
                    $"// {FxDecoyMarker}\nfloat4 PS() : COLOR0 {{ return 1; }}\ntechnique T {{ pass P {{ PixelShader = compile ps_3_0 PS(); }} }}\n");
            }

            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                WorkingDirectory = workingDirectory,
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
            psi.ArgumentList.Add(ProbeArgument);

            if (pinnedCopyOnLibraryPath)
            {
                string rid = DxcLoader.PinnedRid("linux", RuntimeInformation.ProcessArchitecture);
                string pinned = Path.Combine(ForeignDxc.NativeDirectory(rid), "libdxcompiler.so");
                File.Exists(pinned).ShouldBeTrue($"{pinned} is not beside the test assembly");
                libraryDirectory = Path.Combine(Path.GetTempPath(), "sd-dxc-cwd-lib-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(libraryDirectory);
                File.Copy(pinned, Path.Combine(libraryDirectory, "libdxcompiler.so"));
                string? inherited = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(inherited) ? libraryDirectory : libraryDirectory + ":" + inherited;
            }

            ChildProcessResult run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(180), "debug-source working-directory probe", captureHangEvidence: true);
            _output.WriteLine($"cwd={workingDirectory} decoys={decoys} pinnedCopyOnLibraryPath={pinnedCopyOnLibraryPath}\n{run.Stdout}");
            run.ExitCode.ShouldBe(0, $"probe failed:\n{run.Stdout}\n{run.Stderr}");

            return run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .GroupBy(kv => kv[0], kv => kv[1], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        }
        finally
        {
            TryDelete(workingDirectory);
            if (libraryDirectory is not null)
                TryDelete(libraryDirectory);
        }
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
