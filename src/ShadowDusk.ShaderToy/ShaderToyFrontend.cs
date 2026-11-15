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

    /// <summary>Registers this frontend with <see cref="ShaderFrontends"/>. Safe to call more than once.</summary>
    public static void Register() => ShaderFrontends.Register(new ShaderToyFrontend());

    // CA2255 warns against module initializers in libraries because they run code the consumer
    // did not call. This one only adds an entry to a registry, which is the package's purpose.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterOnLoad() => Register();
#pragma warning restore CA2255

    /// <inheritdoc />
    public Result<ShaderFrontendOutput, ShaderError[]> ToFx(string source, string? nameHint)
    {
        ArgumentNullException.ThrowIfNull(source);
        string file = nameHint ?? MemoryName;
        string effectName = SanitizeIdentifier(nameHint is null ? "" : Path.GetFileNameWithoutExtension(nameHint));

        ConvertResult result = ShaderToyConverter.Convert(
            source, new ConvertOptions { EffectName = effectName, TechniqueName = effectName });

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
