#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Compiler.Sksl;

/// <summary>Options for <see cref="SkslConverter.Convert"/>.</summary>
public sealed class SkslConvertOptions
{
    /// <summary>The logical source name used in diagnostics. Defaults to <c>"&lt;memory&gt;.fx"</c>.</summary>
    public string SourceName { get; init; } = "<memory>.fx";

    /// <summary>Optional custom <c>#include</c> resolver (defaults to the file system).</summary>
    public IIncludeResolver? IncludeResolver { get; init; }

    /// <summary>Additional directories searched when resolving <c>#include</c> directives.</summary>
    public IReadOnlyList<string> AdditionalIncludePaths { get; init; } = [];

    /// <summary>
    /// Interpolant semantics (e.g. <c>"COLOR0"</c>) the caller explicitly accepts becoming
    /// <b>uniforms</b> — per-draw constants instead of interpolated values. Off by default:
    /// the converter's default answer to an unsupplyable interpolant is a loud <c>SD0611</c>,
    /// because silently changing interpolation semantics is exactly the wrong-output class this
    /// converter exists to prevent (Gum's own hand-port dropped its <c>COLOR0</c> tint that
    /// way). Opting a semantic in is a documented, warned-about semantic change.
    /// </summary>
    public IReadOnlyList<string> TreatVaryingsAsUniforms { get; init; } = [];
}

/// <summary>The successful product: SkSL text plus its runtime contract.</summary>
/// <param name="SkslText">The runtime-effect source for <c>SKRuntimeEffect.CreateShader</c>.</param>
/// <param name="Warnings">Non-fatal findings — every synthesized uniform carries one.</param>
/// <param name="ChildShaders">The <c>uniform shader</c> children to bind, in order, named after the HLSL textures.</param>
/// <param name="SynthesizedUniforms">Uniforms the consumer must set each draw (see <see cref="MappedSksl.SynthesizedUniforms"/>).</param>
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

        if (parse.Value.Techniques.Count == 0)
        {
            return Fail(options.SourceName, "SD0010", "Effect source contains no techniques.");
        }

        // v1 is deliberately single-technique, single-pass: SkSL has no technique/pass concept,
        // so "which pass becomes THE effect" would be a silent guess on anything larger.
        if (parse.Value.Techniques.Count > 1 || parse.Value.Techniques[0].Passes.Count > 1)
        {
            return Fail(options.SourceName, "SD0615",
                "the effect has multiple techniques/passes, and an SkSL runtime effect is a single " +
                "fragment function — converting one pass and dropping the rest would be a silent " +
                "guess. Split the effect, or convert a single-pass .fx.");
        }

        var pass = parse.Value.Techniques[0].Passes[0];

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

        // 2. Preprocess with the OpenGL macro set — the arm the GL fixtures' `#if OPENGL`
        //    headers select, and the one whose SM3-level profiles the corpus writes there.
        var preprocess = new Preprocessor().Flatten(
            parse.Value.StrippedHlsl,
            options.SourceName,
            PlatformMacros.For(PlatformTarget.OpenGL),
            options.IncludeResolver ?? new FileSystemIncludeResolver(),
            options.AdditionalIncludePaths);
        if (preprocess.IsFailure)
            return Result<SkslConversion, ShaderError[]>.Fail([preprocess.Error]);

        // 3. HLSL -> SPIR-V, with the same faithful DXC every ShadowDusk compile uses.
        using var dxc = new DxcShaderCompiler();
        var spirv = dxc.Compile(new DxcCompileRequest
        {
            HlslSource     = preprocess.Value.Text,
            SourceFileName = options.SourceName,
            EntryPoint     = pass.PixelEntryPoint,
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.OpenGL,
        }, cancellationToken);
        if (spirv.IsFailure)
            return Result<SkslConversion, ShaderError[]>.Fail([spirv.Error]);

        // The HLSL texture name behind each combined sampler, in declaration order — the same
        // extraction the GL sampler table trusts (issue #189's allocator).
        var pairs = SpirvCombinedSamplerPairs.Extract(spirv.Value.Bytes);
        IReadOnlyList<string> textureNames = pairs.IsSuccess
            ? pairs.Value.Select(p => p.TextureName).ToList()
            : [];

        // 4. SPIR-V -> modern GLSL (SPIRV-Cross; the seam BEFORE the MonoGame rewriter).
        var glsl = new SpirvCrossGlslTranspiler().Transpile(spirv.Value.Bytes, cancellationToken);
        if (glsl.IsFailure)
            return Result<SkslConversion, ShaderError[]>.Fail([glsl.Error]);

        // 5. GLSL -> SkSL.
        var mapped = SkslGlslMapper.Map(
            glsl.Value.Text,
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
