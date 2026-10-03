#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Vortice.Dxc;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Measures, one DXC entry point at a time, which calls reach DXC's non-Windows
/// <c>setlocale</c> shims (<c>lib/DxcSupport/Unicode.cpp</c>,
/// <c>include/dxc/WinAdapter.h</c> <c>CA2W</c>/<c>CW2A</c>), and then that once the locale
/// has settled no DXC call changes it again. The first half pins which calls can change the
/// locale at all (only the native compile calls); the second half is the premise of
/// <see cref="DxcForkGate"/>: after the one gated change, every <c>setlocale</c> DXC makes is
/// a same-name call, which Apple's libc answers without allocating under the locale lock, so
/// an ungated compile cannot deadlock against a concurrent <c>fork()</c>. Runs in a fresh,
/// single-threaded child process (it changes the process locale).
/// </summary>
/// <remarks>
/// <para>
/// The detector rides on a DXC bug: each shim does
/// <c>locale = setlocale(LC_ALL, "en_US.UTF-8"); ...; setlocale(LC_ALL, locale);</c>, and
/// since <c>setlocale</c> returns the NEW locale's name, the "restore" sets the new locale
/// again. So after any shim call the process locale stays DXC's UTF-8 locale. Reset the
/// locale to <c>C</c>, run one step, read it back: a changed locale means that step called
/// <c>setlocale</c>. The raw compile step is the positive control: if it does not flip the
/// locale (DXC fixed the restore, or the host lacks the en_US UTF-8 locale) the detector is
/// blind and the test fails instead of passing vacuously.
/// </para>
/// <para>
/// Output: one <c>STEP &lt;name&gt; &lt;yes|no&gt; &lt;locale after&gt;</c> line per step of the
/// first half, then <c>SETTLED &lt;locale&gt;</c> and one <c>STEADY &lt;name&gt; &lt;locale after&gt;</c>
/// line per step of the second half, which never resets the locale.
/// </para>
/// </remarks>
internal static class DxcSetlocaleAudit
{
    /// <summary>
    /// The steps that may change the locale from <c>C</c>: the raw native compile calls, where
    /// DXC's shims live, and the two primes that exist to make that change under the fork gate.
    /// Any other step changing it would be a new DXC entry point that calls <c>setlocale</c>,
    /// to be measured before it is trusted.
    /// </summary>
    public static readonly IReadOnlySet<string> LocaleChangingSteps = new HashSet<string>(StringComparer.Ordinal)
    {
        "signal-isolation-prime",
        "locale-settle",
        "compile-native-call",
        "failing-compile-native-call",
        "preprocess-native-call",
    };

    /// <summary>The step that must flip the locale for the measurement to mean anything.</summary>
    public const string PositiveControl = "compile-native-call";

    private const string Hlsl = """
        cbuffer Params { float4x4 World; float4 Tint; };
        Texture2D Tex; SamplerState Samp;
        float4 PSMain(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Tint * Tex.Sample(Samp, uv) * mul(p, World).x;
        }
        """;

    // An undeclared identifier: a failed compile with a non-empty diagnostic blob, so the
    // GetErrors step reads real text (the success path's blob is empty).
    private const string BrokenHlsl = """
        float4 PSMain() : SV_Target { return missingIdentifier; }
        """;

    public static int Run()
    {
        if (!ProbeLibc.SetLocale("C") || ProbeLibc.CurrentLocale() != "C")
        {
            Console.Error.WriteLine("cannot set the C locale: " + ProbeLibc.CurrentLocale());
            return 3;
        }

        DxcLoader.Register();
        IDxcCompiler3 compiler = null!;
        IDxcResult result = null!;
        byte[] dxil = [];

        Step("create-compiler", () => compiler = Vortice.Dxc.Dxc.CreateDxcCompiler<IDxcCompiler3>());
        Step("signal-isolation-prime", () => DxcSignalIsolation.EnsureIsolated(compiler));
        Step("locale-settle", () => DxcForkGate.SettleLocale(compiler));
        Step("compile-native-call", () => result = DxcNativeInterop.CompileRaw(
            compiler,
            Hlsl,
            DxcFlagBuilder.Build(PlatformTarget.DirectX, ShaderStage.Pixel, "PSMain", []),
            includeHandler: null));
        Step("result-status", () =>
        {
            if (result.GetStatus().Failure) throw new InvalidOperationException("audit shader failed: " + result.GetErrors());
        });
        Step("result-errors", () => result.GetErrors());
        Step("result-object", () =>
        {
            using IDxcBlob blob = result.GetOutput(DxcOutKind.Object);
            dxil = blob.AsBytes();
        });
        Step("result-dispose", () => result.Dispose());

        Step("failing-compile-native-call", () => result = DxcNativeInterop.CompileRaw(
            compiler,
            BrokenHlsl,
            DxcFlagBuilder.Build(PlatformTarget.DirectX, ShaderStage.Pixel, "PSMain", []),
            includeHandler: null));
        Step("failing-result-errors", () =>
        {
            if (!result.GetStatus().Failure) throw new InvalidOperationException("the broken shader compiled");
            if (!result.GetErrors().Contains("missingIdentifier", StringComparison.Ordinal))
                throw new InvalidOperationException("expected DXC's diagnostic text");
            result.Dispose();
        });

        Step("preprocess-native-call", () => result = DxcNativeInterop.CompileRaw(
            compiler, Hlsl, DxcFlagBuilder.BuildPreprocess([]), includeHandler: null));
        Step("preprocess-output", () =>
        {
            using IDxcBlob blob = result.GetOutput(DxcOutKind.Hlsl);
            if (blob.AsBytes().Length == 0) throw new InvalidOperationException("empty preprocess output");
            result.Dispose();
        });

        // The whole reflection path the OpenGL pipeline runs: DxcCreateInstance(IDxcUtils),
        // CreateBlobFromPinned, IDxcUtils::CreateReflection, every reflection getter, Release.
        Step("reflection-extract", () =>
        {
            Result<ShadowDusk.Core.Reflection.ReflectedEffect, ShaderError> reflected = new DxilReflectionExtractor().Extract(dxil);
            if (reflected.IsFailure) throw new InvalidOperationException(reflected.Error.FxcFormattedMessage);
        });

        Step("compiler-dispose", () => compiler.Dispose());

        // Second half: settle once, then run every entry point WITHOUT resetting the locale.
        // The locale must read the same after every step, or some compile makes a
        // locale-changing setlocale call and the ungated compile path is not safe against fork().
        compiler = Vortice.Dxc.Dxc.CreateDxcCompiler<IDxcCompiler3>();
        DxcForkGate.SettleLocale(compiler);
        Console.WriteLine($"SETTLED {ProbeLibc.CurrentLocale()}");

        Steady("compile-native-call", () => result = DxcNativeInterop.CompileRaw(
            compiler, Hlsl, DxcFlagBuilder.Build(PlatformTarget.DirectX, ShaderStage.Pixel, "PSMain", []), includeHandler: null));
        Steady("result-object", () =>
        {
            using IDxcBlob blob = result.GetOutput(DxcOutKind.Object);
            dxil = blob.AsBytes();
            result.Dispose();
        });
        Steady("spirv-compile-native-call", () =>
        {
            using IDxcResult spirv = DxcNativeInterop.CompileRaw(
                compiler, Hlsl, DxcFlagBuilder.Build(PlatformTarget.OpenGL, ShaderStage.Pixel, "PSMain", []), includeHandler: null);
            if (spirv.GetStatus().Failure) throw new InvalidOperationException(spirv.GetErrors());
        });
        // The issue #312 shape: debug information on a SPIR-V target (the compile that dlopens).
        Steady("debug-spirv-compile-native-call", () =>
        {
            using IDxcResult spirv = DxcNativeInterop.CompileRaw(
                compiler,
                Hlsl,
                DxcFlagBuilder.Build(PlatformTarget.Vulkan, ShaderStage.Pixel, "PSMain", [], new DxcCompileOptions { EmbedDebugInfo = true }),
                includeHandler: null);
            if (spirv.GetStatus().Failure) throw new InvalidOperationException(spirv.GetErrors());
        });
        Steady("failing-compile-native-call", () =>
        {
            using IDxcResult failing = DxcNativeInterop.CompileRaw(
                compiler, BrokenHlsl, DxcFlagBuilder.Build(PlatformTarget.DirectX, ShaderStage.Pixel, "PSMain", []), includeHandler: null);
            if (!failing.GetStatus().Failure) throw new InvalidOperationException("the broken shader compiled");
            failing.GetErrors();
        });
        Steady("preprocess-native-call", () =>
        {
            using IDxcResult pre = DxcNativeInterop.CompileRaw(compiler, Hlsl, DxcFlagBuilder.BuildPreprocess([]), includeHandler: null);
            using IDxcBlob blob = pre.GetOutput(DxcOutKind.Hlsl);
            if (blob.AsBytes().Length == 0) throw new InvalidOperationException("empty preprocess output");
        });
        Steady("reflection-extract", () =>
        {
            Result<ShadowDusk.Core.Reflection.ReflectedEffect, ShaderError> reflected = new DxilReflectionExtractor().Extract(dxil);
            if (reflected.IsFailure) throw new InvalidOperationException(reflected.Error.FxcFormattedMessage);
        });
        Steady("compiler-dispose", () => compiler.Dispose());
        return 0;
    }

    private static void Step(string name, Action action)
    {
        if (!ProbeLibc.SetLocale("C"))
            throw new InvalidOperationException("cannot reset the C locale");

        action();

        string after = ProbeLibc.CurrentLocale();
        Console.WriteLine($"STEP {name} {(after == "C" ? "no" : "yes")} {after}");
    }

    private static void Steady(string name, Action action)
    {
        action();
        Console.WriteLine($"STEADY {name} {ProbeLibc.CurrentLocale()}");
    }
}
