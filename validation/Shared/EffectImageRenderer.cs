#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation;

/// <summary>One shader to render: its <c>.mgfx</c> bytes (or a compile error).</summary>
public sealed record ShaderJob(string Name, byte[]? Bytes, string? CompileError);

/// <summary>What happened when we tried to load + render a job.</summary>
public sealed record ShaderOutcome(
    string Name, bool Loaded, bool Rendered, string? Error, string? PngPath);

/// <summary>
/// Loads each effect's <c>.mgfx</c> bytes into a REAL MonoGame DesktopGL
/// <see cref="Effect"/>, applies it to the cat image via the normal
/// <see cref="SpriteBatch"/> path, and saves the result as a PNG. The ONLY
/// difference between the baseline and candidate runs is where the bytes come
/// from — this renderer is identical for both, so any image difference is
/// attributable to the compiler that produced the bytes.
/// </summary>
public sealed class EffectImageRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<ShaderJob> _jobs;
    private readonly Action<Effect, Texture2D> _setParams;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<ShaderOutcome> Outcomes { get; } = new();

    public EffectImageRenderer(
        string catPath, string outDir,
        IReadOnlyList<ShaderJob> jobs, Action<Effect, Texture2D> setParams)
    {
        _catPath = catPath;
        _outDir = outDir;
        _jobs = jobs;
        _setParams = setParams;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk validation (headless)";
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
        if (_done)
        {
            Exit();
            return;
        }

        GraphicsDevice.Clear(Color.Black);
        int w = _cat.Width, h = _cat.Height;
        foreach (var job in _jobs)
            Outcomes.Add(RenderOne(job, w, h));

        _done = true;
        Exit();
    }

    private ShaderOutcome RenderOne(ShaderJob job, int w, int h)
    {
        if (job.Bytes is null)
            return new ShaderOutcome(job.Name, false, false,
                $"compile failed: {job.CompileError}", null);

        Effect effect;
        try
        {
            effect = new Effect(GraphicsDevice, job.Bytes);
        }
        catch (Exception ex)
        {
            return new ShaderOutcome(job.Name, false, false,
                $"new Effect() threw: {ex.Message}", null);
        }

        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            var dest = new Rectangle(0, 0, w, h);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            // Prime SpriteBatch's sprite vertex shader. The candidate shaders are
            // pixel-only (no VS in the pass); the goldens are too. SpriteBatch's
            // VS must be active before the pixel-only effect is applied — matches
            // samples/ShaderViewer/Game1.cs.
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            try { _setParams(effect, _cat); }
            catch { /* a missing/typed param on one side must not abort the row */ }

            // Immediate applies each pass before the draw. Opaque so the captured
            // image is exactly the shader output (cat is opaque; no blend bleed).
            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, null, null, effect);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            string png = Path.Combine(_outDir, job.Name + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            return new ShaderOutcome(job.Name, true, true, null, png);
        }
        catch (Exception ex)
        {
            // Leave the shared SpriteBatch usable for the next shader — an
            // exception mid-Begin/End otherwise wedges every subsequent row.
            try { _sb.End(); } catch { /* may not be in a batch */ }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            string frames = string.Join(" <- ",
                (ex.StackTrace ?? "").Split('\n').Take(4).Select(f => f.Trim().Replace("at ", "")));
            return new ShaderOutcome(job.Name, true, false,
                $"render threw: {ex.GetType().Name}: {ex.Message} @ {frames}", null);
        }
        finally
        {
            effect.Dispose();
        }
    }
}
