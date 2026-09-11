#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// PURE unit tests (no disk, no process, no native) for <see cref="Vkd3dSourceLocator"/>,
/// the issue #202 fix. The probe is a fake vkd3d that reproduces the three measured
/// drifts of the real one — skipped <c>#if</c> arms and block-comment interiors vanish
/// from its line count, every <c>atan2</c> call site inflates it by 20, and columns count
/// in its re-spaced token stream — plus the parse-abort behaviour the bisection relies on
/// (a syntax error, sentinel or the author's, ends the parse; a codegen error reports only
/// after a complete parse). The real-vkd3d pin is <c>Vkd3dShaderCompilerTests</c>.
/// </summary>
public sealed class Vkd3dSourceLocatorTests
{
    private const string File = "user.fx";

    // -------------------------------------------------------------------------
    // The fake vkd3d
    // -------------------------------------------------------------------------

    private sealed class FakeVkd3d
    {
        /// <summary>A token that aborts the parse where it stands (the author's own syntax error).</summary>
        public string? SyntaxMarker { get; init; }

        /// <summary>A token whose error is reported only once the whole text parsed (a codegen-time error).</summary>
        public string? CodegenMarker { get; init; }

        /// <summary>
        /// How this fake's bison spells the sentinel's diagnostic: "invalid token" (bison 3.6+,
        /// the Windows and macOS natives) or <c>$undefined</c> (bison 3.5, the linux-x64 native).
        /// </summary>
        public string SentinelSpelling { get; init; } = Vkd3dSourceLocator.SentinelMessage;

        public int Calls { get; private set; }

        /// <summary>Set when a probe planted the sentinel directly under a backslash-continued line.</summary>
        public bool SawSentinelAfterContinuation { get; private set; }

        public ShaderError? Compile(string text)
        {
            Calls++;
            string[] lines = text.Split('\n');
            int reported = 0;
            int drift = 0;
            bool skipping = false;
            bool inComment = false;
            ShaderError? pending = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string trimmed = line.Trim();

                if (trimmed == Vkd3dSourceLocator.Sentinel && i > 0 && lines[i - 1].TrimEnd('\r').EndsWith('\\'))
                    SawSentinelAfterContinuation = true;

                if (inComment)
                {
                    if (line.Contains("*/", StringComparison.Ordinal)) inComment = false;
                    continue;                                   // collapsed: no line counted
                }
                if (trimmed.StartsWith("#if 0", StringComparison.Ordinal)) { skipping = true; reported++; continue; }
                if (trimmed.StartsWith("#endif", StringComparison.Ordinal)) { skipping = false; reported++; continue; }
                if (skipping) continue;                         // skipped arm: no line counted
                reported++;
                if (trimmed.StartsWith("/*", StringComparison.Ordinal) && !line.Contains("*/", StringComparison.Ordinal))
                {
                    inComment = true;
                    continue;
                }

                int column = 1;
                foreach ((int start, int length) in Vkd3dSourceLocator.Tokenize(line))
                {
                    string token = line.Substring(start, length);
                    if (token == Vkd3dSourceLocator.Sentinel)
                        return Error(reported + drift, column, "E5000", SentinelSpelling);
                    if (token == SyntaxMarker)
                        return Error(reported + drift, column, "E5000", "syntax error, unexpected " + token);
                    if (token == CodegenMarker)
                        pending ??= Error(reported + drift, column, "E5017", "Aborting due to not yet implemented feature: " + token);
                    if (token == "atan2")
                        drift += 20;                            // the template is lexed after the call is reduced
                    column += length + 1;
                }
            }
            return pending;
        }

        private static ShaderError Error(int line, int column, string code, string message) =>
            new(File, line, column, code, message, RawDiagnostics: $"{File}:{line}:{column}: {code}: {message}");
    }

    private static string Join(params string[] lines) => string.Join('\n', lines);

    private static ShaderError Relocate(FakeVkd3d fake, string source, string? original = null)
    {
        ShaderError primary = fake.Compile(source)
            ?? throw new InvalidOperationException("the fixture must fail to compile");
        return Vkd3dSourceLocator.Relocate(primary, source, original ?? source, File, fake.Compile);
    }

    // -------------------------------------------------------------------------
    // Line recovery
    // -------------------------------------------------------------------------

    [Fact]
    public void NoDrift_LineIsReportedAsIs()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a;", "float b;", "float c = BAD;", "float d;");

        ShaderError located = Relocate(fake, source);

        located.Line.ShouldBe(3);
    }

    [Fact]
    public void TemplateDriftBeforeTheLine_IsRemoved()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join(
            "float a = atan2(1, 2);",
            "float b = atan2(3, 4) + atan2(5, 6);",
            "float c;",
            "float d = BAD;",
            "float e;");

        ShaderError raw = fake.Compile(source)!;
        raw.Line.ShouldBe(64, customMessage: "the fake drifts 20 per atan2 call, like vkd3d 2.1");

        Relocate(fake, source).Line.ShouldBe(4);
    }

    [Fact]
    public void DriftFromACallEarlierOnTheSameLine_StillLandsOnThatLine()
    {
        // vkd3d bumps the counter when the call is reduced, so tokens AFTER it on the same
        // line already carry +20: the diagnostic reports line+20, and it is still this line.
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a;", "float b = atan2(1, 2) + BAD;", "float c;");

        fake.Compile(source)!.Line.ShouldBe(22);
        Relocate(fake, source).Line.ShouldBe(2);
    }

    [Fact]
    public void SkippedConditionalArm_LinesDroppedFromVkd3dsCount_AreRestored()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string> { "float a;", "#if 0" };
        for (int i = 0; i < 30; i++) lines.Add($"float skipped{i};");
        lines.AddRange(["#endif", "float b;", "float c = BAD;"]);
        string source = Join(lines.ToArray());

        fake.Compile(source)!.Line.ShouldBe(5, customMessage: "the 30 skipped lines vanish from vkd3d's numbering");
        Relocate(fake, source).Line.ShouldBe(35);
    }

    [Fact]
    public void BlockCommentInterior_CollapsedByVkd3d_IsRestored()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string> { "float a;", "/* a long comment" };
        for (int i = 0; i < 12; i++) lines.Add($"   still commented {i}");
        lines.AddRange(["*/", "float b = BAD;"]);
        string source = Join(lines.ToArray());

        fake.Compile(source)!.Line.ShouldBe(3);
        Relocate(fake, source).Line.ShouldBe(16);
    }

    [Fact]
    public void SyntaxErrorOriginal_ParseAbortsThere_ProbesBeyondItReturnTheOriginal()
    {
        var fake = new FakeVkd3d { SyntaxMarker = "BAD" };
        string source = Join(
            "float a = atan2(1, 2);",
            "float b;",
            "float c = BAD;",
            "float d = atan2(3, 4);",
            "float e;");

        fake.Compile(source)!.Line.ShouldBe(23);
        Relocate(fake, source).Line.ShouldBe(3);
    }

    [Fact]
    public void TheAuthorsOwnInvalidToken_IsLocatedOnItsOwnLine()
    {
        // The pathological case: the diagnostic IS sentinel-shaped (an '@' the author wrote,
        // reachable through an include), so probes at and after its line look exactly like the
        // original. The search settles one line early and the ambiguity check moves it back.
        var fake = new FakeVkd3d { SyntaxMarker = "@" };
        string source = Join("float a = atan2(1, 2);", "float b;", "@", "float c;");

        fake.Compile(source)!.Line.ShouldBe(23);
        Relocate(fake, source).Line.ShouldBe(3);
    }

    [Fact]
    public void DiagnosticOnTheFirstAndLastLine_Converge()
    {
        var first = new FakeVkd3d { CodegenMarker = "BAD" };
        Relocate(first, Join("float a = BAD;", "float b = atan2(1, 2);", "float c;")).Line.ShouldBe(1);

        var last = new FakeVkd3d { CodegenMarker = "BAD" };
        Relocate(last, Join("float a = atan2(1, 2);", "float b;", "float c = BAD;")).Line.ShouldBe(3);
    }

    [Fact]
    public void SentinelIsNeverPlantedUnderABackslashContinuation()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            lines.Add($"#define M{i}(x) \\");
            lines.Add("    (x)");
        }
        lines.Add("float c = BAD;");
        string source = Join(lines.ToArray());

        Relocate(fake, source).Line.ShouldBe(81);
        fake.SawSentinelAfterContinuation.ShouldBeFalse("a sentinel spliced into a macro body corrupts the text being measured");
    }

    // -------------------------------------------------------------------------
    // #line directives (the un-blanked text) -> the author's file and line
    // -------------------------------------------------------------------------

    [Fact]
    public void LineDirectives_MapThePreludeAndAFlattenedIncludeBackToTheirFiles()
    {
        // What the preprocessor hands the vkd3d backend: a 5-line prelude ending in
        // '#line 1 "user.fx"', then the user's file with an include flattened in place.
        string original = Join(
            "// ShadowDusk platform macros",
            "#define FNA 1",
            "#define HLSL 1",
            "#define SM3 1",
            "#line 1 \"user.fx\"",
            "float u1 = atan2(1, 2);",          // user.fx:1
            "#line 1 \"shared/inc.fxh\"",       // the #include line becomes the marker
            "float i1;",                        // inc.fxh:1
            "float i2 = BAD_INC;",              // inc.fxh:2
            "#line 3 \"user.fx\"",
            "float u3 = BAD_USER;",             // user.fx:3
            "float u4;");
        // The backend blanks the directives (line-preserving) before vkd3d sees them.
        string blanked = string.Join('\n', original.Split('\n').Select(l => l.StartsWith("#line", StringComparison.Ordinal) ? "" : l));

        var inc = new FakeVkd3d { CodegenMarker = "BAD_INC" };
        ShaderError located = Vkd3dSourceLocator.Relocate(inc.Compile(blanked)!, blanked, original, File, inc.Compile);
        located.File.ShouldBe("shared/inc.fxh");
        located.Line.ShouldBe(2);

        var user = new FakeVkd3d { CodegenMarker = "BAD_USER" };
        located = Vkd3dSourceLocator.Relocate(user.Compile(blanked)!, blanked, original, File, user.Compile);
        located.File.ShouldBe("user.fx");
        located.Line.ShouldBe(3);
    }

    [Theory]
    [InlineData(1, "user.fx", 1)]
    [InlineData(3, "user.fx", 3)]
    [InlineData(5, "inc.fxh", 1)]
    [InlineData(8, "user.fx", 11)]
    [InlineData(7, "user.fx", 10)]
    public void ResolveLineDirectives_ReplaysTheDirectivesAboveThePhysicalLine(int physical, string file, int line)
    {
        string text = Join("a", "b", "c", "#line 1 \"inc.fxh\"", "d", "#line 10 \"user.fx\"", "e", "f");
        // physical: 1=a 2=b 3=c 4=#line 5=d(inc:1) 6=#line 7=e(user:10) 8=f(user:11)
        (string f, int l) = Vkd3dSourceLocator.ResolveLineDirectives(text, physical, "user.fx");
        f.ShouldBe(file);
        l.ShouldBe(line);
    }

    // -------------------------------------------------------------------------
    // Columns: vkd3d's re-spaced token column -> the author's column
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("int __probe=;", 15, 13)]                       // measured: vkd3d says 15 for the ';' at 13
    [InlineData("    int   __probe   =   ;", 15, 25)]           // indentation and padding dropped
    [InlineData("    if (isGlyph ? glyphFade <= 0.0 : d) {", 26, 29)] // the reporter's line: '<=' is token 6
    [InlineData("float u = atan2(q.x, q.y);", 11, 11)]          // 'atan2' at 11 either way
    [InlineData("float u = atan2(q.x, q.y);", 17, 16)]          // re-spaced 'atan2 ( q': the '(' is at 17, source 16
    [InlineData("float u = atan2(q.x, q.y);", 19, 17)]          // and 'q' at 19, source 17
    [InlineData("x = 1e-6 + .5 + 0.5f;", 12, 12)]               // pp-numbers stay whole
    public void RemapColumn_FindsTheTokenVkd3dCounted(string sourceLine, int vkd3dColumn, int expected)
    {
        Vkd3dSourceLocator.RemapColumn(sourceLine, vkd3dColumn).ShouldBe(expected);
    }

    [Theory]
    [InlineData("SC(x, y);", 5)]      // a macro-expanded line: no token starts at 5 -> unchanged
    [InlineData("float a;", 0)]       // column 0 is "unknown" and stays so
    public void RemapColumn_LeavesAnUnalignedColumnAlone(string sourceLine, int vkd3dColumn)
    {
        Vkd3dSourceLocator.RemapColumn(sourceLine, vkd3dColumn).ShouldBe(vkd3dColumn);
    }

    [Fact]
    public void Tokenize_SplitsLikeVkd3dsPreprocessor()
    {
        const string line = "if (q.x >= 1e-4 && r <<= 2) s += \"a b\"; // tail /* not */";
        string[] tokens = Vkd3dSourceLocator.Tokenize(line).Select(t => line.Substring(t.Start, t.Length)).ToArray();

        tokens.ShouldBe(["if", "(", "q", ".", "x", ">=", "1e-4", "&&", "r", "<<=", "2", ")", "s", "+=", "\"a b\"", ";"]);
    }

    [Fact]
    public void Tokenize_DropsABlockCommentAndKeepsTheRest()
    {
        const string line = "float /* c */ x = .5;";
        string[] tokens = Vkd3dSourceLocator.Tokenize(line).Select(t => line.Substring(t.Start, t.Length)).ToArray();

        tokens.ShouldBe(["float", "x", "=", ".5", ";"]);
    }

    [Fact]
    public void ColumnOfTheRelocatedDiagnostic_IsTheAuthorsColumn()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a = atan2(1, 2);", "    float   b   =   BAD;");

        ShaderError raw = fake.Compile(source)!;
        raw.Column.ShouldBe(11, customMessage: "re-spaced: 'float b = BAD' puts BAD at 11");

        Relocate(fake, source).Column.ShouldBe(21);
    }

    // -------------------------------------------------------------------------
    // What must NOT change, and what must not happen
    // -------------------------------------------------------------------------

    [Fact]
    public void MessageCodeSeverityAndRawText_StayVerbatim()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a = atan2(1, 2);", "float b = BAD;");
        ShaderError raw = fake.Compile(source)!;

        ShaderError located = Vkd3dSourceLocator.Relocate(raw, source, source, File, fake.Compile);

        located.Message.ShouldBe(raw.Message);
        located.Code.ShouldBe(raw.Code);
        located.Severity.ShouldBe(raw.Severity);
        located.RawDiagnostics.ShouldBe(raw.RawDiagnostics, customMessage: "the compiler's own text is never rewritten, even where it shows its own line number");
        located.File.ShouldBe(File);
    }

    [Fact]
    public void UnlocatedAndForeignFileDiagnostics_PassThroughWithoutProbing()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a = atan2(1, 2);", "float b = BAD;");
        var unlocated = new ShaderError(File, 0, 0, "SD0212", "no diagnostics");
        var foreign = new ShaderError("other.fx", 40, 3, "E5000", "elsewhere");

        IReadOnlyList<ShaderError> result = Vkd3dSourceLocator.Relocate([unlocated, foreign], source, source, File, fake.Compile);

        result.ShouldBe([unlocated, foreign]);
        fake.Calls.ShouldBe(0);
    }

    [Fact]
    public void SentinelSpelledByAnOlderBison_IsRecognizedAndConverges()
    {
        // The linux-x64 native's parser was generated by bison 3.5 (Ubuntu 20.04, for the
        // glibc 2.31 baseline), which names the undefined token '$undefined'; 3.6+ says
        // "invalid token". A locator that recognises only one spelling never brackets on the
        // other host: every probe reads as "nothing fired", the budget burns, and the raw
        // coordinates come back (what CI's ubuntu lane showed).
        var fake = new FakeVkd3d { CodegenMarker = "BAD", SentinelSpelling = Vkd3dSourceLocator.SentinelMessageLegacyBison };
        string source = Join(
            "float a = atan2(1, 2);",     // 1  +20
            "float b = atan2(3, 4);",     // 2  +20
            "float c = BAD;",             // 3  vkd3d says 43
            "float d;");                  // 4

        ShaderError located = Relocate(fake, source);

        located.Line.ShouldBe(3);
        located.Message.ShouldBe("Aborting due to not yet implemented feature: BAD");
        fake.Calls.ShouldBeLessThan(Vkd3dSourceLocator.MaxProbes, customMessage: "a bisection, not a burnt budget");
    }

    [Fact]
    public void ProbeBudgetExhausted_LeavesTheDiagnosticAsVkd3dReportedIt()
    {
        // A probe that never fires (every sentinel swallowed) can never converge.
        string source = Join(Enumerable.Range(0, 2000).Select(i => $"float f{i};").ToArray());
        var raw = new ShaderError(File, 1234, 5, "E5017", "some codegen failure");
        int calls = 0;
        ShaderError? Silent(string _) { calls++; return null; }

        ShaderError located = Vkd3dSourceLocator.Relocate(raw, source, source, File, Silent);

        located.ShouldBe(raw);
        calls.ShouldBeLessThanOrEqualTo(Vkd3dSourceLocator.MaxProbes);
    }

    [Fact]
    public void ALargeEffect_ConvergesInLogarithmicProbes()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string>();
        for (int i = 0; i < 3000; i++)
            lines.Add(i % 7 == 0 ? $"float f{i} = atan2({i}, 1);" : $"float f{i};");
        lines[2500] = "float bad = BAD;";
        string source = Join(lines.ToArray());

        ShaderError located = Relocate(fake, source);

        located.Line.ShouldBe(2501);
        (fake.Calls - 1).ShouldBeLessThanOrEqualTo(24, customMessage: "a bisection over 3000 lines, not a scan");
    }

    [Fact]
    public void SeveralDiagnostics_ShareOneProbeSession()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a = atan2(1, 2);", "float b;", "float c = BAD;", "float d;", "float e = BAD;");
        ShaderError raw = fake.Compile(source)!;                       // the first BAD, line 23
        ShaderError second = raw with { Line = 25, Column = 11 };      // as vkd3d would report the second

        IReadOnlyList<ShaderError> located = Vkd3dSourceLocator.Relocate([raw, second], source, source, File, fake.Compile);

        located[0].Line.ShouldBe(3);
        located[1].Line.ShouldBe(5);
    }
}
