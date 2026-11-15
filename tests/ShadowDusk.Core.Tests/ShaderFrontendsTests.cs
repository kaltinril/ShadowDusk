#nullable enable

using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// The registration rules of <see cref="ShaderFrontends"/>. Nothing here registers a frontend
/// that would stay: the registry is process-wide, and other tests in this assembly run beside it.
/// </summary>
public sealed class ShaderFrontendsTests
{
    [Theory]
    [InlineData(ShaderFormat.Unknown)]
    [InlineData(ShaderFormat.FxEffect)]
    [InlineData(ShaderFormat.Slang)]
    public void Register_RefusesAFormatShadowDuskReadsItself(ShaderFormat format)
    {
        var ex = Should.Throw<ArgumentException>(() => ShaderFrontends.Register(new StubFrontend(format)));

        ex.ParamName.ShouldBe("frontend");
        ex.Message.ShouldContain(format.ToString(), Case.Sensitive);
    }

    [Fact]
    public void Register_RefusesNull() =>
        Should.Throw<ArgumentNullException>(() => ShaderFrontends.Register(null!));

    [Theory]
    [InlineData(ShaderFormat.Unknown)]
    [InlineData(ShaderFormat.FxEffect)]
    [InlineData(ShaderFormat.Slang)]
    public void Find_HasNoFrontendForTheBuiltInFormats(ShaderFormat format) =>
        ShaderFrontends.Find(format).ShouldBeNull();

    private sealed class StubFrontend(ShaderFormat format) : IShaderFrontend
    {
        public ShaderFormat Format => format;

        public Result<ShaderFrontendOutput, ShaderError[]> ToFx(string source, string? nameHint) =>
            Result<ShaderFrontendOutput, ShaderError[]>.Ok(new ShaderFrontendOutput(source, []));
    }
}
