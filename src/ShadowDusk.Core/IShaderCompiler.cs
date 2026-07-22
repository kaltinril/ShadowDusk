#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The core consumer contract: compiles HLSL <c>.fx</c> source into a compiled effect
/// entirely in memory, with no <c>fxc.exe</c>, <c>mgfxc</c>, Wine, or Windows SDK required.
/// For the MonoGame/KNI targets the output is a <c>.mgfx</c> effect; for
/// <see cref="PlatformTarget.Fna"/> it is the D3D9 fx_2_0 effects binary (<c>.fxb</c>) FNA
/// loads. This is the product's public entry point — add the library to a project and call
/// <see cref="CompileAsync"/> at runtime or build time.
/// </summary>
/// <remarks>
/// The same contract abstracts every delivery shape: the in-process desktop library
/// (<c>EffectCompiler</c>), the CLI, and the in-browser WASM compiler all implement it and
/// produce the same bytes for a given source and target. Output is behaviorally equivalent
/// to the reference compiler's (<c>mgfxc</c> for MonoGame/KNI targets; <c>fxc /T fx_2_0</c>
/// for FNA) — it loads into the real runtime's <c>Effect</c> and renders the same image —
/// but is not byte-identical to it (different compilers). Determinism is ShadowDusk's own
/// reproducibility only: the same ShadowDusk version, source, and target yield the same bytes.
/// </remarks>
public interface IShaderCompiler
{
    /// <summary>
    /// Compiles the given HLSL source into a compiled effect for the target in
    /// <paramref name="options"/>, returning the effect bytes in memory.
    /// </summary>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to compile.</param>
    /// <param name="options">
    /// Compilation settings: the <see cref="PlatformTarget"/>, include resolution,
    /// debug mode, MGFX version, and DirectX DXBC backend selection.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel a long-running compile.</param>
    /// <returns>
    /// A <see cref="Result{T, TError}"/> that is either a successful
    /// <see cref="CompiledShader"/> (the target plus its effect bytes) or, on failure,
    /// an array of <see cref="ShaderError"/> with source file, line, column, code, and message.
    /// Compilation failures are returned as errors, not thrown as exceptions.
    /// </returns>
    Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One-time warm-up that makes the synchronous <see cref="Compile"/> usable from a
    /// synchronous call site. Idempotent and safe to await repeatedly. On the browser/WASM
    /// host this loads and instantiates every WASM compiler module a subsequent
    /// <see cref="Compile"/> of any supported target needs (DXC, SPIRV-Cross, and
    /// vkd3d-shader); on desktop it is effectively a no-op (the native compilers load
    /// lazily on first use).
    /// </summary>
    /// <remarks>
    /// Await this once from a legal async context — e.g. the Blazor bootstrap or an
    /// <c>async Main</c> — before calling <see cref="Compile"/> from synchronous code such
    /// as MonoGame/KNI's <c>Content.Load&lt;Effect&gt;</c>. Never block on it
    /// (<c>.Result</c> / <c>.Wait()</c>): on single-threaded browser WASM that is the
    /// exact sync-over-async deadlock this API exists to avoid. A failure to load a
    /// required module (e.g. the module asset cannot be fetched) is thrown from the
    /// returned task — loudly, with the underlying loader diagnostics.
    /// </remarks>
    /// <param name="cancellationToken">Token used to cancel the warm-up.</param>
    /// <returns>A task that completes when the compiler is ready for synchronous compiles.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous counterpart of <see cref="CompileAsync"/>: compiles the given HLSL
    /// source into a compiled effect for the target in <paramref name="options"/>, on the
    /// calling thread, returning the effect bytes in memory. Intended for synchronous call
    /// sites that cannot await — e.g. compiling inside MonoGame/KNI's
    /// <c>Content.Load&lt;Effect&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Runs the exact same pipeline as <see cref="CompileAsync"/> (one shared
    /// implementation, never a fork), so for the same source, options, and compiler
    /// version the output bytes are identical. The whole compile runs on the calling
    /// thread and never blocks on a task internally — safe on single-threaded browser
    /// WASM. <b>Precondition on the browser/WASM host:</b> <see cref="InitializeAsync"/>
    /// must have completed first (the WASM compiler modules load asynchronously);
    /// otherwise this returns a clear <see cref="ShaderError"/> (code <c>SD1903</c>)
    /// telling the caller to await <see cref="InitializeAsync"/>. On desktop no prior
    /// initialization is required.
    /// </remarks>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to compile.</param>
    /// <param name="options">
    /// Compilation settings: the <see cref="PlatformTarget"/>, include resolution,
    /// debug mode, MGFX version, and DirectX DXBC backend selection.
    /// </param>
    /// <param name="cancellationToken">Token observed between pipeline stages.</param>
    /// <returns>
    /// A <see cref="Result{T, TError}"/> that is either a successful
    /// <see cref="CompiledShader"/> (the target plus its effect bytes) or, on failure,
    /// an array of <see cref="ShaderError"/> with source file, line, column, code, and message.
    /// Compilation failures are returned as errors, not thrown as exceptions.
    /// </returns>
    Result<CompiledShader, ShaderError[]> Compile(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The targets <see cref="ValidateAsync(string, CompilerOptions?, CancellationToken)"/>
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
    /// Runs the exact same pipeline as <see cref="CompileAsync"/> per target (never a
    /// fork), so what validates is precisely what compiles. When
    /// <paramref name="options"/> carries a <see cref="CompilerOptions.Profile"/>,
    /// that profile pins the backend, so only the profile's target is validated.
    /// FNA and Vulkan are not in the default set (FNA's SM2–3 dialect would
    /// false-alarm MonoGame/KNI shaders); pass explicit targets via
    /// <see cref="ValidateAsync(string, IReadOnlyList{PlatformTarget}, CompilerOptions?, CancellationToken)"/>
    /// to include them.
    /// </remarks>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to validate.</param>
    /// <param name="options">
    /// Optional settings (include resolution, source file name, …). The
    /// <see cref="CompilerOptions.Target"/> value is ignored — each validation target
    /// is applied in turn. Omit for defaults.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the validation.</param>
    /// <returns>The per-target report; see <see cref="ShaderValidationReport"/>.</returns>
    async Task<ShaderValidationReport> ValidateAsync(
        string hlslSource,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
        => await ValidateAsync(hlslSource, ResolveValidationTargets(options), options, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// <see cref="ValidateAsync(string, CompilerOptions?, CancellationToken)"/> over an
    /// explicit set of targets (e.g. add <see cref="PlatformTarget.Fna"/> or
    /// <see cref="PlatformTarget.Vulkan"/>, or check a single target).
    /// </summary>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to validate.</param>
    /// <param name="targets">The platform backends to validate, in order.</param>
    /// <param name="options">Optional settings; <see cref="CompilerOptions.Target"/> is ignored.</param>
    /// <param name="cancellationToken">Token used to cancel the validation.</param>
    async Task<ShaderValidationReport> ValidateAsync(
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
            var compile = await CompileAsync(
                    hlslSource, baseOptions.WithGraphicsTarget(target), cancellationToken)
                .ConfigureAwait(false);
            results.Add(ToTargetValidation(target, compile));
        }

        return new ShaderValidationReport(results);
    }

    /// <summary>
    /// Synchronous counterpart of
    /// <see cref="ValidateAsync(string, CompilerOptions?, CancellationToken)"/>, for
    /// call sites that cannot await. Same pipeline, same report. On the browser/WASM
    /// host the same precondition as <see cref="Compile"/> applies:
    /// <see cref="InitializeAsync"/> must have completed first, otherwise each
    /// target's result carries the clear <c>SD1903</c> error.
    /// </summary>
    /// <param name="hlslSource">The HLSL <c>.fx</c> effect source to validate.</param>
    /// <param name="options">Optional settings; <see cref="CompilerOptions.Target"/> is ignored.</param>
    /// <param name="cancellationToken">Token observed between pipeline stages.</param>
    ShaderValidationReport Validate(
        string hlslSource,
        CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CompilerOptions baseOptions = options ?? new CompilerOptions();
        IReadOnlyList<PlatformTarget> targets = ResolveValidationTargets(options);

        var results = new List<ShaderTargetValidation>(targets.Count);
        foreach (PlatformTarget target in targets)
        {
            var compile = Compile(hlslSource, baseOptions.WithGraphicsTarget(target), cancellationToken);
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
        => compile.IsFailure
            ? new ShaderTargetValidation(target, Succeeded: false, compile.Error, [])
            : new ShaderTargetValidation(target, Succeeded: true, [], compile.Value.Warnings);
}
