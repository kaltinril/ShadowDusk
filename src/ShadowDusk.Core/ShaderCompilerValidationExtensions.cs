#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// <c>Validate</c> / <c>ValidateAsync</c> for any <see cref="IShaderCompiler"/> — extension
/// methods rather than interface members so they are reachable from a variable declared as
/// the CONCRETE compiler type (e.g. <c>var compiler = new EffectCompiler();</c>, the pattern
/// the README/quickstarts use for <see cref="IShaderCompiler.CompileAsync"/>), not only from
/// one declared as <see cref="IShaderCompiler"/>. A C# default interface method would compile
/// only through the interface-typed reference — a footgun for exactly this "just add the
/// package and call the API" promise. Every current and future <see cref="IShaderCompiler"/>
/// implementation gets these for free, with nothing to override.
/// </summary>
public static class ShaderCompilerValidationExtensions
{
    /// <summary>
    /// The targets <see cref="ValidateAsync(IShaderCompiler, string, CompilerOptions?, CancellationToken)"/>
    /// checks when none are specified: OpenGL and DirectX — the two mainstream
    /// MonoGame/KNI backends, and exactly the pair behind the classic field report
    /// "it compiles for DirectX but fails for OpenGL".
    /// </summary>
    private static readonly PlatformTarget[] DefaultValidationTargets =
        [PlatformTarget.OpenGL, PlatformTarget.DirectX];

    /// <summary>
    /// Tries to show ALL issues with a shader in one call: compiles it for each
    /// validation target (by default OpenGL and DirectX) and reports every error and
    /// every warning per target. Print the returned report — its
    /// <see cref="ShaderValidationReport.ToString"/> is the full human-readable
    /// story — or walk <see cref="ShaderValidationReport.Targets"/> for structured
    /// access. Validation never throws for shader problems; a broken shader simply
    /// produces a report with errors.
    /// </summary>
    /// <remarks>
    /// Runs the exact same pipeline as <see cref="IShaderCompiler.CompileAsync"/> per
    /// target (never a fork), so what validates is precisely what compiles. When
    /// <paramref name="options"/> carries a <see cref="CompilerOptions.Profile"/>,
    /// that profile pins the backend, so only the profile's target is validated.
    /// FNA and Vulkan are not in the default set (FNA's SM2–3 dialect would
    /// false-alarm MonoGame/KNI shaders); pass explicit targets via
    /// <see cref="ValidateAsync(IShaderCompiler, string, IReadOnlyList{PlatformTarget}, CompilerOptions?, CancellationToken)"/>
    /// to include them.
    /// </remarks>
    /// <param name="compiler">The compiler to validate with.</param>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to validate.</param>
    /// <param name="options">
    /// Optional settings (include resolution, source file name, …). The
    /// <see cref="CompilerOptions.Target"/> value is ignored — each validation target
    /// is applied in turn. Omit for defaults.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the validation.</param>
    /// <returns>The per-target report; see <see cref="ShaderValidationReport"/>.</returns>
    public static async Task<ShaderValidationReport> ValidateAsync(
        this IShaderCompiler compiler,
        string hlslSource,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
        => await compiler.ValidateAsync(hlslSource, ResolveValidationTargets(options), options, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// <see cref="ValidateAsync(IShaderCompiler, string, CompilerOptions?, CancellationToken)"/>
    /// over an explicit set of targets (e.g. add <see cref="PlatformTarget.Fna"/> or
    /// <see cref="PlatformTarget.Vulkan"/>, or check a single target).
    /// </summary>
    /// <param name="compiler">The compiler to validate with.</param>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to validate.</param>
    /// <param name="targets">The platform backends to validate, in order.</param>
    /// <param name="options">Optional settings; <see cref="CompilerOptions.Target"/> is ignored.</param>
    /// <param name="cancellationToken">Token used to cancel the validation.</param>
    public static async Task<ShaderValidationReport> ValidateAsync(
        this IShaderCompiler compiler,
        string hlslSource,
        IReadOnlyList<PlatformTarget> targets,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        CompilerOptions baseOptions = options ?? new CompilerOptions();

        var results = new List<ShaderTargetValidation>(targets.Count);
        foreach (PlatformTarget target in targets)
        {
            var compile = await compiler.CompileAsync(
                    hlslSource, baseOptions.WithGraphicsTarget(target), cancellationToken)
                .ConfigureAwait(false);
            results.Add(ToTargetValidation(target, compile));
        }

        return new ShaderValidationReport(results);
    }

    /// <summary>
    /// Synchronous counterpart of
    /// <see cref="ValidateAsync(IShaderCompiler, string, CompilerOptions?, CancellationToken)"/>,
    /// for call sites that cannot await. Same pipeline, same report. On the browser/WASM
    /// host the same precondition as <see cref="IShaderCompiler.Compile"/> applies:
    /// <see cref="IShaderCompiler.InitializeAsync"/> must have completed first, otherwise
    /// each target's result carries the clear <c>SD1903</c> error.
    /// </summary>
    /// <param name="compiler">The compiler to validate with.</param>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to validate.</param>
    /// <param name="options">Optional settings; <see cref="CompilerOptions.Target"/> is ignored.</param>
    /// <param name="cancellationToken">Token observed between pipeline stages.</param>
    public static ShaderValidationReport Validate(
        this IShaderCompiler compiler,
        string hlslSource,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CompilerOptions baseOptions = options ?? new CompilerOptions();
        IReadOnlyList<PlatformTarget> targets = ResolveValidationTargets(options);

        var results = new List<ShaderTargetValidation>(targets.Count);
        foreach (PlatformTarget target in targets)
        {
            var compile = compiler.Compile(hlslSource, baseOptions.WithGraphicsTarget(target), cancellationToken);
            results.Add(ToTargetValidation(target, compile));
        }

        return new ShaderValidationReport(results);
    }

    private static IReadOnlyList<PlatformTarget> ResolveValidationTargets(CompilerOptions? options)
        // A CapabilityProfile fully specifies its backend (the pipeline would override
        // any other target anyway) — validating other backends against a profile-pinned
        // request would just re-check the same output twice.
        => options?.Profile is { } profile
            ? [profile.GraphicsTarget]
            : DefaultValidationTargets;

    private static ShaderTargetValidation ToTargetValidation(
        PlatformTarget target,
        Result<CompiledShader, ShaderError[]> compile)
    {
        if (!compile.IsFailure)
            return new ShaderTargetValidation(target, Succeeded: true, [], compile.Value.Warnings);

        // The failure array can carry warnings from earlier, already-compiled stages
        // riding along with the fatal error (CompilationPipeline.Fail(error, warnings)) —
        // split them out so Errors/Warnings keep their documented meaning instead of a
        // warning inflating the reported error count.
        var errors = new List<ShaderError>();
        var warnings = new List<ShaderError>();
        foreach (ShaderError e in compile.Error)
            (e.Severity == ShaderErrorSeverity.Warning ? warnings : errors).Add(e);

        return new ShaderTargetValidation(target, Succeeded: false, errors, warnings);
    }
}
