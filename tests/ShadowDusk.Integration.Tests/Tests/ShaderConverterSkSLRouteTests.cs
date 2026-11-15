#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.ShaderToy;
using ShadowDusk.SkSL;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #506, the package-PRESENT half (this test assembly references <c>ShadowDusk.SkSL</c>):
/// <see cref="ShaderConverter"/> routes <c>.sksl</c> input through the frontend that package
/// registers. The package-absent half is <c>ShaderConverterTests</c> in
/// <c>ShadowDusk.Compiler.Tests</c>.
/// </summary>
public sealed class ShaderConverterSkSLRouteTests
{
    private const string Sksl = """
        uniform shader img;
        uniform float2 size;
        uniform half4 tint;
        half4 main(float2 coord) { return img.eval(coord) * tint; }
        """;

    // sk_FragCoord is refused with SD0666.
    private const string RejectedSksl = """
        half4 main(float2 coord) { return half4(sk_FragCoord.xy / 100.0, 0, 1); }
        """;

    [Fact]
    public void Find_ReturnsTheSkSLFrontend_AndItsLoadTimeRegistrationNeverReplacesOne()
    {
        IShaderFrontend? frontend = ShaderFrontends.Find(ShaderFormat.Sksl);

        frontend.ShouldNotBeNull();
        frontend.ShouldBeOfType<SkSLFrontend>();
        ShaderFrontends.TryRegister(new SkSLFrontend()).ShouldBeFalse();
        ShaderFrontends.Find(ShaderFormat.Sksl).ShouldBeSameAs(frontend);
    }

    [Fact]
    public void CanCompile_IsTrueForSkSL_OnlyWithTheSkslNameHint()
    {
        ShaderConverter.CanCompile(Sksl, "x.sksl").ShouldBeTrue();
        // No marker in the text: without the hint it is not a shader this package can recognize.
        ShaderConverter.CanCompile(Sksl).ShouldBeFalse();
        ShaderConverter.CanConvert(Sksl, "x.sksl").ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task Compile_Sksl_EqualsConvertThenCompile_ByteForByte(PlatformTarget target)
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = target };

        var routed = ShaderConverter.Compile(compiler, Sksl, options, "tint.sksl");
        var routedAsync = await ShaderConverter.CompileAsync(compiler, Sksl, options, "tint.sksl");

        ConvertResult converted = SkSLToFxConverter.Convert(Sksl);
        converted.Success.ShouldBeTrue();
        var separate = compiler.Compile(
            converted.Fx!, new CompilerOptions { Target = target, SourceFileName = "tint.generated.fx" });

        separate.IsSuccess.ShouldBeTrue(Describe(separate));
        routed.IsSuccess.ShouldBeTrue(Describe(routed));
        routedAsync.IsSuccess.ShouldBeTrue(Describe(routedAsync));
        routed.Value.Data.ShouldBe(separate.Value.Data);
        routedAsync.Value.Data.ShouldBe(separate.Value.Data);
    }

    [Fact]
    public void Compile_SkSLTheFrontendRejects_KeepsTheStableCode_AndIsNotUnsupportedInput()
    {
        var result = ShaderConverter.Compile(new EffectCompiler(), RejectedSksl, new CompilerOptions(), "bad.sksl");

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0666");
        error.File.ShouldBe("bad.sksl");
        error.Line.ShouldBe(1);
        error.Kind.ShouldNotBe(ShaderErrorKind.UnsupportedInput);
        error.Severity.ShouldBe(ShaderErrorSeverity.Error);
    }

    private static string Describe(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "";
}
