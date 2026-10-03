#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #228, answered by measurement: does <c>-no-mangle</c> stay collision-free when a
/// generic is instantiated repeatedly? Within ONE entry point, yes (distinct <c>Box_0</c>,
/// <c>Box_1</c>, <c>Box_2</c>; cbuffer members and resources keep the author's names). ACROSS
/// entry points, no: slangc runs once per entry and numbers generic instantiations per run in
/// first-use order, so <c>helper_0</c> was the <c>float</c> instantiation in the vertex unit
/// and the <c>float2</c> one in the pixel unit, and the merged effect failed with a downstream
/// redefinition error (or, when only the signatures differed, silently bound the wrong
/// overload). <see cref="SlangHlslMerger"/> now renames the later unit's colliding symbols.
///
/// <para>Each adversarial fixture is compiled to every target <see cref="SlangCompiler"/>
/// supports, the reflected parameter names are checked exactly, and for the multi-entry
/// fixtures every entry's transitive call graph in the merged effect is proven equal to the
/// call graph in that entry's own slangc output (names normalized away).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangGenericsCollisionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "tests", "fixtures", "shaders", "slang-adversarial");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }

    private static string Source(string fixture) => File.ReadAllText(Path.Combine(FixtureDir, fixture));

    private static string FormatErrors(ShaderError[] errors) =>
        string.Join("; ", errors.Select(e => e.FxcFormattedMessage));

    // Every target SlangCompiler compiles for (Metal is not implemented anywhere).
    private static readonly PlatformTarget[] Targets =
    [
        PlatformTarget.DirectX, PlatformTarget.OpenGL, PlatformTarget.Vulkan,
        PlatformTarget.DirectX12, PlatformTarget.Fna,
    ];

    // Fixture, then the exact reflected parameter names on the sampler-less targets (DirectX,
    // DirectX12) and on the sampler-carrying ones (OpenGL, Vulkan). FNA ships fx_2_0, which
    // has no MGFX parameter table.
    public static IEnumerable<object[]> Fixtures()
    {
        yield return ["GenericsRepeatedInstantiation.slang",
            new[] { "Gain", "Tint", "SpriteTexture" },
            new[] { "Gain", "Tint", "SpriteTexture", "SpriteTextureSampler" }];
        yield return ["GenericsCrossEntryOrder.slang",
            new[] { "WVP", "Gain" },
            new[] { "WVP", "Gain" }];
        yield return ["GenericsInterfaceDivergentBodies.slang",
            new[] { "WorldViewProjection", "Mix" },
            new[] { "WorldViewProjection", "Mix" }];
        yield return ["GenericsUserNameLooksMangled.slang",
            new[] { "Gain", "Gain_0", "Tint" },
            new[] { "Gain", "Gain_0", "Tint" }];
    }

    public static IEnumerable<object[]> FixtureTargetMatrix()
    {
        foreach (object[] f in Fixtures())
        {
            foreach (PlatformTarget t in Targets)
                yield return [f[0], f[1], f[2], t];
        }
    }

    [Theory]
    [MemberData(nameof(FixtureTargetMatrix))]
    public async Task Fixture_CompilesOnEveryTarget_WithTheAuthorsParameterNames(
        string fixture, string[] sampLessNames, string[] samplerNames, PlatformTarget target)
    {
        var options = new CompilerOptions { Target = target, SourceFileName = fixture };

        var result = await new SlangCompiler().CompileAsync(Source(fixture), options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? fixture + " on " + target + ": " + FormatErrors(result.Error) : "");
        byte[] data = result.Value.Data;
        data.ShouldNotBeEmpty();

        if (target == PlatformTarget.Fna)
        {
            // fx_2_0 version token 0xFEFF0901, little-endian on disk.
            data.Take(4).ToArray().ShouldBe(new byte[] { 0x01, 0x09, 0xFF, 0xFE });
            return;
        }

        string[] expected = target is PlatformTarget.DirectX or PlatformTarget.DirectX12 ? sampLessNames : samplerNames;
        MgfxBlobReader.Parse(data).ParameterNames.ToArray()
            .ShouldBe(expected, ignoreOrder: true, customMessage: fixture + " on " + target);
    }

    // ---------------------------------------------------------------------------
    // Semantic check. A merged effect can compile and still bind an entry's call to the wrong
    // body (same name, different instantiation). The closure of an entry is its declaration
    // text with every referenced struct/function/static replaced, recursively, by ITS
    // closure and every generated '_N' suffix normalized away. Equal closures mean the entry
    // resolves to the same bodies it has in its own slangc output.
    // ---------------------------------------------------------------------------

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Captured;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Captured = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58]));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex IdentifierRx =
        new(@"\b[A-Za-z_]\w*\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex GeneratedSuffixRx =
        new(@"^(?:(.*?)_\d+|_S\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Top-level struct, function and static declarations, in order, as (name, text).</summary>
    private static List<(string Name, string Text)> ParseDecls(string hlsl)
    {
        var decls = new List<(string, string)>();
        var current = new System.Text.StringBuilder();
        int depth = 0;
        bool sawBrace = false;
        foreach (string raw in hlsl.Split('\n'))
        {
            string line = raw;
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0)
                line = line[..c];
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
                continue;

            current.Append(line.Trim()).Append(' ');
            foreach (char ch in line)
            {
                if (ch == '{') { depth++; sawBrace = true; }
                else if (ch == '}') depth--;
            }

            bool end = depth <= 0 && ((sawBrace && line.Contains('}')) || (!sawBrace && line.TrimEnd().EndsWith(';')));
            if (!end)
                continue;

            string text = current.ToString().Trim();
            current.Clear();
            depth = 0;
            sawBrace = false;
            string? name = DeclName(text);
            if (name is not null)
                decls.Add((name, text));
        }
        return decls;
    }

    private static string? DeclName(string text)
    {
        int brace = text.IndexOf('{');
        string header = (brace >= 0 ? text[..brace] : text).Trim();
        if (header.StartsWith("cbuffer", StringComparison.Ordinal))
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(header, @"^struct\s+(\w+)");
        if (m.Success)
            return m.Groups[1].Value;

        int paren = header.IndexOf('(');
        int stop = header.IndexOfAny([':', '=', ';']);
        if (paren >= 0 && (stop < 0 || paren < stop))
            return System.Text.RegularExpressions.Regex.Match(header[..paren], @"(\w+)\s*$").Groups[1].Value;

        if (header.StartsWith("static", StringComparison.Ordinal))
        {
            string head = header[..(header.IndexOfAny(['[', '=', ';']) is var i and >= 0 ? i : header.Length)];
            return System.Text.RegularExpressions.Regex.Match(head, @"(\w+)\s*$").Groups[1].Value;
        }
        return null;
    }

    private static string Closure(
        string name, Dictionary<string, string> table, Dictionary<string, string> memo)
    {
        if (memo.TryGetValue(name, out string? done))
            return done;

        string text = IdentifierRx.Replace(table[name], m =>
        {
            if (m.Value == name)
                return "@@self";
            if (table.ContainsKey(m.Value))
                return "[" + Closure(m.Value, table, memo) + "]";
            return GeneratedSuffixRx.IsMatch(m.Value) ? GeneratedSuffixRx.Replace(m.Value, "$1_N") : m.Value;
        });
        return memo[name] = text;
    }

    private static Dictionary<string, string> Table(List<(string Name, string Text)> decls)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string text) in decls)
            table[name] = text;
        return table;
    }

    [Theory]
    [InlineData("GenericsCrossEntryOrder.slang", true)]
    [InlineData("GenericsInterfaceDivergentBodies.slang", true)]
    [InlineData("GenericsRepeatedInstantiation.slang", false)]
    [InlineData("GenericsUserNameLooksMangled.slang", false)]
    public void MergedEffect_EveryEntryResolvesToItsOwnBodies(string fixture, bool vertexAndPixel)
    {
        string source = Source(fixture);
        (string Entry, string Stage)[] entries = vertexAndPixel
            ? [("MainVS", "vertex"), ("MainPS", "fragment")]
            : [("MainPS", "fragment")];

        string slangc = SlangNativeCache.EnsureRunnableSlangc(SlangToolPath.Resolve().ShouldNotBeNull());
        string toolDir = Path.GetDirectoryName(slangc)!;

        foreach (PlatformTarget target in new[] { PlatformTarget.DirectX, PlatformTarget.OpenGL })
        {
            var capture = new CapturingCompiler();
            var options = new CompilerOptions { Target = target, SourceFileName = fixture };
            var result = new SlangCompiler(capture).Compile(source, options);
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");

            List<(string Name, string Text)> mergedDecls = ParseDecls(capture.Captured.ShouldNotBeNull());
            string[] names = mergedDecls.Select(d => d.Name).ToArray();
            names.Distinct(StringComparer.Ordinal).Count().ShouldBe(
                names.Length, "a struct/function/static name is declared twice in the merged effect: " + string.Join(", ", names));
            Dictionary<string, string> mergedTable = Table(mergedDecls);

            var unitTables = new List<Dictionary<string, string>>();
            foreach ((string entry, string stage) in entries)
            {
                var (code, stdout, stderr) = SlangCompiler.RunSlangc(
                    slangc, toolDir, source, entry, stage, PlatformMacros.For(target).Macros, []);
                code.ShouldBe(0, stderr);
                Dictionary<string, string> unitTable = Table(ParseDecls(stdout));
                unitTables.Add(unitTable);

                string merged = Closure(entry, mergedTable, new Dictionary<string, string>());
                string expected = Closure(entry, unitTable, new Dictionary<string, string>());
                merged.ShouldBe(expected, fixture + " / " + target + " / " + entry);
            }

            if (vertexAndPixel)
            {
                // The premise: this fixture really does make slangc reuse a name for two bodies.
                bool collides = unitTables[0].Any(kv =>
                    unitTables[1].TryGetValue(kv.Key, out string? other) && other != kv.Value);
                collides.ShouldBeTrue(fixture + " no longer produces a same-name, different-body pair across entries");
            }
        }
    }
}
