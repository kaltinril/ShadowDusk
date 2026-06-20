#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.ShaderToy.Runtime;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// One gallery entry: the shader name, its converted+compiled OpenGL <c>.mgfx</c>, and two flags
/// that set how strictly its render is gated.
/// </summary>
/// <param name="Name">Shader file name (no extension).</param>
/// <param name="MgfxBytes">The compiled OpenGL <c>.mgfx</c>.</param>
/// <param name="HasUndrivenCustomUniform">
/// True when the shader references a custom uniform the gallery does NOT drive. MonoGame zero-
/// initializes unset effect parameters, so such a shader may legitimately render all-black at the
/// gallery's fixed uniforms; it is therefore NOT gated on "not all-black".
/// </param>
/// <param name="SpatiallyGated">
/// True for the shaders we additionally require to be SPATIALLY non-trivial (many distinct colors) -
/// the render-fidelity proof set (the 4 complex shaders + the representative spatial fixtures). The
/// remaining valid fixtures can be constant-by-design or few-banded, so they are only gated on
/// "loaded + rendered without throwing (and not all-black unless custom-uniform-dependent)".
/// </param>
public sealed record GalleryJob(
    string Name, byte[] MgfxBytes, bool HasUndrivenCustomUniform, bool SpatiallyGated);

/// <summary>Outcome of rendering one gallery job: whether it rendered NON-TRIVIALLY and why.</summary>
public sealed record GalleryResult(
    string Name, bool Ok, string Detail, Color[]? Pixels);

/// <summary>
/// The "everything actually renders" gate (Phase 46 render-fidelity broadening). For each converted
/// corpus shader this:
///   1. loads the OpenGL <c>.mgfx</c> into a real MonoGame DesktopGL <see cref="Effect"/>,
///   2. drives a FIXED iResolution + iTime + iMouse through <see cref="ShaderToyEffect"/>,
///   3. renders a fullscreen pass into an offscreen <see cref="RenderTarget2D"/>,
///   4. reads back the pixels and ASSERTS the frame is NON-TRIVIAL (not all-black, not all one
///      color) using simple statistics: a bright-enough max channel AND a floor on distinct colors,
///   5. composes every thumbnail into a single committed montage PNG.
/// A black or constant frame is a FAILURE: it means the shader loaded but did not really render.
/// </summary>
public sealed class GalleryGame : Game
{
    // Non-trivial thresholds. A real procedural shader at iTime=1.5 paints a varied frame; a broken
    // one (black/constant) fails both gates. These are deliberately generous to avoid false alarms.
    private const int MaxChannelFloor = 24;        // at least one pixel channel must reach this (of 255)
    private const int DistinctColorFloor = 16;     // at least this many distinct RGBA colors must appear

    private const float FixedTime = 1.5f;

    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<GalleryJob> _jobs;
    private readonly int _width;
    private readonly int _height;

    private bool _done;

    public List<GalleryResult> Results { get; } = new();

    public GalleryGame(IReadOnlyList<GalleryJob> jobs, int width, int height)
    {
        _jobs = jobs;
        _width = width;
        _height = height;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width,
            PreferredBackBufferHeight = height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk ShaderToy render gallery (headless)";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        foreach (GalleryJob job in _jobs)
        {
            Results.Add(RenderOne(job));
        }

        _done = true;
        Exit();
    }

    private GalleryResult RenderOne(GalleryJob job)
    {
        Effect effect;
        try
        {
            effect = new Effect(GraphicsDevice, job.MgfxBytes);
        }
        catch (Exception ex)
        {
            return new GalleryResult(job.Name, false, $"new Effect() threw: {ex.Message}", null);
        }

        using var helper = new ShaderToyEffect(GraphicsDevice, effect, ownsEffect: true);
        using var rt = new RenderTarget2D(
            GraphicsDevice, _width, _height, false, SurfaceFormat.Color, DepthFormat.None);

        try
        {
            helper.SetResolution(_width, _height);
            helper.SetTime(FixedTime);
            helper.SetTimeDelta(1f / 60f);
            helper.SetFrame(90);
            // A non-zero mouse so mouse-driven shaders also paint something deterministic.
            helper.SetMouse(new Vector4(_width * 0.5f, _height * 0.5f, 1f, 1f));

            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);
            helper.Draw();
            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[_width * _height];
            rt.GetData(pixels);

            (bool ok, string detail) = AssessNonTrivial(pixels, job);
            return new GalleryResult(job.Name, ok, detail, pixels);
        }
        catch (Exception ex)
        {
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            return new GalleryResult(job.Name, false, $"render threw: {ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Assess a rendered frame. Two tiers (see <see cref="GalleryJob"/>):
    /// <list type="bullet">
    /// <item>Base gate (ALL shaders): the frame must NOT be all-black - i.e. some channel reaches
    /// <see cref="MaxChannelFloor"/>. Skipped only when the shader depends on a custom uniform the
    /// gallery does not drive (MonoGame zeroes unset params, so all-black is then legitimate).</item>
    /// <item>Spatial gate (the render-fidelity proof set): the frame must ALSO contain at least
    /// <see cref="DistinctColorFloor"/> distinct colors, proving real spatially-varying rendering.</item>
    /// </list>
    /// A black or constant frame from a spatially-gated shader is the "loaded but did not render"
    /// failure mode and FAILS.
    /// </summary>
    private static (bool Ok, string Detail) AssessNonTrivial(Color[] pixels, GalleryJob job)
    {
        byte maxChannel = 0;
        var distinct = new HashSet<uint>();
        foreach (Color c in pixels)
        {
            if (c.R > maxChannel) maxChannel = c.R;
            if (c.G > maxChannel) maxChannel = c.G;
            if (c.B > maxChannel) maxChannel = c.B;
            distinct.Add(c.PackedValue);
            if (distinct.Count > DistinctColorFloor * 8 && maxChannel >= MaxChannelFloor)
            {
                break;
            }
        }

        bool brightEnough = maxChannel >= MaxChannelFloor;
        bool variedEnough = distinct.Count >= DistinctColorFloor;
        bool capped = distinct.Count > DistinctColorFloor * 8;

        // Base gate: not all-black (unless an undriven custom uniform makes all-black legitimate).
        bool baseOk = brightEnough || job.HasUndrivenCustomUniform;
        // Spatial gate adds the distinct-color floor for the render-fidelity proof set.
        bool ok = job.SpatiallyGated ? (brightEnough && variedEnough) : baseOk;

        string tier = job.SpatiallyGated ? "spatial" : "base";
        string detail =
            $"[{tier}] maxChannel={maxChannel}, distinctColors={distinct.Count}{(capped ? "+" : "")}";
        if (job.HasUndrivenCustomUniform)
        {
            detail += " (custom-uniform-dependent: all-black tolerated)";
        }

        if (!ok)
        {
            detail = job.SpatiallyGated
                ? "TRIVIAL FRAME (expected spatial variation, got black/constant) -> " + detail
                : "ALL-BLACK FRAME (loaded but rendered nothing) -> " + detail;
        }

        return (ok, detail);
    }

    /// <summary>Width of one thumbnail tile in the montage.</summary>
    public int TileWidth => _width;

    /// <summary>Height of one thumbnail tile in the montage.</summary>
    public int TileHeight => _height;
}
