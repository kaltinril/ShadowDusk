#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Vortice.Dxc;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Measures, one DXC entry point at a time, which calls reach DXC's non-Windows
/// <c>setlocale</c> shims (<c>lib/DxcSupport/Unicode.cpp</c>,
/// <c>include/dxc/WinAdapter.h</c> <c>CA2W</c>/<c>CW2A</c>). Every such call must run inside
/// <see cref="DxcForkGate"/>, or it can deadlock against a concurrent <c>fork()</c> on macOS.
/// Runs in a fresh, single-threaded child process (it changes the process locale).
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
/// Output: one <c>STEP &lt;name&gt; &lt;yes|no&gt; &lt;locale after&gt;</c> line per step.
/// </para>
/// </remarks>
internal static class DxcSetlocaleAudit
{
    /// <summary>The steps that run inside <see cref="DxcForkGate"/> (the raw native compile call).</summary>
    public static readonly IReadOnlySet<string> GatedSteps = new HashSet<string>(StringComparer.Ordinal)
    {
        "signal-isolation-prime",
        "compile-native-call",
        "preprocess-native-call",
    };

    /// <summary>The step that must flip the locale for the measurement to mean anything.</summary>
    public const string PositiveControl = "compile-native-call";

    // A float4 into a float3 is an implicit-truncation WARNING, so the result carries a
    // non-empty error/warning blob for the GetErrors step to read.
    private const string Hlsl = """
        cbuffer Params { float4x4 World; float4 Tint; };
        Texture2D Tex; SamplerState Samp;
        float4 PSMain(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            float3 c = Tint;
            return float4(c, 1) * Tex.Sample(Samp, uv) * mul(p, World).x;
        }
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
        Step("compile-native-call", () => result = DxcNativeInterop.CompileRaw(
            compiler,
            Hlsl,
            DxcFlagBuilder.Build(PlatformTarget.DirectX, ShaderStage.Pixel, "PSMain", []),
            includeHandler: null));
        Step("result-status", () =>
        {
            if (result.GetStatus().Failure) throw new InvalidOperationException("audit shader failed: " + result.GetErrors());
        });
        Step("result-errors", () =>
        {
            if (string.IsNullOrEmpty(result.GetErrors())) throw new InvalidOperationException("expected a warning blob");
        });
        Step("result-object", () =>
        {
            using IDxcBlob blob = result.GetOutput(DxcOutKind.Object);
            dxil = blob.AsBytes();
        });
        Step("result-dispose", () => result.Dispose());

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
}
