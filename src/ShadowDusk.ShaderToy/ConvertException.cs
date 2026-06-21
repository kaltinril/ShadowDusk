namespace ShadowDusk.ShaderToy;

/// <summary>
/// Thrown internally to signal a "loud reject": an unsupported construct or a syntax error.
/// Carries 1-based line/column from the original GLSL plus the offending construct text so the
/// public <see cref="ShaderToyConverter.Convert"/> can surface a precise <see cref="ConvertDiagnostic"/>.
/// Never let one of these escape the public API — convert it to a failed <see cref="ConvertResult"/>.
/// </summary>
internal sealed class ConvertException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public string? Construct { get; }

    public ConvertException(string message, int line, int column, string? construct = null)
        : base(message)
    {
        Line = line;
        Column = column;
        Construct = construct;
    }
}
