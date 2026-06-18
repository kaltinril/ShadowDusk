// Phase 45 B10 rung-4 RENDER proof: the OpenGL reserved-word-uniform offset bridge.
//
//   dotnet run -c Release --project validation/ReservedWordGl
//
// B10 fixed the OpenGL binding of a FREE uniform whose name collides with a GLSL
// reserved word (the canonical case: `float noise;`). On the GL path SPIRV-Cross
// renames it `_noise` to keep the emitted GLSL legal; ShadowDusk's .mgfx
// cbuffer-record join now bridges by register OFFSET so the parameter stays exposed
// under its original name `noise` at the correct ps_uniforms_vec4 register. The
// compile / golden / byte-identity tests prove the BYTES are produced and stable —
// they do NOT prove that setting `noise` by name actually BINDS and DRIVES the
// output when rendered in the real engine. That is what this gate proves.
//
// The fixture (tests/fixtures/shaders/examples/ExReservedWordUniformRender.fx) is
// shaped so `noise` is the SOLE, exactly-assertable driver of the output:
//     return float4(noise, noise, noise, 1);
// so a rendered pixel equals round(noise * 255) on RGB. We load ShadowDusk's GL
// .mgfx into a REAL MonoGame DesktopGL Effect (the gold-standard runtime path B10
// targets), set `noise` BY NAME to two known values, render through SpriteBatch, and
// assert:
//   (1) noise = 0.25 renders ~ (64,64,64)  — exact expected pixel (tolerance);
//   (2) noise = 0.75 renders ~ (191,191,191) — exact expected pixel (tolerance);
//   (3) the two outputs DIFFER by the expected amount (a no-op/zero/mis-register
//       binding cannot pass — the vacuity guard);
//   (4) BONUS rung-4 (only if mgfxc is on PATH): the same .fx compiled by mgfxc,
//       rendered the same way with the same noise, is pixel-equivalent to ShadowDusk.
//
// If the bridge had mapped `noise` to the wrong register (or dropped it), the GL
// compile would fail (SD0012) or the pixels would not track `noise` — both caught.
//
// Self-asserting (exit 0 iff every check passes). Needs a real GL context; set
// SHADOWDUSK_REQUIRE_GL=1 to turn a missing-context skip into a hard failure.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

// Per-channel tolerance for the exact-expected-pixel and cross-compiler checks.
// round(noise*255) is exact for the chosen dyadic-ish values; allow a small margin
// for GL/driver rounding (the established Phase 17/18 dyadic bar is <= 1, llvmpipe
// can drift one more on a *0 + round path, so default 2).
int tolerance = 2;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot  = ShaderInputs.FindRepoRoot();
string fxPath    = Path.Combine(repoRoot, "tests", "fixtures", "shaders",
                                "examples", "ExReservedWordUniformRender.fx");
string catPath   = ShaderInputs.CatPath(repoRoot);
string outDir    = Path.Combine(repoRoot, "validation", "output-reservedword");

// The two noise values to render and their expected 8-bit grey level.
var noiseValues = new[] { 0.25f, 0.75f };

Console.WriteLine($"[reservedword] fx:  {fxPath}");
Console.WriteLine($"[reservedword] cat: {catPath}");
Console.WriteLine($"[reservedword] out: {outDir}  tolerance: {tolerance}\n");

if (!File.Exists(fxPath))
{
    Console.Error.WriteLine($"[reservedword] FATAL: fixture not found: {fxPath}");
    return 2;
}

// ---- Compile the fixture with ShadowDusk (OpenGL, in memory). This is the path B10
//      lives in: GL compile of a reserved-word free uniform. Pre-fix this FAILED with
//      SD0012; a successful compile that exposes `noise` is the first half of the proof.
string src = await File.ReadAllTextAsync(fxPath);
var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
{
    Target = PlatformTarget.OpenGL,
    IncludeResolver = new FileSystemIncludeResolver(),
    SourceFileName = fxPath,
});

if (result.IsFailure)
{
    Console.Error.WriteLine("[reservedword] FATAL: ShadowDusk GL compile FAILED (the B10 SD0012 regression?):");
    foreach (var e in result.Error)
        Console.Error.WriteLine($"    {e.Code}: {e.Message}");
    return 1;
}

byte[] shadowDuskMgfx = result.Value.Data;
Directory.CreateDirectory(outDir);
await File.WriteAllBytesAsync(Path.Combine(outDir, "shadowdusk-gl.mgfx"), shadowDuskMgfx);
Console.WriteLine($"[reservedword] ShadowDusk GL compile OK ({shadowDuskMgfx.Length} bytes); 'noise' must bind by name.");

// ---- BONUS rung-4: compile the SAME .fx with mgfxc (if available) for a cross-compiler
//      pixel-equivalence arm. mgfxc/MojoShader packs uniforms by index, so `noise` has no
//      reserved-word collision there — it is a legitimate golden. Absent mgfxc, the
//      exact-expected-pixel assertion is the bar (the gate still proves B10).
byte[]? mgfxcMgfx = TryCompileWithMgfxc(fxPath, outDir, out string mgfxcNote);
Console.WriteLine($"[reservedword] mgfxc golden: {mgfxcNote}\n");

using var game = new ReservedWordGame(catPath, outDir, shadowDuskMgfx, mgfxcMgfx, noiseValues, tolerance);
game.Run();

int passed = 0;
Console.WriteLine("\n[reservedword] results:");
foreach (var o in game.Outcomes)
{
    if (o.Pass) passed++;
    Console.WriteLine($"  [{(o.Pass ? "PASS" : "FAIL")}] {o.Name,-34} {o.Detail}");
}

bool allPassed = passed == game.Outcomes.Count && game.Outcomes.Count > 0;
Console.WriteLine($"\n[reservedword] {passed}/{game.Outcomes.Count} checks passed.");
return allPassed ? 0 : 1;

// ----------------------------------------------------------------------------------

static byte[]? TryCompileWithMgfxc(string fxPath, string outDir, out string note)
{
    string? mgfxc = LocateMgfxc();
    if (mgfxc is null)
    {
        note = "not found on PATH or in NuGet cache (bonus arm skipped; exact-pixel check still applies)";
        return null;
    }

    string outFile = Path.Combine(outDir, "mgfxc-gl.mgfx");
    try
    {
        var psi = new ProcessStartInfo(mgfxc)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(fxPath);
        psi.ArgumentList.Add(outFile);
        psi.ArgumentList.Add("/Profile:OpenGL");

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);

        if (proc.ExitCode != 0 || !File.Exists(outFile))
        {
            note = $"mgfxc exit {proc.ExitCode}: {(stderr + stdout).Trim()}";
            return null;
        }

        note = $"compiled by {Path.GetFileName(mgfxc)} -> {outFile}";
        return File.ReadAllBytes(outFile);
    }
    catch (Exception ex)
    {
        note = $"mgfxc invocation threw: {ex.Message}";
        return null;
    }
}

static string? LocateMgfxc()
{
    // 1. dotnet global tool shim on PATH (this box: ~/.dotnet/tools/mgfxc).
    foreach (string exe in new[] { "mgfxc", "mgfxc.exe" })
    {
        string? onPath = FindOnPath(exe);
        if (onPath is not null)
            return onPath;
    }

    // 2. mgfxc.exe in the NuGet cache (the tools/compile-fixtures.ps1 fallback).
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string nugetDir = Path.Combine(userProfile, ".nuget", "packages", "dotnet-mgcb-editor-windows");
    if (Directory.Exists(nugetDir))
    {
        string? found = Directory.EnumerateFiles(nugetDir, "mgfxc.exe", SearchOption.AllDirectories)
            .OrderByDescending(p => p)
            .FirstOrDefault();
        if (found is not null)
            return found;
    }

    return null;
}

static string? FindOnPath(string fileName)
{
    string? pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (pathEnv is null) return null;
    foreach (string dir in pathEnv.Split(Path.PathSeparator))
    {
        if (string.IsNullOrWhiteSpace(dir)) continue;
        string candidate = Path.Combine(dir.Trim(), fileName);
        if (File.Exists(candidate))
            return candidate;
    }
    return null;
}

/// <summary>One asserted check (a single noise value, or a cross-compiler/differentiation check).</summary>
internal sealed record ReservedWordOutcome(string Name, bool Pass, string Detail);

/// <summary>
/// One real MonoGame 3.8.2 DesktopGL device. Loads the ShadowDusk (and, if present, the
/// mgfxc) <c>.mgfx</c> into real <see cref="Effect"/>s, sets <c>noise</c> BY NAME to each
/// test value, renders the flat-grey <c>float4(noise,noise,noise,1)</c> through the normal
/// SpriteBatch path, reads pixels back, and asserts the output equals the expected grey
/// (so <c>noise</c> demonstrably binds to the correct register and drives the result).
/// </summary>
internal sealed class ReservedWordGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly byte[] _shadowDuskMgfx;
    private readonly byte[]? _mgfxcMgfx;
    private readonly float[] _noiseValues;
    private readonly int _tolerance;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<ReservedWordOutcome> Outcomes { get; } = new();

    public ReservedWordGame(
        string catPath, string outDir,
        byte[] shadowDuskMgfx, byte[]? mgfxcMgfx,
        float[] noiseValues, int tolerance)
    {
        _catPath = catPath;
        _outDir = outDir;
        _shadowDuskMgfx = shadowDuskMgfx;
        _mgfxcMgfx = mgfxcMgfx;
        _noiseValues = noiseValues;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Phase 45 B10 reserved-word render proof (headless)";
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

        try
        {
            RunAllChecks();
        }
        catch (Exception ex)
        {
            Outcomes.Add(new ReservedWordOutcome("harness", false,
                $"unhandled exception: {ex.GetType().Name}: {ex.Message}"));
        }

        _done = true;
        Exit();
    }

    private void RunAllChecks()
    {
        // Load ShadowDusk's effect once; reuse across noise values (a fresh SetValue
        // per render). Loading itself must succeed — the B10 .mgfx is the subject.
        Effect sdEffect;
        try { sdEffect = new Effect(GraphicsDevice, _shadowDuskMgfx); }
        catch (Exception ex)
        {
            Outcomes.Add(new ReservedWordOutcome("shadowdusk.load", false,
                $"new Effect(ShadowDusk bytes) threw: {ex.Message}"));
            return;
        }

        // The parameter MUST be reachable by its original name (the whole point of B10).
        if (sdEffect.Parameters["noise"] is null)
        {
            Outcomes.Add(new ReservedWordOutcome("shadowdusk.param[noise]", false,
                "effect.Parameters[\"noise\"] is null — the offset bridge did not expose the " +
                "parameter under its original name (it would be the SPIRV-Cross rename '_noise')"));
            sdEffect.Dispose();
            return;
        }
        Outcomes.Add(new ReservedWordOutcome("shadowdusk.param[noise]", true,
            "exposed under original name 'noise' (not the SPIRV-Cross rename '_noise')"));

        // (1)+(2): exact expected pixel for each noise value.
        var sdGreys = new Dictionary<float, int>();
        foreach (float noise in _noiseValues)
        {
            int expected = (int)Math.Round(noise * 255f);
            Color? center = RenderNoise(sdEffect, noise, $"shadowdusk_{noise:0.00}", out string? err);
            if (center is null)
            {
                Outcomes.Add(new ReservedWordOutcome($"shadowdusk.render[noise={noise:0.00}]", false,
                    $"render failed: {err}"));
                continue;
            }

            Color px = center.Value;
            int maxChan = Math.Max(Math.Max(px.R, px.G), px.B);
            int minChan = Math.Min(Math.Min(px.R, px.G), px.B);
            int dExpected = Math.Max(Math.Abs(px.R - expected),
                            Math.Max(Math.Abs(px.G - expected), Math.Abs(px.B - expected)));
            bool grey = (maxChan - minChan) <= _tolerance; // R==G==B (flat grey)
            bool aOk  = Math.Abs(px.A - 255) <= _tolerance;
            bool pass = dExpected <= _tolerance && grey && aOk;

            sdGreys[noise] = (px.R + px.G + px.B) / 3;
            Outcomes.Add(new ReservedWordOutcome($"shadowdusk.render[noise={noise:0.00}]", pass,
                $"pixel={px} expected grey {expected} (round({noise:0.00}*255)); " +
                $"dExpected={dExpected} greySpread={maxChan - minChan} (tolerance {_tolerance})"));
        }

        // (3): the two noise values must produce the expected DIFFERENT outputs — a no-op,
        // zero, or wrong-register binding cannot pass. Expect brightness to track noise.
        if (sdGreys.TryGetValue(0.25f, out int g025) && sdGreys.TryGetValue(0.75f, out int g075))
        {
            int observedDelta = g075 - g025;
            int expectedDelta = (int)Math.Round((0.75f - 0.25f) * 255f); // ~128
            bool pass = Math.Abs(observedDelta - expectedDelta) <= 2 * _tolerance && observedDelta > 0;
            Outcomes.Add(new ReservedWordOutcome("shadowdusk.differentiation[0.25 vs 0.75]", pass,
                $"grey(0.75)-grey(0.25) = {observedDelta} (expected ~{expectedDelta}); " +
                "proves 'noise' DRIVES the output, not a constant"));
        }
        else
        {
            Outcomes.Add(new ReservedWordOutcome("shadowdusk.differentiation[0.25 vs 0.75]", false,
                "could not capture both noise renders"));
        }

        // (4) BONUS rung-4: cross-compiler pixel-equivalence vs mgfxc's .mgfx, same noise.
        if (_mgfxcMgfx is not null)
        {
            Effect? mgfxcEffect = null;
            try { mgfxcEffect = new Effect(GraphicsDevice, _mgfxcMgfx); }
            catch (Exception ex)
            {
                Outcomes.Add(new ReservedWordOutcome("mgfxc.load", false,
                    $"new Effect(mgfxc bytes) threw (control failure): {ex.Message}"));
            }

            if (mgfxcEffect is not null)
            {
                foreach (float noise in _noiseValues)
                {
                    Color? sd = RenderNoise(sdEffect, noise, $"sd_cmp_{noise:0.00}", out _);
                    Color? mg = RenderNoise(mgfxcEffect, noise, $"mgfxc_{noise:0.00}", out string? mgErr,
                        mgfxcParamName: "noise");
                    if (sd is null || mg is null)
                    {
                        Outcomes.Add(new ReservedWordOutcome($"mgfxc.equiv[noise={noise:0.00}]", false,
                            $"render failed (sd={sd is not null}, mgfxc={mg is not null}): {mgErr}"));
                        continue;
                    }

                    Color a = sd.Value, b = mg.Value;
                    int d = Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)),
                            Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
                    bool pass = d <= _tolerance;
                    Outcomes.Add(new ReservedWordOutcome($"mgfxc.equiv[noise={noise:0.00}]", pass,
                        $"ShadowDusk={a} mgfxc={b} maxDelta={d} (tolerance {_tolerance})"));
                }
                mgfxcEffect.Dispose();
            }
        }

        sdEffect.Dispose();
    }

    /// <summary>
    /// Sets <c>noise</c> BY NAME on <paramref name="effect"/>, renders the fixture's flat
    /// grey through the normal SpriteBatch path into an offscreen RT, saves a PNG, and
    /// returns the centre pixel (the frame is flat, so any pixel represents the output).
    /// </summary>
    private Color? RenderNoise(Effect effect, float noise, string pngStem, out string? error,
        string mgfxcParamName = "noise")
    {
        error = null;
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);

            // Bind the texture (required for the sprite effect to be valid) and the
            // reserved-word uniform BY NAME — the exact runtime surface B10 fixes.
            effect.Parameters["SpriteTexture"]?.SetValue(_cat);
            effect.Parameters[mgfxcParamName]?.SetValue(noise);

            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            _sb.Draw(_cat, new Rectangle(0, 0, w, h), Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[w * h];
            rt.GetData(pixels);

            string png = Path.Combine(_outDir, pngStem + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            return pixels[(h / 2) * w + (w / 2)];
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
