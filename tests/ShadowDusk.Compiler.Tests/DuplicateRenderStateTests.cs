#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// PURE unit tests (no disk, no native compiler) for duplicated render-state keys in a
/// pass: <c>ZEnable = true; ZEnable = false;</c> is valid fxc input whose LAST assignment
/// wins. Historically the MGFX path built the state dictionary with
/// <c>ToDictionary</c>, which threw a raw <c>ArgumentException</c> (surfacing as X0099)
/// on the duplicate; the FNA path already did last-wins. The passes here carry NO shader
/// entry points, so the pipeline runs end-to-end (render-state parse → MGFX write)
/// without ever touching DXC / SPIRV-Cross / vkd3d.
/// </summary>
public sealed class DuplicateRenderStateTests
{
    /// <summary>Never invoked (no entry points compile), but keeps the test native-free.</summary>
    private sealed class ThrowingTranspiler : ISpirvToGlslTranspiler
    {
        public Result<GlslSource, ShaderError> Transpile(
            ReadOnlyMemory<byte> spirvBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("must not be reached");
    }

    private static EffectCompiler CreateCompiler() => new(
        dxcCompilerFactory: () => throw new InvalidOperationException("DXC must not be constructed"),
        glslTranspilerFactory: () => new ThrowingTranspiler());

    private static CompilerOptions OpenGlOptions => new()
    {
        Target         = PlatformTarget.OpenGL,
        SourceFileName = "inline.fx",
    };

    [Fact]
    public void DuplicateRenderStateKey_DoesNotThrow_AndCompiles()
    {
        const string source = """
            technique T
            {
                pass P
                {
                    ZEnable = true;
                    ZEnable = false;
                }
            }
            """;

        var compiler = CreateCompiler();

        var result = compiler.Compile(source, OpenGlOptions);

        result.IsSuccess.Should().BeTrue(
            because: "a duplicated render-state key is valid fxc input (last assignment wins); errors: " +
                     (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>"));
    }

    [Fact]
    public void DuplicateRenderStateKey_LastAssignmentWins()
    {
        const string duplicated = """
            technique T
            {
                pass P
                {
                    ZEnable = true;
                    ZEnable = false;
                }
            }
            """;

        const string lastOnly = """
            technique T
            {
                pass P
                {
                    ZEnable = false;
                }
            }
            """;

        var compiler = CreateCompiler();

        var duplicatedResult = compiler.Compile(duplicated, OpenGlOptions);
        var lastOnlyResult   = compiler.Compile(lastOnly, OpenGlOptions);

        duplicatedResult.IsSuccess.Should().BeTrue();
        lastOnlyResult.IsSuccess.Should().BeTrue();

        // fxc semantics: the duplicated pass serializes EXACTLY like a pass declaring
        // only the last value — proving last-wins, not first-wins.
        duplicatedResult.Value.Data.Should().Equal(lastOnlyResult.Value.Data);
    }

    [Fact]
    public void DuplicateKeysWithDifferentCasing_AlsoLastWins()
    {
        // The state dictionary is case-insensitive (fxc state keys are too).
        const string duplicated = """
            technique T
            {
                pass P
                {
                    zenable = true;
                    ZENABLE = false;
                }
            }
            """;

        var compiler = CreateCompiler();

        var result = compiler.Compile(duplicated, OpenGlOptions);

        result.IsSuccess.Should().BeTrue(
            because: "case-insensitive duplicate state keys must not throw; errors: " +
                     (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>"));
    }
}
