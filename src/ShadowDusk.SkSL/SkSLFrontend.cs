#nullable enable

using System.Runtime.CompilerServices;
using ShadowDusk.Core;
using ShadowDusk.ShaderToy;

namespace ShadowDusk.SkSL;

/// <summary>
/// The SkSL frontend for <c>ShadowDusk.Compiler</c>'s <c>ShaderConverter</c>:
/// <see cref="SkSLToFxConverter.Convert"/> behind <see cref="IShaderFrontend"/>. SkSL text has no
/// marker of its own, so <c>ShaderConverter</c> routes to this frontend only when the name hint
/// ends in <c>.sksl</c> (<see cref="ShaderFormat.Sksl"/>). It registers itself when this assembly
/// loads, and <see cref="ShaderFrontends.Find"/> loads the assembly on first need, so installing
/// the package is enough. Call <see cref="Register"/> yourself only where an assembly cannot be
/// loaded by name (a trimmed application).
/// </summary>
/// <remarks>
/// The effect and technique keep the converter's names (<see cref="SkSLToFxConverter.DefaultEffectName"/>,
/// <see cref="SkSLToFxConverter.PremultipliedTechniqueName"/>, <see cref="SkSLToFxConverter.StraightAlphaTechniqueName"/>),
/// whatever the file is called: a host selects the technique by that name. Diagnostics keep the
/// stable <c>SD0660</c> to <c>SD0673</c> codes the converter reports.
/// </remarks>
public sealed class SkSLFrontend : IShaderFrontend
{
    // The code of a ShaderToy-stage error and warning, used when a diagnostic carries none.
    private const string ErrorCode = "SD0672";
    private const string WarningCode = "SD0673";

    private const string MemoryName = "<memory>.sksl";

    /// <inheritdoc />
    public ShaderFormat Format => ShaderFormat.Sksl;

    /// <summary>
    /// Registers this frontend with <see cref="ShaderFrontends"/> as THE SkSL frontend,
    /// replacing any other. Safe to call more than once.
    /// </summary>
    public static void Register() => ShaderFrontends.Register(new SkSLFrontend());

    // CA2255 warns against module initializers in libraries because they run code the consumer
    // did not call. This one only adds an entry to a registry, which is the package's purpose,
    // and only when the application has not registered an SkSL frontend of its own.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterOnLoad() => ShaderFrontends.TryRegister(new SkSLFrontend());
#pragma warning restore CA2255

    /// <inheritdoc />
    public Result<ShaderFrontendOutput, ShaderError[]> ToFx(string source, string? nameHint)
    {
        ArgumentNullException.ThrowIfNull(source);
        string file = string.IsNullOrEmpty(nameHint) ? MemoryName : nameHint;

        ConvertResult result = SkSLToFxConverter.Convert(source);

        if (!result.Success || result.Fx is null)
        {
            ShaderError[] errors = result.Diagnostics.Select(d => Map(file, d)).ToArray();

            // A failed conversion with no diagnostic must still fail loudly.
            if (errors.Length == 0)
            {
                errors =
                [
                    new ShaderError(
                        File: file, Line: 0, Column: 0, Code: ErrorCode,
                        Message: "SkSL conversion failed without a diagnostic."),
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

    private static ShaderError Map(string file, ConvertDiagnostic diagnostic)
    {
        bool isError = diagnostic.Severity == DiagnosticSeverity.Error;
        return new ShaderError(
            File: file,
            Line: diagnostic.Line,
            Column: diagnostic.Column,
            Code: diagnostic.Code ?? (isError ? ErrorCode : WarningCode),
            Message: string.IsNullOrEmpty(diagnostic.Construct)
                ? diagnostic.Message
                : $"{diagnostic.Message} (near '{diagnostic.Construct}')",
            Severity: isError ? ShaderErrorSeverity.Error : ShaderErrorSeverity.Warning);
    }
}
