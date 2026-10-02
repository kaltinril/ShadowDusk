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
/// slangc splits into <c>C_texture_0</c>/<c>C_sampler_0</c>; the author's name is <c>C</c>.
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
    // half, nothing on the sampler half slangc numbered itself.
    private const string CombinedResolvedFx = """
        Texture2D<float4> Comb_texture_0 : register(t2);
        SamplerState Comb_sampler_0;
        Texture2D Plain;
        SamplerState PlainSampler;

        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Comb_texture_0.Sample(Comb_sampler_0, uv) * Plain.Sample(PlainSampler, uv);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public void CombinedSampler2DRegister_KeepsTheAuthorsTextureRegister(PlatformTarget target)
    {
        var (effect, fx) = Slang(CombinedSlang, target);

        fx.ShouldContain("Comb_texture_0 : register(t2);", Case.Sensitive);
        fx.ShouldContain("SamplerState Comb_sampler_0;", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > Plain;", Case.Sensitive);
        fx.ShouldContain("SamplerState PlainSampler;", Case.Sensitive);
        Named(effect).ShouldBe(Named(Fx(CombinedResolvedFx, target)));
        if (target == PlatformTarget.DirectX)
            Named(effect).ShouldContain(s => s.Texture == "Comb_texture_0" && s.TextureSlot == 2);
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

        fx.ShouldContain("Comb_texture_0 : register(t2);", Case.Sensitive);
        fx.ShouldContain("Comb_sampler_0 : register(s3);", Case.Sensitive);
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

        fx.ShouldContain("C3_texture_0 : register(t5);", Case.Sensitive);
        fx.ShouldContain("CC_texture_0 : register(t6);", Case.Sensitive);
        fx.ShouldContain("Unbound_texture_0;", Case.Sensitive);
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

        fx.ShouldContain("CArr_texture_0[int(2)] : register(t8);", Case.Sensitive);
        fx.ShouldContain("C1_texture_0 : register(t4);", Case.Sensitive);
        fx.ShouldContain("SamplerState C1_sampler_0;", Case.Sensitive);
        fx.ShouldContain("CArr_sampler_0[int(2)];", Case.Sensitive);
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

        fx.ShouldContain("ModComb_texture_0 : register(t6);", Case.Sensitive);
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
        error.Message.ShouldContain("'InnerComb_texture_0' (one half of the combined sampler 'InnerComb')", Case.Sensitive);
        error.File.ShouldBe("Foreign.slang");
    }
}
