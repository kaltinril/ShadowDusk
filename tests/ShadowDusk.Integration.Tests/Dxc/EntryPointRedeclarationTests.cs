#nullable enable

using System.Diagnostics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// An entry point declared more than once (a prototype before the technique and the definition
/// after it, the shape two independent reviews hit as <c>A3_EntryAfterTech</c>) ended every
/// OpenGL and Vulkan compile with an access violation inside the pinned DXC's SPIR-V back end:
/// the whole process died, a consumer's game included. <c>DxcSpirvEntryRedeclarations</c> now keeps
/// the entry's redundant prototypes out of DXC's input. Each target runs in a child process
/// (<see cref="EntryPointRedeclarationProbe"/>), because before the fix the first case took the
/// process down and would have taken the test host with it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntryPointRedeclarationTests
{
    private readonly ITestOutputHelper _output;

    public EntryPointRedeclarationTests(ITestOutputHelper output) => _output = output;

    // Verdicts: "OK SAME" compiled to exactly the bytes of the effect without the prototypes;
    // "OK NOTWIN" compiled (no twin to compare); "DIAG <code>" a clean, registered diagnostic.
    private const string Same = "OK SAME";
    private const string Ok = "OK NOTWIN";
    private const string Refused = "DIAG SD0236";
    private const string DxcError = "DIAG X0000";

    /// <summary>What each SPIR-V route (OpenGL, Vulkan, raylib, SkSL) must answer.</summary>
    private static readonly Dictionary<string, string> Spirv = new(StringComparer.Ordinal)
    {
        ["ps-proto-before-technique"] = Same,
        ["ps-proto-and-def-before-technique"] = Same,
        ["ps-proto-after-def"] = Same,
        ["ps-two-protos"] = Same,
        ["ps-proto-def-per-branch"] = Same,
        ["vs-proto-before-technique"] = Same,
        ["vs-and-ps-protos-before-technique"] = Same,
        ["ps-unused-overload-proto"] = Same,
        ["helper-proto"] = Ok,
        ["ps-proto-without-definition"] = DxcError,   // "missing entry point definition" (fxc: X3501)
        ["ps-proto-return-type-differs"] = DxcError,  // "differ only in their return type" (fxc: X3068)
        ["ps-called-between-proto-and-def"] = Refused,
        ["ps-overload-proto-of-called-entry"] = Refused,
        // A macro call that writes the prototype and nothing else is removed whole (proven by
        // re-preprocessing the edited source).
        ["ps-proto-from-macro"] = Same,
        // Review of 2026-10-28: the blanked range keeps a macro call before the prototype, and
        // shapes that used to reach DXC (and crash it) are blanked to the no-prototype bytes.
        ["macro-call-before-proto"] = Same,
        ["two-protos-one-line"] = Same,
        ["typedef-return-proto"] = Same,
        ["vector-return-proto"] = Same,
        ["comment-in-proto"] = Same,
        ["conditional-semantic-proto"] = Same,
        ["const-param-proto-called"] = Same,
        ["macro-named-entry-proto"] = Same,
        // Re-review of 2026-10-29.
        ["macro-call-on-proto-line"] = Same,          // the BLOCKER: the call (and `Unused`) survives
        ["macro-proto-plus-code"] = Refused,          // no edit takes the prototype alone
        ["proto-in-macro-argument"] = Same,
        ["multi-line-proto"] = Same,
        ["inline-proto"] = Refused,                   // DXC itself refuses an inline entry (DirectX 12)
        ["static-proto-and-def"] = Ok,                // on both: the prototype adds nothing; DXC judges the definition
        ["proto-in-active-if"] = Same,
        ["attribute-only-on-proto"] = Refused,
        ["nointerpolation-only-on-proto"] = Refused,
        ["semantic-only-on-proto"] = DxcError,        // DXC ignores a prototype's semantics and reports the gap itself
        ["second-declarator-proto"] = Refused,
        ["proto-and-defined-overloads"] = DxcError,   // DXC's front end reports the ambiguity before its back end runs
        // Third review of 2026-10-29.
        ["noproto-paste-shader-target"] = Same,       // the BLOCKER: no prototype, handed to DXC as written
        ["noproto-paste-ifdef-spirv"] = Same,
        ["noproto-paste-defined-hlsl-version"] = Same,
        ["noproto-entry-name-define"] = Same,
        ["noproto-macros-fxh-sprite"] = Ok,
        ["macro-named-proto-and-def"] = Same,
        ["ifdef-gnuc-proto"] = Same,                  // crashed: __GNUC__ was read as undefined
        ["if-llvm-proto"] = Same,
        ["gnuc-overload-on-proto-line"] = Same,       // was a silent change: the overload EXTRA writes is kept
        ["paren-declarator-proto"] = Refused,         // crashed OpenGL
        ["paren-declarator-def"] = Refused,
        ["paren-declarator-def-alone"] = Ok,
        ["utf8-comment-proto"] = Same,
        ["cr-only-proto"] = Same,
    };

    /// <summary>
    /// The targets that never had the fault keep compiling as they did: vkd3d (DirectX 11, FNA)
    /// accepts every shape fxc does, and DirectX 12's validated DXIL is DXC's own verdict.
    /// </summary>
    private static Dictionary<string, string> Expected(string target) => target switch
    {
        "raylib" or "sksl" => Spirv,
        // The OpenGL target's DXIL reflection companion refuses a `static` entry ("cannot find
        // entry function"), with or without a prototype; the SPIR-V-only routes compile it.
        "OpenGL" => new(Spirv, StringComparer.Ordinal) { ["static-proto-and-def"] = DxcError },
        // Vulkan compiles every entry point as `main` (mgfxc's rename), which leaves a
        // macro-produced `PS` prototype an unrelated, unused declaration: nothing to refuse.
        // The same rename does not see a parenthesised declarator: that prototype is an unrelated
        // declaration, and that definition is not found (DXC's own clean error, as on main).
        "Vulkan" => new(Spirv, StringComparer.Ordinal)
        {
            ["macro-proto-plus-code"] = Ok,
            ["paren-declarator-proto"] = Ok,
            ["paren-declarator-def"] = DxcError,
        },
        // FNA's fx_2_0 has no DX10-style texture objects (its own clean SD0303, as before).
        "Fna" => new(Expected("DirectX"), StringComparer.Ordinal) { ["noproto-macros-fxh-sprite"] = "DIAG SD0303" },
        "DirectX" => new(Spirv, StringComparer.Ordinal)
        {
            ["ps-proto-without-definition"] = DxcError,         // vkd3d E5005, relabelled
            ["ps-proto-return-type-differs"] = "DIAG E5006",
            ["ps-called-between-proto-and-def"] = Ok,
            ["ps-overload-proto-of-called-entry"] = Ok,
            ["macro-proto-plus-code"] = Ok,
            ["inline-proto"] = Ok,
            ["static-proto-and-def"] = DxcError,                // vkd3d E5005, relabelled
            ["attribute-only-on-proto"] = Ok,
            ["nointerpolation-only-on-proto"] = Ok,
            ["semantic-only-on-proto"] = "DIAG E5004",
            ["second-declarator-proto"] = "DIAG E5000",
            ["proto-and-defined-overloads"] = "DIAG E5017",
            // vkd3d's own verdicts, unchanged by this fix (it never touches the DXBC input).
            ["paren-declarator-proto"] = "DIAG E5000",
            ["paren-declarator-def"] = "DIAG E5000",
            ["paren-declarator-def-alone"] = "DIAG E5000",
            ["cr-only-proto"] = "DIAG E4000",
        },
        // DXC's DXIL front end calls an overload prototype without a definition ambiguous, as
        // it always has here (fxc compiles it); unchanged by this fix, which never touches
        // shipped DXIL.
        "DirectX12" => new(Spirv, StringComparer.Ordinal)
        {
            ["ps-unused-overload-proto"] = DxcError,
            ["ps-called-between-proto-and-def"] = Ok,
            ["ps-overload-proto-of-called-entry"] = DxcError,
            ["macro-proto-plus-code"] = Ok,
            ["inline-proto"] = DxcError,
            ["static-proto-and-def"] = DxcError,
            ["attribute-only-on-proto"] = Ok,
            ["nointerpolation-only-on-proto"] = Ok,
            ["second-declarator-proto"] = Ok,
            ["paren-declarator-proto"] = Ok,
            ["paren-declarator-def"] = Ok,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    [Theory]
    [InlineData("OpenGL")]
    [InlineData("Vulkan")]
    [InlineData("DirectX")]
    [InlineData("DirectX12")]
    [InlineData("Fna")]
    [InlineData("raylib")]
    [InlineData("sksl")]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task EntryPointPrototypes_CompileOrFailCleanly_OnEveryTarget_NeverCrash(string target)
    {
        (int exitCode, string output) = await RunProbeAsync(target);

        exitCode.ShouldBe(0, $"the {target} probe died (an access-violation exit is DXC's SPIR-V crash back):\n{output}");
        output.ShouldContain(EntryPointRedeclarationProbe.VerdictPrefix + "DONE", Case.Sensitive);

        Dictionary<string, string> expected = Expected(target);
        foreach (EntryPointRedeclarationProbe.Case c in EntryPointRedeclarationProbe.Cases)
        {
            if (target is "raylib" or "sksl" && !c.PixelOnly)
                continue;

            string verdict = Verdict(output, c.Name);
            verdict.ShouldStartWith(expected[c.Name], Case.Sensitive, $"{target} / {c.Name}");
        }
    }

    /// <summary>
    /// The positive control: the same pinned DXC, handed the plain prototype-then-definition
    /// source with the OpenGL SPIR-V arguments and no input preparation, must still crash, so
    /// the theory above proves the fix rather than a DXC that stopped faulting. When a DXC bump
    /// fixes the fault upstream, this turns red, and the workaround (and SD0236) can be retired.
    /// Windows only: the crash was measured on win-x64. The Linux and macOS builds of the pinned
    /// DXC are not known to fault the same way (the browser build does not: it fails the compile
    /// instead), and the theory above must pass there whatever they do.
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "OpenGL")]
    public async Task RawDxc_StillCrashesOnAnEntryPrototype_ProvingTheProbeCanDetectIt()
    {
        (int exitCode, string output) = await RunProbeAsync("raw-dxc");

        output.ShouldContain(EntryPointRedeclarationProbe.VerdictPrefix + "RAW START", Case.Sensitive);
        output.ShouldNotContain(EntryPointRedeclarationProbe.VerdictPrefix + "RAW NODXC", Case.Sensitive);
        output.ShouldNotContain(EntryPointRedeclarationProbe.VerdictPrefix + "RAW RETURNED", Case.Sensitive,
            "the pinned DXC compiled an entry point with a prototype to SPIR-V without crashing: the fault is gone, so the " +
            "prototype blanking and SD0236 are no longer needed\n" + output);
        exitCode.ShouldNotBe(0, output);
    }

    private static string Verdict(string output, string name)
    {
        string prefix = EntryPointRedeclarationProbe.VerdictPrefix + name + " ";
        string? line = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        line.ShouldNotBeNull($"no verdict for {name} (the probe died on or before it):\n{output}");
        return line[prefix.Length..];
    }

    private async Task<(int ExitCode, string Output)> RunProbeAsync(string target)
    {
        string label = $"entry-redeclaration probe {target}";
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(EntryPointRedeclarationProbe.ProbeArgument);
        psi.ArgumentList.Add(target);

        TimeSpan watchdog = TimeSpan.FromMinutes(4);
        ChildProcessResult run;
        try
        {
            run = await ChildProcess.RunAsync(psi, watchdog, label, captureHangEvidence: true);
        }
        catch (ChildProcessTimeoutException ex)
        {
            throw new ShouldAssertException($"{label} did not finish within {watchdog.TotalSeconds:0} s.\n{ex.Message}");
        }

        // A crash's stderr carries the whole managed stack; keep the head.
        string err = run.Stderr;
        string output = run.Stdout + (err.Length > 4000 ? err[..4000] + "\n[...]" : err);
        _output.WriteLine($"{label}: exit {run.ExitCode} (0x{run.ExitCode:X8}) after {run.Elapsed.TotalSeconds:F1} s\n{output}");
        return (run.ExitCode, output);
    }
}

/// <summary>
/// A fact that runs on Windows only: the raw-DXC crash control is measured there (win-x64), and
/// the Linux and macOS builds of the pinned DXC are not known to fault the same way.
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The raw-DXC entry-prototype crash is measured on Windows only.";
    }
}
