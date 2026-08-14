// SlangCorpus = the Phase 61 cross-validation gate: proof the corpus is REAL Slang and that
// ShadowDusk means the same thing by it as Slang's own compiler does.
//
// THE QUESTION UNDER TEST (the owner's bar, 2026-08-14): "do we have 10-50 comparable real
// Slang-syntax shaders that work in slang (testable with slangc) AND work with ShadowDusk AND
// produce the same output image effects?" Two gates answer it:
//
//   GATE 1 - slangc validity. Every shader in tests/fixtures/shaders/slang/ must be accepted
//   by the REAL pinned slangc (per entry point, -target hlsl). This is what makes the corpus
//   "real Slang" rather than HLSL wearing a .slang extension. slangc here is a TEST-TIME
//   ORACLE, exactly as fxc and stock mgcb are elsewhere in validation/ - it is downloaded on
//   demand (SHA-256-verified), never shipped, and the product never invokes it.
//
//   GATE 2 - pixel equivalence. For the uniform-free procedural subset (no parameters, no
//   textures - so no name-plumbing on either side), render the SAME .slang two ways and
//   pixel-compare in a real GL context:
//       route A (ShadowDusk): SlangFrontend -> .fx -> FxPreParser/preprocessor -> DXC ->
//                             SPIR-V -> SPIRV-Cross GLSL
//       route B (Slang):      slangc -target hlsl -> DXC -> SPIR-V -> SPIRV-Cross GLSL
//   Identical DXC/SPIRV-Cross on both sides means any divergence is attributable to the one
//   thing that differs: whether ShadowDusk's reading of the Slang text matches slangc's.
//   That is precisely the silent-divergence risk of the HLSL-compatible-subset decision, and
//   this gate is what keeps it measured instead of assumed.
//
// Exits non-zero on any failure. First run downloads the oracle (~55 MB, cached after).

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.ImageTests.GlContext;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace ShadowDusk.Validation.SlangCorpus;

internal static class Program
{
    // The pinned Slang oracle. Bump BOTH together - the hash is the whole point of the pin.
    private const string SlangVersion = "2026.14.1";
    private const string SlangSha256 = "5ED0A59D650A0AF0ACA45D5DB4E083B3D8FB5CEA05748747DD95DFBE9C580658";

    /// <summary>Max per-channel delta route A vs route B. Identical DXC on both sides; slangc's
    /// HLSL re-emission may legally reassociate float math, so 0 would be dishonest.</summary>
    private const int Tolerance = 2;

    private static readonly Regex EntryAttribute = new(
        """\[\s*shader\s*\(\s*"(?<stage>[a-z]+)"\s*\)\s*\]\s*(?:\[[^\]]*\]\s*)*[^;{(]*?(?<name>[A-Za-z_]\w*)\s*\(""",
        RegexOptions.Compiled);

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
        string corpusDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "slang");
        string slangc = RestoreSlangOracle(repoRoot);

        string[] corpus = Directory.GetFiles(corpusDir, "*.slang").OrderBy(f => f).ToArray();
        Console.WriteLine($"[slang-corpus] oracle : {slangc}");
        Console.WriteLine($"[slang-corpus] corpus : {corpus.Length} shaders\n");

        if (corpus.Length < 17)
            throw new InvalidOperationException($"corpus has {corpus.Length} shaders; expected >= 17");

        int failures = 0;

        // ---------------------------------------------------------------- gate 1: slangc validity
        Console.WriteLine("=== GATE 1: every corpus shader is REAL Slang (accepted by pinned slangc) ===");
        var slangHlsl = new Dictionary<string, Dictionary<string, string>>();   // file -> entry -> HLSL
        foreach (string file in corpus)
        {
            string name = Path.GetFileName(file);
            var entries = ScanEntries(File.ReadAllText(file));
            if (entries.Count == 0)
            {
                failures++;
                Console.WriteLine($"  [FAIL] {name}: no [shader(...)] entries found");
                continue;
            }

            var perEntry = new Dictionary<string, string>();
            bool ok = true;
            foreach ((string entry, string stage) in entries)
            {
                (int exit, string stdout, string stderr) = RunProcess(slangc,
                    [file, "-target", "hlsl", "-entry", entry, "-stage", stage]);
                if (exit != 0)
                {
                    failures++;
                    ok = false;
                    Console.WriteLine($"  [FAIL] {name} ({entry}/{stage}): slangc rejected it:\n{Indent(stderr)}");
                    break;
                }
                perEntry[entry] = stdout;
            }

            if (ok)
            {
                slangHlsl[name] = perEntry;
                Console.WriteLine($"  [OK  ] {name} ({string.Join(", ", entries.Select(e => e.Entry))})");
            }
        }

        // -------------------------------------------------- gate 2: pixel equivalence (procedural)
        Console.WriteLine("\n=== GATE 2: ShadowDusk's route renders the SAME pixels as slangc's HLSL route ===");
        string[] procedural = corpus.Where(f =>
        {
            string text = File.ReadAllText(f);
            var entries = ScanEntries(text);
            return entries.Count == 1 && entries[0].Stage == "fragment"
                   && !text.Contains("cbuffer") && !text.Contains("Texture2D");
        }).ToArray();

        Console.WriteLine($"  (uniform-free procedural subset: {procedural.Length} shaders)");
        if (procedural.Length < 8)
            throw new InvalidOperationException(
                $"only {procedural.Length} uniform-free procedural shaders; the render-equivalence gate needs >= 8");

        using var gl = new GlHost();
        using var dxc = new DxcShaderCompiler();
        var transpiler = new SpirvCrossGlslTranspiler();

        foreach (string file in procedural)
        {
            string name = Path.GetFileName(file);
            if (!slangHlsl.TryGetValue(name, out var perEntry))
            {
                Console.WriteLine($"  [SKIP] {name}: failed gate 1");
                continue;
            }

            string source = File.ReadAllText(file);
            string entry = ScanEntries(source)[0].Entry;

            try
            {
                // Route A: ShadowDusk's ACTUAL frontend output, through the pipeline's own stages.
                string glslA = PixelGlslFromFx(ConvertViaShadowDusk(source, name), entry, name, dxc, transpiler);

                // Route B: slangc's own HLSL emission for the same text, same downstream stages.
                string glslB = PixelGlslFromHlsl(perEntry[entry], entry, name + " (slangc HLSL)", dxc, transpiler);

                (int maxDelta, double variedFraction) = gl.RenderAndCompare(glslA, glslB);

                // Non-degeneracy: two equal CONSTANT screens prove nothing (a broken link or
                // a black clear on both sides would "match" perfectly). The image must vary
                // spatially - a hard-edged checkerboard varies plenty while using only two
                // byte values, so the metric is pixels-that-differ-from-the-first, not
                // palette size.
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
        Console.WriteLine(failures == 0
            ? $"Slang corpus gate: PASSED ({corpus.Length} slangc-validated, {procedural.Length} render-equivalent)"
            : $"Slang corpus gate: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Route A's front half: the real product frontend, then the real parse/preprocess.</summary>
    private static string ConvertViaShadowDusk(string slangSource, string name)
    {
        var converted = SlangFrontend.ConvertToFx(slangSource, new SlangConvertOptions
        {
            SourceName = name,
            TechniqueName = Path.GetFileNameWithoutExtension(name),
        });
        if (converted.IsFailure)
        {
            throw new InvalidOperationException("frontend: " +
                string.Join(" | ", converted.Error.Select(e => $"{e.Code}: {e.Message}")));
        }
        return converted.Value.FxText;
    }

    private static string PixelGlslFromFx(
        string fxText, string entry, string name, DxcShaderCompiler dxc, SpirvCrossGlslTranspiler transpiler)
    {
        var parse = FxPreParser.Parse(fxText, name);
        if (parse.IsFailure)
            throw new InvalidOperationException($"parse: {parse.Error.Message}");

        var pre = new Preprocessor().Flatten(
            parse.Value.StrippedHlsl, name,
            PlatformMacros.For(PlatformTarget.OpenGL), new FileSystemIncludeResolver(), []);
        if (pre.IsFailure)
            throw new InvalidOperationException($"preprocess: {pre.Error.Message}");

        return PixelGlslFromHlsl(pre.Value.Text, entry, name, dxc, transpiler);
    }

    private static string PixelGlslFromHlsl(
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

    // ------------------------------------------------------------------ slangc oracle restore
    private static string RestoreSlangOracle(string repoRoot)
    {
        string oracleDir = Path.Combine(repoRoot, "validation", "SlangCorpus", ".slang-oracle");
        string slangc = Path.Combine(oracleDir, "bin", "slangc.exe");
        if (File.Exists(slangc))
            return slangc;

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "the pinned slangc oracle download is windows-x86_64; run this gate on the Windows gate box");
        }

        string url = $"https://github.com/shader-slang/slang/releases/download/v{SlangVersion}/slang-{SlangVersion}-windows-x86_64.zip";
        string zip = Path.Combine(Path.GetTempPath(), $"slang-oracle-{Guid.NewGuid():N}.zip");

        Console.WriteLine($"[slang-corpus] downloading the slangc oracle v{SlangVersion} (first run only)...");
        using (var http = new HttpClient())
        using (var download = http.GetStreamAsync(url).GetAwaiter().GetResult())
        using (var outFile = File.Create(zip))
        {
            download.CopyTo(outFile);
        }

        // Verify BEFORE extracting - an unverified binary is what the pin exists to prevent.
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        if (!actual.Equals(SlangSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidOperationException(
                $"slang oracle SHA-256 mismatch: expected {SlangSha256}, got {actual} - refusing to extract");
        }

        Directory.CreateDirectory(oracleDir);
        ZipFile.ExtractToDirectory(zip, oracleDir, overwriteFiles: true);
        File.Delete(zip);

        if (!File.Exists(slangc))
            throw new FileNotFoundException($"slangc.exe not found after extraction: {slangc}");
        return slangc;
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
        string.Join('\n', text.Split('\n').Take(8).Select(l => "         " + l));

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
/// A hidden GLFW window + GL 3.3 compatibility context (the ImageTests fixture's measured
/// recipe) with a fullscreen-quad renderer over the reused <see cref="OffscreenRenderer"/>.
/// Both routes' fragment shaders draw with the SAME hand-written passthrough vertex shader,
/// whose outputs are named to match SPIRV-Cross's semantic-derived varying names — so linking
/// works identically for both and the only variable is the fragment shader under test.
/// </summary>
internal sealed class GlHost : IDisposable
{
    // SPIRV-Cross names PS inputs from HLSL SEMANTICS (in_var_TEXCOORD0), so a VS whose out
    // uses that exact name links against every corpus PS. SV_Position input becomes
    // gl_FragCoord and needs nothing from us.
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
            Title                   = "ShadowDusk SlangCorpus (offscreen)",
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

        // One fullscreen triangle-strip quad shared by every draw.
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

    /// <summary>
    /// Renders both fragment shaders and returns (max channel delta, fraction of A's pixels
    /// that differ from A's first pixel — the spatial-variation measure the degeneracy check
    /// uses).
    /// </summary>
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
