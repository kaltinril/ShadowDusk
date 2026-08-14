#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ShaderToy;

namespace ShadowDusk.Cli;

internal sealed class PipelineRunner
{
    public async Task<Result<byte[], IReadOnlyList<ShaderError>>> RunAsync(
        CliArguments args,
        CancellationToken ct = default)
    {
        // Stage 1: Read source file. UnauthorizedAccessException (ACL-denied path,
        // directory-as-file) is an input failure exactly like IOException — map it to
        // X0001 rather than letting it crash out as an internal X0099.
        string sourceText;
        try
        {
            sourceText = await File.ReadAllTextAsync(args.SourceFile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(new ShaderError(
                File: args.SourceFile,
                Line: 0,
                Column: 0,
                Code: "X0001",
                Message: ex.Message));
        }

        // Stage 1.5 (Phase 47): if the input is ShaderToy / GLSL, convert it to .fx text up front;
        // a real .fx is passed straight through unchanged. Everything downstream (stage 2/3) is the
        // EXACT .fx path — the converter is a front-end text transform, glsl -> .fx, nothing more.
        var detection = InputFormatDetector.Detect(args.SourceFile, sourceText, args.InputFormat);
        if (detection.IsFailure)
            return Fail(detection.Error);

        string hlslSource;
        bool isConvertedGlsl = detection.Value == InputKind.Glsl;
        bool isConvertedSlang = detection.Value == InputKind.Slang;
        if (isConvertedGlsl)
        {
            var converted = ConvertShaderToy(args, sourceText);
            if (converted.IsFailure)
                return Result<byte[], IReadOnlyList<ShaderError>>.Fail(converted.Error);
            hlslSource = converted.Value;
        }
        else if (isConvertedSlang)
        {
            // Phase 61 (issue #198): .slang routes through the Slang frontend exactly as .glsl
            // routes through the ShaderToy converter — a PURE TEXT transform upstream of the
            // unchanged pipeline (HLSL-compatible Slang; the body compiles through the same DXC
            // as every .fx, so nothing extra is needed on any platform). Entry-point and
            // Slang-only-construct diagnostics point at the real .slang file and line.
            var converted = ShadowDusk.Compiler.Slang.SlangFrontend.ConvertToFx(
                sourceText,
                new ShadowDusk.Compiler.Slang.SlangConvertOptions
                {
                    SourceName    = args.SourceFile,
                    TechniqueName = SanitizeIdentifier(Path.GetFileNameWithoutExtension(args.SourceFile)),
                });

            if (converted.IsFailure)
                return Result<byte[], IReadOnlyList<ShaderError>>.Fail(converted.Error);

            foreach (var w in converted.Value.Warnings)
                Console.Error.WriteLine(MgcbErrorFormatter.Format(w));

            hlslSource = converted.Value.FxText;
        }
        else
        {
            hlslSource = sourceText;
        }

        // Stage 2: Build options and delegate compilation to the library.
        IIncludeResolver includeResolver = new FileSystemIncludeResolver();

        // F2: for ShaderToy/GLSL input, a pipeline (DXC) error is in the GENERATED HLSL, whose line
        // numbers do NOT correspond to the user's .glsl. Attribute those errors to a synthetic
        // "<name>.generated.fx" name so they are never mistaken for the original source (e.g. a 30-line
        // .glsl reporting "line 51"). Convert-stage diagnostics keep the real .glsl name (they ARE located
        // in the user's GLSL). SourceFileName is diagnostics-only and does not affect output bytes.
        string compileSourceName = isConvertedGlsl || isConvertedSlang
            ? Path.GetFileNameWithoutExtension(args.SourceFile) + ".generated.fx"
            : args.SourceFile;

        // For the converted routes the compile's SourceFileName is a synthetic ".generated.fx"
        // with no directory, so a #include in the ORIGINAL source (legal in the Slang route,
        // whose body is HLSL) would lose its relative anchor. Append the original file's
        // directory to the search paths so those includes keep resolving.
        IReadOnlyList<string> includePaths = args.IncludePaths;
        if (isConvertedSlang
            && Path.GetDirectoryName(Path.GetFullPath(args.SourceFile)) is { Length: > 0 } sourceDir)
        {
            includePaths = [.. args.IncludePaths, sourceDir];
        }

        var options = new CompilerOptions
        {
            Target                 = args.Platform,
            IncludeResolver        = includeResolver,
            AdditionalIncludePaths = includePaths,
            SourceFileName         = compileSourceName,
            Debug                  = args.Debug,
            MgfxVersion            = args.MgfxVersion,
            DxbcBackend            = args.DxbcBackend,
            Defines                = args.Defines ?? [],
            // A --target-runtime profile fully specifies the output target; when set it overrides
            // Target / MgfxVersion (resolved in the pipeline, since the profile implies its backend).
            Profile                = args.Profile,
        };

        var compiler       = new EffectCompiler();
        var compileResult  = await compiler.CompileAsync(hlslSource, options, ct).ConfigureAwait(false);

        if (compileResult.IsFailure)
        {
            // F2: when the converted GLSL/Slang fails the pipeline compile, lead with a Note so the user
            // knows the error below is in the GENERATED HLSL (.fx) produced from their shader, not their
            // source file. Identifier collisions (F1) are auto-fixed at convert time, so reaching here
            // means the generated HLSL hit a real limit (e.g. an SM3 instruction cap on a heavy shader).
            if (isConvertedGlsl || isConvertedSlang)
            {
                var note = new ShaderError(
                    File: args.SourceFile, Line: 0, Column: 0, Code: "SD0003",
                    Message: $"the converted .fx generated from '{Path.GetFileName(args.SourceFile)}' " +
                             "failed to compile. The diagnostics below refer to the GENERATED HLSL " +
                             $"('{compileSourceName}'), not your original source lines.",
                    Severity: ShaderErrorSeverity.Note);
                var withNote = new List<ShaderError> { note };
                withNote.AddRange(compileResult.Error);
                return Result<byte[], IReadOnlyList<ShaderError>>.Fail(withNote);
            }

            return Result<byte[], IReadOnlyList<ShaderError>>.Fail(compileResult.Error);
        }

        // Stage 2.5 (Phase 60, issue #199): an `.xnb` output path means "wrap it", so a consumer
        // can drop the file where their mgfxc-built .xnb sat and keep calling
        // Content.Load<Effect> unchanged. Extension-driven rather than a switch, because that is
        // the seamless shape: the consumer already says where the file goes, and a
        // ShadowDusk-specific flag to get correct output is exactly what the standing directive
        // forbids. It cannot mis-fire — `.xnb` has no other meaning as a shader-compiler output,
        // and every other extension is passed through untouched.
        //
        // The payload inside the container is compileResult.Value.Data VERBATIM, so `out.mgfx`
        // and the payload of `out.xnb` are byte-identical BY CONSTRUCTION, not by a second code
        // path that has to be kept in step (the Phase 42 one-pipeline precedent).
        bool wrapAsXnb = Path.GetExtension(args.OutputFile)
            .Equals(".xnb", StringComparison.OrdinalIgnoreCase);

        byte[] mgfxBytes = wrapAsXnb
            ? compileResult.Value.ToXnb()
            : compileResult.Value.Data;

        // Non-fatal diagnostics — the underlying compiler's verbatim warnings plus
        // the GL portability findings (SD0400-SD0499). Printed to stderr in the
        // MGCB-parseable warning form without failing the build, the same contract
        // as the ShaderToy convert warnings below; a warning-free compile keeps
        // stderr empty.
        foreach (string line in MgcbErrorFormatter.FormatAll(compileResult.Value.Warnings))
            Console.Error.WriteLine(line);

        // Stage 3: Write output file.
        try
        {
            string? outputDir = Path.GetDirectoryName(Path.GetFullPath(args.OutputFile));
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            await File.WriteAllBytesAsync(args.OutputFile, mgfxBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(new ShaderError(
                File: args.OutputFile,
                Line: 0,
                Column: 0,
                Code: "X0002",
                Message: ex.Message));
        }

        return Result<byte[], IReadOnlyList<ShaderError>>.Ok(mgfxBytes);
    }

    // Runs the ShaderToy/GLSL -> .fx converter and surfaces its diagnostics in the CLI's existing
    // MGCB-parseable form, always pointing at the ORIGINAL .glsl source (the emitted .fx text's line
    // numbers are meaningless to the author). On error, returns every diagnostic at once (the converter
    // collects them); on success, returns the .fx text and emits non-fatal warnings (+ an optional
    // drivable-uniforms note) to stderr without failing the build.
    private static Result<string, IReadOnlyList<ShaderError>> ConvertShaderToy(
        CliArguments args, string glsl)
    {
        string effectName = SanitizeIdentifier(Path.GetFileNameWithoutExtension(args.SourceFile));
        var options = new ConvertOptions
        {
            EffectName    = effectName,
            TechniqueName = effectName,
        };

        ConvertResult result = ShaderToyConverter.Convert(glsl, options);

        if (!result.Success || result.Fx is null)
        {
            var errors = result.Diagnostics
                .Select(d => MapDiagnostic(args.SourceFile, d))
                .ToArray();

            // Defensive: a failed convert with no diagnostics should still fail loudly, never silently.
            if (errors.Length == 0)
                errors = new[]
                {
                    new ShaderError(
                        File: args.SourceFile, Line: 0, Column: 0, Code: "SD0006",
                        Message: "ShaderToy/GLSL conversion failed without a diagnostic."),
                };

            return Result<string, IReadOnlyList<ShaderError>>.Fail(errors);
        }

        // Success: surface any non-fatal warnings (e.g. a dropped standalone void main() wrapper).
        foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning))
            Console.Error.WriteLine(MgcbErrorFormatter.Format(MapDiagnostic(args.SourceFile, d)));

        // The drivable effect parameters the consumer must set each frame at runtime. Gated behind
        // --print-uniforms so the default success path keeps stderr empty for the MGCB contract.
        if (args.PrintUniforms && result.UsedUniforms.Count > 0)
        {
            var note = new ShaderError(
                File: args.SourceFile, Line: 0, Column: 0, Code: "SD0000",
                Message: $"drivable effect parameters: {string.Join(", ", result.UsedUniforms)}",
                Severity: ShaderErrorSeverity.Note);
            Console.Error.WriteLine(MgcbErrorFormatter.Format(note));
        }

        return Result<string, IReadOnlyList<ShaderError>>.Ok(result.Fx);
    }

    // ShaderToy convert diagnostics get a dedicated SD#### code space so they are distinguishable from
    // fxc / pipeline errors; MgcbErrorFormatter passes SD#### through unchanged and drops the
    // file(line,col) prefix when Line <= 0. The File is the ORIGINAL .glsl path.
    private static ShaderError MapDiagnostic(string sourceFile, ConvertDiagnostic d)
    {
        ShaderErrorSeverity severity = d.Severity == DiagnosticSeverity.Error
            ? ShaderErrorSeverity.Error
            : ShaderErrorSeverity.Warning;

        // SD0006 / SD0007 are this stage's OWN codes. They used to be SD0010 and SD0001,
        // which are already allocated to "effect source contains no techniques" and
        // "#include file not found" — so a converter failure printed a code whose
        // published meaning was unrelated and unactionable, breaking the registry's
        // one-code-one-condition invariant.
        string code = d.Severity == DiagnosticSeverity.Error ? "SD0006" : "SD0007";

        string message = string.IsNullOrEmpty(d.Construct)
            ? d.Message
            : $"{d.Message} (near '{d.Construct}')";

        return new ShaderError(
            File: sourceFile,
            Line: d.Line,
            Column: d.Column,
            Code: code,
            Message: message,
            Severity: severity);
    }

    // Derives a valid HLSL identifier for the emitted technique/effect name from the source file name
    // (a file like '2d-noise.glsl' would otherwise yield a leading digit / hyphen).
    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "ShaderToyEffect";

        var sb = new System.Text.StringBuilder(name.Length + 1);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        if (char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    private static Result<byte[], IReadOnlyList<ShaderError>> Fail(ShaderError error) =>
        Result<byte[], IReadOnlyList<ShaderError>>.Fail(new ShaderError[] { error });
}
