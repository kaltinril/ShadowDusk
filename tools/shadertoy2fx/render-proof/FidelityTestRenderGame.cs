#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.ShaderToyViewer.Runtime;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>One shader to render through the REAL MonoGame pipeline for the fidelity gate.</summary>
public sealed record FidelityTestJob(string Name, byte[] MgfxBytes);

/// <summary>The MonoGame test render for one shader: read-back RGBA8 (top-row-first) or an error.</summary>
public sealed record FidelityTestRender(string Name, byte[]? Rgba, string? Error);

/// <summary>
/// Renders every fidelity job through a SINGLE real MonoGame DesktopGL context: load the converted
/// <c>.mgfx</c> into an <see cref="Effect"/>, drive the SAME fixed ShaderToy uniforms the GL reference
/// uses, render a fullscreen pass to an offscreen <see cref="RenderTarget2D"/>, and read back RGBA8.
/// <c>RenderTarget2D.GetData</c> is top-row-first, matching the reference renderer's flipped read-back,
/// so the two buffers diff pixel-for-pixel.
/// </summary>
public sealed class FidelityTestRenderGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<FidelityTestJob> _jobs;
    private readonly RefUniforms _u;
    private readonly int _width;
    private readonly int _height;

    private bool _done;

    public List<FidelityTestRender> Renders { get; } = new();

    public FidelityTestRenderGame(
        IReadOnlyList<FidelityTestJob> jobs, RefUniforms u, int width, int height)
    {
        _jobs = jobs;
        _u = u;
        _width = width;
        _height = height;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width,
            PreferredBackBufferHeight = height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk ShaderToy fidelity test render (headless)";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        // ONE shared render target reused for every shader. Each shader does a full Clear+Draw that
        // overwrites it completely, then GetData reads it back before the next shader's Clear. Creating
        // and disposing a fresh RenderTarget2D per iteration caused cross-shader BLEED (a later shader
        // read an earlier shader's texels) because the GL texture handle was recycled under the pending
        // readback. A single long-lived RT eliminates that aliasing.
        using var rt = new RenderTarget2D(
            GraphicsDevice, _width, _height, false, SurfaceFormat.Color, DepthFormat.None);

        foreach (FidelityTestJob job in _jobs)
            Renders.Add(RenderOne(job, rt));

        _done = true;
        Exit();
    }

    private FidelityTestRender RenderOne(FidelityTestJob job, RenderTarget2D rt)
    {
        Effect effect;
        try
        {
            effect = new Effect(GraphicsDevice, job.MgfxBytes);
        }
        catch (Exception ex)
        {
            return new FidelityTestRender(job.Name, null, $"new Effect() threw: {ex.Message}");
        }

        using var helper = new ShaderToyEffect(GraphicsDevice, effect, ownsEffect: true);

        try
        {
            helper.SetResolution(_u.ResolutionX, _u.ResolutionY);
            helper.SetTime(_u.Time);
            helper.SetTimeDelta(_u.TimeDelta);
            helper.SetFrame(_u.Frame);
            helper.SetMouse(new Vector4(_u.MouseX, _u.MouseY, _u.MouseZ, _u.MouseW));

            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);
            helper.Draw();
            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[_width * _height];
            rt.GetData(pixels);

            var rgba = new byte[_width * _height * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgba[i * 4 + 0] = pixels[i].R;
                rgba[i * 4 + 1] = pixels[i].G;
                rgba[i * 4 + 2] = pixels[i].B;
                rgba[i * 4 + 3] = pixels[i].A;
            }

            return new FidelityTestRender(job.Name, rgba, null);
        }
        catch (Exception ex)
        {
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            return new FidelityTestRender(job.Name, null, $"render threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
