#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;
using Names = ShadowDusk.Slang.SlangcHoistedResourceNames;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #302, the pure mapping: which emitted texture names are slangc's own (hoisted out of a
/// combined sampler or another aggregate), which author name each maps back to, and which
/// collisions and unprovable shapes fail by code. No disk, no process: the emissions and the
/// preprocessed texts below are slangc v2026.14.1's, verbatim in shape. The real-slangc proof is
/// <see cref="SlangHoistedTextureNameTests"/>.
/// </summary>
public sealed class SlangcHoistedResourceNamesTests
{
    private const string Entry = """
        [shader("fragment")]
        float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }
        """;

    // What slangc emits for 'Sampler2D Comb;' (measured): the halves are located in its core module.
    private const string CombinedEmission = """
        #line 93 "core"
        Texture2D<float4 > Comb_texture_0 : register(t0);

        #line 1188 "hlsl.meta.slang"
        SamplerState Comb_sampler_0;

        #line 3 "<stdin>"
        float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            return Comb_texture_0.Sample(Comb_sampler_0, uv_0);
        }

        """;

    private static Names.Decision Decide(
        string emission, string source, Names.PreprocessedTexts? texts = null, params UserDefine[] defines) =>
        Names.Decide(emission, source, "Names.slang", defines, texts);

    private static Names.PreprocessedTexts Texts(string entry, params string[] others) => new(entry, others);

    // ---- Reading the emission --------------------------------------------------------------

    [Fact]
    public void FindGlobals_ReadsEveryGlobalTextureAndSampler_WithItsLineDirectiveLocation()
    {
        const string emission = """
            #pragma pack_matrix(column_major)

            #line 93 "core"
            Texture2D<float4 > Comb_texture_0 : register(t2);

            #line 1188 "hlsl.meta.slang"
            SamplerState Comb_sampler_0;

            #line 6046 "core.meta.slang"
            Texture2D<float4 >  Arr_texture_0[int(2)] : register(t6);

            #line 6046
            SamplerComparisonState  Sh_sampler_0;

            #line 12 "<stdin>"
            TextureCube<float4 > Plain : register(t13, space1);

            #line 4
            float4 fetch_0(Texture2D<float4 > s_texture_0, SamplerState s_sampler_0, float2 uv_0)
            {
                Texture2D<float4 > local_texture_0;
                return s_texture_0.Sample(s_sampler_0, uv_0);
            }

            """;

        Names.FindGlobals(emission).ShouldBe(
        [
            new Names.GlobalResource("Comb_texture_0", IsTexture: true, "core", 93),
            new Names.GlobalResource("Comb_sampler_0", IsTexture: false, "hlsl.meta.slang", 1188),
            new Names.GlobalResource("Arr_texture_0", IsTexture: true, "core.meta.slang", 6046),
            new Names.GlobalResource("Sh_sampler_0", IsTexture: false, "core.meta.slang", 6046),
            new Names.GlobalResource("Plain", IsTexture: true, "<stdin>", 12),
        ]);
    }

    // ---- A combined sampler declared as a global -------------------------------------------

    [Fact]
    public void CombinedSampler_TextureHalfTakesTheAuthorsName_FromTheRawSourceAlone()
    {
        Names.Decision decision = Decide(CombinedEmission, "Sampler2D Comb;\n" + Entry);

        decision.Error.ShouldBeNull();
        decision.NeedsPreprocess.ShouldBeFalse();
        decision.Renames.ShouldBe(new Dictionary<string, string> { ["Comb_texture_0"] = "Comb" });
    }

    [Fact]
    public void Apply_RenamesEveryUseAndTheDeclaration_AndNothingElse()
    {
        const string emission = """
            #line 93 "Comb_texture_0.slang"
            Texture2D<float4 > Comb_texture_0 : register(t0);
            SamplerState Comb_sampler_0;
            float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                float4 Comb_texture_0x = Comb_texture_0.Sample(Comb_sampler_0, uv_0);
                return Comb_texture_0x + xComb_texture_0;
            }
            """;

        string renamed = Names.Apply(emission, new Dictionary<string, string> { ["Comb_texture_0"] = "Comb" });

        renamed.ShouldBe("""
            #line 93 "Comb_texture_0.slang"
            Texture2D<float4 > Comb : register(t0);
            SamplerState Comb_sampler_0;
            float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                float4 Comb_texture_0x = Comb.Sample(Comb_sampler_0, uv_0);
                return Comb_texture_0x + xComb_texture_0;
            }
            """);
    }

    [Theory]
    [InlineData("Sampler2D Comb[2];", "Texture2D<float4 >  Comb_texture_0[int(2)] : register(t6);", "SamplerState  Comb_sampler_0[int(2)];")]
    [InlineData("Sampler3D Comb;", "Texture3D<float4 > Comb_texture_0 : register(t2);", "SamplerState Comb_sampler_0;")]
    [InlineData("SamplerCube Comb : register(t3);", "TextureCube<float4 > Comb_texture_0 : register(t3);", "SamplerState Comb_sampler_0;")]
    [InlineData("Sampler2DShadow Comb;", "Texture2D<float > Comb_texture_0;", "SamplerComparisonState Comb_sampler_0;")]
    [InlineData("Sampler2D<float4> Comb;", "Texture2D<float4 > Comb_texture_0;", "SamplerState Comb_sampler_0;")]
    // slangc numbers a hoisted name against its other generated names (measured: a struct field
    // 'Comb_texture' takes the next number), so the halves' numbers need not match.
    [InlineData("Sampler2D Comb;", "Texture2D<float4 > Comb_texture_1;", "SamplerState Comb_sampler_0;")]
    public void EveryCombinedSamplerShape_MapsBackToItsGlobal(string declaration, string texture, string sampler)
    {
        string emitted = texture[(texture.IndexOf('>') + 1)..].Trim().Split('[', ' ', ';')[0];

        Names.Decision decision = Decide(
            $"#line 93 \"core\"\n{texture}\n#line 1188 \"hlsl.meta.slang\"\n{sampler}\n", declaration + "\n" + Entry);

        decision.Error.ShouldBeNull();
        decision.Renames.ShouldBe(new Dictionary<string, string> { [emitted] = "Comb" });
    }

    [Fact]
    public void GeneratedNameMentionedOnlyInACommentOrAString_IsStillGenerated()
    {
        // The comment a consumer hit by this bug would have written next to the declaration.
        const string source = """
            Sampler2D Comb; // reflects as Comb_texture_0, see effect.Parameters["Comb_texture_0"]
            /* Comb_texture_0 */
            """ + "\n" + Entry;

        Names.Decision decision = Decide(CombinedEmission, source);

        decision.NeedsPreprocess.ShouldBeFalse();
        decision.Renames.ShouldBe(new Dictionary<string, string> { ["Comb_texture_0"] = "Comb" });
    }

    [Fact]
    public void SamplerHalf_AndAHoistedSamplerOfAStruct_AreNeverRenamedOrRejected()
    {
        // A struct that holds only a sampler: nothing sets a sampler by name.
        const string emission = """
            #line 93 "core"
            SamplerState gS_s_0 : register(s0);

            #line 3 "<stdin>"
            Texture2D<float4 > SpriteTexture : register(t0);

            """;

        Names.Decision decision = Decide(emission, "struct S { SamplerState s; };\nS gS;\nTexture2D SpriteTexture;\n" + Entry);

        decision.ShouldBe(Names.Decision.Nothing);
    }

    // ---- Names the author wrote that merely look hoisted ------------------------------------

    [Fact]
    public void AuthorGlobalWithAHoistedLookingName_IsLeftAlone_WithoutAskingSlangc()
    {
        // slangc locates an author's own global at its declaration line (measured).
        const string emission = """
            #line 1 "<stdin>"
            Texture2D<float4 > tex_layer_0 : register(t0);

            #line 2
            Texture2D<float4 > my_tex_1 : register(t1);

            #line 3
            SamplerState S;

            """;

        Names.Decision decision = Decide(emission, "Texture2D tex_layer_0;\nTexture2D my_tex_1;\nSamplerState S;\n" + Entry);

        decision.ShouldBe(Names.Decision.Nothing);
    }

    [Fact]
    public void AuthorGlobalNamedLikeACombinedSamplersHalves_IsLeftAlone()
    {
        // Both spellings are the author's: no 'Comb' exists to hoist them from.
        const string emission = """
            #line 1 "<stdin>"
            Texture2D<float4 > Comb_texture_0 : register(t0);

            #line 2
            SamplerState Comb_sampler_0;

            """;

        Names.Decision decision = Decide(emission, "Texture2D Comb_texture_0;\nSamplerState Comb_sampler_0;\n" + Entry);

        decision.ShouldBe(Names.Decision.Nothing);
    }

    [Fact]
    public void ShaderWithNoHoistedLookingTexture_DecidesNothing_WhateverTheSourceSpells()
    {
        const string emission = "#line 1 \"<stdin>\"\nTexture2D<float4 > SpriteTexture : register(t0);\nSamplerState SpriteSampler;\n";

        Decide(emission, "#include \"x.hlsli\"\nimport y;\n#define A(n) n##_texture_0\n" + Entry)
            .ShouldBe(Names.Decision.Nothing);
    }

    // ---- When the raw source cannot decide --------------------------------------------------

    [Theory]
    [InlineData("#define DECLARE(n) Sampler2D n;\nDECLARE(Comb)\n")]            // declared through a macro
    [InlineData("#define PASTE(a) a##_texture_0\nSampler2D Comb;\n")]          // can paste an identifier
    [InlineData("#include \"decls.hlsli\"\n")]                                 // another file
    [InlineData("import decls;\n")]                                            // a module
    [InlineData("Sampler2D Comb;\nstatic const int Comb_texture_0_count = 0; // \\\n")] // a line splice
    public void SourceThatCanHideOrFormTheName_NeedsThePreprocessedText(string declarations)
    {
        Decide(CombinedEmission, declarations + Entry).NeedsPreprocess.ShouldBeTrue();
    }

    [Fact]
    public void CombinedSamplerNamedThroughADefine_NeedsThePreprocessedText_ThenMapsBack()
    {
        string source = "Sampler2D NAME;\n" + Entry;
        UserDefine[] defines = [new UserDefine("NAME", "Comb")];

        Decide(CombinedEmission, source, texts: null, defines).NeedsPreprocess.ShouldBeTrue();

        Names.Decision decision = Decide(
            CombinedEmission, source, Texts("Sampler2D Comb ; float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { }"), defines);
        decision.Error.ShouldBeNull();
        decision.Renames.ShouldBe(new Dictionary<string, string> { ["Comb_texture_0"] = "Comb" });
    }

    [Fact]
    public void PreprocessedText_ThatSpellsTheEmittedName_ProvesItIsTheAuthors()
    {
        // Declared through a macro body, so slangc's #line is the invocation, not the spelling.
        const string source = "#define DECL Texture2D Comb_texture_0; SamplerState Comb_sampler_0;\nDECL\n" + Entry;

        Decide(CombinedEmission, source).NeedsPreprocess.ShouldBeTrue();
        Decide(CombinedEmission, source, Texts("Texture2D Comb_texture_0 ; SamplerState Comb_sampler_0 ; float4 MainPS ( ) { }"))
            .ShouldBe(Names.Decision.Nothing);
    }

    [Fact]
    public void CombinedSamplerInAReadModule_MapsBackThroughTheModulesText()
    {
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > ModComb_texture_0 : register(t6);

            #line 1188 "hlsl.meta.slang"
            SamplerState ModComb_sampler_0;

            """;
        const string source = "import \"C:/shaders/m.slang\";\n" + Entry;

        Names.Decision decision = Decide(
            emission, source,
            Texts("import \"C:/shaders/m.slang\" ; float4 MainPS ( ) { return ModComb . Sample ( uv ) ; }",
                "module m ; public Sampler2D ModComb : register ( t6 ) ;"));

        decision.Error.ShouldBeNull();
        decision.Renames.ShouldBe(new Dictionary<string, string> { ["ModComb_texture_0"] = "ModComb" });
    }

    [Fact]
    public void NameNoReadTextExplains_WhileAnImportWasNotRead_IsUnproven()
    {
        // Declared in a module reached only by name: its text was never read, so the name may
        // be an author's own global there or a hoist of one. Not guessed either way.
        const string emission = """
            #line 6024 "core.meta.slang"
            Texture2D<float4 > Other_texture_0;

            #line 1188 "hlsl.meta.slang"
            SamplerState Other_sampler_0;

            """;

        Names.Decision decision = Decide(
            emission, "import inner;\n" + Entry, Texts("import inner ; float4 MainPS ( ) { return fetch ( uv ) ; }"));

        ShaderError error = decision.Error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0642");
        error.File.ShouldBe("Names.slang");
        error.Message.ShouldContain("'Other_texture_0'", Case.Sensitive);
        decision.Renames.ShouldBeEmpty();
    }

    // ---- A texture hoisted out of anything else has no author name --------------------------

    [Theory]
    // struct M { Texture2D t; SamplerState s; }; M gM;
    [InlineData("struct M { Texture2D t; SamplerState s; };\nM gM;\n", "gM_t_0", "gM", 2, 3)]
    // The same struct with a data field: slangc puts it in its global parameter block.
    [InlineData("struct M { Texture2D t; SamplerState s; float4 tint; };\n\n  M   gM;\n", "globalParams_gM_t_0", "gM", 3, 7)]
    // A combined sampler nested in a struct: 'gM_c' is not a global the author declared.
    [InlineData("struct M { Sampler2D c; };\nM gM;\n", "gM_c_texture_0", "gM", 2, 3)]
    [InlineData("struct M { Texture2D t; SamplerState s; };\nM gM[2];\n", "gM_t_0", "gM", 2, 3)]
    public void TextureHeldInAStruct_IsRejectedAtTheGlobalsDeclaration(
        string declarations, string emitted, string aggregate, int line, int column)
    {
        string emission =
            $"#line 93 \"core\"\nTexture2D<float4 > {emitted} : register(t0);\n#line 1188 \"hlsl.meta.slang\"\n" +
            $"SamplerState {emitted.Replace("_texture_0", "_sampler_0").Replace("_t_0", "_s_0")};\n";

        Names.Decision decision = Decide(emission, declarations + Entry);

        ShaderError error = decision.Error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0640");
        (error.File, error.Line, error.Column).ShouldBe(("Names.slang", line, column));
        error.Message.ShouldContain($"a texture held in '{aggregate}' under a name it generated, '{emitted}'", Case.Sensitive);
        error.Message.ShouldContain($"effect.Parameters[\"{emitted}\"]", Case.Sensitive);
        decision.Renames.ShouldBeEmpty();
    }

    [Theory]
    // cbuffer C { Texture2D T; SamplerState S; float4 x; }
    [InlineData("cbuffer C { Texture2D T; SamplerState S; float4 x; }\n", "C_T_0")]
    // float4 MainPS(uniform Texture2D T, ...)
    [InlineData("", "entryPointParams_T_0")]
    public void TextureHeldInABlockOrAnEntryParameter_IsRejected(string declarations, string emitted)
    {
        string emission = $"#line 2518 \"hlsl.meta.slang\"\nTexture2D<float4 > {emitted} : register(t1);\n";

        ShaderError error = Decide(emission, declarations + Entry).Error.ShouldNotBeNull();

        error.Code.ShouldBe("SD0640");
        (error.File, error.Line, error.Column).ShouldBe(("Names.slang", 0, 0));
        error.Message.ShouldContain($"a texture held in an aggregate under a name it generated, '{emitted}'", Case.Sensitive);
    }

    // ---- Collisions -------------------------------------------------------------------------

    [Fact]
    public void AuthorGlobalSpelledLikeTheGeneratedName_IsACollision()
    {
        // 'Sampler2D Comb; Texture2D Comb_texture_0;': slangc emits BOTH under one name (measured).
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > Comb_texture_0 : register(t0);

            #line 7 "<stdin>"
            SamplerState Comb_sampler_0 : register(s0);

            #line 2
            Texture2D<float4 > Comb_texture_0 : register(t1);

            """;

        ShaderError error = Decide(emission, "Sampler2D Comb;\nTexture2D Comb_texture_0;\n" + Entry).Error.ShouldNotBeNull();

        error.Code.ShouldBe("SD0641");
        error.Message.ShouldContain("declares the global 'Comb_texture_0' more than once", Case.Sensitive);
    }

    [Fact]
    public void NonResourceGlobalSpelledLikeTheGeneratedName_IsACollision()
    {
        // 'Sampler2D Comb; float4 Comb_texture_0;' (measured): a cbuffer member of that name.
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > Comb_texture_0 : register(t0);
            SamplerState Comb_sampler_0;
            cbuffer globalParams_0 : register(b0)
            {
                float4 Comb_texture_0;
            }
            """;

        ShaderError error = Decide(emission, "Sampler2D Comb;\nfloat4 Comb_texture_0;\n" + Entry).Error.ShouldNotBeNull();

        error.Code.ShouldBe("SD0641");
    }

    [Fact]
    public void AuthorNameAlreadyUsedInTheOutput_IsACollision_LocatedAtTheCombinedSampler()
    {
        // slangc merges 'namespace A { float4 Comb; }' and a global 'Sampler2D Comb' into one
        // flat scope (measured): the cbuffer member already holds the author's name.
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > Comb_texture_0 : register(t0);
            SamplerState Comb_sampler_0;
            cbuffer globalParams_0 : register(b0)
            {
                float4 Comb;
            }
            """;

        ShaderError error = Decide(emission, "namespace A { float4 Comb; }\n  Sampler2D Comb;\n" + Entry).Error.ShouldNotBeNull();

        error.Code.ShouldBe("SD0641");
        (error.File, error.Line, error.Column).ShouldBe(("Names.slang", 2, 13));
        error.Message.ShouldContain("already uses the identifier 'Comb'", Case.Sensitive);
    }

    [Fact]
    public void SemanticSpelledLikeTheAuthorName_IsNotACollision()
    {
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > Comb_texture_0 : register(t0);
            SamplerState Comb_sampler_0;
            struct PSIn_0
            {
                float2 uv_0 : Comb;
            };
            """;

        Names.Decision decision = Decide(emission, "Sampler2D Comb;\nstruct PSIn { float2 uv : Comb; };\n" + Entry);

        decision.Error.ShouldBeNull();
        decision.Renames.ShouldBe(new Dictionary<string, string> { ["Comb_texture_0"] = "Comb" });
    }
}
