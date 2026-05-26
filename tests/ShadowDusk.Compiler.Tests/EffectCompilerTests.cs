#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Integration tests for <see cref="EffectCompiler"/>.
/// All tests that invoke DXC or SPIRV-Cross are tagged Category=Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EffectCompilerTests
{
    // ---------------------------------------------------------------------------
    // Fixture paths
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Walk up from the test assembly's base directory until tests/fixtures is found.
    /// This mirrors the pattern used in ShadowDusk.Integration.Tests.
    /// </summary>
    private static readonly string FixturesDir = FindFixturesDir();

    private static string ShaderPath(string fileName)
        => Path.Combine(FixturesDir, "shaders", fileName);

    private static string FindFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir.Parent is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate tests/fixtures directory. " +
            "Ensure the test is running from a directory under the repository root.");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileFileAsync(
        string shaderFileName,
        PlatformTarget target,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string source = await File.ReadAllTextAsync(ShaderPath(shaderFileName), cancellationToken);
        var compiler = new EffectCompiler();
        var effectiveOptions = options ?? new CompilerOptions { Target = target };
        return await compiler.CompileAsync(source, effectiveOptions, cancellationToken);
    }

    // ---------------------------------------------------------------------------
    // Success path — fixture files
    // ---------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_Minimal_OpenGL_ReturnsBytes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileFileAsync("Minimal.fx", PlatformTarget.OpenGL, cancellationToken: cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("compiled output must contain bytes");
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task Compile_Minimal_DirectX_ReturnsBytes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileFileAsync("Minimal.fx", PlatformTarget.DirectX, cancellationToken: cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("compiled output must contain bytes");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_Textured_OpenGL_ReturnsBytes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileFileAsync("textured.fx", PlatformTarget.OpenGL, cancellationToken: cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("compiled output must contain bytes");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_Cbuffer_OpenGL_HasParameters()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileFileAsync("cbuffer.fx", PlatformTarget.OpenGL, cancellationToken: cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("compiled output must contain bytes");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_MultiPass_OpenGL_HasTwoPasses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileFileAsync("multipass.fx", PlatformTarget.OpenGL, cancellationToken: cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("compiled output must contain bytes");
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task Compile_Minimal_Vulkan_ReturnsBytes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await CompileFileAsync("Minimal.fx", PlatformTarget.Vulkan, cancellationToken: cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("compiled output must contain bytes");
    }

    // ---------------------------------------------------------------------------
    // Determinism
    // ---------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_Deterministic_SameBytesOnRepeat()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        string source = await File.ReadAllTextAsync(ShaderPath("Minimal.fx"), cts.Token);
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL };
        var compiler = new EffectCompiler();

        var first  = await compiler.CompileAsync(source, options, cts.Token);
        var second = await compiler.CompileAsync(source, options, cts.Token);

        first.IsSuccess.Should().BeTrue(
            because: first.IsFailure ? FormatErrors(first.Error) : "first compilation must succeed");
        second.IsSuccess.Should().BeTrue(
            because: second.IsFailure ? FormatErrors(second.Error) : "second compilation must succeed");

        first.Value.Data.Should().Equal(second.Value.Data,
            because: "identical inputs must produce byte-identical output (determinism)");
    }

    // ---------------------------------------------------------------------------
    // Debug flag
    // ---------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_Debug_DoesNotFail()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        string source = await File.ReadAllTextAsync(ShaderPath("Minimal.fx"), cts.Token);
        var options = new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            Debug  = true,
        };
        var compiler = new EffectCompiler();

        var result = await compiler.CompileAsync(source, options, cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "debug compilation must succeed");
        result.Value.Data.Should().NotBeEmpty("debug compilation must produce output bytes");
    }

    // ---------------------------------------------------------------------------
    // In-memory include resolver
    // ---------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_InMemoryIncludes_Resolves()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Helpers.fxh provides a trivial helper function used by the inline source.
        const string helperSource = """
            float4 ApplyTint(float4 color)
            {
                return color * float4(1.0, 1.0, 1.0, 1.0);
            }
            """;

        const string shaderSource = """
            #include "Helpers.fxh"

            struct VertexInput  { float4 Position : POSITION; float4 Color : COLOR0; };
            struct PixelInput   { float4 Position : SV_POSITION; float4 Color : COLOR0; };

            PixelInput VS(VertexInput input)
            {
                PixelInput output;
                output.Position = input.Position;
                output.Color    = input.Color;
                return output;
            }

            float4 PS(PixelInput input) : SV_TARGET
            {
                return ApplyTint(input.Color);
            }

            technique Technique1
            {
                pass Pass1
                {
                    VertexShader = compile vs_4_0 VS();
                    PixelShader  = compile ps_4_0 PS();
                }
            }
            """;

        var resolver = new InMemoryIncludeResolver(
            new Dictionary<string, string> { ["Helpers.fxh"] = helperSource });

        var options = new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = resolver,
        };

        var compiler = new EffectCompiler();
        var result   = await compiler.CompileAsync(shaderSource, options, cts.Token);

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? FormatErrors(result.Error) : "in-memory include must resolve successfully");
        result.Value.Data.Should().NotBeEmpty("compilation with resolved include must produce bytes");
    }

    // ---------------------------------------------------------------------------
    // Failure paths
    // ---------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_SyntaxError_ReturnsErrors()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Intentionally invalid HLSL — `this_is_not_valid_hlsl` is neither a
        // statement nor a declaration.
        const string badHlsl = """
            float4 VS(float4 pos : POSITION) : SV_POSITION {
                this_is_not_valid_hlsl
                return pos;
            }
            technique Technique1 { pass Pass1 { VertexShader = compile vs_4_0 VS(); PixelShader = compile ps_4_0 VS(); } }
            """;

        var compiler = new EffectCompiler();
        var result   = await compiler.CompileAsync(badHlsl, new CompilerOptions { Target = PlatformTarget.OpenGL }, cts.Token);

        result.IsFailure.Should().BeTrue("invalid HLSL must produce a failure result");
        result.Error.Should().NotBeEmpty("at least one error must be reported");
        result.Error[0].Line.Should().BeGreaterThan(0, "error must carry a source line number");
        result.Error[0].Message.Should().NotBeNullOrWhiteSpace("error must carry the compiler's message text");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_MissingInclude_ReturnsError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The include resolver is not configured, so Missing.fxh cannot be found.
        const string shaderSource = """
            #include "Missing.fxh"
            float4 VS(float4 pos : POSITION) : SV_POSITION { return pos; }
            technique Technique1 { pass Pass1 { VertexShader = compile vs_4_0 VS(); PixelShader = compile ps_4_0 VS(); } }
            """;

        var compiler = new EffectCompiler();
        var result   = await compiler.CompileAsync(shaderSource, new CompilerOptions { Target = PlatformTarget.OpenGL }, cts.Token);

        result.IsFailure.Should().BeTrue("a missing include must produce a failure result");
        result.Error.Should().NotBeEmpty("at least one error must be reported for the missing include");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string FormatErrors(ShaderError[] errors)
        => string.Join("; ", errors.Select(e => e.FxcFormattedMessage));
}
