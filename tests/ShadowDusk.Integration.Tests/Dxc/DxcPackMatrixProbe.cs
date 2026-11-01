#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Vortice.Dxc;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The child-process half of <see cref="DxcPackMatrixTests"/> (issue #452). A
/// <c>#pragma pack_matrix</c> argument DXC rejects is an error DXC formats from state it did not
/// set, and the pinned DXC sometimes crashed the process while reporting it under parallel load,
/// which no .NET code can catch; so the compiles run out of the test host, on several threads at
/// once, and the parent reads the exit code and the verdict counts.
/// </summary>
/// <remarks>
/// Selected by <see cref="ProbeArgument"/> from <see cref="DxcConcurrencyProbe.Main"/>:
/// <c>--dxc-pack-matrix-probe &lt;DirectX12|OpenGL|Vulkan|raw&gt; &lt;threads&gt; &lt;rounds&gt;</c>.
/// A target compiles every <see cref="Cases"/> source <c>rounds</c> times on each of
/// <c>threads</c> threads through the public API and prints one <c>PACKMATRIX &lt;case&gt; &lt;verdict&gt; &lt;count&gt;</c>
/// line per distinct verdict. <c>raw</c> is the measurement aid: the same pinned DXC called
/// directly (DirectX 12 arguments, no guard) on the invalid sources, the same way, printing how
/// many compiles returned.
/// </remarks>
public static class DxcPackMatrixProbe
{
    /// <summary>The argument that selects this probe.</summary>
    public const string ProbeArgument = "--dxc-pack-matrix-probe";

    /// <summary>Stdout line prefix.</summary>
    public const string Prefix = "PACKMATRIX ";

    private const string Body =
        "float4x4 M;\nfloat4 PS(float4 c : COLOR0) : COLOR0 { return mul(c, M) * 0.5; }\n" +
        "technique T { pass P0 { PixelShader = compile ps_4_0 PS(); } }\n";

    /// <summary>The cases: name, effect.</summary>
    public static IReadOnlyList<(string Name, string Effect)> Cases { get; } =
    [
        // The reported probe (p_pragma_pack_bad.fx): an invalid argument and an entry prototype.
        ("reported", "#pragma pack_matrix(bogus)\nfloat4 PS(float4 c : COLOR0) : COLOR0;\n" +
                     "float4 PS(float4 c : COLOR0) : COLOR0 { return c * 0.5; }\n" +
                     "technique Main { pass P0 { PixelShader = compile ps_3_0 PS(); } }\n"),
        ("bogus", "#pragma pack_matrix(bogus)\n" + Body),
        ("upper", "#pragma pack_matrix(ROW_MAJOR)\n" + Body),     // crashed a sequential OpenGL compile
        ("empty", "#pragma pack_matrix()\n" + Body),
        ("macro-bogus", "#define LAYOUT bogus\n#pragma pack_matrix(LAYOUT)\n" + Body),
        ("operator", "_Pragma(\"pack_matrix(bogus)\")\n" + Body),
        // Controls DXC compiles: refused by nothing.
        ("row", "#pragma pack_matrix(row_major)\n" + Body),
        ("macro-row", "#define LAYOUT row_major\n#pragma pack_matrix(LAYOUT)\n" + Body),
        ("dead-bogus", "#if 0\n#pragma pack_matrix(bogus)\n#endif\n" + Body),
    ];

    /// <summary>Runs the probe; see the class remarks for the arguments.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out int threads) || !int.TryParse(args[2], out int rounds))
        {
            Console.Error.WriteLine($"usage: {ProbeArgument} <DirectX12|OpenGL|Vulkan|raw> <threads> <rounds>");
            return 64;
        }

        if (args[0] == "raw")
            return RunRaw(threads, rounds);

        PlatformTarget target = Enum.Parse<PlatformTarget>(args[0], ignoreCase: true);
        var verdicts = new System.Collections.Concurrent.ConcurrentDictionary<(string, string), int>();
        using var barrier = new Barrier(threads);
        Thread[] workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            for (int round = 0; round < rounds; round++)
            {
                foreach ((string name, string effect) in Cases)
                {
                    var result = new EffectCompiler().Compile(effect, new CompilerOptions { Target = target, SourceFileName = "pack.fx" });
                    string verdict = result.IsSuccess ? "OK" : $"DIAG {result.Error[0].Code} {result.Error[0].Line}:{result.Error[0].Column}";
                    verdicts.AddOrUpdate((name, verdict), 1, (_, n) => n + 1);
                }
            }
        }, maxStackSize: 16 * 1024 * 1024)).ToArray();

        foreach (Thread t in workers) t.Start();
        foreach (Thread t in workers) t.Join();

        foreach (((string name, string verdict), int count) in verdicts.OrderBy(v => v.Key.Item1, StringComparer.Ordinal))
            Console.WriteLine($"{Prefix}{name} {verdict} x{count}");
        Console.WriteLine($"{Prefix}DONE");
        return 0;
    }

    private static int RunRaw(int threads, int rounds)
    {
        if (DxcLoader.Register() is { } loadError)
        {
            Console.WriteLine($"{Prefix}RAW NODXC {loadError.Code}: {loadError.Message}");
            return 3;
        }

        IReadOnlyList<string> arguments = DxcFlagBuilder.Build(PlatformTarget.DirectX12, ShaderStage.Pixel, "PS", [], new DxcCompileOptions());
        string[] sources =
        [
            "#pragma pack_matrix(bogus)\nfloat4 PS(float4 c : COLOR0) : SV_Target { return c; }\n",
            "#pragma pack_matrix(ROW_MAJOR)\nfloat4 PS(float4 c : COLOR0) : SV_Target { return c; }\n",
        ];
        int returned = 0;
        Console.WriteLine($"{Prefix}RAW START");
        Console.Out.Flush();
        using var barrier = new Barrier(threads);
        Thread[] workers = Enumerable.Range(0, threads).Select(w => new Thread(() =>
        {
            using IDxcCompiler3 compiler = Vortice.Dxc.Dxc.CreateDxcCompiler<IDxcCompiler3>();
            barrier.SignalAndWait();
            for (int round = 0; round < rounds; round++)
            {
                using IDxcResult result = DxcNativeInterop.CompileRaw(compiler, sources[(w + round) % sources.Length], arguments, includeHandler: null);
                _ = result.GetErrors();
                Interlocked.Increment(ref returned);
            }
        }, maxStackSize: 16 * 1024 * 1024)).ToArray();
        foreach (Thread t in workers) t.Start();
        foreach (Thread t in workers) t.Join();
        Console.WriteLine($"{Prefix}RAW RETURNED {returned}");
        return 0;
    }
}
