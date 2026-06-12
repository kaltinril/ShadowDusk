#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

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
        string hlslSource;
        try
        {
            hlslSource = await File.ReadAllTextAsync(args.SourceFile, ct).ConfigureAwait(false);
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

        // Stage 2: Build options and delegate compilation to the library.
        IIncludeResolver includeResolver = new FileSystemIncludeResolver();

        var options = new CompilerOptions
        {
            Target                 = args.Platform,
            IncludeResolver        = includeResolver,
            AdditionalIncludePaths = args.IncludePaths,
            SourceFileName         = args.SourceFile,
            Debug                  = args.Debug,
            MgfxVersion            = args.MgfxVersion,
            DxbcBackend            = args.DxbcBackend,
        };

        var compiler       = new EffectCompiler();
        var compileResult  = await compiler.CompileAsync(hlslSource, options, ct).ConfigureAwait(false);

        if (compileResult.IsFailure)
            return Result<byte[], IReadOnlyList<ShaderError>>.Fail(compileResult.Error);

        byte[] mgfxBytes = compileResult.Value.Data;

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

    private static Result<byte[], IReadOnlyList<ShaderError>> Fail(ShaderError error) =>
        Result<byte[], IReadOnlyList<ShaderError>>.Fail(new ShaderError[] { error });
}
