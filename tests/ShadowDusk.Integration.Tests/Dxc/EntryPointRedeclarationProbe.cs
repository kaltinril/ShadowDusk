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
        new("ps-proto-from-macro", Fx("#define DECLARE(name) float4 name(float4 c : COLOR0) : COLOR0;", "DECLARE(PS)", TechPs, PsDef),
            Fx("#define DECLARE(name) float4 name(float4 c : COLOR0) : COLOR0;", "", TechPs, PsDef), true),

        // Review of 2026-10-28. A macro call without a `;` of its own right before the prototype
        // must survive the blanking (it was wiped with it: a parameter silently lost on GL/VK).
        new("macro-call-before-proto", Fx("#define DECL_PARAM(n) float4 n;", "DECL_PARAM(Unused)", PsProto, TechPs, PsDefUnused),
            Fx("#define DECL_PARAM(n) float4 n;", "DECL_PARAM(Unused)", "", TechPs, PsDefUnused), true),
        // Shapes that still reached DXC and crashed it.
        new("two-protos-one-line", Fx(PsProto + " " + PsProto, TechPs, PsDef), Fx("", TechPs, PsDef), true),
        new("typedef-return-proto", Fx("typedef float4 F4;", "F4 PS(float4 c : COLOR0) : COLOR0;", TechPs, PsDef),
            Fx("typedef float4 F4;", "", TechPs, PsDef), true),
        new("vector-return-proto", Fx("vector<float, 4> PS(float4 c : COLOR0) : COLOR0;", TechPs, PsDef), Fx("", TechPs, PsDef), true),
        new("comment-in-proto", Fx("float4 /* a */ PS /* b */ (float4 c : COLOR0) : COLOR0;", TechPs, PsDef), Fx("", TechPs, PsDef), true),
        new("conditional-semantic-proto", Fx("float4 PS(float4 c : COLOR0)", "#if SM4", " : SV_Target", "#else", " : COLOR0", "#endif", ";", TechPs, PsDef),
            Fx("", "", "", "", "", "", "", TechPs, PsDef), true),
        new("const-param-proto-called", Fx("float4 PS(const float4 c : COLOR0) : COLOR0;", TechPs, PsDef, Half), Fx("", TechPs, PsDef, Half), true),
        new("macro-named-entry-proto", Fx("#define PF PSX", "float4 PF(float4 c : COLOR0) : COLOR0;",
            "technique T { pass { PixelShader = compile PS_SM PF(); } }", "float4 PF(float4 c : COLOR0) : COLOR0 { return c; }"),
            Fx("#define PF PSX", "", "technique T { pass { PixelShader = compile PS_SM PF(); } }", "float4 PF(float4 c : COLOR0) : COLOR0 { return c; }"), true),

        // Re-review of 2026-10-29. The BLOCKER shape on one line: the call must survive, only the
        // prototype goes (the twin keeps the call and the parameter it declares).
        new("macro-call-on-proto-line", Fx("#define DECL_PARAM(n) float4 n;", "DECL_PARAM(Unused) " + PsProto, TechPs, PsDefUnused),
            Fx("#define DECL_PARAM(n) float4 n;", "DECL_PARAM(Unused)", TechPs, PsDefUnused), true),
        // A macro call that writes the prototype and other code: no edit takes only the prototype.
        new("macro-proto-plus-code", Fx("#define DECLARE2(name) float4 Unused; float4 name(float4 c : COLOR0) : COLOR0;",
            "DECLARE2(PS)", TechPs, PsDefUnused), null, true),
        new("proto-in-macro-argument", Fx("#define ID(x) x", "ID(" + PsProto + ")", TechPs, PsDef), Fx("#define ID(x) x", "ID()", TechPs, PsDef), true),
        new("multi-line-proto", Fx("float4 PS(float4 c", "    : COLOR0)", "    : COLOR0;", TechPs, PsDef), Fx("", "", "", TechPs, PsDef), true),
        // DXC's own front end answers an `inline` / `static` entry with "cannot find entry function"
        // (DirectX 12): a prototype carrying one is refused, one on both is left to DXC.
        new("inline-proto", Fx("inline " + PsProto, TechPs, PsDef), null, true),
        new("static-proto-and-def", Fx("static " + PsProto, TechPs, "static " + PsDef), null, true),
        new("proto-in-active-if", Fx("#if 1", PsProto, "#endif", TechPs, PsDef), Fx("#if 1", "", "#endif", TechPs, PsDef), true),
        // What a prototype says beyond its definition could be merged into the definition by
        // DXC's front end, so removing it is refused rather than risking a silent change.
        new("attribute-only-on-proto", Fx("[earlydepthstencil] " + PsProto, TechPs, PsDef), null, true),
        new("nointerpolation-only-on-proto", Fx("float4 PS(nointerpolation float4 c : COLOR0) : COLOR0;", TechPs, PsDef), null, true),
        new("semantic-only-on-proto", Fx(PsProto, TechPs, "float4 PS(float4 c) : COLOR0 { return c; }"), null, true),
        // Shapes the declaration reading does not model are refused, never handed to DXC.
        new("second-declarator-proto", Fx("float4 Other(float x), PS(float4 c : COLOR0) : COLOR0;", TechPs, PsDef), null, true),
        // A prototype beside two definitions (overloads): DXC's front end reports the ambiguity.
        new("proto-and-defined-overloads", Fx(PsProto, TechPs, PsDef, "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return uv.xyxy; }"), null, true),

        // Third review of 2026-10-29. BLOCKER: no prototype at all, with a conditional on a macro
        // only DXC defines and a macro that pastes tokens or names the entry, compiled on main and
        // was refused SD0236. Each must compile exactly as without the extra macro.
        new("noproto-paste-shader-target", Fx(CatDefine, "#if __SHADER_TARGET_MAJOR >= 4", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled),
            Fx("", "#if __SHADER_TARGET_MAJOR >= 4", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled), true),
        new("noproto-paste-ifdef-spirv", Fx(CatDefine, "#ifdef __spirv__", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled),
            Fx("", "#ifdef __spirv__", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled), true),
        new("noproto-paste-defined-hlsl-version", Fx(CatDefine, "#if defined(__HLSL_VERSION)", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled),
            Fx("", "#if defined(__HLSL_VERSION)", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled), true),
        new("noproto-entry-name-define", Fx("#define ENTRY_NAME PS", "#if __SHADER_TARGET_MAJOR >= 4", "#define SCALE 2", "#else", "#define SCALE 1", "#endif",
                TechPs, "float4 ENTRY_NAME(float4 c : COLOR0) : COLOR0 { return c * SCALE; }"),
            Fx("", "#if __SHADER_TARGET_MAJOR >= 4", "#define SCALE 2", "#else", "#define SCALE 1", "#endif", TechPs, PsDefScaled), true),
        new("noproto-macros-fxh-sprite", Fx(MacrosFxh, "#if __SHADER_TARGET_MAJOR >= 4", "#define TINT 1", "#endif", "DECLARE_TEXTURE(Texture, 0);",
                "float4 SpritePS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return SAMPLE_TEXTURE(Texture, uv) * color; }",
                "technique SpriteBatch { pass { PixelShader = compile PS_SM SpritePS(); } }"), null, true),
        // An entry declared twice through a macro naming it: blanked (the view proves it).
        new("macro-named-proto-and-def", Fx("#define ENTRY_NAME PS", "#if __SHADER_TARGET_MAJOR >= 4", "#define SCALE 2", "#else", "#define SCALE 1", "#endif",
                "float4 ENTRY_NAME(float4 c : COLOR0) : COLOR0;", TechPs, "float4 ENTRY_NAME(float4 c : COLOR0) : COLOR0 { return c * SCALE; }"),
            Fx("#define ENTRY_NAME PS", "#if __SHADER_TARGET_MAJOR >= 4", "#define SCALE 2", "#else", "#define SCALE 1", "#endif",
                "", TechPs, "float4 ENTRY_NAME(float4 c : COLOR0) : COLOR0 { return c * SCALE; }"), true),
        // SHOULD-FIX 1: a macro DXC defines that the old list did not know. This crashed.
        new("ifdef-gnuc-proto", Fx("#ifdef __GNUC__", PsProto, "#endif", TechPs, PsDef), Fx("#ifdef __GNUC__", "", "#endif", TechPs, PsDef), true),
        new("if-llvm-proto", Fx("#if __llvm__", PsProto, "#endif", TechPs, PsDef), Fx("#if __llvm__", "", "#endif", TechPs, PsDef), true),
        // ... and a macro DXC expands to an overload definition on the prototype's line, which the
        // positional range took too (DXC then picked the other overload): only the prototype goes.
        new("gnuc-overload-on-proto-line", Fx(GnucExtra, "EXTRA float4 PS(float4 c : COLOR0) SEM;", TechPs, PsDefTint),
            Fx(GnucExtra, "EXTRA", TechPs, PsDefTint), true),
        // SHOULD-FIX 2: a parenthesised declarator crashed OpenGL.
        new("paren-declarator-proto", Fx("float4 (PS)(float4 c : COLOR0) : COLOR0;", TechPs, PsDef), null, true),
        new("paren-declarator-def", Fx(PsProto, TechPs, "float4 (PS)(float4 c : COLOR0) : COLOR0 { return c; }"), null, true),
        new("paren-declarator-def-alone", Fx(TechPs, "float4 (PS)(float4 c : COLOR0) : COLOR0 { return c * float4(1, 0.5, 0.25, 1); }"), null, true),
        // NITs: a non-ASCII comment in the blanked prototype; a carriage-return-only file.
        new("utf8-comment-proto", Fx("float4 PS(float4 c /* é\U0001F600 */ : COLOR0) : COLOR0;", TechPs, PsDef), Fx("", TechPs, PsDef), true),
        new("cr-only-proto", Fx(PsProto, TechPs, PsDef).Replace('\n', '\r'), Fx("", TechPs, PsDef).Replace('\n', '\r'), true),
        // Final review of 2026-10-29. DXC's own preprocessor decides: a prototype (beside a call
        // that would be refused, or carrying `precise`) in a branch DXC never compiles changes
        // nothing; these were SD0236 on the branch and compile on 0.22.1.
        new("xbox-dead-proto-and-call", Fx("#ifdef _XBOX", PsProto, Half, "#endif", TechPs, PsDef), Fx("", "", "", "", TechPs, PsDef), true),
        new("xbox-dead-precise-proto", Fx("#ifdef _XBOX", "precise " + PsProto, "#endif", TechPs, PsDef), Fx("", "", "", TechPs, PsDef), true),
        new("ifndef-hlsl-version-dead-proto", Fx("#ifndef __HLSL_VERSION", PsProto, Half, "#endif", TechPs, PsDef), Fx("", "", "", "", TechPs, PsDef), true),
        // C's unsigned preprocessor arithmetic, which the managed evaluator got wrong: TRUE to DXC,
        // so the prototype is live and must go. Crashed OpenGL and Vulkan on main and the branch.
        new("unsigned-cmp-proto", Fx("#if -1 > 0u", PsProto, "#endif", TechPs, PsDef), Fx("#if -1 > 0u", "", "#endif", TechPs, PsDef), true),
        new("unsigned-div-proto", Fx("#if (-1 / 2u) > 100", PsProto, "#endif", TechPs, PsDef), Fx("#if (-1 / 2u) > 100", "", "#endif", TechPs, PsDef), true),
        // A source DXC's preprocessor rejects reaches DXC as written: the front end stops on it.
        new("error-directive-proto", Fx("#error not this target", PsProto, TechPs, PsDef), null, true),
        // Issue #444: DXC crashes expanding __TIMESTAMP__ (even -P); refused (SD0238) wherever DXC runs.
        new("timestamp-dead-branch", Fx("#if 0", "static const int Stamp = __TIMESTAMP__;", "#endif", TechPs, PsDef), Fx("", "", "", TechPs, PsDef), true),
        new("timestamp-in-comment", Fx("/* __TIMESTAMP__ */ // __TIMESTAMP__", TechPs, PsDef), Fx("", TechPs, PsDef), true),
    ];

    private const string CatDefine = "#define CAT(a,b) a##b";
    private const string PsDefScaled = "float4 PS(float4 c : COLOR0) : COLOR0 { return c * SCALE; }";

    private const string MacrosFxh =
        "#define DECLARE_TEXTURE(Name, index) Texture2D<float4> Name : register(t##index); sampler Name##Sampler : register(s##index)\n" +
        "#define SAMPLE_TEXTURE(Name, texCoord) Name.Sample(Name##Sampler, texCoord)";

    private const string GnucExtra =
        "#ifdef __GNUC__\n#define EXTRA float4 Tint(float4 c) { return c * 2; }\n#else\n#define EXTRA\n#endif\n" +
        "#define SEM : COLOR0\nfloat4 Tint(int4 c) { return float4(c); }";

    private const string PsDefTint = "float4 PS(float4 c : COLOR0) : COLOR0 { return Tint(c); }";

    private const string PsDefUnused = "float4 PS(float4 c : COLOR0) : COLOR0 { return c * Unused; }";

    /// <summary>The plain prototype-then-definition HLSL the raw-DXC control compiles.</summary>
    public const string RawControlSource =
        "float4 PS(float4 c : COLOR0) : SV_Target;\nfloat4 PS(float4 c : COLOR0) : SV_Target { return c; }\n";

    /// <summary>Runs the probe; see the class remarks for the argument.</summary>
    public static int Run(string[] args)
    {
        if (args.Length is not (1 or 2))
        {
            Console.Error.WriteLine($"usage: {ProbeArgument} <OpenGL|Vulkan|DirectX|DirectX12|Fna|raylib|sksl|raw-dxc> [<file.fx>]");
            return 64;
        }

        string target = args[0];
        if (target == "raw-dxc")
            return RunRawControl();
        if (args.Length == 2)
            return RunFile(target, Path.GetFullPath(args[1]));

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

    /// <summary>
    /// One effect file from disk (with its <c>#include</c>s), one verdict line: sweeps a folder of
    /// probe shaders a file per process, so one crash cannot hide the others.
    /// </summary>
    private static int RunFile(string target, string path)
    {
        string name = Path.GetFileName(path);
        Console.WriteLine($"{VerdictPrefix}{name} START");
        Console.Out.Flush();
        Result<string, ShaderError[]> compiled = Compile(target, File.ReadAllText(path), path);
        Console.WriteLine(compiled.IsFailure
            ? $"{VerdictPrefix}{name} DIAG {compiled.Error[0].Code} {compiled.Error[0].Line}:{compiled.Error[0].Column} {compiled.Error[0].Message.ReplaceLineEndings(" ")}"
            : $"{VerdictPrefix}{name} OK {compiled.Value}");
        return 0;
    }

    /// <summary>The compiled output as a comparable string (hex for binary containers).</summary>
    private static Result<string, ShaderError[]> Compile(string target, string effect, string sourceName = SourceName)
    {
        var includes = new ShadowDusk.Core.Preprocessor.FileSystemIncludeResolver();
        switch (target)
        {
            case "raylib":
            {
                var r = RaylibConverter.Convert(effect, new RaylibConvertOptions { SourceName = sourceName, IncludeResolver = includes });
                return r.IsFailure ? Result<string, ShaderError[]>.Fail(r.Error) : Result<string, ShaderError[]>.Ok(Hash(r.Value.FragmentShader));
            }
            case "sksl":
            {
                var s = SkslConverter.Convert(effect, new SkslConvertOptions { SourceName = sourceName, IncludeResolver = includes });
                return s.IsFailure ? Result<string, ShaderError[]>.Fail(s.Error) : Result<string, ShaderError[]>.Ok(Hash(s.Value.SkslText));
            }
            default:
            {
                var options = new CompilerOptions { Target = Enum.Parse<PlatformTarget>(target, ignoreCase: true), SourceFileName = sourceName, IncludeResolver = includes };
                var m = new EffectCompiler().Compile(effect, options);
                return m.IsFailure
                    ? Result<string, ShaderError[]>.Fail(m.Error)
                    : Result<string, ShaderError[]>.Ok(Convert.ToHexString(SHA256.HashData(m.Value.Data)) + ":" + m.Value.Data.Length);
            }
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))) + ":" + text.Length;

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
