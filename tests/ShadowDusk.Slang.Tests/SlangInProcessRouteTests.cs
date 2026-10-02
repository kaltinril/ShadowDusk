#nullable enable

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// The in-process slangc route (issue #257): the browser runs slangc compiled to WebAssembly
/// inside the page, through <see cref="SlangCompiler"/>'s internal in-process constructor.
/// These tests pin the two things that make that route the SAME route as the desktop one:
/// the argument list is the one <see cref="SlangcArguments.Build"/> list, and the raw text an
/// in-process slangc returns is normalized to exactly what the process route's per-line
/// capture produces. The byte identity of the WebAssembly slangc itself against native slangc
/// is measured by <c>.wasm-build/slang-wasm/node-test-slangc-wasm.mjs</c> and, in a real
/// browser, by <c>tests/ShadowDusk.BrowserTests/browser-slang-gate.mjs</c>.
/// </summary>
public sealed class SlangInProcessRouteTests
{
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
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, []));
        }
    }

    [Fact]
    public void Build_IsSlangcRunsExactCommandLine()
    {
        IReadOnlyList<string> args = SlangcArguments.Build(
            PlatformMacros.For(PlatformTarget.OpenGL).Macros,
            [new UserDefine("QUALITY", "2")],
            "MainPS",
            "fragment");

        args.ShouldBe(
        [
            "-lang", "slang",
            "-DMGFX=1", "-DGLSL=1", "-DOPENGL=1",
            "-DQUALITY=2",
            "-target", "hlsl",
            "-no-hlsl-pack-constant-buffer-elements",
            "-no-mangle",
            "-entry", "MainPS",
            "-stage", "fragment",
            "--", "-",
        ]);
    }

    /// <summary>
    /// The node identity gate (<c>.wasm-build/slang-wasm/node-test-slangc-wasm.mjs</c>) and any
    /// other JS harness read slangc's per-target command lines from
    /// <c>tests/fixtures/golden/slangc-args.json</c> instead of re-typing them. This pins that file
    /// to <see cref="SlangcArguments.Build"/> and <see cref="PlatformMacros.For(PlatformTarget)"/>,
    /// so a flag or macro change here cannot leave the gate testing a stale command line.
    /// Regenerate with <c>SHADOWDUSK_REGENERATE_SLANGC_ARGS=1</c> (never in CI).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void CommittedSlangcArgs_MatchTheSharedArgumentBuilder()
    {
        // 'targets' is the per-entry compile; 'preprocess' is the preprocess-only pass that finds
        // author-written registers (issue #252 follow-up). Both are slangc invocation shapes
        // the in-process module must answer exactly like native slangc.
        var targets = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var preprocess = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (PlatformTarget target in Enum.GetValues<PlatformTarget>().Where(PlatformMacros.IsSupported))
        {
            targets[target.ToString()] = SlangcArguments.Build(PlatformMacros.For(target).Macros, [], "{entry}", "{stage}");
            preprocess[target.ToString()] = SlangcArguments.BuildPreprocess(PlatformMacros.For(target).Macros, []);
        }
        string expected = JsonSerializer.Serialize(new { targets, preprocess }, new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        string path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "slangc-args.json");
        if (Environment.GetEnvironmentVariable("SHADOWDUSK_REGENERATE_SLANGC_ARGS") == "1")
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                throw new InvalidOperationException("Regenerating slangc-args.json in CI would turn this check into a fabricated pass.");
            File.WriteAllText(path, expected);
            return;
        }

        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).ShouldBe(expected,
            "tests/fixtures/golden/slangc-args.json is stale: regenerate it with SHADOWDUSK_REGENERATE_SLANGC_ARGS=1");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a\nb\n", "a\nb\n")]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\rb", "a\nb\n")]
    [InlineData("unterminated", "unterminated\n")]
    [InlineData("\n\n", "\n\n")]
    [InlineData("a\r\r\nb", "a\n\nb\n")]
    public void JoinOutputLines_MatchesTheProcessRoutesPerLineCapture(string raw, string expected) =>
        SlangcArguments.JoinOutputLines(raw).ShouldBe(expected);

    [Fact]
    public void InProcessRoute_PassesTheSharedArgumentList_AndNormalizesItsOutput()
    {
        const string source = """
            [shader("vertex")]
            float4 MainVS(float4 p : POSITION) : SV_Position { return p; }

            [shader("fragment")]
            float4 MainPS() : SV_Target { return 1.0; }
            """;
        var calls = new List<IReadOnlyList<string>>();
        var downstream = new CapturingCompiler();
        var compiler = new SlangCompiler(downstream, (slangSource, arguments) =>
        {
            slangSource.ShouldBe(source);
            calls.Add(arguments);
            string entry = arguments[arguments.Count - 5];
            // CRLF on purpose: an in-process writer hands back raw text, line endings and all.
            return (0, $"#pragma pack_matrix(column_major)\r\n\r\n#line 2 \"<stdin>\"\r\nfloat4 {entry}() : SV_Target\r\n{{\r\n    return 0;\r\n}}\r\n", "");
        });

        var options = new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "Two.slang" };
        var result = compiler.Compile(source, options);

        result.IsSuccess.ShouldBeTrue();
        calls.Count.ShouldBe(2);
        calls[0].ShouldBe(SlangcArguments.Build(PlatformMacros.For(PlatformTarget.DirectX).Macros, [], "MainVS", "vertex"));
        calls[1].ShouldBe(SlangcArguments.Build(PlatformMacros.For(PlatformTarget.DirectX).Macros, [], "MainPS", "fragment"));
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldNotContain("\r", Case.Sensitive);
        fx.ShouldNotContain("#pragma pack_matrix", Case.Sensitive);
        fx.ShouldContain("float4 MainVS() : SV_Target\n{\n    return 0;\n}\n", Case.Sensitive);
        fx.ShouldContain("float4 MainPS() : SV_Target\n{\n    return 0;\n}\n", Case.Sensitive);
    }

    [Fact]
    public void InProcessRoute_NonZeroExit_SurfacesSlangcsOwnDiagnostic()
    {
        const string source = """
            [shader("fragment")]
            float4 MainPS() : SV_Target { return 1.0; }
            """;
        var compiler = new SlangCompiler(new CapturingCompiler(), (_, _) =>
            (1, "", "error[E30015]: undefined identifier 'nope'.\r\n  --> <stdin>:2:37\r\n"));

        var result = compiler.Compile(source, new CompilerOptions { SourceFileName = "Bad.slang" });

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.Single();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Bad.slang", 2, 37, "E30015"));
        error.Message.ShouldContain("undefined identifier 'nope'", Case.Sensitive);
        error.Message.ShouldNotContain("\r", Case.Sensitive);
    }

    /// <summary>
    /// The whole corpus through the IN-PROCESS seam, with native slangc standing behind it
    /// (raw stdout read whole, not line by line): the assembled <c>.fx</c> must hash to the
    /// committed manifest's <c>AssembledFx</c> keys, i.e. the seam adds and loses nothing
    /// relative to the process route.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void InProcessSeam_BackedByNativeSlangc_ReproducesTheManifestsAssembledFx()
    {
        string repoRoot = FindRepoRoot();
        string slangc = SlangNativeCache.EnsureRunnableSlangc(
            SlangToolPath.Resolve() ?? throw new InvalidOperationException("slangc not restored (tools/restore.*)"));

        var expected = JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(
            Path.Combine(repoRoot, "tests", "fixtures", "golden", "byte-identity", "slang-manifest.json")))!;

        var failures = new List<string>();
        int checkedCount = 0;
        foreach ((string key, string hash) in expected.Where(kv => kv.Key.StartsWith("AssembledFx/", StringComparison.Ordinal)))
        {
            string rel = key["AssembledFx/".Length..];
            string path = rel.StartsWith("slang/", StringComparison.Ordinal)
                ? Path.Combine(repoRoot, "tests", "fixtures", "shaders", rel)
                : Path.Combine(repoRoot, "plan", "PHASE-65-appendix", "slang-probe", "shaders", rel["phase65-probe/".Length..]);
            string source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

            var capture = new CapturingCompiler();
            var result = new SlangCompiler(capture, (src, args) => RunWhole(slangc, src, args)).Compile(
                source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = Path.GetFileName(path) });
            if (result.IsFailure || capture.Captured is null)
            {
                failures.Add($"{key}: {string.Join(" | ", result.IsFailure ? result.Error.Select(e => e.Message) : [])}");
                continue;
            }
            string got = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Captured))).ToLowerInvariant();
            if (got != hash)
                failures.Add($"{key}: manifest={hash} in-process-seam={got}");
            checkedCount++;
        }

        failures.ShouldBeEmpty();
        checkedCount.ShouldBe(21);
    }

    // Native slangc behind the in-process delegate shape: arguments verbatim, source on stdin,
    // stdout/stderr returned as raw whole text (what an in-process writer captures).
    private static (int, string, string) RunWhole(string slangc, string source, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(slangc)
        {
            WorkingDirectory = Path.GetDirectoryName(slangc)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in arguments)
            psi.ArgumentList.Add(a);

        // The delegate shape is synchronous by contract, so this is the blocking form of the
        // shared child-process helper: same concurrent drain, tree kill and evidence on timeout.
        ChildProcessResult run = ChildProcess.Run(psi, TimeSpan.FromSeconds(120), "slangc", standardInput: source);
        return (run.ExitCode, run.Stdout, run.Stderr);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }
}
