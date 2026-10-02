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

        /// <summary>
        /// Physical lines where a statement marker is not legal: the fake answers the marker
        /// there with the syntax error vkd3d gives (the parse stops, nothing after it answers).
        /// </summary>
        public HashSet<int> RejectMarkerOnLines { get; init; } = [];

        public int Calls { get; private set; }

        /// <summary>Compiles that carried statement markers (one parse, many measurements).</summary>
        public int MarkerCompiles { get; private set; }

        /// <summary>Compiles whose text did not end with the locator's terminator line.</summary>
        public int CompilesWithoutTerminator { get; private set; }

        /// <summary>Set when a probe planted the sentinel directly under a backslash-continued line.</summary>
        public bool SawSentinelAfterContinuation { get; private set; }

        public ShaderError? Compile(string text)
        {
            Calls++;
            if (text.Contains(MarkerPrefix, StringComparison.Ordinal))
                MarkerCompiles++;
            if (!text.EndsWith("\n" + Vkd3dSourceLocator.Terminator, StringComparison.Ordinal))
                CompilesWithoutTerminator++;

            string[] lines = text.Split('\n');
            int reported = 0;
            int drift = 0;
            int parenDepth = 0;
            bool skipping = false;
            bool inComment = false;
            ShaderError? pending = null;
            var warnings = new List<string>();

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
                        return Error(reported + drift, column, "E5000", SentinelSpelling, warnings);
                    if (token == SyntaxMarker)
                        return Error(reported + drift, column, "E5000", "syntax error, unexpected " + token, warnings);
                    if (token == CodegenMarker)
                        pending ??= Error(reported + drift, column, "E5017", "Aborting due to not yet implemented feature: " + token);
                    if (token.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                    {
                        // The statement marker: a non-fatal warning that quotes its name, or,
                        // where it is not legal, a syntax error at its '['.
                        if (RejectMarkerOnLines.Contains(i + 1))
                            return Error(reported + drift, 1, "E5000", "syntax error, unexpected '['", warnings);
                        warnings.Add($"{File}:{reported + drift}:1: W5302: Unrecognized attribute '{token}'.");
                    }
                    if (token == "(")
                        parenDepth++;
                    if (token == ")" && --parenDepth < 0)       // the terminator: the parse ends here, whatever came before
                        return Error(reported + drift, column, "E5000", "syntax error, unexpected ')'", warnings);
                    if (token == "atan2")
                        drift += 20;                            // the template is lexed after the call is reduced
                    column += length + 1;
                }
            }
            return pending;
        }

        // Like vkd3d's message buffer: everything said before the error, then the error.
        private static ShaderError Error(int line, int column, string code, string message, List<string>? before = null) =>
            new(File, line, column, code, message,
                RawDiagnostics: string.Join('\n', (before ?? []).Append($"{File}:{line}:{column}: {code}: {message}")));
    }

    private const string MarkerPrefix = "__shadowdusk_line_";

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
        raw.RawDiagnostics.ShouldBe("user.fx:22:11: E5017: Aborting due to not yet implemented feature: BAD");

        ShaderError located = Vkd3dSourceLocator.Relocate(raw, source, source, File, fake.Compile);

        located.Message.ShouldBe(raw.Message);
        located.Code.ShouldBe(raw.Code);
        located.Severity.ShouldBe(raw.Severity);
        located.RawDiagnostics.ShouldBe(
            "user.fx:2:11: E5017: Aborting due to not yet implemented feature: BAD",
            customMessage: "only the location prefix moves; the compiler's code and text stay verbatim");
        located.File.ShouldBe(File);
    }

    // Issue #202, second half: the delivery surfaces print the raw blob under the relocated
    // summary whenever vkd3d said more than one line, so a raw blob left in vkd3d's own
    // coordinates still showed line numbers that disagreed with the summary (and on the
    // reporter's file ran past its end).
    [Fact]
    public void RawBlob_EveryLocatedLineMovesWithTheSummary_TheRestStaysVerbatim()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join(
            "float a = atan2(1, 2);",     // 1  +20
            "float b;",                   // 2
            "float c = BAD;",             // 3  vkd3d says 23
            "float d;",                   // 4
            "float e = BAD;");            // 5  vkd3d says 25
        ShaderError first = fake.Compile(source)!;
        first.Line.ShouldBe(23);
        ShaderError primary = first with
        {
            RawDiagnostics = string.Join('\n',
                "user.fx:23:11: E5017: Aborting due to not yet implemented feature: BAD",
                "user.fx:25:11: E5017: Aborting due to not yet implemented feature: BAD",
                "other.fxh:40:3: E5017: a line in another file",
                "vkd3d: some unlocated note"),
        };

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.Line.ShouldBe(3);
        located.RawDiagnostics.ShouldBe(string.Join('\n',
            "user.fx:3:11: E5017: Aborting due to not yet implemented feature: BAD",
            "user.fx:5:11: E5017: Aborting due to not yet implemented feature: BAD",
            "other.fxh:40:3: E5017: a line in another file",
            "vkd3d: some unlocated note"));
    }

    [Fact]
    public void RawBlob_ManyLinesInAConstantDriftStretch_CostFewProbes()
    {
        // 2000 codegen errors after one atan2: the summary's own bisection brackets the
        // stretch, and every later line is inferred from the constant drift, not re-bisected.
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string> { "float a = atan2(1, 2);" };
        for (int i = 0; i < 2000; i++)
            lines.Add("float f = BAD;");
        string source = Join(lines.ToArray());
        ShaderError first = fake.Compile(source)!;
        first.Line.ShouldBe(22);
        ShaderError primary = first with
        {
            RawDiagnostics = string.Join('\n',
                Enumerable.Range(0, 2000).Select(i => $"user.fx:{22 + i}:11: E5017: Aborting due to not yet implemented feature: BAD")),
        };
        int before = fake.Calls;

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.Line.ShouldBe(2);
        string[] rawLines = located.RawDiagnostics!.Split('\n');
        for (int i = 0; i < rawLines.Length; i++)
            rawLines[i].ShouldStartWith($"user.fx:{2 + i}:11: ", Case.Sensitive);
        (fake.Calls - before).ShouldBeLessThanOrEqualTo(
            Vkd3dSourceLocator.MaxProbes, customMessage: "2000 lines must not each pay for a bisection");
    }

    // -------------------------------------------------------------------------
    // The raw blob's cost: one marker compile measures every statement start
    // -------------------------------------------------------------------------

    /// <summary>A function body whose drift changes on almost every line, with five codegen errors.</summary>
    private static readonly string[] MarkedFunction =
    [
        "float2 dir;",                              // 1
        "float4 PS()",                              // 2
        "{",                                        // 3
        "    float a = atan2(1, 2);",               // 4   +20
        "    float b = BAD;",                       // 5   vkd3d says 25
        "    float c = atan2(3, 4) + BAD;",         // 6   the BAD is after the call: 46
        "",                                         // 7
        "    // a comment line",                    // 8
        "    if (a > b)",                           // 9
        "    {",                                    // 10
        "        c = atan2(5, 6);",                 // 11  +20
        "        b = BAD;",                         // 12  72
        "    }",                                    // 13
        "    else",                                 // 14
        "        b = BAD;",                         // 15  75
        "    float d = a +",                        // 16
        "        BAD;",                             // 17  77
        "    return a;",                            // 18
        "}",                                        // 19
    ];

    private static ShaderError MarkedFunctionPrimary(FakeVkd3d fake, string source)
    {
        ShaderError first = fake.Compile(source)!;
        first.Line.ShouldBe(25);
        return first with
        {
            RawDiagnostics = string.Join('\n',
                "user.fx:25:11: E5017: one",
                "user.fx:46:29: E5017: two",
                "user.fx:72:5: E5017: three",
                "user.fx:75:5: E5017: four",
                "user.fx:77:1: E5017: five"),
        };
    }

    private static readonly string MarkedFunctionExpected = string.Join('\n',
        "user.fx:5:15: E5017: one",
        "user.fx:6:29: E5017: two",
        "user.fx:12:13: E5017: three",
        "user.fx:15:13: E5017: four",
        "user.fx:17:9: E5017: five");

    [Fact]
    public void RawBlob_StatementMarkers_MeasureTheWholeFunctionInOneCompile()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join(MarkedFunction);
        ShaderError primary = MarkedFunctionPrimary(fake, source);

        // What the summary alone costs: the same diagnostic with a one-line raw text.
        var summaryOnly = new FakeVkd3d { CodegenMarker = "BAD" };
        Vkd3dSourceLocator.Relocate(summaryOnly.Compile(source)!, source, source, File, summaryOnly.Compile);
        int summaryProbes = summaryOnly.Calls - 1;

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.Line.ShouldBe(5);
        located.RawDiagnostics.ShouldBe(MarkedFunctionExpected);
        fake.MarkerCompiles.ShouldBe(1);
        (fake.Calls - 1 - summaryProbes - fake.MarkerCompiles).ShouldBeLessThanOrEqualTo(1,
            "the four other lines come from the one marker compile; only 'five', on the second line "
            + "of a statement, may need a sentinel of its own");
    }

    [Fact]
    public void RawBlob_AMisplacedMarker_CostsOneMoreCompile_NeverAWrongLine()
    {
        // The statement-start reading is a heuristic. Where it is wrong the marker is a syntax
        // error, the parse stops there, and nothing after it answers: the marker is dropped
        // and the rest is measured by the next compile.
        var fake = new FakeVkd3d { CodegenMarker = "BAD", RejectMarkerOnLines = [11] };
        string source = Join(MarkedFunction);
        ShaderError primary = MarkedFunctionPrimary(fake, source);

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.RawDiagnostics.ShouldBe(MarkedFunctionExpected);
        fake.MarkerCompiles.ShouldBe(2);
    }

    [Fact]
    public void RawBlob_MarkersThatNeverAnswer_FallBackToTheBisection()
    {
        // Every marker rejected: the attempts are capped and the lines are still placed.
        var fake = new FakeVkd3d { CodegenMarker = "BAD", RejectMarkerOnLines = [.. Enumerable.Range(1, MarkedFunction.Length)] };
        string source = Join(MarkedFunction);
        ShaderError primary = MarkedFunctionPrimary(fake, source);

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.RawDiagnostics.ShouldBe(MarkedFunctionExpected);
        fake.MarkerCompiles.ShouldBe(Vkd3dSourceLocator.MaxMarkerCompiles);
    }

    [Fact]
    public void RawBlob_ALineThatCannotBePlaced_LeavesTheWholeBlobAsVkd3dWroteIt()
    {
        // Top-level code carries no statement marker, and a template call on every line
        // leaves nothing to infer, so each of these 200 lines needs probes of its own: more
        // than the allowance. A block that mixed relocated and raw line numbers would be
        // worse than either, so the blob comes back untouched (the summary still moves).
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string>();
        for (int i = 0; i < 400; i++)
            lines.Add("float f = atan2(1, 2) + BAD;");
        string source = Join(lines.ToArray());
        ShaderError first = fake.Compile(source)!;
        ShaderError primary = first with
        {
            RawDiagnostics = string.Join('\n',
                Enumerable.Range(0, 200).Select(i => $"user.fx:{(2 * i + 1) + 20 * (2 * i + 1)}:17: E5017: x")),
        };

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.Line.ShouldBe(1);
        located.RawDiagnostics.ShouldBe(primary.RawDiagnostics);
        (fake.Calls - 1).ShouldBeLessThanOrEqualTo(Vkd3dSourceLocator.MaxProbes + Vkd3dSourceLocator.MaxRawProbes);
    }

    [Fact]
    public void EveryProbe_EndsWithTheTerminator_SoASwallowedSentinelNeverRunsTheWholeCompile()
    {
        // A sentinel planted in a skipped #if arm (or a block comment) is never seen by
        // vkd3d. Without the terminator that probe is the complete failing compile again:
        // on the reporter's Apos.Shapes file one such probe doubled the time to the error.
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        var lines = new List<string> { "float a;", "#if 0" };
        for (int i = 0; i < 60; i++) lines.Add($"float skipped{i};");
        lines.AddRange(["#endif", "float4 PS()", "{", "    float b = atan2(1, 2);", "    float c = BAD;", "    float d = BAD;", "}"]);
        string source = Join(lines.ToArray());
        ShaderError first = fake.Compile(source)!;
        ShaderError primary = first with
        {
            RawDiagnostics = $"user.fx:{first.Line}:11: E5017: one\nuser.fx:{first.Line + 1}:11: E5017: two",
        };

        ShaderError located = Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile);

        located.Line.ShouldBe(67);
        located.RawDiagnostics.ShouldBe("user.fx:67:15: E5017: one\nuser.fx:68:15: E5017: two");
        fake.Calls.ShouldBeGreaterThan(2);
        fake.CompilesWithoutTerminator.ShouldBe(1, "only the test's own first compile; every probe carries the terminator");
    }

    [Fact]
    public void StatementStartLines_AreOnlyWhereAStatementCanBePrefixed()
    {
        string[] source =
        [
            "float2 dir;",                                  // 1   top level
            "struct V",                                     // 2
            "{",                                            // 3
            "    float4 p : POSITION;",                     // 4   a struct member
            "};",                                           // 5
            "static const float k[2] = {",                  // 6
            "    1.0,",                                     // 7   an initializer
            "    2.0 };",                                   // 8
            "#if SM4",                                      // 9
            "float4 Skipped() { return 0; }",               // 10
            "#endif",                                       // 11
            "float4 PS(V v) : COLOR",                       // 12
            "{",                                            // 13
            "    float a = 1;",                             // 14  yes
            "    /* a comment",                             // 15
            "       float hidden; */ float b = 2;",         // 16  starts inside the comment
            "    float m[2] = {",                           // 17  yes
            "        1.0,",                                 // 18
            "        2.0 };",                               // 19
            "    for (int i = 0;",                          // 20  yes
            "         i < 2;",                              // 21  inside the parentheses
            "         i++)",                                // 22
            "    {",                                        // 23
            "        a += m[i];",                           // 24  yes
            "    }",                                        // 25  yes
            "    if (a > b)",                               // 26  yes
            "        a = b;",                               // 27  the if's own statement
            "    else",                                     // 28
            "        b = a;",                               // 29
            "    do",                                       // 30  yes
            "    {",                                        // 31
            "        a -= 1;",                              // 32  yes
            "    }",                                        // 33  yes
            "    while (a > 0);",                           // 34  the do's own while
            "    switch (int(a))",                          // 35  yes
            "    {",                                        // 36
            "        case 0:",                              // 37
            "            b = 1;",                           // 38
            "            break;",                           // 39  yes
            "        default:",                             // 40
            "            break;",                           // 41
            "    }",                                        // 42  yes
            "#define TWICE(x) \\",                          // 43
            "    ((x) * 2)",                                // 44  a macro body
            "    return a +",                               // 45  yes
            "        b;",                                   // 46  the second line of a statement
            "}",                                            // 47  yes
            "technique T { pass P {",                       // 48
            "    PixelShader = compile ps_3_0 PS(); } }",   // 49
        ];

        Vkd3dSourceLocator.StatementStartLines(source)
            .ShouldBe([14, 17, 20, 24, 25, 26, 30, 32, 33, 35, 39, 42, 45, 47]);
    }

    [Fact]
    public void BuildLineTable_AgreesWithResolveLineDirectives_OnEveryLine()
    {
        string text = Join(
            "// prelude", "#define FNA 1", "#line 1 \"C:/dir/user.fx\"", "a", "#line 1 \"inc.fxh\"", "b", "c",
            "#line 3 \"C:/dir/user.fx\"", "d", "  #  line 40", "e");
        const string given = @"C:\dir\user.fx";

        (string File, int Line)[] table = Vkd3dSourceLocator.BuildLineTable(text, given);

        table.Length.ShouldBe(12);
        for (int p = 1; p < table.Length; p++)
            table[p].ShouldBe(Vkd3dSourceLocator.ResolveLineDirectives(text, p, given), $"physical line {p}");
        table[4].ShouldBe((given, 1), "a slash-only difference keeps the spelling the compiler was given");
        table[6].ShouldBe(("inc.fxh", 1));
        table[11].ShouldBe((given, 40));
    }

    [Fact]
    public void RawBlob_UnconvergedSummary_LeavesTheBlobAsVkd3dWroteIt()
    {
        string source = Join(Enumerable.Range(0, 200).Select(i => $"float f{i};").ToArray());
        var raw = new ShaderError(File, 150, 5, "E5017", "x",
            RawDiagnostics: "user.fx:150:5: E5017: x\nuser.fx:160:5: E5017: y");

        ShaderError located = Vkd3dSourceLocator.Relocate(raw, source, source, File, _ => null);

        located.ShouldBe(raw);
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

    // -------------------------------------------------------------------------
    // Cancellation (issue #255): each probe is one uninterruptible native compile and a
    // diagnostic may need up to MaxProbes of them, so the token is checked before every one.
    // -------------------------------------------------------------------------

    /// <summary>A 3000-line effect whose relocation needs a dozen probes (measured below).</summary>
    private static string LargeFailingSource()
    {
        var lines = new List<string>();
        for (int i = 0; i < 3000; i++)
            lines.Add(i % 7 == 0 ? $"float f{i} = atan2({i}, 1);" : $"float f{i};");
        lines[2500] = "float bad = BAD;";
        return Join(lines.ToArray());
    }

    [Fact]
    public void TokenAlreadyCancelled_ThrowsBeforeTheFirstProbe()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = LargeFailingSource();
        ShaderError primary = fake.Compile(source)!;
        int callsBefore = fake.Calls;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            Vkd3dSourceLocator.Relocate(primary, source, source, File, fake.Compile, cts.Token));

        (fake.Calls - callsBefore).ShouldBe(0, "a cancelled relocation must not run a single probe");
    }

    [Fact]
    public void TokenCancelledDuringAProbe_StopsBeforeTheNextOne()
    {
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = LargeFailingSource();
        ShaderError primary = fake.Compile(source)!;

        // Control: uncancelled, this relocation takes many probes, so stopping after
        // three below is the token's doing and not the bisection converging.
        int uncancelled = 0;
        Vkd3dSourceLocator.Relocate(primary, source, source, File, s => { uncancelled++; return fake.Compile(s); })
            .Line.ShouldBe(2501);
        uncancelled.ShouldBeGreaterThan(6);

        using var cts = new CancellationTokenSource();
        int probes = 0;
        ShaderError? CancelOnTheThird(string text)
        {
            if (++probes == 3)
                cts.Cancel();
            return fake.Compile(text);
        }

        Should.Throw<OperationCanceledException>(() =>
            Vkd3dSourceLocator.Relocate(primary, source, source, File, CancelOnTheThird, cts.Token));

        probes.ShouldBe(3, "the probe in flight finishes; the next one must not start");
    }

    [Fact]
    public void TokenCancelled_MultiDiagnosticForm_StopsToo()
    {
        // The warnings path of a SUCCESSFUL compile goes through the list overload.
        var fake = new FakeVkd3d { CodegenMarker = "BAD" };
        string source = Join("float a = atan2(1, 2);", "float b;", "float c = BAD;", "float d;", "float e = BAD;");
        ShaderError raw = fake.Compile(source)!;
        ShaderError second = raw with { Line = 25, Column = 11 };
        int callsBefore = fake.Calls;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            Vkd3dSourceLocator.Relocate([raw, second], source, source, File, fake.Compile, cts.Token));

        (fake.Calls - callsBefore).ShouldBe(0);
    }

    [Fact]
    public void TokenCancelled_ButNothingToRelocate_DoesNotThrow()
    {
        // The check guards the probes, not the call: an unlocated diagnostic needs none,
        // so it comes back untouched even under a cancelled token.
        var unlocated = new ShaderError(File, 0, 0, "SD0212", "no location");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Vkd3dSourceLocator.Relocate(unlocated, "float a;", "float a;", File, _ => null, cts.Token)
            .ShouldBe(unlocated);
    }
}
