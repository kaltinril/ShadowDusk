#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// A user-supplied preprocessor macro — the library-level equivalent of mgfxc's
/// <c>/Defines:</c> CLI flag, injected after the platform macros. Unlike
/// <see cref="MacroDefinition"/> the value is free-form text, matching what fxc and
/// mgfxc accept (<c>NAME=text</c>; a bare <c>NAME</c> defines <c>1</c>).
/// </summary>
/// <param name="Name">The macro name.</param>
/// <param name="Value">The macro body; defaults to <c>1</c>.</param>
public sealed record UserDefine(string Name, string Value = "1");
