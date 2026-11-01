#nullable enable

using System.Runtime.CompilerServices;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Preprocessing;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// Source shapes the pinned vkd3d-shader cannot survive, refused with a registered diagnostic
/// before any host hands the source to <c>vkd3d_shader_compile</c>. One native call cannot be
/// interrupted, and a fault inside it ends the consumer's whole process (a hang never returns
/// the compile at all), so these are decided in
/// managed code on the preprocessed view (dead <c>#if</c> branches dropped, macros expanded), and
/// identically on the desktop and the browser host (<see cref="Vkd3dCompileContract.CheckSource"/>).
/// </summary>
/// <remarks>
/// The view is only read, never compiled: vkd3d still gets the source exactly as before
/// (<see cref="Vkd3dCompileContract.PrepareSource"/>), so no output byte can move. Every entry
/// point of an effect compiles the same source string, so the verdict is cached per string
/// instance.
/// </remarks>
internal static class Vkd3dInputGuard
{
    /// <summary>A cast to <c>void</c> (issue #448).</summary>
    public const string VoidCastCode = "SD0239";

    /// <summary>A macro that expands to itself, which vkd3d's preprocessor never returns on (issue #449).</summary>
    public const string SelfReferenceCode = "SD0240";

    private sealed record Verdict(string SourceFileName, ShaderError? Error);

    private static readonly ConditionalWeakTable<string, Verdict> Cache = new();

    /// <summary>The refusal for <paramref name="hlslSource"/>, or null when vkd3d may compile it.</summary>
    public static ShaderError? Check(string hlslSource, string sourceFileName)
    {
        if (Cache.TryGetValue(hlslSource, out Verdict? cached) &&
            string.Equals(cached.SourceFileName, sourceFileName, StringComparison.Ordinal))
        {
            return cached.Error;
        }

        ShaderError? error = Decide(hlslSource, sourceFileName);
        Cache.AddOrUpdate(hlslSource, new Verdict(sourceFileName, error));
        return error;
    }

    private static ShaderError? Decide(string hlslSource, string sourceFileName)
    {
        Result<FxMacroPreprocessor.InputGuardView, ShaderError> view =
            FxMacroPreprocessor.ProcessForInputGuards(hlslSource, sourceFileName);

        if (view.IsSuccess && view.Value.SelfReference is { } selfReference)
            return SelfReferenceError(selfReference, hlslSource, sourceFileName);

        // Cheap gate: no 'void' token anywhere, so no macro can produce a cast to it either
        // (short of pasting the word together, which nobody writes).
        if (!ContainsWord(hlslSource, "void"))
            return null;

        // A source the managed preprocessor cannot read (SD0009) is still vkd3d's to judge, as
        // before; the casts are then looked for in the written text, outside comments and
        // directives (a dead branch counts, which only ever refuses more).
        IReadOnlyList<(int Line, int Column)> casts = view.IsSuccess
            ? FxMacroPreprocessor.FindVoidCasts(view.Value.View, skipDirectives: true)
            : FxMacroPreprocessor.FindVoidCasts(hlslSource, skipDirectives: true);
        if (casts.Count == 0)
            return null;

        (int physicalLine, int column) = casts[0];
        (string file, int line) = new SourceLineMap(hlslSource, sourceFileName).Resolve(physicalLine);
        return new ShaderError(
            File: file,
            Line: line,
            Column: column,
            Code: VoidCastCode,
            Message: "a cast to 'void' ('(void)expression') is not valid HLSL: fxc and mgfxc reject it (error X3017), " +
                     "and the pinned vkd3d-shader that compiles the DirectX 11 and FNA targets crashes the whole " +
                     "process on it, so ShadowDusk refuses it before compiling. Drop the cast: an expression " +
                     "statement's value is discarded anyway.");
    }

    private static ShaderError SelfReferenceError(
        FxMacroPreprocessor.BlockedSelfReference selfReference, string hlslSource, string sourceFileName)
    {
        // Inside a directive (an #if) the token has no physical line of its own: the directive's
        // author position, which the preprocessor already followed through the #line directives.
        (string file, int line) = selfReference.PhysicalLine > 0
            ? new SourceLineMap(hlslSource, sourceFileName).Resolve(selfReference.PhysicalLine)
            : (selfReference.File, selfReference.Line);
        return new ShaderError(
            File: file,
            Line: line,
            Column: 0,
            Code: SelfReferenceCode,
            Message: $"macro '{selfReference.Name}' expands to itself here (a self-referential or mutually recursive " +
                     "macro). C stops the expansion at the repeated name, and fxc/mgfxc compile it, but the " +
                     "preprocessor of the pinned vkd3d-shader that compiles the DirectX 11 and FNA targets never " +
                     "returns on it, so ShadowDusk refuses it rather than hang. Rename the macro or the name it " +
                     $"expands to so that '{selfReference.Name}' does not expand to itself.");
    }

    private static bool ContainsWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = at == 0 || !IsIdentifierChar(text[at - 1]);
            int end = at + word.Length;
            bool endOk = end >= text.Length || !IsIdentifierChar(text[end]);
            if (startOk && endOk)
                return true;
            at = end;
        }
        return false;

        static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }
}
