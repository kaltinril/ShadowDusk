#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #292, through real slangc on DirectX and OpenGL: two register shapes the issue #252
/// stripper used to drop silently. (1) A combined <c>Sampler2D C : register(t2)</c>, which
/// slangc splits into <c>C_texture_0</c>/<c>C_sampler_0</c>; the author's name is <c>C</c>,
/// which the texture half carries by the time the <c>.fx</c> is assembled (issue #302).
/// (2) A register written in an <c>import</c>ed module, which the entry source's
/// <c>slangc -E</c> output does not contain (it does not expand <c>import</c>). Each must land
/// where the declarations slangc actually compiled land when an author writes them out by hand
/// in a <c>.fx</c> file, and a shape whose authorship cannot be proven must fail as
/// <c>SD0628</c>, never be guessed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangForeignRegisterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-foreign-register-{Guid.NewGuid():N}");

    public SlangForeignRegisterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is not a test failure.
        }
    }

    private const string FxHeader = """
        #if SM4
            #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
            #define PS_SHADERMODEL ps_3_0
        #endif

        """;

    private const string FxTechnique = """

        technique T
        {
            pass P0
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        }
        """;

    /// <summary>Forwards to the real pipeline, keeping the assembled <c>.fx</c> text.</summary>
    private sealed class CapturingPipeline : IShaderCompiler
    {
        private readonly EffectCompiler _inner = new();

        public string? Fx { get; private set; }

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Fx = hlslSource;
            return _inner.Compile(hlslSource, options, cancellationToken);
        }
    }

    private static Result<CompiledShader, ShaderError[]> CompileSlang(string source, PlatformTarget target, out string? fx)
    {
        var pipeline = new CapturingPipeline();
        var result = new SlangCompiler(pipeline).Compile(
            source, new CompilerOptions { Target = target, SourceFileName = "Foreign.slang" });
        fx = pipeline.Fx;
        return result;
    }

    private static (MgfxBlobReader Effect, string Fx) Slang(string source, PlatformTarget target)
    {
        var result = CompileSlang(source, target, out string? fx);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        return (MgfxBlobReader.Parse(result.Value.Data), fx.ShouldNotBeNull());
    }

    private static MgfxBlobReader Fx(string declarationsAndShader, PlatformTarget target)
    {
        var result = new EffectCompiler().Compile(
            FxHeader + declarationsAndShader + FxTechnique,
            new CompilerOptions { Target = target, SourceFileName = "Foreign.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    // Each sampler record keyed by the name of the texture parameter it binds.
    private static (string Texture, byte TextureSlot, byte SamplerSlot)[] Named(MgfxBlobReader effect) =>
        effect.Samplers
            .Select(s => (effect.Parameters[s.Parameter].Name, s.TextureSlot, s.SamplerSlot))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();

    // A forward-slash absolute path: what an author writes in an import string on every host.
    private string WriteModule(string fileName, string text)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
        return path.Replace('\\', '/');
    }

    // ---- (2) Combined Sampler2D ------------------------------------------------------------

    private const string CombinedSlang = """
        Sampler2D Comb : register(t2);
        Texture2D Plain;
        SamplerState PlainSampler;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Comb.Sample(uv) * Plain.Sample(PlainSampler, uv);
        }
        """;

    // What slangc compiles CombinedSlang to, written out by hand: the author's t2 on the texture
    // half (under the author's name, issue #302), nothing on the sampler half slangc numbered itself.
    private const string CombinedResolvedFx = """
        Texture2D<float4> Comb : register(t2);
        SamplerState Comb_sampler_0;
        Texture2D Plain;
        SamplerState PlainSampler;

        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Comb.Sample(Comb_sampler_0, uv) * Plain.Sample(PlainSampler, uv);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSampler2DRegister_KeepsTheAuthorsTextureRegister(PlatformTarget target)
    {
        var (effect, fx) = Slang(CombinedSlang, target);

        fx.ShouldContain("Texture2D<float4 > Comb : register(t2);", Case.Sensitive);
        fx.ShouldContain("SamplerState Comb_sampler_0;", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > Plain;", Case.Sensitive);
        fx.ShouldContain("SamplerState PlainSampler;", Case.Sensitive);
        Named(effect).ShouldBe(Named(Fx(CombinedResolvedFx, target)));
        if (target == PlatformTarget.DirectX)
            Named(effect).ShouldContain(s => s.Texture == "Comb" && s.TextureSlot == 2);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSampler2DWithBothRegisters_KeepsBoth(PlatformTarget target)
    {
        const string source = """
            Sampler2D Comb : register(t2) : register(s3);

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Comb.Sample(uv);
            }
            """;

        var (_, fx) = Slang(source, target);

        fx.ShouldContain("Texture2D<float4 > Comb : register(t2);", Case.Sensitive);
        fx.ShouldContain("SamplerState Comb_sampler_0 : register(s3);", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSamplerVariants_KeepTheirAuthorRegisters(PlatformTarget target)
    {
        // Measured (v2026.14.1): every Sampler<Shape> splits the same way.
        const string source = """
            Sampler3D C3 : register(t5);
            SamplerCube CC : register(t6);
            Sampler2D Unbound;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return C3.Sample(float3(uv, 0)) + CC.Sample(float3(uv, 1)) + Unbound.Sample(uv);
            }
            """;

        var (_, fx) = Slang(source, target);

        fx.ShouldContain("Texture3D<float4 > C3 : register(t5);", Case.Sensitive);
        fx.ShouldContain("TextureCube<float4 > CC : register(t6);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > Unbound;", Case.Sensitive);
        fx.ShouldNotContain("register(s", Case.Sensitive);
    }

    [Fact]
    public void CombinedSamplerShapesOpenGlCannotBind_DirectX_KeepTheirTextureRegisters()
    {
        // DirectX only: OpenGL rejects both shapes loudly on the .fx route as well, an array of
        // separate samplers in SPIRV-Cross (SD0100) and a 1D sampler in the GL rewrite (SD0210).
        const string source = """
            Sampler2D CArr[2] : register(t8);
            Sampler1D C1 : register(t4);

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return CArr[1].Sample(uv) + C1.Sample(uv.x);
            }
            """;

        var (_, fx) = Slang(source, PlatformTarget.DirectX);

        fx.ShouldContain("CArr[int(2)] : register(t8);", Case.Sensitive);
        fx.ShouldContain("Texture1D<float4 > C1 : register(t4);", Case.Sensitive);
        fx.ShouldContain("SamplerState C1_sampler_0;", Case.Sensitive);
        fx.ShouldContain("CArr_sampler_0[int(2)];", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void StructWithAResourceRegister_KeepsItOnTheHoistedFieldOfItsClass(PlatformTarget target)
    {
        // The same hoisting as a combined sampler: slangc emits the struct's fields as
        // gM_a_0 / gM_b_0 and lays the struct out from the author's s5 (measured: s5, s6), so
        // both hoisted samplers keep a register of the author's class. Samplers, because a
        // TEXTURE held in a struct has no author-written parameter name and is rejected
        // (issue #302, SD0640).
        const string source = """
            struct M { SamplerState a; SamplerState b; };
            M gM : register(s5);
            Texture2D Tex;

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Tex.Sample(gM.a, uv) + Tex.Sample(gM.b, uv);
            }
            """;

        var (_, fx) = Slang(source, target);

        fx.ShouldContain("SamplerState gM_a_0 : register(s5);", Case.Sensitive);
        fx.ShouldContain("SamplerState gM_b_0 : register(s6);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > Tex;", Case.Sensitive);
    }

    // ---- (1) A register in an imported module ----------------------------------------------

    private const string ModulePixelShader = """
        SamplerState S;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return ModTex.Sample(S, uv) * ModNoReg.Sample(ModS, uv);
        }
        """;

    // What slangc compiles the imported module + entry to, written out by hand.
    private const string ModuleResolvedFx = """
        Texture2D ModTex : register(t3);
        Texture2D ModNoReg;
        SamplerState ModS;
        SamplerState S;

        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return ModTex.Sample(S, uv) * ModNoReg.Sample(ModS, uv);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void RegisterInAnImportedModule_IsKept_AndItsUnregisteredNeighboursAreStripped(PlatformTarget target)
    {
        string module = WriteModule("texmod.slang", """
            module texmod;
            public Texture2D ModTex : register(t3);
            public Texture2D ModNoReg;
            public SamplerState ModS;
            """);

        var (effect, fx) = Slang($"import \"{module}\";\n" + ModulePixelShader, target);

        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
        // slangc numbers an imported resource with no author register itself (measured).
        fx.ShouldContain("Texture2D<float4 > ModNoReg;", Case.Sensitive);
        fx.ShouldContain("SamplerState ModS;", Case.Sensitive);
        fx.ShouldContain("SamplerState S;", Case.Sensitive);
        Named(effect).ShouldBe(Named(Fx(ModuleResolvedFx, target)));
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ImportedModuleRegister_FollowsThisTargetsMacros(PlatformTarget target)
    {
        // slangc applies the compile's -D macros to an imported module too (measured), so the
        // module's own #if decides, exactly as it decides what slangc compiled.
        string module = WriteModule("branchmod.slang", """
            module branchmod;
            public Texture2D ModTex;
            public Texture2D ModNoReg;
            #if OPENGL
            public SamplerState ModS;
            #else
            public SamplerState ModS : register(s4);
            #endif
            """);

        var (_, fx) = Slang($"import \"{module}\";\n" + ModulePixelShader, target);

        if (target == PlatformTarget.OpenGL)
            fx.ShouldContain("SamplerState ModS;", Case.Sensitive);
        else
            fx.ShouldContain("SamplerState ModS : register(s4);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > ModTex;", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSamplerInAnImportedModule_KeepsItsTextureRegister(PlatformTarget target)
    {
        string module = WriteModule("combmod.slang", """
            module combmod;
            public Sampler2D ModComb : register(t6);
            """);
        string source = $"import \"{module}\";\n" + """
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return ModComb.Sample(uv);
            }
            """;

        var (_, fx) = Slang(source, target);

        fx.ShouldContain("Texture2D<float4 > ModComb : register(t6);", Case.Sensitive);
        fx.ShouldContain("SamplerState ModComb_sampler_0;", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void RegisterThroughAMacroFromTheImportingModule_IsKept(PlatformTarget target)
    {
        // The module defines the macro and #includes the file that uses it. slangc names the
        // included file in its #line, and that file alone does not say what MSLOT is; the
        // imported module's own -E pass (followed from the entry source's quoted import) does.
        WriteModule("slots.hlsli", "public Texture2D ModTex : MSLOT;\npublic Texture2D ModNoReg;\npublic SamplerState ModS;\n");
        string module = WriteModule("macromod.slang", """
            module macromod;
            #define MSLOT register(t3)
            #include "slots.hlsli"
            """);

        var (effect, fx) = Slang($"import \"{module}\";\n" + ModulePixelShader, target);

        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > ModNoReg;", Case.Sensitive);
        Named(effect).ShouldBe(Named(Fx(ModuleResolvedFx, target)));
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSamplerBehindAnImportByModuleName_FailsLoudly(PlatformTarget target)
    {
        // slangc locates a combined sampler's halves in its own core module, and the file that
        // declares it is reached only through 'import inner;', a module-name import whose search
        // ShadowDusk does not model. No pass can prove the register is the author's (or not), so
        // the compile refuses by name rather than guess.
        WriteModule("inner.slang", "module inner;\npublic Sampler2D InnerComb : register(t6);\n");
        string outer = WriteModule("outer.slang", "module outer;\n__exported import inner;\n");
        string source = $"import \"{outer}\";\n" + """
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return InnerComb.Sample(uv);
            }
            """;

        var result = CompileSlang(source, target, out _);

        result.IsSuccess.ShouldBeFalse();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0628");
        error.Message.ShouldContain("'InnerComb_texture_0' (which slangc may have hoisted out of 'InnerComb')", Case.Sensitive);
        error.File.ShouldBe("Foreign.slang");
    }

    // ---- Which files count as modules ------------------------------------------------------

    private const string ModTexPixelShader = """
        SamplerState S;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return ModTex.Sample(S, uv);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ModuleImportedByName_ThatOpensWithAModuleDeclaration_IsRead(PlatformTarget target)
    {
        // 'import inner;' is not followed, but slangc's #line names inner.slang and the file
        // opens with 'module inner;', so it is a module and its own text is what slangc compiled.
        WriteModule("inner.slang", "module inner;\npublic Texture2D ModTex : register(t3);\n");
        string outer = WriteModule("outer.slang", "module outer;\n__exported import inner;\n");

        var (_, fx) = Slang($"import \"{outer}\";\n" + ModTexPixelShader, target);

        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void FileReachedOnlyByName_WithoutAModuleDeclaration_FailsLoudly(PlatformTarget target)
    {
        // The same shape without 'module inner;': nothing ShadowDusk can read tells this file
        // from a fragment some unseen module #includes after defining macros, so its text is not
        // taken as what slangc compiled.
        WriteModule("inner.slang", "public Texture2D ModTex : register(t3);\n");
        string outer = WriteModule("outer.slang", "module outer;\n__exported import inner;\n");

        var result = CompileSlang($"import \"{outer}\";\n" + ModTexPixelShader, target, out _);

        result.IsSuccess.ShouldBeFalse();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0628");
        error.File.ShouldEndWith("inner.slang", Case.Sensitive);
        error.Line.ShouldBe(1);
        error.Message.ShouldContain("does not open with a 'module' or 'implementing' declaration", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void FragmentWhoseRegisterDependsOnItsIncludersMacro_IsReadThroughTheIncluder(PlatformTarget target)
    {
        // On its own the fragment preprocesses to a PLAIN declaration (SLOTTED is not defined),
        // but the module that #includes it defines SLOTTED, so slangc compiled the register.
        // Reading the fragment alone would strip the author's t3 silently.
        WriteModule("slots.hlsli", """
            public Texture2D ModTex
            #ifdef SLOTTED
                : register(t3)
            #endif
            ;
            """);
        string module = WriteModule("slotmod.slang", "module slotmod;\n#define SLOTTED 1\n#include \"slots.hlsli\"\n");

        var (_, fx) = Slang($"import \"{module}\";\n" + ModTexPixelShader, target);

        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ModuleMacros_DoNotReachAnImportedModule(PlatformTarget target)
    {
        // What makes a module's own -E output trustworthy: a macro defined by the importer does
        // not cross the import (measured), so 'inner' compiles WITHOUT the register and slangc's
        // number on ModTex is its own.
        WriteModule("inner.slang", """
            module inner;
            public Texture2D ModTex
            #ifdef SLOTTED
                : register(t3)
            #endif
            ;
            """);
        string outer = WriteModule("outer.slang", "module outer;\n#define SLOTTED 1\n__exported import \"inner.slang\";\n");

        var (_, fx) = Slang($"import \"{outer}\";\n" + ModTexPixelShader, target);

        fx.ShouldContain("Texture2D<float4 > ModTex;", Case.Sensitive);
    }

    // ---- What the pass costs, through real slangc -------------------------------------------

    private static (Result<CompiledShader, ShaderError[]> Result, List<IReadOnlyList<string>> Runs) CompileCounting(
        string source, PlatformTarget target)
    {
        var runs = new List<IReadOnlyList<string>>();
        var compiler = new SlangCompiler(
            new CapturingPipeline(),
            () => SlangToolPath.GetUnsupportedReason() is { } reason
                ? new SlangCompiler.SlangcLocation(reason, null)
                : new SlangCompiler.SlangcLocation(null, SlangToolPath.Resolve()),
            SlangNativeCache.EnsureRunnableSlangc,
            (path, directory, text, arguments) =>
            {
                runs.Add(arguments);
                return SlangCompiler.RunSlangc(path, directory, text, arguments);
            });
        var result = compiler.Compile(source, new CompilerOptions { Target = target, SourceFileName = "Cost.slang" });
        return (result, runs);
    }

    [Theory]
    // shape, target, slangc runs for the whole compile (1 entry point + the -E runs).
    [InlineData("untextured", PlatformTarget.DirectX, 1)]
    [InlineData("untextured", PlatformTarget.OpenGL, 1)]
    [InlineData("textured", PlatformTarget.DirectX, 2)]
    [InlineData("textured", PlatformTarget.OpenGL, 2)]
    [InlineData("combined", PlatformTarget.DirectX, 2)]
    [InlineData("combined", PlatformTarget.OpenGL, 2)]
    [InlineData("import", PlatformTarget.DirectX, 2)]
    [InlineData("import", PlatformTarget.OpenGL, 2)]
    [InlineData("chain", PlatformTarget.DirectX, 2)]
    [InlineData("chain", PlatformTarget.OpenGL, 2)]
    // A register in a fragment the ENTRY source #includes: 2 before this change, 2 now.
    [InlineData("include", PlatformTarget.DirectX, 2)]
    [InlineData("include", PlatformTarget.OpenGL, 2)]
    public void RegisterPass_CostsTheSameThroughRealSlangc(string shape, PlatformTarget target, int expectedRuns)
    {
        // The counts SlangRegisterPassCostTests pins with a fake slangc, here with the real one:
        // this is what proves the one-run-for-several-inputs form really comes back as one line
        // per input (a fallback to one run per file would show up as a higher count).
        string source = shape switch
        {
            "untextured" => "cbuffer P { float4 Tint; };\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return Tint * uv.x; }\n",
            "textured" => "Texture2D ModTex : register(t1);\nSamplerState S : register(s1);\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return ModTex.Sample(S, uv); }\n",
            "combined" => "Sampler2D Comb : register(t2);\n[shader(\"fragment\")]\nfloat4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return Comb.Sample(uv); }\n",
            "import" => $"import \"{WriteModule("texmod.slang", "module texmod;\npublic Texture2D ModTex : register(t3);\n")}\";\n" + ModTexPixelShader,
            "include" => $"#include \"{WriteModule("slots.hlsli", "Texture2D ModTex : register(t3);\n")}\"\n" + ModTexPixelShader,
            _ => $"import \"{WriteChain()}\";\n" + ModTexPixelShader,
        };

        var (result, runs) = CompileCounting(source, target);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        runs.Count.ShouldBe(expectedRuns, string.Join(" | ", runs.Select(r => string.Join(" ", r.SkipWhile(a => a != "-E")))));
    }

    // a.slang imports b.slang imports c.slang, by quoted path; the texture is in the last.
    private string WriteChain()
    {
        WriteModule("c.slang", "module c;\npublic Texture2D ModTex : register(t3);\n");
        WriteModule("b.slang", "module b;\n__exported import \"c.slang\";\n");
        return WriteModule("a.slang", "module a;\n__exported import \"b.slang\";\n");
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ChainOfThreeQuotedImports_KeepsTheRegisterInTheLast(PlatformTarget target)
    {
        var (_, fx) = Slang($"import \"{WriteChain()}\";\n" + ModTexPixelShader, target);

        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
        fx.ShouldContain("SamplerState S;", Case.Sensitive);
    }

    // ---- Issue #325: a module global named like a hoist of an entry global ------------------

    private const string LayerModule = """
        module m;
        public Texture2D tex_layer_0 : register(t3);
        public SamplerState ModS;
        public float4 fetchMod(float2 uv) { return tex_layer_0.Sample(ModS, uv); }
        """;

    private const string LayerEntry = """
        Texture2D tex;
        SamplerState S;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return fetchMod(uv) + tex.Sample(S, uv);
        }
        """;

    // What slangc compiles the two files to, written out by hand: the module author's t3 on
    // tex_layer_0, nothing on the entry's tex (slangc's own number).
    private const string LayerResolvedFx = """
        Texture2D tex_layer_0 : register(t3);
        SamplerState ModS;
        Texture2D tex;
        SamplerState S;

        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return tex_layer_0.Sample(ModS, uv) + tex.Sample(S, uv);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ModuleTextureNamedLikeAHoistOfAnEntryGlobal_KeepsItsRegister(PlatformTarget target)
    {
        // 'tex_layer_0' is the module author's own name; the entry's plain 'tex' is its prefix.
        // Measured (v2026.14.1): before this fix the entry's 'tex' claimed the name as its hoist
        // and the module's register(t3) was stripped, moving the texture to another slot.
        string module = WriteModule("m.slang", LayerModule);

        var (effect, fx) = Slang($"import \"{module}\";\n" + LayerEntry, target);

        fx.ShouldContain("Texture2D<float4 > tex_layer_0 : register(t3);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > tex;", Case.Sensitive);
        fx.ShouldContain("SamplerState ModS;", Case.Sensitive);
        Named(effect).ShouldBe(Named(Fx(LayerResolvedFx, target)));
        effect.Parameters.Select(p => p.Name).ShouldContain("tex_layer_0");
        if (target == PlatformTarget.DirectX)
            Named(effect).ShouldContain(s => s.Texture == "tex_layer_0" && s.TextureSlot == 3);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ModuleTextureNamedLikeAHoistOfAnEntryGlobal_WithTheRegisterOnThePrefix_IsStripped(PlatformTarget target)
    {
        // The reverse: the ENTRY writes register(t1) on 'tex' and the module's 'tex_layer_0' has
        // none. The prefix must not keep slangc's own number on the module's texture either.
        string module = WriteModule("m.slang", LayerModule.Replace(" : register(t3)", "", StringComparison.Ordinal));

        var (_, fx) = Slang($"import \"{module}\";\n" + LayerEntry.Replace("Texture2D tex;", "Texture2D tex : register(t1);", StringComparison.Ordinal), target);

        fx.ShouldContain("Texture2D<float4 > tex_layer_0;", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > tex : register(t1);", Case.Sensitive);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void ModuleTextureNamedLikeAHoist_CostsTheSameAsAnyRegisteredModuleDeclaration(PlatformTarget target)
    {
        string module = WriteModule("m.slang", LayerModule);

        var (result, runs) = CompileCounting($"import \"{module}\";\n" + LayerEntry, target);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        // One compile, one preprocess run reading the entry source and the module together.
        runs.Count.ShouldBe(2);
        runs[1].SkipWhile(a => a != "--").ShouldBe(["--", "-", module]);
    }
}
