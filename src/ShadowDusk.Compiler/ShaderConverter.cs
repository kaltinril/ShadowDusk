#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler;

/// <summary>
/// One auto-detecting entry point per output kind, for callers that receive shaders without a
/// trustworthy file extension. Each method detects the source language from the text
/// (<see cref="ShaderFormatDetector"/>), routes an <c>.fx</c> effect or HLSL-compatible Slang to
/// the same pipeline the format-specific entry points use, and returns an
/// <see cref="ShaderErrorKind.UnsupportedInput"/> error (code <c>SD0650</c>) without compiling
/// anything when the input is not one of those two.
/// </summary>
/// <remarks>
/// <para><b>Telling "not mine" from "broken":</b> a failed result whose errors contain a
/// <see cref="ShaderErrorKind.UnsupportedInput"/> error means nothing was attempted, so a caller
/// that chains loaders can try the next one. Any other failure (a compile error, an
/// <see cref="ShaderErrorKind.AmbiguousInput"/> error, an include error) is a real error in a
/// recognized shader and should be reported.</para>
/// <para><b>GLSL input</b> (<see cref="ShaderFormat.Glsl"/>) is reported as unsupported here:
/// its converter is the separate <c>ShadowDusk.ShaderToy</c> package, which this package does
/// not reference. Call <c>ShaderToyConverter.Convert</c> first, then pass its <c>.fx</c>
/// output to any method on this class.</para>
/// <para>The format-specific entry points (<see cref="EffectCompiler"/>,
/// <see cref="SlangFrontend"/>, <see cref="SkslConverter"/>, <see cref="RaylibConverter"/>)
/// are unchanged and remain the way to force a format.</para>
/// </remarks>
public static class ShaderConverter
{
    /// <summary>The diagnostic code of an <see cref="ShaderErrorKind.UnsupportedInput"/> error.</summary>
    public const string UnsupportedInputCode = "SD0650";

    /// <summary>
    /// Whether the methods on this class would try to convert <paramref name="source"/>. Cheap:
    /// a text scan, no compile. False means the input is not a format this package reads, and a
    /// caller can hand it to something else. True means the conversion will be attempted and
    /// any failure is a real error; that includes ambiguous input, which fails loudly.
    /// </summary>
    /// <param name="source">The shader source text.</param>
    /// <param name="nameHint">Optional file name; see <see cref="ShaderFormatDetector.Detect"/>.</param>
    public static bool CanConvert(string source, string? nameHint = null)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(source, nameHint);
        return detection.IsAmbiguous || IsRoutable(detection.Format);
    }

    /// <summary>
    /// Detects the format of <paramref name="source"/> and compiles it with
    /// <paramref name="compiler"/> to the target in <paramref name="options"/> (MonoGame, KNI,
    /// FNA). Slang is converted to <c>.fx</c> first, exactly as
    /// <see cref="SlangFrontend.ConvertToFx"/> does.
    /// </summary>
    /// <param name="compiler">The compiler to run, usually an <see cref="EffectCompiler"/>.</param>
    /// <param name="source">The shader source text.</param>
    /// <param name="options">Compilation settings, as for <see cref="IShaderCompiler.Compile"/>.</param>
    /// <param name="nameHint">Optional file name; used for detection ties and in diagnostics.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<CompiledShader, ShaderError[]> Compile(
        IShaderCompiler compiler,
        string source,
        CompilerOptions options,
        string? nameHint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        Result<FxInput, ShaderError[]> fx = ToFx(source, nameHint);
        if (fx.IsFailure)
            return Result<CompiledShader, ShaderError[]>.Fail(fx.Error);

        return WithWarnings(compiler.Compile(fx.Value.FxText, options, cancellationToken), fx.Value.Warnings);
    }

    /// <summary>Asynchronous counterpart of <see cref="Compile"/>; same detection and routing.</summary>
    /// <param name="compiler">The compiler to run, usually an <see cref="EffectCompiler"/>.</param>
    /// <param name="source">The shader source text.</param>
    /// <param name="options">Compilation settings, as for <see cref="IShaderCompiler.CompileAsync"/>.</param>
    /// <param name="nameHint">Optional file name; used for detection ties and in diagnostics.</param>
    /// <param name="cancellationToken">Token used to cancel a long-running compile.</param>
    public static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        IShaderCompiler compiler,
        string source,
        CompilerOptions options,
        string? nameHint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        Result<FxInput, ShaderError[]> fx = ToFx(source, nameHint);
        if (fx.IsFailure)
            return Result<CompiledShader, ShaderError[]>.Fail(fx.Error);

        Result<CompiledShader, ShaderError[]> compiled =
            await compiler.CompileAsync(fx.Value.FxText, options, cancellationToken).ConfigureAwait(false);
        return WithWarnings(compiled, fx.Value.Warnings);
    }

    /// <summary>
    /// Detects the format of <paramref name="source"/> and converts it to an SkSL fragment
    /// shader: <see cref="SkslConverter.Convert(string, SkslConvertOptions, CancellationToken)"/>
    /// for an <c>.fx</c> effect, <see cref="SkslConverter.ConvertSlang"/> for Slang.
    /// </summary>
    /// <param name="source">The shader source text.</param>
    /// <param name="options">Conversion options; see <see cref="SkslConvertOptions"/>.</param>
    /// <param name="nameHint">Optional file name; used only for detection ties.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<SkslConversion, ShaderError[]> ConvertToSksl(
        string source,
        SkslConvertOptions options,
        string? nameHint = null,
        CancellationToken cancellationToken = default)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(source, nameHint);
        return detection.Format switch
        {
            ShaderFormat.FxEffect => SkslConverter.Convert(source, options, cancellationToken),
            ShaderFormat.Slang => SkslConverter.ConvertSlang(source, options, cancellationToken),
            _ => Result<SkslConversion, ShaderError[]>.Fail([DetectionError(detection, nameHint)]),
        };
    }

    /// <summary>
    /// Detects the format of <paramref name="source"/> and converts it to a raylib fragment
    /// shader: <see cref="RaylibConverter.Convert"/> for an <c>.fx</c> effect,
    /// <see cref="RaylibConverter.ConvertSlang"/> for Slang. A raylib-style GLSL <c>.fs</c> file
    /// is GLSL input, so it is reported as unsupported and can be loaded by raylib directly.
    /// </summary>
    /// <param name="source">The shader source text.</param>
    /// <param name="options">Conversion options; see <see cref="RaylibConvertOptions"/>.</param>
    /// <param name="nameHint">Optional file name; used only for detection ties.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<RaylibShader, ShaderError[]> ConvertToRaylib(
        string source,
        RaylibConvertOptions options,
        string? nameHint = null,
        CancellationToken cancellationToken = default)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(source, nameHint);
        return detection.Format switch
        {
            ShaderFormat.FxEffect => RaylibConverter.Convert(source, options, cancellationToken),
            ShaderFormat.Slang => RaylibConverter.ConvertSlang(source, options, cancellationToken),
            _ => Result<RaylibShader, ShaderError[]>.Fail([DetectionError(detection, nameHint)]),
        };
    }

    private sealed record FxInput(string FxText, IReadOnlyList<ShaderError> Warnings);

    private static bool IsRoutable(ShaderFormat format) =>
        format is ShaderFormat.FxEffect or ShaderFormat.Slang;

    private static Result<FxInput, ShaderError[]> ToFx(string source, string? nameHint)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(source, nameHint);
        switch (detection.Format)
        {
            case ShaderFormat.FxEffect:
                return Result<FxInput, ShaderError[]>.Ok(new FxInput(source, []));

            case ShaderFormat.Slang:
                Result<SlangFxConversion, ShaderError[]> slang = SlangFrontend.ConvertToFx(
                    source, new SlangConvertOptions { SourceName = nameHint ?? "<memory>.slang" });
                return slang.IsFailure
                    ? Result<FxInput, ShaderError[]>.Fail(slang.Error)
                    : Result<FxInput, ShaderError[]>.Ok(new FxInput(slang.Value.FxText, slang.Value.Warnings));

            default:
                return Result<FxInput, ShaderError[]>.Fail([DetectionError(detection, nameHint)]);
        }
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

    private static ShaderError DetectionError(ShaderFormatDetection detection, string? nameHint)
    {
        string file = nameHint ?? "<memory>";
        if (detection.IsAmbiguous)
        {
            return new ShaderError(
                File: file, Line: 0, Column: 0, Code: "SD0005",
                Message: $"Ambiguous shader input: {detection.Reason} Pass a name hint ending in .fx or .glsl, " +
                         "or call the format-specific entry point.",
                Kind: ShaderErrorKind.AmbiguousInput);
        }

        string why = detection.Format == ShaderFormat.Glsl
            ? "GLSL input is converted by the ShadowDusk.ShaderToy package (ShaderToyConverter.Convert), " +
              "whose .fx output this entry point then accepts."
            : $"It is not an .fx effect or HLSL-compatible Slang: {detection.Reason}";
        return new ShaderError(
            File: file, Line: 0, Column: 0, Code: UnsupportedInputCode,
            Message: $"Unsupported shader input. {why}",
            Kind: ShaderErrorKind.UnsupportedInput);
    }
}
