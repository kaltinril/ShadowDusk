// Phase 43 (F1/F1b/F2/F9) rung-3/4 validation: the Phase 43 writer-fidelity corpus
// (pass render states, annotations, baked sampler states) must LOAD in a REAL
// MonoGame 3.8.2 DesktopGL Effect, and — where the fixture is PS-only — render
// pixel-equivalent to the same .fx compiled by the real mgfxc (the committed
// golden), through the identical SpriteBatch path.
//
// Before Phase 43, ANY fixture with a pass state, an annotation, or a baked
// sampler state desynced MonoGame's reader and threw at new Effect(); the load
// arm of this harness is precisely that regression gate.
//
//   dotnet run -c Release --project validation/StateFidelity
//
// Exit 0 iff every row passes (candidate loads; golden loads; PS rows differ by
// at most --tolerance (default 4, the Phase 18 bar) per channel).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

int tolerance = 4;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot  = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string catPath   = ShaderInputs.CatPath(repoRoot);
string outDir    = Path.Combine(repoRoot, "validation", "output-state");

// (Name, RendersViaSpriteBatch, HasGolden). VS-bearing fixtures are load-only here
// (SpriteBatch is the PS-only path); AnnotatedTechnique has no golden because
// mgfxc 3.8.2's grammar cannot parse technique/pass annotations at all.
var rows = new (string Name, bool Renders, bool HasGolden)[]
{
    ("StateBlendAdditive", true,  true),
    ("StateDepthStencil",  true,  true),
    ("StateRasterizer",    true,  true),
    ("SamplerStatesFull",  true,  true),
    ("render-states",      false, true),
    ("annotations",        false, true),
    ("AnnotatedTechnique", false, false),
};

Console.WriteLine($"[state] cat: {catPath}");
Console.WriteLine($"[state] out: {outDir}  tolerance: {tolerance}\n");

// ---- Compile every candidate with ShadowDusk (OpenGL, in memory). ----
var compiler = new EffectCompiler();
var jobs = new List<StateRow>();
foreach (var (name, renders, hasGolden) in rows)
{
    string fxPath = Path.Combine(shaderDir, name + ".fx");
    string src = await File.ReadAllTextAsync(fxPath);
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fxPath,
    });

    byte[]? candidate = result.IsSuccess ? result.Value.Data : null;
    string? compileError = result.IsFailure
        ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
        : null;

    string goldenPath = Path.Combine(goldenDir, name + ".mgfx");
    byte[]? golden = hasGolden && File.Exists(goldenPath)
        ? await File.ReadAllBytesAsync(goldenPath)
        : null;
    if (hasGolden && golden is null)
        compileError = (compileError is null ? "" : compileError + " | ") +
                       $"golden missing: {goldenPath}";

    jobs.Add(new StateRow(name, renders, hasGolden, candidate, golden, compileError));
}

foreach (var j in jobs)
    Console.WriteLine($"  [{(j.CandidateBytes is null ? "FAIL" : "OK  ")}] compile {j.Name,-20} " +
                      $"{(j.CompileError ?? $"{j.CandidateBytes!.Length} bytes")}");
Console.WriteLine();

using var game = new StateFidelityGame(catPath, outDir, jobs, tolerance);
game.Run();

int ok = 0;
Console.WriteLine("\n[state] results:");
foreach (var o in game.Outcomes)
{
    bool pass = o.Pass;
    if (pass) ok++;
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {o.Name,-20} {o.Detail}");
}
Console.WriteLine($"\n[state] {ok}/{game.Outcomes.Count} rows passed.");
return ok == game.Outcomes.Count ? 0 : 1;

internal sealed record StateRow(
    string Name, bool Renders, bool HasGolden,
    byte[]? CandidateBytes, byte[]? GoldenBytes, string? CompileError);

internal sealed record StateOutcome(string Name, bool Pass, string Detail);

/// <summary>
/// One real MonoGame 3.8.2 DesktopGL device: loads each row's candidate (ShadowDusk)
/// and golden (mgfxc) bytes into real <see cref="Effect"/>s, renders the PS-only rows
/// through the identical SpriteBatch path, and pixel-compares the two arms in memory.
/// </summary>
internal sealed class StateFidelityGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<StateRow> _rows;
    private readonly int _tolerance;
    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<StateOutcome> Outcomes { get; } = new();

    public StateFidelityGame(string catPath, string outDir, IReadOnlyList<StateRow> rows, int tolerance)
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
        Window.Title = "ShadowDusk Phase 43 state-fidelity validation (headless)";
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

        foreach (StateRow row in _rows)
            Outcomes.Add(RunRow(row));

        _done = true;
        Exit();
    }

    private StateOutcome RunRow(StateRow row)
    {
        if (row.CandidateBytes is null || (row.HasGolden && row.GoldenBytes is null))
            return new StateOutcome(row.Name, false, $"compile/golden failure: {row.CompileError}");

        // THE load bar: real MonoGame 3.8.2 Effect must accept ShadowDusk's bytes.
        Effect candidate;
        try { candidate = new Effect(GraphicsDevice, row.CandidateBytes); }
        catch (Exception ex)
        {
            return new StateOutcome(row.Name, false, $"candidate new Effect() threw: {ex.Message}");
        }

        Effect? golden = null;
        if (row.GoldenBytes is not null)
        {
            try { golden = new Effect(GraphicsDevice, row.GoldenBytes); }
            catch (Exception ex)
            {
                return new StateOutcome(row.Name, false, $"GOLDEN new Effect() threw (control failure): {ex.Message}");
            }
        }

        if (!row.Renders || golden is null)
            return new StateOutcome(row.Name, true,
                golden is null ? "candidate loaded (no golden — load-only row)"
                               : "candidate + golden loaded (load-only row)");

        // Render both arms through the IDENTICAL path and compare in memory.
        Color[]? candPixels = RenderArm(candidate, row.Name + ".candidate", out string? candErr);
        if (candPixels is null)
            return new StateOutcome(row.Name, false, $"candidate render failed: {candErr}");
        Color[]? goldPixels = RenderArm(golden, row.Name + ".golden", out string? goldErr);
        if (goldPixels is null)
            return new StateOutcome(row.Name, false, $"golden render failed: {goldErr}");

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
        return new StateOutcome(row.Name, pass,
            $"rendered both arms; diffPixels={diffCount} maxDelta={maxDelta} (tolerance {_tolerance})");
    }

    private Color[]? RenderArm(Effect effect, string pngStem, out string? error)
    {
        error = null;
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
        try
        {
            var dest = new Rectangle(0, 0, w, h);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil,
                Color.Black, 1f, 1);

            // Prime SpriteBatch's sprite VS (PS-only effects), as ShaderViewer does.
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            effect.Parameters["SpriteTexture"]?.SetValue(_cat);
            effect.Parameters["DetailTexture"]?.SetValue(_cat);

            // NOTE deliberately NOT Opaque: the in-pass blend/depth/stencil states
            // under test override the device state at EffectPass.Apply — both arms
            // get the identical starting state, so any difference is the .mgfx's.
            _sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone, effect);
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
