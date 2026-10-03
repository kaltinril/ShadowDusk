#nullable enable

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ShadowDusk.Compiler;
using ShadowDusk.Core;

namespace ShadowDusk.Integration.Tests.NativeStack;

/// <summary>
/// The child-process half of <see cref="DeepShaderStackTests"/> (issue #306): compiles one
/// deeply nested but valid effect through the real public API and reports what happened on
/// stdout. A native stack overflow is not an exception, it ends the process, so the parent
/// test reads the exit code: 0 compiled, 2 a clean diagnostic, anything else a crash.
/// </summary>
/// <remarks>
/// Selected by <see cref="ProbeArgument"/> from <see cref="Dxc.DxcConcurrencyProbe.Main"/>:
/// <c>--deep-shader-probe &lt;shape&gt; &lt;depth&gt; &lt;target&gt; &lt;worker|worker-sync|inline&gt;</c>.
/// <c>worker</c> compiles through <c>CompileAsync</c>, <c>worker-sync</c> through the synchronous
/// <c>Compile</c>; <c>inline</c> switches <see cref="NativeCompileStack"/> off, which is the behaviour before
/// the fix and the positive control that proves the crash this guards against is real.
/// </remarks>
public static class DeepShaderProbe
{
    /// <summary>The argument that selects this probe.</summary>
    public const string ProbeArgument = "--deep-shader-probe";

    /// <summary>Stdout line prefix of the probe's verdict.</summary>
    public const string VerdictPrefix = "DEEP ";

    /// <summary>
    /// A valid one-technique pixel-shader effect whose body is <paramref name="depth"/> levels
    /// deep in the given <paramref name="shape"/>, the same shapes as the WASM stack-depth gate
    /// (<c>tests/ShadowDusk.BrowserTests/Vkd3dCorpusProbe/DepthProbe.cs</c>):
    /// <c>add</c> (an additive chain of <paramref name="depth"/> terms), <c>elseif</c>
    /// (<paramref name="depth"/> chained <c>else if</c> branches) and <c>calls</c>
    /// (<paramref name="depth"/> functions each calling the previous one).
    /// </summary>
    public static string Effect(string shape, int depth)
    {
        var sb = new StringBuilder();
        sb.Append("#if OPENGL\n#define SV_POSITION POSITION\n#define PS_SHADERMODEL ps_3_0\n#else\n#define PS_SHADERMODEL ps_4_0\n#endif\n");
        sb.Append("struct VSOut { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 TextureCoordinates : TEXCOORD0; };\n");
        if (shape == "calls")
        {
            sb.Append("float f0(float x) { return x * 1.5; }\n");
            for (int i = 1; i <= depth; i++)
                sb.Append(CultureInfo.InvariantCulture, $"float f{i}(float x) {{ return f{i - 1}(x) + 1.0; }}\n");
        }

        sb.Append("float4 MainPS(VSOut input) : COLOR0 { float2 uv = input.TextureCoordinates; ");
        sb.Append(shape switch
        {
            "add" => $"float x = {string.Join(" + ", Enumerable.Repeat("uv.x", depth))}; return float4(x,0,0,1);",
            "elseif" => "float x = 0.0; " + string.Join(" else ", Enumerable.Range(0, depth).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"if (uv.x > {i}.0) x = {i}.0;"))) + " return float4(x,0,0,1);",
            "calls" => string.Create(CultureInfo.InvariantCulture, $"return float4(f{depth}(uv.x),0,0,1);"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown depth-probe shape"),
        });
        sb.Append(" }\n");
        sb.Append("technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n");
        return sb.ToString();
    }

    /// <summary>Runs the probe; see the class remarks for the arguments and exit codes.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine($"usage: {ProbeArgument} <add|elseif|calls> <depth> <target> <worker|worker-sync|inline>");
            return 64;
        }

        string shape = args[0];
        int depth = int.Parse(args[1], CultureInfo.InvariantCulture);
        var target = Enum.Parse<PlatformTarget>(args[2], ignoreCase: true);
        string mode = args[3];
        NativeCompileStack.Enabled = mode switch
        {
            "worker" or "worker-sync" => true,
            "inline" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(args), args[3], "worker, worker-sync or inline"),
        };

        Console.WriteLine($"{VerdictPrefix}START {shape} {depth} {target} {mode}");
        Console.Out.Flush();

        // The public API exactly as a game or content build calls it. CompileAsync (a game at
        // runtime) runs the whole pipeline on one worker from a thread-pool thread; the
        // synchronous Compile (MGCB plugin, content pipeline) runs the pipeline on the calling
        // thread and hands each native call to a worker. Both must survive.
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = target, SourceFileName = "deep.fx" };
        string effect = Effect(shape, depth);
        Result<CompiledShader, ShaderError[]> result = mode == "worker-sync"
            ? compiler.Compile(effect, options)
            : compiler.CompileAsync(effect, options).GetAwaiter().GetResult();

        if (result.IsFailure)
        {
            Console.WriteLine($"{VerdictPrefix}DIAG {string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))}");
            return 2;
        }

        Console.WriteLine(
            $"{VerdictPrefix}OK bytes={result.Value.Data.Length} sha256={Convert.ToHexString(SHA256.HashData(result.Value.Data))} " +
            $"workers={NativeCompileStack.WorkersStarted}");
        return 0;
    }
}
