#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;
using Transport = ShadowDusk.Slang.Tests.SlangcTransport;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #292: the wiring that decides registers on declarations from another file (an
/// imported module), driven through a FAKE slangc on both transports (the in-process seam the
/// browser uses and the process seam), so it is pinned on every host with no disk and no
/// process. The emission and <c>-E</c> texts below are slangc v2026.14.1's, verbatim in shape.
/// The real-slangc proof is <see cref="SlangForeignRegisterTests"/>; what each shape costs in
/// slangc runs is pinned by <see cref="SlangRegisterPassCostTests"/>.
/// </summary>
public sealed class SlangForeignRegisterPassTests
{
    private const string ModulePath = "C:/shaders/texmod.slang";

    private const string Source = """
        import "C:/shaders/texmod.slang";
        SamplerState S;

        [shader("fragment")]
        float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return ModTex.Sample(S, uv) * ModNoReg.Sample(S, uv); }
        """;

    // slangc numbers every resource and names the file each came from.
    private const string Emission = """
        #pragma pack_matrix(column_major)

        #line 2 "C:/shaders/texmod.slang"
        Texture2D<float4 > ModTex : register(t3);

        #line 2 "<stdin>"
        SamplerState S : register(s0);

        #line 3 "C:/shaders/texmod.slang"
        Texture2D<float4 > ModNoReg : register(t0);

        #line 5 "<stdin>"
        float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            return ModTex.Sample(S, uv_0) * ModNoReg.Sample(S, uv_0);
        }

        """;

    // -E does not expand an import: the entry text has no ModTex declaration at all.
    private const string EntryPreprocessed =
        "import \"C:/shaders/texmod.slang\" ; SamplerState S ; [ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return ModTex . Sample ( S , uv ) * ModNoReg . Sample ( S , uv ) ; } \n";

    private const string ModulePreprocessed =
        "module texmod ; public Texture2D ModTex : register ( t3 ) ; public Texture2D ModNoReg ; \n";

    private static CompilerOptions Options(PlatformTarget target) =>
        new() { Target = target, SourceFileName = "Entry.slang" };

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Text);

    [Fact]
    public void BuildPreprocessFiles_IsThePreprocessListWithTheFilesAfterStdin()
    {
        IReadOnlyList<MacroDefinition> macros = PlatformMacros.For(PlatformTarget.OpenGL).Macros;
        UserDefine[] defines = [new UserDefine("QUALITY", "2")];
        IReadOnlyList<string> stdin = SlangcArguments.BuildPreprocess(macros, defines);

        // With the entry source: BuildPreprocess itself, then the files.
        SlangcArguments.BuildPreprocessFiles(macros, defines, includeEntry: true, [ModulePath, "D:/b.slang"])
            .ShouldBe([.. stdin, ModulePath, "D:/b.slang"]);
        // Without: the same prefix, the files in place of '-'.
        SlangcArguments.BuildPreprocessFiles(macros, defines, includeEntry: false, [ModulePath])
            .ShouldBe([.. stdin.Take(stdin.Count - 1), ModulePath]);
    }

    [Theory]
    [InlineData(Transport.InProcess, PlatformTarget.DirectX)]
    [InlineData(Transport.InProcess, PlatformTarget.OpenGL)]
    [InlineData(Transport.Process, PlatformTarget.DirectX)]
    [InlineData(Transport.Process, PlatformTarget.OpenGL)]
    public void ImportedModuleRegister_IsJudgedFromTheModulesOwnText(Transport transport, PlatformTarget target)
    {
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(Emission, EntryPreprocessed, Files((ModulePath, ModulePreprocessed)));

        var result = slangc.Compiler(transport, downstream).Compile(Source, Options(target));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > ModNoReg;", Case.Sensitive);
        fx.ShouldContain("SamplerState S;", Case.Sensitive);

        // One compile, then ONE preprocess run that reads the entry source and the module
        // together, with the compile's macros: the same lists on both transports.
        IReadOnlyList<MacroDefinition> macros = PlatformMacros.For(target).Macros;
        slangc.Calls.Count.ShouldBe(2);
        slangc.Calls[1].ShouldBe(SlangcArguments.BuildPreprocessFiles(macros, [], includeEntry: true, [ModulePath]));
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ImportOnlySource_StillRunsThePass(Transport transport)
    {
        // The entry source never spells 'register' (the old skip), but it imports, and the
        // emission holds a declaration from another file, which the entry text cannot speak for.
        var slangc = new ScriptedSlangc(Emission, EntryPreprocessed, Files((ModulePath, ModulePreprocessed)));

        SlangcRegisterStripper.MayWriteRegister(Source, []).ShouldBeFalse();
        slangc.Compiler(transport, new ScriptedSlangc.CapturingCompiler())
            .Compile(Source, Options(PlatformTarget.OpenGL)).IsSuccess.ShouldBeTrue();

        slangc.PreprocessInputs.ShouldHaveSingleItem().ShouldBe(["-", ModulePath]);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ModuleThatCannotBeOpened_IsSD0628_WithSlangcsOwnWords(Transport transport)
    {
        // Anywhere a module's pass cannot be read, -E exits 0 with an error on stderr and prints
        // no line for that input, so the combined run is repeated one input at a time and the
        // failure lands on its file.
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(Emission, EntryPreprocessed);

        var result = slangc.Compiler(transport, downstream).Compile(Source, Options(PlatformTarget.DirectX));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe((ModulePath, 2, 1, "SD0628"));
        error.Message.ShouldContain("'ModTex'", Case.Sensitive);
        error.Message.ShouldContain($"error[E00001]: cannot open file '{ModulePath}'", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
        slangc.PreprocessInputs.ShouldBe([["-", ModulePath], ["-"], [ModulePath]]);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ModuleWhoseTextCannotDecide_IsSD0628_NamingTheDeclaration(Transport transport)
    {
        // 'ModTex : MSLOT' where MSLOT is not a macro the module's own pass knows.
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(Emission, EntryPreprocessed,
            Files((ModulePath, "module texmod ; public Texture2D ModTex : MSLOT ; public Texture2D ModNoReg ; \n")));

        var result = slangc.Compiler(transport, downstream).Compile(Source, Options(PlatformTarget.OpenGL));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe((ModulePath, 2, "SD0628"));
        error.Message.ShouldContain("'ModTex'", Case.Sensitive);
        error.Message.ShouldContain("will not guess", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void FileThatIsNotProvenAModule_IsSD0628_NeverReadAsOne(Transport transport)
    {
        // slangc's #line names 'frag.hlsli', which the entry source does not import by quoted
        // path (it is reached through 'import texmod;', by name) and which does not open with a
        // module declaration. It may be a fragment some module #includes after defining macros,
        // so its own text ('ModTex' declared plainly) proves nothing: no silent strip.
        const string fragment = "C:/shaders/frag.hlsli";
        string emission = Emission.Replace(ModulePath, fragment, StringComparison.Ordinal);
        string entry = EntryPreprocessed.Replace("import \"C:/shaders/texmod.slang\"", "import texmod", StringComparison.Ordinal);
        string source = Source.Replace("import \"C:/shaders/texmod.slang\"", "import texmod", StringComparison.Ordinal);
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(emission, entry,
            Files((fragment, "public Texture2D ModTex ; public Texture2D ModNoReg ; \n")));

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(PlatformTarget.DirectX));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe((fragment, 2, "SD0628"));
        error.Message.ShouldContain("does not open with a 'module' or 'implementing' declaration", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void FragmentIncludedByAnImportedModule_IsJudgedFromThatModulesText(Transport transport)
    {
        // The module defines a macro and #includes the fragment; slangc's #line names the
        // fragment. On its own the fragment reads as a PLAIN declaration (the '#ifdef' is off),
        // which is not what slangc compiled; the module's own -E output is.
        const string fragment = "C:/shaders/frag.hlsli";
        string emission = Emission.Replace(ModulePath, fragment, StringComparison.Ordinal);
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(emission, EntryPreprocessed,
            Files(
                (fragment, "public Texture2D ModTex ; public Texture2D ModNoReg ; \n"),
                (ModulePath, ModulePreprocessed)));

        var result = slangc.Compiler(transport, downstream).Compile(Source, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > ModNoReg;", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void CombinedSamplerInAModule_IsFoundThroughTheQuotedImport(Transport transport)
    {
        // slangc locates a combined sampler's halves in its own core module, never the author's
        // file, so the module is reached through the entry text's quoted import instead.
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > ModComb_texture_0 : register(t6);

            #line 1188 "hlsl.meta.slang"
            SamplerState ModComb_sampler_0 : register(s0);

            #line 3 "<stdin>"
            float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                return ModComb_texture_0.Sample(ModComb_sampler_0, uv_0);
            }

            """;
        const string entry =
            "import \"C:/shaders/texmod.slang\" ; [ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return ModComb . Sample ( uv ) ; } \n";
        const string source =
            "import \"C:/shaders/texmod.slang\";\n[shader(\"fragment\")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return ModComb.Sample(uv); }\n";
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(emission, entry,
            Files((ModulePath, "module texmod ; public Sampler2D ModComb : register ( t6 ) ; \n")));

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        // The texture half carries the module's own name for the global (issue #302), decided
        // from the module text this pass already read: no further slangc run.
        fx.ShouldContain("Texture2D<float4 > ModComb : register(t6);", Case.Sensitive);
        fx.ShouldContain("SamplerState ModComb_sampler_0;", Case.Sensitive);
        fx.ShouldContain("return ModComb.Sample(ModComb_sampler_0, uv_0);", Case.Sensitive);
        // The core module is never handed to slangc as a file to preprocess.
        slangc.PreprocessInputs.ShouldBe([["-"], [ModulePath]]);
    }

    // ---- Issue #325: a module global named like a hoist of an entry global ------------------

    private const string LayerModulePath = "C:/shaders/m.slang";

    // slangc's emission for the issue's repro (measured): the module's 'tex_layer_0' keeps its
    // author register t3 and is located at its own declaration; the entry's 'tex' is numbered.
    private static string LayerEmission(string modulePath) => $$"""
        #pragma pack_matrix(column_major)

        #line 2 "{{modulePath}}"
        Texture2D<float4 > tex_layer_0 : register(t3);
        #line 3
        SamplerState ModS : register(s1);

        #line 2 "<stdin>"
        Texture2D<float4 > tex : register(t0);
        #line 3
        SamplerState S : register(s0);

        #line 4 "{{modulePath}}"
        float4 fetchMod_0(float2 uv_0)
        {
            return tex_layer_0.Sample(ModS, uv_0);
        }

        #line 5 "<stdin>"
        float4 MainPS(float4 pos_0 : SV_Position, float2 uv_1 : TEXCOORD0) : SV_TARGET
        {
            return fetchMod_0(uv_1) + tex.Sample(S, uv_1);
        }

        """;

    private static string LayerSource(string modulePath) =>
        $"import \"{modulePath}\";\nTexture2D tex;\nSamplerState S;\n[shader(\"fragment\")]\n" +
        "float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return fetchMod(uv) + tex.Sample(S, uv); }\n";

    private static string LayerEntryPreprocessed(string modulePath) =>
        $"import \"{modulePath}\" ; Texture2D tex ; SamplerState S ; [ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : " +
        "SV_Position , float2 uv : TEXCOORD0 ) : SV_Target { return fetchMod ( uv ) + tex . Sample ( S , uv ) ; } \n";

    private const string LayerModulePreprocessed =
        "module m ; public Texture2D tex_layer_0 : register ( t3 ) ; public SamplerState ModS ; public float4 fetchMod ( float2 uv ) { return tex_layer_0 . Sample ( ModS , uv ) ; } \n";

    [Theory]
    [InlineData(Transport.InProcess, PlatformTarget.DirectX, LayerModulePath)]
    [InlineData(Transport.InProcess, PlatformTarget.OpenGL, LayerModulePath)]
    [InlineData(Transport.Process, PlatformTarget.DirectX, LayerModulePath)]
    [InlineData(Transport.Process, PlatformTarget.OpenGL, LayerModulePath)]
    // Imported by a relative path, slangc locates the module by the bare name it was written
    // with (measured): still a file, never slangc's core module.
    [InlineData(Transport.InProcess, PlatformTarget.DirectX, "m.slang")]
    [InlineData(Transport.Process, PlatformTarget.OpenGL, "m.slang")]
    public void ModuleGlobalNamedLikeAHoistOfAnEntryGlobal_KeepsItsRegister(Transport transport, PlatformTarget target, string modulePath)
    {
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(LayerEmission(modulePath), LayerEntryPreprocessed(modulePath),
            Files((modulePath, LayerModulePreprocessed)));

        var result = slangc.Compiler(transport, downstream).Compile(LayerSource(modulePath), Options(target));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        string fx = downstream.Captured.ShouldNotBeNull();
        // The module author's t3 stays; the entry's 'tex' is slangc's own number and goes.
        fx.ShouldContain("Texture2D<float4 > tex_layer_0 : register(t3);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > tex;", Case.Sensitive);
        fx.ShouldContain("SamplerState ModS;", Case.Sensitive);
        // And the name is the author's: the hoisted-name pass (issue #302) leaves it alone.
        fx.ShouldContain("return tex_layer_0.Sample(ModS, uv_0);", Case.Sensitive);
        // One preprocess run, the entry source and the module together: the same cost as any
        // other registered module declaration.
        slangc.PreprocessInputs.ShouldBe([["-", modulePath]]);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ModuleGlobalNamedLikeAHoist_WithoutARegister_IsStripped(Transport transport)
    {
        // The control: the module author wrote no register, so slangc's t3 is its own number.
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(LayerEmission(LayerModulePath), LayerEntryPreprocessed(LayerModulePath),
            Files((LayerModulePath, LayerModulePreprocessed.Replace(" : register ( t3 )", "", StringComparison.Ordinal))));

        var result = slangc.Compiler(transport, downstream).Compile(LayerSource(LayerModulePath), Options(PlatformTarget.OpenGL));

        result.IsSuccess.ShouldBeTrue();
        downstream.Captured.ShouldNotBeNull().ShouldContain("Texture2D<float4 > tex_layer_0;", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ModuleGlobalNamedLikeAHoist_InAModuleThatCannotBeRead_IsSD0628_NeverStripped(Transport transport)
    {
        // Before the fix the entry's 'tex' claimed 'tex_layer_0' as its hoist and the module was
        // never consulted; now an unreadable module is a loud failure, located at the declaration.
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(LayerEmission(LayerModulePath), LayerEntryPreprocessed(LayerModulePath));

        var result = slangc.Compiler(transport, downstream).Compile(LayerSource(LayerModulePath), Options(PlatformTarget.DirectX));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe((LayerModulePath, 2, "SD0628"));
        error.Message.ShouldContain("'tex_layer_0'", Case.Sensitive);
        error.Message.ShouldContain("could not be read", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
        // The fallback read the module on its own (and found it unreadable) instead of skipping
        // it on the strength of the entry's 'tex'.
        slangc.PreprocessInputs.ShouldBe([["-", LayerModulePath], ["-"], [LayerModulePath]]);
    }
}
