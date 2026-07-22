#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.GLSL;

/// <summary>
/// Compile-time portability lint over the emitted MonoGame-GL GLSL. Flags constructs
/// that compile fine here — and load on lenient desktop drivers — but are known to
/// fail or silently misbehave at RUNTIME on narrower GL stacks (WebGL1 / KNI Reach,
/// ANGLE Direct3D11 in Windows browsers, strict Mesa), where the consumer's only
/// signal is MonoGame's lazy draw-time <c>"Shader Compilation Failed"</c> /
/// <c>"Unable to link effect program"</c> exception with the real driver log hidden
/// behind <c>Debug.WriteLine</c> — i.e. "an error on the SpriteBatch call".
///
/// <para>Every finding is a <see cref="ShaderErrorSeverity.Warning"/>, never an
/// error: the artifact is valid and renders on the stacks that accept it — the lint
/// exists so the narrower stacks stop being a silent field failure. Findings surface
/// through <c>CompiledShader.Warnings</c>. Codes <c>SD0400</c>–<c>SD0402</c>
/// (registered in <c>docs/error-codes.md</c>).</para>
/// </summary>
public static class GlslPortabilityAnalyzer
{
    // Interpolants SpriteBatch's built-in SpriteEffect vertex shader writes (verified
    // against the mgfxc golden tests/fixtures/golden/OpenGL/SpriteEffect.mgfx):
    // COLOR0 -> vFrontColor and TEXCOORD0 -> vTexCoord0, nothing else. A PS-only pass
    // reading any other varying cannot link against it on a strict GL runtime.
    private static readonly string[] SpriteEffectProvidedVaryings = { "vFrontColor", "vTexCoord0" };

    private static readonly Regex VaryingDeclaration = new(
        @"^\s*varying\s+vec4\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex GradientCall = new(
        @"\b(dFdx|dFdy|fwidth)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ForOrWhileHeader = new(
        @"\b(for|while)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex DoBlock = new(
        @"\bdo\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex DivergentExit = new(
        @"\b(break|discard)\s*;",
        RegexOptions.Compiled);

    /// <summary>
    /// Analyzes one rewritten shader stage and returns the portability findings, all
    /// warning-severity. <paramref name="passHasVertexShader"/> is the pass-level
    /// fact that decides whether the SpriteBatch interpolant check applies (a pass
    /// with its own VS defines its own varying contract and is exempt).
    /// </summary>
    /// <param name="glsl">The final rewritten GLSL for the stage (the bytes that go into the effect).</param>
    /// <param name="stage">Which stage <paramref name="glsl"/> is.</param>
    /// <param name="passHasVertexShader">Whether the pass this shader belongs to also has a vertex shader.</param>
    /// <param name="sourceFileName">The consumer-facing source file name used on the findings.</param>
    /// <param name="entryPoint">The HLSL entry-point name, for the finding text.</param>
    public static IReadOnlyList<ShaderError> Analyze(
        string glsl,
        ShaderStage stage,
        bool passHasVertexShader,
        string sourceFileName,
        string? entryPoint)
    {
        var findings = new List<ShaderError>();
        string entry = string.IsNullOrEmpty(entryPoint)
            ? (stage == ShaderStage.Vertex ? "the vertex shader" : "the pixel shader")
            : $"{(stage == ShaderStage.Vertex ? "vertex shader" : "pixel shader")} '{entryPoint}'";

        if (stage == ShaderStage.Pixel)
        {
            CheckGradientOpsInsideDivergentLoops(glsl, entry, sourceFileName, findings);
            if (!passHasVertexShader)
                CheckSpriteBatchInterpolants(glsl, entry, sourceFileName, findings);
        }

        CheckEssl100LoopShapes(glsl, entry, sourceFileName, findings);

        return findings;
    }

    /// <summary>
    /// SD0400 (issue #141): a derivative op lexically inside a loop whose body has a
    /// divergent exit. ANGLE's Direct3D11 backend (WebGL in every Windows browser)
    /// silently evaluates every gradient op in such a loop to 0.0 — no compile, link,
    /// or runtime error. fxc does not stay silent on the same HLSL shape: it warns
    /// X3553 and force-unrolls (or rejects X4532 on level_9 profiles). ShadowDusk
    /// cannot force-unroll a runtime-bounded loop, so it warns instead.
    /// </summary>
    private static void CheckGradientOpsInsideDivergentLoops(
        string glsl, string entry, string sourceFileName, List<ShaderError> findings)
    {
        foreach ((int bodyStart, int bodyEnd, string header) in CollectLoopBodies(glsl))
        {
            string bodyText = glsl.Substring(bodyStart, bodyEnd - bodyStart + 1);
            if (!DivergentExit.IsMatch(bodyText))
                continue;

            foreach (Match g in GradientCall.Matches(glsl))
            {
                if (g.Index <= bodyStart || g.Index >= bodyEnd)
                    continue;

                findings.Add(new ShaderError(
                    File: sourceFileName,
                    Line: 0,
                    Column: 0,
                    Code: "SD0400",
                    Message:
                        $"In {entry}, {g.Groups[1].Value}() is inside a loop with a conditional " +
                        $"break/discard (emitted GL loop: \"{header}\"). On ANGLE Direct3D11 — " +
                        "WebGL in every Windows browser — every derivative inside such a loop " +
                        "silently evaluates to 0.0, with no compile or link error (fxc warns " +
                        "X3553 and force-unrolls the same shape; a runtime-bounded loop cannot " +
                        "be unrolled here). If the effect targets browsers, compute the " +
                        "derivative before the loop and reuse the value inside it.",
                    Severity: ShaderErrorSeverity.Warning));
            }
        }
    }

    /// <summary>
    /// SD0401: a pass with no vertex shader whose pixel shader reads interpolants the
    /// built-in SpriteEffect VS never writes. MonoGame/KNI's GL runtime links the
    /// program from the currently-bound VS+PS pair lazily at the FIRST DRAW — with
    /// SpriteBatch that VS is SpriteEffect's, which writes only vFrontColor (COLOR0)
    /// and vTexCoord0 (TEXCOORD0). A varying statically read but never written is a
    /// hard link error on strict GL stacks (Mesa, WebGL/ANGLE; GLSL ES 1.00 §4.3.5),
    /// surfacing as the engine's generic draw-time exception; lenient desktop drivers
    /// instead feed garbage values.
    /// </summary>
    private static void CheckSpriteBatchInterpolants(
        string glsl, string entry, string sourceFileName, List<ShaderError> findings)
    {
        var unprovided = new List<string>();
        foreach (Match m in VaryingDeclaration.Matches(glsl))
        {
            string name = m.Groups["name"].Value;
            if (Array.IndexOf(SpriteEffectProvidedVaryings, name) < 0)
                unprovided.Add($"{name} ({VaryingNameToSemantic(name)})");
        }

        if (unprovided.Count == 0)
            return;

        findings.Add(new ShaderError(
            File: sourceFileName,
            Line: 0,
            Column: 0,
            Code: "SD0401",
            Message:
                $"The pass has no vertex shader, and {entry} reads " +
                $"{string.Join(", ", unprovided)} — interpolants SpriteBatch's built-in " +
                "vertex shader never writes (it provides only COLOR0 and TEXCOORD0). Drawn " +
                "with SpriteBatch on OpenGL, the program fails to link on strict drivers " +
                "(Mesa/Linux, WebGL, ANGLE) at the FIRST draw call, surfacing as MonoGame's " +
                "generic \"Shader Compilation Failed\"/\"Unable to link effect program\" " +
                "exception. Read only COLOR0/TEXCOORD0 in a SpriteBatch pixel shader, or " +
                "give the pass its own vertex shader that writes these interpolants.",
            Severity: ShaderErrorSeverity.Warning));
    }

    /// <summary>
    /// SD0402 (issue #138): a loop shape outside GLSL ES 1.00 Appendix A. WebGL1 /
    /// KNI Reach enforce Appendix A at Effect-load time (ANGLE's ValidateLimitations
    /// pass for #version-100 shaders), so the effect may fail to load there — desktop
    /// GL, WebGL2, and KNI HiDef are unaffected. mgfxc's SM3 pipeline force-unrolled
    /// pixel loops, so reference output never contains these shapes.
    /// </summary>
    private static void CheckEssl100LoopShapes(
        string glsl, string entry, string sourceFileName, List<ShaderError> findings)
    {
        void Warn(string shape) => findings.Add(new ShaderError(
            File: sourceFileName,
            Line: 0,
            Column: 0,
            Code: "SD0402",
            Message:
                $"In {entry}, the emitted GL contains {shape}, a loop shape outside GLSL ES " +
                "1.00 Appendix A. WebGL1 / KNI Reach enforce Appendix A when the effect " +
                "loads, so the effect may fail to load there with the engine's generic " +
                "\"Shader Compilation Failed\" exception (desktop GL, WebGL2, and KNI HiDef " +
                "are unaffected). If the effect targets WebGL1/Reach, rewrite the loop with " +
                "a constant bound, the index advanced in the for-header, and no other " +
                "writes to the index.",
            Severity: ShaderErrorSeverity.Warning));

        foreach (Match m in ForOrWhileHeader.Matches(glsl))
        {
            int open = glsl.IndexOf('(', m.Index);
            int close = FindMatchingParen(glsl, open);
            if (close < 0)
                continue;

            if (m.Groups[1].Value == "while")
            {
                // A do-while's trailing `} while (...)` belongs to the do-block finding.
                if (IsDoWhileTail(glsl, m.Index))
                    continue;
                Warn($"a while loop (\"{Snippet(glsl, m.Index, close)}\")");
                continue;
            }

            // Split the for-header at TOP-LEVEL semicolons (paren-depth 0) so a
            // function call in the condition cannot confuse the classification.
            (string init, string increment, bool wellFormed) = SplitForHeader(glsl, open, close);
            if (!wellFormed)
                continue;

            if (init.Trim().Length == 0)
                Warn($"a header-less for loop (\"{Snippet(glsl, m.Index, close)}\")");
            else if (increment.Trim().Length == 0)
                Warn($"a for loop with an empty increment — the index advances in the body " +
                     $"(\"{Snippet(glsl, m.Index, close)}\")");
        }

        foreach (Match m in DoBlock.Matches(glsl))
        {
            // Rule 9b lowers every one-shot do{...}while(false) before this analyzer
            // runs, so any surviving do-block is a genuine multi-iteration loop.
            Warn("a do-while loop");
        }
    }

    /// <summary>Collects every loop body brace span: for/while header parens then braces, and do-blocks.</summary>
    private static List<(int Start, int End, string Header)> CollectLoopBodies(string glsl)
    {
        var bodies = new List<(int, int, string)>();

        foreach (Match m in ForOrWhileHeader.Matches(glsl))
        {
            int open = glsl.IndexOf('(', m.Index);
            int close = FindMatchingParen(glsl, open);
            if (close < 0)
                continue;
            int b = close + 1;
            while (b < glsl.Length && char.IsWhiteSpace(glsl[b]))
                b++;
            if (b >= glsl.Length || glsl[b] != '{')
                continue;
            int end = FindMatchingBrace(glsl, b);
            if (end > 0)
                bodies.Add((b, end, Snippet(glsl, m.Index, close)));
        }

        foreach (Match m in DoBlock.Matches(glsl))
        {
            int b = glsl.IndexOf('{', m.Index);
            int end = FindMatchingBrace(glsl, b);
            if (end > 0)
                bodies.Add((b, end, "do{...}while"));
        }

        return bodies;
    }

    private static (string Init, string Increment, bool WellFormed) SplitForHeader(
        string glsl, int open, int close)
    {
        int depth = 0;
        int firstSemi = -1, secondSemi = -1;
        for (int i = open + 1; i < close; i++)
        {
            char c = glsl[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ';' && depth == 0)
            {
                if (firstSemi < 0) firstSemi = i;
                else if (secondSemi < 0) secondSemi = i;
                else return (string.Empty, string.Empty, false); // not a plain for-header
            }
        }
        if (firstSemi < 0 || secondSemi < 0)
            return (string.Empty, string.Empty, false);

        string init      = glsl[(open + 1)..firstSemi];
        string increment = glsl[(secondSemi + 1)..close];
        return (init, increment, true);
    }

    private static bool IsDoWhileTail(string glsl, int whileIndex)
    {
        for (int i = whileIndex - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(glsl[i]))
                continue;
            return glsl[i] == '}';
        }
        return false;
    }

    private static string Snippet(string glsl, int start, int closeParen)
    {
        int len = Math.Min(64, closeParen + 1 - start);
        return glsl.Substring(start, len).Replace('\n', ' ').Replace('\r', ' ');
    }

    private static int FindMatchingParen(string s, int open)
    {
        if (open < 0 || open >= s.Length || s[open] != '(')
            return -1;
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static int FindMatchingBrace(string s, int open)
    {
        if (open < 0 || open >= s.Length || s[open] != '{')
            return -1;
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Maps a MojoShader-dialect varying name back to its HLSL semantic for finding text.</summary>
    private static string VaryingNameToSemantic(string varyingName) => varyingName switch
    {
        "vFrontColor" => "COLOR0",
        "vBackColor"  => "COLOR1",
        _ when varyingName.StartsWith("vTexCoord", StringComparison.Ordinal)
            => "TEXCOORD" + varyingName["vTexCoord".Length..],
        _ when varyingName.StartsWith("var_", StringComparison.Ordinal)
            => varyingName["var_".Length..],
        _ => varyingName,
    };
}
