#nullable enable

using System.Text;
using ShadowDusk.Core;
using Shouldly;

namespace ShadowDusk.Slang.Tests;

/// <summary>How <see cref="SlangCompiler"/> reaches slangc: the in-process seam the browser
/// uses, or the child-process seam the desktop uses.</summary>
public enum SlangcTransport
{
    InProcess,
    Process,
}

/// <summary>
/// A fake slangc for the register-pass tests (issue #292): it answers a compile with a canned
/// emission and a preprocess-only (<c>-E</c>) run the way slangc v2026.14.1 does (measured):
/// one line per input in argument order, an input it cannot open prints
/// <c>error[E00001]: cannot open file</c> on stderr and no line, and the run still exits 0.
/// Every argument list it is handed is recorded, which is what the cost tests count.
/// </summary>
internal sealed class ScriptedSlangc(
    string emission,
    string entryPreprocessed,
    IReadOnlyDictionary<string, string>? files = null,
    int compileExitCode = 0,
    string compileStderr = "")
{
    public const string FakePath = "fake-tools/slangc";

    private readonly IReadOnlyDictionary<string, string> _files = files ?? new Dictionary<string, string>();

    /// <summary>Every argument list received, in order.</summary>
    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>The inputs (after <c>--</c>) of each preprocess-only run, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> PreprocessInputs =>
        Calls.Where(c => c.Contains("-E")).Select(Inputs).ToList();

    private static IReadOnlyList<string> Inputs(IReadOnlyList<string> arguments) =>
        arguments.SkipWhile(a => a != "--").Skip(1).ToList();

    public (int ExitCode, string Stdout, string Stderr) Run(string slangSource, IReadOnlyList<string> arguments)
    {
        Calls.Add(arguments);
        if (!arguments.Contains("-E"))
            return compileExitCode == 0 ? (0, emission, "") : (compileExitCode, "", compileStderr);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        foreach (string input in Inputs(arguments))
        {
            if (input == "-")
                stdout.Append(entryPreprocessed.TrimEnd('\n')).Append('\n');
            else if (_files.TryGetValue(input, out string? text))
                stdout.Append(text.TrimEnd('\n')).Append('\n');
            else
                stderr.Append($"error[E00001]: cannot open file '{input}'\n");
        }
        return (0, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A <see cref="SlangCompiler"/> over this fake, on either transport.</summary>
    public SlangCompiler Compiler(SlangcTransport transport, IShaderCompiler downstream) =>
        transport == SlangcTransport.InProcess
            ? new SlangCompiler(downstream, Run)
            : new SlangCompiler(
                downstream,
                () => new SlangCompiler.SlangcLocation(null, FakePath),
                prepareSlangc: path => path,
                runSlangc: (path, _, source, arguments) =>
                {
                    path.ShouldBe(FakePath);
                    return Run(source, arguments);
                });

    /// <summary>A downstream that keeps the assembled <c>.fx</c> and compiles nothing.</summary>
    public sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Captured { get; private set; }

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
}
