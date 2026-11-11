#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Compiler;

/// <summary>
/// Compiles a vertex shader and a pixel shader, given as two separate HLSL texts, to the same
/// outputs <see cref="IShaderCompiler.CompileAsync"/> produces for a single <c>.fx</c>: MGFX and
/// KNIFX (OpenGL, DirectX 11, DirectX 12, Vulkan) and FNA <c>fx_2_0</c>. The two texts are merged by
/// <see cref="HlslStageFrontend"/> and compiled by the unchanged pipeline, so every
/// <see cref="CompilerOptions"/> setting (<c>Target</c>, <c>Container</c>, <c>MgfxVersion</c>,
/// <c>Profile</c>, <c>Defines</c>, <c>IncludeResolver</c>, ...) works as it does for a <c>.fx</c>.
/// </summary>
/// <example>
/// <code>
/// var result = HlslStageCompiler.Compile(
///     new EffectCompiler(),
///     new HlslStageSources(vertexHlsl, pixelHlsl, VertexEntry: "VSMain", PixelEntry: "PSMain"),
///     new CompilerOptions { Target = PlatformTarget.OpenGL, Container = EffectContainer.Knifx });
/// </code>
/// </example>
public static class HlslStageCompiler
{
    /// <summary>The targets <see cref="ValidateAsync"/> checks when none are given: OpenGL and DirectX.</summary>
    private static readonly PlatformTarget[] DefaultValidationTargets = [PlatformTarget.OpenGL, PlatformTarget.DirectX];

    /// <summary>
    /// Merges <paramref name="sources"/> into one effect and compiles it with <paramref name="compiler"/>
    /// to the target in <paramref name="options"/>. A merge failure returns its <c>SD0660</c>-<c>SD0667</c>
    /// errors and compiles nothing; compile errors point at <see cref="HlslStageSources.VertexName"/> or
    /// <see cref="HlslStageSources.PixelName"/> and the line in that source.
    /// </summary>
    /// <param name="compiler">The compiler to run, usually an <see cref="EffectCompiler"/>.</param>
    /// <param name="sources">The two sources and their entry points.</param>
    /// <param name="options">Compilation settings, as for <see cref="IShaderCompiler.Compile"/>.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<CompiledShader, ShaderError[]> Compile(
        IShaderCompiler compiler,
        HlslStageSources sources,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        Result<HlslStageFxConversion, ShaderError[]> fx = HlslStageFrontend.ConvertToFx(sources);
        if (fx.IsFailure)
            return Result<CompiledShader, ShaderError[]>.Fail(fx.Error);

        return WithWarnings(compiler.Compile(fx.Value.FxText, options, cancellationToken), fx.Value.Warnings);
    }

    /// <summary>Asynchronous counterpart of <see cref="Compile"/>.</summary>
    /// <param name="compiler">The compiler to run, usually an <see cref="EffectCompiler"/>.</param>
    /// <param name="sources">The two sources and their entry points.</param>
    /// <param name="options">Compilation settings, as for <see cref="IShaderCompiler.CompileAsync"/>.</param>
    /// <param name="cancellationToken">Token used to cancel a long-running compile.</param>
    public static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        IShaderCompiler compiler,
        HlslStageSources sources,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        Result<HlslStageFxConversion, ShaderError[]> fx = HlslStageFrontend.ConvertToFx(sources);
        if (fx.IsFailure)
            return Result<CompiledShader, ShaderError[]>.Fail(fx.Error);

        Result<CompiledShader, ShaderError[]> compiled =
            await compiler.CompileAsync(fx.Value.FxText, options, cancellationToken).ConfigureAwait(false);
        return WithWarnings(compiled, fx.Value.Warnings);
    }

    /// <summary>
    /// Tries to show every issue with the pair in one call, as
    /// <see cref="ShaderCompilerValidationExtensions.ValidateAsync(IShaderCompiler, string, CompilerOptions?, CancellationToken)"/>
    /// does for a <c>.fx</c>: merges the sources, then compiles for each target (OpenGL and DirectX by
    /// default; only the profile's target when <paramref name="options"/> carries a
    /// <see cref="CompilerOptions.Profile"/>). A merge failure is reported for every target.
    /// </summary>
    /// <param name="compiler">The compiler to validate with.</param>
    /// <param name="sources">The two sources and their entry points.</param>
    /// <param name="targets">The targets to validate, or null for the default set.</param>
    /// <param name="options">Optional settings; <see cref="CompilerOptions.Target"/> is ignored.</param>
    /// <param name="cancellationToken">Token used to cancel the validation.</param>
    public static async Task<ShaderValidationReport> ValidateAsync(
        IShaderCompiler compiler,
        HlslStageSources sources,
        IReadOnlyList<PlatformTarget>? targets = null,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        IReadOnlyList<PlatformTarget> resolved = targets
            ?? (options?.Profile is { } profile ? [profile.GraphicsTarget] : DefaultValidationTargets);

        Result<HlslStageFxConversion, ShaderError[]> fx = HlslStageFrontend.ConvertToFx(sources);
        if (fx.IsFailure)
        {
            return new ShaderValidationReport(resolved
                .Select(t => new ShaderTargetValidation(t, Succeeded: false, fx.Error, []))
                .ToList());
        }

        return await compiler.ValidateAsync(fx.Value.FxText, resolved, options, cancellationToken).ConfigureAwait(false);
    }

    // The frontend's own non-fatal findings ride along with the compiler's, never dropped.
    private static Result<CompiledShader, ShaderError[]> WithWarnings(
        Result<CompiledShader, ShaderError[]> compiled, IReadOnlyList<ShaderError> frontendWarnings) =>
        compiled.IsSuccess && frontendWarnings.Count > 0
            ? Result<CompiledShader, ShaderError[]>.Ok(compiled.Value with
            {
                Warnings = [.. frontendWarnings, .. compiled.Value.Warnings],
            })
            : compiled;
}
