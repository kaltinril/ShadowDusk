using ShadowDusk.ShaderToy;
using ShadowDusk.ShaderToy.Multipass;

namespace ShadowDusk.ShaderToy.Cli;

/// <summary>
/// Command-line front end for the Phase 46 ShaderToy → <c>.fx</c> experimental converter.
/// Thin wrapper over <see cref="ShaderToyConverter.Convert(string, ConvertOptions?)"/>: parses
/// arguments, reads input (file or stdin), and emits either the produced <c>.fx</c> or
/// MGCB-parseable diagnostics. Also offers a <c>--multipass</c> BATCH mode that converts a
/// ShaderToy multi-tab export (the API JSON) into one <c>.fx</c> per render tab plus a wiring
/// manifest and a documented render example (see <see cref="MultipassConverter"/>).
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitConvertFailed = 1;
    private const int ExitBadUsage = 2;

    private const string Usage =
        "Usage: shadertoy2fx <input.glsl> [-o <output.fx>] [--common <common.glsl>] " +
        "[--name <EffectName>] [--technique <TechniqueName>]\n" +
        "   or: shadertoy2fx --multipass <export.json> -o <outdir>\n" +
        "  <input.glsl>          ShaderToy image-tab GLSL; '-' or omitted reads stdin.\n" +
        "  -o <output.fx>|<dir>  Write the .fx here (single mode) or the output directory (multipass).\n" +
        "  --common <common.glsl> Optional ShaderToy 'Common' tab source.\n" +
        "  --name <EffectName>   Effect name embedded in the emitted .fx.\n" +
        "  --technique <name>    Name of the emitted technique.\n" +
        "  --multipass <export.json>  BATCH: convert a ShaderToy multi-tab export to one .fx per\n" +
        "                        render tab plus manifest.json and WIRING.md in the -o directory.";

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

        // BATCH multipass-export mode is a distinct path; dispatch to it before the single-file flow.
        if (parsed.MultipassPath is not null)
        {
            return RunMultipass(parsed);
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

    /// <summary>
    /// BATCH multipass-export mode: parse a ShaderToy multi-tab export JSON, convert each render tab to
    /// a <c>.fx</c>, and write the <c>.fx</c> files + <c>manifest.json</c> + <c>WIRING.md</c> into the
    /// output directory. Exits non-zero (loud) if any pass fails to convert; per-pass errors are written
    /// to stderr in MGCB form.
    /// </summary>
    private static int RunMultipass(ParsedArgs parsed)
    {
        if (parsed.OutputPath is null)
        {
            Console.Error.WriteLine("error: --multipass requires an output directory via -o <outdir>.");
            Console.Error.WriteLine(Usage);
            return ExitBadUsage;
        }

        string json;
        try
        {
            json = File.ReadAllText(parsed.MultipassPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitBadUsage;
        }

        if (!ShaderToyProject.TryParse(json, out ShaderToyProject? project, out string? parseError))
        {
            Console.Error.WriteLine($"{parsed.MultipassPath}(0,0): error: invalid ShaderToy export: {parseError}");
            return ExitConvertFailed;
        }

        var options = new ConvertOptions
        {
            EffectName = parsed.EffectName ?? new ConvertOptions().EffectName,
            TechniqueName = parsed.TechniqueName ?? new ConvertOptions().TechniqueName,
        };

        MultipassResult result = MultipassConverter.Convert(project!, options);

        // Project-level diagnostics (skipped sound/cubemap passes, parse warnings).
        WriteDiagnostics(parsed.MultipassPath!, result.Diagnostics);

        try
        {
            Directory.CreateDirectory(parsed.OutputPath);

            foreach (MultipassPassResult pass in result.Passes)
            {
                // Per-pass diagnostics, labelled by the pass output file so they read like MGCB errors.
                WriteDiagnostics(pass.OutputFileName, pass.Diagnostics);

                if (pass.Fx is not null)
                {
                    File.WriteAllText(Path.Combine(parsed.OutputPath, pass.OutputFileName), pass.Fx);
                }
            }

            File.WriteAllText(
                Path.Combine(parsed.OutputPath, "manifest.json"), MultipassManifest.ToJson(result));
            File.WriteAllText(
                Path.Combine(parsed.OutputPath, "WIRING.md"), MultipassManifest.ToWiringMarkdown(result));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitBadUsage;
        }

        if (!result.Success)
        {
            Console.Error.WriteLine("error: one or more render passes failed to convert (see diagnostics above).");
            return ExitConvertFailed;
        }

        string order = string.Join(" -> ", result.Passes.Select(p => p.Name));
        Console.Error.WriteLine($"info: converted {result.Passes.Count} pass(es): {order}");
        Console.Error.WriteLine($"info: wrote .fx files + manifest.json + WIRING.md to {parsed.OutputPath}");
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
        string? multipass = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-o":
                    output = TakeValue(args, ref i, arg);
                    break;
                case "--multipass":
                    multipass = TakeValue(args, ref i, arg);
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

        if (multipass is not null && input is not null)
        {
            throw new UsageException("--multipass takes the export via its own value; do not also pass a positional <input.glsl>.");
        }

        return new ParsedArgs(input, output, common, name, technique, multipass);
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
        string? TechniqueName,
        string? MultipassPath);

    private sealed class UsageException(string message) : Exception(message);
}
