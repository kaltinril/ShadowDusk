#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Compiler.Internal;

/// <summary>One pixel shader taken to SPIRV-Cross's modern GLSL, plus its sampler pairs.</summary>
/// <param name="Glsl">SPIRV-Cross's raw GLSL (<c>#version 140</c>), never the MonoGame-rewritten dialect.</param>
/// <param name="SamplerPairs">
/// The (texture, sampler) pairs behind the GLSL's combined samplers, in the order SPIRV-Cross
/// declares them, or the <c>SD0217</c> failure for a shape the extraction does not model. Left
/// as a result so each consumer decides whether an unmodeled shape is fatal for it.
/// </param>
internal sealed record ModernGlslPixelShader(
    string Glsl,
    Result<IReadOnlyList<CombinedSamplerPair>, ShaderError> SamplerPairs);

/// <summary>
/// The front half every source-emitting converter (SkSL, raylib) shares:
/// <c>HLSL → [DXC] → SPIR-V → [SPIRV-Cross] → modern GLSL</c>, stopping at the seam BEFORE the
/// MonoGame GLSL rewriter and the MGFX writer. Same preprocessor macro set, same DXC request, and
/// same SPIRV-Cross options as the OpenGL target, so the converters inherit the GL backend's
/// faithful front half rather than re-deriving it.
///
/// <para>Two steps, because the legacy-sampler recovery (issue #308, <see cref="LegacySamplerRecovery"/>)
/// arrives with its compiler input already preprocessed: <see cref="Flatten"/> inlines the
/// <c>#include</c>s of a raw pre-parse's HLSL with the OpenGL macro set, and
/// <see cref="CompilePixel"/> compiles whichever text it is given, as is.</para>
/// </summary>
internal static class ModernGlslSeam
{
    /// <summary>
    /// Flattens the pre-parser's HLSL with the OpenGL macro set: the arm the corpus'
    /// <c>#if OPENGL</c> headers select, and the one whose SM3-level profiles that arm declares.
    /// </summary>
    public static Result<PreprocessedSource, ShaderError> Flatten(
        string strippedHlsl,
        string sourceName,
        IIncludeResolver? includeResolver,
        IReadOnlyList<string> additionalIncludePaths) =>
        new Preprocessor().Flatten(
            strippedHlsl,
            sourceName,
            PlatformMacros.For(PlatformTarget.OpenGL),
            includeResolver ?? new FileSystemIncludeResolver(),
            additionalIncludePaths);

    /// <summary>
    /// Compiles and transpiles one pixel entry point of <paramref name="compilerInput"/>: the
    /// flattened text of <see cref="Flatten"/>, or the recovery's preprocessed pre-parse
    /// (<c>FxPreprocessedParse.Parsed.StrippedHlsl</c>), whose <c>#include</c>s are already
    /// inlined and whose macros are already expanded. Flattening that again would prepend the
    /// macro block a second time, so this step never flattens.
    /// </summary>
    public static Result<ModernGlslPixelShader, ShaderError[]> CompilePixel(
        string compilerInput,
        string pixelEntryPoint,
        string sourceName,
        CancellationToken cancellationToken)
    {
        using var dxc = new DxcShaderCompiler();
        var spirv = dxc.Compile(new DxcCompileRequest
        {
            HlslSource     = compilerInput,
            SourceFileName = sourceName,
            EntryPoint     = pixelEntryPoint,
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.OpenGL,
        }, cancellationToken);
        if (spirv.IsFailure)
            return Result<ModernGlslPixelShader, ShaderError[]>.Fail([spirv.Error]);

        var pairs = SpirvCombinedSamplerPairs.Extract(spirv.Value.Bytes);

        var glsl = new SpirvCrossGlslTranspiler().Transpile(spirv.Value.Bytes, cancellationToken);
        if (glsl.IsFailure)
            return Result<ModernGlslPixelShader, ShaderError[]>.Fail([glsl.Error]);

        return Result<ModernGlslPixelShader, ShaderError[]>.Ok(
            new ModernGlslPixelShader(glsl.Value.Text, pairs));
    }
}
