#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.ShaderToy.Runtime;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>One shader to render + its analytic asserter, plus an optional hook that drives any
/// CUSTOM effect parameters the consumer owns (e.g. a custom <c>uniform</c>). The hook is the proof
/// that a host-set parameter reflects through to a valid effect parameter and renders.</summary>
public sealed record RenderJob(
    string Name,
    byte[] MgfxBytes,
    Func<int, int, RgbAssertion[]> Asserter,
    Action<ShaderToyEffect>? CustomSetup = null);

/// <summary>Result of rendering + asserting one job.</summary>
public sealed record RenderResult(string Name, bool Ok, string Detail, string? PngPath);

/// <summary>
/// Loads each converted <c>.mgfx</c> into a REAL MonoGame DesktopGL <see cref="Effect"/>, drives
/// the ShaderToy uniforms through <see cref="ShaderToyEffect"/>, renders a fullscreen pass to an
/// offscreen <see cref="RenderTarget2D"/>, reads back the pixels, runs the analytic asserts, and
/// saves the rendered PNG.
/// </summary>
public sealed class RenderProofGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<RenderJob> _jobs;
    private readonly string _outDir;
    private readonly int _width;
    private readonly int _height;

    private bool _done;

    public List<RenderResult> Results { get; } = new();

    public RenderProofGame(IReadOnlyList<RenderJob> jobs, string outDir, int width, int height)
    {
        _jobs = jobs;
        _outDir = outDir;
        _width = width;
        _height = height;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width,
            PreferredBackBufferHeight = height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk ShaderToy render proof (headless)";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        foreach (RenderJob job in _jobs)
            Results.Add(RenderOne(job));

        _done = true;
        Exit();
    }

    private RenderResult RenderOne(RenderJob job)
    {
        Effect effect;
        try
        {
            effect = new Effect(GraphicsDevice, job.MgfxBytes);
        }
        catch (Exception ex)
        {
            return new RenderResult(job.Name, false, $"new Effect() threw: {ex.Message}", null);
        }

        using var helper = new ShaderToyEffect(GraphicsDevice, effect, ownsEffect: true);
        using var rt = new RenderTarget2D(
            GraphicsDevice, _width, _height, false, SurfaceFormat.Color, DepthFormat.None);

        try
        {
            // Fixed, deterministic uniforms (iTime=0). Only the ones the effect declares are set.
            helper.SetResolution(_width, _height);
            helper.SetTime(0f);
            helper.SetTimeDelta(0f);
            helper.SetFrame(0);
            helper.SetMouse(Vector4.Zero);

            // Drive any consumer-owned custom uniforms (proves a host-set parameter renders through).
            job.CustomSetup?.Invoke(helper);

            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);
            helper.Draw();
            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[_width * _height];
            rt.GetData(pixels);

            // Save the PNG (top-row-first, same layout as GetData) for human eyeball.
            string png = Path.Combine(_outDir, job.Name + ".png");
            using (var fs = File.Create(png))
                rt.SaveAsPng(fs, _width, _height);

            // Run analytic asserts against the read-back pixels (displayed-image coords).
            RgbAssertion[] asserts = job.Asserter(_width, _height);
            var failures = new List<string>();
            foreach (RgbAssertion a in asserts)
            {
                Color px = pixels[a.Y * _width + a.X];
                float r = px.R / 255f, g = px.G / 255f, b = px.B / 255f;
                bool ok = Math.Abs(r - a.ExpectedR) <= a.Tolerance
                       && Math.Abs(g - a.ExpectedG) <= a.Tolerance
                       && Math.Abs(b - a.ExpectedB) <= a.Tolerance;
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "      [{0}] {1,-22} ({2,3},{3,3}) got=({4:0.00},{5:0.00},{6:0.00}) "
                    + "exp=({7:0.00},{8:0.00},{9:0.00}) tol={10:0.00}",
                    ok ? "ok  " : "FAIL", a.Label, a.X, a.Y, r, g, b,
                    a.ExpectedR, a.ExpectedG, a.ExpectedB, a.Tolerance);
                Console.WriteLine(line);
                if (!ok)
                    failures.Add($"{a.Label}: got=({r:0.00},{g:0.00},{b:0.00}) exp=({a.ExpectedR:0.00},{a.ExpectedG:0.00},{a.ExpectedB:0.00})");
            }

            bool allOk = failures.Count == 0;
            string detail = allOk
                ? $"{asserts.Length} asserts passed"
                : $"{failures.Count}/{asserts.Length} asserts FAILED: {string.Join("; ", failures)}";
            return new RenderResult(job.Name, allOk, detail, png);
        }
        catch (Exception ex)
        {
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            return new RenderResult(job.Name, false, $"render threw: {ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>Prints the summary and returns a process exit code (0 = all passed).</summary>
    public int Report()
    {
        Console.WriteLine();
        int ok = 0;
        foreach (RenderResult r in Results)
        {
            string status = r.Ok ? "PASS" : "FAIL";
            if (r.Ok)
                ok++;
            Console.WriteLine($"  [{status}] {r.Name,-16} {r.Detail}");
            if (r.PngPath is not null)
                Console.WriteLine($"           png: {r.PngPath}");
        }

        Console.WriteLine($"\n[render-proof] {ok}/{Results.Count} shaders rendered + asserted correctly.");

        if (Results.Count == 0)
        {
            Console.Error.WriteLine("[render-proof] No results produced (render harness did nothing).");
            return 3;
        }

        return ok == Results.Count ? 0 : 1;
    }
}
