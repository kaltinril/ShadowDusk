#nullable enable

using System.Security.Cryptography;
using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Vortice.Dxc;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The child-process half of <see cref="EntryPointRedeclarationTests"/>: compiles every
/// entry-point-prototype case through the real public API for one target and prints a verdict
/// line per case. Before the fix the first such compile on a SPIR-V target ended the process
/// with an access violation inside DXC, which no .NET code can catch, so this runs out of the
/// test host and the parent reads the exit code and the lines that made it out.
/// </summary>
/// <remarks>
/// Selected by <see cref="ProbeArgument"/> from <see cref="DxcConcurrencyProbe.Main"/>:
/// <c>--entry-redeclaration-probe &lt;OpenGL|Vulkan|DirectX|DirectX12|Fna|raylib|sksl|raw-dxc&gt;</c>.
/// <c>raw-dxc</c> is the positive control: the same pinned DXC, called directly with the plain
/// prototype source and the OpenGL SPIR-V arguments, which must still crash, so the cases
/// passing proves the fix and not a DXC that stopped faulting.
/// </remarks>
public static class EntryPointRedeclarationProbe
{
    /// <summary>The argument that selects this probe.</summary>
    public const string ProbeArgument = "--entry-redeclaration-probe";

    /// <summary>Stdout line prefix of each verdict.</summary>
    public const string VerdictPrefix = "REDECL ";

    /// <summary>The source file name every case compiles under (the Vulkan container records it).</summary>
    public const string SourceName = "redecl.fx";

    /// <summary>One case: its effect, and the effect it must compile to exactly (null: no twin).</summary>
    /// <param name="Name">Case identifier printed in the verdict.</param>
    /// <param name="Effect">The effect, with its entry point declared more than once (or a control).</param>
    /// <param name="Twin">The same effect with the prototypes replaced by blank lines, or null.</param>
    /// <param name="PixelOnly">True when the converters (raylib, SkSL: one pixel pass) can take it.</param>
    public sealed record Case(string Name, string Effect, string? Twin, bool PixelOnly);

    private const string Prelude =
        "#if SM4 || SM6\n#define VS_SM vs_4_0_level_9_1\n#define PS_SM ps_4_0_level_9_1\n#define POS_OUT SV_Position\n" +
        "#else\n#define VS_SM vs_3_0\n#define PS_SM ps_3_0\n#define POS_OUT POSITION0\n#endif\n";

    private const string PsProto = "float4 PS(float4 c : COLOR0) : COLOR0;";
    private const string PsDef = "float4 PS(float4 c : COLOR0) : COLOR0 { return c * float4(1, 0.5, 0.25, 1); }";
    private const string PsDefSm6 = "float4 PS(float4 c : COLOR0) : COLOR0 { return c.bgra; }";
    private const string VsProto = "float4 VS(float4 p : POSITION0) : POS_OUT;";
    private const string VsDef = "float4 VS(float4 p : POSITION0) : POS_OUT { return mul(p, WVP); }";
    private const string Wvp = "float4x4 WVP;";
    private const string TechPs = "technique T { pass { PixelShader = compile PS_SM PS(); } }";
    private const string TechVsPs = "technique T { pass { VertexShader = compile VS_SM VS(); PixelShader = compile PS_SM PS(); } }";
    private const string Half = "float4 Half(float4 c : COLOR0) : COLOR0 { return PS(c) * 0.5; }";
    private const string Overload = "float4 PS(float2 uv : TEXCOORD0) : COLOR0;";

    private static string Fx(params string[] lines) => Prelude + string.Join('\n', lines) + "\n";

    /// <summary>The cases, in the order the probe compiles them.</summary>
    public static IReadOnlyList<Case> Cases { get; } =
    [
        // The reported shape: a pixel entry point's prototype before the technique, the definition after it.
        new("ps-proto-before-technique", Fx(PsProto, TechPs, PsDef), Fx("", TechPs, PsDef), true),
        new("ps-proto-and-def-before-technique", Fx(PsProto, PsDef, TechPs), Fx("", PsDef, TechPs), true),
        new("ps-proto-after-def", Fx(PsDef, PsProto, TechPs), Fx(PsDef, "", TechPs), true),
        new("ps-two-protos", Fx(PsProto, PsProto, TechPs, PsDef), Fx("", "", TechPs, PsDef), true),
        // One definition per shader-model branch (the VsTransformColorTexture layout): DXC sees one.
        new("ps-proto-def-per-branch", Fx(PsProto, TechPs, "#if SM6", PsDefSm6, "#else", PsDef, "#endif"),
            Fx("", TechPs, "#if SM6", PsDefSm6, "#else", PsDef, "#endif"), true),
        // The vertex variant, and both stages at once.
        new("vs-proto-before-technique", Fx(Wvp, VsProto, PsDef, TechVsPs, VsDef), Fx(Wvp, "", PsDef, TechVsPs, VsDef), false),
        new("vs-and-ps-protos-before-technique", Fx(Wvp, VsProto, PsProto, TechVsPs, PsDef, VsDef), Fx(Wvp, "", "", TechVsPs, PsDef, VsDef), false),
        // An overload prototype with no definition of its own, of an entry nothing calls (fxc compiles past it).
        new("ps-unused-overload-proto", Fx(Overload, PsProto, TechPs, PsDef), Fx("", "", TechPs, PsDef), true),
        // Controls that never declared the entry twice: a helper's prototype is untouched.
        new("helper-proto", Fx("float4 Tint(float4 c);", TechPs,
            "float4 PS(float4 c : COLOR0) : COLOR0 { return Tint(c); }", "float4 Tint(float4 c) { return c * 0.5; }"), null, true),
        // Prototype without a definition: a clean error everywhere (fxc: X3501), never a crash.
        new("ps-proto-without-definition", Fx(PsProto, TechPs), null, true),
        // A prototype differing only in its return type: a clean error (fxc: X3068).
        new("ps-proto-return-type-differs", Fx("float3 PS(float4 c : COLOR0) : COLOR0;", TechPs, PsDef), null, true),
        // Shapes SPIR-V cannot be given safely: refused with SD0236 instead of crashing.
        new("ps-called-between-proto-and-def", Fx(PsProto, Half, TechPs, PsDef), null, true),
        new("ps-overload-proto-of-called-entry", Fx(Overload, PsDef, Half, TechPs), null, true),
        new("ps-proto-from-macro", Fx("#define DECLARE(name) float4 name(float4 c : COLOR0) : COLOR0;", "DECLARE(PS)", TechPs, PsDef), null, true),
    ];

    /// <summary>The plain prototype-then-definition HLSL the raw-DXC control compiles.</summary>
    public const string RawControlSource =
        "float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) : SV_Target { return c; }\n";

    /// <summary>Runs the probe; see the class remarks for the argument.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine($"usage: {ProbeArgument} <OpenGL|Vulkan|DirectX|DirectX12|Fna|raylib|sksl|raw-dxc>");
            return 64;
        }

        string target = args[0];
        if (target == "raw-dxc")
            return RunRawControl();

        foreach (Case c in Cases)
        {
            if (target is "raylib" or "sksl" && !c.PixelOnly)
                continue;

            Console.WriteLine($"{VerdictPrefix}{c.Name} START");
            Console.Out.Flush();

            Result<string, ShaderError[]> compiled = Compile(target, c.Effect);
            if (compiled.IsFailure)
            {
                ShaderError first = compiled.Error[0];
                Console.WriteLine($"{VerdictPrefix}{c.Name} DIAG {first.Code} {first.Line}:{first.Column} {first.Message.ReplaceLineEndings(" ")}");
                continue;
            }

            string twin = "NOTWIN";
            if (c.Twin is not null)
            {
                Result<string, ShaderError[]> expected = Compile(target, c.Twin);
                twin = expected.IsFailure
                    ? "TWINFAILED " + expected.Error[0].Code
                    : string.Equals(expected.Value, compiled.Value, StringComparison.Ordinal) ? "SAME" : "DIFF";
            }
            Console.WriteLine($"{VerdictPrefix}{c.Name} OK {twin}");
            Console.Out.Flush();
        }

        Console.WriteLine($"{VerdictPrefix}DONE");
        return 0;
    }

    /// <summary>The compiled output as a comparable string (hex for binary containers).</summary>
    private static Result<string, ShaderError[]> Compile(string target, string effect)
    {
        switch (target)
        {
            case "raylib":
            {
                var r = RaylibConverter.Convert(effect, new RaylibConvertOptions { SourceName = SourceName });
                return r.IsFailure ? Result<string, ShaderError[]>.Fail(r.Error) : Result<string, ShaderError[]>.Ok(r.Value.FragmentShader);
            }
            case "sksl":
            {
                var s = SkslConverter.Convert(effect, new SkslConvertOptions { SourceName = SourceName });
                return s.IsFailure ? Result<string, ShaderError[]>.Fail(s.Error) : Result<string, ShaderError[]>.Ok(s.Value.SkslText);
            }
            default:
            {
                var options = new CompilerOptions { Target = Enum.Parse<PlatformTarget>(target, ignoreCase: true), SourceFileName = SourceName };
                var m = new EffectCompiler().Compile(effect, options);
                return m.IsFailure
                    ? Result<string, ShaderError[]>.Fail(m.Error)
                    : Result<string, ShaderError[]>.Ok(Convert.ToHexString(SHA256.HashData(m.Value.Data)) + ":" + m.Value.Data.Length);
            }
        }
    }

    /// <summary>
    /// The pinned DXC, called with the OpenGL pixel SPIR-V arguments on a prototype-then-definition
    /// source, without ShadowDusk's input preparation: the fault the fix keeps DXC away from.
    /// </summary>
    private static int RunRawControl()
    {
        if (DxcLoader.Register() is { } loadError)
        {
            Console.WriteLine($"{VerdictPrefix}RAW NODXC {loadError.Code}: {loadError.Message}");
            return 3;
        }

        using IDxcCompiler3 compiler = Vortice.Dxc.Dxc.CreateDxcCompiler<IDxcCompiler3>();
        IReadOnlyList<string> arguments = DxcFlagBuilder.Build(PlatformTarget.OpenGL, ShaderStage.Pixel, "PS", [], new DxcCompileOptions());

        Console.WriteLine($"{VerdictPrefix}RAW START {string.Join(' ', arguments)}");
        Console.Out.Flush();

        using IDxcResult result = DxcNativeInterop.CompileRaw(compiler, RawControlSource, arguments, includeHandler: null);
        Console.WriteLine($"{VerdictPrefix}RAW RETURNED status=0x{result.GetStatus().Code:X8} {result.GetErrors().ReplaceLineEndings(" ")}");
        return 0;
    }
}
