#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Permanent regression coverage for the README/quickstart usage pattern
/// <c>var compiler = new EffectCompiler(); await compiler.ValidateAsync(...)</c> — the
/// exact shape every getting-started doc uses for <see cref="EffectCompiler.CompileAsync"/>.
///
/// <para><c>Validate</c>/<c>ValidateAsync</c> are <see cref="ShaderCompilerValidationExtensions"/>
/// extension methods on <see cref="IShaderCompiler"/> rather than default interface
/// members precisely so this compiles: a C# default interface method is reachable ONLY
/// through an interface-typed reference, so <c>var compiler = new EffectCompiler();</c>
/// (the field type is <see cref="EffectCompiler"/>, not <see cref="IShaderCompiler"/>)
/// would fail to compile against a default interface method with
/// <c>CS1061: 'EffectCompiler' does not contain a definition for 'ValidateAsync'</c>. If
/// this file stops compiling, that regression is back.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class EffectCompilerValidateApiSurfaceTests
{
    // Validate's default target set is OpenGL + DirectX, so the source must be valid on
    // BOTH. The SM4-gated header is what makes that true: since Phase 51 A10 a bare
    // 'compile ps_3_0' is below MonoGame's DirectX_11 floor and is refused (SD0015,
    // matching mgfxc), while OpenGL caps at SM3 and needs the legacy profile.
    private const string ValidFx = """
        #if SM4
            #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
            #define PS_SHADERMODEL ps_3_0
        #endif

        float4 MainPS() : COLOR0
        {
            return float4(1, 0, 0, 1);
        }

        technique T
        {
            pass P0
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        }
        """;

    [Fact]
    public async Task ValidateAsync_CallableOnAnEffectCompilerTypedVariable_NotOnlyIShaderCompiler()
    {
        // Deliberately NOT `IShaderCompiler compiler = ...` — this is the whole point of
        // the test. `var` here infers EffectCompiler.
        var compiler = new EffectCompiler();

        ShaderValidationReport report = await compiler.ValidateAsync(ValidFx);

        report.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_CallableOnAnEffectCompilerTypedVariable_NotOnlyIShaderCompiler()
    {
        var compiler = new EffectCompiler();

        ShaderValidationReport report = compiler.Validate(ValidFx);

        report.IsValid.ShouldBeTrue();
    }
}
