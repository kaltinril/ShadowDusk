#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Lexer;
using ShadowDusk.HLSL.Preprocessing;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #308 fidelity sweep. The legacy-sampler recovery hands DXC a text that ShadowDusk's own
/// managed preprocessor produced (<see cref="FxMacroPreprocessor.ProcessForCompiler"/>), so that
/// text has to be what DXC's preprocessor would have produced from the same flattened source. This
/// compares the two, token for token, on every fixture in the repository, with the OpenGL macro
/// set: DXC with <c>-P</c> (the desktop oracle this test host has) against the managed view. Only
/// the token stream is compared (whitespace, comments, <c>#line</c> and <c>#pragma</c> lines
/// aside): that is what the compiler sees.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue308PreprocessorFidelityCorpusTests(ITestOutputHelper output)
{
    /// <summary>
    /// Fixtures DXC's preprocessor itself rejects (and so does the managed one): nothing to
    /// compare. <c>PreprocessorTest.fx</c> probes a deliberately malformed <c>#if foo(TEST)</c>.
    /// </summary>
    private static readonly HashSet<string> MalformedForEveryPreprocessor = new(StringComparer.Ordinal)
    {
        "third-party/MonoGame/PreprocessorTest.fx",
    };

    [Fact]
    [Trait("Platform", "OpenGL")]
    public void EveryFixture_ManagedCompilerInputView_MatchesDxcPreprocessOutputTokenForToken()
    {
        string root = TestHelpers.FixturePath("");
        string[] files = Directory.GetFiles(root, "*.fx", SearchOption.AllDirectories);
        files.Length.ShouldBeGreaterThan(100, "the fixture corpus was not copied next to the test assembly");

        MacroSet macros = PlatformMacros.For(PlatformTarget.OpenGL);
        var dxcMacros = macros.Macros
            .Select(m => (m.Name, (string?)m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        using var dxc = new DxcShaderCompiler();
        var failures = new List<string>();
        int compared = 0;

        foreach (string path in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            string raw = File.ReadAllText(path);

            var flattened = new ShadowDusk.Core.Preprocessor.Preprocessor().Flatten(raw, path, macros, new FileSystemIncludeResolver(), []);
            if (flattened.IsFailure)
                continue; // an include that does not resolve never reaches either preprocessor in a compile

            var ours = FxMacroPreprocessor.ProcessForCompiler(flattened.Value.Text, path);
            var theirs = dxc.Preprocess(new DxcPreprocessRequest
            {
                HlslSource = flattened.Value.Text,
                SourceFileName = path,
                Macros = dxcMacros,
            });

            if (theirs.IsFailure)
            {
                if (!MalformedForEveryPreprocessor.Contains(name))
                    failures.Add($"{name}: DXC -P itself fails: {theirs.Error.Message}");
                else
                    ours.IsFailure.ShouldBeTrue($"{name}: DXC rejects this fixture, so the managed view must too");
                continue;
            }

            if (ours.IsFailure)
            {
                failures.Add($"{name}: the managed view fails where DXC -P succeeds: {ours.Error.Code} {ours.Error.Message}");
                continue;
            }

            compared++;
            string[] a = CodeTokens(ours.Value.Text, path);
            string[] b = CodeTokens(theirs.Value, path);
            if (!a.SequenceEqual(b, StringComparer.Ordinal))
            {
                int i = 0;
                while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
                string near(string[] t) => string.Join(" ", t.Skip(Math.Max(0, i - 5)).Take(12));
                failures.Add($"{name}: token {i} differs; managed: [{near(a)}] dxc: [{near(b)}]");
            }
        }

        output.WriteLine($"compared {compared} of {files.Length} fixtures against DXC -P");
        compared.ShouldBeGreaterThan(100);
        failures.ShouldBeEmpty();
    }

    /// <summary>The identifiers, numbers, strings and punctuation of a text, in order; directives and comments dropped.</summary>
    private static string[] CodeTokens(string text, string file) =>
        new FxLexer(text, file).Tokenize()
            .Where(t => t.Kind is not (TokenKind.Preprocessor or TokenKind.LineComment or TokenKind.BlockComment or TokenKind.EOF))
            .Select(t => t.Text)
            .ToArray();
}
