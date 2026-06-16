// Phase 44 D - KNI v4.02 DIRECTX (WinForms.DX11) load + render validation.
//
// Compiles the SM5 PS-only corpus with ShadowDusk's UNCHANGED EffectCompiler
// (DirectX target -> DXBC SM5 in an MGFX v10 container), then loads BOTH those bytes
// (candidate) AND the committed mgfxc DirectX goldens (tests/fixtures/golden/
// DirectX_11/*.mgfx) into a REAL KNI (nkast) DX11 Effect on the WinForms.DX11 desktop
// backend, renders the standard cat-corpus scene for each through the IDENTICAL
// SpriteBatch path, and pixel-compares the two arms in process.
//
// This is the DirectX analogue of validation/KniDesktopGL (which proved the GL cell):
// it closes the matrix's KNI DirectX cell by showing ShadowDusk's shipping DX output
// loads + renders pixel-equivalent to mgfxc's DX output on a CURRENT KNI runtime
// (v4.2.9001), same backend (DX11), same scene.
//
//   dotnet run -c Release --project validation/KniWinFormsDX   (needs a real DX11 desktop)
//
// Exit 0 iff every row passes: candidate loads, golden loads (the control), and the
// two renders differ by at most --tolerance (default 4, the Phase 18 DX bar) per channel.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Dx;
// UseWindowsForms pulls in System.Drawing via implicit usings; pin Color to XNA's.
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int tolerance = 4;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
                tolerance = t;

        // ---- Runtime-integrity guard ------------------------------------------------
        // Prove we are actually running on KNI, not MonoGame. KNI ships its Game type in
        // an assembly named "Xna.Framework.Game"; MonoGame's is "MonoGame.Framework". If a
        // stray MonoGame assembly ever shadowed KNI's, this harness would silently validate
        // the wrong runtime; fail loudly instead. KNI v4.02 stamps version 4.2.9001.x.
        AssemblyName xna = typeof(Game).Assembly.GetName();
        Console.WriteLine($"[kni-dx] XNA implementation: {xna.Name} {xna.Version}");
        bool isKni = xna.Name is not null
            && xna.Name.StartsWith("Xna.Framework", StringComparison.OrdinalIgnoreCase)
            && !xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
        if (!isKni)
        {
            Console.WriteLine($"[kni-dx] verdict: FAIL - expected the KNI (nkast 'Xna.Framework.*') runtime, " +
                              $"got '{xna.Name}'. This harness must render on KNI DX11; aborting so we cannot " +
                              "mislabel a MonoGame render as KNI.");
            return 2;
        }

        string repoRoot  = DxShaderInputs.FindRepoRoot();
        string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11");
        string catPath   = DxShaderInputs.CatPath(repoRoot);
        string outDir    = Path.Combine(repoRoot, "validation", "output-dx", "kni");

        Console.WriteLine($"[kni-dx] cat: {catPath}");
        Console.WriteLine($"[kni-dx] shaders: {shaderDir}");
        Console.WriteLine($"[kni-dx] goldens: {goldenDir}");
        Console.WriteLine($"[kni-dx] out: {outDir}  tolerance: {tolerance}\n");

        // ---- Compile every candidate with ShadowDusk (DirectX, synchronously). ----
        // Phase 42 sync Compile(): on desktop no InitializeAsync warm-up is needed, so the
        // STA Main stays synchronous (no async-over-[STAThread] dance).
        var compiler = new EffectCompiler();
        var rows = new List<DxRow>();
        foreach (string name in DxShaderInputs.ShaderNames)
        {
            string fxPath = Path.Combine(shaderDir, name + ".fx");
            string? compileError = null;
            byte[]? candidate = null;
            if (!File.Exists(fxPath))
            {
                compileError = $".fx not found: {fxPath}";
            }
            else
            {
                string src = File.ReadAllText(fxPath);
                var result = compiler.Compile(src, new CompilerOptions
                {
                    Target = PlatformTarget.DirectX,
                    IncludeResolver = new FileSystemIncludeResolver(),
                    SourceFileName = fxPath,
                });
                if (result.IsSuccess) candidate = result.Value.Data;
                else compileError = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
            }

            string goldenPath = Path.Combine(goldenDir, name + ".mgfx");
            byte[]? golden = File.Exists(goldenPath) ? File.ReadAllBytes(goldenPath) : null;
            if (golden is null)
                compileError = (compileError is null ? "" : compileError + " | ") +
                               $"golden missing: {goldenPath}";

            rows.Add(new DxRow(name, candidate, golden, compileError));
        }

        Console.WriteLine("[kni-dx] compile results:");
        foreach (var r in rows)
            Console.WriteLine($"  [{(r.CandidateBytes is null ? "FAIL" : "OK  ")}] {r.Name,-12} " +
                              $"{(r.CompileError ?? $"{r.CandidateBytes!.Length} bytes")}");
        Console.WriteLine();

        using var game = new KniDxCompareGame(catPath, outDir, rows, tolerance);
        game.Run();

        int ok = 0;
        Console.WriteLine("\n[kni-dx] load + render results (real KNI WinForms.DX11):");
        foreach (var o in game.Outcomes)
        {
            if (o.Pass) ok++;
            Console.WriteLine($"  [{(o.Pass ? "PASS" : "FAIL")}] {o.Name,-12} {o.Detail}");
        }
        Console.WriteLine($"\n[kni-dx] {ok}/{game.Outcomes.Count} loaded + rendered + matched mgfxc in real KNI v{xna.Version}.");
        return ok == game.Outcomes.Count ? 0 : 1;
    }
}

internal sealed record DxRow(string Name, byte[]? CandidateBytes, byte[]? GoldenBytes, string? CompileError);

internal sealed record DxOutcome(string Name, bool Pass, string Detail);

/// <summary>
/// One real KNI WinForms.DX11 device: loads each row's candidate (ShadowDusk) and golden
/// (mgfxc) DX bytes into real <see cref="Effect"/>s, renders both through the identical
/// SpriteBatch path, and pixel-compares the two arms in memory. Mirrors Phase 43's
/// StateFidelityGame, on the DX backend under KNI. Headless note: WinForms.DX11 hosts the
/// swap chain in a Win32/WinForms window; we render a single frame to an offscreen
/// RenderTarget2D then Exit, so the window is created but never meaningfully shown.
/// </summary>
internal sealed class KniDxCompareGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<DxRow> _rows;
    private readonly int _tolerance;
    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<DxOutcome> Outcomes { get; } = new();

    public KniDxCompareGame(string catPath, string outDir, IReadOnlyList<DxRow> rows, int tolerance)
    {
        _catPath = catPath;
        _outDir = outDir;
        _rows = rows;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Phase 44 KNI DX11 validation (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        GraphicsDevice.Clear(Color.Black);

        foreach (DxRow row in _rows)
            Outcomes.Add(RunRow(row));

        _done = true;
        Exit();
    }

    private DxOutcome RunRow(DxRow row)
    {
        if (row.CandidateBytes is null || row.GoldenBytes is null)
            return new DxOutcome(row.Name, false, $"compile/golden failure: {row.CompileError}");

        // THE load bar: a real KNI DX11 Effect must accept ShadowDusk's bytes.
        Effect candidate;
        try { candidate = new Effect(GraphicsDevice, row.CandidateBytes); }
        catch (Exception ex)
        {
            return new DxOutcome(row.Name, false, $"candidate new Effect() threw: {ex.GetType().Name}: {ex.Message}");
        }

        Effect golden;
        try { golden = new Effect(GraphicsDevice, row.GoldenBytes); }
        catch (Exception ex)
        {
            candidate.Dispose();
            return new DxOutcome(row.Name, false, $"GOLDEN new Effect() threw (control failure): {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Color[]? candPixels = RenderArm(candidate, row.Name + ".candidate", out string? candErr);
            if (candPixels is null)
                return new DxOutcome(row.Name, false, $"candidate render failed: {candErr}");
            Color[]? goldPixels = RenderArm(golden, row.Name + ".golden", out string? goldErr);
            if (goldPixels is null)
                return new DxOutcome(row.Name, false, $"golden render failed: {goldErr}");

            int maxDelta = 0, diffCount = 0;
            for (int i = 0; i < candPixels.Length; i++)
            {
                int d = Math.Max(
                    Math.Max(Math.Abs(candPixels[i].R - goldPixels[i].R),
                             Math.Abs(candPixels[i].G - goldPixels[i].G)),
                    Math.Max(Math.Abs(candPixels[i].B - goldPixels[i].B),
                             Math.Abs(candPixels[i].A - goldPixels[i].A)));
                if (d > 0) diffCount++;
                if (d > maxDelta) maxDelta = d;
            }

            bool pass = maxDelta <= _tolerance;
            return new DxOutcome(row.Name, pass,
                $"loaded both arms; diffPixels={diffCount} maxDelta={maxDelta} (tolerance {_tolerance})");
        }
        finally
        {
            candidate.Dispose();
            golden.Dispose();
        }
    }

    private Color[]? RenderArm(Effect effect, string pngStem, out string? error)
    {
        error = null;
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            var dest = new Rectangle(0, 0, w, h);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            // Prime SpriteBatch's sprite vertex shader (PS-only effects need a VS).
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            try { DxShaderInputs.SetParams(effect, _cat); }
            catch { /* a missing/typed param on one side must not abort the row */ }

            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, null, null, effect);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[w * h];
            rt.GetData(pixels);

            string png = Path.Combine(_outDir, pngStem + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            return pixels;
        }
        catch (Exception ex)
        {
            try { _sb.End(); } catch { /* may not be in a batch */ }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
