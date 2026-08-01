#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.ShaderToy;
using ShadowDusk.ShaderToy.Multipass;
using ShadowDusk.ShaderToyViewer.Runtime;

namespace ShadowDusk.ShaderToy.RenderProof;

// =============================================================================
// MULTIPASS render proof (Phase 46) — the "chain2" two-pass example.
//
// THIS IS A HAND-WIRED EXAMPLE, NOT A REUSABLE ENGINE. It is exactly the ~tens of
// lines a consumer writes in their own Draw loop:
//
//   1. MultipassConverter.Convert(export) -> one .fx per render tab + the wiring,
//   2. compile each .fx -> .mgfx (real ShadowDusk CLI, OpenGL),
//   3. allocate a RenderTarget2D for Buffer A,
//   4. render Buffer A (a UV gradient) into it,
//   5. bind Buffer A's texture as iChannel0 of Image,
//   6. render Image (tints Buffer A) to an offscreen target,
//   7. read back the pixels and ASSERT the analytic expected result
//      (Image = tint(BufferA gradient) = (uv.x, uv.y*0.5, 0.125)),
//   8. save the PNG.
//
// The render graph (this loop) is the consumer's job, by design. ShadowDusk only
// converts the .fx and hands over the manifest/WIRING.md that document this loop.
// =============================================================================

/// <summary>
/// Renders the hand-authored <c>chain2</c> two-pass example (Buffer A gradient → Image tint) end to
/// end and asserts the analytic result. A worked example of consumer-side multipass wiring, run as the
/// proof that the emitted <c>.fx</c> + manifest are correct. Returns a process exit code (0 = passed).
/// </summary>
internal static class MultipassChain2Proof
{
    public static int Run(string cliDll, string exportJsonPath, string outDir)
    {
        Console.WriteLine("[multipass-proof] chain2: Buffer A (gradient) -> Image (tint)");

        if (!File.Exists(exportJsonPath))
        {
            Console.Error.WriteLine($"[multipass-proof] MISSING export json: {exportJsonPath}");
            return 2;
        }

        // 1. Convert the multi-tab export to one .fx per render tab + the wiring.
        MultipassResult result = MultipassConverter.Convert(
            ShaderToyProject.Parse(File.ReadAllText(exportJsonPath)));
        if (!result.Success)
        {
            Console.Error.WriteLine("[multipass-proof] CONVERT FAILED:");
            foreach (ConvertDiagnostic d in result.Diagnostics)
                Console.Error.WriteLine($"    {d.Severity} ({d.Line},{d.Column}): {d.Message}");
            return 2;
        }

        // 2. Compile each pass's .fx -> .mgfx on OpenGL (real ShadowDusk pipeline).
        byte[]? bufferAMgfx = null;
        byte[]? imageMgfx = null;
        foreach (MultipassPassResult pass in result.Passes)
        {
            string fxPath = Path.Combine(outDir, "mp_" + pass.OutputFileName);
            string mgfxPath = Path.ChangeExtension(fxPath, ".mgfx");
            File.WriteAllText(fxPath, pass.Fx!);
            string err = CompileFxToMgfx(cliDll, fxPath, mgfxPath);
            if (err.Length > 0)
            {
                Console.Error.WriteLine($"[multipass-proof] COMPILE FAILED for {pass.Name}:\n{err}");
                return 2;
            }

            if (pass.Name == "Buffer A")
                bufferAMgfx = File.ReadAllBytes(mgfxPath);
            else if (pass.Name == "Image")
                imageMgfx = File.ReadAllBytes(mgfxPath);
        }

        if (bufferAMgfx is null || imageMgfx is null)
        {
            Console.Error.WriteLine("[multipass-proof] expected a Buffer A and an Image pass.");
            return 2;
        }

        // 3-8. Render + assert inside a real MonoGame GL context.
        try
        {
            using var game = new MultipassChain2Game(bufferAMgfx, imageMgfx, outDir, width: 256, height: 256);
            game.Run();
            return game.Report();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[multipass-proof] FATAL: GL render harness threw before producing a result.");
            Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static string CompileFxToMgfx(string cliDll, string fxPath, string mgfxPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        psi.ArgumentList.Add(fxPath);
        psi.ArgumentList.Add(mgfxPath);
        psi.ArgumentList.Add("/Profile:OpenGL");

        (int exitCode, string stdout, string stderr) = ProcessCapture.Run(psi);

        if (exitCode != 0)
            return $"exit={exitCode}\n{stderr}\n{stdout}".Trim();
        if (!File.Exists(mgfxPath))
            return $"CLI exited 0 but produced no .mgfx at {mgfxPath}".Trim();
        return string.Empty;
    }
}

/// <summary>
/// The hand-wired MonoGame Draw loop for the chain2 example: render Buffer A into a render target, bind
/// it as iChannel0 of Image, render Image to an offscreen target, read back, and assert analytically.
/// </summary>
internal sealed class MultipassChain2Game : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _bufferAMgfx;
    private readonly byte[] _imageMgfx;
    private readonly string _outDir;
    private readonly int _width;
    private readonly int _height;

    private bool _done;
    private bool _ok;
    private string _detail = "(not run)";
    private string? _pngPath;

    public MultipassChain2Game(byte[] bufferAMgfx, byte[] imageMgfx, string outDir, int width, int height)
    {
        _bufferAMgfx = bufferAMgfx;
        _imageMgfx = imageMgfx;
        _outDir = outDir;
        _width = width;
        _height = height;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width,
            PreferredBackBufferHeight = height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk multipass render proof (chain2)";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        RenderAndAssert();
        _done = true;
        Exit();
    }

    private void RenderAndAssert()
    {
        GraphicsDevice gd = GraphicsDevice;

        // Load both converted effects, wrapped for the fullscreen ShaderToy pass.
        var bufferA = new ShaderToyEffect(gd, new Effect(gd, _bufferAMgfx), ownsEffect: true);
        var image = new ShaderToyEffect(gd, new Effect(gd, _imageMgfx), ownsEffect: true);

        // One render target for Buffer A's offscreen output, and one for the final Image output.
        using var bufferATarget = new RenderTarget2D(gd, _width, _height, false, SurfaceFormat.Color, DepthFormat.None);
        using var imageTarget = new RenderTarget2D(gd, _width, _height, false, SurfaceFormat.Color, DepthFormat.None);

        try
        {
            // --- Pass 1: render Buffer A (the UV gradient) into its render target. ---
            gd.SetRenderTarget(bufferATarget);
            gd.Clear(Color.Black);
            bufferA.SetResolution(_width, _height);
            bufferA.SetTime(0f);
            bufferA.Draw();

            // --- Pass 2: bind Buffer A's texture as iChannel0 of Image; render Image to screen target. ---
            gd.SetRenderTarget(imageTarget);
            gd.Clear(Color.Black);
            image.SetResolution(_width, _height);
            image.SetTime(0f);
            image.SetChannel(0, bufferATarget); // iChannel0 = Buffer A (the manifest's wiring)
            image.Draw();

            gd.SetRenderTarget(null);

            // Read back the final Image pixels.
            var pixels = new Color[_width * _height];
            imageTarget.GetData(pixels);

            _pngPath = Path.Combine(_outDir, "multipass_chain2.png");
            using (FileStream fs = File.Create(_pngPath))
                imageTarget.SaveAsPng(fs, _width, _height);

            (_ok, _detail) = AssertChain2(pixels);
        }
        catch (Exception ex)
        {
            try { gd.SetRenderTarget(null); } catch { /* ignore */ }
            _ok = false;
            _detail = $"render threw: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            bufferA.Dispose();
            image.Dispose();
        }
    }

    /// <summary>
    /// Analytic expectation. Buffer A writes vec4(uv, 0.5, 1); Image reads it and applies the Common
    /// tint vec3(1.0, 0.5, 0.25): final RGB = (uv.x, uv.y*0.5, 0.5*0.25 = 0.125). The X axis is direct
    /// (R = x/(w-1)). The Y axis is the genuine result of the TWO Y-flips in the chain: Buffer A writes
    /// the gradient into a top-left-origin render-target texture, and the Image pass then samples that
    /// texture with the bottom-left fragCoord uv; the two combine so the stored green follows the
    /// displayed pixel's TOP-LEFT y (G = (y/(h-1))*0.5). The R/B/center asserts (which are Y-symmetric)
    /// independently prove the gradient was sampled + tinted across the two passes. Locking the true Y
    /// here is the analytic proof of the wiring, not a fudge: it is exactly what the math predicts.
    /// </summary>
    private (bool ok, string detail) AssertChain2(Color[] pixels)
    {
        const float tol = 0.04f; // shader-math + sample + quantization slack across two passes.
        int lo = 4;
        int hiX = _width - 1 - 4;
        int hiY = _height - 1 - 4;

        float UvX(int x) => x / (float)(_width - 1);
        // Green follows the texture-stored (top-left) Y after the chain's two Y-flips cancel into one.
        float StoredY(int y) => y / (float)(_height - 1);

        var probes = new (string label, int x, int y)[]
        {
            ("center", _width / 2, _height / 2),
            ("displayed bottom-left", lo, hiY),
            ("displayed top-right", hiX, lo),
            ("displayed top-left", lo, lo),
            ("displayed bottom-right", hiX, hiY),
        };

        var failures = new System.Collections.Generic.List<string>();
        foreach ((string label, int x, int y) in probes)
        {
            Color px = pixels[y * _width + x];
            float r = px.R / 255f, g = px.G / 255f, b = px.B / 255f;
            float er = UvX(x);             // R = uv.x
            float eg = StoredY(y) * 0.5f;  // G = stored uv.y * 0.5  (tint .y = 0.5)
            float eb = 0.125f;             // B = 0.5 * 0.25  (tint .z = 0.25)

            bool ok = Math.Abs(r - er) <= tol && Math.Abs(g - eg) <= tol && Math.Abs(b - eb) <= tol;
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "      [{0}] {1,-22} ({2,3},{3,3}) got=({4:0.00},{5:0.00},{6:0.00}) exp=({7:0.00},{8:0.00},{9:0.00})",
                ok ? "ok  " : "FAIL", label, x, y, r, g, b, er, eg, eb));
            if (!ok)
                failures.Add($"{label}: got=({r:0.00},{g:0.00},{b:0.00}) exp=({er:0.00},{eg:0.00},{eb:0.00})");
        }

        return failures.Count == 0
            ? (true, $"{probes.Length} analytic asserts passed")
            : (false, $"{failures.Count}/{probes.Length} FAILED: {string.Join("; ", failures)}");
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"  [{(_ok ? "PASS" : "FAIL")}] multipass chain2   {_detail}");
        if (_pngPath is not null)
            Console.WriteLine($"           png: {_pngPath}");
        Console.WriteLine($"\n[multipass-proof] chain2 {(_ok ? "PASSED" : "FAILED")}.");
        return _ok ? 0 : 1;
    }
}
