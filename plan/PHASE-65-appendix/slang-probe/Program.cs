// THROWAWAY measurement harness for the Phase 65 spike ("full, slangc-backed Slang input -
// is it worth building?"). Not shipped, not a gate, not referenced by any product code.
//
// Answers, with REAL slangc runs (pinned v2026.14.1, the same pin validation/SlangCorpus uses):
//   1. OQ2 - does slangc's `-target hlsl` emission survive DXC (the OpenGL/Vulkan/DirectX route)
//      and vkd3d-shader at an SM<=3 profile (the FNA route)? Tested on the 17-shader Slang
//      corpus, 3 new Gum-shaped fragment shaders, AND a sample of the GENERAL .fx fixture
//      corpus (not Gum-shaped) run through slangc as HLSL-compatible Slang.
//   2. Residue - does slangc still mangle symbols with "_0" and wrap cbuffers in
//      SLANG_ParameterGroup_* structs?
//   5. Expressiveness - does a hand-written genuinely-Slang-only shader (generics + interface)
//      compile through real slangc, and does the resulting HLSL still reach DXC/vkd3d?
//
// Item 3 (FX9 wall / entry synthesis reuse) is answered by reading SlangFrontend.cs +
// SlangEntryScanner.cs directly (both already ship the [shader(...)] convention this probe
// reuses) - no runtime measurement needed, see the phase doc.

using System.Diagnostics;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;

namespace ShadowDusk.Validation.SlangProbe;

internal static class Program
{
    private static readonly Regex EntryAttribute = new(
        """\[\s*shader\s*\(\s*"(?<stage>[a-z]+)"\s*\)\s*\]\s*(?:\[[^\]]*\]\s*)*[^;{(]*?(?<name>[A-Za-z_]\w*)\s*\(""",
        RegexOptions.Compiled);

    private static int Main()
    {
        string repoRoot = FindRepoRoot();
        string mainCheckoutRoot = FindMainCheckoutRoot(repoRoot);

        // Reuse the ALREADY-CACHED pinned slangc oracle from the main checkout
        // (validation/SlangCorpus downloads it there; the worktree does not need its own copy -
        // same pinned v2026.14.1 bytes, verified by that project's SHA-256 check on first fetch).
        string slangc = Path.Combine(mainCheckoutRoot, "validation", "SlangCorpus", ".slang-oracle", "bin", "slangc.exe");
        if (!File.Exists(slangc))
        {
            Console.WriteLine($"[slang-probe] FAIL: slangc oracle not found at {slangc}");
            Console.WriteLine("[slang-probe] run validation/SlangCorpus once (or tools/setup-local-testing.ps1 -WithRenderGates) to fetch it.");
            return 1;
        }
        Console.WriteLine($"[slang-probe] slangc  : {slangc}");

        using var dxc = new DxcShaderCompiler();

        Console.WriteLine();
        Console.WriteLine("=== PART A: existing 17-shader .slang corpus (already-shipped fixtures) ===");
        string slangCorpusDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "slang");
        foreach (string file in Directory.GetFiles(slangCorpusDir, "*.slang").OrderBy(f => f))
            ProbeSlangFile(slangc, dxc, file, Path.GetFileName(file));

        Console.WriteLine();
        Console.WriteLine("=== PART B: 3 new Gum-shaped fragment shaders (grayscale/tint/blur) ===");
        string gumDir = Path.Combine(repoRoot, "plan", "PHASE-65-appendix", "slang-probe", "shaders");

        // Dump one raw slangc HLSL emission for manual inspection (residue quoting in the phase doc).
        {
            (int exit, string stdout, _) = RunProcess(slangc,
                [Path.Combine(gumDir, "GumGrayscale.slang"), "-target", "hlsl", "-entry", "MainPS", "-stage", "fragment"]);
            if (exit == 0)
                File.WriteAllText(Path.Combine(repoRoot, "plan", "PHASE-65-appendix", "slang-probe", "GumGrayscale.slangc-hlsl.txt"), stdout);
        }

        foreach (string name in new[] { "GumGrayscale.slang", "GumTint.slang", "GumBlur.slang" })
            ProbeSlangFile(slangc, dxc, Path.Combine(gumDir, name), name);

        Console.WriteLine();
        Console.WriteLine("=== PART C: genuinely Slang-only feature (generics + interface) ===");
        ProbeSlangFile(slangc, dxc, Path.Combine(gumDir, "GenericsProbe.slang"), "GenericsProbe.slang", isGenericsProbe: true);
        {
            (int exit, string stdout, _) = RunProcess(slangc,
                [Path.Combine(gumDir, "GenericsProbe.slang"), "-target", "hlsl", "-entry", "MainPS", "-stage", "fragment"]);
            if (exit == 0)
                File.WriteAllText(Path.Combine(repoRoot, "plan", "PHASE-65-appendix", "slang-probe", "GenericsProbe.slangc-hlsl.txt"), stdout);
        }

        Console.WriteLine();
        Console.WriteLine("=== PART D: sample of the GENERAL .fx fixture corpus (not Gum-shaped) ===");
        string generalDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string[] generalSample =
        [
            "MultiTexture.fx",             // multiple Texture2D/SamplerState pairs
            "ForwardLighting.fx",          // per-pixel lighting math, multiple cbuffers
            "ArrayUniform.fx",             // array-typed uniform
            "SimpleLightShader.fx",        // lighting math, non-Gum-shaped
            "MultiCbuffer.fx",             // multiple explicit cbuffers
            "SamplerStatesFull.fx",        // full sampler_state block breadth
        ];
        foreach (string name in generalSample)
            ProbeFxFile(slangc, dxc, Path.Combine(generalDir, name), name);

        // A genuine third-party, non-Gum, non-fixture-authored-by-us shader (Apostolique's
        // Apos.Shapes gallery, already vendored for the DX/Vulkan render gates).
        ProbeFxFile(slangc, dxc,
            Path.Combine(generalDir, "third-party", "Apos.Shapes", "apos-shapes.fx"), "apos-shapes.fx");

        Console.WriteLine();
        Console.WriteLine("=== PART E: what the SHIPPED frontend (SlangFrontend.ConvertToFx) does with GenericsProbe.slang ===");
        ProbeShippedFrontend(dxc, Path.Combine(gumDir, "GenericsProbe.slang"));

        Console.WriteLine();
        Console.WriteLine("[slang-probe] done.");
        return 0;
    }

    // ------------------------------------------------------------------ Part E: shipped-frontend behavior on the generics probe
    private static void ProbeShippedFrontend(DxcShaderCompiler dxc, string path)
    {
        string source = File.ReadAllText(path);
        var converted = SlangFrontend.ConvertToFx(source, new SlangConvertOptions
        {
            SourceName    = "GenericsProbe.slang",
            TechniqueName = "GenericsProbeEffect",
        });

        if (converted.IsFailure)
        {
            foreach (var err in converted.Error)
                Console.WriteLine($"  SlangFrontend REJECTED: {err.Code}: {err.Message}");
            return;
        }

        Console.WriteLine("  SlangFrontend.ConvertToFx did NOT reject this file (SD0600's pattern list doesn't");
        Console.WriteLine("  name 'interface' or bare generic-constraint syntax) - it fell through to .fx assembly.");
        Console.WriteLine("  Compiling the assembled body's generic/interface syntax directly through DXC (bypassing");
        Console.WriteLine("  slangc entirely, exactly what the SHIPPED product would do today):");

        // Strip the technique block the same way FxPreParser would, then hand the raw body
        // (still containing 'interface'/generic syntax) straight to DXC, mirroring the SHIPPED
        // pipeline (no slangc anywhere in it).
        var parse = FxPreParser.Parse(converted.Value.FxText, "GenericsProbe.slang");
        if (parse.IsFailure)
        {
            Console.WriteLine($"  FxPreParser REJECTED first: {parse.Error.Code}: {parse.Error.Message}");
            return;
        }

        var result = dxc.Compile(new DxcCompileRequest
        {
            HlslSource     = parse.Value.StrippedHlsl,
            SourceFileName = "GenericsProbe.slang",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.OpenGL,
            Macros         = [("SM4", null), ("OPENGL", null)],
        });

        Console.WriteLine(result.IsSuccess
            ? "  DXC unexpectedly ACCEPTED the raw interface/generic syntax (would need re-checking)"
            : $"  DXC REJECTED (as expected, no Slang toolchain in the shipped product): {result.Error.Code}: {FirstLine(result.Error.Message)}");
    }

    // ------------------------------------------------------------------ Part A/B/C: .slang input
    private static void ProbeSlangFile(string slangc, DxcShaderCompiler dxc, string path, string name, bool isGenericsProbe = false)
    {
        string source = File.ReadAllText(path);
        var entries = ScanEntries(source);
        if (entries.Count == 0)
        {
            Console.WriteLine($"  [{name}] no [shader(...)] entries found - SKIP");
            return;
        }

        foreach ((string entry, string stage) in entries)
        {
            (int exit, string stdout, string stderr) = RunProcess(slangc,
                [path, "-target", "hlsl", "-entry", entry, "-stage", stage]);

            if (exit != 0)
            {
                Console.WriteLine($"  [{name} :: {entry}/{stage}] slangc REJECTED:");
                Console.WriteLine(Indent(stderr));
                if (isGenericsProbe)
                    Console.WriteLine($"  [{name}] => genuinely-Slang-only feature did NOT survive slangc -target hlsl either.");
                continue;
            }

            Console.WriteLine($"  [{name} :: {entry}/{stage}] slangc OK ({stdout.Length} chars HLSL)");
            ReportResidue(name, stdout);
            TryDxc(dxc, name, entry, stage, stdout, platform: "OpenGL(SM6 SPIR-V route)");
            TryVkd3d(name, entry, stage, stdout, profile: "ps_3_0/vs_3_0 (FNA SM<=3 route)");
        }
    }

    // ------------------------------------------------------------------ Part D: .fx input
    private static void ProbeFxFile(string slangc, DxcShaderCompiler dxc, string path, string name)
    {
        string source = File.ReadAllText(path);
        var parse = FxPreParser.Parse(source, path);
        if (parse.IsFailure)
        {
            Console.WriteLine($"  [{name}] FxPreParser FAILED: {parse.Error.Message} - SKIP");
            return;
        }

        // Flatten with OpenGL platform macros (SM4 branch) so the #if ladders the shipped
        // corpus uses resolve to ONE concrete body before slangc ever sees it - the same
        // "strip FX9, define macros" methodology Phase 61 A5 used. Pass the FULL path (not just
        // the filename) so #include resolution can find sibling .fxh files.
        var pre = new Preprocessor().Flatten(
            parse.Value.StrippedHlsl, path,
            PlatformMacros.For(PlatformTarget.OpenGL), new FileSystemIncludeResolver(), []);
        if (pre.IsFailure)
        {
            Console.WriteLine($"  [{name}] preprocess FAILED: {pre.Error.Message} - SKIP");
            return;
        }

        string flattenedHlsl = pre.Value.Text;

        var entries = new List<(string Entry, string Stage)>();
        foreach (var technique in parse.Value.Techniques)
        foreach (var pass in technique.Passes)
        {
            if (pass.VertexEntryPoint is { } ve && !entries.Any(e => e.Entry == ve))
                entries.Add((ve, "vertex"));
            if (pass.PixelEntryPoint is { } pe && !entries.Any(e => e.Entry == pe))
                entries.Add((pe, "fragment"));
        }

        if (entries.Count == 0)
        {
            Console.WriteLine($"  [{name}] no technique/pass entries found - SKIP");
            return;
        }

        // Write the flattened HLSL body (ordinary .fx source, no technique/pass block, no Slang
        // attributes - this is the "how much of our REAL production HLSL does slangc accept as
        // Slang" measurement) to a temp file for slangc.
        string tmp = Path.Combine(Path.GetTempPath(), $"slangprobe-{Guid.NewGuid():N}.hlsl");
        File.WriteAllText(tmp, flattenedHlsl);
        try
        {
            foreach ((string entry, string stage) in entries)
            {
                (int exit, string stdout, string stderr) = RunProcess(slangc,
                    [tmp, "-target", "hlsl", "-entry", entry, "-stage", stage]);

                if (exit != 0)
                {
                    Console.WriteLine($"  [{name} :: {entry}/{stage}] slangc REJECTED (real production HLSL, not Gum-shaped):");
                    Console.WriteLine(Indent(stderr));
                    continue;
                }

                Console.WriteLine($"  [{name} :: {entry}/{stage}] slangc OK ({stdout.Length} chars HLSL)");
                ReportResidue(name, stdout);
                TryDxc(dxc, name, entry, stage, stdout, platform: "OpenGL(SM6 SPIR-V route)");
                TryVkd3d(name, entry, stage, stdout, profile: "ps_3_0/vs_3_0 (FNA SM<=3 route)");
            }
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ------------------------------------------------------------------ residue check
    private static void ReportResidue(string name, string hlsl)
    {
        int mangled = Regex.Matches(hlsl, @"\b\w+_0\b").Count;
        bool wrapped = hlsl.Contains("SLANG_ParameterGroup_", StringComparison.Ordinal);
        if (mangled > 0 || wrapped)
        {
            Console.WriteLine($"    residue: _0-suffixed identifiers={mangled}, SLANG_ParameterGroup_ wrapping={wrapped}");
        }
    }

    // ------------------------------------------------------------------ DXC reachability (GL/Vulkan/DirectX route)
    private static void TryDxc(DxcShaderCompiler dxc, string name, string entry, string slangStage, string hlsl, string platform)
    {
        ShaderStage stage = slangStage == "vertex" ? ShaderStage.Vertex : ShaderStage.Pixel;

        Result<PlatformBlob, ShaderError> result = dxc.Compile(new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = name,
            EntryPoint     = entry,
            Stage          = stage,
            Platform       = PlatformTarget.OpenGL,
        });

        Console.WriteLine(result.IsSuccess
            ? $"    DXC [{platform}]: OK"
            : $"    DXC [{platform}]: FAIL - {result.Error.Code}: {FirstLine(result.Error.Message)}");
    }

    // ------------------------------------------------------------------ vkd3d-shader reachability (FNA SM<=3 route)
    private static void TryVkd3d(string name, string entry, string slangStage, string hlsl, string profile)
    {
        ShaderStage stage = slangStage == "vertex" ? ShaderStage.Vertex : ShaderStage.Pixel;
        string profileOverride = stage == ShaderStage.Vertex ? "vs_3_0" : "ps_3_0";

        var vkd3d = new Vkd3dShaderCompiler();
        Result<PlatformBlob, ShaderError> result = vkd3d.Compile(new D3DCompileRequest
        {
            HlslSource      = hlsl,
            SourceFileName  = name,
            EntryPoint      = entry,
            Stage           = stage,
            ProfileOverride = profileOverride,
        });

        Console.WriteLine(result.IsSuccess
            ? $"    vkd3d-shader [{profile}]: OK"
            : $"    vkd3d-shader [{profile}]: FAIL - {result.Error.Code}: {FirstLine(result.Error.Message)}");
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private static List<(string Entry, string Stage)> ScanEntries(string source)
    {
        var entries = new List<(string, string)>();
        foreach (Match m in EntryAttribute.Matches(source))
        {
            string stage = m.Groups["stage"].Value switch
            {
                "vertex" => "vertex",
                "fragment" or "pixel" => "fragment",
                var other => other,
            };
            entries.Add((m.Groups["name"].Value, stage));
        }
        return entries;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Take(6).Select(l => "         " + l));

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("could not locate the repository root (ShadowDusk.slnx)");
    }

    // The worktree this probe runs from has no slangc cache of its own; the main checkout
    // (sibling of .claude/worktrees/<id>) already has one from validation/SlangCorpus. Walk up
    // past any .claude/worktrees/<id> segment to find it.
    private static string FindMainCheckoutRoot(string repoRoot)
    {
        int marker = repoRoot.IndexOf(Path.Combine(".claude", "worktrees"), StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? repoRoot : repoRoot[..marker].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
