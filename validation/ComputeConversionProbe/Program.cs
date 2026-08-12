// Phase 58 Area D / D1 - the hand-conversion probe.
//
//   dotnet run -c Release --project validation/ComputeConversionProbe
//
// THE QUESTION, and nothing wider: can a human convincingly re-express one real compute
// shader as something stock MonoGame can actually run? Phase 58 section 6.5.4 makes this a
// GATE - "if a human cannot do it convincingly, no converter should be written" - precisely
// so nobody builds a compute-to-pixel transpiler on the strength of the idea alone. This is
// the same shape as the Phase-0 hand-translate probe that gated Phase 46's ShaderToy parser.
//
// THE SUBJECT is cpt-max's `compute_write_to_texture` sample: an odd-even transposition sort
// that orders each scanline's pixels by hue, one pass per phase. It is named in section
// 6.5.1 as the most tractable shape ("read texture, write texture"). The hand conversion,
// and the scatter-vs-gather obstacle that is the actual finding, are documented at length in
// HueSortConverted.fx next to this file.
//
// THE ORACLE is a CPU implementation of the ORIGINAL compute kernel - transcribed from
// cpt-max's HLSL statement by statement, in its native scatter form, writing two pixels per
// iteration exactly as the GPU kernel does. Section 6.5.3 asks for fidelity against the
// original shader's own behaviour, and with the fork target declined (section 5.1, owner
// decision 2026-08-11) there is no fork runtime to render against, so the reference is the
// kernel's SEMANTICS rather than its pixels. That is a weaker oracle than a fork render and
// this file says so plainly; it is still decisive for the question actually being asked,
// because a conversion that disagrees with the original kernel's own definition has failed
// no matter what any runtime shows.
//
// WHAT WOULD MAKE THIS PROBE VACUOUS, and the arms that prevent it:
//   * A sort that is already sorted proves nothing -> the input is deliberately hue-shuffled
//     and arm 1 asserts the input does NOT already match the expected output.
//   * A shader that passes everything through unchanged would match on an already-ordered
//     row -> arm 2 asserts the render actually CHANGED pixels versus the input.
//   * One phase of an odd-even sort barely moves anything -> the probe runs the full
//     multi-pass ping-pong to a FULLY sorted result, which is the real workload.
//
// Self-asserting (exit 0 iff every check passes). Needs a real GL context; set
// SHADOWDUSK_REQUIRE_GL=1 to turn a missing-context skip into a hard failure.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

// The sort is exact integer-comparison work on 8-bit colour, so the only slack needed is
// for point-sampling/round-trip noise, not for arithmetic drift.
int tolerance = 1;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot = ShaderInputs.FindRepoRoot();
string fxPath   = Path.Combine(repoRoot, "validation", "ComputeConversionProbe", "HueSortConverted.fx");
string outDir   = Path.Combine(repoRoot, "validation", "output-computeconversion");

Console.WriteLine("[compute-probe] Phase 58 Area D / D1 - compute-to-pixel-shader hand-conversion probe");
Console.WriteLine($"[compute-probe] fx:  {fxPath}");
Console.WriteLine($"[compute-probe] out: {outDir}  tolerance: {tolerance}\n");

if (!File.Exists(fxPath))
{
    Console.Error.WriteLine($"[compute-probe] FATAL: converted shader not found: {fxPath}");
    return 2;
}

// ---- Compile the CONVERTED shader through the real, unmodified product pipeline. That it
//      compiles at all is the first half of the claim: the conversion output is ordinary
//      .fx, needing no ShadowDusk change whatsoever (the additive shape section 6.5 wants).
string src = await File.ReadAllTextAsync(fxPath);
var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
{
    Target          = PlatformTarget.OpenGL,
    IncludeResolver = new FileSystemIncludeResolver(),
    SourceFileName  = fxPath,
});

if (result.IsFailure)
{
    Console.Error.WriteLine("[compute-probe] FATAL: the converted shader does not compile:");
    foreach (var e in result.Error)
        Console.Error.WriteLine($"    {e.Code}: {e.Message}");
    return 1;
}

byte[] mgfx = result.Value.Data;
Directory.CreateDirectory(outDir);
await File.WriteAllBytesAsync(Path.Combine(outDir, "hue-sort-converted-gl.mgfx"), mgfx);
Console.WriteLine($"[compute-probe] converted shader compiles for OpenGL OK ({mgfx.Length} bytes).");

foreach (var w in result.Value.Warnings)
    Console.WriteLine($"[compute-probe] warning {w.Code}: {w.Message}");

using var game = new ComputeConversionGame(outDir, mgfx, tolerance);
game.Run();

int passed = 0;
Console.WriteLine("\n[compute-probe] results:");
foreach (var o in game.Outcomes)
{
    if (o.Pass) passed++;
    Console.WriteLine($"  [{(o.Pass ? "PASS" : "FAIL")}] {o.Name,-42} {o.Detail}");
}

bool allPassed = passed == game.Outcomes.Count && game.Outcomes.Count > 0;
Console.WriteLine($"\n[compute-probe] {passed}/{game.Outcomes.Count} checks passed.");
Console.WriteLine(allPassed
    ? "[compute-probe] D1 VERDICT: the hand conversion reproduces the original kernel's defined result."
    : "[compute-probe] D1 VERDICT: the hand conversion does NOT reproduce the kernel. See failures above.");
return allPassed ? 0 : 1;

internal sealed record ProbeOutcome(string Name, bool Pass, string Detail);

internal sealed class ComputeConversionGame : Game
{
    // Small enough to reason about by hand, wide enough that an odd-even sort needs many
    // phases (a 24-wide row needs up to 24 to fully order).
    private const int Width  = 24;
    private const int Height = 8;

    private readonly GraphicsDeviceManager _gdm;
    private readonly string _outDir;
    private readonly byte[] _mgfx;
    private readonly int _tolerance;

    private SpriteBatch _sb = null!;
    private bool _done;

    public List<ProbeOutcome> Outcomes { get; } = new();

    public ComputeConversionGame(string outDir, byte[] mgfx, int tolerance)
    {
        _outDir    = outDir;
        _mgfx      = mgfx;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Width,
            PreferredBackBufferHeight = Height,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Phase 58 D1 compute-conversion probe (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        GraphicsDevice.Clear(Color.Black);

        try { RunProbe(); }
        catch (Exception ex)
        {
            Outcomes.Add(new ProbeOutcome("harness", false,
                $"unhandled exception: {ex.GetType().Name}: {ex.Message}"));
        }

        _done = true;
        Exit();
    }

    private void RunProbe()
    {
        Effect effect;
        try { effect = new Effect(GraphicsDevice, _mgfx); }
        catch (Exception ex)
        {
            Outcomes.Add(new ProbeOutcome("converted effect loads in real MonoGame", false,
                $"{ex.GetType().Name}: {ex.Message}"));
            return;
        }
        Outcomes.Add(new ProbeOutcome("converted effect loads in real MonoGame", true,
            "new Effect(GraphicsDevice, mgfx) succeeded"));

        Color[] input = BuildShuffledInput();

        // The number of phases an odd-even transposition sort needs to guarantee ordering
        // is the row length. Running the full count is the point: a single phase would let
        // a nearly-no-op shader pass.
        const int phases = Width;

        Color[] cpuExpected = CpuReferenceSort(input, phases);

        // Arm 1 (anti-vacuity): the expected result must actually DIFFER from the input,
        // or "the GPU matched the CPU" would be satisfied by a shader that does nothing.
        int inputVsExpected = MaxChannelDelta(input, cpuExpected);
        Outcomes.Add(new ProbeOutcome("the sort is a real transformation", inputVsExpected > _tolerance,
            $"input vs CPU-sorted maxd {inputVsExpected} (must exceed tolerance {_tolerance}, else the probe is vacuous)"));

        using var source = new Texture2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color);
        source.SetData(input);

        using var rtA = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        using var rtB = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);

        effect.Parameters["Width"].SetValue((float)Width);
        effect.Parameters["TexelWidth"].SetValue(1f / Width);

        // The host recipe from HueSortConverted.fx's header, executed: ping-pong two render
        // targets, one full-screen draw per phase, alternating OffsetX. This is the part a
        // converter could never write for the user (section 6.5.2) and is why any Area D
        // output would be "a shader PLUS a recipe".
        Texture2D read = source;
        RenderTarget2D write = rtA;

        for (int phase = 0; phase < phases; phase++)
        {
            effect.Parameters["OffsetX"].SetValue((float)(phase % 2));

            GraphicsDevice.SetRenderTarget(write);
            GraphicsDevice.Clear(Color.Transparent);

            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
                      DepthStencilState.None, RasterizerState.CullNone, effect);
            _sb.Draw(read, new Rectangle(0, 0, Width, Height), Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            read  = write;
            write = ReferenceEquals(write, rtA) ? rtB : rtA;
        }

        var gpu = new Color[Width * Height];
        ((RenderTarget2D)read).GetData(gpu);

        SavePng(Path.Combine(_outDir, "input.png"), input);
        SavePng(Path.Combine(_outDir, "gpu-converted.png"), gpu);
        SavePng(Path.Combine(_outDir, "cpu-reference.png"), cpuExpected);

        // Arm 2 (anti-vacuity): the GPU must have CHANGED something. A pass-through shader
        // would sail through a comparison against an input that happened to be close.
        int gpuVsInput = MaxChannelDelta(input, gpu);
        Outcomes.Add(new ProbeOutcome("the shader actually rewrote pixels", gpuVsInput > _tolerance,
            $"input vs GPU maxd {gpuVsInput} (a pass-through shader would score 0 here)"));

        // Arm 3 (the claim): the converted PIXEL shader, run in real MonoGame, reproduces
        // what the ORIGINAL COMPUTE kernel is defined to produce.
        int gpuVsCpu = MaxChannelDelta(cpuExpected, gpu);
        int differing = CountDifferingPixels(cpuExpected, gpu, _tolerance);
        Outcomes.Add(new ProbeOutcome("GPU pixel shader == CPU compute kernel", gpuVsCpu <= _tolerance,
            $"maxd {gpuVsCpu} over {Width * Height} px, {differing} px beyond tolerance {_tolerance}"));

        // Arm 4: the result is genuinely hue-ordered per row. Arm 3 alone would still pass
        // if BOTH implementations were wrong in the same way; this checks the property the
        // kernel exists to establish, independently of either implementation.
        int unorderedRows = CountUnorderedRows(gpu);
        Outcomes.Add(new ProbeOutcome("every row is hue-ordered after the sort", unorderedRows == 0,
            $"{unorderedRows} of {Height} rows out of order (independent property check)"));
    }

    // ---------------------------------------------------------------------------------
    // The CPU oracle: cpt-max's kernel transcribed statement for statement, kept in its
    // NATIVE SCATTER FORM (writes two pixels per iteration) rather than restructured into
    // the gather form the pixel shader uses. That is deliberate - if the oracle were
    // written in the gather form, it would share the conversion's central assumption and
    // could not falsify it.
    // ---------------------------------------------------------------------------------

    private static float HueFromRgb(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float minimum = MathF.Min(r, MathF.Min(g, b));
        float maximum = MathF.Max(r, MathF.Max(g, b));
        float delta   = maximum - minimum;

        float hue = delta == 0 ? 0
            : (r == maximum) ?     (g - b) / delta
            : (g == maximum) ? 2 + (b - r) / delta
            :                  4 + (r - g) / delta;

        hue *= 60;
        return hue >= 0 ? hue : hue + 360;
    }

    private static Color[] CpuReferenceSort(Color[] input, int phases)
    {
        var current = (Color[])input.Clone();

        for (int phase = 0; phase < phases; phase++)
        {
            int offsetX = phase % 2;
            var next = (Color[])current.Clone();

            for (int y = 0; y < Height; y++)
            {
                // One "thread" per pair, exactly as `Dispatch(w/2/8, h/8, 1)` launches them.
                for (int gx = 0; gx * 2 + offsetX + 1 < Width + 1; gx++)
                {
                    int xL = gx * 2 + offsetX;
                    int xR = xL + 1;
                    if (xL >= Width) break;

                    Color colL = current[y * Width + xL];
                    Color colR = xR < Width ? current[y * Width + xR] : colL;

                    bool exceedBorder = xR >= Width;
                    bool swap = HueFromRgb(colL) > HueFromRgb(colR) && !exceedBorder;

                    next[y * Width + xL] = swap ? colR : colL;
                    if (!exceedBorder)
                        next[y * Width + xR] = swap ? colL : colR;
                }
            }

            current = next;
        }

        return current;
    }

    // A hue-shuffled input: every row holds the same set of saturated hues in a scrambled,
    // deterministic order, so the sorted result is well-defined and visibly different.
    private static Color[] BuildShuffledInput()
    {
        var px = new Color[Width * Height];

        for (int y = 0; y < Height; y++)
        {
            // A fixed co-prime stride per row gives a different scramble per row with no RNG.
            int stride = 5 + y * 2;
            for (int x = 0; x < Width; x++)
            {
                int slot = (x * stride + y * 3) % Width;
                float hue = slot * (360f / Width);
                px[y * Width + x] = HsvToRgb(hue, 1f, 1f);
            }
        }

        return px;
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float hp = h / 60f;
        float x = c * (1 - MathF.Abs(hp % 2 - 1));
        float r, g, b;

        if      (hp < 1) { r = c; g = x; b = 0; }
        else if (hp < 2) { r = x; g = c; b = 0; }
        else if (hp < 3) { r = 0; g = c; b = x; }
        else if (hp < 4) { r = 0; g = x; b = c; }
        else if (hp < 5) { r = x; g = 0; b = c; }
        else             { r = c; g = 0; b = x; }

        float m = v - c;
        return new Color((int)MathF.Round((r + m) * 255),
                         (int)MathF.Round((g + m) * 255),
                         (int)MathF.Round((b + m) * 255), 255);
    }

    private static int CountUnorderedRows(Color[] px)
    {
        int bad = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 1; x < Width; x++)
            {
                // A one-degree slack absorbs the 8-bit round trip; the hues here are ~15
                // degrees apart, so this cannot mask a real ordering failure.
                if (HueFromRgb(px[y * Width + x]) < HueFromRgb(px[y * Width + x - 1]) - 1f)
                {
                    bad++;
                    break;
                }
            }
        }
        return bad;
    }

    private static int MaxChannelDelta(Color[] a, Color[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i].R - b[i].R));
            max = Math.Max(max, Math.Abs(a[i].G - b[i].G));
            max = Math.Max(max, Math.Abs(a[i].B - b[i].B));
        }
        return max;
    }

    private static int CountDifferingPixels(Color[] a, Color[] b, int tolerance)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i].R - b[i].R) > tolerance ||
                Math.Abs(a[i].G - b[i].G) > tolerance ||
                Math.Abs(a[i].B - b[i].B) > tolerance)
                n++;
        }
        return n;
    }

    private void SavePng(string path, Color[] px)
    {
        using var tex = new Texture2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color);
        tex.SetData(px);
        using var fs = File.Create(path);
        tex.SaveAsPng(fs, Width, Height);
    }
}
