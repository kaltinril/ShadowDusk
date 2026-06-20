using ShadowDusk.ShaderToy;

namespace ShadowDusk.ShaderToy.Cli;

/// <summary>
/// Command-line front end for the Phase 46 ShaderToy → <c>.fx</c> experimental converter.
/// Thin wrapper over <see cref="ShaderToyConverter.Convert(string, ConvertOptions?)"/>: parses
/// arguments, reads input (file or stdin), and emits either the produced <c>.fx</c> or
/// MGCB-parseable diagnostics.
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitConvertFailed = 1;
    private const int ExitBadUsage = 2;

    private const string Usage =
        "Usage: shadertoy2fx <input.glsl> [-o <output.fx>] [--common <common.glsl>] " +
        "[--name <EffectName>] [--technique <TechniqueName>]\n" +
        "  <input.glsl>          ShaderToy image-tab GLSL; '-' or omitted reads stdin.\n" +
        "  -o <output.fx>        Write the .fx here instead of stdout.\n" +
        "  --common <common.glsl> Optional ShaderToy 'Common' tab source.\n" +
        "  --name <EffectName>   Effect name embedded in the emitted .fx.\n" +
        "  --technique <name>    Name of the emitted technique.";

    private static int Main(string[] args)
    {
        ParsedArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(Usage);
            return ExitBadUsage;
        }

        // The label used in diagnostics: the real path, or "<stdin>" when reading the console.
        string inputLabel = parsed.InputPath is null or "-" ? "<stdin>" : parsed.InputPath;

        string glsl;
        string? common;
        try
        {
            glsl = ReadInput(parsed.InputPath);
            common = parsed.CommonPath is null ? null : File.ReadAllText(parsed.CommonPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitBadUsage;
        }

        var options = new ConvertOptions
        {
            CommonSource = common,
            EffectName = parsed.EffectName ?? new ConvertOptions().EffectName,
            TechniqueName = parsed.TechniqueName ?? new ConvertOptions().TechniqueName,
        };

        ConvertResult result = ShaderToyConverter.Convert(glsl, options);

        if (!result.Success || result.Fx is null)
        {
            WriteDiagnostics(inputLabel, result.Diagnostics);
            return ExitConvertFailed;
        }

        // Surface any non-fatal warnings even on success, so they are not silently lost.
        WriteDiagnostics(inputLabel, result.Diagnostics);

        try
        {
            if (parsed.OutputPath is null)
            {
                Console.Out.Write(result.Fx);
            }
            else
            {
                File.WriteAllText(parsed.OutputPath, result.Fx);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitBadUsage;
        }

        string uniforms = result.UsedUniforms.Count == 0 ? "(none)" : string.Join(", ", result.UsedUniforms);
        Console.Error.WriteLine($"info: referenced uniforms: {uniforms}");
        return ExitSuccess;
    }

    private static string ReadInput(string? inputPath)
    {
        if (inputPath is null or "-")
        {
            return Console.In.ReadToEnd();
        }

        return File.ReadAllText(inputPath);
    }

    private static void WriteDiagnostics(string inputLabel, IReadOnlyList<ConvertDiagnostic> diagnostics)
    {
        foreach (ConvertDiagnostic diagnostic in diagnostics)
        {
            string severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            string construct = string.IsNullOrEmpty(diagnostic.Construct) ? string.Empty : $" ({diagnostic.Construct})";
            Console.Error.WriteLine(
                $"{inputLabel}({diagnostic.Line},{diagnostic.Column}): {severity}: {diagnostic.Message}{construct}");
        }
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        string? input = null;
        string? output = null;
        string? common = null;
        string? name = null;
        string? technique = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-o":
                    output = TakeValue(args, ref i, arg);
                    break;
                case "--common":
                    common = TakeValue(args, ref i, arg);
                    break;
                case "--name":
                    name = TakeValue(args, ref i, arg);
                    break;
                case "--technique":
                    technique = TakeValue(args, ref i, arg);
                    break;
                case "-h" or "--help":
                    throw new UsageException("help requested");
                default:
                    if (arg.Length > 1 && arg[0] == '-' && arg != "-")
                    {
                        throw new UsageException($"unknown flag '{arg}'");
                    }

                    if (input is not null)
                    {
                        throw new UsageException($"unexpected extra argument '{arg}'");
                    }

                    input = arg;
                    break;
            }
        }

        return new ParsedArgs(input, output, common, name, technique);
    }

    private static string TakeValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new UsageException($"missing value for '{flag}'");
        }

        index++;
        return args[index];
    }

    private sealed record ParsedArgs(
        string? InputPath,
        string? OutputPath,
        string? CommonPath,
        string? EffectName,
        string? TechniqueName);

    private sealed class UsageException(string message) : Exception(message);
}
