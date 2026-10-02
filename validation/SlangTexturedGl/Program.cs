// =============================================================================
// SlangTexturedGl - issue #252 rung-4 RENDER gate for the REAL-slangc route
// (ShadowDusk.Slang.SlangCompiler) on OpenGL, in REAL MonoGame DesktopGL, with the
// texture supplied the way a game supplies it: by SpriteBatch, on texture unit 0.
// -----------------------------------------------------------------------------
// WHY THIS GATE EXISTS
//
// validation/SlangFullCorpus's GL pixel gate renders only the uniform-free procedural
// shaders, and its DX gate binds every texture through effect.Parameters[...]. Neither can
// see WHICH texture unit a sampler landed on. slangc numbers every resource itself
// ('SamplerState SpriteSampler : register(s0)'), and ShadowDusk's GL sampler allocator
// reads a modern SamplerState register as an author reservation (mgfxc's own measured rule,
// issue #189), so a single-texture Slang shader came out on ps_s1. SpriteBatcher binds the
// draw texture to unit 0 after EffectPass.Apply(), so the shader sampled an empty unit.
//
// WHAT IT CHECKS, per textured PS-only shader of the real-slangc corpus
// (tests/fixtures/shaders/slang/*.slang with one [shader("fragment")] entry and a
// Texture2D; the set is discovered, and a new one without an expectation below FAILS):
//   1. structural: the compiled .mgfx sampler table puts the texture on unit 0;
//   2. render: SpriteBatch draws a non-uniform texture through the effect, the texture is
//      NEVER set through effect.Parameters, and the picture must match the shader's own
//      math evaluated on the CPU for the same texels (max channel delta <= tolerance).
// Invert.slang additionally renders against the committed mgfxc golden of the equivalent
// .fx (tests/fixtures/golden/OpenGL/Invert.mgfx) in the SAME scene: the golden must match
// the CPU expectation too (the CONTROL that proves the harness), and ShadowDusk's build
// must match the golden (the drop-in claim).
//
// Exit 0 iff every row passes. SHADOWDUSK_REQUIRE_GL=1 turns a no-GL-device skip into a
// failure (same guard the other GL gates use).
// =============================================================================

using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests;
using ShadowDusk.Slang;

int tolerance = 2;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string repoRoot = FindRepoRoot();
string corpusDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "slang");
string goldenInvert = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL", "Invert.mgfx");
string outDir = Path.Combine(repoRoot, "validation", "output-slang-textured-gl");

Console.WriteLine("=== issue #252: real-slangc textured shaders on OpenGL, texture on SpriteBatch's unit 0 (real MonoGame DesktopGL) ===");
Console.WriteLine($"[slang-tex] tolerance: {tolerance}  out: {outDir}\n");

if (!SlangToolPath.IsSupportedOnThisPlatform || SlangToolPath.Resolve() is null)
{
    Console.Error.WriteLine("[slang-tex] FAIL: no runnable restored slangc for this host. Run tools/restore.sh / restore.ps1 first.");
    return 1;
}
if (!File.Exists(goldenInvert))
{
    Console.Error.WriteLine($"[slang-tex] FAIL: the mgfxc golden is missing: {goldenInvert}");
    return 1;
}

var entryAttr = new Regex("""\[\s*shader\s*\(\s*"(?<stage>[a-z]+)"\s*\)\s*\]""");
string[] textured = Directory.GetFiles(corpusDir, "*.slang")
    .OrderBy(f => f, StringComparer.Ordinal)
    .Where(f =>
    {
        string text = File.ReadAllText(f);
        var stages = entryAttr.Matches(text).Select(m => m.Groups["stage"].Value).ToList();
        return stages.Count == 1 && stages[0] is "fragment" or "pixel" && text.Contains("Texture2D", StringComparison.Ordinal);
    })
    .ToArray();

int failures = 0;
var rows = new List<ShaderRow>();
foreach (string file in textured)
{
    string name = Path.GetFileNameWithoutExtension(file);
    if (!Expectations.All.TryGetValue(name, out Expectation? expectation))
    {
        Console.WriteLine($"[slang-tex] FAIL {name}: textured PS-only corpus shader with no CPU expectation in this gate; add one.");
        failures++;
        continue;
    }

    var result = new SlangCompiler().Compile(
        File.ReadAllText(file),
        new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = Path.GetFileName(file) });
    if (result.IsFailure)
    {
        Console.WriteLine($"[slang-tex] FAIL {name}: compile: " +
                          string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        failures++;
        continue;
    }

    byte[] mgfx = result.Value.Data;
    var reader = MgfxBlobReader.Parse(mgfx);
    string table = string.Join(", ", reader.Samplers.Select(s => $"{s.Name}: texSlot {s.TextureSlot}, sampSlot {s.SamplerSlot}"));
    bool onUnitZero = reader.Samplers.Count == 1 && reader.Samplers[0].TextureSlot == 0 && reader.Samplers[0].SamplerSlot == 0;
    Console.WriteLine($"[slang-tex] {name,-12} sampler table [{table}] -> " +
                      (onUnitZero ? "OK (unit 0)" : "WRONG: SpriteBatch binds unit 0, the shader does not read it"));
    if (!onUnitZero)
        failures++;

    rows.Add(new ShaderRow(name, mgfx, expectation));
}
Console.WriteLine();

if (rows.Count < 6)
{
    Console.WriteLine($"[slang-tex] FAIL: only {rows.Count} textured shaders compiled; expected the 6-shader set (Invert, Posterize, Sepia, Threshold, TintUniform, Vignette).");
    failures++;
}

Directory.CreateDirectory(outDir);
using var game = new SlangTexturedGame(rows, File.ReadAllBytes(goldenInvert), outDir, tolerance);
game.Run();

if (game.Skipped)
{
    bool requireGl = string.Equals(Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_GL"), "1", StringComparison.Ordinal);
    Console.WriteLine($"[slang-tex] {(requireGl ? "FAIL" : "SKIPPED")} (no GL device): {game.SkipReason}");
    return requireGl ? 1 : 0;
}

foreach (string line in game.Report)
    Console.WriteLine(line);
failures += game.Failures;

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"[slang-tex] PASS: {rows.Count} real-slangc textured shaders render correctly with SpriteBatch's unit-0 texture (issue #252)."
    : $"[slang-tex] FAIL: {failures} failure(s).");
return failures == 0 ? 0 : 1;

static string FindRepoRoot()
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            return dir.FullName;
    }
    throw new DirectoryNotFoundException("could not locate the repository root (ShadowDusk.slnx)");
}

// -----------------------------------------------------------------------------

internal sealed record ShaderRow(string Name, byte[] Mgfx, Expectation Expectation);

/// <summary>A shader's own math on the CPU, plus the uniforms the render sets.</summary>
internal sealed record Expectation(
    Func<Vector4, Vector2, Vector4> Shade,
    Action<Effect> SetUniforms);

internal static class Expectations
{
    private static readonly Vector4 Tint = new(0.5f, 1.0f, 0.25f, 1.0f);
    private const float Levels = 4f;
    private const float Cutoff = 0.5f;

    public static readonly IReadOnlyDictionary<string, Expectation> All = new Dictionary<string, Expectation>(StringComparer.Ordinal)
    {
        ["Invert"] = new((c, _) => new Vector4(1 - c.X, 1 - c.Y, 1 - c.Z, c.W), _ => { }),
        ["Sepia"] = new((c, _) => new Vector4(
                Sat(c.X * 0.393f + c.Y * 0.769f + c.Z * 0.189f),
                Sat(c.X * 0.349f + c.Y * 0.686f + c.Z * 0.168f),
                Sat(c.X * 0.272f + c.Y * 0.534f + c.Z * 0.131f),
                c.W), _ => { }),
        ["Vignette"] = new((c, uv) =>
        {
            float dx = uv.X - 0.5f, dy = uv.Y - 0.5f;
            float d2 = dx * dx + dy * dy;
            float fade = Sat(1.0f - d2 * 2.5f);
            return new Vector4(c.X * fade, c.Y * fade, c.Z * fade, c.W);
        }, _ => { }),
        ["TintUniform"] = new((c, _) => c * Tint, e => Param(e, "Tint").SetValue(Tint)),
        ["Posterize"] = new((c, _) =>
        {
            float Q(float v) => Sat(MathF.Floor(v * Levels) / MathF.Max(Levels - 1f, 1f));
            return new Vector4(Q(c.X), Q(c.Y), Q(c.Z), c.W);
        }, e => Param(e, "Levels").SetValue(Levels)),
        ["Threshold"] = new((c, _) =>
        {
            float luma = c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
            float v = luma >= Cutoff ? 1f : 0f;
            return new Vector4(v, v, v, c.W);
        }, e => Param(e, "Cutoff").SetValue(Cutoff)),
    };

    private static float Sat(float v) => Math.Clamp(v, 0f, 1f);

    private static EffectParameter Param(Effect e, string name) =>
        e.Parameters[name] ?? throw new InvalidOperationException(
            $"effect has no '{name}' parameter (has: {string.Join(", ", e.Parameters.Select(p => p.Name))})");
}

internal sealed class SlangTexturedGame : Game
{
    private const int Size = 64;

    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<ShaderRow> _rows;
    private readonly byte[] _invertGolden;
    private readonly string _outDir;
    private readonly int _tolerance;
    private bool _done;

    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public int Failures { get; private set; }
    public List<string> Report { get; } = new();

    public SlangTexturedGame(IReadOnlyList<ShaderRow> rows, byte[] invertGolden, string outDir, int tolerance)
    {
        _rows = rows;
        _invertGolden = invertGolden;
        _outDir = outDir;
        _tolerance = tolerance;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk SlangTexturedGl (headless)";
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

        GraphicsDevice gd = GraphicsDevice;
        Color[] texels = SourceTexels();
        using var sprite = new Texture2D(gd, Size, Size, false, SurfaceFormat.Color);
        sprite.SetData(texels);

        foreach (ShaderRow row in _rows)
        {
            try
            {
                Validate(gd, row, sprite, texels);
            }
            catch (Exception ex)
            {
                Report.Add($"[slang-tex] {row.Name,-12} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Failures++;
            }
        }
        Exit();
    }

    private void Validate(GraphicsDevice gd, ShaderRow row, Texture2D sprite, Color[] texels)
    {
        Color[] expected = Expected(row.Expectation, texels);

        using var effect = new Effect(gd, row.Mgfx);
        row.Expectation.SetUniforms(effect);
        Color[] image = RenderSprite(gd, effect, sprite);
        SavePng(gd, image, $"{row.Name}_slang.png");

        (int maxd, int over) = Compare(image, expected);
        bool ok = over == 0;
        Report.Add($"[slang-tex] {row.Name,-12} real-slangc vs CPU expectation: maxd {maxd}, {over} px over tolerance -> " +
                   (ok ? "OK" : $"WRONG (centre {Fmt(image[Px(Size / 2, Size / 2)])}, want {Fmt(expected[Px(Size / 2, Size / 2)])})"));
        if (!ok)
            Failures++;

        if (row.Name != "Invert")
            return;

        // The mgfxc control: the equivalent .fx built by the reference compiler, same scene.
        using var golden = new Effect(gd, _invertGolden);
        Color[] goldImage = RenderSprite(gd, golden, sprite);
        SavePng(gd, goldImage, "Invert_mgfxc.png");

        (int gmaxd, int gover) = Compare(goldImage, expected);
        bool controlOk = gover == 0;
        Report.Add($"[slang-tex] {row.Name,-12} mgfxc golden vs CPU expectation: maxd {gmaxd}, {gover} px over tolerance -> " +
                   (controlOk ? "OK (control)" : "HARNESS FAULT: the reference build does not render the expected image"));

        (int smaxd, int sover) = Compare(image, goldImage);
        bool sameAsMgfxc = sover == 0;
        Report.Add($"[slang-tex] {row.Name,-12} real-slangc vs mgfxc golden:      maxd {smaxd}, {sover} px over tolerance -> " +
                   (sameAsMgfxc ? "OK" : "WRONG"));
        if (!controlOk || !sameAsMgfxc)
            Failures++;
    }

    /// <summary>Non-uniform on every channel so a wrong unit, a constant colour, or a
    /// transposed read all show up: R ramps with x, G with y, B with the checker of both.</summary>
    private static Color[] SourceTexels()
    {
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[Px(x, y)] = new Color(x * 4 + 2, y * 4 + 1, ((x / 8 + y / 8) % 2) * 200 + 30, 255);
        return px;
    }

    private static Color[] Expected(Expectation e, Color[] texels)
    {
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Color t = texels[Px(x, y)];
                var c = new Vector4(t.R / 255f, t.G / 255f, t.B / 255f, t.A / 255f);
                var uv = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size);
                Vector4 o = e.Shade(c, uv);
                px[Px(x, y)] = new Color(
                    (byte)Math.Round(Math.Clamp(o.X, 0f, 1f) * 255f),
                    (byte)Math.Round(Math.Clamp(o.Y, 0f, 1f) * 255f),
                    (byte)Math.Round(Math.Clamp(o.Z, 0f, 1f) * 255f),
                    (byte)Math.Round(Math.Clamp(o.W, 0f, 1f) * 255f));
            }
        }
        return px;
    }

    /// <summary>The texture reaches the shader ONLY through SpriteBatch (unit 0), never
    /// through effect.Parameters: that is the realistic idiom, and it is what makes the
    /// sampler's unit observable in the picture.</summary>
    private static Color[] RenderSprite(GraphicsDevice gd, Effect effect, Texture2D sprite)
    {
        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Magenta);
        using var sb = new SpriteBatch(gd);
        sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, null, null, effect);
        sb.Draw(sprite, new Rectangle(0, 0, Size, Size), Color.White);
        sb.End();
        gd.SetRenderTarget(null);
        var px = new Color[Size * Size];
        rt.GetData(px);
        return px;
    }

    private (int MaxDelta, int Over) Compare(Color[] a, Color[] b)
    {
        int maxDelta = 0, over = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)),
                             Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A)));
            if (d > maxDelta) maxDelta = d;
            if (d > _tolerance) over++;
        }
        return (maxDelta, over);
    }

    private void SavePng(GraphicsDevice gd, Color[] img, string name)
    {
        using var rt = new RenderTarget2D(gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        rt.SetData(img);
        using var fs = File.Create(Path.Combine(_outDir, name));
        rt.SaveAsPng(fs, Size, Size);
    }

    private static int Px(int x, int y) => y * Size + x;

    private static string Fmt(Color c) => $"({c.R},{c.G},{c.B},{c.A})";
}
