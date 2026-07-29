// =============================================================================
// SamplerPairsGl — Phase 51 A7 rung-4 RENDER validation in REAL MonoGame DesktopGL
// for per-(texture, sampler)-PAIR OpenGL sampler records.
// -----------------------------------------------------------------------------
// The GL sampler table is built from the combined samplers SPIRV-Cross folds each
// (texture, sampler) pair into, in ITS declaration order (SpirvCombinedSamplerPairs).
// A compile-time test can check the record structure, but only a real render proves
// the records actually BIND: MonoGame's GL runtime does
// glUniform1i(glGetUniformLocation(program, record.Name), record.TextureSlot) and then
// binds Parameters[record.Parameter]'s texture to that unit. If the name misses, or the
// parameter is the wrong texture, the compile is still clean and the picture is wrong.
//
// Pipeline under test (ZERO mocking of compiler or runtime):
//   ShadowDusk EffectCompiler (OpenGL) -> .mgfx bytes -> new Effect(gd, bytes)
//   -> bind REAL Texture2Ds -> render through SpriteBatch -> read pixels back -> assert.
//
// ======================= WHY THE FIXTURES LOOK LIKE THAT =====================
// Both fixtures render an ASYMMETRIC function of their two samplers, so a swap or a
// missed binding changes the PICTURE. The obvious `diffuse * light` would render
// identically under a swap and prove nothing.
//
// SpriteBatch forces the sprite texture onto unit 0 AFTER EffectPass.Apply()
// (SpriteBatcher.FlushVertexArray does `_device.Textures[0] = texture;` right after
// pass.Apply(), with that exact comment upstream). That is not a problem to work
// around, it IS the realistic shape: unit 0 comes from sb.Draw(...) and unit 1 comes
// from Parameters["Lightmap"].SetValue(...). It also means unit 1 is the one carrying
// the claim, which is precisely the record that did not exist before this change.
//
// ============================ WHAT IS PROVEN HERE ============================
//   A. SHARED SAMPLER (SharedSamplerPair.fx) — two textures, one SamplerState.
//      Sprite/unit 0 = RED, Lightmap = GREEN, output = (diffuse.r, light.g, 0, 1):
//        yellow (255,255,0) = correct
//        red    (255,0,0)   = ps_s1 got no texture unit and read unit 0 (the old bug)
//        black  (0,0,0)     = the two pairs are swapped
//      Three outcomes that cannot be confused, and the two failure modes are the two
//      real ones. Also cross-checked against the mgfxc golden when one is committed.
//
//   B. MIRROR (SamplerPairMirror.fx) — one texture, two SamplerStates with different
//      filters, the second-declared one sampled FIRST. Output = (point.r, linear.r):
//      sampling between two texels of a 2x1 black/white texture, Point snaps and
//      Linear blends, so R != G proves each record carried ITS OWN baked state in
//      declaration order rather than both getting one sampler's state.
//
// ===================== HONEST LIMITATIONS (NOT hidden) ======================
//   * Arm A's mgfxc pixel-diff runs only when tests/fixtures/golden/OpenGL/
//     SharedSamplerPair.mgfx exists; without it the arm still proves in-runtime
//     binding correctness, which is the claim that matters, but not "same picture as
//     mgfxc". The arm reports which of the two it ran.
//   * Arm B has no mgfxc golden by design: it is a statement about ShadowDusk's own
//     per-pair state baking, and the two filters are compared against each other
//     in one render, so no reference build is needed.
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
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL");
string outDir    = Path.Combine(repoRoot, "validation", "output-samplerpairs");

Console.WriteLine("=== Phase 51 A7 sampler-pair rung-4 render validation (real MonoGame DesktopGL) ===");
Console.WriteLine($"[pairs] out: {outDir}  tolerance: {tolerance}\n");

async System.Threading.Tasks.Task<byte[]> CompileAsync(string name)
{
    string fxPath = Path.Combine(shaderDir, name + ".fx");
    string src = await File.ReadAllTextAsync(fxPath);
    var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
    {
        Target          = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName  = fxPath,
    });
    if (result.IsFailure)
        throw new Exception($"compile {name} failed: " +
            string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
    return result.Value.Data;
}

byte[] sharedMgfx, mirrorMgfx;
try
{
    sharedMgfx = await CompileAsync("SharedSamplerPair");
    mirrorMgfx = await CompileAsync("SamplerPairMirror");
}
catch (Exception ex)
{
    Console.Error.WriteLine("[pairs] " + ex.Message);
    return 2;
}
Console.WriteLine($"[pairs] compiled OK: shared {sharedMgfx.Length} B, mirror {mirrorMgfx.Length} B");

// The mgfxc golden for arm A, when one has been generated (tools/compile-fixtures.ps1).
string sharedGoldenPath = Path.Combine(goldenDir, "SharedSamplerPair.mgfx");
byte[]? sharedGolden = File.Exists(sharedGoldenPath)
    ? await File.ReadAllBytesAsync(sharedGoldenPath)
    : null;
Console.WriteLine(sharedGolden is null
    ? $"[pairs] no mgfxc golden at {sharedGoldenPath} — arm A runs in-runtime-only (see header)"
    : $"[pairs] mgfxc golden: {sharedGolden.Length} B — arm A also pixel-diffs vs mgfxc");

Directory.CreateDirectory(outDir);

using var game = new SamplerPairsGame(sharedMgfx, mirrorMgfx, sharedGolden, outDir, tolerance);
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
            $"\n[pairs] FAIL — SHADOWDUSK_REQUIRE_GL=1 but the GL device could not be created: {game.SkipReason}");
        return 1;
    }
    Console.WriteLine($"\n[pairs] SKIPPED (no GL device): {game.SkipReason}");
    return 0;
}

Console.WriteLine();
foreach (string line in game.Report)
    Console.WriteLine(line);

Console.WriteLine($"\n[pairs] {(game.Passed ? "PASS" : "FAIL")} — rung-4 per-pair GL sampler-record validation.");
return game.Passed ? 0 : 1;

// -----------------------------------------------------------------------------

sealed class SamplerPairsGame : Game
{
    private const int Size = 64;

    private static readonly Color Red   = new(255, 0, 0, 255);
    private static readonly Color Green = new(0, 255, 0, 255);

    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _sharedMgfx, _mirrorMgfx;
    private readonly byte[]? _sharedGolden;
    private readonly string _outDir;
    private readonly int _tolerance;
    private bool _done;

    public bool Passed { get; private set; }
    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Report { get; } = new();

    public SamplerPairsGame(
        byte[] sharedMgfx, byte[] mirrorMgfx, byte[]? sharedGolden, string outDir, int tolerance)
    {
        _sharedMgfx = sharedMgfx;
        _mirrorMgfx = mirrorMgfx;
        _sharedGolden = sharedGolden;
        _outDir = outDir;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk sampler-pair validation (headless)";
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
        try { ok &= ValidateSharedSampler(); }
        catch (Exception ex) { Report.Add($"[A shared] EXCEPTION: {ex.GetType().Name}: {ex.Message}"); ok = false; }
        try { ok &= ValidateMirror(); }
        catch (Exception ex) { Report.Add($"[B mirror] EXCEPTION: {ex.GetType().Name}: {ex.Message}"); ok = false; }

        Passed = ok;
        Exit();
    }

    // ---- A. Two textures through ONE shared SamplerState ----------------------
    private bool ValidateSharedSampler()
    {
        GraphicsDevice gd = GraphicsDevice;

        Effect candidate;
        try { candidate = new Effect(gd, _sharedMgfx); }
        catch (Exception ex)
        {
            Report.Add($"[A shared] new Effect() FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        Report.Add("[A shared] new Effect(gd, mgfx) loaded OK in real DesktopGL; params = [" +
                   string.Join(", ", candidate.Parameters.Select(p => p.Name)) + "]");

        using Texture2D spriteRed = Solid(gd, Red);
        using Texture2D lightGreen = Solid(gd, Green);

        Color[] img = RenderSprite(gd, candidate, spriteRed, e =>
        {
            // Unit 0 is the sprite (SpriteBatch forces it); unit 1 must come from here.
            e.Parameters["DiffuseMap"]?.SetValue(spriteRed);
            e.Parameters["Lightmap"]?.SetValue(lightGreen);
        });
        SavePng(gd, img, "shared_candidate.png");

        Color centre = img[Px(Size / 2, Size / 2)];
        bool correct = Approx(centre, new Color(255, 255, 0, 255));

        // Name the failure mode instead of just saying "wrong colour".
        string diagnosis =
            correct                                     ? "correct — each pair bound its own texture"
            : Approx(centre, Red)                       ? "WRONG: ps_s1 got no texture unit and read unit 0 (the pre-A7 bug)"
            : Approx(centre, new Color(0, 0, 0, 255))   ? "WRONG: the two pairs are swapped"
            : Approx(centre, Green)                     ? "WRONG: both samplers read the Lightmap"
                                                        : "WRONG: unrecognised — check the harness bindings";
        Report.Add($"[A shared] centre = {Fmt(centre)} (want (255,255,0)) -> {diagnosis}");

        bool all = correct;

        // Same-scene pixel diff vs the real mgfxc build, when a golden exists.
        if (_sharedGolden is not null)
        {
            Effect golden;
            try { golden = new Effect(gd, _sharedGolden); }
            catch (Exception ex)
            {
                Report.Add($"[A shared] GOLDEN new Effect() threw (control failure): {ex.Message}");
                candidate.Dispose();
                return false;
            }

            Color[] goldImg = RenderSprite(gd, golden, spriteRed, e =>
            {
                // mgfxc's MojoShader naming for a combined sampler is <sampler>+<texture>;
                // ShadowDusk keeps the plain texture name (project_decisions.md). Set BOTH
                // spellings so each arm gets the same textures under whichever name it uses.
                foreach (string n in new[] { "DiffuseMap", "TextureSampler+DiffuseMap" })
                    e.Parameters[n]?.SetValue(spriteRed);
                foreach (string n in new[] { "Lightmap", "TextureSampler+Lightmap" })
                    e.Parameters[n]?.SetValue(lightGreen);
            });
            SavePng(gd, goldImg, "shared_golden.png");

            (int maxDelta, int diffCount) = Compare(img, goldImg);
            bool match = diffCount == 0;
            Report.Add($"[A shared] vs mgfxc golden: maxd {maxDelta}, {diffCount} px over tolerance " +
                       $"{_tolerance} -> {OkWrong(match)}");
            all &= match;
            golden.Dispose();
        }
        else
        {
            Report.Add("[A shared] vs mgfxc golden: SKIPPED (no golden committed) — in-runtime arm only");
        }

        candidate.Dispose();
        return all;
    }

    // ---- B. One texture through TWO SamplerStates -----------------------------
    private bool ValidateMirror()
    {
        GraphicsDevice gd = GraphicsDevice;

        Effect effect;
        try { effect = new Effect(gd, _mirrorMgfx); }
        catch (Exception ex)
        {
            Report.Add($"[B mirror] new Effect() FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        Report.Add("[B mirror] new Effect(gd, mgfx) loaded OK in real DesktopGL; params = [" +
                   string.Join(", ", effect.Parameters.Select(p => p.Name)) + "]");

        // Two 2x1 black/white textures with IDENTICAL content, so the only thing that can
        // make the two channels differ is the baked filter each record carries. They must be
        // distinct texture OBJECTS: MonoGame 3.8.2's GL backend has no sampler objects and
        // applies filtering with glTexParameteri on the bound texture, so one texture bound
        // to two units gets whichever filter was applied last (see the fixture header).
        using Texture2D pointTex = Ramp(gd);
        using Texture2D linearTex = Ramp(gd);

        // PointTexture is the ps_s0 pair, and SpriteBatch re-binds unit 0 to the sprite after
        // EffectPass.Apply() — so pass it AS the sprite rather than fighting that.
        Color[] img = RenderSprite(gd, effect, pointTex, e =>
        {
            e.Parameters["PointTexture"]?.SetValue(pointTex);
            e.Parameters["LinearTexture"]?.SetValue(linearTex);
        });
        SavePng(gd, img, "mirror_candidate.png");

        // Read at the horizontal centre, where the sample coordinate sits between texels.
        // R = PointSampler (ps_s0, unit 0), G = LinearSampler (ps_s1, unit 1).
        Color centre = img[Px(Size / 2, Size / 2)];
        bool snapped = centre.R <= 8 || centre.R >= 247;
        bool blended = centre.G is > 64 and < 192;
        bool differ = Math.Abs(centre.R - centre.G) > 32;

        Report.Add($"[B mirror] centre = {Fmt(centre)}: point-filtered R snapped -> {OkWrong(snapped)}; " +
                   $"linear-filtered G blended -> {OkWrong(blended)}; the two states differ -> {OkWrong(differ)}");
        Report.Add("[B mirror] R != G is the claim: the two textures hold IDENTICAL pixels and the " +
                   "pairs are sampled in reverse declaration order, so only a per-pair baked state " +
                   "applied in the right order can make the channels disagree.");

        effect.Dispose();
        return snapped && blended && differ;
    }

    // ---- shared ---------------------------------------------------------------

    private static Texture2D Solid(GraphicsDevice gd, Color c)
    {
        var t = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        t.SetData(new[] { c });
        return t;
    }

    /// <summary>A 2x1 black-then-white texture: Point snaps to a texel, Linear blends.</summary>
    private static Texture2D Ramp(GraphicsDevice gd)
    {
        var t = new Texture2D(gd, 2, 1, false, SurfaceFormat.Color);
        t.SetData(new[] { new Color(0, 0, 0, 255), new Color(255, 255, 255, 255) });
        return t;
    }

    private Color[] RenderSprite(GraphicsDevice gd, Effect effect, Texture2D sprite, Action<Effect> bind)
    {
        bind(effect);
        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Black);
        using var sb = new SpriteBatch(gd);
        // LinearClamp (not PointClamp): arm B needs the device default NOT to be Point, so a
        // dropped baked state shows up as both channels agreeing rather than accidentally
        // reproducing the expected Point result.
        sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
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
    private static string OkWrong(bool ok) => ok ? "OK" : "WRONG";
}
