#nullable enable

using FluentAssertions;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Bug-hunt 2026-07-27 M11: a vertex-attribute reflection failure must be a compile-time
/// <c>Result</c> error, never an empty table. The old empty-table fallback shipped a
/// <c>.mgfx</c> whose zero-element input layout failed at the consumer's first Draw with
/// an unattributed <c>E_INVALIDARG</c> (the exact Phase-54 crash class).
/// </summary>
public sealed class SpirvVertexInputReflectorTests
{
    [Fact]
    public void Read_GarbageBytes_FailsWithReflectionError()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8];

        var result = SpirvVertexInputReflector.Read(garbage);

        result.IsFailure.Should().BeTrue(
            "unparseable SPIR-V must fail the compile, not silently produce an empty attribute table");
        result.Error.Code.Should().Be("SD0101");
        result.Error.Message.Should().Contain("SPIR-V");
    }

    [Fact]
    public void Read_EmptyBlob_FailsWithReflectionError()
    {
        var result = SpirvVertexInputReflector.Read(System.ReadOnlyMemory<byte>.Empty);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0101");
    }
}
