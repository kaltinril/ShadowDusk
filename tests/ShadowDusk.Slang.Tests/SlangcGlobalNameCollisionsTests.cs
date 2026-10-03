#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;
using Collisions = ShadowDusk.Slang.SlangcGlobalNameCollisions;
using Kind = ShadowDusk.Slang.SlangcGlobalNameCollisions.DeclarationKind;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #323, the pure part: which global declarations a Slang text holds, in which namespace,
/// and which pairs of them slangc's <c>-no-mangle</c> output merges. No disk, no process. The
/// shapes are the ones measured through slangc v2026.14.1 (see the class doc of
/// <see cref="SlangcGlobalNameCollisions"/>); the real-slangc proof is
/// <see cref="SlangNamespaceCollisionTests"/>.
/// </summary>
public sealed class SlangcGlobalNameCollisionsTests
{
    private const string Entry = "Entry.slang";

    private static IReadOnlyList<Collisions.GlobalDeclaration> Raw(string source) =>
        Collisions.Scan(source, Entry, rawSource: true);

    private static IReadOnlyList<Collisions.GlobalDeclaration> Preprocessed(string text, string file = Entry) =>
        Collisions.Scan(text, file, rawSource: false);

    private static string[] Qualified(IEnumerable<Collisions.GlobalDeclaration> declarations) =>
        declarations.Select(d => d.QualifiedName).ToArray();

    // ---- Reading the raw source --------------------------------------------------------------

    [Fact]
    public void Scan_FindsGlobalsInNamespaces_NestedDottedAndColonSeparated_WithLines()
    {
        const string source = """
            namespace A { Texture2D T; }
            namespace B
            {
                namespace X { Texture2D T; SamplerState S; }
            }
            namespace C.Y { Texture2D T; }
            namespace D::Z { Texture2D T; }
            Texture2D T;
            """;

        IReadOnlyList<Collisions.GlobalDeclaration> found = Raw(source);

        Qualified(found).ShouldBe(["A.T", "B.X.T", "B.X.S", "C.Y.T", "D.Z.T", "T"]);
        found[0].ShouldBe(new Collisions.GlobalDeclaration("T", "A", Entry, 1, 25, Kind.Variable));
        found[1].ShouldBe(new Collisions.GlobalDeclaration("T", "B.X", Entry, 4, 29, Kind.Variable));
        found[5].ShouldBe(new Collisions.GlobalDeclaration("T", "", Entry, 8, 11, Kind.Variable));
    }

    [Fact]
    public void Scan_FindsEveryDeclaratorShape()
    {
        const string source = """
            Texture2D<float4> Tex : register(t3);
            Sampler2D Comb : register(t2) : register(s3);
            SamplerState A, B;
            float4 Arr[2] = { float4(1, 0, 0, 1), float4(0, 1, 0, 1) };
            public static const float K = 1.0;
            uniform float4 Tint;
            ParameterBlock<P> Blk;
            Texture2D Many[3];
            namespace N { static float4 Acc = float4(0, 0, 0, 0); }
            """;

        Qualified(Raw(source)).ShouldBe(["Tex", "Comb", "A", "B", "Arr", "K", "Tint", "Blk", "Many", "N.Acc"]);
    }

    [Fact]
    public void Scan_RecordsAConstantBuffersNameAsAGroup_AndItsMembersAsGlobalsOfItsNamespace()
    {
        const string source = """
            namespace A { cbuffer C : register(b0) { float4 Tint; float Fade; } }
            cbuffer D { float4x4 World; }
            """;

        IReadOnlyList<Collisions.GlobalDeclaration> found = Raw(source);

        Qualified(found).ShouldBe(["A.C", "A.Tint", "A.Fade", "D", "World"]);
        found[0].Kind.ShouldBe(Kind.ParameterGroup);
        found[1].Kind.ShouldBe(Kind.Variable);
        found[3].Kind.ShouldBe(Kind.ParameterGroup);
    }

    [Fact]
    public void Scan_IgnoresFunctionsTypesAttributesAndTheirBodies()
    {
        const string source = """
            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                Texture2D local;
                return float4(uv, 0, 1);
            }
            namespace A { float4 f(float2 uv) { return float4(uv, 0, 1); } }
            namespace B { float4 f(float2 uv); }
            T g<T>(T x) where T : IFoo { return x; }
            __generic<T> T h(T x) { return x; }
            struct S { Texture2D inStruct; SamplerState s; };
            interface IFoo { float4 get(); }
            enum E { One, Two }
            extension S { float4 m() { return 0; } }
            typealias Tex = Texture2D<float4>;
            typedef float4 V;
            float4 operator+(S a, S b) { return 0; }
            Texture2D Only;
            """;

        Qualified(Raw(source)).ShouldBe(["Only"]);
    }

    [Fact]
    public void Scan_SkipsPreprocessorLinesAndComments_InTheRawSource()
    {
        const string source = """
            #define DECL(ns, n) namespace ns { Texture2D n; }
            #include "other.hlsli"
            // namespace A { Texture2D Commented; }
            /* namespace B { Texture2D AlsoCommented; } */
            import "m.slang";
            __exported import other;
            module m;
            namespace A { Texture2D T; }
            """;

        Qualified(Raw(source)).ShouldBe(["A.T"]);
    }

    [Fact]
    public void Scan_IgnoresAStringThatSpellsADeclaration() =>
        Raw("[shader(\"namespace A { Texture2D T; }\")]\nfloat4 MainPS() : SV_Target { return 0; }\n").ShouldBeEmpty();

    // ---- Reading slangc's -E token stream ---------------------------------------------------

    [Fact]
    public void Scan_ReadsSlangcsPreprocessedTokenStream_WithNoLines()
    {
        const string text =
            "namespace A { Texture2D T ; } namespace B { Texture2D T ; } SamplerState S ; " +
            "[ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : SV_Position , float2 uv : TEXCOORD0 ) : SV_Target " +
            "{ return A . T . Sample ( S , uv ) + B . T . Sample ( S , uv ) ; } \n";

        IReadOnlyList<Collisions.GlobalDeclaration> found = Preprocessed(text, "C:/a/m.slang");

        Qualified(found).ShouldBe(["A.T", "B.T", "S"]);
        found.ShouldAllBe(d => d.Line == 0 && d.Column == 0 && d.File == "C:/a/m.slang");
    }

    [Fact]
    public void Scan_ReadsAModulesTokenStream_PastItsModuleDeclarationAndModifiers() =>
        Qualified(Preprocessed("module mN ; namespace A { public Texture2D T ; public SamplerState MS ; public float4 fetchA ( float2 uv ) { return T . Sample ( MS , uv ) ; } } "))
            .ShouldBe(["A.T", "A.MS"]);

    // ---- Which pairs merge ------------------------------------------------------------------

    [Fact]
    public void FindCollisions_TwoGlobalsOfOneNameInDifferentNamespaces_IncludingTheGlobalScope()
    {
        Collision("namespace A { Texture2D T; }\nnamespace B { Texture2D T; }\n").ShouldNotBeNull()
            .ShouldSatisfyAllConditions(
                c => c.First.QualifiedName.ShouldBe("A.T"),
                c => c.Second.QualifiedName.ShouldBe("B.T"));
        Collision("namespace A { Texture2D T; }\nTexture2D T;\n").ShouldNotBeNull();
        Collision("namespace A { namespace X { Sampler2D Comb; } }\nnamespace B { Sampler2D Comb; }\n").ShouldNotBeNull();
        Collision("namespace A { float4 Tint; }\nnamespace B { float4 Tint; }\n").ShouldNotBeNull();
        Collision("namespace A { static const float4 K = 1; }\nnamespace B { static const float4 K = 2; }\n").ShouldNotBeNull();
        Collision("namespace A { cbuffer C { float4 X; } }\nnamespace B { cbuffer C { float4 Y; } }\n").ShouldNotBeNull()
            .First.Kind.ShouldBe(Kind.ParameterGroup);
        Collision("namespace A { cbuffer CA { float4 Tint; } }\nnamespace B { cbuffer CB { float4 Tint; } }\n").ShouldNotBeNull()
            .First.Name.ShouldBe("Tint");
    }

    [Fact]
    public void FindCollisions_NothingForDistinctNames_Functions_OrTypes()
    {
        Collision("namespace A { Texture2D T; }\nnamespace B { Texture2D U; }\n").ShouldBeNull();
        Collision("namespace A { float4 f(float2 uv) { return 0; } }\nnamespace B { float4 f(float2 uv) { return 0; } }\n").ShouldBeNull();
        Collision("namespace A { struct S { float4 x; }; }\nnamespace B { struct S { float4 y; }; }\n").ShouldBeNull();
        Collision("namespace A { Texture2D T; }\nnamespace A { Texture2D T; }\n").ShouldBeNull(); // slangc's own E30200
        Collision("Texture2D T;\nSamplerState S;\n").ShouldBeNull();
    }

    [Fact]
    public void FindCollisions_AcrossFiles_TwoModulesAtOnePath_ButNotTheEntryBesideAModule()
    {
        var entryT = Preprocessed("Texture2D T ; ", Entry);
        var moduleA = Preprocessed("module ma ; public Texture2D T ; ", "C:/a/ma.slang");
        var moduleB = Preprocessed("module mb ; public Texture2D T ; ", "C:/a/mb.slang");
        var moduleNs = Preprocessed("module mN ; namespace A { public Texture2D T ; } ", "C:/a/mN.slang");

        // Two modules, each a plain T used inside itself: merged (measured).
        Collisions.FindCollisions([.. moduleA, .. moduleB], Entry).ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(
                c => c.First.File.ShouldBe("C:/a/ma.slang"),
                c => c.Second.File.ShouldBe("C:/a/mb.slang"));
        // The entry's plain T beside a module's: slangc's own ambiguity error when it matters.
        Collisions.FindCollisions([.. entryT, .. moduleA], Entry).ShouldBeEmpty();
        // The entry's plain T beside a module's namespaced T: merged (measured).
        Collisions.FindCollisions([.. entryT, .. moduleNs], Entry).ShouldHaveSingleItem();
        // One file, two spellings of its path.
        Collisions.FindCollisions([.. moduleA, .. Preprocessed("module ma ; public Texture2D T ; ", "C:\\a\\ma.slang")], Entry).ShouldBeEmpty();
    }

    [Fact]
    public void FindCollisions_ReportsEachNameOnce()
    {
        const string source = "namespace A { Texture2D T; }\nnamespace B { Texture2D T; }\nnamespace C { Texture2D T; }\n";

        Collisions.FindCollisions(Raw(source), Entry).ShouldHaveSingleItem();
    }

    // ---- The error ---------------------------------------------------------------------------

    [Fact]
    public void Error_IsSD0643_NamingBothDeclarationsWithFileLineAndColumn_LocatedAtTheSecond()
    {
        Collisions.Collision collision = Collision("namespace A { Texture2D T; }\nnamespace B { Texture2D T; }\n")!;

        ShaderError error = Collisions.Error(collision, Entry);

        (error.File, error.Line, error.Column, error.Code).ShouldBe((Entry, 2, 25, "SD0643"));
        error.Message.ShouldContain("'A.T' (Entry.slang:1:25)", Case.Sensitive);
        error.Message.ShouldContain("'B.T' (Entry.slang:2:25)", Case.Sensitive);
        error.Message.ShouldContain("-no-mangle", Case.Sensitive);
        error.Message.ShouldContain("ONE declaration named 'T'", Case.Sensitive);
    }

    [Fact]
    public void Error_ForDeclarationsWithoutLines_NamesTheFilesAndLandsOnTheEntrySource()
    {
        var a = Preprocessed("module ma ; public Texture2D T ; ", "C:/a/ma.slang");
        var b = Preprocessed("module mb ; public Texture2D T ; ", "C:/a/mb.slang");
        Collisions.Collision collision = Collisions.FindCollisions([.. a, .. b], Entry).Single();

        ShaderError error = Collisions.Error(collision, Entry);

        (error.File, error.Line, error.Column).ShouldBe((Entry, 0, 0));
        error.Message.ShouldContain("'T' (C:/a/ma.slang)", Case.Sensitive);
        error.Message.ShouldContain("'T' (C:/a/mb.slang)", Case.Sensitive);
    }

    [Fact]
    public void Locate_GivesAPreprocessedDeclarationTheLineOfTheSameOneInTheRawSource()
    {
        const string source = "#define SLOT register(t1)\nnamespace A { Texture2D T : SLOT; }\nnamespace B { Texture2D T; }\n";
        var fromPreprocessed = Collisions.FindCollisions(
            Preprocessed("namespace A { Texture2D T : register ( t1 ) ; } namespace B { Texture2D T ; } "), Entry).Single();

        Collisions.Collision located = Collisions.Locate(fromPreprocessed, Raw(source));

        (located.First.Line, located.First.Column).ShouldBe((2, 25));
        (located.Second.Line, located.Second.Column).ShouldBe((3, 25));
        // A declaration the raw text does not show (formed by a macro) keeps line 0.
        var macroFormed = Collisions.FindCollisions(
            Preprocessed("namespace A { Texture2D M ; } namespace B { Texture2D M ; } "), Entry).Single();
        Collisions.Locate(macroFormed, Raw(source)).First.Line.ShouldBe(0);
    }

    // ---- The free gates ----------------------------------------------------------------------

    [Theory]
    [InlineData("Texture2D T;\n", null, null, false)]
    [InlineData("namespace A { Texture2D T; }\n", null, null, true)]
    [InlineData("// namespace in a comment\nTexture2D T;\n", null, null, true)]
    [InlineData("NS\nTexture2D T;\n", "NS", "namespace A { Texture2D T; }", true)]
    [InlineData("NS\nTexture2D T;\n", "NS", "1", false)]
    public void MaySpellNamespace_ReadsTheSourceAndTheDefines(string source, string? name, string? value, bool expected) =>
        Collisions.MaySpellNamespace(source, name is null ? [] : [new UserDefine(name, value!)]).ShouldBe(expected);

    [Theory]
    [InlineData("namespace A { Texture2D T; }\nimport \"m.slang\";\n", null, null, true)]
    [InlineData("#if OPENGL\nnamespace A { Texture2D T; }\n#endif\n", null, null, false)]
    [InlineData("namespace A { Texture2D T; } // a \\ splice\n", null, null, false)]
    // A -D name the source spells could rename or form a declaration.
    [InlineData("namespace A { Texture2D T; }\n", "T", "U", false)]
    [InlineData("namespace A { Texture2D T; }\n", "QUALITY", "2", true)]
    [InlineData("namespace A { Texture2D T; }\n", "NS", "namespace B { Texture2D T; }", false)]
    public void RawTextIsWhatSlangcCompiles_OnlyWithoutDirectivesSplicesOrADefineItSpells(
        string source, string? name, string? value, bool expected) =>
        Collisions.RawTextIsWhatSlangcCompiles(source, name is null ? [] : [new UserDefine(name, value!)]).ShouldBe(expected);

    private static Collisions.Collision? Collision(string source) =>
        Collisions.FindCollisions(Raw(source), Entry).SingleOrDefault();
}
