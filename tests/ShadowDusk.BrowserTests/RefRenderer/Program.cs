// Phase 24 reference renderer.
//
// Renders each of the 10 precompiled OpenGL corpus .mgfx files on REAL desktop
// DesktopGL, using the SAME draw recipe the browser KNI sample uses
// (ShaderFiddleGame.Draw): a fixed SIZE x SIZE square viewport, black clear,
// the cat fit-centered, BlendState.Opaque + SamplerState.LinearClamp, the
// SpriteBatch VS primed with a passthrough Draw, then the effect applied in
// SpriteSortMode.Immediate. Parameters are set BY NAME identically to
// WebShaderInputs.SetParams. The output PNGs are the references the Playwright
// harness pixel-compares the browser WebGL capture against — so this isolates
// "does the SAME .mgfx render the same in WebGL as in DesktopGL?".
//
// Usage: RefRenderer <mgfxDir> <catPath> <outDir> <size>

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: RefRenderer <mgfxDir> <catPath> <outDir> <size>");
    return 2;
}

string mgfxDir = args[0];
string catPath = args[1];
string outDir = args[2];
int size = int.Parse(args[3]);

string[] names =
{
    "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
    "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
};

var jobs = new List<(string Name, byte[]? Bytes)>();
foreach (var name in names)
{
    string p = Path.Combine(mgfxDir, name + ".mgfx");
    jobs.Add((name, File.Exists(p) ? File.ReadAllBytes(p) : null));
}

Directory.CreateDirectory(outDir);
using var game = new RefGame(catPath, outDir, size, jobs);
game.Run();

int ok = 0;
foreach (var o in game.Outcomes)
{
    Console.WriteLine($"  [{(o.Ok ? "OK  " : "FAIL")}] {o.Name,-12} {o.Detail}");
    if (o.Ok) ok++;
}
Console.WriteLine($"[ref] {ok}/{game.Outcomes.Count} rendered at {size}x{size}.");
return ok == game.Outcomes.Count ? 0 : 1;

sealed record RefOutcome(string Name, bool Ok, string Detail);

sealed class RefGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly int _size;
    private readonly List<(string Name, byte[]? Bytes)> _jobs;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<RefOutcome> Outcomes { get; } = new();

    public RefGame(string catPath, string outDir, int size, List<(string, byte[]?)> jobs)
    {
        _catPath = catPath;
        _outDir = outDir;
        _size = size;
        _jobs = jobs;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = size,
            PreferredBackBufferHeight = size,
            // KNI's BlazorGL/WebGL runtime is the Reach profile, so the desktop
            // reference uses Reach too — this isolates the GLSL DIALECT (the
            // Phase 24 question) from any profile difference. Verified: rendering
            // the references in HiDef vs Reach produces byte-identical output for
            // all 10 corpus shaders, so the Dissolve divergence found in WebGL is
            // NOT a profile artifact — it is a real KNI-WebGL render difference.
            GraphicsProfile = GraphicsProfile.Reach,
        };
        Window.Title = "ShadowDusk Phase 24 reference renderer";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }

        foreach (var (name, bytes) in _jobs)
            Outcomes.Add(RenderOne(name, bytes));

        _done = true;
        Exit();
    }

    private RefOutcome RenderOne(string name, byte[]? bytes)
    {
        if (bytes is null)
            return new RefOutcome(name, false, "mgfx not found");

        Effect effect;
        try { effect = new Effect(GraphicsDevice, bytes); }
        catch (Exception ex) { return new RefOutcome(name, false, $"new Effect threw: {ex.Message}"); }

        using var rt = new RenderTarget2D(GraphicsDevice, _size, _size, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            // Match the browser: GraphicsDevice.Clear(Color.Black) then draw.
            GraphicsDevice.Clear(Color.Black);

            Rectangle dest = FitCentered(_cat.Width, _cat.Height, _size, _size);

            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            try { SetParams(effect, _cat); } catch { /* missing param must not abort */ }

            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, null, null, effect);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            string png = Path.Combine(_outDir, name + ".png");
            using var outFs = File.Create(png);
            rt.SaveAsPng(outFs, _size, _size);
            return new RefOutcome(name, true, png);
        }
        catch (Exception ex)
        {
            try { _sb.End(); } catch { }
            try { GraphicsDevice.SetRenderTarget(null); } catch { }
            return new RefOutcome(name, false, $"render threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally { effect.Dispose(); }
    }

    // Mirrors ShaderFiddleGame.FitCentered exactly.
    private static Rectangle FitCentered(int w, int h, int vw, int vh)
    {
        if (w <= 0 || h <= 0 || vw <= 0 || vh <= 0)
            return new Rectangle(0, 0, vw, vh);
        float scale = Math.Min((float)vw / w, (float)vh / h);
        int dw = (int)(w * scale);
        int dh = (int)(h * scale);
        return new Rectangle((vw - dw) / 2, (vh - dh) / 2, dw, dh);
    }

    // Mirrors WebShaderInputs.SetParams / validation ShaderInputs.SetParams.
    private static void SetParams(Effect e, Texture2D cat)
    {
        e.Parameters["SpriteTexture"]?.SetValue(cat);
        e.Parameters["TintColor"]?.SetValue(new Vector4(1f, 0.5f, 0.5f, 1f));
        e.Parameters["_sepiaTone"]?.SetValue(new Vector3(1.2f, 1.0f, 0.8f));
        e.Parameters["BloomThreshold"]?.SetValue(new Vector4(0.25f, 0.25f, 0.25f, 0.25f));
        e.Parameters["BloomIntensity"]?.SetValue(1.5f);
        e.Parameters["BloomSaturation"]?.SetValue(0.8f);
        e.Parameters["_attenuation"]?.SetValue(800.0f);
        e.Parameters["_linesFactor"]?.SetValue(0.04f);
        e.Parameters["angle"]?.SetValue(0.5f);
        e.Parameters["scale"]?.SetValue(0.5f);
        e.Parameters["ScreenSize"]?.SetValue(new Vector2(cat.Width, cat.Height));
        e.Parameters["_dissolveTex"]?.SetValue(cat);
        e.Parameters["_progress"]?.SetValue(0.5f);
        e.Parameters["_dissolveThreshold"]?.SetValue(0.04f);
        e.Parameters["_dissolveThresholdColor"]?.SetValue(new Vector4(1f, 0.5f, 0f, 1f));
    }
}
