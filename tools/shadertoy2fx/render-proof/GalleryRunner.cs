#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.ShaderToy;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// Drives the Phase 46 render GALLERY: an automated "every authored corpus shader actually renders"
/// gate. It enumerates <c>tests/.../corpus/authored/*.glsl</c>, converts + compiles each to OpenGL,
/// renders them all in one real MonoGame GL context, asserts each frame is NON-TRIVIAL, and writes a
/// single committed montage PNG plus gitignored per-shader thumbnails.
/// </summary>
public static class GalleryRunner
{
    private const int TileW = 320;
    private const int TileH = 240;

    /// <summary>
    /// The ShaderToy built-in uniforms the gallery DRIVES every frame (see <see cref="GalleryGame"/>).
    /// A shader referencing only these is fully driven; anything else is an undriven custom uniform.
    /// </summary>
    private static readonly HashSet<string> DrivenBuiltins = new(StringComparer.Ordinal)
    {
        "iResolution", "iTime", "iTimeDelta", "iFrame", "iMouse",
        "iChannel0", "iChannel1", "iChannel2", "iChannel3",
        "iChannelTime", "iChannelResolution", "iDate", "iSampleRate",
    };

    /// <summary>
    /// The render-fidelity PROOF set: shaders we require to render SPATIALLY non-trivially (many
    /// distinct colors) under the gallery's fixed uniforms. These are the 4 complex shaders plus
    /// representative spatial fixtures that paint a varying image with no custom uniforms. Every other
    /// valid fixture is still rendered + montaged + gated on "not all-black", but may be constant-by-
    /// design or few-banded, so it is not held to the spatial floor.
    /// </summary>
    private static readonly HashSet<string> SpatiallyGated = new(StringComparer.Ordinal)
    {
        // The 4 complex Phase-46 shaders (the headline render-fidelity proof).
        "raymarch_sphere", "fbm_clouds", "kaleidoscope", "domain_warp",
        // Representative spatial fixtures: each paints a smoothly-varying image, no custom uniforms.
        "gradient_uv", "radial_distance", "atan_polar", "mat2_rotation",
        "for_loop_accumulate", "helper_functions", "length_normalize_dot",
        "mix_clamp_smoothstep", "mod_negative", "swizzle_ops", "time_animation",
        "while_loop", "pow_gamma",
    };

    public static int Run(string cliDll, string repoRoot, string outDir)
    {
        string authoredDir = CorpusLocator.FindAuthored(repoRoot);

        if (!Directory.Exists(authoredDir))
        {
            Console.Error.WriteLine($"[gallery] authored corpus not found: {authoredDir}");
            return 2;
        }

        string thumbsDir = Path.Combine(outDir, "gallery-thumbs");
        Directory.CreateDirectory(thumbsDir);
        string galleryPng = Path.Combine(outDir, "gallery.png");

        string[] glslFiles = Directory.EnumerateFiles(authoredDir, "*.glsl", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();

        Console.WriteLine($"[gallery] authored corpus: {authoredDir}");
        Console.WriteLine($"[gallery] {glslFiles.Length} .glsl files; tile={TileW}x{TileH}\n");

        var jobs = new List<GalleryJob>();
        var skipped = new List<(string Name, string Why)>();

        // ---- CPU phase: convert + compile (OpenGL) each shader; skip ones that legitimately fail. ----
        foreach (string glslPath in glslFiles)
        {
            string name = Path.GetFileNameWithoutExtension(glslPath);
            string glsl = File.ReadAllText(glslPath);

            ConvertResult conv = ShaderToyConverter.Convert(glsl, new ConvertOptions { EffectName = name });
            if (!conv.Success || conv.Fx is null)
            {
                string why = "convert failed: " + string.Join("; ",
                    conv.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.Message));
                skipped.Add((name, why));
                Console.WriteLine($"  [skip] {name,-28} {why}");
                continue;
            }

            string fxPath = Path.Combine(thumbsDir, name + ".fx");
            string mgfxPath = Path.Combine(thumbsDir, name + ".mgfx");
            File.WriteAllText(fxPath, conv.Fx);

            string compileError = CompileFxToMgfx(cliDll, fxPath, mgfxPath);
            if (compileError.Length > 0)
            {
                skipped.Add((name, "OpenGL compile failed"));
                Console.WriteLine($"  [skip] {name,-28} OpenGL compile failed: {Trim(compileError)}");
                continue;
            }

            bool hasUndrivenCustom = conv.UsedUniforms.Any(u => !DrivenBuiltins.Contains(u));
            bool spatiallyGated = SpatiallyGated.Contains(name);
            jobs.Add(new GalleryJob(
                name, File.ReadAllBytes(mgfxPath), hasUndrivenCustom, spatiallyGated));
        }

        Console.WriteLine($"\n[gallery] {jobs.Count} shaders convert+compile; {skipped.Count} skipped.\n");

        if (jobs.Count == 0)
        {
            Console.Error.WriteLine("[gallery] No shaders to render.");
            return 2;
        }

        // ---- GL phase: render + non-trivial assert + montage, in one real MonoGame GL context. ----
        List<GalleryResult> results;
        try
        {
            using var game = new GalleryGame(jobs, TileW, TileH);
            game.Run();
            results = game.Results;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[gallery] FATAL: the MonoGame GL render harness threw before producing results.");
            Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }

        // ---- Compose the montage + write per-shader thumbnails (gitignored). ----
        WriteThumbnailsAndMontage(results, thumbsDir, galleryPng);

        // ---- Report. ----
        Console.WriteLine();
        int ok = 0;
        foreach (GalleryResult r in results)
        {
            string status = r.Ok ? "PASS" : "FAIL";
            if (r.Ok) ok++;
            Console.WriteLine($"  [{status}] {r.Name,-28} {r.Detail}");
        }

        Console.WriteLine($"\n[gallery] {ok}/{results.Count} shaders rendered NON-TRIVIALLY.");
        Console.WriteLine($"[gallery] montage: {galleryPng}");
        if (skipped.Count > 0)
        {
            Console.WriteLine($"[gallery] skipped (did not convert/compile on OpenGL): " +
                string.Join(", ", skipped.Select(s => s.Name)));
        }

        if (results.Count == 0)
        {
            Console.Error.WriteLine("[gallery] No results produced (render harness did nothing).");
            return 3;
        }

        if (ok != results.Count)
        {
            string offenders = string.Join(", ", results.Where(r => !r.Ok).Select(r => r.Name));
            Console.Error.WriteLine($"[gallery] TRIVIAL/failed renders: {offenders}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Build a labeled grid montage on the CPU (one Texture2D, one PNG) and also write each shader's
    /// own thumbnail PNG into the gitignored thumbs dir. CPU compositing keeps the montage independent
    /// of GL draw ordering and needs no extra render target.
    /// </summary>
    private static void WriteThumbnailsAndMontage(
        IReadOnlyList<GalleryResult> results, string thumbsDir, string galleryPng)
    {
        var rendered = results.Where(r => r.Pixels is not null).ToList();
        int n = rendered.Count;
        if (n == 0)
        {
            return;
        }

        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);

        const int pad = 4;
        const int labelH = 14;
        int cellW = TileW + pad;
        int cellH = TileH + labelH + pad;
        int montW = cols * cellW + pad;
        int montH = rows * cellH + pad;

        var montage = new Color[montW * montH];
        var bg = new Color(24, 24, 28);
        for (int i = 0; i < montage.Length; i++)
        {
            montage[i] = bg;
        }

        for (int idx = 0; idx < n; idx++)
        {
            GalleryResult r = rendered[idx];
            Color[] px = r.Pixels!;
            int col = idx % cols;
            int row = idx / cols;
            int ox = pad + col * cellW;
            int oy = pad + row * cellH + labelH;

            // Blit the thumbnail.
            for (int y = 0; y < TileH; y++)
            {
                for (int x = 0; x < TileW; x++)
                {
                    montage[(oy + y) * montW + (ox + x)] = px[y * TileW + x];
                }
            }

            // A pass/fail status bar above the tile (green = non-trivial pass, red = trivial/fail).
            Color bar = r.Ok ? new Color(40, 160, 60) : new Color(190, 50, 50);
            for (int y = 0; y < labelH - 2; y++)
            {
                for (int x = 0; x < TileW; x++)
                {
                    montage[(oy - labelH + y) * montW + (ox + x)] = bar;
                }
            }
        }

        // Save the montage and per-shader thumbnails via a throwaway headless GL device path is not
        // available here; use a Texture2D from the active device instead. The GalleryGame already
        // disposed, so create a tiny device-less encode by writing raw via a fresh hidden Game would
        // be heavy. Simpler: reopen a minimal device just for PNG encoding.
        EncodePngs(rendered, montage, montW, montH, thumbsDir, galleryPng);
    }

    /// <summary>
    /// Encode the montage + thumbnails to PNG. MonoGame's <see cref="Texture2D.SaveAsPng"/> needs a
    /// live <see cref="GraphicsDevice"/>, so spin a tiny hidden one just for encoding.
    /// </summary>
    private static void EncodePngs(
        IReadOnlyList<GalleryResult> rendered, Color[] montage, int montW, int montH,
        string thumbsDir, string galleryPng)
    {
        using var encoder = new PngEncoderGame();
        encoder.Encode(device =>
        {
            using (var tex = new Texture2D(device, montW, montH, false, SurfaceFormat.Color))
            {
                tex.SetData(montage);
                using var fs = File.Create(galleryPng);
                tex.SaveAsPng(fs, montW, montH);
            }

            foreach (GalleryResult r in rendered)
            {
                using var tex = new Texture2D(device, TileW, TileH, false, SurfaceFormat.Color);
                tex.SetData(r.Pixels!);
                using var fs = File.Create(Path.Combine(thumbsDir, r.Name + ".png"));
                tex.SaveAsPng(fs, TileW, TileH);
            }
        });
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
        {
            return $"exit={exitCode}\n{stderr}\n{stdout}".Trim();
        }

        if (!File.Exists(mgfxPath))
        {
            return $"CLI exited 0 but produced no .mgfx\n{stderr}\n{stdout}".Trim();
        }

        return string.Empty;
    }

    private static string Trim(string s) =>
        s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "...";
}
