#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Dx;

/// <summary>One shader to render: its <c>.mgfx</c> bytes (or a compile error).</summary>
public sealed record ShaderJob(string Name, byte[]? Bytes, string? CompileError);

/// <summary>What happened when we tried to load + render a job.</summary>
public sealed record ShaderOutcome(
    string Name, bool Loaded, bool Rendered, string? Error, string? PngPath);

/// <summary>
/// DirectX (Phase 18) analogue of Phase 17's <c>EffectImageRenderer</c>. Loads each
/// effect's <c>.mgfx</c> bytes into a REAL MonoGame.Framework.WindowsDX (DX11)
/// <see cref="Effect"/>, applies it to the cat image via the normal
/// <see cref="SpriteBatch"/> path, and saves the result as a PNG.
///
/// The ONLY difference between the baseline and candidate runs is where the bytes
/// come from — this renderer is identical for both, so any image difference is
/// attributable to the compiler that produced the bytes. The render/blend/sampler
/// state is pinned identically to Phase 17 so differences are shader-driven, not
/// state-driven. We deliberately do NOT bind any parameter the real runtime does
/// not expose — the MonoGame <see cref="Effect"/> + <see cref="SpriteBatch"/> path
/// does all binding (CLAUDE.md forbids teaching the renderer fake bindings).
///
/// Headless note: MonoGame.Framework.WindowsDX still creates a Win32 game window
/// (it is the DX11 swap-chain host). We run a single Draw frame to an offscreen
/// RenderTarget2D and then Exit, so the window is created but never meaningfully
/// shown. This is the WindowsDX equivalent of Phase 17's DesktopGL hidden boot.
/// </summary>
public sealed class DxEffectImageRenderer : Game
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

    public DxEffectImageRenderer(
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
        Window.Title = "ShadowDusk DX validation (headless)";
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
            // THE rung-4 load test: does the real DX11 Effect loader accept the
            // .mgfx? This is what the structural decoder cannot prove.
            effect = new Effect(GraphicsDevice, job.Bytes);
        }
        catch (Exception ex)
        {
            return new ShaderOutcome(job.Name, false, false,
                $"new Effect() threw: {ex.GetType().Name}: {ex.Message}", null);
        }

        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            var dest = new Rectangle(0, 0, w, h);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            // Prime SpriteBatch's sprite vertex shader (pixel-only effects need a VS).
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            try { _setParams(effect, _cat); }
            catch { /* a missing/typed param on one side must not abort the row */ }

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
