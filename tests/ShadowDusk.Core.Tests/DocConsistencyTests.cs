#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Guards plan/phase-doc drift (issue #218): the phase index in <c>plan/plan.md</c> must agree
/// with each linked phase doc's own <c>**Status:**</c> header, nothing archived under
/// <c>plan/DONE/</c> may still claim to be open, and the committed pipeline SVG must carry the
/// notes from <c>docs/pipeline-overview.puml</c>.
///
/// <para>Lives in Core.Tests next to <see cref="PinnedNativeVersionConsistencyTests"/> on purpose:
/// it reads repo files but is cheap, and the CI unit jobs filter out
/// <c>Category=Integration</c>, so tagging it that way would move it to the label-gated job
/// where it would not run on a normal PR. The parsers are pure (string in, data out) so the
/// negative tests exercise them on in-memory samples.</para>
/// </summary>
public sealed class DocConsistencyTests
{
    // ---- pure parsers ------------------------------------------------------------------

    private static readonly string[] Glyphs = ["✅", "🟢", "🟡", "🔵", "🚧", "📋", "🔬", "🟦"];

    // Glyphs meaning "finished". Everything else in Glyphs means open / partial / planned.
    private static readonly HashSet<string> DoneGlyphs = ["✅", "🟢", "🟦"];

    // Glyph classes. A row and its doc may use different glyphs of the same class (a doc's
    // "🟢 Implemented" is a row's "✅ Done"), but never different classes.
    private static string GlyphClass(string glyph) =>
        DoneGlyphs.Contains(glyph) ? "done" : glyph is "🟡" or "🚧" ? "partial" : "open";

    private static readonly Regex Link = new(@"\[[^\]]*\]\((?<href>[^)#]+\.md)(?:#[^)]*)?\)", RegexOptions.Compiled);

    private static readonly Regex OpenWord =
        new(@"^(?:in progress|open|planned|future)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal sealed record IndexRow(int Number, string Phase, string Status, string Href);

    /// <summary>Rows of the 4-column index table (Phase | Status | File | Summary).</summary>
    internal static List<IndexRow> ParseIndex(string planMd)
    {
        var rows = new List<IndexRow>();
        bool inTable = false;
        int n = 0;
        foreach (string raw in planMd.Split('\n'))
        {
            n++;
            string line = raw.TrimEnd('\r');
            if (Regex.IsMatch(line, @"^\|\s*Phase\s*\|\s*Status\s*\|\s*File\s*\|")) { inTable = true; continue; }
            if (!inTable) continue;
            if (!line.StartsWith('|')) { inTable = false; continue; }
            if (Regex.IsMatch(line, @"^\|\s*-")) continue;

            string[] cells = line.Split('|');
            // cells[0] is empty (leading pipe); Phase, Status, File, Summary follow.
            if (cells.Length < 5) continue;
            Match link = Link.Match(cells[3]);
            rows.Add(new IndexRow(n, cells[1].Trim(), cells[2].Trim(), link.Success ? link.Groups["href"].Value : ""));
        }
        return rows;
    }

    /// <summary>The text after the first <c>**Status</c> marker line in a phase doc, or null.</summary>
    internal static string? ParseDocStatus(string docMd)
    {
        foreach (string raw in docMd.Split('\n'))
        {
            Match m = Regex.Match(raw, @"^\s*>?\s*\*\*Status\b:?\s*\*{0,2}:?\s*(?<rest>.*)$");
            if (m.Success) return m.Groups["rest"].Value.Trim();
        }
        return null;
    }

    internal static string? FirstGlyph(string text)
    {
        string? best = null;
        int bestAt = int.MaxValue;
        foreach (string g in Glyphs)
        {
            int at = text.IndexOf(g, StringComparison.Ordinal);
            if (at >= 0 && at < bestAt) { best = g; bestAt = at; }
        }
        return best;
    }

    /// <summary>Status text with leading emphasis, glyphs and whitespace removed.</summary>
    internal static string LeadingWords(string status)
    {
        string s = status;
        foreach (string g in Glyphs) s = s.Replace(g, "");
        return s.TrimStart(' ', '*', '_', '>', ':');
    }

    internal static bool SaysOpen(string status) => OpenWord.IsMatch(LeadingWords(status));

    /// <summary>Lines of every <c>note ... end note</c> block (and single-line notes) in a puml file.</summary>
    internal static List<string> ParsePumlNoteLines(string puml)
    {
        var lines = new List<string>();
        bool inNote = false;
        foreach (string raw in puml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (inNote)
            {
                if (Regex.IsMatch(line, @"^\s*end\s*note\b", RegexOptions.IgnoreCase)) { inNote = false; continue; }
                lines.Add(line);
            }
            else if (Regex.IsMatch(line, @"^\s*note\b", RegexOptions.IgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0) lines.Add(line[(colon + 1)..]);
                else inNote = true;
            }
        }
        return lines;
    }

    /// <summary>Letters and digits only, lowercased: immune to creole markup and SVG run splitting.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Note lines whose text is missing from the SVG. Fails (throws) when no notes are found.</summary>
    internal static List<string> MissingNoteLines(string puml, string svg)
    {
        List<string> noteLines = ParsePumlNoteLines(puml).Where(l => Normalize(l).Length > 0).ToList();
        noteLines.ShouldNotBeEmpty("no note text found in the puml: the check would pass vacuously");

        string svgText = Normalize(string.Concat(XDocument.Parse(svg).Descendants().Where(e => !e.HasElements).Select(e => e.Value)));
        svgText.ShouldNotBeEmpty("the SVG has no text at all");
        return noteLines.Where(l => !svgText.Contains(Normalize(l), StringComparison.Ordinal)).ToList();
    }

    // ---- checks (shared by the real-file tests and the negative tests) -----------------

    internal static List<string> IndexProblems(string planMd, Func<string, string?> readDoc, Func<string, bool> docExists)
    {
        var problems = new List<string>();
        List<IndexRow> rows = ParseIndex(planMd);
        if (rows.Count == 0) { problems.Add("no index rows parsed from plan.md"); return problems; }

        foreach (IndexRow r in rows)
        {
            string who = $"plan.md:{r.Number} (Phase {r.Phase})";
            if (r.Href.Length == 0) { problems.Add($"{who}: no phase doc link"); continue; }
            if (!docExists(r.Href)) { problems.Add($"{who}: linked doc '{r.Href}' does not exist"); continue; }

            bool inDone = r.Href.StartsWith("DONE/", StringComparison.Ordinal);
            string? rowGlyph = FirstGlyph(r.Status);
            if (inDone && (SaysOpen(r.Status) || (rowGlyph is not null && !DoneGlyphs.Contains(rowGlyph))))
                problems.Add($"{who}: links into DONE/ but status is '{r.Status}'");

            string? docStatus = ParseDocStatus(readDoc(r.Href) ?? "");
            if (docStatus is null) { problems.Add($"{who}: '{r.Href}' has no **Status:** header"); continue; }
            string? docGlyph = FirstGlyph(docStatus);
            if (rowGlyph is not null && docGlyph is not null && GlyphClass(rowGlyph) != GlyphClass(docGlyph))
                problems.Add($"{who}: row glyph {rowGlyph} but '{r.Href}' Status glyph is {docGlyph}");
        }
        return problems;
    }

    internal static List<string> DoneDocProblems(IEnumerable<(string Name, string Content)> docs)
    {
        var problems = new List<string>();
        foreach ((string name, string content) in docs)
        {
            string? status = ParseDocStatus(content);
            if (status is not null && SaysOpen(status))
                problems.Add($"DONE/{name}: Status header says '{status}'");
        }
        return problems;
    }

    // ---- real-file tests ---------------------------------------------------------------

    [Fact]
    public void PlanIndex_AgreesWithPhaseDocs()
    {
        string root = FindRepoRoot();
        string plan = Path.Combine(root, "plan");
        string md = File.ReadAllText(Path.Combine(plan, "plan.md"));

        string Resolve(string href) => Path.GetFullPath(Path.Combine(plan, href.Replace('/', Path.DirectorySeparatorChar)));

        List<string> problems = IndexProblems(md,
            href => File.Exists(Resolve(href)) ? File.ReadAllText(Resolve(href)) : null,
            href => File.Exists(Resolve(href)));

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
        ParseIndex(md).Count.ShouldBeGreaterThan(40, "the index parse looks truncated");
    }

    [Fact]
    public void DoneDocs_NoneClaimToBeOpen()
    {
        string done = Path.Combine(FindRepoRoot(), "plan", "DONE");
        var docs = Directory.EnumerateFiles(done, "*.md", SearchOption.TopDirectoryOnly)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToList();
        docs.Count.ShouldBeGreaterThan(20, "plan/DONE enumeration looks wrong");

        List<string> problems = DoneDocProblems(docs);
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PipelineSvg_ContainsEveryPumlNoteLine()
    {
        string root = FindRepoRoot();
        string puml = File.ReadAllText(Path.Combine(root, "docs", "pipeline-overview.puml"));
        string svg = File.ReadAllText(Path.Combine(root, "docfx", "images", "pipeline-overview.svg"));

        List<string> missing = MissingNoteLines(puml, svg);
        missing.ShouldBeEmpty("pipeline-overview.svg is stale; regenerate it from the puml. Missing note text:" +
                              Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // ---- negative tests: prove each assertion can fail ---------------------------------

    private const string Header = "| Phase | Status | File | Summary |\n|---|---|---|---|\n";

    private static List<string> Index(string rows, string? doc, bool exists = true) =>
        IndexProblems(Header + rows + "\n", _ => doc, _ => exists);

    [Fact]
    public void IndexProblems_GlyphMismatch_IsReported() =>
        Index("| 1 | ✅ Done | [a](a.md) | s |", "**Status:** 🔵 Open").Any(p => p.Contains("glyph", StringComparison.Ordinal)).ShouldBeTrue();

    [Fact]
    public void IndexProblems_SameClassDifferentGlyph_IsClean() =>
        Index("| 1 | ✅ Done | [a](a.md) | s |", "**Status:** 🟢 **Implemented**").ShouldBeEmpty();

    [Fact]
    public void IndexProblems_PartialRowVersusDoneDoc_IsReported() =>
        Index("| 1 | 🟡 tail open | [a](a.md) | s |", "**Status:** 🟢 **Proven**").Any(p => p.Contains("glyph", StringComparison.Ordinal)).ShouldBeTrue();

    [Fact]
    public void IndexProblems_OpenGlyphRowInDoneFolder_IsReported() =>
        Index("| 1 | 🔵 Open | [a](DONE/a.md) | s |", "**Status:** 🔵 Open").Any(p => p.Contains("DONE/", StringComparison.Ordinal)).ShouldBeTrue();

    [Fact]
    public void IndexProblems_PlannedWordInDoneFolder_IsReported() =>
        Index("| 1 | Planned | [a](DONE/a.md) | s |", "**Status:** Planned").Any(p => p.Contains("DONE/", StringComparison.Ordinal)).ShouldBeTrue();

    [Fact]
    public void IndexProblems_MissingDoc_IsReported() =>
        Index("| 1 | ✅ Done | [a](a.md) | s |", null, exists: false).Any(p => p.Contains("does not exist", StringComparison.Ordinal)).ShouldBeTrue();

    [Fact]
    public void IndexProblems_NoRowsParsed_IsReported() =>
        IndexProblems("no table here", _ => null, _ => true).ShouldNotBeEmpty();

    [Fact]
    public void IndexProblems_ConsistentRow_IsClean() =>
        Index("| 1 | ✅ **Done** (2026) | [a](DONE/a.md) | s |", "> **Status: ✅ DONE (2026)**").ShouldBeEmpty();

    [Theory]
    [InlineData("**Status:** 🔵 **Open** (x)")]
    [InlineData("**Status:** In progress")]
    [InlineData("**Status: IN PROGRESS**")]
    [InlineData("**Status:** 📋 Planned / not started")]
    public void DoneDocProblems_OpenStatus_IsReported(string header) =>
        DoneDocProblems([("x.md", "# T\n\n" + header + "\n")]).ShouldNotBeEmpty();

    [Theory]
    [InlineData("**Status:** ✅ Done. Open questions remain in section 5.")]
    [InlineData("**Status: FIXED (2026-07-23).**")]
    [InlineData("> **Status: COMPLETE** body")]
    public void DoneDocProblems_FinishedStatus_IsClean(string header) =>
        DoneDocProblems([("x.md", header + "\n")]).ShouldBeEmpty();

    [Fact]
    public void MissingNoteLines_StaleSvg_ReportsTheLine()
    {
        const string puml = "@startuml\nnote as N1\n**Alpha** beta\nnew line added later\nend note\n@enduml";
        const string svg = "<svg><text>Alpha</text><text>beta</text></svg>";
        MissingNoteLines(puml, svg).ShouldBe(["new line added later"]);
    }

    [Fact]
    public void MissingNoteLines_SplitRunsAndMarkup_Match()
    {
        const string puml = "note as N1\n== Head (x)\n**Bold** plain *(it)*\nend note";
        const string svg = "<svg><text>Head (x)</text><text>Bold</text><text> plain </text><text>(it)</text></svg>";
        MissingNoteLines(puml, svg).ShouldBeEmpty();
    }

    [Fact]
    public void MissingNoteLines_NoNotes_FailsInsteadOfPassing() =>
        Should.Throw<ShouldAssertException>(() => MissingNoteLines("@startuml\nA -> B\n@enduml", "<svg><text>A</text></svg>"));

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
