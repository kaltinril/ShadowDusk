#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// PURE unit tests (no disk, no process, no native) for <see cref="Vkd3dInputGuard"/>, the shared
/// refusal both vkd3d hosts apply before any native call (<see cref="Vkd3dCompileContract.CheckSource"/>).
/// The out-of-process proof that the refused shapes crash the pinned vkd3d, and that the refusal
/// keeps them away from it, is <c>ShadowDusk.Integration.Tests.Vkd3d.Vkd3dInputGuardTests</c>.
/// </summary>
public sealed class Vkd3dInputGuardTests
{
    private static ShaderError? Check(string source) =>
        Vkd3dCompileContract.CheckSource(source, "guard.fx");

    [Theory]
    [InlineData("float4 PS(float2 uv : TEXCOORD0) : SV_Target { (void)uv; return 0; }", 1, 48)]
    [InlineData("float4 PS(float2 uv : TEXCOORD0) : SV_Target {\n  (\n  void\n  )\n  -uv.x;\n  return 0;\n}", 2, 3)]
    [InlineData("float4 PS(float2 uv : TEXCOORD0) : SV_Target {\n\t(void)!uv.x; return 0; }", 2, 2)]
    [InlineData("float4 PS(float2 uv : TEXCOORD0) : SV_Target {\n(void)++uv.x; return 0; }", 2, 1)]
    [InlineData("float4 PS(float2 uv : TEXCOORD0) : SV_Target {\n(void)\"s\"; return 0; }", 2, 1)]
    public void ACastToVoid_IsRefusedWithSD0239_AtTheCast(string source, int line, int column)
    {
        ShaderError error = Check(source).ShouldNotBeNull();

        error.Code.ShouldBe(Vkd3dInputGuard.VoidCastCode);
        error.File.ShouldBe("guard.fx");
        (error.Line, error.Column).ShouldBe((line, column));
        error.Message.ShouldContain("X3017", Case.Sensitive);
    }

    [Theory]
    [InlineData("float F(void) { return 1; }")]
    [InlineData("float F(void);")]
    [InlineData("float4 PS(void) : SV_Target { return 0; }")]
    [InlineData("float F(void)\n{ return 1; }")]
    [InlineData("float (F)(void) { return 1; }")]
    [InlineData("struct S { float F(void) { return 1; } };")]
    [InlineData("void F(inout float a) { a += 1; }")]
    [InlineData("float F() { return 1; } // (void)F();")]
    [InlineData("float F() { /* (void)F(); */ return 1; }")]
    [InlineData("#if 0\nfloat F() { (void)0; return 1; }\n#endif\nfloat F() { return 1; }")]
    [InlineData("#define DISCARD(e) (void)(e)\nfloat F() { return 1; }")]
    [InlineData("float F() { return 1; }")]
    public void NoCastToVoid_IsLeftToVkd3d(string source)
    {
        Check(source).ShouldBeNull();
    }

    [Fact]
    public void ACastAMacroWrites_IsRefused_AtTheMacroCall()
    {
        ShaderError error = Check("#define DISCARD(e) (void)(e)\nfloat F(float x) {\n  DISCARD(x);\n  return x;\n}").ShouldNotBeNull();

        error.Code.ShouldBe(Vkd3dInputGuard.VoidCastCode);
        error.Line.ShouldBe(3);
    }

    [Fact]
    public void ACastInAnIncludedFile_IsLocatedThroughTheFlattenersLineDirectives()
    {
        const string flattened =
            "#line 1 \"main.fx\"\n" +
            "float A() { return 1; }\n" +
            "#line 1 \"helpers.fxh\"\n" +
            "float B(float x) {\n" +
            "  (void)x;\n" +
            "  return x;\n" +
            "}\n";

        ShaderError error = Check(flattened).ShouldNotBeNull();

        (error.File, error.Line).ShouldBe(("helpers.fxh", 2));
    }

    [Theory]
    [InlineData("#define X X\nfloat F() {\n  return X;\n}", "X", 3)]
    [InlineData("#define F(x) F(x)\nfloat F(float v) { return v; }", "F", 2)]
    [InlineData("#define A(x) B(x)\n#define B(x) A(x)\n\nfloat A(float v) { return v; }", "A", 4)]
    [InlineData("#define A B\n#define B A\nstatic const float A = 1;", "A", 3)]
    [InlineData("#define F G\n#define G(x) F(x)\nfloat G(float v) { return v; }", "G", 3)]
    [InlineData("#define X X\n#if X\n#endif\nfloat F() { return 1; }", "X", 2)]
    public void AMacroThatExpandsToItself_IsRefusedWithSD0240(string source, string name, int line)
    {
        ShaderError error = Check(source).ShouldNotBeNull();

        error.Code.ShouldBe(Vkd3dInputGuard.SelfReferenceCode);
        error.Line.ShouldBe(line);
        error.Message.ShouldContain($"macro '{name}'", Case.Sensitive);
    }

    [Theory]
    [InlineData("#define X X\nfloat F() { return 1; }")]                           // never used
    [InlineData("#define X X\n#ifdef X\n#endif\nfloat F() { return 1; }")]          // #ifdef does not expand
    [InlineData("#define X X\n#if 0\nfloat X;\n#endif\nfloat F() { return 1; }")]  // only in a dead branch
    [InlineData("#define F(x) F(x)\nfloat F;")]                                    // function-like, no call
    [InlineData("#define F(x) (x + F)\nstatic const float F = 1;\nfloat G() { return F(2); }")]
    [InlineData("#define ID(x) x\nfloat G(float v) { return ID(ID)(v); }")]          // '(' from after the expansion
    [InlineData("#define F(x) ((x) + 1)\nfloat G() { return F(F(1)); }")]
    [InlineData("#define F G\nfloat G(float v) { return F(v); }")]
    public void AMacroVkd3dExpandsAndFinishes_IsLeftToVkd3d(string source)
    {
        Check(source).ShouldBeNull();
    }

    [Fact]
    public void ASourceTheManagedPreprocessorCannotRead_IsStillScannedAsWritten()
    {
        // An unknown directive is an SD0009 for the managed view; vkd3d judges it as before, but a
        // cast in the code still never reaches it.
        ShaderError error = Check("#frobnicate\nfloat F(float x) { (void)x; return x; }").ShouldNotBeNull();

        error.Code.ShouldBe(Vkd3dInputGuard.VoidCastCode);
        error.Line.ShouldBe(2);
    }
}
