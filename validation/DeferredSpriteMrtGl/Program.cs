// =============================================================================
// DeferredSpriteMrtGl — Phase 51 A2 rung-4 TRUE MULTIPLE-RENDER-TARGET render
// validation in REAL MonoGame DesktopGL, for the Phase 41 GAP-2 shader.
// -----------------------------------------------------------------------------
// GAP-2 (Nez's DeferredSprite.fx failed on OpenGL with "Semantic COLOR is
// invalid") was closed at COMPILE + golden-structural-match on 2026-06-27 by the
// GL-only GlStructOutputColorRewriter plus the true-MRT gl_FragData[0] slot-0
// fix. The render rung was left open with an explicit reason, verbatim from
// Phase 41: "a true MRT render proof (bind 2 render targets, draw, read back
// BOTH attachments, compare to mgfxc) needs a NEW render driver — the current GL
// render gates are single-target only." This is that driver.
//
// Every other GL render gate in this repo binds ONE render target, so nothing we
// run could distinguish "the shader writes both COLOR0 and COLOR1 to the right
// attachments" from "the shader writes COLOR0 and the second output silently
// went nowhere". Structural matching cannot see it either: the second output
// lives in the emitted GLSL (gl_FragData[1]), not in the .mgfx record tables.
//
// Pipeline under test (ZERO mocking of compiler or runtime):
//   ShadowDusk EffectCompiler (OpenGL) -> .mgfx bytes -> new Effect(gd, bytes)
//   -> SetRenderTargets(rt0, rt1) -> SpriteBatch draw -> GetData BOTH attachments.
//
// ======================= WHY THE SCENE LOOKS LIKE THAT =======================
// The sprite is a 64x64 texture drawn into a 64x64 rect, i.e. 1:1 texel-to-pixel,
// so texture filtering cannot blur the assertions and neither build's baked
// sampler state can shift a boundary. Its LEFT half is opaque red and its RIGHT
// half is the same red at alpha 64/255, so with _alphaCutoff = 0.5 the shader's
// clip() keeps the left half and discards the right — in ONE draw, with no
// filtering band to reason about.
//
// The normal map is a DIFFERENT colour (blue) with a DIFFERENT alpha, so the two
// attachments can never be confused for one another, and the alpha is put through
// the shader's own arithmetic (normal.a *= _alphaAsSelfIllumination *
// _selfIlluminationPower = 0.25) so attachment 1 carries a value no clear colour
// and no copy of attachment 0 can produce: 200/255 * 0.25 * 255 = exactly 50.
//
// Both targets are cleared to TRANSPARENT black (0,0,0,0) — distinct from every
// value the shader writes — so "this attachment was never written" is a visible,
// nameable outcome rather than something that blends into a correct result.
//
// Expected, per attachment (Arm A asserts these exactly):
//              left half (kept)        right half (clipped)
//   RT0        (255,  0,  0,255)       (0,0,0,0)
//   RT1        (  0,  0,255, 50)       (0,0,0,0)
//
// Named failure modes this discriminates, none of which a single-target gate or
// a structural match can see:
//   * RT1 all-clear            -> the COLOR1 output was dropped
//   * RT1 == RT0               -> both outputs went to attachment 0 (or the same
//                                 value was broadcast to both draw buffers)
//   * RT0/RT1 swapped          -> the output slots are reversed
//   * right half of RT1 written-> clip() discarded only attachment 0
//   * right half of RT0 written-> clip() did not fire at all
//
// ============================ WHAT IS PROVEN HERE ============================
//   A. ABSOLUTE — the candidate's two attachments carry exactly the values the
//      HLSL says they should, and each wrong pixel is diagnosed by name.
//   B. vs mgfxc — the same scene rendered by the real `mgfxc` OpenGL golden
//      (tests/fixtures/golden/OpenGL/DeferredSprite.mgfx), pixel-diffed on BOTH
//      attachments. This is the rung-4 claim: same picture as the reference
//      compiler, in the real engine, on every attachment.
//
// ===================== HONEST LIMITATIONS (NOT hidden) ======================
//   * Two attachments, not eight. The shader is the real-world Nez one and has
//      two outputs; this proves the MRT path works, not that N>2 does.
//   * Arm B runs only when the mgfxc golden is present. It is committed, so in
//      practice it always runs — but the arm reports which of the two it did, so
//      a deleted golden can never pass as a completed diff.
// =============================================================================

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

int tolerance = 4;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot   = ShaderInputs.FindRepoRoot();
string shaderPath = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "DeferredSprite.fx");
string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL", "DeferredSprite.mgfx");
string outDir     = Path.Combine(repoRoot, "validation", "output-mrt");

Console.WriteLine("=== Phase 51 A2 DeferredSprite true-MRT rung-4 render validation (real MonoGame DesktopGL) ===");
Console.WriteLine($"[mrt] out: {outDir}  tolerance: {tolerance}\n");

byte[] candidateMgfx;
try
{
    string src = await File.ReadAllTextAsync(shaderPath);
    var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
    {
        Target          = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName  = shaderPath,
    });
    if (result.IsFailure)
        throw new Exception("compile DeferredSprite (OpenGL) failed: " +
            string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
    candidateMgfx = result.Value.Data;
}
catch (Exception ex)
{
    Console.Error.WriteLine("[mrt] " + ex.Message);
    return 2;
}
Console.WriteLine($"[mrt] compiled OK: candidate {candidateMgfx.Length} B");

byte[]? goldenMgfx = null;
if (File.Exists(goldenPath))
{
    goldenMgfx = await File.ReadAllBytesAsync(goldenPath);
    Console.WriteLine($"[mrt] mgfxc golden: {goldenMgfx.Length} B — arm B pixel-diffs BOTH attachments vs mgfxc");
}
else
{
    Console.WriteLine($"[mrt] no mgfxc golden at {goldenPath} — arm B will be reported SKIPPED");
}

Directory.CreateDirectory(outDir);

using var game = new MrtGame(candidateMgfx, goldenMgfx, outDir, tolerance);
game.Run();

if (game.Skipped)
{
    // SHADOWDUSK_REQUIRE_GL=1 turns a "no GL device" skip into a hard failure — the same
    // soft-skip-as-green guard the other GL gates use (Phase 37). CI sets it, so a
    // regression that silently loses the GL context turns the lane RED instead of
    // masking itself green; a normal headless dev run without the flag skips cleanly.
    bool requireGl = string.Equals(
        Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_GL"), "1", StringComparison.Ordinal);
    if (requireGl)
    {
        Console.Error.WriteLine(
            $"\n[mrt] FAIL — SHADOWDUSK_REQUIRE_GL=1 but the GL device could not be created: {game.SkipReason}");
        return 1;
    }
    Console.WriteLine($"\n[mrt] SKIPPED (no GL device): {game.SkipReason}");
    return 0;
}

Console.WriteLine();
foreach (string line in game.Report)
    Console.WriteLine(line);

Console.WriteLine($"\n[mrt] {(game.Passed ? "PASS" : "FAIL")} — rung-4 true 2-attachment MRT GL validation.");
return game.Passed ? 0 : 1;

// -----------------------------------------------------------------------------

sealed class MrtGame : Game
{
    private const int Size = 64;

    // The scene's four expected values (see the header for why each was chosen).
    private static readonly Color Cleared  = new(0, 0, 0, 0);
    private static readonly Color Diffuse  = new(255, 0, 0, 255);
    private static readonly Color NormalIn = new(0, 0, 255, 200);
    private static readonly Color NormalOut = new(0, 0, 255, 50);   // 200/255 * 0.25 * 255 == 50 exactly

    private const float AlphaCutoff            = 0.5f;
    private const float AlphaAsSelfIllumination = 0.5f;
    private const float SelfIlluminationPower   = 0.5f;

    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _candidateMgfx;
    private readonly byte[]? _goldenMgfx;
    private readonly string _outDir;
    private readonly int _tolerance;
    private bool _done;

    public bool Passed { get; private set; }
    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Report { get; } = new();

    public MrtGame(byte[] candidateMgfx, byte[]? goldenMgfx, string outDir, int tolerance)
    {
        _candidateMgfx = candidateMgfx;
        _goldenMgfx = goldenMgfx;
        _outDir = outDir;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile           = GraphicsProfile.HiDef,   // MRT needs HiDef
        };
        Window.Title = "ShadowDusk DeferredSprite MRT validation (headless)";
    }

    protected override void Initialize()
    {
        try { base.Initialize(); }
        catch (Exception ex)
        {
            Skipped = true;
            SkipReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done || Skipped) { Exit(); return; }
        _done = true;

        try { Passed = Validate(); }
        catch (Exception ex)
        {
            Report.Add($"[mrt] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Passed = false;
        }
        Exit();
    }

    private bool Validate()
    {
        GraphicsDevice gd = GraphicsDevice;

        int maxTargets = gd.GraphicsProfile == GraphicsProfile.HiDef ? 4 : 1;
        Report.Add($"[mrt] device: profile {gd.GraphicsProfile}, adapter '{gd.Adapter.Description}' " +
                   $"(MRT requires HiDef; up to {maxTargets} targets)");

        Effect candidate;
        try { candidate = new Effect(gd, _candidateMgfx); }
        catch (Exception ex)
        {
            Report.Add($"[mrt] candidate new Effect() FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        Report.Add("[mrt] candidate new Effect(gd, mgfx) loaded OK in real DesktopGL; params = [" +
                   string.Join(", ", candidate.Parameters.Select(p => p.Name)) + "]");

        using Texture2D sprite = SplitAlphaSprite(gd);
        using Texture2D normalMap = Solid(gd, NormalIn);

        (Color[] c0, Color[] c1) = RenderMrt(gd, candidate, sprite, normalMap);
        SavePng(gd, c0, "candidate_rt0_color.png");
        SavePng(gd, c1, "candidate_rt1_normal.png");

        bool ok = AssertScene("A candidate", c0, c1);

        // ---- B. Same scene through the real mgfxc build, both attachments -----
        if (_goldenMgfx is not null)
        {
            Effect golden;
            try { golden = new Effect(gd, _goldenMgfx); }
            catch (Exception ex)
            {
                Report.Add($"[mrt] GOLDEN new Effect() threw (control failure): {ex.GetType().Name}: {ex.Message}");
                candidate.Dispose();
                return false;
            }
            Report.Add("[mrt] golden params = [" +
                       string.Join(", ", golden.Parameters.Select(p => p.Name)) + "]");

            (Color[] g0, Color[] g1) = RenderMrt(gd, golden, sprite, normalMap);
            SavePng(gd, g0, "golden_rt0_color.png");
            SavePng(gd, g1, "golden_rt1_normal.png");

            // The golden is the reference, but it is not exempt from being wrong in the
            // runtime (Phase 51 A3 found a real mgfxc/MojoShader GL miscompile). Report
            // the golden's own absolute correctness separately so a diff failure can be
            // attributed to the right side instead of silently blaming the candidate.
            bool goldenAbsolute = AssertScene("B golden ", g0, g1);
            if (!goldenAbsolute)
                Report.Add("[mrt] NOTE: the mgfxc golden itself does not render the scene the HLSL " +
                           "describes — read the rows above before attributing the diff below.");

            (int m0, int d0) = Compare(c0, g0);
            (int m1, int d1) = Compare(c1, g1);
            bool match = d0 == 0 && d1 == 0;
            Report.Add($"[mrt] vs mgfxc golden: attachment 0 maxd {m0} ({d0} px over tolerance {_tolerance}), " +
                       $"attachment 1 maxd {m1} ({d1} px over tolerance {_tolerance}) -> {OkWrong(match)}");
            ok &= match && goldenAbsolute;
            golden.Dispose();
        }
        else
        {
            Report.Add("[mrt] vs mgfxc golden: SKIPPED (no golden committed) — absolute arm only");
        }

        candidate.Dispose();
        return ok;
    }

    /// <summary>
    /// Assert both attachments against what the HLSL says, and name the failure mode
    /// rather than only reporting "wrong colour".
    /// </summary>
    private bool AssertScene(string tag, Color[] rt0, Color[] rt1)
    {
        // Sample well inside each half; the 1:1 texel mapping means there is no blend
        // band, but staying off the seam keeps the assertion honest about that claim.
        Color kept0 = rt0[Px(16, 32)], kept1 = rt1[Px(16, 32)];
        Color cut0  = rt0[Px(48, 32)], cut1  = rt1[Px(48, 32)];

        bool ok0Kept = Approx(kept0, Diffuse);
        bool ok1Kept = Approx(kept1, NormalOut);
        bool ok0Cut  = Approx(cut0, Cleared);
        bool ok1Cut  = Approx(cut1, Cleared);

        string diagnosis =
            ok0Kept && ok1Kept && ok0Cut && ok1Cut ? "correct — both attachments written, clip() discarded both"
            : Approx(kept1, Cleared)               ? "WRONG: attachment 1 never written (the COLOR1 output was dropped)"
            : Approx(kept1, kept0)                 ? "WRONG: attachment 1 holds attachment 0's value (outputs not split)"
            : Approx(kept0, NormalOut) && Approx(kept1, Diffuse) ? "WRONG: the two output slots are swapped"
            : !ok1Cut                              ? "WRONG: clip() discarded attachment 0 only — attachment 1 kept the clipped half"
            : !ok0Cut                              ? "WRONG: clip() did not fire"
                                                   : "WRONG: unrecognised — check the harness bindings";

        Report.Add($"[{tag}] kept half: rt0 {Fmt(kept0)} (want {Fmt(Diffuse)}) -> {OkWrong(ok0Kept)}; " +
                   $"rt1 {Fmt(kept1)} (want {Fmt(NormalOut)}) -> {OkWrong(ok1Kept)}");
        Report.Add($"[{tag}] clipped half: rt0 {Fmt(cut0)} (want {Fmt(Cleared)}) -> {OkWrong(ok0Cut)}; " +
                   $"rt1 {Fmt(cut1)} (want {Fmt(Cleared)}) -> {OkWrong(ok1Cut)}");
        Report.Add($"[{tag}] -> {diagnosis}");

        // Non-vacuity: an all-clear pair would satisfy nothing above, but say it out loud
        // so "the draw never happened" can never read as a quiet pass.
        int written0 = rt0.Count(c => c.A != 0 || c.R != 0 || c.G != 0 || c.B != 0);
        int written1 = rt1.Count(c => c.A != 0 || c.R != 0 || c.G != 0 || c.B != 0);
        Report.Add($"[{tag}] non-vacuity: {written0}/{rt0.Length} px written on attachment 0, " +
                   $"{written1}/{rt1.Length} on attachment 1 (both must be ~half the surface)");

        bool halfish0 = written0 > rt0.Length / 4 && written0 < rt0.Length * 3 / 4;
        bool halfish1 = written1 > rt1.Length / 4 && written1 < rt1.Length * 3 / 4;

        return ok0Kept && ok1Kept && ok0Cut && ok1Cut && halfish0 && halfish1;
    }

    // ---- rendering ------------------------------------------------------------

    /// <summary>
    /// Bind TWO render targets, draw the sprite through the effect, and read BOTH
    /// attachments back. This is the whole point of the driver: every other GL gate
    /// in the repo binds one.
    /// </summary>
    private (Color[] Rt0, Color[] Rt1) RenderMrt(
        GraphicsDevice gd, Effect effect, Texture2D sprite, Texture2D normalMap)
    {
        // mgfxc's MojoShader naming for a combined sampler is <sampler>+<texture>;
        // ShadowDusk keeps the plain texture name (project_decisions.md). Set every
        // spelling either build can expose, so both arms get identical inputs.
        foreach (string n in new[] { "_normalMap", "_normalMapSampler+_normalMap" })
            effect.Parameters[n]?.SetValue(normalMap);
        effect.Parameters["_alphaCutoff"]?.SetValue(AlphaCutoff);
        effect.Parameters["_alphaAsSelfIllumination"]?.SetValue(AlphaAsSelfIllumination);
        effect.Parameters["_selfIlluminationPower"]?.SetValue(SelfIlluminationPower);

        using var rt0 = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        using var rt1 = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);

        gd.SetRenderTargets(new RenderTargetBinding(rt0), new RenderTargetBinding(rt1));
        gd.Clear(Cleared);

        using var sb = new SpriteBatch(gd);
        // PointClamp so nothing about this scene depends on a filter: the sprite is drawn
        // 1:1 anyway, but a point filter makes that independent of whichever baked sampler
        // state each build emits for the bare `sampler s0;`.
        sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, null, null, effect);
        sb.Draw(sprite, new Rectangle(0, 0, Size, Size), Color.White);
        sb.End();

        gd.SetRenderTargets(null);

        var a = new Color[Size * Size];
        var b = new Color[Size * Size];
        rt0.GetData(a);
        rt1.GetData(b);
        return (a, b);
    }

    // ---- fixtures -------------------------------------------------------------

    private static Texture2D Solid(GraphicsDevice gd, Color c)
    {
        var t = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        t.SetData(new[] { c });
        return t;
    }

    /// <summary>
    /// A Size x Size sprite: left half opaque, right half at alpha 64/255. Drawn into a
    /// Size x Size rect it is 1:1 texel-to-pixel, so the alpha step lands exactly on the
    /// half-way column and the shader's clip(alpha &lt; _alphaCutoff) has a hard edge with
    /// no filtered band on either side of it.
    /// </summary>
    private static Texture2D SplitAlphaSprite(GraphicsDevice gd)
    {
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[y * Size + x] = x < Size / 2 ? Diffuse : new Color(Diffuse.R, Diffuse.G, Diffuse.B, (byte)64);
        var t = new Texture2D(gd, Size, Size, false, SurfaceFormat.Color);
        t.SetData(px);
        return t;
    }

    // ---- shared ---------------------------------------------------------------

    private (int MaxDelta, int DiffCount) Compare(Color[] a, Color[] b)
    {
        int maxDelta = 0, diffCount = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)),
                             Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A)));
            if (d > maxDelta) maxDelta = d;
            if (d > _tolerance) diffCount++;
        }
        return (maxDelta, diffCount);
    }

    private void SavePng(GraphicsDevice gd, Color[] img, string name)
    {
        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        rt.SetData(img);
        using var fs = File.Create(Path.Combine(_outDir, name));
        rt.SaveAsPng(fs, Size, Size);
    }

    private static int Px(int x, int y) => y * Size + x;

    private static bool Approx(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 4 && Math.Abs(a.G - b.G) <= 4 &&
        Math.Abs(a.B - b.B) <= 4 && Math.Abs(a.A - b.A) <= 4;

    private static string Fmt(Color c) => $"({c.R},{c.G},{c.B},{c.A})";
    private static string OkWrong(bool ok) => ok ? "OK" : "WRONG";
}
