#nullable enable

using System.Text;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Slang;

/// <summary>Options for <see cref="SlangFrontend.ConvertToFxAsync"/>.</summary>
public sealed class SlangConvertOptions
{
    /// <summary>
    /// The on-disk path of the <c>.slang</c> source, when it has one. When set and present,
    /// <c>slangc</c> is pointed at the real file so Slang's own <c>import</c>/include
    /// resolution works from the file's directory; otherwise the source text is compiled from a
    /// temporary copy.
    /// </summary>
    public string? SourceFilePath { get; init; }

    /// <summary>The logical source name used in diagnostics. Defaults to <c>"&lt;memory&gt;.slang"</c>.</summary>
    public string SourceName { get; init; } = "<memory>.slang";

    /// <summary>Name of the synthesized <c>technique</c>. Defaults to <c>SlangEffect</c>.</summary>
    public string TechniqueName { get; init; } = "SlangEffect";
}

/// <summary>The successful product of the Slang frontend: <c>.fx</c> text for the unchanged pipeline.</summary>
/// <param name="FxText">The generated <c>.fx</c> effect source.</param>
/// <param name="Warnings">Non-fatal findings (e.g. a parameter kept under its mangled name).</param>
public sealed record SlangFxConversion(string FxText, IReadOnlyList<ShaderError> Warnings);

/// <summary>
/// The <b>Slang input frontend</b> (issue #198): accepts a <c>.slang</c> source and produces
/// ordinary <c>.fx</c> effect text for ShadowDusk's <b>unchanged</b> faithful pipeline —
/// <c>.slang → [slangc] → HLSL → (merge/demangle) → .fx → the normal compile</c>. DXC remains
/// the only HLSL→SPIR-V compiler; Slang sits strictly <i>upstream</i>, in the same
/// architectural slot as the ShaderToy/GLSL converter, so no existing output byte can change.
///
/// <para><b>What it accepts</b> (owner direction 2026-08-13): valid Slang, rejecting only what
/// MonoGame itself cannot hold. slangc's own diagnostics pass through verbatim; an entry point
/// whose stage no <c>Effect</c> can load (compute, mesh, raytracing — stock MonoGame and KNI
/// hold exactly vertex + pixel) is rejected loudly by name (<c>SD0602</c>), never silently
/// skipped. There is no ShadowDusk-blessed subset of Slang.</para>
///
/// <para><b>The technique block is synthesized.</b> Slang has no <c>technique</c>/<c>pass</c>
/// concept (`slangc` errors on the FX9 block of any real <c>.fx</c> — measured), so entry
/// points are declared the Slang way, with <c>[shader("vertex")]</c> /
/// <c>[shader("fragment")]</c> attributes, and the frontend generates a one-pass technique
/// from them: both stages when both are present, pixel-only (the SpriteBatch shape) when only a
/// fragment entry exists. Shader-model selection follows the ShaderToy frontend's measured
/// convention: <c>#if SM4</c> → <c>vs/ps_4_0_level_9_1</c> (mgfxc's DirectX_11 floor), else
/// <c>vs/ps_3_0</c>.</para>
///
/// <para><b>No route through this frontend is `mgfxc`-equivalent</b> — `mgfxc` cannot read
/// Slang at all. The pipeline below the generated-HLSL seam is as faithful as it ever was; the
/// seam above it is Slang's own HLSL emission.</para>
/// </summary>
public static class SlangFrontend
{
    /// <summary>File extension the CLI auto-routes through this frontend.</summary>
    public const string Extension = ".slang";

    /// <summary>
    /// Converts Slang source to <c>.fx</c> effect text, or returns the loud diagnostics
    /// (slangc's own, verbatim, or ShadowDusk's registered <c>SD06xx</c> codes).
    /// </summary>
    /// <param name="slangSource">The Slang source text.</param>
    /// <param name="options">Conversion options; see <see cref="SlangConvertOptions"/>.</param>
    /// <param name="cancellationToken">Cancels the underlying <c>slangc</c> invocation.</param>
    public static async Task<Result<SlangFxConversion, ShaderError[]>> ConvertToFxAsync(
        string slangSource,
        SlangConvertOptions options,
        CancellationToken cancellationToken = default)
    {
        // 1. Entry points, from the user's own source (the [shader(...)] attributes are the
        //    only authoritative statement of intent — nothing here guesses).
        var entries = SlangEntryScanner.Scan(slangSource, options.SourceName);
        if (entries.IsFailure)
            return Result<SlangFxConversion, ShaderError[]>.Fail(entries.Error);

        // 2. The toolchain.
        (string? tool, IReadOnlyList<string> probed) = SlangToolchain.Locate();
        if (tool is null)
        {
            return Result<SlangFxConversion, ShaderError[]>.Fail(
                [SlangToolchain.NotFound(options.SourceName, probed)]);
        }

        // 3. Compile from the real file when there is one (imports resolve from its directory);
        //    a temp copy otherwise.
        string sourcePath;
        string? tempDir = null;
        if (options.SourceFilePath is not null && File.Exists(options.SourceFilePath))
        {
            sourcePath = options.SourceFilePath;
        }
        else
        {
            tempDir = Path.Combine(Path.GetTempPath(), "shadowdusk_slang_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            sourcePath = Path.Combine(tempDir, Path.GetFileName(options.SourceName));
            await File.WriteAllTextAsync(sourcePath, slangSource, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var emitted = await SlangToolchain
                .EmitHlslAsync(tool, sourcePath, entries.Value, cancellationToken)
                .ConfigureAwait(false);
            if (emitted.IsFailure)
                return Result<SlangFxConversion, ShaderError[]>.Fail(emitted.Error);

            // 4. Merge, strip, demangle.
            bool userHadRegisters = slangSource.Contains("register", StringComparison.Ordinal);
            var processed = SlangHlslPostProcessor.Process(
                emitted.Value, userHadRegisters, options.SourceName);
            if (processed.IsFailure)
                return Result<SlangFxConversion, ShaderError[]>.Fail(processed.Error);

            // 5. Assemble the .fx.
            string fx = Synthesize(processed.Value.Body, entries.Value, options);
            return Result<SlangFxConversion, ShaderError[]>.Ok(
                new SlangFxConversion(fx, processed.Value.Warnings));
        }
        finally
        {
            if (tempDir is not null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* non-fatal */ }
            }
        }
    }

    private static string Synthesize(
        string body, IReadOnlyList<SlangEntryPoint> entries, SlangConvertOptions options)
    {
        SlangEntryPoint? vs = entries.FirstOrDefault(e => e.Stage == SlangStage.Vertex);
        SlangEntryPoint? ps = entries.FirstOrDefault(e => e.Stage == SlangStage.Fragment);

        var sb = new StringBuilder();
        sb.AppendLine($"// Generated from '{options.SourceName}' by ShadowDusk's Slang frontend.");
        sb.AppendLine("// The HLSL below is slangc's own emission (merged across entry points and");
        sb.AppendLine("// demangled); the technique block is synthesized from the [shader(...)] attributes.");
        sb.AppendLine();

        // The ShaderToy frontend's measured convention, reasons and all: mgfxc's DirectX_11
        // profile REJECTS anything below SM 4.0 level 9.1, while its OpenGL profile caps at
        // SM3 and ShadowDusk's FNA target is MojoShader SM2-3 — so gate on SM4 (which exactly
        // the DirectX profiles define), not on OPENGL.
        sb.AppendLine("#if SM4");
        sb.AppendLine("    #define VS_SHADERMODEL vs_4_0_level_9_1");
        sb.AppendLine("    #define PS_SHADERMODEL ps_4_0_level_9_1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define VS_SHADERMODEL vs_3_0");
        sb.AppendLine("    #define PS_SHADERMODEL ps_3_0");
        sb.AppendLine("#endif");
        sb.AppendLine();
        sb.AppendLine(body.Trim());
        sb.AppendLine();
        sb.AppendLine($"technique {options.TechniqueName}");
        sb.AppendLine("{");
        sb.AppendLine("    pass P0");
        sb.AppendLine("    {");
        if (vs is not null)
            sb.AppendLine($"        VertexShader = compile VS_SHADERMODEL {vs.Name}();");
        if (ps is not null)
            sb.AppendLine($"        PixelShader = compile PS_SHADERMODEL {ps.Name}();");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
