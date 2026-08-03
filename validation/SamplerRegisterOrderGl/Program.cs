// =============================================================================
// SamplerRegisterOrderGl — GitHub issue #189 rung-4 RENDER validation in REAL
// MonoGame DesktopGL: OpenGL sampler slots must follow `register(sN)` /
// declaration order, the way fxc and therefore mgfxc allocate them.
// -----------------------------------------------------------------------------
// WHY THIS GATE HAD TO EXIST BEFORE THE FIX
//
// Every pre-existing GL render gate binds its textures through
// `effect.Parameters[...]`, and under that binding style a first-use-ordered
// sampler table is INTERNALLY CONSISTENT: each record names the uniform its own
// GLSL declares and points at the texture that uniform reads, so the picture is
// right no matter which unit number the pair landed on. That is exactly why
// 30+ green gates could not see this defect.
//
// The Phase 51 A7 gate (validation/SamplerPairsGl) went further and deliberately
// gave its two textures IDENTICAL pixels so the ps_s{k} numbering "must be
// invisible in the picture" — see the comment block on its arm B. This fixture is
// the counterexample that arm was built not to see.
//
// WHAT MAKES THE NUMBERING OBSERVABLE WITHOUT ANY MANUAL SLOT BINDING
//
// SpriteBatcher.FlushVertexArray does `_device.Textures[0] = texture;` right AFTER
// EffectPass.Apply(). So texture unit 0 is not ours to allocate: SpriteBatch owns
// it, and whichever sampler our table put on unit 0 reads the SPRITE rather than
// whatever the effect parameter said. `sampler s : register(s0)` meaning "the
// SpriteBatch texture" is the single most common custom-effect idiom in MonoGame,
// so getting slot 0 wrong is not an edge case.
//
// TWO ARMS, EACH ISOLATING A DIFFERENT VARIABLE
//
// ARM "order" (tests/fixtures/shaders/SamplerRegisterOrder.fx) — the ORDER slots
// are allocated in:
//
//   sampler SpriteSampler : register(s0);   // SpriteBatch owns unit 0
//   sampler MaskSampler   : register(s1);
//   ... sampled in REVERSE declaration order ...
//   return float4(sprite.r, mask.g, 0, 1);
//
// RED sprite + GREEN mask:
//   correct (declaration order)    -> (255, 255, 0)  yellow
//   first-use order (the #189 bug) -> (  0,   0, 0)  black
// Both channels flip together, so neither outcome can be mistaken for a tolerance
// artefact.
//
// ARM "sparse" (tests/fixtures/shaders/SamplerRegisterSparse.fx) — the ABSOLUTE
// register VALUE. Samplers at s2/s3 with NOTHING at s0/s1, sampled strictly IN
// declaration order so ordering cannot be what it measures:
//
//   BLUE sprite + RED MaskA + GREEN MaskB:
//     registers honoured (mgfxc)  -> (255, 255, 0)  yellow
//     compacted to units 0/1      -> (  0, 255, 0)  green
//       (MaskA landed on unit 0 and SpriteBatch overwrote it with the sprite;
//        only the RED channel moves, and the untouched green is the control
//        proving the harness bound anything at all)
//
// Measured 2026-08-02, and load-bearing for the sparse arm: mgfxc honours the
// register annotation ONLY for the legacy `sampler` form. Compiled at ps_3_0 a
// legacy sampler IS the combined sampler, so `: register(sN)` pins its SM3 register
// directly. For the modern spelling it does NOT: given
// `Texture2D T : register(t3); SamplerState S : register(s2);` mgfxc's OpenGL build
// puts the pair on slot 0 regardless, allocating by texture declaration order. So
// the sparse fixture must stay in legacy syntax or it stops testing anything.
//
// EVIDENCE EACH ARM PRODUCES
//   1. ShadowDusk's own build renders the expected colour (the absolute claim).
//   2. The mgfxc golden renders it too (the CONTROL — without it, both builds being
//      wrong in the same direction would pass).
//   3. ShadowDusk's build is pixel-identical to that golden in the SAME scene (the
//      drop-in claim).
//   4. A structural read-back of both .mgfx sampler tables, so a failure says WHICH
//      unit each sampler landed on instead of only "wrong colour".
//
// Both goldens are committed, so unlike SamplerPairsGl arm A there is no
// "no golden -> in-runtime only" mode: a missing golden is a hard failure here.
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

string repoRoot  = ShaderInputs.FindRepoRoot();
string outDir    = Path.Combine(repoRoot, "validation", "output-samplerregisterorder");

Console.WriteLine("=== issue #189 sampler register rung-4 render validation (real MonoGame DesktopGL) ===");
Console.WriteLine($"[regorder] out: {outDir}  tolerance: {tolerance}\n");

// Two arms, each isolating a different variable. A: the ORDER slots are allocated in
// (samples out of declaration order). B: the ABSOLUTE register VALUE (samples strictly
// in declaration order, so order cannot be what it measures, and sits at s2/s3 with
// nothing at s0/s1).
var fixtures = new[] { "SamplerRegisterOrder", "SamplerRegisterSparse" };
var compiled = new Dictionary<string, (byte[] Candidate, byte[] Golden)>(StringComparer.Ordinal);

foreach (string name in fixtures)
{
    string fxPath = Path.Combine(repoRoot, "tests", "fixtures", "shaders", name + ".fx");
    string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL", name + ".mgfx");

    if (!File.Exists(goldenPath))
    {
        Console.Error.WriteLine($"[regorder] FAIL — the mgfxc golden is missing: {goldenPath}\n" +
                                "Regenerate it with tools/compile-fixtures.ps1. This gate compares against the " +
                                "reference compiler and has no in-runtime-only mode.");
        return 2;
    }

    byte[] candidateMgfx;
    try
    {
        string src = await File.ReadAllTextAsync(fxPath);
        var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        });
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"[regorder] {name} compile FAILED: " +
                string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
            return 2;
        }
        candidateMgfx = result.Value.Data;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[regorder] {name} compile threw: {ex.GetType().Name}: {ex.Message}");
        return 2;
    }

    byte[] goldenMgfx = await File.ReadAllBytesAsync(goldenPath);
    compiled[name] = (candidateMgfx, goldenMgfx);
    Console.WriteLine($"[regorder] {name}: candidate {candidateMgfx.Length} B, mgfxc golden {goldenMgfx.Length} B");

    // Structural read-back, before any GL work. Printed for BOTH builds so a failure
    // names the actual slot assignment rather than leaving it to be inferred from a colour.
    foreach ((string label, byte[] bytes) in new[] { ("candidate", candidateMgfx), ("mgfxc   ", goldenMgfx) })
    {
        try
        {
            foreach (string line in MgfxSamplerTable.Describe(bytes))
                Console.WriteLine($"[regorder]   {label} {line}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[regorder]   {label} (structural read-back unavailable: {ex.Message})");
        }
    }
}
Console.WriteLine();

Directory.CreateDirectory(outDir);

using var game = new RegisterOrderGame(
    compiled["SamplerRegisterOrder"].Candidate, compiled["SamplerRegisterOrder"].Golden,
    compiled["SamplerRegisterSparse"].Candidate, compiled["SamplerRegisterSparse"].Golden,
    outDir, tolerance);
game.Run();

if (game.Skipped)
{
    // SHADOWDUSK_REQUIRE_GL=1 turns a "no GL device" skip into a hard failure — the
    // same soft-skip-as-green guard the other GL gates use (Phase 37).
    bool requireGl = string.Equals(
        Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_GL"), "1", StringComparison.Ordinal);
    if (requireGl)
    {
        Console.Error.WriteLine(
            $"\n[regorder] FAIL — SHADOWDUSK_REQUIRE_GL=1 but the GL device could not be created: {game.SkipReason}");
        return 1;
    }
    Console.WriteLine($"\n[regorder] SKIPPED (no GL device): {game.SkipReason}");
    return 0;
}

foreach (string line in game.Report)
    Console.WriteLine(line);

Console.WriteLine($"\n[regorder] {(game.Passed ? "PASS" : "FAIL")} — rung-4 OpenGL sampler register-order validation (issue #189).");
return game.Passed ? 0 : 1;

// -----------------------------------------------------------------------------

/// <summary>
/// Minimal MGFX v10 reader for just the shader sampler tables. Deliberately local to
/// this driver and deliberately tiny: its only job is to turn a failure into a
/// sentence naming which sampler landed on which texture unit.
/// </summary>
static class MgfxSamplerTable
{
    public static List<string> Describe(byte[] d)
    {
        int i = 0;

        byte Byte() => d[i++];
        int I32() { int v = BitConverter.ToInt32(d, i); i += 4; return v; }
        string S7()
        {
            int n = 0, shift = 0;
            while (true)
            {
                byte b = Byte();
                n |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            string s = System.Text.Encoding.UTF8.GetString(d, i, n);
            i += n;
            return s;
        }

        if (d.Length < 6 || d[0] != 'M' || d[1] != 'G' || d[2] != 'F' || d[3] != 'X')
            throw new InvalidDataException("not an MGFX container");
        i = 4;
        Byte();          // version
        Byte();          // profile
        I32();           // effect key

        int cbCount = I32();
        for (int c = 0; c < cbCount; c++)
        {
            S7();
            i += 2 + 2 + 4;
            int np = I32();
            i += 4 * np;
        }

        var samplerLines = new List<string>();
        int shaderCount = I32();
        for (int s = 0; s < shaderCount; s++)
        {
            Byte();                      // isVertexShader
            int blen = I32();
            i += blen;                   // bytecode / GLSL
            int nsamp = Byte();
            for (int k = 0; k < nsamp; k++)
            {
                Byte();                  // type
                int texSlot = Byte();
                int sampSlot = Byte();
                if (Byte() != 0)         // hasState
                {
                    i += 3 + 4;          // address u/v/w + border colour
                    Byte();              // filter
                    i += 4 + 4 + 4;      // maxAnisotropy, maxMipLevel, mipMapLevelOfDetailBias
                }
                string name = S7();
                int param = Byte();
                samplerLines.Add($"sampler record: name={name} texSlot={texSlot} sampSlot={sampSlot} param={param}");
            }
            int ncbi = Byte();
            i += ncbi;
            int natt = Byte();
            for (int a = 0; a < natt; a++) { S7(); Byte(); Byte(); i += 2; }
        }

        // Parameter names, so `param=N` above resolves to something readable. This is a
        // best-effort convenience only: the sampler table above is the evidence, so a
        // parameter shape this mini-reader does not model degrades the message rather
        // than losing it. (The full recursive model lives in validation/decode_mgfx.py.)
        var names = new List<string>();
        try
        {
            int pcount = I32();
            for (int p = 0; p < pcount && p < 64; p++)
            {
                byte pclass = Byte();
                Byte();                      // type
                names.Add(S7());             // name
                S7();                        // semantic
                if (I32() != 0) throw new InvalidDataException("annotations");
                byte rows = Byte(), cols = Byte();
                if (I32() != 0) throw new InvalidDataException("array elements");
                if (I32() != 0) throw new InvalidDataException("struct members");
                // A value leaf (Scalar/Vector/Matrix with no elements/members) carries a raw
                // rows*cols*4 default-value blob with NO length prefix. An Object parameter
                // (class 3 — textures and samplers, which is all this fixture has) carries
                // none at all.
                if (pclass <= 2)
                    i += rows * cols * 4;
            }
        }
        catch (Exception ex)
        {
            names.Add($"(parameter list truncated: {ex.Message})");
        }

        var lines = new List<string>();
        foreach (string line in samplerLines)
        {
            int idx = line.LastIndexOf("param=", StringComparison.Ordinal);
            string resolved = line;
            if (idx >= 0 && int.TryParse(line[(idx + 6)..], out int pi) && pi >= 0 && pi < names.Count)
                resolved = line + $" ({names[pi]})";
            lines.Add(resolved);
        }
        lines.Add("parameters: [" + string.Join(", ", names) + "]");
        return lines;
    }
}

sealed class RegisterOrderGame : Game
{
    private const int Size = 64;

    private static readonly Color Red    = new(255, 0, 0, 255);
    private static readonly Color Green  = new(0, 255, 0, 255);
    private static readonly Color Yellow = new(255, 255, 0, 255);
    private static readonly Color Black  = new(0, 0, 0, 255);

    private static readonly Color Blue = new(0, 0, 255, 255);

    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _candidate, _golden, _sparseCandidate, _sparseGolden;
    private readonly string _outDir;
    private readonly int _tolerance;
    private bool _done;

    public bool Passed { get; private set; }
    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Report { get; } = new();

    public RegisterOrderGame(
        byte[] candidate, byte[] golden,
        byte[] sparseCandidate, byte[] sparseGolden,
        string outDir, int tolerance)
    {
        _candidate = candidate;
        _golden = golden;
        _sparseCandidate = sparseCandidate;
        _sparseGolden = sparseGolden;
        _outDir = outDir;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk sampler register-order validation (headless)";
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

        bool ok = true;
        try { ok &= Validate(); }
        catch (Exception ex)
        {
            Report.Add($"[regorder] order  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            ok = false;
        }
        try { ok &= ValidateSparse(); }
        catch (Exception ex)
        {
            Report.Add($"[regorder] sparse EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            ok = false;
        }
        Passed = ok;
        Exit();
    }

    private bool Validate()
    {
        GraphicsDevice gd = GraphicsDevice;

        Effect candidate, golden;
        try { candidate = new Effect(gd, _candidate); }
        catch (Exception ex)
        {
            Report.Add($"[regorder] candidate new Effect() FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        try { golden = new Effect(gd, _golden); }
        catch (Exception ex)
        {
            Report.Add($"[regorder] GOLDEN new Effect() threw (control failure): {ex.Message}");
            candidate.Dispose();
            return false;
        }

        Report.Add("[regorder] candidate params = [" +
                   string.Join(", ", candidate.Parameters.Select(p => p.Name)) + "]");
        Report.Add("[regorder] mgfxc     params = [" +
                   string.Join(", ", golden.Parameters.Select(p => p.Name)) + "]");

        using Texture2D sprite = Solid(gd, Red);
        using Texture2D mask   = Solid(gd, Green);

        // SpriteSampler is NOT set through a parameter — SpriteBatch supplies unit 0.
        // That is the realistic idiom and it is load-bearing for this test: it is what
        // makes "which unit did the sampler land on" observable in the picture.
        //
        // The mask IS set through a parameter, under BOTH naming spellings, because
        // FxPreParser's synthesized texture name (`MaskSampler_SDTexture`) leaks into
        // ShadowDusk's parameter table where mgfxc emits the plain `MaskSampler`
        // (recorded in ISSUE-145). Setting both means this gate measures the SLOT
        // assignment rather than accidentally measuring that naming divergence.
        void Bind(Effect e)
        {
            foreach (string n in new[] { "MaskSampler", "MaskSampler_SDTexture", "MaskSampler+MaskSampler" })
                e.Parameters[n]?.SetValue(mask);
        }

        Color[] candImg = RenderSprite(gd, candidate, sprite, Bind);
        Color[] goldImg = RenderSprite(gd, golden,    sprite, Bind);
        SavePng(gd, candImg, "regorder_candidate.png");
        SavePng(gd, goldImg, "regorder_golden.png");

        Color candCentre = candImg[Px(Size / 2, Size / 2)];
        Color goldCentre = goldImg[Px(Size / 2, Size / 2)];

        // ---- A. absolute claim -------------------------------------------------
        bool candCorrect = Approx(candCentre, Yellow);
        string diagnosis =
            candCorrect                      ? "correct — SpriteSampler is on unit 0 (declaration order), MaskSampler on unit 1"
            : Approx(candCentre, Black)      ? "WRONG (issue #189): first-use order put MaskSampler on unit 0, where SpriteBatch overwrote it with the sprite, and SpriteSampler on unit 1 which nothing bound"
            : Approx(candCentre, Red)        ? "WRONG: both samplers read unit 0 (the sprite)"
            : Approx(candCentre, Green)      ? "WRONG: both samplers read the mask"
                                             : "WRONG: unrecognised — check the harness bindings";
        Report.Add($"[regorder] order  candidate centre = {Fmt(candCentre)} (want (255,255,0)) -> {diagnosis}");

        // The golden rendering the expected colour is the CONTROL. If mgfxc's own build
        // does not render yellow here, the harness is wrong, not the compiler, and the
        // comparison below would be meaningless.
        bool goldCorrect = Approx(goldCentre, Yellow);
        Report.Add($"[regorder] order  mgfxc     centre = {Fmt(goldCentre)} (want (255,255,0)) -> " +
                   (goldCorrect ? "OK (control)" : "HARNESS FAULT — the reference build does not render the expected colour"));

        // ---- B. drop-in claim --------------------------------------------------
        (int maxDelta, int diffCount) = Compare(candImg, goldImg);
        bool match = diffCount == 0;
        Report.Add($"[regorder] order  vs mgfxc golden: maxd {maxDelta}, {diffCount} px over tolerance " +
                   $"{_tolerance} -> {(match ? "OK" : "WRONG")}");

        candidate.Dispose();
        golden.Dispose();
        return candCorrect && goldCorrect && match;
    }

    /// <summary>
    /// Arm B — the ABSOLUTE register value. `SamplerRegisterSparse.fx` puts its two samplers
    /// at s2/s3 with NOTHING at s0/s1, and samples them strictly in declaration order so
    /// ordering cannot be what this measures. Compacting them to units 0/1 is order-preserving
    /// and still wrong: SpriteBatch overwrites unit 0 with the sprite after Apply().
    /// </summary>
    private bool ValidateSparse()
    {
        GraphicsDevice gd = GraphicsDevice;

        Effect candidate, golden;
        try { candidate = new Effect(gd, _sparseCandidate); }
        catch (Exception ex)
        {
            Report.Add($"[regorder] sparse candidate new Effect() FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        try { golden = new Effect(gd, _sparseGolden); }
        catch (Exception ex)
        {
            Report.Add($"[regorder] sparse GOLDEN new Effect() threw (control failure): {ex.Message}");
            candidate.Dispose();
            return false;
        }

        // A BLUE sprite, so the sampler that wrongly lands on unit 0 reads blue and its RED
        // channel collapses. Green stays lit either way and is the control proving the
        // harness bound anything at all.
        using Texture2D sprite = Solid(gd, Blue);
        using Texture2D maskA  = Solid(gd, Red);
        using Texture2D maskB  = Solid(gd, Green);

        void Bind(Effect e)
        {
            foreach (string n in new[] { "MaskA", "MaskA_SDTexture", "MaskA+MaskA" })
                e.Parameters[n]?.SetValue(maskA);
            foreach (string n in new[] { "MaskB", "MaskB_SDTexture", "MaskB+MaskB" })
                e.Parameters[n]?.SetValue(maskB);
        }

        Color[] candImg = RenderSprite(gd, candidate, sprite, Bind);
        Color[] goldImg = RenderSprite(gd, golden,    sprite, Bind);
        SavePng(gd, candImg, "sparse_candidate.png");
        SavePng(gd, goldImg, "sparse_golden.png");

        Color candCentre = candImg[Px(Size / 2, Size / 2)];
        Color goldCentre = goldImg[Px(Size / 2, Size / 2)];

        bool candCorrect = Approx(candCentre, Yellow);
        string diagnosis =
            candCorrect                 ? "correct — MaskA is on unit 2 and MaskB on unit 3, so SpriteBatch's unit 0 hits neither"
            : Approx(candCentre, Green) ? "WRONG (issue #189 sparse half): the registers were compacted to units 0/1, so MaskA sat on unit 0 and SpriteBatch overwrote it with the sprite"
            : Approx(candCentre, Red)   ? "WRONG: MaskB lost its binding"
                                        : "WRONG: unrecognised — check the harness bindings";
        Report.Add($"[regorder] sparse candidate centre = {Fmt(candCentre)} (want (255,255,0)) -> {diagnosis}");

        bool goldCorrect = Approx(goldCentre, Yellow);
        Report.Add($"[regorder] sparse mgfxc     centre = {Fmt(goldCentre)} (want (255,255,0)) -> " +
                   (goldCorrect ? "OK (control)" : "HARNESS FAULT — the reference build does not render the expected colour"));

        (int maxDelta, int diffCount) = Compare(candImg, goldImg);
        bool match = diffCount == 0;
        Report.Add($"[regorder] sparse vs mgfxc golden: maxd {maxDelta}, {diffCount} px over tolerance " +
                   $"{_tolerance} -> {(match ? "OK" : "WRONG")}");

        candidate.Dispose();
        golden.Dispose();
        return candCorrect && goldCorrect && match;
    }

    private static Texture2D Solid(GraphicsDevice gd, Color c)
    {
        var t = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        t.SetData(new[] { c });
        return t;
    }

    private Color[] RenderSprite(GraphicsDevice gd, Effect effect, Texture2D sprite, Action<Effect> bind)
    {
        bind(effect);
        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Black);
        using var sb = new SpriteBatch(gd);
        sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, null, null, effect);
        sb.Draw(sprite, new Rectangle(0, 0, Size, Size), Color.White);
        sb.End();
        gd.SetRenderTarget(null);
        var px = new Color[Size * Size];
        rt.GetData(px);
        return px;
    }

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
        Math.Abs(a.R - b.R) <= 4 && Math.Abs(a.G - b.G) <= 4 && Math.Abs(a.B - b.B) <= 4;

    private static string Fmt(Color c) => $"({c.R},{c.G},{c.B})";
}
