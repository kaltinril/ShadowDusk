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

    private static SlangcRegisterStripper.RegisterVerdict Judge(
        SlangcRegisterStripper.EmittedResource resource, string entry, string? otherFiles = null) =>
        SlangcRegisterStripper.Judge(
            resource,
            SlangcRegisterStripper.AuthorBindings.Parse(entry),
            otherFiles is null ? null : SlangcRegisterStripper.AuthorBindings.Parse(otherFiles));

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
    public void Judge_DeclarationFromAnotherFile_IsUnprovenUntilThatFileIsRead()
    {
        const string entry = "import \"m.slang\" ; SamplerState S ;";
        var modTex = Emitted("ModTex", 't', "m.slang");

        Judge(modTex, entry).ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        Judge(modTex, entry, "module m ; public Texture2D ModTex : register ( t3 ) ;").ShouldBe(SlangcRegisterStripper.RegisterVerdict.Keep);
        Judge(modTex, entry, "module m ; public Texture2D ModTex ;").ShouldBe(SlangcRegisterStripper.RegisterVerdict.Strip);
        // A register through a macro this file does not define: neither reading.
        Judge(modTex, entry, "module m ; public Texture2D ModTex : MSLOT ;").ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        // Only a local of that name: not a global declaration.
        Judge(modTex, entry, "module m ; void f ( ) { Texture2D ModTex ; }").ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
        // Two files disagree.
        Judge(modTex, entry, "public Texture2D ModTex : register ( t3 ) ; public Texture2D ModTex ;")
            .ShouldBe(SlangcRegisterStripper.RegisterVerdict.Unproven);
    }

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
    [InlineData("import \"D:\\x\\m.slang\" ;", "C:/a/outer.slang", "D:\\x\\m.slang")]
    public void QuotedImports_ResolveLikeSlangc(string preprocessed, string? importingFile, string expected) =>
        SlangcRegisterStripper.QuotedImports(preprocessed, importingFile).ShouldBe(new[] { expected });

    [Fact]
    public void QuotedImports_DoNotFollowAnImportByModuleName() =>
        SlangcRegisterStripper.QuotedImports("module outer ; __exported import inner ;", "C:/a/outer.slang").ShouldBeEmpty();
}
