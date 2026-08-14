#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Slang;

/// <summary>The post-processed HLSL body plus the non-fatal findings made on the way.</summary>
internal sealed record SlangProcessedHlsl(string Body, IReadOnlyList<ShaderError> Warnings);

/// <summary>
/// Turns slangc's raw multi-entry emission into one clean HLSL translation unit. The pinned
/// slangc refuses per-entry <c>-o</c> files but happily emits every kernel to stdout — as
/// <b>separate concatenated modules</b>, each with its own prologue and its own copy of the
/// shared declarations (measured 2026-08-13 on v2026.14.1: the shared cbuffer/struct blocks are
/// byte-identical between modules, which is what makes textual dedup sound). Four passes:
///
/// <list type="number">
///   <item><b>Prologue strip.</b> Each module opens with slangc's fixed boilerplate
///   (<c>#pragma pack_matrix(column_major)</c>, an NVAPI include guard, an fxc-warning guard).
///   The pragma is redundant with the pipeline's own matrix-layout handling — column_major IS
///   the fxc/DXC default the pipeline is built around — but a <c>row_major</c> pragma would
///   silently fight it, so that shape is rejected loudly (<c>SD0607</c>), never passed through.</item>
///   <item><b><c>#line</c> strip.</b> Diagnostics for the generated text are attributed to the
///   generated file (the ShaderToy F2 convention); stale <c>#line</c>s pointing into the
///   <c>.slang</c> would mis-locate errors in text the user cannot see.</item>
///   <item><b>Duplicate-declaration dedup</b> across modules, keyed on whitespace-normalized
///   block text.</item>
///   <item><b>Register handling + demangle</b> — see <see cref="Process"/>.</item>
/// </list>
/// </summary>
internal static class SlangHlslPostProcessor
{
    private static readonly Regex LineDirective = new(@"^\s*#line\b", RegexOptions.Compiled);
    private static readonly Regex PackMatrix = new(@"^\s*#pragma\s+pack_matrix\s*\(\s*(?<layout>\w+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex RegisterAnnotation = new(@"\s*:\s*register\s*\(\s*(?<kind>[a-z])(?<index>\d+)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex IdentifierToken = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    // .fx-level words an unmangled identifier must never become: they would change how the
    // FX9 layer parses the generated file.
    private static readonly HashSet<string> ReservedInFx = new(StringComparer.Ordinal)
    {
        "technique", "pass", "texture", "sampler", "sampler_state", "compile",
        "VertexShader", "PixelShader", "register", "packoffset",
    };

    /// <summary>
    /// Processes raw slangc stdout into one merged, demangled HLSL body.
    /// </summary>
    /// <param name="rawEmission">slangc's stdout: one or more concatenated HLSL modules.</param>
    /// <param name="userSourceHadRegisters">
    /// Whether the user's own <c>.slang</c> contained a <c>register</c> token. slangc always
    /// synthesizes <c>register(...)</c> annotations; when the user wrote none, every annotation
    /// is slangc's invention and is stripped so the pipeline's own allocation (the one every
    /// plain <c>.fx</c> gets, and the one issue #189 made faithful) applies. When the user DID
    /// write registers, all annotations are kept — and a post-merge conflict (two distinct
    /// declarations on one register) fails loudly (<c>SD0605</c>) rather than letting DXC or a
    /// downstream stage misbind silently.
    /// </param>
    /// <param name="sourceName">The logical source name, for diagnostics.</param>
    public static Result<SlangProcessedHlsl, ShaderError[]> Process(
        string rawEmission, bool userSourceHadRegisters, string sourceName)
    {
        var warnings = new List<ShaderError>();

        // Pass 1+2: strip prologues and #line directives, watching for the one pragma shape
        // that must never pass through.
        var kept = new List<string>();
        bool inNvapiGuard = false, inWarningGuard = false;
        foreach (string rawLine in rawEmission.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine;

            Match pack = PackMatrix.Match(line);
            if (pack.Success)
            {
                if (!pack.Groups["layout"].Value.Equals("column_major", StringComparison.Ordinal))
                {
                    return Result<SlangProcessedHlsl, ShaderError[]>.Fail(
                    [
                        new ShaderError(
                            File: sourceName, Line: 0, Column: 0, Code: "SD0607",
                            Message: $"slangc emitted '#pragma pack_matrix({pack.Groups["layout"].Value})', " +
                                     "a matrix layout this pipeline does not model on the Slang route " +
                                     "(column_major is the fxc/DXC default the .mgfx matrix handling is " +
                                     "built around; passing a different layout through would silently " +
                                     "transpose every matrix parameter)."),
                    ]);
                }
                continue;   // redundant with the pipeline default — dropped
            }

            if (line.StartsWith("#ifdef SLANG_HLSL_ENABLE_NVAPI", StringComparison.Ordinal))
            {
                inNvapiGuard = true;
                continue;
            }
            if (line.StartsWith("#ifndef __DXC_VERSION_MAJOR", StringComparison.Ordinal))
            {
                inWarningGuard = true;
                continue;
            }
            if (inNvapiGuard || inWarningGuard)
            {
                if (line.StartsWith("#endif", StringComparison.Ordinal))
                {
                    inNvapiGuard = false;
                    inWarningGuard = false;
                }
                continue;
            }

            if (LineDirective.IsMatch(line))
                continue;

            kept.Add(line);
        }

        // Pass 3: split into top-level blocks and drop byte-duplicate declarations (the shared
        // globals each slang module re-emits).
        List<string> blocks = SplitTopLevelBlocks(string.Join('\n', kept));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (string block in blocks)
        {
            string key = Regex.Replace(block, @"\s+", " ").Trim();
            if (key.Length == 0 || seen.Add(key))
                unique.Add(block);
        }

        string body = string.Join("\n\n", unique.Where(b => b.Trim().Length > 0));

        // Pass 4a: registers.
        if (!userSourceHadRegisters)
        {
            body = RegisterAnnotation.Replace(body, "");
        }
        else
        {
            var byRegister = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in RegisterAnnotation.Matches(body))
            {
                string reg = m.Groups["kind"].Value + m.Groups["index"].Value;
                byRegister[reg] = byRegister.TryGetValue(reg, out int n) ? n + 1 : 1;
            }

            string[] conflicts = byRegister.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToArray();
            if (conflicts.Length > 0)
            {
                return Result<SlangProcessedHlsl, ShaderError[]>.Fail(
                [
                    new ShaderError(
                        File: sourceName, Line: 0, Column: 0, Code: "SD0605",
                        Message: "after merging slangc's per-entry emissions, more than one declaration " +
                                 $"landed on register(s) {string.Join(", ", conflicts)}. This happens when " +
                                 "explicit register() annotations in the .slang combine with slangc's " +
                                 "per-stage allocation. Give every resource an explicit, distinct register, " +
                                 "or remove the explicit registers entirely."),
                ]);
            }
        }

        // Pass 4b: demangle. slangc suffixes every user symbol with a deterministic '_0'
        // ('Desaturation' -> 'Desaturation_0'); left alone, those become the EFFECT PARAMETER
        // NAMES the consumer's Parameters["..."] lookups see. Renaming X_0 -> X is applied only
        // when it is provably safe: the bare name occurs nowhere in the document (so nothing can
        // be captured) and is not an .fx-level keyword. An unsafe rename is SKIPPED with a
        // warning — the worst case is a mangled parameter name, never a miscompile.
        var tokens = new HashSet<string>(
            IdentifierToken.Matches(body).Select(m => m.Value), StringComparer.Ordinal);

        var renames = new List<(string From, string To)>();
        foreach (string token in tokens.Where(t => t.EndsWith("_0", StringComparison.Ordinal)))
        {
            string bare = token[..^2];
            if (bare.Length == 0 || char.IsDigit(bare[0]))
                continue;

            if (tokens.Contains(bare) || ReservedInFx.Contains(bare))
            {
                warnings.Add(new ShaderError(
                    File: sourceName, Line: 0, Column: 0, Code: "SD0606",
                    Message: $"the Slang symbol behind '{token}' could not be renamed back to '{bare}' " +
                             $"(the name is already in use or reserved). If '{bare}' is an effect " +
                             $"parameter, address it as Parameters[\"{token}\"].",
                    Severity: ShaderErrorSeverity.Warning));
                continue;
            }

            renames.Add((token, bare));
        }

        // Longest first, so 'Foo_0_0' can never be half-eaten by the 'Foo_0' rename.
        foreach ((string from, string to) in renames.OrderByDescending(r => r.From.Length))
            body = Regex.Replace(body, $@"\b{Regex.Escape(from)}\b", to.Replace("$", "$$"));

        // Pass 5: flatten slang's parameter-group wrapping. slangc emits every cbuffer as ONE
        // struct-typed member ('cbuffer Params { SLANG_ParameterGroup_Params Params; }', uses as
        // 'Params.Desaturation'). That shape is measured-unrepresentable on the GL MojoShader
        // lowering (SD0210 rejects struct uniform-block members), and on every target it would
        // surface the STRUCT as the effect parameter instead of the members the user declared.
        // Flattening restores exactly what the user wrote: 'cbuffer Params { float4x4 ...; }'
        // with plain member access. Applied only to slang's own fixed shape (the
        // SLANG_ParameterGroup_ prefix + single-member body); anything else passes through and
        // fails loudly downstream if unrepresentable.
        body = FlattenParameterGroups(body);

        return Result<SlangProcessedHlsl, ShaderError[]>.Ok(new SlangProcessedHlsl(body, warnings));
    }

    private static readonly Regex CbufferBlock = new(
        @"cbuffer\s+(?<name>\w+)\s*(?<reg>:\s*register\s*\([^)]*\)\s*)?\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex SingleStructMember = new(
        @"^\s*(?<type>SLANG_ParameterGroup_\w+)\s+(?<var>\w+)\s*;\s*$", RegexOptions.Compiled);

    /// <summary>See the pass-5 comment in <see cref="Process"/>.</summary>
    internal static string FlattenParameterGroups(string body)
    {
        foreach (Match cbuffer in CbufferBlock.Matches(body).Cast<Match>().ToArray())
        {
            Match member = SingleStructMember.Match(cbuffer.Groups["body"].Value);
            if (!member.Success)
                continue;

            string structType = member.Groups["type"].Value;
            string memberVar = member.Groups["var"].Value;

            // The struct definition slang emitted for the group.
            Match structDef = Regex.Match(body,
                $@"struct\s+{Regex.Escape(structType)}\s*\{{(?<fields>[^{{}}]*)\}}\s*;?");
            if (!structDef.Success)
                continue;

            // Only flatten when the struct's SOLE use is this cbuffer member — if it appears
            // anywhere else (a function parameter, a second variable), inlining would change
            // meaning, so leave it for the downstream stages to accept or loudly reject.
            int uses = Regex.Matches(body, $@"\b{Regex.Escape(structType)}\b").Count;
            if (uses != 2)   // the definition + the one member
                continue;

            string flattened =
                $"cbuffer {cbuffer.Groups["name"].Value} " +
                (cbuffer.Groups["reg"].Success ? cbuffer.Groups["reg"].Value : "") +
                "{" + structDef.Groups["fields"].Value + "}";

            body = body.Replace(cbuffer.Value, flattened);
            body = body.Replace(structDef.Value, "");
            body = Regex.Replace(body, $@"\b{Regex.Escape(memberVar)}\s*\.\s*", "");
        }

        return body;
    }

    /// <summary>
    /// Splits HLSL text into top-level chunks: a chunk ends where brace depth returns to zero at
    /// a ';' or '}'. Comments are honored so a brace in a comment cannot skew the depth.
    /// </summary>
    private static List<string> SplitTopLevelBlocks(string text)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool inLineComment = false, inBlockComment = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    current.Append('/');
                    i++;
                    inBlockComment = false;
                }
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/') { inLineComment = true; continue; }
                if (text[i + 1] == '*') { inBlockComment = true; continue; }
            }

            switch (c)
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    // A block closes at depth 0 either directly ('}') or with a trailing ';'
                    // (struct/cbuffer). Consume the optional ';' into the same chunk.
                    if (depth == 0)
                    {
                        int j = i + 1;
                        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                        if (j < text.Length && text[j] == ';')
                        {
                            current.Append(text.AsSpan(i + 1, j - i));
                            i = j;
                        }
                        blocks.Add(current.ToString());
                        current.Clear();
                    }
                    break;
                case ';':
                    if (depth == 0)
                    {
                        blocks.Add(current.ToString());
                        current.Clear();
                    }
                    break;
            }
        }

        if (current.ToString().Trim().Length > 0)
            blocks.Add(current.ToString());

        return blocks;
    }
}
