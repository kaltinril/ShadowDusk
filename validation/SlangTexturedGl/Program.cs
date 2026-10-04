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
// One more row, not from the corpus (issue #252 follow-up): Invert with its sampler register
// written ONLY in the '#else' of an '#if OPENGL'. That register never reaches an OpenGL
// compile, so the texture must still be on unit 0 and the picture must still match the CPU
// expectation and the mgfxc golden. When "the author wrote a register" was read from the raw
// source text, the dead register kept slangc's invented register(s0) alive and put the
// texture back on ps_s1.
//
// One more row (issue #252, author-written register): Invert through a combined
// 'Sampler2D SpriteTexture : register(s0)', which must land on unit 0 like mgfxc's legacy
// 'sampler2D : register(s0)' (it used to land on unit 1), and match the CPU and the mgfxc golden.
//
// One more row (issue #302): Invert's sprite multiplied by a SECOND texture read through a
// combined 'Sampler2D Comb', which the game sets the way a game sets any texture parameter:
// effect.Parameters["Comb"].SetValue(texture). slangc hoists the combined sampler's texture
// out under a generated name (Comb_texture_0), and that name used to be the reflected
// parameter, so effect.Parameters["Comb"] was null in real MonoGame. The row fails on that
// null (the parameter list is printed), and its picture must match the CPU expectation of
// both textures (so a texture bound to the wrong unit shows up too).
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

void AddRow(string name, string source, string sourceFileName, Expectation expectation, bool compareWithInvertGolden)
{
    var result = new SlangCompiler().Compile(
        source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = sourceFileName });
    if (result.IsFailure)
    {
        Console.WriteLine($"[slang-tex] FAIL {name}: compile: " +
                          string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        failures++;
        return;
    }

    byte[] mgfx = result.Value.Data;
    var reader = MgfxBlobReader.Parse(mgfx);
    string table = string.Join(", ", reader.Samplers.Select(s =>
        $"{s.Name} ({reader.Parameters[s.Parameter].Name}): texSlot {s.TextureSlot}, sampSlot {s.SamplerSlot}"));
    // The sprite's sampler on unit 0 and nothing else, except the issue #302 row, whose second
    // texture must be a parameter under the author's name (the game sets it by that name).
    bool spriteOnUnitZero = reader.Samplers.Any(s =>
        reader.Parameters[s.Parameter].Name == "SpriteTexture" && s.TextureSlot == 0 && s.SamplerSlot == 0);
    bool tableShape = expectation.CombinedTexture is null
        ? reader.Samplers.Count == 1
        : reader.Samplers.Count == 2 && reader.Samplers.Any(s => reader.Parameters[s.Parameter].Name == expectation.CombinedTexture);
    bool onUnitZero = spriteOnUnitZero && tableShape;
    Console.WriteLine($"[slang-tex] {name,-12} sampler table [{table}] -> " +
                      (onUnitZero ? "OK (unit 0)" : expectation.CombinedTexture is null
                          ? "WRONG: SpriteBatch binds unit 0, the shader does not read it"
                          : $"WRONG: expected SpriteTexture on unit 0 plus a texture parameter named '{expectation.CombinedTexture}'"));
    if (!onUnitZero)
        failures++;

    rows.Add(new ShaderRow(name, mgfx, expectation, compareWithInvertGolden));
}

foreach (string file in textured)
{
    string name = Path.GetFileNameWithoutExtension(file);
    if (!Expectations.All.TryGetValue(name, out Expectation? expectation))
    {
        Console.WriteLine($"[slang-tex] FAIL {name}: textured PS-only corpus shader with no CPU expectation in this gate; add one.");
        failures++;
        continue;
    }

    AddRow(name, File.ReadAllText(file), Path.GetFileName(file), expectation, compareWithInvertGolden: name == "Invert");
}

if (rows.Count < 6)
{
    Console.WriteLine($"[slang-tex] FAIL: only {rows.Count} textured shaders compiled; expected the 6-shader set (Invert, Posterize, Sepia, Threshold, TintUniform, Vignette).");
    failures++;
}

// Issue #252 follow-up: Invert, with the sampler's register only in the branch OpenGL skips.
{
    const string plainSampler = "SamplerState SpriteSampler;";
    const string branchedSampler =
        "#if OPENGL\nSamplerState SpriteSampler;\n#else\nSamplerState SpriteSampler : register(s0);\n#endif";
    string invert = File.ReadAllText(Path.Combine(corpusDir, "Invert.slang"));
    if (!invert.Contains(plainSampler, StringComparison.Ordinal))
    {
        Console.WriteLine("[slang-tex] FAIL Invert#if: Invert.slang no longer declares 'SamplerState SpriteSampler;', so the inactive-branch row cannot be built.");
        failures++;
    }
    else
    {
        AddRow("Invert#if", invert.Replace(plainSampler, branchedSampler, StringComparison.Ordinal),
            "InvertInactiveBranchRegister.slang", Expectations.All["Invert"], compareWithInvertGolden: true);
    }
}

// Issue #302: a combined Sampler2D the game sets through effect.Parameters["Comb"].
AddRow("InvertComb", """
    // Issue #302 gate row: the sprite (SpriteBatch, unit 0) multiplied by a second texture the
    // game sets by name through a combined sampler.
    Texture2D SpriteTexture;
    SamplerState SpriteSampler;
    Sampler2D Comb;

    [shader("fragment")]
    float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
    {
        float4 c = SpriteTexture.Sample(SpriteSampler, uv);
        float4 k = Comb.Sample(uv);
        return float4((1.0 - c.rgb) * k.rgb, c.a);
    }
    """, "InvertCombinedSampler.slang", Expectations.InvertComb, compareWithInvertGolden: false);

// Issue #252, author-written register: the sprite read through a COMBINED sampler the author
// pinned to s0, the way a ported '.fx' writes 'sampler2D SpriteTextureSampler : register(s0)'.
// mgfxc gives the legacy combined sampler unit 0 on OpenGL; this used to land on unit 1 (the
// split pair's reservation rule), where SpriteBatch's texture never was.
AddRow("InvertS0", """
    // Gate row: Invert through a combined sampler with the author's register(s0).
    Sampler2D SpriteTexture : register(s0);

    [shader("fragment")]
    float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
    {
        float4 c = SpriteTexture.Sample(uv);
        return float4(1.0 - c.rgb, c.a);
    }
    """, "InvertCombinedS0.slang", Expectations.All["Invert"], compareWithInvertGolden: true);
Console.WriteLine();

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
    ? $"[slang-tex] PASS: {rows.Count} real-slangc textured shaders render correctly with SpriteBatch's unit-0 texture (issue #252) and the combined sampler set by the author's name (issue #302)."
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

/// <summary><paramref name="CompareWithInvertGolden"/>: the row is the Invert program, so it is
/// also rendered against the committed mgfxc golden of the equivalent <c>.fx</c>.</summary>
internal sealed record ShaderRow(string Name, byte[] Mgfx, Expectation Expectation, bool CompareWithInvertGolden);

/// <summary>A shader's own math on the CPU, plus the uniforms the render sets.</summary>
/// <param name="Shade">(sprite texel, second texel, uv) to the output colour. The second texel is
/// the game-set texture's, meaningful only when <paramref name="CombinedTexture"/> is set.</param>
/// <param name="CombinedTexture">Issue #302: the name of the texture parameter the game sets
/// its second texture through (<c>effect.Parameters[name].SetValue</c>), or null.</param>
internal sealed record Expectation(
    Func<Vector4, Vector4, Vector2, Vector4> Shade,
    Action<Effect> SetUniforms,
    string? CombinedTexture = null);

internal static class Expectations
{
    private static readonly Vector4 Tint = new(0.5f, 1.0f, 0.25f, 1.0f);
    private const float Levels = 4f;
    private const float Cutoff = 0.5f;

    public static readonly IReadOnlyDictionary<string, Expectation> All = new Dictionary<string, Expectation>(StringComparer.Ordinal)
    {
        ["Invert"] = new((c, _, _) => new Vector4(1 - c.X, 1 - c.Y, 1 - c.Z, c.W), _ => { }),
        ["Sepia"] = new((c, _, _) => new Vector4(
                Sat(c.X * 0.393f + c.Y * 0.769f + c.Z * 0.189f),
                Sat(c.X * 0.349f + c.Y * 0.686f + c.Z * 0.168f),
                Sat(c.X * 0.272f + c.Y * 0.534f + c.Z * 0.131f),
                c.W), _ => { }),
        ["Vignette"] = new((c, _, uv) =>
        {
            float dx = uv.X - 0.5f, dy = uv.Y - 0.5f;
            float d2 = dx * dx + dy * dy;
            float fade = Sat(1.0f - d2 * 2.5f);
            return new Vector4(c.X * fade, c.Y * fade, c.Z * fade, c.W);
        }, _ => { }),
        ["TintUniform"] = new((c, _, _) => c * Tint, e => Param(e, "Tint").SetValue(Tint)),
        ["Posterize"] = new((c, _, _) =>
        {
            float Q(float v) => Sat(MathF.Floor(v * Levels) / MathF.Max(Levels - 1f, 1f));
            return new Vector4(Q(c.X), Q(c.Y), Q(c.Z), c.W);
        }, e => Param(e, "Levels").SetValue(Levels)),
        ["Threshold"] = new((c, _, _) =>
        {
            float luma = c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
            float v = luma >= Cutoff ? 1f : 0f;
            return new Vector4(v, v, v, c.W);
        }, e => Param(e, "Cutoff").SetValue(Cutoff)),
    };

    /// <summary>Issue #302: Invert of the sprite, multiplied by the game-set texture read through
    /// the combined sampler <c>Comb</c>. The game sets that texture through
    /// <c>effect.Parameters["Comb"]</c>, the name the author wrote.</summary>
    public static readonly Expectation InvertComb = new(
        (c, k, _) => new Vector4((1 - c.X) * k.X, (1 - c.Y) * k.Y, (1 - c.Z) * k.Z, c.W),
        _ => { },
        CombinedTexture: "Comb");

    private static float Sat(float v) => Math.Clamp(v, 0f, 1f);

    public static EffectParameter Param(Effect e, string name) =>
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
        Color[] secondTexels = SecondTexels();
        using var second = new Texture2D(gd, Size, Size, false, SurfaceFormat.Color);
        second.SetData(secondTexels);

        foreach (ShaderRow row in _rows)
        {
            try
            {
                Validate(gd, row, sprite, texels, second, secondTexels);
            }
            catch (Exception ex)
            {
                Report.Add($"[slang-tex] {row.Name,-12} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Failures++;
            }
        }
        Exit();
    }

    private void Validate(GraphicsDevice gd, ShaderRow row, Texture2D sprite, Color[] texels, Texture2D second, Color[] secondTexels)
    {
        Color[] expected = Expected(row.Expectation, texels, secondTexels);

        using var effect = new Effect(gd, row.Mgfx);
        row.Expectation.SetUniforms(effect);
        // Issue #302: the second texture reaches the shader by the author's parameter name, as a
        // game would set it. A missing parameter throws, naming the parameters the effect has.
        if (row.Expectation.CombinedTexture is { } combined)
            Expectations.Param(effect, combined).SetValue(second);
        Color[] image = RenderSprite(gd, effect, sprite);
        string fileStem = row.Name.Replace('#', '_');
        SavePng(gd, image, $"{fileStem}_slang.png");

        (int maxd, int over) = Compare(image, expected);
        bool ok = over == 0;
        Report.Add($"[slang-tex] {row.Name,-12} real-slangc vs CPU expectation: maxd {maxd}, {over} px over tolerance -> " +
                   (ok ? "OK" : $"WRONG (centre {Fmt(image[Px(Size / 2, Size / 2)])}, want {Fmt(expected[Px(Size / 2, Size / 2)])})"));
        if (!ok)
            Failures++;

        if (!row.CompareWithInvertGolden)
            return;

        // The mgfxc control: the equivalent .fx built by the reference compiler, same scene.
        using var golden = new Effect(gd, _invertGolden);
        Color[] goldImage = RenderSprite(gd, golden, sprite);
        SavePng(gd, goldImage, $"{fileStem}_mgfxc.png");

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

    /// <summary>The issue #302 row's second texture: non-uniform too, and unlike the sprite on
    /// every channel, so reading the sprite twice (or the wrong unit) cannot pass.</summary>
    private static Color[] SecondTexels()
    {
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                px[Px(x, y)] = new Color(255 - y * 4, 255 - x * 4, 40 + ((x / 16 + y / 16) % 2) * 180, 255);
        return px;
    }

    private static Color[] Expected(Expectation e, Color[] texels, Color[] secondTexels)
    {
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Color t = texels[Px(x, y)];
                var c = new Vector4(t.R / 255f, t.G / 255f, t.B / 255f, t.A / 255f);
                Color s = secondTexels[Px(x, y)];
                var k = new Vector4(s.R / 255f, s.G / 255f, s.B / 255f, s.A / 255f);
                var uv = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size);
                Vector4 o = e.Shade(c, k, uv);
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
