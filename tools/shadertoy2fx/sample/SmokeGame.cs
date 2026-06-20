#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.ShaderToy.Runtime;

namespace ShadowDusk.ShaderToy.Sample;

/// <summary>The headless verdict for one bundled shader in <c>--smoke</c> mode.</summary>
/// <param name="Name">The shader display name.</param>
/// <param name="Ok">True when it converted, compiled, loaded, and rendered a non-trivial frame.</param>
/// <param name="Detail">A human-readable explanation of the result.</param>
/// <param name="PngPath">The written PNG path, or <c>null</c> if none was produced.</param>
public sealed record SmokeResult(string Name, bool Ok, string Detail, string? PngPath);

/// <summary>
/// The automated, NON-interactive validation of the whole runtime path. For EACH bundled shader it
/// runs convert -> in-memory compile -> load -> render-ONE-frame to an offscreen
/// <see cref="RenderTarget2D"/>, writes a PNG to <c>sample/output/</c>, and asserts the frame is
/// non-trivial (not all-black). No window loop, so it cannot hang on a desktop without a display
/// server beyond the single hidden GL context MonoGame needs to create a device.
/// </summary>
public sealed class SmokeGame : Game
{
    private const int Width = 320;
    private const int Height = 180;

    // A representative, deterministic moment in each animation: non-zero so a time-driven shader
    // (which is black at t = 0 for some) renders something, and a held mouse so iMouse shaders glow.
    private const float ProbeTime = 1.3f;

    private readonly GraphicsDeviceManager _gdm;
    private readonly string _outDir;

    private bool _done;

    public List<SmokeResult> Results { get; } = new();

    public SmokeGame(string outDir)
    {
        _outDir = outDir;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Width,
            PreferredBackBufferHeight = Height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk ShaderToy sample (headless smoke)";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        foreach (ShaderEntry entry in ShaderCatalog.Entries)
            Results.Add(RenderOne(entry));

        _done = true;
        Exit();
    }

    private SmokeResult RenderOne(ShaderEntry entry)
    {
        CompiledShaderToy built = SampleCompiler.Build(GraphicsDevice, entry);
        if (!built.Ok || built.Effect is null)
            return new SmokeResult(entry.DisplayName, false, built.Error, null);

        using ShaderToyEffect shader = built.Effect;
        using var rt = new RenderTarget2D(
            GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);

        try
        {
            shader.SetResolution(Width, Height);
            shader.SetTime(ProbeTime);
            shader.SetTimeDelta(1f / 60f);
            shader.SetFrame(78);
            // Mouse near the upper-right (ShaderToy bottom-left origin), button held.
            shader.SetMouse(new Vector4(Width * 0.7f, Height * 0.7f, Width * 0.7f, Height * 0.7f));

            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);
            shader.Draw();
            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[Width * Height];
            rt.GetData(pixels);

            string png = Path.Combine(_outDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".png");
            using (FileStream fs = File.Create(png))
                rt.SaveAsPng(fs, Width, Height);

            (bool nonTrivial, string stats) = Analyze(pixels);
            string detail = nonTrivial
                ? $"rendered non-trivial frame ({stats})"
                : $"rendered ALL-BLACK / trivial frame ({stats})";
            return new SmokeResult(entry.DisplayName, nonTrivial, detail, png);
        }
        catch (Exception ex)
        {
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore cleanup error */ }
            return new SmokeResult(entry.DisplayName, false, $"render threw: {ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>
    /// A frame is "non-trivial" when a meaningful fraction of its pixels are clearly non-black.
    /// This catches a silently-broken pipeline (a black or near-black image) without asserting any
    /// specific colors, so it works for every animated/interactive shader the same way.
    /// </summary>
    private static (bool NonTrivial, string Stats) Analyze(Color[] pixels)
    {
        long lit = 0;
        int maxChannel = 0;
        foreach (Color p in pixels)
        {
            int m = Math.Max(p.R, Math.Max(p.G, p.B));
            if (m > maxChannel)
                maxChannel = m;
            if (m >= 16) // > ~6% of full brightness on any channel
                lit++;
        }

        double litFraction = (double)lit / pixels.Length;
        string stats = string.Format(
            CultureInfo.InvariantCulture,
            "lit={0:P1}, maxChannel={1}", litFraction, maxChannel);

        // Require both a bright-enough peak and a non-tiny lit area: a stray single bright pixel
        // should not pass, but no shader in the corpus fills less than a few percent of the frame.
        bool nonTrivial = maxChannel >= 24 && litFraction >= 0.02;
        return (nonTrivial, stats);
    }

    /// <summary>Prints the summary and returns a process exit code (0 = every shader rendered).</summary>
    public int Report()
    {
        Console.WriteLine();
        int ok = 0;
        foreach (SmokeResult r in Results)
        {
            string status = r.Ok ? "PASS" : "FAIL";
            if (r.Ok)
                ok++;
            Console.WriteLine($"  [{status}] {r.Name,-34} {r.Detail}");
            if (r.PngPath is not null)
                Console.WriteLine($"           png: {r.PngPath}");
        }

        Console.WriteLine($"\n[smoke] {ok}/{Results.Count} shaders converted + compiled in-memory + rendered.");

        if (Results.Count == 0)
        {
            Console.Error.WriteLine("[smoke] No results produced (render harness did nothing).");
            return 3;
        }

        return ok == Results.Count ? 0 : 1;
    }
}
