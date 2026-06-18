#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Validation;

/// <summary>
/// Phase 45 B10 reserved-word-uniform RENDER probe, shared across the format-specific
/// render harnesses (MGFX v11 in MonoGame 3.8.5, MGFX v10 + KNIFX in KNI v4.02).
///
/// B10 fixed the OpenGL binding of a FREE uniform whose name collides with a GLSL
/// reserved word (the canonical case <c>float noise;</c>). On the GL path SPIRV-Cross
/// renames it <c>_noise</c> to keep the emitted GLSL legal; ShadowDusk's effect-container
/// cbuffer-record join bridges by register OFFSET so the parameter stays exposed under its
/// original name <c>noise</c>. That fix lives in the SHARED GL container path, so it flows
/// into every container ShadowDusk writes - not just the v10 MGFX proven by
/// <c>validation/ReservedWordGl</c>. This probe re-proves the SAME non-vacuous bar
/// (set <c>noise</c> by name to 0.25 / 0.75, render, assert the pixel is grey
/// round(noise*255) = 64 / 191, and that the two outputs differ by ~127) for whichever
/// container the host harness selects, in that harness's REAL engine.
///
/// It deliberately does NOT touch the shared 10-shader golden corpus
/// (<see cref="ShaderInputs.ShaderNames"/>): it compiles only the focused
/// <c>ExReservedWordUniformRender.fx</c> fixture (whose PS is <c>float4(noise,noise,noise,1)</c>),
/// so adding this proof ripples into no other driver and needs no new goldens.
/// </summary>
public static class ReservedWordProbe
{
    /// <summary>The two <c>noise</c> values to render and assert (grey 64 and 191).</summary>
    public static readonly float[] NoiseValues = { 0.25f, 0.75f };

    /// <summary>
    /// Compile <c>ExReservedWordUniformRender.fx</c> with the supplied <paramref name="options"/>
    /// (a container/version chosen by the host harness), load + render it in the host's real
    /// engine, and assert <c>noise</c> binds under its original name and exactly drives the
    /// output. Writes PNGs + the raw container bytes under
    /// <c>validation/output/reservedword-&lt;leaf&gt;</c>.
    /// </summary>
    /// <param name="label">Human label for the container, e.g. "MGFX v11" / "KNIFX v11".</param>
    /// <param name="outLeaf">Output subdir leaf, e.g. "v11" / "kni-v10" / "kni-knifx".</param>
    /// <param name="container">
    /// The effect container to emit (<see cref="EffectContainer.Mgfx"/> or
    /// <see cref="EffectContainer.Knifx"/>) - the only format axis the caller chooses.
    /// </param>
    /// <param name="mgfxVersion">
    /// The MGFX container version (10 or 11). Ignored when <paramref name="container"/> is
    /// <see cref="EffectContainer.Knifx"/> (KNIFX carries its own v11), matching the compiler.
    /// </param>
    /// <param name="tolerance">Per-channel rounding tolerance (default 2, as in ReservedWordGl).</param>
    /// <returns>Process exit code: 0 iff every check passed.</returns>
    public static async System.Threading.Tasks.Task<int> RunAsync(
        string label, string outLeaf,
        EffectContainer container = EffectContainer.Mgfx, int mgfxVersion = 10, int tolerance = 2)
    {
        string repoRoot = ShaderInputs.FindRepoRoot();
        string fxPath = Path.Combine(repoRoot, "tests", "fixtures", "shaders",
                                     "examples", "ExReservedWordUniformRender.fx");
        string catPath = ShaderInputs.CatPath(repoRoot);
        string outDir = Path.Combine(repoRoot, "validation", "output", "reservedword-" + outLeaf);

        Console.WriteLine($"[reservedword:{outLeaf}] container: {label}");
        Console.WriteLine($"[reservedword:{outLeaf}] fx:  {fxPath}");
        Console.WriteLine($"[reservedword:{outLeaf}] cat: {catPath}");
        Console.WriteLine($"[reservedword:{outLeaf}] out: {outDir}  tolerance: {tolerance}\n");

        if (!File.Exists(fxPath))
        {
            Console.Error.WriteLine($"[reservedword:{outLeaf}] FATAL: fixture not found: {fxPath}");
            return 2;
        }

        // ---- Compile the fixture with the host-selected container. This exercises the SHARED
        //      GL container path the B10 offset bridge lives in; a successful compile that
        //      exposes `noise` is the first half of the proof (pre-fix this FAILED with SD0012).
        string src = await File.ReadAllTextAsync(fxPath);
        var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            Container = container,
            MgfxVersion = mgfxVersion,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fxPath,
        });

        if (result.IsFailure)
        {
            Console.Error.WriteLine($"[reservedword:{outLeaf}] FATAL: ShadowDusk {label} compile FAILED " +
                                    "(the B10 SD0012 regression?):");
            foreach (var e in result.Error)
                Console.Error.WriteLine($"    {e.Code}: {e.Message}");
            return 1;
        }

        byte[] mgfx = result.Value.Data;
        Directory.CreateDirectory(outDir);
        await File.WriteAllBytesAsync(Path.Combine(outDir, "shadowdusk-" + outLeaf + ".bin"), mgfx);
        Console.WriteLine($"[reservedword:{outLeaf}] ShadowDusk {label} compile OK ({mgfx.Length} bytes); " +
                          "'noise' must bind by name.\n");

        using var game = new ReservedWordProbeGame(catPath, outDir, mgfx, NoiseValues, tolerance, outLeaf);
        game.Run();

        int passed = 0;
        Console.WriteLine($"[reservedword:{outLeaf}] results ({label}):");
        foreach (var o in game.Outcomes)
        {
            if (o.Pass) passed++;
            Console.WriteLine($"  [{(o.Pass ? "PASS" : "FAIL")}] {o.Name,-38} {o.Detail}");
        }

        bool allPassed = passed == game.Outcomes.Count && game.Outcomes.Count > 0;
        Console.WriteLine($"\n[reservedword:{outLeaf}] {passed}/{game.Outcomes.Count} checks passed " +
                          $"({label} in the real engine).");
        return allPassed ? 0 : 1;
    }
}

/// <summary>One asserted check (a single noise value, or the differentiation check).</summary>
public sealed record ReservedWordProbeOutcome(string Name, bool Pass, string Detail);

/// <summary>
/// One real engine device (MonoGame 3.8.5 or KNI v4.02, supplied by the host harness's
/// package reference). Loads the reserved-word <c>.mgfx</c>/<c>.knifx</c> bytes into a real
/// <see cref="Effect"/>, asserts <c>noise</c> is exposed under its ORIGINAL name, sets it
/// BY NAME to each test value, renders the flat-grey <c>float4(noise,noise,noise,1)</c>
/// through the normal SpriteBatch path, reads the pixel back, and asserts it equals the
/// expected grey - so <c>noise</c> demonstrably binds to the correct register and drives the
/// result in this container/runtime. Mirrors <c>validation/ReservedWordGl</c>'s assertions.
/// </summary>
internal sealed class ReservedWordProbeGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly byte[] _mgfx;
    private readonly float[] _noiseValues;
    private readonly int _tolerance;
    private readonly string _leaf;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<ReservedWordProbeOutcome> Outcomes { get; } = new();

    public ReservedWordProbeGame(
        string catPath, string outDir, byte[] mgfx,
        float[] noiseValues, int tolerance, string leaf)
    {
        _catPath = catPath;
        _outDir = outDir;
        _mgfx = mgfx;
        _noiseValues = noiseValues;
        _tolerance = tolerance;
        _leaf = leaf;
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
            Outcomes.Add(new ReservedWordProbeOutcome("harness", false,
                $"unhandled exception: {ex.GetType().Name}: {ex.Message}"));
        }

        _done = true;
        Exit();
    }

    private void RunAllChecks()
    {
        // Load the effect once; reuse across noise values. Loading itself must succeed - a
        // malformed container throws here, so a clean load is part of the format proof.
        Effect effect;
        try { effect = new Effect(GraphicsDevice, _mgfx); }
        catch (Exception ex)
        {
            Outcomes.Add(new ReservedWordProbeOutcome("load", false,
                $"new Effect(bytes) threw: {ex.Message}"));
            return;
        }

        // The parameter MUST be reachable by its original name (the whole point of B10).
        if (effect.Parameters["noise"] is null)
        {
            Outcomes.Add(new ReservedWordProbeOutcome("param[noise]", false,
                "effect.Parameters[\"noise\"] is null - the offset bridge did not expose the " +
                "parameter under its original name (it would be the SPIRV-Cross rename '_noise')"));
            effect.Dispose();
            return;
        }
        Outcomes.Add(new ReservedWordProbeOutcome("param[noise]", true,
            "exposed under original name 'noise' (not the SPIRV-Cross rename '_noise')"));

        // (1)+(2): exact expected pixel for each noise value.
        var greys = new Dictionary<float, int>();
        foreach (float noise in _noiseValues)
        {
            int expected = (int)Math.Round(noise * 255f);
            Color? center = RenderNoise(effect, noise, $"{_leaf}_{noise:0.00}", out string? err);
            if (center is null)
            {
                Outcomes.Add(new ReservedWordProbeOutcome($"render[noise={noise:0.00}]", false,
                    $"render failed: {err}"));
                continue;
            }

            Color px = center.Value;
            int maxChan = Math.Max(Math.Max(px.R, px.G), px.B);
            int minChan = Math.Min(Math.Min(px.R, px.G), px.B);
            int dExpected = Math.Max(Math.Abs(px.R - expected),
                            Math.Max(Math.Abs(px.G - expected), Math.Abs(px.B - expected)));
            bool grey = (maxChan - minChan) <= _tolerance; // R==G==B (flat grey)
            bool aOk = Math.Abs(px.A - 255) <= _tolerance;
            bool pass = dExpected <= _tolerance && grey && aOk;

            greys[noise] = (px.R + px.G + px.B) / 3;
            Outcomes.Add(new ReservedWordProbeOutcome($"render[noise={noise:0.00}]", pass,
                $"pixel={px} expected grey {expected} (round({noise:0.00}*255)); " +
                $"dExpected={dExpected} greySpread={maxChan - minChan} (tolerance {_tolerance})"));
        }

        // (3): the two noise values must produce the expected DIFFERENT outputs - a no-op,
        // zero, or wrong-register binding cannot pass. Brightness must track noise.
        if (greys.TryGetValue(0.25f, out int g025) && greys.TryGetValue(0.75f, out int g075))
        {
            int observedDelta = g075 - g025;
            int expectedDelta = (int)Math.Round((0.75f - 0.25f) * 255f); // ~128
            bool pass = Math.Abs(observedDelta - expectedDelta) <= 2 * _tolerance && observedDelta > 0;
            Outcomes.Add(new ReservedWordProbeOutcome("differentiation[0.25 vs 0.75]", pass,
                $"grey(0.75)-grey(0.25) = {observedDelta} (expected ~{expectedDelta}); " +
                "proves 'noise' DRIVES the output, not a constant"));
        }
        else
        {
            Outcomes.Add(new ReservedWordProbeOutcome("differentiation[0.25 vs 0.75]", false,
                "could not capture both noise renders"));
        }

        effect.Dispose();
    }

    /// <summary>
    /// Sets <c>noise</c> BY NAME on <paramref name="effect"/>, renders the fixture's flat
    /// grey through the normal SpriteBatch path into an offscreen RT, saves a PNG, and
    /// returns the centre pixel (the frame is flat, so any pixel represents the output).
    /// </summary>
    private Color? RenderNoise(Effect effect, float noise, string pngStem, out string? error)
    {
        error = null;
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);

            // Bind the texture (required for the sprite effect to be valid) and the
            // reserved-word uniform BY NAME - the exact runtime surface B10 fixes.
            effect.Parameters["SpriteTexture"]?.SetValue(_cat);
            effect.Parameters["noise"]?.SetValue(noise);

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
