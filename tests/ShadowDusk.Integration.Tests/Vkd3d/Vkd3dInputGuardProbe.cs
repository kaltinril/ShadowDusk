#nullable enable

using System.Reflection;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Vkd3d;

namespace ShadowDusk.Integration.Tests.Vkd3d;

/// <summary>
/// The child-process half of <see cref="Vkd3dInputGuardTests"/>: compiles every case through the
/// real public API for one target and prints a verdict line per case. Unguarded, the pinned
/// vkd3d-shader ended the process with an access violation on a cast to <c>void</c> (issue #448),
/// which no .NET code can catch, so this runs out of the test host and the parent reads the exit
/// code and the lines that made it out.
/// </summary>
/// <remarks>
/// Selected by <see cref="ProbeArgument"/> from <c>DxcConcurrencyProbe.Main</c>:
/// <c>--vkd3d-input-guard-probe &lt;DirectX|Fna|d3dcompiler|raw-void-cast|raw-self-macro&gt; [void|macro]</c>:
/// the cast-to-void cases (<see cref="Cases"/>, the default) or the self-referential macro cases
/// of issue #449 (<see cref="MacroCases"/>), which made vkd3d's preprocessor run forever.
/// <c>d3dcompiler</c> compiles the DirectX cases with the d3dcompiler_47 oracle (Windows).
/// <c>raw-void-cast</c> and <c>raw-self-macro</c> are the positive controls: the same pinned vkd3d
/// called with no guard, which must still crash, or still not return.
/// </remarks>
public static class Vkd3dInputGuardProbe
{
    /// <summary>The argument that selects this probe.</summary>
    public const string ProbeArgument = "--vkd3d-input-guard-probe";

    /// <summary>Stdout line prefix of each verdict.</summary>
    public const string VerdictPrefix = "VKD3DGUARD ";

    /// <summary>One case: its name and its effect body (the technique is appended per target).</summary>
    public sealed record Case(string Name, string Body);

    private const string Sampler = "sampler2D B;\n";
    private const string Ps = "float4 PS(float2 uv : TEXCOORD0) : COLOR0";

    /// <summary>The cases, in the order the probe compiles them.</summary>
    public static IReadOnlyList<Case> Cases { get; } =
    [
        // Issue #448: every cast to void measured crashed vkd3d (0xC0000005), DirectX 11 and FNA.
        new("void-tex2d", Sampler + Ps + " {\n    (void)tex2D(B, uv);\n    return float4(uv, 0, 1);\n}\n"),
        new("void-local", Ps + " {\n    float x = 1;\n    (void)x;\n    return float4(uv, 0, 1);\n}\n"),
        new("void-zero", Ps + " {\n    (void)0;\n    return float4(uv, 0, 1);\n}\n"),
        new("void-input", Ps + " {\n    (void)uv;\n    return float4(uv, 0, 1);\n}\n"),
        new("void-call", Ps + " {\n    (void)sin(uv.x);\n    return float4(uv, 0, 1);\n}\n"),
        new("void-paren", Ps + " {\n    (void)(uv.x + 1);\n    return float4(uv, 0, 1);\n}\n"),
        new("void-spaced", Sampler + Ps + " {\n    ( void ) tex2D(B, uv);\n    return float4(uv, 0, 1);\n}\n"),
        new("void-in-expression", Ps + " {\n    float y = (void)0 + 1;\n    return float4(uv, y, 1);\n}\n"),
        new("void-return", "void F(float a) { return (void)a; }\n" + Ps + " {\n    F(uv.x);\n    return float4(uv, 0, 1);\n}\n"),
        new("void-from-macro", "#define DISCARD(e) (void)(e)\n" + Ps + " {\n    DISCARD(uv.x);\n    return float4(uv, 0, 1);\n}\n"),
        new("void-type-from-macro", "#define V void\n" + Ps + " {\n    (V)uv;\n    return float4(uv, 0, 1);\n}\n"),
        // Controls: no cast reaches vkd3d, so each compiles exactly as before.
        new("param-list-void", "float F(void) { return 1; }\nfloat G(void);\nfloat G(void) { return 2; }\n" +
            Ps + " {\n    return float4(uv, F(), G());\n}\n"),
        new("void-return-type", "void F(inout float a) { a += 1; }\n" + Ps + " {\n    float a = uv.x;\n    F(a);\n    return float4(uv, a, 1);\n}\n"),
        new("void-in-dead-branch", "#if 0\n" + Ps + " {\n    (void)uv;\n    return float4(uv, 0, 1);\n}\n#else\n" +
            Ps + " {\n    return float4(uv, 0, 1);\n}\n#endif\n"),
        new("void-in-comments", Ps + " {\n    /* (void)uv; */\n    return float4(uv, 0, 1); // (void)uv;\n}\n"),
        new("void-in-unused-macro", "#define DISCARD(e) (void)(e)\n" + Ps + " {\n    return float4(uv, 0, 1);\n}\n"),
    ];

    private const string PsColor = "float4 PS(float4 c : COLOR0) : COLOR0";

    /// <summary>
    /// The issue #449 cases. Unguarded, every refused one ran until the watchdog killed it
    /// (vkd3d's preprocessor inside one <c>vkd3d_shader_compile</c> call), on FNA and on a
    /// DirectX 11 (<c>ps_4_0</c>) copy alike.
    /// </summary>
    public static IReadOnlyList<Case> MacroCases { get; } =
    [
        // The reported probes: the entry point's own name defined as a macro naming itself.
        new("fn-self-entry", "#define PS(x) PS(x)\nfloat4 PS(float4 c : COLOR0) : COLOR0;\nfloat4 PS(float4 c : COLOR0) : COLOR0 { return c * 0.5; }\n"),
        new("obj-self-entry", "#define PS PS\nfloat4 PS(float4 c : COLOR0) : COLOR0;\nfloat4 PS(float4 c : COLOR0) : COLOR0 { return c * 0.5; }\n"),
        new("fn-self", "#define F(x) F(x)\nfloat F(float x) { return x * 2; }\n" + PsColor + " {\n    return c * F(0.5);\n}\n"),
        new("obj-self-identifier", "#define K K\nstatic const float K = 0.5;\n" + PsColor + " {\n    return c * K;\n}\n"),
        new("obj-self-call", "#define G G\nfloat G(float x) { return x; }\n" + PsColor + " {\n    return c * G(0.5);\n}\n"),
        new("fn-mutual", "#define A(x) B(x)\n#define B(x) A(x)\nfloat A(float x) { return x; }\n" + PsColor + " {\n    return c * A(0.5);\n}\n"),
        new("obj-mutual", "#define A B\n#define B A\nstatic const float A = 0.5;\n" + PsColor + " {\n    return c * A;\n}\n"),
        new("obj-to-fn", "#define F G\n#define G(x) F(x)\nfloat G(float v) { return v; }\n" + PsColor + " {\n    return c * G(0.5);\n}\n"),
        new("obj-self-in-if", "#define X X\n#if X\nstatic const float K = 2;\n#else\nstatic const float K = 1;\n#endif\n" + PsColor + " { return c * K; }\n"),
        // Controls vkd3d finishes on (measured), which must still compile.
        new("fn-self-unused", "#define F(x) F(x)\n" + PsColor + " { return c * 0.5; }\n"),
        new("fn-self-name-without-call", "#define F(x) (x + F)\nstatic const float F = 1;\n" + PsColor + " { return c * F(0.5); }\n"),
        new("fn-name-from-argument", "#define ID(x) x\n" + PsColor + " { return c * ID(ID)(0.5); }\n"),
        new("obj-self-ifdef", "#define X X\n#ifdef X\nstatic const float K = 2;\n#else\nstatic const float K = 1;\n#endif\n" + PsColor + " { return c * K; }\n"),
        new("obj-self-dead", "#define X X\n#if 0\nstatic const float X = 2;\n#endif\n" + PsColor + " { return c; }\n"),
        new("nested-call", "#define F(x) ((x) + 1)\n" + PsColor + " { return c * F(F(0.5)); }\n"),
        new("alias", "#define F G\nfloat G(float x) { return x; }\n" + PsColor + " { return c * F(0.5); }\n"),
    ];

    /// <summary>The plain HLSL the raw hang control hands vkd3d.</summary>
    public const string RawSelfMacroSource =
        "#define K K\nstatic const float K = 0.5;\nfloat4 PS(float4 c : COLOR0) : SV_Target { return c * K; }\n";

    /// <summary>The source of a case on a target (the technique names the target's profile).</summary>
    public static string Effect(Case c, string target) =>
        c.Body + $"technique T {{ pass P0 {{ PixelShader = compile {(target == "Fna" ? "ps_3_0" : "ps_4_0")} PS(); }} }}\n";

    /// <summary>The plain HLSL the raw control hands vkd3d.</summary>
    public const string RawVoidCastSource =
        "float4 PS(float2 uv : TEXCOORD0) : SV_Target { (void)uv; return float4(uv, 0, 1); }\n";

    /// <summary>Runs the probe; see the class remarks for the argument.</summary>
    public static int Run(string[] args)
    {
        if (args.Length is not (1 or 2))
        {
            Console.Error.WriteLine($"usage: {ProbeArgument} <DirectX|Fna|d3dcompiler|raw-void-cast|raw-self-macro> [void|macro]");
            return 64;
        }

        string target = args[0];
        if (target == "raw-void-cast")
            return RunRaw(RawVoidCastSource);
        if (target == "raw-self-macro")
            return RunRaw(RawSelfMacroSource);
        // Measurement aid: a plain HLSL file (entry PS, ps_4_0) through the unguarded call.
        if (target == "raw-file" && args.Length == 2)
            return RunRaw(File.ReadAllText(args[1]));

        foreach (Case c in args.Length == 2 && args[1] == "macro" ? MacroCases : Cases)
        {
            Console.WriteLine($"{VerdictPrefix}{c.Name} START");
            Console.Out.Flush();

            var options = new CompilerOptions
            {
                Target = target == "Fna" ? PlatformTarget.Fna : PlatformTarget.DirectX,
                DxbcBackend = target == "d3dcompiler" ? DxbcBackend.D3DCompiler : DxbcBackend.Vkd3d,
                SourceFileName = "guard.fx",
            };
            var compiled = new EffectCompiler().Compile(Effect(c, target), options);
            Console.WriteLine(compiled.IsFailure
                ? $"{VerdictPrefix}{c.Name} DIAG {compiled.Error[0].Code} {compiled.Error[0].Line}:{compiled.Error[0].Column} {compiled.Error[0].Message.ReplaceLineEndings(" ")}"
                : $"{VerdictPrefix}{c.Name} OK");
            Console.Out.Flush();
        }

        Console.WriteLine($"{VerdictPrefix}DONE");
        return 0;
    }

    /// <summary>
    /// The pinned vkd3d-shader, called the way the backend calls it but WITHOUT the input guard:
    /// the fault the guard keeps vkd3d away from.
    /// </summary>
    private static int RunRaw(string source)
    {
        if (Vkd3dLoader.Register() is { } loadError)
        {
            Console.WriteLine($"{VerdictPrefix}RAW NOVKD3D {loadError.Code}: {loadError.Message}");
            return 3;
        }

        MethodInfo invoke = typeof(Vkd3dShaderCompiler).GetMethod("InvokeNative", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Vkd3dShaderCompiler.InvokeNative not found");
        var request = new D3DCompileRequest
        {
            HlslSource = source,
            SourceFileName = "raw.fx",
            EntryPoint = "PS",
            Stage = ShaderStage.Pixel,
        };

        Console.WriteLine($"{VerdictPrefix}RAW START");
        Console.Out.Flush();
        object? outcome = invoke.Invoke(null, [source, request, "ps_4_0", Vkd3dTargetType.DxbcTpf]);
        Console.WriteLine($"{VerdictPrefix}RAW RETURNED {outcome}");
        return 0;
    }
}
