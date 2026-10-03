#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Sksl;

/// <summary>Options for <see cref="SkslConverter.Convert(string, SkslConvertOptions, CancellationToken)"/>.</summary>
public sealed class SkslConvertOptions
{
    /// <summary>The logical source name used in diagnostics. Defaults to <c>"&lt;memory&gt;.fx"</c>.</summary>
    public string SourceName { get; init; } = "<memory>.fx";

    /// <summary>Optional custom <c>#include</c> resolver (defaults to the file system).</summary>
    public IIncludeResolver? IncludeResolver { get; init; }

    /// <summary>Additional directories searched when resolving <c>#include</c> directives.</summary>
    public IReadOnlyList<string> AdditionalIncludePaths { get; init; } = [];

    /// <summary>
    /// Interpolant semantics (e.g. <c>"TEXCOORD1"</c>) the caller explicitly accepts becoming
    /// <b>uniforms</b> named <c>in_var_&lt;SEMANTIC&gt;</c> — per-draw constants instead of
    /// interpolated values. Off by default: the converter's answer to an unsupplyable
    /// interpolant is a loud <c>SD0611</c>, because silently changing interpolation semantics is
    /// exactly the wrong-output class this converter exists to prevent. Opting a semantic in is a
    /// documented, warned-about semantic change.
    ///
    /// <para><c>COLOR0</c> (SpriteBatch's vertex color) is <b>not</b> governed by this option: it
    /// always converts, by default, to the synthesized <c>float4</c> uniform
    /// <c>ShadowDusk_Color</c> (set it to the sprite's tint, white when untinted). Listing
    /// <c>"COLOR0"</c> here is accepted and changes nothing, so callers written before that
    /// default keep working, but they now get <c>ShadowDusk_Color</c> instead of
    /// <c>in_var_COLOR0</c>.</para>
    /// </summary>
    public IReadOnlyList<string> TreatVaryingsAsUniforms { get; init; } = [];
}

/// <summary>The successful product: SkSL text plus its runtime contract.</summary>
/// <param name="SkslText">The runtime-effect source for <c>SKRuntimeEffect.CreateShader</c>.</param>
/// <param name="Warnings">Non-fatal findings — every synthesized uniform carries one.</param>
/// <param name="ChildShaders">The <c>uniform shader</c> children to bind, in order, named after the HLSL textures.</param>
/// <param name="SynthesizedUniforms">
/// Uniforms the consumer must set each draw (see <see cref="MappedSksl.SynthesizedUniforms"/>):
/// <c>ShadowDusk_Color</c> (the sprite's tint, for a shader that reads <c>COLOR0</c>) and
/// <c>ShadowDusk_Resolution</c> (output size in pixels, for a shader that uses its UV
/// arithmetically).
/// </param>
public sealed record SkslConversion(
    string SkslText,
    IReadOnlyList<ShaderError> Warnings,
    IReadOnlyList<string> ChildShaders,
    IReadOnlyList<string> SynthesizedUniforms);

/// <summary>
/// Converts an HLSL <c>.fx</c> pixel shader to an <b>SkSL runtime effect</b> for SkiaSharp's
/// <c>SKRuntimeEffect</c> (issue #197). The route is the §2.3 seam:
/// <c>HLSL → [DXC] → SPIR-V → [SPIRV-Cross] → modern GLSL → [convention mapper] → SkSL</c> —
/// the same faithful front half as every ShadowDusk compile (no substitute compiler), branching
/// <i>before</i> the MonoGame GLSL rewriter and the MGFX writer.
///
/// <para><b>What this is, honestly:</b> a source-to-source converter judged by rendered-image
/// fidelity (owner decision 2026-08-13, resolving Phase 57 §3 for this case) — <b>never</b>
/// <c>mgfxc</c>-equivalence, because Skia has no reference compiler to be equivalent to, and
/// never a validation-matrix §1 backend. SkSL is source-only (<c>SKRuntimeEffect</c> has no
/// bytecode entry point) and runtime effects have <b>no vertex stage and no varyings</b>, which
/// bounds the convertible set to fragment-only, coordinate-driven effects with uniform inputs.
/// Everything outside that set is rejected loudly (<c>SD0610</c>–<c>SD0613</c>), never
/// silently narrowed.</para>
/// </summary>
public static class SkslConverter
{
    /// <summary>
    /// Converts the pixel shader of <paramref name="fxSource"/>'s single technique/pass to
    /// SkSL, or returns the loud diagnostics for anything the SkSL model cannot hold.
    /// </summary>
    /// <param name="fxSource">The HLSL <c>.fx</c> effect source.</param>
    /// <param name="options">Conversion options; see <see cref="SkslConvertOptions"/>.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<SkslConversion, ShaderError[]> Convert(
        string fxSource,
        SkslConvertOptions options,
        CancellationToken cancellationToken = default) =>
        Convert(fxSource, options, dxcCompilerFactory: null, glslTranspilerFactory: null, cancellationToken);

    /// <summary>
    /// <see cref="Convert(string, SkslConvertOptions, CancellationToken)"/> with the same
    /// backend-injection seam <see cref="EffectCompiler"/> has, so a host without the native
    /// DXC/SPIRV-Cross (the browser/WASM host) can supply its own: the SAME faithful
    /// components compiled for that host, never a substitute compiler.
    /// </summary>
    /// <param name="fxSource">The HLSL <c>.fx</c> effect source.</param>
    /// <param name="options">Conversion options; see <see cref="SkslConvertOptions"/>.</param>
    /// <param name="dxcCompilerFactory">HLSL to SPIR-V frontend; <see langword="null"/> = bundled desktop DXC.</param>
    /// <param name="glslTranspilerFactory">SPIR-V to GLSL transpiler; <see langword="null"/> = bundled SPIRV-Cross.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<SkslConversion, ShaderError[]> Convert(
        string fxSource,
        SkslConvertOptions options,
        Func<IDxcShaderCompiler>? dxcCompilerFactory,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory,
        CancellationToken cancellationToken = default)
    {
        // 1. Parse the FX9 layer.
        var parse = FxPreParser.Parse(fxSource, options.SourceName);
        if (parse.IsFailure)
        {
            return Result<SkslConversion, ShaderError[]>.Fail(
            [
                new ShaderError(
                    File: parse.Error.SourceFile,
                    Line: parse.Error.Line,
                    Column: parse.Error.Column,
                    Code: $"FX{(int)parse.Error.Code:D4}",
                    Message: parse.Error.Message),
            ]);
        }

        Result<SkslConversion, ShaderError[]> first = ConvertCore(
            parse.Value, options, recovered: null, out bool shaderCompileFailed, cancellationToken,
            dxcCompilerFactory, glslTranspilerFactory);
        if (first.IsSuccess || !shaderCompileFailed)
            return first;

        // Legacy-sampler recovery (issues #308, #327): the same one CompilationPipeline.Run uses.
        // The DXC compile failed; if the text it was given still holds legacy D3D9 sampler syntax
        // once preprocessed (a declaration in an #include'd file, or one that comes out of a
        // macro, which the raw pre-parse cannot see), repeat the pre-parse on the PREPROCESSED
        // source and convert from that. Only ever reached on a failure, so an effect that
        // converts from its raw source is converted from exactly that source, as before.
        LegacySamplerRecovery.Outcome outcome = LegacySamplerRecovery.Evaluate(
            fxSource,
            options.SourceName,
            PlatformMacros.For(PlatformTarget.OpenGL),
            options.IncludeResolver ?? new FileSystemIncludeResolver(),
            options.AdditionalIncludePaths);
        return LegacySamplerRecovery.Apply(
            first, outcome,
            retry => ConvertCore(retry.Parsed.Parsed, options, retry, out _, cancellationToken,
                dxcCompilerFactory, glslTranspilerFactory));
    }

    /// <summary>
    /// One pass of the conversion over a pre-parse: the raw one (every conversion's first and
    /// normally only pass), or, with <paramref name="recovered"/>, the pre-parse of the
    /// preprocessed source (issue #308). <paramref name="shaderCompileFailed"/> is set when the
    /// DXC compile (or the transpile behind it) is what failed on a first pass: the one failure
    /// the recovery can be consulted for.
    /// </summary>
    private static Result<SkslConversion, ShaderError[]> ConvertCore(
        FxParseResult parsed,
        SkslConvertOptions options,
        LegacySamplerRecovery.Outcome.Retry? recovered,
        out bool shaderCompileFailed,
        CancellationToken cancellationToken,
        Func<IDxcShaderCompiler>? dxcCompilerFactory,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory)
    {
        shaderCompileFailed = false;

        if (parsed.Techniques.Count == 0)
        {
            return Fail(options.SourceName, "SD0010", "Effect source contains no techniques.");
        }

        // v1 is deliberately single-technique, single-pass: SkSL has no technique/pass concept,
        // so "which pass becomes THE effect" would be a silent guess on anything larger.
        if (parsed.Techniques.Count > 1 || parsed.Techniques[0].Passes.Count > 1)
        {
            return Fail(options.SourceName, "SD0615",
                "the effect has multiple techniques/passes, and an SkSL runtime effect is a single " +
                "fragment function — converting one pass and dropping the rest would be a silent " +
                "guess. Split the effect, or convert a single-pass .fx.");
        }

        var pass = parsed.Techniques[0].Passes[0];

        // The Gum lesson at stage level: a vertex shader cannot ride along (SkSL has no vertex
        // stage, by Skia's design), and quietly discarding it would change what the effect draws.
        if (pass.VertexEntryPoint is not null)
        {
            return Fail(options.SourceName, "SD0610",
                $"pass '{pass.Name}' compiles a vertex shader ('{pass.VertexEntryPoint}'), and SkSL " +
                "runtime effects have no vertex stage at all (a Skia platform limit, not a " +
                "ShadowDusk one). Only pixel-only passes convert; refusing rather than silently " +
                "dropping the vertex work.");
        }

        if (pass.PixelEntryPoint is null)
        {
            return Fail(options.SourceName, "SD0610",
                $"pass '{pass.Name}' has no pixel shader — an SkSL runtime effect IS a pixel " +
                "function, so there is nothing to convert.");
        }

        // 2-4. HLSL -> SPIR-V -> modern GLSL, the shared seam BEFORE the MonoGame rewriter. A
        // recovery's text already has its #includes inlined and its macros expanded (flattening
        // it again would prepend the macro block a second time); a raw pre-parse is flattened here.
        string compilerInput;
        if (recovered is not null)
        {
            compilerInput = parsed.StrippedHlsl;
        }
        else
        {
            var flattened = ModernGlslSeam.Flatten(
                parsed.StrippedHlsl, options.SourceName, options.IncludeResolver, options.AdditionalIncludePaths);
            if (flattened.IsFailure)
                return Result<SkslConversion, ShaderError[]>.Fail([flattened.Error]);
            compilerInput = flattened.Value.Text;
        }

        var seam = ModernGlslSeam.CompilePixel(compilerInput, pass.PixelEntryPoint, options.SourceName, cancellationToken,
            dxcCompilerFactory, glslTranspilerFactory);
        if (seam.IsFailure)
        {
            shaderCompileFailed = recovered is null;
            return Result<SkslConversion, ShaderError[]>.Fail(seam.Error);
        }

        // The HLSL texture name behind each combined sampler, in declaration order — the same
        // extraction the GL sampler table trusts (issue #189's allocator).
        var pairs = seam.Value.SamplerPairs;
        IReadOnlyList<string> textureNames = pairs.IsSuccess
            ? pairs.Value.Select(p => p.TextureName).ToList()
            : [];

        // 5. GLSL -> SkSL.
        var mapped = SkslGlslMapper.Map(
            seam.Value.Glsl,
            textureNames,
            options.TreatVaryingsAsUniforms.ToHashSet(StringComparer.Ordinal),
            options.SourceName);
        if (mapped.IsFailure)
            return Result<SkslConversion, ShaderError[]>.Fail(mapped.Error);

        return Result<SkslConversion, ShaderError[]>.Ok(new SkslConversion(
            mapped.Value.SkslText,
            mapped.Value.Warnings,
            mapped.Value.ChildShaders,
            mapped.Value.SynthesizedUniforms));
    }

    private static Result<SkslConversion, ShaderError[]> Fail(string file, string code, string message) =>
        Result<SkslConversion, ShaderError[]>.Fail(
            [new ShaderError(File: file, Line: 0, Column: 0, Code: code, Message: message)]);
}
