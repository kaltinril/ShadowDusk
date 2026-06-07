#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>A single preprocessor macro definition (a <c>#define</c>) injected into compilation.</summary>
/// <param name="Name">The macro name.</param>
/// <param name="Value">The integer value; defaults to <c>1</c>.</param>
public sealed record MacroDefinition(string Name, int Value = 1);
