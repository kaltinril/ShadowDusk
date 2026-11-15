#nullable enable

using System.Runtime.CompilerServices;
using System.Text;
using ShadowDusk.Core;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// The ShaderToy / GLSL frontend for <c>ShadowDusk.Compiler</c>'s <c>ShaderConverter</c>:
/// <see cref="ShaderToyConverter.Convert"/> behind <see cref="IShaderFrontend"/>. It registers
/// itself when this assembly loads, and <see cref="ShaderFrontends.Find"/> loads the assembly on
/// first need, so installing the package is enough. Call <see cref="Register"/> yourself only
/// where an assembly cannot be loaded by name (a trimmed application).
/// </summary>
public sealed class ShaderToyFrontend : IShaderFrontend
{
    /// <summary>The code of a conversion error (a construct outside the supported subset).</summary>
    public const string ConversionErrorCode = "SD0006";

    /// <summary>The code of a non-fatal conversion adjustment.</summary>
    public const string ConversionWarningCode = "SD0007";

    private const string MemoryName = "<memory>.glsl";

    /// <inheritdoc />
    public ShaderFormat Format => ShaderFormat.Glsl;

    /// <summary>
    /// Registers this frontend with <see cref="ShaderFrontends"/> as THE GLSL frontend,
    /// replacing any other. Safe to call more than once.
    /// </summary>
    public static void Register() => ShaderFrontends.Register(new ShaderToyFrontend());

    // CA2255 warns against module initializers in libraries because they run code the consumer
    // did not call. This one only adds an entry to a registry, which is the package's purpose,
    // and only when the application has not registered a GLSL frontend of its own: this runs
    // whenever the assembly is first used, which can be long after startup.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterOnLoad() => ShaderFrontends.TryRegister(new ShaderToyFrontend());
#pragma warning restore CA2255

    /// <inheritdoc />
    public Result<ShaderFrontendOutput, ShaderError[]> ToFx(string source, string? nameHint)
    {
        ArgumentNullException.ThrowIfNull(source);
        string file = nameHint ?? MemoryName;

        // With a name, the effect and technique are named after the file, as the ShadowDuskCLI
        // names them for a .glsl input. With none, the converter's own defaults stand, so the
        // result is what ShaderToyConverter.Convert(source) gives.
        string stem = FileStem(nameHint);
        ConvertOptions options = stem.Length == 0
            ? new ConvertOptions()
            : new ConvertOptions { EffectName = SanitizeIdentifier(stem), TechniqueName = SanitizeIdentifier(stem) };

        ConvertResult result = ShaderToyConverter.Convert(source, options);

        if (!result.Success || result.Fx is null)
        {
            ShaderError[] errors = result.Diagnostics.Select(d => Map(file, d)).ToArray();

            // A failed conversion with no diagnostic must still fail loudly.
            if (errors.Length == 0)
            {
                errors =
                [
                    new ShaderError(
                        File: file, Line: 0, Column: 0, Code: ConversionErrorCode,
                        Message: "ShaderToy/GLSL conversion failed without a diagnostic."),
                ];
            }

            return Result<ShaderFrontendOutput, ShaderError[]>.Fail(errors);
        }

        ShaderError[] warnings = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning)
            .Select(d => Map(file, d))
            .ToArray();
        return Result<ShaderFrontendOutput, ShaderError[]>.Ok(new ShaderFrontendOutput(result.Fx, warnings));
    }

    // The same codes, text and location the ShadowDuskCLI reports for a .glsl input.
    private static ShaderError Map(string file, ConvertDiagnostic diagnostic) => new(
        File: file,
        Line: diagnostic.Line,
        Column: diagnostic.Column,
        Code: diagnostic.Severity == DiagnosticSeverity.Error ? ConversionErrorCode : ConversionWarningCode,
        Message: string.IsNullOrEmpty(diagnostic.Construct)
            ? diagnostic.Message
            : $"{diagnostic.Message} (near '{diagnostic.Construct}')",
        Severity: diagnostic.Severity == DiagnosticSeverity.Error ? ShaderErrorSeverity.Error : ShaderErrorSeverity.Warning);

    // The file name without directory or extension. Both separators are split on every host:
    // Path treats '\' as an ordinary character off Windows, and the same hint must name the
    // same technique (it is in the compiled bytes) wherever the game runs.
    internal static string FileStem(string? nameHint)
    {
        if (string.IsNullOrEmpty(nameHint))
            return "";

        string leaf = nameHint[(nameHint.LastIndexOfAny(['/', '\\']) + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot < 0 ? leaf : leaf[..dot];
    }

    // A valid HLSL identifier for the effect and technique name, from the file name
    // ('2d-noise.glsl' would otherwise start with a digit and hold a hyphen).
    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "ShaderToyEffect";

        var text = new StringBuilder(name.Length + 1);
        foreach (char c in name)
            text.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        if (char.IsDigit(text[0]))
            text.Insert(0, '_');

        return text.ToString();
    }
}
