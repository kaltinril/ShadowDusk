#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// PURE tests for the transpiler's input guard: a SPIR-V blob whose byte length is not a
/// multiple of 4 must fail loudly (SD0100) BEFORE the native call — previously
/// <c>MemoryMarshal.Cast</c> silently dropped the tail bytes and handed SPIRV-Cross a
/// truncated module. The guard returns before any P/Invoke, so no native is needed here.
/// </summary>
public sealed class SpirvCrossGlslTranspilerGuardTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1023)]
    public void Transpile_NonMultipleOfFourLength_FailsWithSD0100(int byteLength)
    {
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(new byte[byteLength]);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0100");
        result.Error.Message.Should().Contain("multiple of 4");
    }
}
