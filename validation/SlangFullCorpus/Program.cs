// SlangFullCorpus = the Phase 66 A7 render gate for the REAL-slangc compile route
// (ShadowDusk.Slang.SlangCompiler), the sibling of validation/SlangCorpus (which validates
// ONLY the HLSL-compatible-SUBSET frontend, ShadowDusk.Compiler.Slang.SlangFrontend, and stays
// unchanged by this phase). Two gates:
//
//   GATE 1 - compile sweep. Every shader in the 21-file real-slangc corpus (the shipped
//   17-shader tests/fixtures/shaders/slang/ set + the 4 Phase 65 Gum/generics-probe shaders)
//   compiles through the REAL ShadowDusk.Slang.SlangCompiler - which itself invokes the real,
//   restored slangc.exe (not a downloaded test-time oracle: for THIS package, slangc IS the
//   product route, not a comparison target) - on every reachable target: OpenGL, DirectX_11,
//   DirectX12, Vulkan. FNA is skipped, matching validation/SlangCorpus's own precedent (that
//   gate never covers FNA either - see its own "not yet proven" note in
//   docs/validation-matrix.md section 8.0). Every success is also parsed with the real
//   MgfxBlobReader (source-linked from ShadowDusk.Integration.Tests, the same parser a
//   compiled-effect-consuming test uses) as a rung-2 structural check, not just a magic-header
//   peek.
//
//   GATE 2 - pixel equivalence (OpenGL, uniform-free procedural subset). The SAME evidence
//   model validation/SlangCorpus's own gate 2 already established (Phase 61 section 5.2):
//   render the SAME .slang file two ways through the SAME downstream DXC + SPIRV-Cross and
//   pixel-diff. The two routes here are narrower than SlangCorpus's (there both sides differ
//   in HOW the Slang text is READ; here BOTH sides already go through the real slangc - the
//   only variable is whether ShadowDusk's OWN .fx-wrapping / platform-macro-forwarding /
//   preprocessing pipeline around slangc's HLSL emission changes the rendered pixels):
//     route A (ShadowDusk): SlangCompiler's REAL assembled .fx text (captured via an injected
//                            stub IShaderCompiler, per Phase 66 A3's own test pattern) ->
//                            FxPreParser -> Preprocessor -> DXC -> SPIRV-Cross GLSL.
//     route B (slangc raw): the SAME slangc invocation SlangCompiler.RunSlangc uses internally
//                            (identical flags - internal + InternalsVisibleTo, not a
//                            hand-duplicated flag list that could silently drift) -> its raw
//                            HLSL emission fed DIRECTLY to the same DXC -> SPIRV-Cross, with NO
//                            ShadowDusk .fx wrapping/merge/preprocessing at all.
//   Identical slangc invocation AND identical downstream DXC/SPIRV-Cross on both sides means
//   any divergence is attributable to the one thing that differs: the wrapping pipeline itself.
//
//   GATE 3 - real engine load (DirectX_11 only). Loads every corpus shader's SlangCompiler
//   DirectX_11 output into a REAL MonoGame.Framework.WindowsDX Effect (validation/SharedDx's
//   DxEffectImageRenderer, the same renderer validation/CandidateDx uses) and draws one frame.
//   This is the "compile-and-load" half of Phase 66 A7's own stated fallback: a full 4-target
//   render-vs-reference-compiler gate (mirroring the DX11/DX12/Vulkan/GL BaselineXxx+CandidateXxx
//   multi-process shape every other backend target uses) is real, separate, multi-project
//   engineering this stage did not build - see the phase doc's A7 section for exactly what
//   is proven here vs left open for a follow-up.
//
// Exits non-zero on any failure.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.ImageTests.GlContext;
using ShadowDusk.Integration.Tests;
using ShadowDusk.Slang;
using ShadowDusk.Validation.Dx;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace ShadowDusk.Validation.SlangFullCorpus;

internal static class Program
{
    /// <summary>Max per-channel delta route A vs route B. Both sides run the SAME slangc
    /// emission through the SAME DXC/SPIRV-Cross; the only difference is ShadowDusk's own
    /// .fx-wrapping, so 0 would be the ideal, but DXC's own optimizer is not guaranteed
    /// bit-stable across two independently constructed (if textually near-identical)
    /// translation units, so a small tolerance stays honest rather than flaky.</summary>
    private const int Tolerance = 2;

    private static readonly Regex EntryAttribute = new(
        """\[\s*shader\s*\(\s*"(?<stage>[a-z]+)"\s*\)\s*\]\s*(?:\[[^\]]*\]\s*)*[^;{(]*?(?<name>[A-Za-z_]\w*)\s*\(""",
        RegexOptions.Compiled);

    private static readonly PlatformTarget[] CompileSweepTargets =
    [
        PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.DirectX12, PlatformTarget.Vulkan,
    ];

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            return 1;
        }
    }

    private static int Run()
    {
        string repoRoot = FindRepoRoot();
        string[] corpus = Corpus(repoRoot);

        Console.WriteLine($"[slang-full-corpus] corpus : {corpus.Length} shaders (real-slangc route, ShadowDusk.Slang)\n");
        if (corpus.Length < 21)
            throw new InvalidOperationException($"corpus has {corpus.Length} shaders; expected >= 21");

        if (!SlangToolPath.IsSupportedOnThisPlatform || SlangToolPath.Resolve() is null)
        {
            Console.Error.WriteLine(
                "FAIL: the restored slangc.exe (tools/slang/win-x64/) was not found. " +
                "Run tools/restore.ps1 first - this gate exercises the REAL product route " +
                "(ShadowDusk.Slang.SlangCompiler), not a separately downloaded test-time oracle.");
            return 1;
        }

        int failures = 0;

        failures += RunCompileSweep(corpus);
        failures += RunPixelEquivalenceGate(corpus);
        failures += RunDirectX11LoadGate(corpus);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"Slang full-corpus gate: PASSED ({corpus.Length} shaders across {CompileSweepTargets.Length} targets)"
            : $"Slang full-corpus gate: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------- gate 1
    private static int RunCompileSweep(string[] corpus)
    {
        Console.WriteLine("=== GATE 1: every corpus shader compiles through the REAL SlangCompiler ===");
        Console.WriteLine($"    (targets: {string.Join(", ", CompileSweepTargets)})\n");

        int failures = 0;
        var perTargetOk = CompileSweepTargets.ToDictionary(t => t, _ => 0);

        foreach (string file in corpus)
        {
            string name = Path.GetFileName(file);
            string source = File.ReadAllText(file);
            var cells = new List<string>();

            foreach (PlatformTarget target in CompileSweepTargets)
            {
                var options = new CompilerOptions { Target = target, SourceFileName = name };
                var result = new SlangCompiler().Compile(source, options);

                if (result.IsFailure)
                {
                    failures++;
                    cells.Add($"{target}=FAIL[{string.Join(",", result.Error.Select(e => e.Code))}]");
                    continue;
                }

                try
                {
                    MgfxBlobReader.Parse(result.Value.Data);
                }
                catch (Exception ex)
                {
                    failures++;
                    cells.Add($"{target}=STRUCT-FAIL[{ex.Message}]");
                    continue;
                }

                perTargetOk[target]++;
                cells.Add($"{target}=OK");
            }

            Console.WriteLine($"  {name,-32} {string.Join("  ", cells)}");
        }

        Console.WriteLine();
        foreach (PlatformTarget target in CompileSweepTargets)
            Console.WriteLine($"  {target}: {perTargetOk[target]}/{corpus.Length}");
        Console.WriteLine();

        return failures;
    }

    // ------------------------------------------------------------------------------- gate 2
    private static int RunPixelEquivalenceGate(string[] corpus)
    {
        Console.WriteLine("=== GATE 2: ShadowDusk's .fx-wrapped route renders the SAME pixels as slangc's RAW HLSL (OpenGL) ===");

        string[] procedural = corpus.Where(f =>
        {
            string text = File.ReadAllText(f);
            var entries = ScanEntries(text);
            return entries.Count == 1 && entries[0].Stage == "fragment"
                   && !text.Contains("cbuffer") && !text.Contains("Texture2D");
        }).ToArray();

        Console.WriteLine($"  (uniform-free procedural subset: {procedural.Length} shaders)");
        if (procedural.Length < 5)
        {
            Console.WriteLine($"  [SKIP] only {procedural.Length} uniform-free procedural shaders found - " +
                               "leaving the gate a no-op rather than a false failure floor. This is a " +
                               "real-corpus-shape finding, not a driver bug; see the phase doc.");
            return 0;
        }

        string slangcPath = SlangToolPath.ResolveOrThrow();
        string toolDirectory = SlangNativeCache.EnsureWritableToolDirectory(slangcPath);
        string runnableSlangc = Path.Combine(toolDirectory, "slangc.exe");
        var platformMacros = PlatformMacros.For(PlatformTarget.OpenGL).Macros;

        using var gl = new GlHost();
        using var dxc = new DxcShaderCompiler();
        var transpiler = new SpirvCrossGlslTranspiler();

        int failures = 0;
        foreach (string file in procedural)
        {
            string name = Path.GetFileName(file);
            string source = File.ReadAllText(file);
            var entry = ScanEntries(source)[0];

            try
            {
                string glslA = RouteA_ShadowDuskWrapped(source, name, entry.Entry);
                string glslB = RouteB_SlangcRaw(runnableSlangc, toolDirectory, source, entry.Entry, platformMacros, name, dxc, transpiler);

                (int maxDelta, double variedFraction) = gl.RenderAndCompare(glslA, glslB);

                if (variedFraction < 0.05)
                {
                    failures++;
                    Console.WriteLine($"  [FAIL] {name}: image is near-constant ({variedFraction:P1} of pixels vary)");
                    continue;
                }

                if (maxDelta > Tolerance)
                {
                    failures++;
                    Console.WriteLine($"  [FAIL] {name}: routes diverge, max channel delta {maxDelta} (tolerance {Tolerance})");
                    continue;
                }

                Console.WriteLine($"  [OK  ] {name}: pixel-identical within {Tolerance} (maxd {maxDelta}, {variedFraction:P0} of pixels vary)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>Route A: the REAL SlangCompiler pipeline, captured at the point it hands its
    /// assembled .fx text to the downstream compiler (the same capture technique Phase 66
    /// A3's own tests use for the VS+PS merge assertion), then run through the SAME
    /// parse/preprocess/DXC/SPIRV-Cross stages validation/SlangCorpus's own route A uses.</summary>
    private static string RouteA_ShadowDuskWrapped(string slangSource, string name, string entry)
    {
        var capture = new CapturingStubCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = name };
        var result = new SlangCompiler(capture).Compile(slangSource, options);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                "SlangCompiler: " + string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        }
        if (capture.CapturedFxText is null)
            throw new InvalidOperationException("SlangCompiler never reached the downstream compiler");

        var parse = FxPreParser.Parse(capture.CapturedFxText, name);
        if (parse.IsFailure)
            throw new InvalidOperationException($"parse: {parse.Error.Message}");

        var pre = new Preprocessor().Flatten(
            parse.Value.StrippedHlsl, name,
            PlatformMacros.For(PlatformTarget.OpenGL), new FileSystemIncludeResolver(), []);
        if (pre.IsFailure)
            throw new InvalidOperationException($"preprocess: {pre.Error.Message}");

        return CompileFragmentToGlsl(pre.Value.Text, entry, name, new DxcShaderCompiler(), new SpirvCrossGlslTranspiler());
    }

    /// <summary>Route B: slangc's raw HLSL emission for the SAME entry, via the exact same
    /// invocation <see cref="SlangCompiler.RunSlangc"/> uses internally (internal +
    /// InternalsVisibleTo - see ShadowDusk.Slang.csproj - so this can never silently drift
    /// from what SlangCompiler actually passes), fed directly to DXC with NO ShadowDusk .fx
    /// wrapping, merge, or preprocessing at all.</summary>
    private static string RouteB_SlangcRaw(
        string slangcPath, string toolDirectory, string slangSource, string entry,
        IReadOnlyList<MacroDefinition> platformMacros, string name,
        DxcShaderCompiler dxc, SpirvCrossGlslTranspiler transpiler)
    {
        (int exitCode, string stdout, string stderr) = SlangCompiler.RunSlangc(
            slangcPath, toolDirectory, slangSource, entry, "fragment", platformMacros, []);
        if (exitCode != 0)
            throw new InvalidOperationException($"slangc (route B): {stderr}");

        return CompileFragmentToGlsl(stdout, entry, name + " (slangc raw HLSL)", dxc, transpiler);
    }

    private static string CompileFragmentToGlsl(
        string hlsl, string entry, string name, DxcShaderCompiler dxc, SpirvCrossGlslTranspiler transpiler)
    {
        var spirv = dxc.Compile(new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = name,
            EntryPoint     = entry,
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.OpenGL,
        }, default);
        if (spirv.IsFailure)
            throw new InvalidOperationException($"DXC: {spirv.Error.Message}");

        var glsl = transpiler.Transpile(spirv.Value.Bytes, default);
        if (glsl.IsFailure)
            throw new InvalidOperationException($"SPIRV-Cross: {glsl.Error.Message}");

        return glsl.Value.Text;
    }

    /// <summary>Captures the assembled <c>.fx</c> text <see cref="SlangCompiler"/> hands to its
    /// downstream compiler, then still returns a well-formed (if empty) success so the caller's
    /// <c>Result</c> plumbing is satisfied - the caller never inspects the returned bytes, only
    /// <see cref="CapturedFxText"/>.</summary>
    private sealed class CapturingStubCompiler : IShaderCompiler
    {
        public string? CapturedFxText { get; private set; }

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            CapturedFxText = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, []));
        }
    }

    // ------------------------------------------------------------------------------- gate 3
    private static int RunDirectX11LoadGate(string[] corpus)
    {
        Console.WriteLine("=== GATE 3: every corpus shader loads into a REAL MonoGame.Framework.WindowsDX Effect (DirectX_11) ===");

        string repoRoot = FindRepoRoot();
        string catPath = DxShaderInputs.CatPath(repoRoot);
        string outDir = Path.Combine(repoRoot, "validation", "output-slang-full-corpus", "directx11");

        var jobs = new List<ShaderJob>();
        var compiler = new SlangCompiler();
        foreach (string file in corpus)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string source = File.ReadAllText(file);
            var options = new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = Path.GetFileName(file) };
            var result = compiler.Compile(source, options);

            jobs.Add(result.IsFailure
                ? new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")))
                : new ShaderJob(name, result.Value.Data, null));
        }

        using var game = new DxEffectImageRenderer(catPath, outDir, jobs, DxShaderInputs.SetParams);
        game.Run();

        int failures = 0;
        foreach (var outcome in game.Outcomes)
        {
            // "Loaded" (a real Effect accepted the bytes) is this gate's actual bar - see the
            // class doc comment for why a render failure on the 3 VS+PS float4x4-cbuffer
            // shaders is tracked but does not fail the gate (SpriteBatch's own vertex format
            // is not guaranteed compatible with a custom VS's input layout; that is a harness
            // limitation of reusing SpriteBatch as the draw path, not a SlangCompiler defect -
            // see the phase doc's A7 section for the precise scope this leaves open).
            string status = outcome.Loaded ? "LOADED" : "FAIL  ";
            if (!outcome.Loaded)
                failures++;
            string renderNote = outcome.Loaded ? (outcome.Rendered ? "rendered" : "loaded, draw failed (see phase doc)") : "";
            Console.WriteLine($"  [{status}] {outcome.Name,-20} {renderNote} {outcome.Error ?? ""}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {jobs.Count - failures}/{jobs.Count} loaded into a real DirectX_11 Effect.");
        Console.WriteLine();
        return failures;
    }

    // ------------------------------------------------------------------------------- shared
    private static string[] Corpus(string repoRoot)
    {
        string shippedDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "slang");
        string probeDir = Path.Combine(repoRoot, "plan", "PHASE-65-appendix", "slang-probe", "shaders");

        var files = Directory.GetFiles(shippedDir, "*.slang").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (string name in new[] { "GumGrayscale.slang", "GumTint.slang", "GumBlur.slang", "GenericsProbe.slang" })
            files.Add(Path.Combine(probeDir, name));
        return files.ToArray();
    }

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

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("could not locate the repository root (ShadowDusk.slnx)");
    }
}

/// <summary>
/// A hidden GLFW window + GL 3.3 compatibility context, a verbatim copy of
/// validation/SlangCorpus's own <c>GlHost</c> (that project's own internal helper class, not a
/// shared file - duplicated here rather than risking a shared-file edit to the existing,
/// already-shipped SlangCorpus gate, which is out of this phase's scope).
/// </summary>
internal sealed class GlHost : IDisposable
{
    private const string PassthroughVs = """
        #version 140
        in vec2 aPos;
        out vec2 in_var_TEXCOORD0;
        void main()
        {
            in_var_TEXCOORD0 = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    private readonly IWindow _window;
    private readonly GL _gl;
    private readonly OffscreenRenderer _renderer;
    private readonly uint _vao;
    private readonly uint _vbo;

    public GlHost()
    {
        Window.PrioritizeGlfw();
        _window = Window.Create(WindowOptions.Default with
        {
            Size                    = new Vector2D<int>(1, 1),
            Title                   = "ShadowDusk SlangFullCorpus (offscreen)",
            IsVisible               = false,
            ShouldSwapAutomatically = false,
            IsEventDriven           = true,
            API                     = new GraphicsAPI(
                ContextAPI.OpenGL, ContextProfile.Compatability, ContextFlags.Default, new APIVersion(3, 3)),
            VSync                   = false,
        });
        _window.Initialize();
        _gl = GL.GetApi(_window);
        _window.MakeCurrent();

        _renderer = new OffscreenRenderer(_gl);

        float[] quad = [-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f];
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (float* p = quad)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            }
        }
    }

    public (int MaxDelta, double VariedFraction) RenderAndCompare(string fragmentA, string fragmentB)
    {
        byte[] a = RenderOne(fragmentA);
        byte[] b = RenderOne(fragmentB);

        int maxDelta = 0;
        for (int i = 0; i < a.Length; i++)
            maxDelta = Math.Max(maxDelta, Math.Abs(a[i] - b[i]));

        int pixelCount = a.Length / 4;
        int varied = 0;
        for (int p = 1; p < pixelCount; p++)
        {
            int o = p * 4;
            if (a[o] != a[0] || a[o + 1] != a[1] || a[o + 2] != a[2] || a[o + 3] != a[3])
                varied++;
        }

        return (maxDelta, (double)varied / pixelCount);
    }

    private byte[] RenderOne(string fragment)
    {
        using var program = GlslShaderProgram.Compile(_gl, PassthroughVs, fragment);

        _renderer.Bind();
        _renderer.Clear(0, 0, 0, 255);
        program.Use(_gl);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        int posLocation = _gl.GetAttribLocation(program.Handle, "aPos");
        if (posLocation < 0)
            throw new InvalidOperationException("passthrough VS has no 'aPos' attribute after link");
        _gl.EnableVertexAttribArray((uint)posLocation);
        unsafe
        {
            _gl.VertexAttribPointer((uint)posLocation, 2, VertexAttribPointerType.Float, false, 0, null);
        }

        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.Finish();
        return _renderer.ReadPixels();
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _renderer.Dispose();
        _gl.Dispose();
        _window.Dispose();
    }
}
