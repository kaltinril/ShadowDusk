#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;
using Transport = ShadowDusk.Slang.Tests.SlangcTransport;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Pins what the register strip COSTS in slangc invocations (issue #292, owner request): one
/// process spawn each on desktop, one seam call each in the browser. Every shape has one entry
/// point, so each compile is 1 run for the entry plus the preprocess-only (<c>-E</c>) runs
/// listed here, on both transports. A change that makes any shape pay more fails here instead
/// of growing silently. The same counts through REAL slangc are pinned by
/// <c>SlangForeignRegisterTests.RegisterPass_CostsTheSameThroughRealSlangc</c>; the wall-time
/// measurement is in <c>project_facts.md</c>.
/// </summary>
public sealed class SlangRegisterPassCostTests
{
    private const string A = "C:/shaders/a.slang";
    private const string B = "C:/shaders/b.slang";
    private const string C = "C:/shaders/c.slang";

    private const string PixelShaderEmission = """

        #line 9 "<stdin>"
        float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            return float4(uv_0, 0, 1);
        }

        """;

    private const string PixelShader =
        "[shader(\"fragment\")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }\n";

    private const string PixelShaderPreprocessed =
        "[ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return float4 ( uv , 0 , 1 ) ; } \n";

    /// <summary>One shape: what slangc is given and answers, and the <c>-E</c> runs expected.</summary>
    private sealed record Shape(
        string Source,
        string Emission,
        string EntryPreprocessed,
        Dictionary<string, string> Files,
        string[][] ExpectedPreprocessRuns);

    private static Shape Make(
        string declarations, string emittedDeclarations, string preprocessedDeclarations,
        string[][] expectedRuns, params (string Path, string Text)[] files) =>
        new(
            declarations + PixelShader,
            emittedDeclarations + PixelShaderEmission,
            preprocessedDeclarations + " " + PixelShaderPreprocessed,
            files.ToDictionary(f => f.Path, f => f.Text),
            expectedRuns);

    private static readonly Dictionary<string, Shape> Shapes = new()
    {
        // (a) Untextured: slangc's output holds no texture/sampler register. Nothing to decide.
        ["a: untextured"] = Make(
            "cbuffer P { float4 Tint; };\n",
            "#line 1 \"<stdin>\"\ncbuffer P : register(b0)\n{\n    float4 Tint;\n}\n",
            "cbuffer P { float4 Tint ; } ;",
            []),

        // Untextured, but the source spells 'register' (on the cbuffer). This used to run the
        // pass; with no texture/sampler register in the output there is nothing to ask.
        ["a: untextured, cbuffer register(b0) written"] = Make(
            "cbuffer P : register(b0) { float4 Tint; };\n",
            "#line 1 \"<stdin>\"\ncbuffer P : register(b0)\n{\n    float4 Tint;\n}\n",
            "cbuffer P : register ( b0 ) { float4 Tint ; } ;",
            []),

        // (b) Textured, every register in the entry file: the entry source alone.
        ["b: textured, author registers in the entry file"] = Make(
            "Texture2D Tex : register(t1);\nSamplerState Samp : register(s1);\n",
            "#line 1 \"<stdin>\"\nTexture2D<float4 > Tex : register(t1);\n#line 2\nSamplerState Samp : register(s1);\n",
            "Texture2D Tex : register ( t1 ) ; SamplerState Samp : register ( s1 ) ;",
            [["-"]]),

        // Textured with no register written anywhere: the source cannot spell one, no pass.
        ["b: textured, no register written"] = Make(
            "Texture2D Tex;\nSamplerState Samp;\n",
            "#line 1 \"<stdin>\"\nTexture2D<float4 > Tex : register(t0);\n#line 2\nSamplerState Samp : register(s0);\n",
            "Texture2D Tex ; SamplerState Samp ;",
            []),

        // (c) Combined Sampler2D with an author register: the entry source alone. slangc's core
        // module (the halves' #line file) is never handed to slangc as a file.
        ["c: combined Sampler2D, author register"] = Make(
            "Sampler2D Comb : register(t2);\n",
            "#line 93 \"core\"\nTexture2D<float4 > Comb_texture_0 : register(t2);\n#line 1188 \"hlsl.meta.slang\"\nSamplerState Comb_sampler_0 : register(s0);\n",
            "Sampler2D Comb : register ( t2 ) ;",
            [["-"]]),

        // Combined, no register, no import: the halves are located in 'core', but nothing can
        // come from a module and the source cannot spell a register.
        ["c: combined Sampler2D, no register written"] = Make(
            "Sampler2D Comb;\n",
            "#line 93 \"core\"\nTexture2D<float4 > Comb_texture_0 : register(t0);\n#line 1188 \"hlsl.meta.slang\"\nSamplerState Comb_sampler_0 : register(s0);\n",
            "Sampler2D Comb ;",
            []),

        // (d) One module imported by quoted path declares a registered texture: ONE run reads
        // the entry source and the module together.
        ["d: one quoted import with a registered texture"] = Make(
            $"import \"{A}\";\nSamplerState Samp;\n",
            $"#line 2 \"{A}\"\nTexture2D<float4 > ModTex : register(t3);\n#line 2 \"<stdin>\"\nSamplerState Samp : register(s0);\n",
            $"import \"{A}\" ; SamplerState Samp ;",
            [["-", A]],
            (A, "module a ; public Texture2D ModTex : register ( t3 ) ;")),

        // (e) A chain of three quoted imports, the texture in the last. The last module opens
        // with 'module c;', so it is known to be a module from its own text: still ONE run, and
        // the two modules in between are never read.
        ["e: chain of three quoted imports, texture in the last"] = Make(
            $"import \"{A}\";\nSamplerState Samp;\n",
            $"#line 2 \"{C}\"\nTexture2D<float4 > ModTex : register(t3);\n#line 2 \"<stdin>\"\nSamplerState Samp : register(s0);\n",
            $"import \"{A}\" ; SamplerState Samp ;",
            [["-", C]],
            (A, "module a ; __exported import \"b.slang\" ;"),
            (B, "module b ; __exported import \"c.slang\" ;"),
            (C, "module c ; public Texture2D ModTex : register ( t3 ) ;")),

        // The same chain where no file opens with a module declaration: the last file must be
        // REACHED through the imports before its text is trusted (it could be an #include'd
        // fragment), one run per level.
        ["e: chain of three, no module declarations"] = Make(
            $"import \"{A}\";\nSamplerState Samp;\n",
            $"#line 1 \"{C}\"\nTexture2D<float4 > ModTex : register(t3);\n#line 2 \"<stdin>\"\nSamplerState Samp : register(s0);\n",
            $"import \"{A}\" ; SamplerState Samp ;",
            [["-", C], [A], [B]],
            (A, "__exported import \"b.slang\" ;"),
            (B, "__exported import \"c.slang\" ;"),
            (C, "public Texture2D ModTex : register ( t3 ) ;")),

        // The same chain with a COMBINED sampler in the last module: slangc locates its halves
        // in its core module, so no file is named and every reachable module has to be read.
        ["e: chain of three, combined sampler in the last"] = Make(
            $"import \"{A}\";\n",
            "#line 93 \"core\"\nTexture2D<float4 > ModComb_texture_0 : register(t6);\n#line 1188 \"hlsl.meta.slang\"\nSamplerState ModComb_sampler_0 : register(s0);\n",
            $"import \"{A}\" ;",
            [["-"], [A], [B], [C]],
            (A, "module a ; __exported import \"b.slang\" ;"),
            (B, "module b ; __exported import \"c.slang\" ;"),
            (C, "module c ; public Sampler2D ModComb : register ( t6 ) ;")),

        // The error path is bounded too. The entry source #includes a fragment that declares the
        // texture; slangc's #line names the fragment, which cannot be preprocessed on its own
        // (here: not openable). The combined run is then not one clean line per input, so the
        // entry source is read alone, and its text (which -E expanded the #include into) decides
        // everything: the fragment is not run a second time.
        ["entry #include whose fragment does not preprocess alone"] = Make(
            "#include \"C:/shaders/frag.hlsli\"\nSamplerState Samp;\n",
            "#line 1 \"C:/shaders/frag.hlsli\"\nTexture2D<float4 > IncTex : register(t7);\n#line 2 \"<stdin>\"\nSamplerState Samp : register(s0);\n",
            "Texture2D IncTex : register ( t7 ) ; SamplerState Samp ;",
            [["-", "C:/shaders/frag.hlsli"], ["-"]]),

        // Two modules on one level are read in one run, and a module imported twice only once.
        ["two quoted imports on one level, one shared"] = Make(
            $"import \"{A}\";\nimport \"{B}\";\n",
            "#line 93 \"core\"\nTexture2D<float4 > ModComb_texture_0 : register(t6);\n#line 1188 \"hlsl.meta.slang\"\nSamplerState ModComb_sampler_0 : register(s0);\n",
            $"import \"{A}\" ; import \"{B}\" ;",
            [["-"], [A, B], [C]],
            (A, "module a ; __exported import \"c.slang\" ;"),
            (B, "module b ; __exported import \"c.slang\" ;"),
            (C, "module c ; public Sampler2D ModComb : register ( t6 ) ;")),
    };

    public static TheoryData<string, Transport, PlatformTarget> Cases()
    {
        var data = new TheoryData<string, Transport, PlatformTarget>();
        foreach (string shape in Shapes.Keys)
        {
            foreach (Transport transport in Enum.GetValues<Transport>())
            {
                data.Add(shape, transport, PlatformTarget.DirectX);
                data.Add(shape, transport, PlatformTarget.OpenGL);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachShape_CostsExactlyTheseSlangcRuns(string shapeName, Transport transport, PlatformTarget target)
    {
        Shape shape = Shapes[shapeName];
        var slangc = new ScriptedSlangc(shape.Emission, shape.EntryPreprocessed, shape.Files);

        var result = slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler())
            .Compile(shape.Source, new CompilerOptions { Target = target, SourceFileName = "Cost.slang" });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        slangc.PreprocessInputs.Select(run => string.Join(" ", run))
            .ShouldBe(shape.ExpectedPreprocessRuns.Select(run => string.Join(" ", run)));
        // One entry point: one compile run, then the preprocess runs and nothing else.
        slangc.Calls.Count.ShouldBe(1 + shape.ExpectedPreprocessRuns.Length);
    }

    [Fact]
    public void NoFileIsPreprocessedTwiceInOneCompile()
    {
        foreach ((string name, Shape shape) in Shapes)
        {
            var slangc = new ScriptedSlangc(shape.Emission, shape.EntryPreprocessed, shape.Files);
            slangc.Compiler(Transport.InProcess, new ScriptedSlangc.CapturingCompiler())
                .Compile(shape.Source, new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "Cost.slang" })
                .IsSuccess.ShouldBeTrue(name);

            string[] inputs = slangc.PreprocessInputs.SelectMany(run => run).Where(input => input != "-").ToArray();
            inputs.ShouldBe(inputs.Distinct(), name);
        }
    }
}
