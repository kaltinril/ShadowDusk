#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.HLSL.Ast;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Issue #446: the DirectX 12 + Vulkan source rewrite that maps a vertex shader's legacy
/// <c>: POSITION[0]</c> OUTPUT semantic to <c>: SV_Position</c> for DXC. It must rewrite the return
/// semantic, <c>out</c> parameters and the returned struct's members, and must never touch a
/// vertex INPUT (the <c>POSITION0</c> attribute MonoGame's input layout binds).
/// </summary>
public sealed class DxcVertexPositionOutputRewriterTests
{
    private static IReadOnlyList<TechniqueInfo> Techniques(params (string? Vs, string? Ps)[] passes) =>
        new[]
        {
            new TechniqueInfo
            {
                Name = "T",
                Span = default,
                IsEffect11 = false,
                Annotations = Array.Empty<AnnotationEntry>(),
                Passes = passes.Select((p, i) => new PassInfo
                {
                    Name = "P" + i,
                    Span = default,
                    VertexEntryPoint = p.Vs,
                    PixelEntryPoint = p.Ps,
                    VertexProfile = p.Vs is null ? null : "vs_4_0",
                    PixelProfile = p.Ps is null ? null : "ps_4_0",
                    RenderStates = Array.Empty<RenderStateEntry>(),
                    Annotations = Array.Empty<AnnotationEntry>(),
                }).ToArray(),
            },
        };

    private static string Rewrite(string hlsl, string vs = "VS", string? ps = "PS") =>
        DxcVertexPositionOutputRewriter.Rewrite(hlsl, Techniques((vs, ps)));

    [Fact]
    public void AnOutputStructBesideAnSvPositionTheEntryAlreadyWrites_IsLeftAlone()
    {
        // The entry writes SV_Position through its return; its `out` struct's POSITION0 member is
        // an ordinary output, and mapping it too would give DXC two SV_Position outputs (found by
        // the issue-pass review, 2026-10-31).
        const string hlsl = """
            struct VOut { float4 wpos : POSITION0; float2 uv : TEXCOORD0; };
            float4 VS(float4 p : POSITION0, out VOut o) : SV_Position { o.wpos = p; o.uv = 0; return p; }
            """;
        Rewrite(hlsl, ps: null).ShouldBeSameAs(hlsl);
    }

    [Fact]
    public void ReturnedStruct_PositionMember_BecomesSvPosition_InputStructUntouched()
    {
        const string hlsl = """
            struct VIn  { float4 Pos : POSITION0; float4 Col : COLOR0; float2 UV : TEXCOORD0; };
            struct VOut { float4 Pos : POSITION0; float4 Col : COLOR0; };
            VOut VS(VIn i) { VOut o; o.Pos = i.Pos; o.Col = i.Col; return o; }
            float4 PS(VOut i) : COLOR0 { return i.Col; }
            """;

        string result = Rewrite(hlsl);

        result.ShouldContain("struct VIn  { float4 Pos : POSITION0; float4 Col : COLOR0; float2 UV : TEXCOORD0; };", Case.Sensitive);
        result.ShouldContain("struct VOut { float4 Pos : SV_Position; float4 Col : COLOR0; };", Case.Sensitive);
        result.Split('\n').Length.ShouldBe(hlsl.Split('\n').Length, "line numbers must not move");
    }

    [Fact]
    public void ReturnSemantic_AndOutParameters_AreRewritten_InputParametersAreNot()
    {
        const string hlsl = """
            float4 VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) : POSITION0 { return pos; }
            void VS2(in float4 pos : POSITION, out float4 oPos : POSITION, out float2 oUv : TEXCOORD0) { oPos = pos; oUv = 0; }
            void VS3(inout float4 pos : POSITION0) { }
            """;

        string result = DxcVertexPositionOutputRewriter.Rewrite(hlsl, Techniques(("VS", null), ("VS2", null), ("VS3", null)));

        result.ShouldContain("float4 VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) : SV_Position { return pos; }", Case.Sensitive);
        result.ShouldContain("void VS2(in float4 pos : POSITION, out float4 oPos : SV_Position, out float2 oUv : TEXCOORD0)", Case.Sensitive);
        result.ShouldContain("void VS3(inout float4 pos : POSITION0)", Case.Sensitive);
    }

    [Fact]
    public void OutStructParameter_IsAnOutputStruct()
    {
        const string hlsl = """
            struct VOut { float4 Pos : POSITION; float2 UV : TEXCOORD0; };
            void VS(float4 p : POSITION0, out VOut o) { o.Pos = p; o.UV = 0; }
            """;

        Rewrite(hlsl, ps: null).ShouldContain("struct VOut { float4 Pos : SV_Position; float2 UV : TEXCOORD0; };", Case.Sensitive);
    }

    [Fact]
    public void SemanticsAreCaseInsensitive_AndPositionOneIsNotThePosition()
    {
        const string hlsl = """
            struct VOut { float4 Pos : Position0; float4 World : POSITION1; float4 T : POSITIONT; };
            VOut VS(float4 p : position) { VOut o = (VOut)0; return o; }
            """;

        string result = Rewrite(hlsl, ps: null);

        result.ShouldContain("float4 Pos : SV_Position;", Case.Sensitive);
        result.ShouldContain("float4 World : POSITION1;", Case.Sensitive);
        result.ShouldContain("float4 T : POSITIONT;", Case.Sensitive);
        result.ShouldContain("VS(float4 p : position)", Case.Sensitive);
    }

    [Fact]
    public void StructSharedWithAVertexInput_IsLeftAlone()
    {
        // Rewriting the shared definition would rename the vertex attribute the input layout binds.
        const string hlsl = """
            struct V { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
            V VS(V i) { return i; }
            """;

        Rewrite(hlsl, ps: null).ShouldBeSameAs(hlsl);
    }

    [Fact]
    public void AlreadySvPosition_IsLeftAlone()
    {
        const string hlsl = """
            struct VOut { float4 Pos : SV_POSITION; float4 Ndc : POSITION0; };
            VOut VS(float4 p : POSITION0) { VOut o = (VOut)0; return o; }
            void VS2(out float4 a : SV_Position, out float4 b : POSITION0) { a = 0; b = 0; }
            """;

        DxcVertexPositionOutputRewriter.Rewrite(hlsl, Techniques(("VS", null), ("VS2", null))).ShouldBeSameAs(hlsl);
    }

    [Fact]
    public void PixelShaderOnlyStructsAndNonEntryFunctions_AreUntouched()
    {
        // A pixel-input struct the vertex shader does not write, and a helper returning the vertex
        // struct, keep their semantics; only the entry's own return type is an output.
        const string hlsl = """
            struct PIn { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
            struct Helper { float4 Pos : POSITION0; };
            Helper Make() { Helper h = (Helper)0; return h; }
            float4 VS(float4 p : POSITION0) : SV_Position { return p; }
            float4 PS(PIn i) : COLOR0 { return 1; }
            """;

        Rewrite(hlsl).ShouldBeSameAs(hlsl);
    }

    [Fact]
    public void EveryConditionalDefinition_IsRewritten()
    {
        const string hlsl = """
            #if SM4
            struct VOut { float4 Pos : POSITION0; };
            #else
            struct VOut { float4 Pos : POSITION; };
            #endif
            VOut VS(float4 p : POSITION0); // prototype
            VOut VS(float4 p : POSITION0) { VOut o; o.Pos = p; return o; }
            """;

        string result = Rewrite(hlsl, ps: null);

        System.Text.RegularExpressions.Regex.Matches(result, @"struct VOut \{ float4 Pos : SV_Position; \};").Count.ShouldBe(2);
        result.ShouldContain("VOut VS(float4 p : POSITION0); // prototype", Case.Sensitive);
    }

    [Fact]
    public void NoVertexEntry_IsNoOp()
    {
        const string hlsl = "struct VOut { float4 Pos : POSITION0; };\nVOut VS() { VOut o = (VOut)0; return o; }";

        DxcVertexPositionOutputRewriter.Rewrite(hlsl, Techniques((null, "PS"))).ShouldBeSameAs(hlsl);
        DxcVertexPositionOutputRewriter.Rewrite(hlsl, Array.Empty<TechniqueInfo>()).ShouldBeSameAs(hlsl);
    }

    [Fact]
    public void CommentsAndStrings_DoNotConfuseTheScan()
    {
        const string hlsl = """
            // VOut VS(VIn i) : POSITION0
            /* struct VOut { float4 Pos : POSITION0; }; */
            struct VOut { float4 Pos /* : POSITION0 */ : POSITION0; };
            VOut /*x*/ VS(float4 p : POSITION0 /* , out float4 q : POSITION0 */) { VOut o; o.Pos = p; return o; }
            """;

        string result = Rewrite(hlsl, ps: null);

        result.ShouldContain("// VOut VS(VIn i) : POSITION0", Case.Sensitive);
        result.ShouldContain("/* struct VOut { float4 Pos : POSITION0; }; */", Case.Sensitive);
        result.ShouldContain(": SV_Position; };", Case.Sensitive);
        result.ShouldContain("VS(float4 p : POSITION0 /* , out float4 q : POSITION0 */)", Case.Sensitive);
    }
}
