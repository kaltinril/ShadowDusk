#nullable enable

using System.Text;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Slang;

/// <summary>
/// The ONE place slangc's argument lists and the ONE output-text convention live, shared by
/// every way <see cref="SlangCompiler"/> reaches slangc (issue #257): the child process on
/// desktop and the in-process WebAssembly build in the browser (<c>ShadowDusk.Slang.Wasm</c>,
/// which hands each list to slang's own option parser verbatim). Two invocation shapes exist:
/// the per-entry compile (<see cref="Build"/>) and the preprocess-only pass
/// (<see cref="BuildPreprocess"/>). Keeping them here is what makes the two hosts' slangc
/// output comparable byte for byte; a second copy of either would drift silently.
/// </summary>
internal static class SlangcArguments
{
    /// <summary>
    /// The slangc command line for one entry point, excluding the executable name. Source
    /// is read from stdin (<c>-- -</c>), which is also why every host sees the input named
    /// <c>&lt;stdin&gt;</c> in slangc's <c>#line</c> directives and diagnostics.
    /// </summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<MacroDefinition> platformMacros,
        IReadOnlyList<UserDefine> defines,
        string entryName,
        string stage)
    {
        List<string> args = CommonPrefix(platformMacros, defines);
        args.Add("-entry");
        args.Add(entryName);
        args.Add("-stage");
        args.Add(stage);
        // '--' then '-': read the single input file from stdin (slangc -h: "Use '-' once to
        // read from standard input; -lang is required, stdin is limited to 256 MiB").
        args.Add("--");
        args.Add("-");
        return args;
    }

    /// <summary>
    /// The slangc command line for the PREPROCESS-ONLY pass (issue #252 follow-up): the
    /// compile's own list with <c>-E</c> ("Output the preprocessing result and exit") in place
    /// of the entry point. slangc then prints the source's token stream after its own
    /// preprocessor ran with exactly the macros the compile sees (inactive <c>#if</c> branches
    /// gone, macros expanded, comments dropped), which is what
    /// <see cref="SlangcRegisterStripper.AuthorBoundNames"/> reads to learn which registers
    /// the author wrote. Same list on both transports, like <see cref="Build"/>.
    /// </summary>
    /// <remarks>
    /// Measured (v2026.14.1, native and the WebAssembly build alike): the output is
    /// independent of <c>-target</c>/<c>-entry</c>/<c>-stage</c>, and <c>-E</c> exits 0 even
    /// when the preprocessor reports an error (the error is on stderr), which is why
    /// <see cref="SlangCompiler"/> runs this pass only after every entry point compiled.
    /// </remarks>
    public static IReadOnlyList<string> BuildPreprocess(
        IReadOnlyList<MacroDefinition> platformMacros,
        IReadOnlyList<UserDefine> defines)
    {
        List<string> args = CommonPrefix(platformMacros, defines);
        args.Add("-E");
        args.Add("--");
        args.Add("-");
        return args;
    }

    // Everything the compile and the preprocess-only pass share: language, macros, target and
    // the two emission flags. One copy, so the preprocess pass can never see a different
    // macro set than the compile it describes.
    private static List<string> CommonPrefix(
        IReadOnlyList<MacroDefinition> platformMacros,
        IReadOnlyList<UserDefine> defines)
    {
        var args = new List<string>(16 + platformMacros.Count + defines.Count)
        {
            "-lang",
            "slang",
        };
        // Platform macros first, user defines after: same ordering ToDxcFlags() uses for
        // the ordinary .fx route, so a user -D of the same name (unusual, but not
        // forbidden) still wins.
        foreach (MacroDefinition macro in platformMacros)
            args.Add($"-D{macro.Name}={macro.Value}");
        foreach (UserDefine define in defines)
            args.Add($"-D{define.Name}={define.Value}");
        args.Add("-target");
        args.Add("hlsl");
        // Without this, slangc wraps every cbuffer's members in a generated
        // 'SLANG_ParameterGroup_*' struct and gives the cbuffer itself a single member of
        // that struct type (Phase 65 §2's residue finding). That is legal HLSL (DXC
        // compiles it fine), but ShadowDusk's own OpenGL uniform-block lowering (the
        // MojoShader-dialect GLSL rewrite) only models FLAT float/vec2/vec3/vec4/mat4
        // members and arrays of those directly inside a cbuffer, so a nested-struct member
        // fails loudly with SD0210 (measured, Phase 66 A3: every corpus shader with a
        // cbuffer failed OpenGL specifically until this flag was added). This flag makes
        // slangc emit flat members directly in the cbuffer instead; DirectX_11 was
        // unaffected either way.
        args.Add("-no-hlsl-pack-constant-buffer-elements");
        // Phase 66 A4: without this, slangc renames every symbol with an '_N' suffix
        // ('float BlurAmount' -> 'float BlurAmount_0'), which would surface in a
        // consumer's compiled effect's reflected parameter table and break
        // effect.Parameters["BlurAmount"] lookups. '-no-mangle' is documented by slangc
        // itself as experimental ("do as little mangling of names as possible"), but
        // measured (Phase 66 A4) against the full 21-shader corpus on both DirectX_11 and
        // OpenGL: every top-level declaration that matters for the reflected parameter
        // table (cbuffer names, cbuffer members, Texture2D/SamplerState declarations)
        // comes back with the author's exact original name, with no collisions anywhere
        // in the corpus (including GenericsProbe.slang's real generic-over-interface
        // function). Local variables and struct field names still carry an '_N' suffix,
        // but those are never part of an Effect's reflected parameter table.
        // Issue #228: the '_N' numbering of structs/functions is per slangc run, so two entry
        // points can reuse one name for different generic instantiations; SlangHlslMerger
        // renames those apart when it merges the per-entry units.
        args.Add("-no-mangle");
        return args;
    }

    /// <summary>
    /// Normalizes raw slangc output text to the exact string the desktop route produces:
    /// <see cref="System.Diagnostics.Process.OutputDataReceived"/> splits the stream on
    /// <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>, delivers an unterminated last line at end of
    /// stream, and <see cref="SlangCompiler.RunSlangc(string, string, string, IReadOnlyList{string})"/> re-joins every line with <c>'\n'</c>.
    /// The in-process route captures slangc's writer output whole, so it applies this to land
    /// on the same text (and therefore the same assembled <c>.fx</c>) on every host.
    /// </summary>
    public static string JoinOutputLines(string raw)
    {
        if (raw.Length == 0)
            return raw;

        var sb = new StringBuilder(raw.Length + 1);
        int lineStart = 0;
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\n' || c == '\r')
            {
                sb.Append(raw, lineStart, i - lineStart).Append('\n');
                i += c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n' ? 2 : 1;
                lineStart = i;
                continue;
            }
            i++;
        }
        if (lineStart < raw.Length)
            sb.Append(raw, lineStart, raw.Length - lineStart).Append('\n');
        return sb.ToString();
    }
}
