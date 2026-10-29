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
        // Final review of 2026-10-29: DXC's preprocessor decides and proves.
        ["xbox-dead-proto-and-call"] = Same,          // was SD0236 on the branch; 0.22.1 compiled it
        ["xbox-dead-precise-proto"] = Same,
        ["ifndef-hlsl-version-dead-proto"] = Same,
        ["unsigned-cmp-proto"] = Same,                // crashed main: the managed evaluator read it as false
        ["unsigned-div-proto"] = Same,
        ["error-directive-proto"] = DxcError,         // DXC's front end stops on the #error, no back end
        // Issue #444: every DXC call defines __TIMESTAMP__ to clang's placeholder text, so DXC's
        // built-in (which crashed the process) never expands. 0.22.1 compiled the first three.
        ["timestamp-dead-branch"] = Same,
        ["timestamp-in-comment"] = Same,
        ["timestamp-unused-macro"] = Same,
        ["timestamp-plain"] = TimestampLiteral,       // crashed 0.22.1 (0xC0000409)
        ["timestamp-paste"] = TimestampLiteral,       // crashed 0.22.1 and the SD0238 branch (a paste it could not see)
        ["timestamp-in-macro-body"] = TimestampLiteral,
        // Fourth review of 2026-10-29: crashed 0.22.1 and the branch (OpenGL 0xC0000005).
        ["gate-comment-before-hash"] = Same,
        ["gate-block-comment-before-hash"] = Same,
        ["gate-define-body-after-comment"] = Same,
        ["gate-hash-comment-define"] = Refused,       // a macro writes the prototype and the definition together
        ["gate-hash-space-comment-define"] = Refused,
        ["gate-define-comment-name"] = Refused,
        ["gate-define-form-feed-name"] = Refused,
        ["gate-hash-vertical-tab-define"] = Refused,
        ["gnu-line-marker-proto"] = Same,             // was a spurious SD0236 on the branch
        ["line-directive-inside-proto"] = Same,       // the #line is kept, so later lines keep their numbers
    };

    /// <summary>A used <c>__TIMESTAMP__</c> is a string literal: DXC's own front-end error for it in arithmetic.</summary>
    private const string TimestampLiteral = "DIAG X0000";

    /// <summary>The reviewer's raw files compile <c>ps_3_0</c>, below the DirectX 11 floor (mgfxc's own refusal).</summary>
    private const string Sm3OnDirectX = "DIAG SD0015";

    /// <summary>
    /// The targets that never had the fault keep compiling as they did: vkd3d (DirectX 11, FNA)
    /// accepts every shape fxc does, and DirectX 12's validated DXIL is DXC's own verdict.
    /// </summary>
    private static Dictionary<string, string> Expected(string target) => target switch
    {
        // The converters compile with warnings as errors (-WX): DXC's own preprocessor warning
        // about the unsigned conversion stops the compile in the front end, before the back end.
        "raylib" => new(Expected("sksl"), StringComparer.Ordinal)
        {
            // raylib's built-in vertex shader writes no TEXCOORD0 (its own registered refusal).
            ["gate-define-body-after-comment"] = "DIAG SD0633",
        },
        "sksl" => new(Spirv, StringComparer.Ordinal)
        {
            ["unsigned-cmp-proto"] = DxcError,
            ["unsigned-div-proto"] = DxcError,
        },
        // The OpenGL target's DXIL reflection companion refuses a `static` entry ("cannot find
        // entry function"), with or without a prototype; the SPIR-V-only routes compile it.
        // The same companion reads a source whose preparation is refused as written (DXIL has no
        // fault), so an overload prototype of a called entry gets DXC's own "ambiguous entry point
        // function" from it, as on 0.22.1, before the SPIR-V compile would refuse it.
        "OpenGL" => new(Spirv, StringComparer.Ordinal)
        {
            ["static-proto-and-def"] = DxcError,
            ["ps-overload-proto-of-called-entry"] = DxcError,
            ["inline-proto"] = DxcError,               // "cannot find entry function", as on DirectX 12
        },
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
        "Fna" => new(Expected("DirectX"), StringComparer.Ordinal)
        {
            ["noproto-macros-fxh-sprite"] = "DIAG SD0303",
            // ps_3_0 is FNA's own profile; vkd3d reads the reviewer's raw files as it always has.
            ["gate-comment-before-hash"] = Same,
            ["gate-block-comment-before-hash"] = Same,
            ["gate-define-body-after-comment"] = Same,
            ["gate-hash-comment-define"] = "DIAG E5000",
            ["gate-hash-space-comment-define"] = "DIAG E5000",
            ["gate-define-comment-name"] = Ok,
            ["gate-define-form-feed-name"] = "DIAG E4000",
            ["gate-hash-vertical-tab-define"] = "DIAG E5000",
            ["gnu-line-marker-proto"] = Same,
            ["line-directive-inside-proto"] = Same,
        },
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
            ["error-directive-proto"] = "DIAG E4001",           // vkd3d's own #error
            ["timestamp-plain"] = "DIAG E5000",                  // vkd3d's own verdict on sizeof
            ["timestamp-paste"] = "DIAG E5000",
            ["timestamp-in-macro-body"] = "DIAG E5000",
            ["gate-comment-before-hash"] = Sm3OnDirectX,
            ["gate-block-comment-before-hash"] = Sm3OnDirectX,
            ["gate-define-body-after-comment"] = Sm3OnDirectX,
            ["gate-hash-comment-define"] = Sm3OnDirectX,
            ["gate-hash-space-comment-define"] = Sm3OnDirectX,
            ["gate-define-comment-name"] = Sm3OnDirectX,
            ["gate-define-form-feed-name"] = Sm3OnDirectX,
            ["gate-hash-vertical-tab-define"] = Sm3OnDirectX,
            ["gnu-line-marker-proto"] = Sm3OnDirectX,
            ["line-directive-inside-proto"] = Sm3OnDirectX,
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
            // Validated DXIL never had the fault: DXC compiles the macro's two declarations as one.
            ["gate-hash-comment-define"] = Ok,
            ["gate-hash-space-comment-define"] = Ok,
            ["gate-define-comment-name"] = Ok,
            ["gate-define-form-feed-name"] = Ok,
            ["gate-hash-vertical-tab-define"] = Ok,
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
    /// Issue #444: DXC's preprocessor (the zero-technique fallback's <c>-P</c> run) expands
    /// <c>__TIMESTAMP__</c> to clang's own text for an unknown time, used plainly, pasted from two
    /// halves and inside a macro body, and skips it in a dead branch, in a child process: DXC's
    /// built-in expansion ended the process (<c>0xC0000409</c>).
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task TimestampExpandsToClangsPlaceholder_InDxcsPreprocessor_NeverCrashes()
    {
        (int exitCode, string output) = await RunProbeAsync("timestamp-expansion");

        exitCode.ShouldBe(0, $"the __TIMESTAMP__ probe died (0xC0000409 is DXC's built-in expansion back):\n{output}");
        output.ShouldContain(EntryPointRedeclarationProbe.VerdictPrefix + "DONE", Case.Sensitive);
        const string Placeholder = "K = \"??? ??? ?? ??:??:?? ????\";";
        Verdict(output, "ts-plain").ShouldBe("PP " + Placeholder);
        Verdict(output, "ts-paste").ShouldBe("PP " + Placeholder);
        Verdict(output, "ts-macro-body").ShouldBe("PP " + Placeholder);
        Verdict(output, "ts-dead-branch").ShouldBe("PP K = 1;");
    }

    /// <summary>
    /// The positive control of the test above: the same pinned DXC's preprocessor, called without
    /// the <c>__TIMESTAMP__</c> define, must still end the process, so the test above proves the
    /// define rather than a DXC that stopped faulting. When a DXC bump fixes the fault upstream,
    /// this turns red, and the define can be retired. Windows only, as measured (win-x64).
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "OpenGL")]
    public async Task RawDxc_StillCrashesExpandingTimestamp_ProvingTheProbeCanDetectIt()
    {
        (int exitCode, string output) = await RunProbeAsync("raw-timestamp");

        output.ShouldContain(EntryPointRedeclarationProbe.VerdictPrefix + "RAW START", Case.Sensitive);
        output.ShouldNotContain(EntryPointRedeclarationProbe.VerdictPrefix + "RAW NODXC", Case.Sensitive);
        output.ShouldNotContain(EntryPointRedeclarationProbe.VerdictPrefix + "RAW RETURNED", Case.Sensitive,
            "the pinned DXC expanded its built-in __TIMESTAMP__ without crashing: the fault is gone, so the define in " +
            "DxcFlagBuilder is no longer needed\n" + output);
        exitCode.ShouldNotBe(0, output);
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
