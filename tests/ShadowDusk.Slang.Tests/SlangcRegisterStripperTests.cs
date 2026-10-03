#nullable enable

using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>Pure unit tests for <see cref="SlangcRegisterStripper"/> (issue #252).</summary>
public sealed class SlangcRegisterStripperTests
{
    private const string SlangcEmission = """
        Texture2D<float4 > SpriteTexture : register(t0);
        SamplerState SpriteSampler : register(s0);
        TextureCube<float4 > Env : register(t1, space0);
        SamplerComparisonState Shadow : register(s1);
        Texture2D<float4 > Arr[2] : register(t2);
        cbuffer Params : register(b0)
        {
            float Levels;
        }
        """;

    [Fact]
    public void Strip_RemovesEverySlangcNumberedTextureAndSamplerRegister()
    {
        string stripped = SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string>());

        stripped.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
        stripped.ShouldContain("SamplerState SpriteSampler;", Case.Sensitive);
        stripped.ShouldContain("TextureCube<float4 > Env;", Case.Sensitive);
        stripped.ShouldContain("SamplerComparisonState Shadow;", Case.Sensitive);
        stripped.ShouldContain("Texture2D<float4 > Arr[2];", Case.Sensitive);
        stripped.ShouldNotContain("register(t", Case.Sensitive);
        stripped.ShouldNotContain("register(s", Case.Sensitive);
    }

    [Fact]
    public void Strip_LeavesConstantBufferRegistersAlone()
    {
        SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string>())
            .ShouldContain("cbuffer Params : register(b0)", Case.Sensitive);
    }

    [Fact]
    public void Strip_KeepsTheRegisterOfAnAuthorBoundName()
    {
        string stripped = SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string> { "SpriteSampler" });

        stripped.ShouldContain("SamplerState SpriteSampler : register(s0);", Case.Sensitive);
        stripped.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
    }

    [Fact]
    public void AuthorBoundNames_FindsRegisterAnnotations_IncludingArrays()
    {
        const string source = """
            Texture2D Mask : register(t1);
            SamplerState MaskSampler: register( s1 );
            Texture2D Layers[4] : register(t4);
            Texture2D Free;
            """;

        var names = SlangcRegisterStripper.AuthorBoundNames(source);

        names.ShouldBe(new[] { "Mask", "MaskSampler", "Layers" }, ignoreOrder: true);
    }

    [Fact]
    public void AuthorBoundNames_IgnoresCommentsAndVkBinding()
    {
        // A register in a comment is not one; vk::binding is not an HLSL register and slangc's
        // HLSL drops it (measured), so it reserves nothing on the .fx route either.
        const string source = """
            // SamplerState Old : register(s0);
            /* Texture2D Gone : register(t0); */
            [[vk::binding(3)]] SamplerState S;
            """;

        SlangcRegisterStripper.AuthorBoundNames(source).ShouldBeEmpty();
    }

    // ---- Issue #252 follow-up: the names come from slangc's preprocess-only (-E) output,
    // which is a one-line token stream. The strings below are that output, verbatim, for the
    // sources named in each test (slangc v2026.14.1).

    [Fact]
    public void AuthorBoundNames_ReadsSlangcsPreprocessedTokenStream()
    {
        // '#define SLOT(n) : register(n)' + 'Texture2D Mask SLOT(t1); SamplerState MaskSampler SLOT(s1);'
        const string preprocessed =
            "Texture2D Mask : register ( t1 ) ; SamplerState MaskSampler : register ( s1 ) ; " +
            "Texture2D Layers [ 4 ] : register ( t4 ) ; Texture2D Free ; " +
            "[ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return Mask . Sample ( MaskSampler , uv ) ; } \n";

        SlangcRegisterStripper.AuthorBoundNames(preprocessed)
            .ShouldBe(new[] { "Mask", "MaskSampler", "Layers" }, ignoreOrder: true);
    }

    [Fact]
    public void AuthorBoundNames_InactiveBranchRegister_IsAbsentFromThePreprocessedText()
    {
        // '#if OPENGL / SamplerState SpriteSampler; / #else / SamplerState SpriteSampler : register(s0); / #endif'
        // preprocessed with -DOPENGL=1: the register never reaches the compile, so it is not bound.
        const string openGl =
            "Texture2D SpriteTexture ; SamplerState SpriteSampler ; [ shader ( \"fragment\" ) ] float4 MainPS ( ) : SV_Target { return 0 ; } \n";
        // The same source preprocessed for DirectX (OPENGL undefined): the #else branch is live.
        const string directX =
            "Texture2D SpriteTexture ; SamplerState SpriteSampler : register ( s0 ) ; [ shader ( \"fragment\" ) ] float4 MainPS ( ) : SV_Target { return 0 ; } \n";

        SlangcRegisterStripper.AuthorBoundNames(openGl).ShouldBeEmpty();
        SlangcRegisterStripper.AuthorBoundNames(directX).ShouldBe(new[] { "SpriteSampler" });
    }

    [Fact]
    public void AuthorBoundNames_IgnoresARegisterSpelledInsideAStringLiteral()
    {
        const string preprocessed = "[ shader ( \"Fake : register(s0)\" ) ] SamplerState Real : register ( s2 ) ; \n";

        SlangcRegisterStripper.AuthorBoundNames(preprocessed).ShouldBe(new[] { "Real" });
    }

    [Theory]
    [InlineData("Texture2D T;\nSamplerState S;\n", false)]
    [InlineData("#if OPENGL\nSamplerState S;\n#endif\n#define TWO 2\n", false)]
    // slangc's 'register' is case-sensitive (REGISTER(t3) is a syntax error, measured).
    [InlineData("Texture2D T : REGISTER(t3);\n", false)]
    [InlineData("Texture2D T : register(t3);\n", true)]
    [InlineData("// a comment that says register\nTexture2D T;\n", true)]
    [InlineData("#include \"Slots.fxh\"\nTexture2D T SLOT;\n", true)]
    [InlineData("#define R(k) : regi##ster(k)\nTexture2D T R(t3);\n", true)]
    // slangc splices a backslash-newline inside an identifier (measured: this binds t3).
    [InlineData("Texture2D T : regis\\\nter(t3);\n", true)]
    public void MayWriteRegister_IsFalseOnlyWhenNoRegisterTokenCanExist(string source, bool expected) =>
        SlangcRegisterStripper.MayWriteRegister(source, []).ShouldBe(expected);

    [Theory]
    [InlineData("SLOT", ": register(t5)", true)]
    [InlineData("SLOT", "regi##ster", true)]
    [InlineData("QUALITY", "2", false)]
    public void MayWriteRegister_AlsoReadsUserDefines(string name, string value, bool expected) =>
        SlangcRegisterStripper.MayWriteRegister("Texture2D T SLOT;\n", [new UserDefine(name, value)]).ShouldBe(expected);

    [Theory]
    [InlineData("Texture1D<float4 > A")]
    [InlineData("Texture1DArray<float4 > A")]
    [InlineData("Texture2D<float4 > A")]
    [InlineData("Texture2DArray<float4 > A")]
    [InlineData("Texture2DMS<float4 > A")]
    [InlineData("Texture2DMSArray<float4 > A")]
    [InlineData("Texture3D<float4 > A")]
    [InlineData("TextureCube<float4 > A")]
    [InlineData("TextureCubeArray<float4 > A")]
    [InlineData("RWTexture2D<float4 > A")]
    [InlineData("Texture2D<float4 >  A[int(2)]")]
    [InlineData("SamplerState A")]
    [InlineData("SamplerComparisonState A")]
    public void Strip_CoversEveryRealResourceType(string declaration)
    {
        string register = declaration.StartsWith("Sampler", StringComparison.Ordinal) ? "s3" : "t3";

        SlangcRegisterStripper.Strip($"{declaration} : register({register});", new HashSet<string>())
            .ShouldBe($"{declaration};");
    }

    [Theory]
    // User types that merely START with a resource type's name are not resources.
    [InlineData("TextureRegion_0 gRegion : register(t0);")]
    [InlineData("Texture2DInfo_0 gInfo : register(t0);")]
    [InlineData("MyTexture2D gMine : register(t0);")]
    [InlineData("SamplerStateInfo_0 gInfo : register(s0);")]
    [InlineData("SamplerStates_0 gStates : register(s0);")]
    public void Strip_LeavesUserTypesNamedLikeAResourceAlone(string declaration) =>
        SlangcRegisterStripper.Strip(declaration, new HashSet<string>()).ShouldBe(declaration);

    // ---- Issue #292: combined samplers and declarations from other files --------------------

    private static SlangcRegisterStripper.EmittedResource Emitted(string name, char cls, string file = "<stdin>") =>
        new(name, cls, file, 1);

    private static Dictionary<string, SlangcRegisterStripper.AuthorBindings> Modules(params (string File, string Text)[] modules) =>
        modules.ToDictionary(
            m => SlangcRegisterStripper.PathKey(m.File),
            m => SlangcRegisterStripper.AuthorBindings.Parse(m.Text));

    // Every reachable module read: the final verdict.
    private static SlangcRegisterStripper.RegisterVerdict Judge(
        SlangcRegisterStripper.EmittedResource resource, string entry, params (string File, string Text)[] modules) =>
        SlangcRegisterStripper.Judge(
            resource, SlangcRegisterStripper.AuthorBindings.Parse(entry), Modules(modules), closureComplete: true);

    [Theory]
    // 'Sampler2D Comb : register(t2)': slangc keeps t2 on the texture half and numbers the sampler.
    [InlineData("Sampler2D Comb : register ( t2 ) ;", "Comb_texture_0", 't', "Keep")]
    [InlineData("Sampler2D Comb : register ( t2 ) ;", "Comb_sampler_0", 's', "Strip")]
    // 'register(s3)' alone: the reverse.
    [InlineData("Sampler2D Comb : register ( s3 ) ;", "Comb_texture_0", 't', "Strip")]
    [InlineData("Sampler2D Comb : register ( s3 ) ;", "Comb_sampler_0", 's', "Keep")]
    // Both written.
    [InlineData("Sampler2D Comb : register ( t2 ) : register ( s3 ) ;", "Comb_texture_0", 't', "Keep")]
    [InlineData("Sampler2D Comb : register ( t2 ) : register ( s3 ) ;", "Comb_sampler_0", 's', "Keep")]
    // Neither, and arrays / other shapes / a generic element type.
    [InlineData("Sampler2D Comb ;", "Comb_texture_0", 't', "Strip")]
    [InlineData("Sampler2D CArr [ 2 ] : register ( t8 ) ;", "CArr_texture_0", 't', "Keep")]
    [InlineData("SamplerCube CC : register ( t6 ) ;", "CC_texture_0", 't', "Keep")]
    [InlineData("Sampler2D < float4 > G : register ( t5 , space1 ) ;", "G_texture_0", 't', "Keep")]
    public void Judge_MapsACombinedSamplersSplitHalvesBackToTheAuthorsDeclaration(
        string entry, string emittedName, char cls, string expected) =>
        // slangc's #line for a split half is its own core module, never "<stdin>" (measured).
        Judge(Emitted(emittedName, cls, "core"), entry).ToString().ShouldBe(expected);

    [Theory]
    // A struct's resource fields are hoisted as '<var>_<field>_<n>' (measured): 'Samplers gS;'
    // gives 'gS_s_0', and 'M gM : register(t5)' puts t5 on the texture field only.
    [InlineData("Samplers gS ;", "gS_s_0", 's', "Strip")]
    [InlineData("M gM : register ( t5 ) ;", "gM_t_0", 't', "Keep")]
    [InlineData("M gM : register ( t5 ) ;", "gM_s_0", 's', "Strip")]
    // An underscore in the author's own name: every prefix is a candidate.
    [InlineData("Sampler2D my_tex : register ( t1 ) ;", "my_tex_texture_0", 't', "Keep")]
    // Declared nowhere the entry text shows (a module's, or a non-global): not decidable here.
    [InlineData("void f ( ) { Samplers gS ; }", "gS_s_0", 's', "Unproven")]
    public void Judge_MapsAStructsHoistedResourcesBackToTheAuthorsGlobal(
        string entry, string emittedName, char cls, string expected) =>
        Judge(Emitted(emittedName, cls, "core"), entry).ToString().ShouldBe(expected);

    [Fact]
    public void Judge_APlainNameEndingLikeASplitHalf_IsStillMatchedVerbatim() =>
        // An author's own 'Texture2D Foo_texture_0 : register(t1)' is not a split.
        Judge(Emitted("Foo_texture_0", 't'), "Texture2D Foo_texture_0 : register ( t1 ) ;")
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);

    [Fact]
    public void Judge_DeclarationLocatedInAModule_IsDecidedByThatModulesOwnText()
    {
        const string entry = "import \"C:/a/m.slang\" ; SamplerState S ;";
        const string file = "C:/a/m.slang";
        var modTex = Emitted("ModTex", 't', file);

        Judge(modTex, entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        Judge(modTex, entry, (file, "module m ; public Texture2D ModTex : register ( t3 ) ;")).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
        Judge(modTex, entry, (file, "module m ; public Texture2D ModTex ;")).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
        // A register through a macro this file does not define: neither reading.
        Judge(modTex, entry, (file, "module m ; public Texture2D ModTex : MSLOT ;")).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        // Only a local of that name: not a global declaration.
        Judge(modTex, entry, (file, "module m ; void f ( ) { Texture2D ModTex ; }")).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        // The same text says both.
        Judge(modTex, entry, (file, "public Texture2D ModTex : register ( t3 ) ; public Texture2D ModTex ;"))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        // slangc located it in THIS module, so what another module says about the name is not asked.
        Judge(modTex, entry, (file, "module m ; public Texture2D ModTex : register ( t3 ) ;"), ("C:/a/other.slang", "Texture2D ModTex ;"))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
        // The #line spelling and the import spelling of one file are one file.
        Judge(Emitted("ModTex", 't', "C://a//m.slang"), entry, ("C:\\a\\m.slang", "module m ; public Texture2D ModTex : register ( t3 ) ;"))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
    }

    [Fact]
    public void Judge_DeclarationNotLocatedInAModule_NeedsEveryModule_AndTheirAgreement()
    {
        // Located in a file that is not a known module (a fragment some module #includes), or
        // hoisted (located in slangc's core module): the modules together decide.
        var entry = SlangcRegisterStripper.AuthorBindings.Parse("import \"C:/a/m.slang\" ; SamplerState S ;");
        var inFragment = Emitted("ModTex", 't', "C:/a/frag.hlsli");
        var hoisted = Emitted("Comb_texture_0", 't', "core");
        var bound = Modules(("C:/a/m.slang", "module m ; public Texture2D ModTex : register ( t3 ) ; public Sampler2D Comb : register ( t2 ) ;"));
        var plain = Modules(("C:/a/m.slang", "module m ; public Texture2D ModTex ; public Sampler2D Comb ;"));
        var both = Modules(
            ("C:/a/m.slang", "module m ; public Texture2D ModTex : register ( t3 ) ; public Sampler2D Comb : register ( t2 ) ;"),
            ("C:/a/n.slang", "module n ; Texture2D ModTex ; Sampler2D Comb ;"));

        foreach (var resource in new[] { inFragment, hoisted })
        {
            // Not before every reachable module is read: an unread one could disagree.
            SlangcRegisterStripper.Judge(resource, entry, bound, closureComplete: false)
                .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Pending);
            SlangcRegisterStripper.Judge(resource, entry, bound, closureComplete: true)
                .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
            SlangcRegisterStripper.Judge(resource, entry, plain, closureComplete: true)
                .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
            SlangcRegisterStripper.Judge(resource, entry, both, closureComplete: true)
                .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
            // An import that could not be read: "the modules read agree" proves nothing.
            SlangcRegisterStripper.Judge(resource, entry, bound, closureComplete: true, closureBroken: true)
                .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        }
    }

    [Fact]
    public void Judge_DeclarationLocatedInAModule_IsDecidedBeforeTheClosureIsComplete() =>
        SlangcRegisterStripper.Judge(
                Emitted("ModTex", 't', "C:/a/m.slang"),
                SlangcRegisterStripper.AuthorBindings.Parse("import \"C:/a/m.slang\" ;"),
                Modules(("C:/a/m.slang", "module m ; public Texture2D ModTex : register ( t3 ) ;")),
                closureComplete: false)
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);

    [Theory]
    [InlineData("module m ; public Texture2D T ;", true)]
    [InlineData("  implementing host ; Texture2D T ;", true)]
    [InlineData("public Texture2D T ;", false)]
    [InlineData("Texture2D module ;", false)]
    [InlineData("modules m ;", false)]
    [InlineData("", false)]
    public void OpensAsModule_OnlyForAModuleOrImplementingDeclarationAtTheTop(string preprocessed, bool expected) =>
        SlangcRegisterStripper.OpensAsModule(preprocessed).ShouldBe(expected);

    [Theory]
    [InlineData("Texture2D T;\nSamplerState S;\n", null, null, false)]
    [InlineData("import \"m.slang\";\nSamplerState S;\n", null, null, true)]
    [InlineData("__exported import m;\n", null, null, true)]
    [InlineData("MODS\nSamplerState S;\n", "MODS", "import \"m.slang\";", true)]
    [InlineData("MODS\nSamplerState S;\n", "MODS", "1", false)]
    public void MayImport_ReadsTheSourceAndTheDefines(string source, string? name, string? value, bool expected) =>
        SlangcRegisterStripper.MayImport(source, name is null ? [] : [new UserDefine(name, value!)]).ShouldBe(expected);

    [Theory]
    [InlineData("C:/a/m.slang", "C:/a/m.slang")]
    [InlineData("C:\\a\\m.slang", "C:/a/m.slang")]
    // slangc's #line for an import written with backslashes (measured).
    [InlineData("C://a//m.slang", "C:/a/m.slang")]
    [InlineData("rel/m.slang", "rel/m.slang")]
    public void PathKey_GivesOneSpellingPerFile(string path, string expected) =>
        SlangcRegisterStripper.PathKey(path).ShouldBe(expected);

    [Fact]
    public void Judge_DeclarationFromAFileTheEntryIncludes_IsDecidedByTheEntryText()
    {
        // '#include' IS expanded by -E: the entry text speaks for the included file's declarations.
        const string entry = "Texture2D IncTex : register ( t7 ) ; Texture2D IncNoReg ; SamplerState S ;";

        Judge(Emitted("IncTex", 't', "C:/inc.hlsli"), entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
        Judge(Emitted("IncNoReg", 't', "C:/inc.hlsli"), entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
        // A local of the same name in the entry is not a global declaration.
        Judge(Emitted("ModTex", 't', "C:/m.slang"), "float4 f ( ) { Texture2D ModTex ; return 0 ; }")
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
    }

    [Fact]
    public void FindRegistered_TracksSlangcsLineDirectives()
    {
        const string emission = """
            #line 2 "C:/m.slang"
            Texture2D<float4 > ModTex : register(t3);
            #line 3 "<stdin>"
            SamplerState S : register(s0);
            #line 3 "C:/m.slang"
            Texture2D<float4 > ModNoReg : register(t1);
            SamplerState ModS : register(s4);
            #line 93 "core"
            Texture2D<float4 > Comb_texture_0 : register(t2);
            cbuffer Params : register(b0)
            """;

        SlangcRegisterStripper.FindRegistered(emission).ShouldBe(
        [
            new SlangcRegisterStripper.EmittedResource("ModTex", 't', "C:/m.slang", 2),
            new SlangcRegisterStripper.EmittedResource("S", 's', "<stdin>", 3),
            new SlangcRegisterStripper.EmittedResource("ModNoReg", 't', "C:/m.slang", 3),
            new SlangcRegisterStripper.EmittedResource("ModS", 's', "C:/m.slang", 4),
            new SlangcRegisterStripper.EmittedResource("Comb_texture_0", 't', "core", 93),
        ]);
    }

    [Fact]
    public void FindRegistered_WithoutAnyLineDirective_IsTheEntrySource() =>
        SlangcRegisterStripper.FindRegistered("SamplerState S : register(s0);\n")
            .ShouldHaveSingleItem().File.ShouldBe(SlangcRegisterStripper.EntrySourceFile);

    [Fact]
    public void AuthorBoundNames_SeesEveryRegisterOfAChain() =>
        SlangcRegisterStripper.AuthorBoundNames("Sampler2D Comb : register ( t2 ) : register ( s3 ) ;")
            .ShouldBe(new[] { "Comb" });

    [Theory]
    // From the entry source: as written (slangc resolves it from its working directory, which the pass shares).
    [InlineData("import \"C:/a/m.slang\" ;", null, "C:/a/m.slang")]
    [InlineData("import \"rel/m.slang\" ;", null, "rel/m.slang")]
    // From another file: against that file's directory, rooted paths as written, on every host alike.
    [InlineData("__exported import \"inner.slang\" ;", "C:/a/outer.slang", "C:/a/inner.slang")]
    [InlineData("__include \"part.slang\" ;", "/home/u/outer.slang", "/home/u/part.slang")]
    [InlineData("import \"/abs/m.slang\" ;", "C:/a/outer.slang", "/abs/m.slang")]
    // A string literal's escaped backslash is one backslash of the path.
    [InlineData("import \"D:\\\\x\\\\m.slang\" ;", "C:/a/outer.slang", "D:\\x\\m.slang")]
    public void QuotedImports_ResolveLikeSlangc(string preprocessed, string? importingFile, string expected) =>
        SlangcRegisterStripper.QuotedImports(preprocessed, importingFile).ShouldBe(new[] { expected });

    [Fact]
    public void QuotedImports_DoNotFollowAnImportByModuleName() =>
        SlangcRegisterStripper.QuotedImports("module outer ; __exported import inner ;", "C:/a/outer.slang").ShouldBeEmpty();

    // ---- Issue #325: an author's name shaped like a hoist ------------------------------------

    private const string PrefixEntry = "import \"C:/a/m.slang\" ; Texture2D tex ; SamplerState S ;";
    private const string ModuleFile = "C:/a/m.slang";

    private static SlangcRegisterStripper.AuthorBindings Bindings(string text) =>
        SlangcRegisterStripper.AuthorBindings.Parse(text);

    [Fact]
    public void Judge_ModuleGlobalNamedLikeAHoistOfAnEntryGlobal_IsMatchedVerbatim_NeverThroughThePrefix()
    {
        // 'tex_layer_0' has the hoisted shape ('<global>_<field>_<n>') and the entry declares a
        // plain 'tex'. slangc locates it in the module, whose text spells it: it is the module
        // author's own name, and its register is the module's to decide.
        var layer = Emitted("tex_layer_0", 't', ModuleFile);
        const string bound = "module m ; public Texture2D tex_layer_0 : register ( t3 ) ; public SamplerState ModS ;";
        const string plain = "module m ; public Texture2D tex_layer_0 ; public SamplerState ModS ;";

        SlangcRegisterStripper.Judge(layer, Bindings(PrefixEntry), Modules((ModuleFile, bound)), closureComplete: true, located: Bindings(bound))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
        SlangcRegisterStripper.Judge(layer, Bindings(PrefixEntry), Modules((ModuleFile, plain)), closureComplete: true, located: Bindings(plain))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
        // The module's text is read but not yet proven a module: its own verdict waits for the
        // closure, and the entry's 'tex' still does not claim the name.
        SlangcRegisterStripper.Judge(layer, Bindings(PrefixEntry), Modules(), closureComplete: false, located: Bindings(bound))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Pending);
        // Unread (the file could not be preprocessed): nothing may claim it through a prefix.
        SlangcRegisterStripper.Judge(layer, Bindings(PrefixEntry), Modules(), closureComplete: true, closureBroken: true)
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        SlangcRegisterStripper.Judge(layer, Bindings(PrefixEntry), Modules(), closureComplete: false)
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Pending);
    }

    [Fact]
    public void Judge_EntryRegisterOnThePrefix_DoesNotReachAModulesLiteralName()
    {
        // The reverse hole: 'Texture2D tex : register(t1)' in the entry must not KEEP a register
        // slangc numbered on the module's own plain 'tex_layer_0'.
        var layer = Emitted("tex_layer_0", 't', ModuleFile);
        const string entry = "import \"C:/a/m.slang\" ; Texture2D tex : register ( t1 ) ;";
        const string plain = "module m ; public Texture2D tex_layer_0 ;";

        SlangcRegisterStripper.Judge(layer, Bindings(entry), Modules((ModuleFile, plain)), closureComplete: true, located: Bindings(plain))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
    }

    [Fact]
    public void IsHoistedName_OnlyForANameNoReadTextSpells()
    {
        var entry = Bindings(PrefixEntry);
        var module = Bindings("module m ; public Texture2D tex_layer_0 : register ( t3 ) ;");
        var other = Bindings("module m ; public Sampler2D tex : register ( t3 ) ;");

        // Located in slangc's core module: a hoist (of the entry's or a module's global).
        SlangcRegisterStripper.IsHoistedName(Emitted("tex_texture_0", 't', "core"), entry, null).ShouldBeTrue();
        SlangcRegisterStripper.IsHoistedName(Emitted("tex_texture_0", 't', "hlsl.meta.slang"), entry, null).ShouldBeTrue();
        // Spelled verbatim by the entry: the author's own global (issue #302's collision case).
        SlangcRegisterStripper.IsHoistedName(Emitted("tex", 't', "core"), entry, null).ShouldBeFalse();
        // Located in a file whose read text spells it: the author's.
        SlangcRegisterStripper.IsHoistedName(Emitted("tex_layer_0", 't', ModuleFile), entry, module).ShouldBeFalse();
        // Located in a file whose read text does not spell it: hoisted out of something there.
        SlangcRegisterStripper.IsHoistedName(Emitted("tex_texture_0", 't', ModuleFile), entry, other).ShouldBeTrue();
        // Located in a file not read: not a hoist until the text says so.
        SlangcRegisterStripper.IsHoistedName(Emitted("tex_layer_0", 't', ModuleFile), entry, null).ShouldBeFalse();
        // Not shaped like a hoist at all.
        SlangcRegisterStripper.IsHoistedName(Emitted("ModTex", 't', "core"), entry, null).ShouldBeFalse();
    }

    [Theory]
    // slangc's own embedded modules (measured #line file names).
    [InlineData("core", true)]
    [InlineData("hlsl.meta.slang", true)]
    [InlineData("core.meta.slang", true)]
    [InlineData("glsl", true)]
    // A file imported by a relative path is located by a bare name too (measured, issue #325).
    [InlineData("m.slang", false)]
    [InlineData("./m.slang", false)]
    [InlineData("sub/m.slang", false)]
    [InlineData("C:/a/m.slang", false)]
    [InlineData("C:\\a\\m.slang", false)]
    [InlineData("<stdin>", false)]
    public void IsCoreModuleFile_IsSlangcsOwnModules_NotARelativeImport(string file, bool expected)
    {
        SlangcRegisterStripper.IsCoreModuleFile(file).ShouldBe(expected);
        new SlangcRegisterStripper.EmittedResource("Comb_texture_0", 't', file, 1).IsCoreHoist.ShouldBe(expected);
    }

    [Fact]
    public void Judge_DeclarationInsideANamespaceBlock_IsAGlobalDeclaration()
    {
        // 'namespace A { ... }' does not nest its declarations away from the global scope (slangc
        // even drops the namespace from the emitted name, issue #323), in the entry and in a
        // module alike; a struct's or function's braces still do.
        const string entry = "namespace A { Texture2D T ; namespace X { SamplerState S : register ( s2 ) ; } } struct M { Texture2D InStruct ; } ;";
        Judge(Emitted("T", 't'), entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
        Judge(Emitted("S", 's'), entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
        Judge(Emitted("InStruct", 't', "C:/a/m.slang"), entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);

        const string module = "module m ; namespace A . B { public Texture2D ModTex ; public SamplerState MS : register ( s1 ) ; }";
        Judge(Emitted("ModTex", 't', ModuleFile), "import \"C:/a/m.slang\" ;", (ModuleFile, module))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
        Judge(Emitted("MS", 's', ModuleFile), "import \"C:/a/m.slang\" ;", (ModuleFile, module))
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
    }

    [Fact]
    public void AuthorBindings_Spells_EveryIdentifierOfTheText_OutsideStrings()
    {
        var bindings = Bindings("module m ; public Texture2D tex_layer_0 : register ( t3 ) ; [ shader ( \"tex_other_0\" ) ]");

        bindings.Spells("tex_layer_0").ShouldBeTrue();
        bindings.Spells("register").ShouldBeTrue();
        bindings.Spells("tex").ShouldBeFalse();
        bindings.Spells("tex_other_0").ShouldBeFalse();
    }
}
