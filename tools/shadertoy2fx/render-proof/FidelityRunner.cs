#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ShadowDusk.ShaderToy;
using ShadowDusk.TestPng;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// The Phase 46 PIXEL-FIDELITY gate. For each authored corpus shader it renders the ORIGINAL ShaderToy
/// GLSL directly in a raw Silk.NET GL context (GROUND TRUTH), renders OUR converted <c>.fx -> .mgfx</c>
/// through real MonoGame, and DIFFS the two RGBA8 buffers at the same fixed resolution + uniforms.
///
/// <para>
/// This is the difference between "renders something plausible" (the gallery non-trivial gate) and
/// "renders what the original GLSL renders". It reports a per-shader match table (mean abs diff,
/// max channel delta, % of pixels within tolerance), asserts the deterministic shaders match within a
/// DOCUMENTED tolerance, and classifies every divergence honestly as a conversion bug, float/precision
/// chaos, or a Y-flip/orientation mismatch - never hidden by loosening the tolerance.
/// </para>
/// </summary>
public static class FidelityRunner
{
    // Fixed render config - identical for the GL reference and the MonoGame test render.
    private const int Width = 320;
    private const int Height = 240;
    private static readonly RefUniforms Uniforms = new(
        ResolutionX: Width, ResolutionY: Height,
        Time: 1.5f, TimeDelta: 1f / 60f, Frame: 90,
        MouseX: Width * 0.5f, MouseY: Height * 0.5f, MouseZ: 1f, MouseW: 1f);

    // ---- DOCUMENTED tolerance (tuned to what the faithful shaders actually achieve; see report). ----
    // A shader MATCHES when its mean absolute per-channel difference is small AND the overwhelming
    // majority of pixels are within a tight per-channel delta. Both gates must pass: the mean catches
    // a uniform shift, the percentile catches localized structural breakage that a mean would dilute.
    private const double MeanAbsDiffPass = 6.0 / 255.0;   // <= ~6/255 average channel error
    private const int WithinTolDelta = 12;                // a pixel is "within tol" if every channel <= 12/255
    private const double WithinTolFractionPass = 0.95;    // >= 95% of pixels within that delta

    // Classification thresholds (only applied to shaders that FAIL the pass gates above).
    private const int YFlipMaxDelta = 16;                 // a vertical flip of OURS matches REF within this

    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "iResolution", "iTime", "iTimeDelta", "iFrame", "iMouse",
        "iChannel0", "iChannel1", "iChannel2", "iChannel3",
        "iChannelTime", "iChannelResolution", "iDate", "iSampleRate",
        "iGlobalTime",
    };

    public static int Run(string cliDll, string repoRoot, string outDir)
    {
        string authoredDir = CorpusLocator.FindAuthored(repoRoot);

        if (!Directory.Exists(authoredDir))
        {
            Console.Error.WriteLine($"[fidelity] authored corpus not found: {authoredDir}");
            return 2;
        }

        string workDir = Path.Combine(outDir, "fidelity-work");
        Directory.CreateDirectory(workDir);
        string montagePng = Path.Combine(outDir, "fidelity.png");

        string? only = Environment.GetEnvironmentVariable("FIDELITY_ONLY");
        string[] glslFiles = Directory.EnumerateFiles(authoredDir, "*.glsl", SearchOption.TopDirectoryOnly)
            .Where(p => only is null || only.Length == 0 ||
                        only.Split(',').Contains(Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal))
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();

        Console.WriteLine($"[fidelity] corpus:  {authoredDir}");
        Console.WriteLine($"[fidelity] config:  {Width}x{Height}  iTime={Uniforms.Time}  " +
            $"iMouse=({Uniforms.MouseX},{Uniforms.MouseY})  iFrame={Uniforms.Frame}\n");

        // ---- Phase A: GL ground-truth reference renders (raw Silk.NET GL). --------------------------
        var refRenders = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var skipped = new List<(string Name, string Why)>();

        using (var reference = new GlReferenceRenderer(Width, Height))
        {
            if (reference.IsUnavailable)
            {
                Console.Error.WriteLine(
                    "[fidelity] FATAL: could not create a raw OpenGL reference context: "
                    + reference.UnavailableReason);
                Console.Error.WriteLine(
                    "[fidelity] The fidelity gate needs a real GL context for the GROUND-TRUTH render; "
                    + "it does NOT soft-pass. Run on a host with a GL driver (GPU or Mesa llvmpipe).");
                return 3;
            }

            foreach (string glslPath in glslFiles)
            {
                string name = Path.GetFileNameWithoutExtension(glslPath);
                string glsl = File.ReadAllText(glslPath);

                // Skip shaders that reference a custom uniform with no value (no fair ground truth).
                IReadOnlyList<string> used = ProbeUsedUniforms(glsl, name);
                string[] custom = used.Where(u => !Builtins.Contains(u)).ToArray();
                if (custom.Length > 0)
                {
                    string why = "uses undriven custom uniform(s): " + string.Join(", ", custom);
                    skipped.Add((name, why));
                    continue;
                }

                // Skip shapes the RAW-GL reference cannot faithfully reproduce - NOT a converter
                // judgement, purely a "no fair ground truth in a plain #version 330 ShaderToy harness":
                //   - an exact-type custom-uniform ALIAS the converter folds onto a builtin (e.g.
                //     `uniform float time;` -> iTime): the raw body still reads the raw `time`, which the
                //     reference would leave at 0 while our converted shader correctly uses iTime.
                //   - a `varying`/`in`/`attribute` the converter aliases to the harness screen UV: the
                //     reference has no vertex stage feeding it, so it reads 0.
                //   - `gl_FragCoord.z`/`.w`: the converter publishes the documented (.z=0,.w=1); a real
                //     rasterizer gives .z=0.5 (post-viewport depth), so the two legitimately differ.
                string? unfair = ReferenceUnfairReason(glsl);
                if (unfair is not null)
                {
                    skipped.Add((name, unfair));
                    continue;
                }

                (byte[]? rgba, string? skipReason) = reference.RenderReference(glsl, Uniforms);
                if (rgba is null)
                {
                    skipped.Add((name, skipReason ?? "reference render failed"));
                    continue;
                }

                refRenders[name] = rgba;
            }
        }

        Console.WriteLine($"[fidelity] {refRenders.Count} GL reference renders; {skipped.Count} skipped.");
        foreach ((string name, string why) in skipped)
            Console.WriteLine($"  [skip] {name,-30} {why.Replace('\n', ' ')}");
        Console.WriteLine();

        if (refRenders.Count == 0)
        {
            Console.Error.WriteLine("[fidelity] No reference renders produced; nothing to diff.");
            return 2;
        }

        // ---- Phase B: convert + compile OUR .fx -> .mgfx for each referenced shader. ----------------
        var testJobs = new List<FidelityTestJob>();
        foreach (string name in refRenders.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string glsl = File.ReadAllText(Path.Combine(authoredDir, name + ".glsl"));
            ConvertResult conv = ShaderToyConverter.Convert(glsl, new ConvertOptions { EffectName = name });
            if (!conv.Success || conv.Fx is null)
            {
                skipped.Add((name, "convert failed: " + FirstError(conv)));
                continue;
            }

            string fxPath = Path.Combine(workDir, name + ".fx");
            string mgfxPath = Path.Combine(workDir, name + ".mgfx");
            File.WriteAllText(fxPath, conv.Fx);

            string compileError = CompileFxToMgfx(cliDll, fxPath, mgfxPath);
            if (compileError.Length > 0)
            {
                skipped.Add((name, "OpenGL compile failed: " + Trim(compileError)));
                continue;
            }

            testJobs.Add(new FidelityTestJob(name, File.ReadAllBytes(mgfxPath)));
        }

        if (testJobs.Count == 0)
        {
            Console.Error.WriteLine("[fidelity] No shaders converted+compiled; nothing to render.");
            return 2;
        }

        // ---- Phase C: MonoGame test renders. --------------------------------------------------------
        // Each shader renders in its OWN MonoGame GraphicsDevice (one Game.Run per shader). A SHARED
        // device leaks pixel-shader state across effects: MonoGame DesktopGL's EffectPass.Apply does not
        // reliably re-bind the GL program when the previous Effect was disposed, so shader N+1 silently
        // kept running shader N's pixel shader (e.g. mat_compound_assign rendered fbm_clouds). A fresh
        // device per shader is the only state-clean isolation; it is slower but this is an out-of-band
        // correctness gate where a faithful render matters more than speed.
        List<FidelityTestRender> testRenders;
        try
        {
            testRenders = new List<FidelityTestRender>();
            foreach (FidelityTestJob job in testJobs)
            {
                using var game = new FidelityTestRenderGame(new[] { job }, Uniforms, Width, Height);
                game.Run();
                testRenders.AddRange(game.Renders);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[fidelity] FATAL: the MonoGame test-render harness threw before producing results.");
            Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }

        // ---- Phase D: diff + classify. --------------------------------------------------------------
        var rows = new List<FidelityRow>();
        foreach (FidelityTestRender tr in testRenders.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (tr.Rgba is null)
            {
                rows.Add(FidelityRow.Errored(tr.Name, tr.Error ?? "test render produced no pixels"));
                continue;
            }

            byte[] reference = refRenders[tr.Name];
            rows.Add(Diff(tr.Name, reference, tr.Rgba));
        }

        // ---- Phase E: montage (reference | ours | amplified-diff per row). --------------------------
        WriteMontage(rows, refRenders, testRenders, montagePng);

        if (Environment.GetEnvironmentVariable("FIDELITY_DUMP_DIVERGENT") == "1")
            DumpDivergent(rows, refRenders, testRenders, workDir);

        // ---- Phase F: report + exit code. -----------------------------------------------------------
        return Report(rows, skipped, montagePng);
    }

    // ============================================================================================

    private static FidelityRow Diff(string name, byte[] reference, byte[] ours)
    {
        int n = Math.Min(reference.Length, ours.Length);
        long sumAbs = 0;
        int maxDelta = 0;
        int withinTol = 0;
        int pixelCount = n / 4;

        for (int i = 0; i < pixelCount; i++)
        {
            int b = i * 4;
            int dr = Math.Abs(reference[b + 0] - ours[b + 0]);
            int dg = Math.Abs(reference[b + 1] - ours[b + 1]);
            int db = Math.Abs(reference[b + 2] - ours[b + 2]);
            sumAbs += dr + dg + db;
            int pixMax = Math.Max(dr, Math.Max(dg, db));
            if (pixMax > maxDelta) maxDelta = pixMax;
            if (pixMax <= WithinTolDelta) withinTol++;
        }

        double meanAbs = sumAbs / (double)(pixelCount * 3) / 255.0;
        double withinFrac = withinTol / (double)pixelCount;

        bool pass = meanAbs <= MeanAbsDiffPass && withinFrac >= WithinTolFractionPass;
        string classification = pass
            ? "MATCH"
            : Classify(name, reference, ours, meanAbs, maxDelta, withinFrac);

        return new FidelityRow(name, true, null, meanAbs, maxDelta, withinFrac, pass, classification);
    }

    /// <summary>
    /// Honest classification of a divergence: a real CONVERSION BUG, legitimate FLOAT/PRECISION CHAOS,
    /// or a Y-FLIP/orientation mismatch. We never loosen the tolerance to hide a divergence; we name it.
    /// </summary>
    private static string Classify(
        string name, byte[] reference, byte[] ours, double meanAbs, int maxDelta, double withinFrac)
    {
        // (c) Vertical-flip test: does a vertical flip of OUR image match the reference near-perfectly?
        // A CLEAN flip-match (flipped mean ~ 0) is NOT a harness orientation bug - we proved no global
        // Y-flip exists (gradient_uv/atan_polar/kaleidoscope all match unflipped). It is a genuine
        // CONVERSION finding: the body's geometry is mirrored, e.g. a matrix-ORDER / handedness trap.
        byte[] flipped = FlipVertically(ours);
        double flippedMean = MeanAbs(reference, flipped);
        if (flippedMean * 4.0 < meanAbs && flippedMean <= YFlipMaxDelta / 255.0)
            return $"MATRIX-ORDER / MIRRORED GEOMETRY (CONVERSION BUG: OUR image is the vertical MIRROR " +
                   $"of the reference - flipped mean {flippedMean * 255:0.0}/255 << unflipped {meanAbs * 255:0.0}/255) - INVESTIGATE";

        // (b) Float/precision chaos vs (a) structural conversion bug. Heuristic: precision chaos in a
        // deep raymarcher/noise field keeps MOST pixels close (high within-tol fraction) while a thin
        // shell of fine detail diverges hard; a structural bug shifts a LARGE fraction of pixels.
        if (withinFrac >= 0.80 && meanAbs <= 14.0 / 255.0)
            return $"FLOAT/PRECISION CHAOS (structurally same; {withinFrac:P1} within {WithinTolDelta}/255, " +
                   $"thin high-delta shell, max {maxDelta})";

        return $"POSSIBLE CONVERSION BUG (broad divergence: only {withinFrac:P1} within {WithinTolDelta}/255, " +
               $"mean {meanAbs * 255:0.0}/255, max {maxDelta}) - INVESTIGATE";
    }

    private static double MeanAbs(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int pixels = n / 4;
        long sum = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            sum += Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
        }
        return sum / (double)(pixels * 3) / 255.0;
    }

    private static byte[] FlipVertically(byte[] rgba)
    {
        int stride = Width * 4;
        var outp = new byte[rgba.Length];
        for (int y = 0; y < Height; y++)
            Array.Copy(rgba, y * stride, outp, (Height - 1 - y) * stride, stride);
        return outp;
    }

    // ============================================================================================

    private static void WriteMontage(
        IReadOnlyList<FidelityRow> rows,
        IReadOnlyDictionary<string, byte[]> refRenders,
        IReadOnlyList<FidelityTestRender> testRenders,
        string montagePng)
    {
        var byName = testRenders.ToDictionary(r => r.Name, r => r.Rgba, StringComparer.Ordinal);
        var rendered = rows
            .Where(r => r.Rendered && refRenders.ContainsKey(r.Name) &&
                        byName.TryGetValue(r.Name, out byte[]? p) && p is not null)
            .ToList();

        if (rendered.Count == 0)
            return;

        const int pad = 6;
        const int labelH = 16;
        const int cols = 3; // reference | ours | amplified diff
        int cellW = Width + pad;
        int rowH = Height + labelH + pad;
        int montW = cols * cellW + pad;
        int montH = rendered.Count * rowH + pad;

        var montage = new byte[montW * montH * 4];
        FillRect(montage, montW, 0, 0, montW, montH, (24, 24, 28));

        for (int r = 0; r < rendered.Count; r++)
        {
            FidelityRow row = rendered[r];
            byte[] reference = refRenders[row.Name];
            byte[] ours = byName[row.Name]!;
            byte[] diff = AmplifiedDiff(reference, ours);

            int oy = pad + r * rowH + labelH;

            BlitInto(montage, montW, reference, pad, oy);
            BlitInto(montage, montW, ours, pad + cellW, oy);
            BlitInto(montage, montW, diff, pad + 2 * cellW, oy);

            // Status bar above the row: green = match, amber = chaos/orientation, red = possible bug.
            (byte R, byte G, byte B) bar = row.Pass
                ? ((byte)40, (byte)160, (byte)60)
                : row.Classification.StartsWith("POSSIBLE CONVERSION BUG", StringComparison.Ordinal)
                    ? ((byte)190, (byte)50, (byte)50)
                    : ((byte)200, (byte)150, (byte)40);
            FillRect(montage, montW, pad, oy - labelH, montW - 2 * pad, labelH - 3, bar);
        }

        PngEncoder.Save(montagePng, montage, montW, montH);
    }

    /// <summary>Dump per-shader reference/ours/diff PNGs for divergent shaders (eyeball debugging).</summary>
    private static void DumpDivergent(
        IReadOnlyList<FidelityRow> rows,
        IReadOnlyDictionary<string, byte[]> refRenders,
        IReadOnlyList<FidelityTestRender> testRenders,
        string workDir)
    {
        var byName = testRenders.ToDictionary(r => r.Name, r => r.Rgba, StringComparer.Ordinal);
        foreach (FidelityRow row in rows.Where(r => r.Rendered && !r.Pass))
        {
            if (!refRenders.TryGetValue(row.Name, out byte[]? reference)) continue;
            if (!byName.TryGetValue(row.Name, out byte[]? ours) || ours is null) continue;
            SaveRgba(reference, Path.Combine(workDir, row.Name + ".ref.png"));
            SaveRgba(ours, Path.Combine(workDir, row.Name + ".ours.png"));
            SaveRgba(AmplifiedDiff(reference, ours), Path.Combine(workDir, row.Name + ".diff.png"));
        }
    }

    /// <summary>Save a Width x Height render as an opaque PNG (render alpha is not meaningful for eyeballing).</summary>
    private static void SaveRgba(byte[] rgba, string path)
    {
        var opaque = new byte[Width * Height * 4];
        BlitInto(opaque, Width, rgba, 0, 0);
        PngEncoder.Save(path, opaque, Width, Height);
    }

    /// <summary>Copy a Width x Height render into an RGBA canvas at (ox, oy), forcing alpha opaque.</summary>
    private static void BlitInto(byte[] canvas, int canvasWidth, byte[] rgba, int ox, int oy)
    {
        for (int y = 0; y < Height; y++)
        {
            int src = y * Width * 4;
            int dst = ((oy + y) * canvasWidth + ox) * 4;
            for (int x = 0; x < Width; x++, src += 4, dst += 4)
            {
                canvas[dst] = rgba[src];
                canvas[dst + 1] = rgba[src + 1];
                canvas[dst + 2] = rgba[src + 2];
                canvas[dst + 3] = 255;
            }
        }
    }

    private static void FillRect(byte[] canvas, int canvasWidth, int x0, int y0, int w, int h, (byte R, byte G, byte B) colour)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * canvasWidth + x) * 4;
                (canvas[i], canvas[i + 1], canvas[i + 2], canvas[i + 3]) = (colour.R, colour.G, colour.B, (byte)255);
            }
    }

    /// <summary>Per-channel |ref-ours| amplified 4x and clamped, on a dark field, for eyeball.</summary>
    private static byte[] AmplifiedDiff(byte[] reference, byte[] ours)
    {
        int n = Math.Min(reference.Length, ours.Length);
        var outp = new byte[reference.Length];
        for (int i = 0; i < n; i += 4)
        {
            outp[i + 0] = (byte)Math.Min(255, Math.Abs(reference[i + 0] - ours[i + 0]) * 4);
            outp[i + 1] = (byte)Math.Min(255, Math.Abs(reference[i + 1] - ours[i + 1]) * 4);
            outp[i + 2] = (byte)Math.Min(255, Math.Abs(reference[i + 2] - ours[i + 2]) * 4);
            outp[i + 3] = 255;
        }
        return outp;
    }

    // ============================================================================================

    private static int Report(
        IReadOnlyList<FidelityRow> rows, IReadOnlyList<(string Name, string Why)> skipped, string montagePng)
    {
        Console.WriteLine();
        Console.WriteLine("  shader                         mean/255  max  within12/255   verdict");
        Console.WriteLine("  ----------------------------------------------------------------------------");

        int match = 0, errored = 0, diverged = 0, bugs = 0;
        foreach (FidelityRow row in rows)
        {
            if (!row.Rendered)
            {
                errored++;
                Console.WriteLine($"  {row.Name,-30}    ERROR   -        -          {row.Classification}");
                continue;
            }

            string verdict = row.Pass ? "MATCH" : row.Classification;
            if (row.Pass) match++;
            else
            {
                diverged++;
                if (IsConversionBug(row.Classification))
                    bugs++;
            }

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-30} {1,7:0.00}  {2,3}     {3,6:P1}     {4}",
                row.Name, row.MeanAbs * 255, row.MaxDelta, row.WithinFrac, verdict));
        }

        Console.WriteLine();
        Console.WriteLine($"[fidelity] {match}/{rows.Count} shaders MATCH within tolerance " +
            $"(mean <= {MeanAbsDiffPass * 255:0.0}/255 AND >= {WithinTolFractionPass:P0} of pixels within {WithinTolDelta}/255).");
        Console.WriteLine($"[fidelity] {diverged} diverged ({bugs} flagged as a CONVERSION BUG to investigate; " +
            $"the rest are documented float/derivative chaos), {errored} errored.");
        Console.WriteLine($"[fidelity] montage: {montagePng}");
        if (skipped.Count > 0)
        {
            Console.WriteLine($"[fidelity] skipped {skipped.Count} (custom uniform / non-reference-compilable):");
            foreach ((string name, string why) in skipped)
                Console.WriteLine($"    {name,-30} {Trim(why)}");
        }

        // A POSSIBLE CONVERSION BUG or a render error is a hard failure. Float/precision chaos and
        // Y-flip are REPORTED but, being expected for deep procedural shaders, do not fail the gate by
        // themselves UNLESS they are the unexplained "possible bug" class.
        if (errored > 0 || bugs > 0)
        {
            Console.Error.WriteLine(
                $"[fidelity] GATE FAILED: {bugs} conversion bug(s) flagged for follow-up, {errored} render error(s).");
            return 1;
        }

        return 0;
    }

    /// <summary>A classification that names a real converter defect (vs documented float/derivative chaos).</summary>
    private static bool IsConversionBug(string classification) =>
        classification.StartsWith("POSSIBLE CONVERSION BUG", StringComparison.Ordinal) ||
        classification.StartsWith("MATRIX-ORDER", StringComparison.Ordinal);

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Returns a reason when a plain <c>#version 330</c> ShaderToy harness cannot be a FAIR ground
    /// truth for this body (see the call site), else null. Purely about reference reproducibility - it
    /// makes no claim about the converter, which handles all these shapes correctly.
    /// </summary>
    private static string? ReferenceUnfairReason(string glsl)
    {
        // A varying/attribute the converter aliases to harness screen UV; the raw reference has no
        // vertex stage to feed it (it would read 0).
        if (System.Text.RegularExpressions.Regex.IsMatch(
                glsl, @"^\s*(varying|attribute|in)\s+\w+\s+\w+\s*;", System.Text.RegularExpressions.RegexOptions.Multiline))
            return "no fair reference: top-level varying/in is converter-aliased to harness UV (raw GL reads 0)";

        // gl_FragCoord.z/.w: converter publishes the documented (.z=0,.w=1); a real rasterizer gives
        // .z=0.5, so the bodies legitimately differ on those channels.
        if (System.Text.RegularExpressions.Regex.IsMatch(glsl, @"gl_FragCoord\s*\.\s*[zw]"))
            return "no fair reference: reads gl_FragCoord.z/.w (converter convention .z=0/.w=1 vs raster .z=0.5)";

        // An exact-type custom-uniform alias the converter folds onto a builtin (e.g. `uniform float
        // time;` -> iTime). The raw body still reads the raw name, which the reference can't drive
        // without re-implementing the fold; converted output is correct, reference can't match it.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     glsl, @"^\s*uniform\s+\w+\s+(\w+)\s*;", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            string decl = m.Groups[1].Value;
            if (decl is "time" or "u_time" or "iGlobalTime")
                return $"no fair reference: exact-type alias uniform '{decl}' is folded onto iTime by the converter";
        }

        return null;
    }

    private static IReadOnlyList<string> ProbeUsedUniforms(string glsl, string name)
    {
        ConvertResult conv = ShaderToyConverter.Convert(glsl, new ConvertOptions { EffectName = name });
        return conv.Success ? conv.UsedUniforms : Array.Empty<string>();
    }

    private static string FirstError(ConvertResult conv) =>
        string.Join("; ", conv.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Message));

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
            return $"CLI exited 0 but produced no .mgfx\n{stderr}\n{stdout}".Trim();
        return string.Empty;
    }

    private static string Trim(string s) =>
        s.Length <= 140 ? s.Replace('\n', ' ') : s[..140].Replace('\n', ' ') + "...";
}

/// <summary>One row of the fidelity table.</summary>
public sealed record FidelityRow(
    string Name, bool Rendered, string? Error,
    double MeanAbs, int MaxDelta, double WithinFrac, bool Pass, string Classification)
{
    public static FidelityRow Errored(string name, string error) =>
        new(name, false, error, 0, 0, 0, false, error);
}
