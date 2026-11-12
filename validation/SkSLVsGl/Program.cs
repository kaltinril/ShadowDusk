// =============================================================================
// SkSLVsGl: render-fidelity gate for the SkSL INPUT frontend (ShadowDusk.SkSL).
// -----------------------------------------------------------------------------
//     SkSL --[real SkiaSharp SKRuntimeEffect, CPU raster]--------------> reference image
//     SkSL --[SkSLToFxConverter]--> .fx --[EffectCompiler, OpenGL]--> .mgfx
//          --[real MonoGame DesktopGL Effect, NDC fullscreen quad]----> candidate image
//
// Skia has no reference compiler for this direction, so the evidence is rendered-image fidelity
// against real Skia (as validation/SkiaVsKni is for the output direction). Same source texels,
// same size, same uniforms; tolerance 2/255.
//
// The host contract under test (SkSLToFxConverter's XML doc): a non-square render target (64x48,
// so an X/Y mix-up in the coordinate mapping shows), children of other non-square sizes bound with
// <child>TexelSize = 1/size, premultiplied texels (some translucent), and every case drawn with
// premultiplied source-over (Skia SrcOver, MonoGame BlendState.AlphaBlend) onto the same opaque
// background, so a translucent result is compared after blending, as a consumer would see it.
//
// Positive controls (each MUST diverge or the gate fails):
//   * control-flipped-y          Skia: the gradient with its Y flipped (origin convention);
//   * control-child-offset       Skia: the child-eval case with a different eval offset;
//   * control-swapped-resolution GL: the gradient with iResolution = (height, width) (non-square axis);
//   * control-nonpremul-blend    GL: the translucent case drawn with BlendState.NonPremultiplied;
//   * control-straight-texture   GL: the translucent child uploaded un-premultiplied (alpha axis).
// Exit: 0 pass (or no GL device without SHADOWDUSK_REQUIRE_GL=1), 1 divergence, 2 setup error.
// =============================================================================

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ShaderToy;
using ShadowDusk.SkSL;
using ShadowDusk.Validation;
using SkiaSharp;

const int Width = 64, Height = 48;
int tolerance = 2;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string outDir = Path.Combine(ShaderInputs.FindRepoRoot(), "validation", "output-sksl");
Directory.CreateDirectory(outDir);

// Background both arms draw onto (opaque, so the blended result is comparable as plain RGBA).
var background = new byte[] { 30, 60, 90, 255 };
float[] sizeUniform = { Width, Height };
Texels opaque = Texels.Make(50, 34, translucent: false, seed: 0);
Texels glass = Texels.Make(50, 34, translucent: true, seed: 0);
Texels second = Texels.Make(40, 56, translucent: true, seed: 7);

var cases = new List<Case>
{
    new("Gradient",
        "uniform float2 size;\nhalf4 main(float2 c) { half2 uv = half2(c / size); return half4(uv.x, uv.y, 0.25, 1); }",
        new() { ["size"] = sizeUniform }, new()),
    new("Math",
        "uniform float2 size; uniform half4 tint;\n" +
        "half4 main(float2 c) { float2 uv = c / size; half3 k = half3(mix(tint.rgb, half3(uv.x, uv.y, 1 - uv.x), 0.5));\n" +
        "  return half4(fract(k * 3.0), 1); }",
        new() { ["size"] = sizeUniform, ["tint"] = new[] { 0.9f, 0.4f, 0.2f, 1f } }, new()),
    new("ChildEval",
        "uniform shader img;\n" +
        "half4 main(float2 c) { half4 s = img.eval(c + float2(30, 20)); return half4(1 - s.r, s.g, s.b, 1); }",
        new(), new() { ["img"] = opaque }),
    new("Translucent",
        "uniform float2 size;\n" +
        "half4 main(float2 c) { half2 uv = half2(c / size); half a = 0.2 + 0.6 * uv.x;\n" +
        "  return half4(uv.x, uv.y, 0.5, 1) * a; }",
        new() { ["size"] = sizeUniform }, new()),
    new("TranslucentChild",
        "uniform shader img;\n" +
        "half4 main(float2 c) { return img.eval(c * 0.9) * 0.8; }",
        new(), new() { ["img"] = glass }),
    new("TwoChildren",
        "uniform shader a; uniform shader b;\n" +
        "half4 main(float2 c) { return mix(a.eval(c), b.eval(float2(c.y, c.x)), 0.5); }",
        new(), new() { ["a"] = glass, ["b"] = second }),
};

// Skia-only variants (the GL arm keeps the real shader).
var skiaControls = new Dictionary<string, (string Of, string Sksl)>
{
    ["control-flipped-y"] = ("Gradient",
        "uniform float2 size;\nhalf4 main(float2 c) { half2 uv = half2(float2(c.x, size.y - c.y) / size); return half4(uv.x, uv.y, 0.25, 1); }"),
    ["control-child-offset"] = ("ChildEval",
        "uniform shader img;\n" +
        "half4 main(float2 c) { half4 s = img.eval(c + float2(33, 20)); return half4(1 - s.r, s.g, s.b, 1); }"),
};

// GL-only variants: the real converted shader, drawn with a host-contract mistake.
var glJobs = cases.Select(c => new GlJob(c.Name, c, SwapResolution: false, NonPremulBlend: false, StraightTextures: false)).ToList();
glJobs.Add(new GlJob("control-swapped-resolution", cases.First(c => c.Name == "Gradient"), true, false, false));
glJobs.Add(new GlJob("control-nonpremul-blend", cases.First(c => c.Name == "Translucent"), false, true, false));
glJobs.Add(new GlJob("control-straight-texture", cases.First(c => c.Name == "TranslucentChild"), false, false, true));

// ---- Convert + compile every case through the real product pipeline ----------
var mgfx = new Dictionary<string, byte[]>();
foreach (Case c in cases)
{
    ConvertResult conv = SkSLToFxConverter.Convert(c.Sksl);
    if (!conv.Success || conv.Fx is null)
    {
        Console.Error.WriteLine($"[sksl] {c.Name}: conversion FAILED: " + string.Join(" | ", conv.Diagnostics.Select(d => d.Message)));
        return 2;
    }
    await File.WriteAllTextAsync(Path.Combine(outDir, c.Name + ".converted.fx"), conv.Fx);
    var compiled = await new EffectCompiler().CompileAsync(conv.Fx, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = c.Name + ".fx",
    });
    if (compiled.IsFailure)
    {
        Console.Error.WriteLine($"[sksl] {c.Name}: OpenGL compile FAILED: " + string.Join(" | ", compiled.Error.Select(e => $"{e.Code}: {e.Message}")));
        return 2;
    }
    mgfx[c.Name] = compiled.Value.Data;
}

// ---- Skia arm (reference) ----------------------------------------------------
var reference = new Dictionary<string, byte[]>();
foreach (Case c in cases)
    reference[c.Name] = RenderSkia(c.Sksl, c);
foreach ((string name, (string of, string sksl)) in skiaControls)
    reference[name] = RenderSkia(sksl, cases.First(k => k.Name == of));

// ---- GL arm (candidate) -------------------------------------------------------
using var game = new GlArm(glJobs, mgfx, background, Width, Height);
game.Run();

if (game.Skipped)
{
    if (Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_GL") == "1")
    {
        Console.Error.WriteLine($"[sksl] FAIL: SHADOWDUSK_REQUIRE_GL=1 but no GL device: {game.SkipReason}");
        return 1;
    }
    Console.WriteLine($"[sksl] SKIPPED (no GL device): {game.SkipReason}");
    return 0;
}

bool ok = game.Failures.Count == 0;
foreach (string f in game.Failures)
    Console.Error.WriteLine("[sksl] " + f);

foreach (Case c in cases)
{
    byte[] cand = game.Images[c.Name], refImg = reference[c.Name];
    WritePng(Path.Combine(outDir, c.Name + ".skia.png"), refImg);
    WritePng(Path.Combine(outDir, c.Name + ".gl.png"), cand);
    (int maxd, int over) = Compare(refImg, cand, tolerance);
    bool match = over == 0 && Distinct(refImg) > 8;
    Console.WriteLine($"[sksl] {c.Name,-28} maxd {maxd,3}, {over} px over {tolerance}, {Distinct(refImg)} colors -> {(match ? "MATCH" : "DIFF")}");
    ok &= match;
}

var controlPairs = skiaControls.Select(kv => (Name: kv.Key, Skia: reference[kv.Key], Gl: game.Images[kv.Value.Of]))
    .Concat(glJobs.Where(j => j.Name.StartsWith("control-", StringComparison.Ordinal))
        .Select(j => (Name: j.Name, Skia: reference[j.Case.Name], Gl: game.Images[j.Name])));
foreach ((string name, byte[] skia, byte[] gl) in controlPairs)
{
    (int maxd, int over) = Compare(skia, gl, tolerance);
    bool caught = over > 0;
    Console.WriteLine($"[sksl] {name,-28} maxd {maxd,3}, {over} px over {tolerance} -> {(caught ? "caught (diverges, as it must)" : "NOT CAUGHT")}");
    ok &= caught;
}

Console.WriteLine($"\n[sksl] {(ok ? "PASS" : "FAIL")}: SkSL frontend render fidelity vs real SkiaSharp.");
return ok ? 0 : 1;

// ----------------------------------------------------------------------------
byte[] RenderSkia(string sksl, Case c)
{
    using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(sksl, out string errors);
    if (effect is null)
        throw new InvalidDataException($"{c.Name}: Skia rejected the SkSL: {errors}");

    var uniforms = new SKRuntimeEffectUniforms(effect);
    foreach (string name in effect.Uniforms)
    {
        float[] v = c.Uniforms[name];
        if (v.Length == 1) uniforms[name] = v[0]; else uniforms[name] = v;
    }

    var children = new SKRuntimeEffectChildren(effect);
    var owned = new List<IDisposable>();
    foreach ((string name, Texels tex) in c.Children)
    {
        var info = new SKImageInfo(tex.Width, tex.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        SKImage image = SKImage.FromPixelCopy(info, tex.Premul);
        SKShader child = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        owned.Add(image);
        owned.Add(child);
        children[name] = child;
    }

    try
    {
        using SKShader shader = effect.ToShader(uniforms, children);
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(background[0], background[1], background[2], background[3]));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcOver, IsAntialias = false })
            canvas.DrawRect(new SKRect(0, 0, Width, Height), paint);
        return bitmap.Bytes;
    }
    finally
    {
        foreach (IDisposable d in owned) d.Dispose();
    }
}

static (int MaxDelta, int Over) Compare(byte[] a, byte[] b, int tol)
{
    int maxd = 0, over = 0;
    for (int i = 0; i < a.Length; i += 4)
    {
        int d = 0;
        for (int ch = 0; ch < 4; ch++)
            d = Math.Max(d, Math.Abs(a[i + ch] - b[i + ch]));
        maxd = Math.Max(maxd, d);
        if (d > tol) over++;
    }
    return (maxd, over);
}

static int Distinct(byte[] rgba)
{
    var seen = new HashSet<int>();
    for (int i = 0; i < rgba.Length; i += 4) seen.Add(BitConverter.ToInt32(rgba, i));
    return seen.Count;
}

static void WritePng(string path, byte[] rgba)
{
    // Both arms blend onto an opaque background, so every pixel is opaque and premul == straight.
    var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using SKImage image = SKImage.FromPixelCopy(info, rgba);
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream fs = File.Create(path);
    data.SaveTo(fs);
}

/// <summary>A child image: the same texels premultiplied (what both arms bind) and straight (a control).</summary>
internal sealed record Texels(int Width, int Height, byte[] Premul, byte[] Straight)
{
    public static Texels Make(int w, int h, bool translucent, int seed)
    {
        var premul = new byte[w * h * 4];
        var straight = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte r = (byte)((x * 5 + seed * 11) & 255), g = (byte)((y * 7 + seed * 3) & 255), b = (byte)((x * 5 + y * 3) & 255);
                byte a = translucent ? (byte)(64 + ((x * 3 + y * 5) % 160)) : (byte)255;
                straight[i] = r; straight[i + 1] = g; straight[i + 2] = b; straight[i + 3] = a;
                premul[i] = (byte)((r * a + 127) / 255);
                premul[i + 1] = (byte)((g * a + 127) / 255);
                premul[i + 2] = (byte)((b * a + 127) / 255);
                premul[i + 3] = a;
            }
        return new Texels(w, h, premul, straight);
    }
}

internal sealed record Case(string Name, string Sksl, Dictionary<string, float[]> Uniforms, Dictionary<string, Texels> Children);

internal sealed record GlJob(string Name, Case Case, bool SwapResolution, bool NonPremulBlend, bool StraightTextures);

internal sealed class GlArm : Game
{
    private readonly IReadOnlyList<GlJob> _jobs;
    private readonly IReadOnlyDictionary<string, byte[]> _mgfx;
    private readonly Color _background;
    private readonly int _width, _height;
    private bool _done;

    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Failures { get; } = new();
    public Dictionary<string, byte[]> Images { get; } = new();

    public GlArm(IReadOnlyList<GlJob> jobs, IReadOnlyDictionary<string, byte[]> mgfx, byte[] background, int width, int height)
    {
        _jobs = jobs; _mgfx = mgfx; _width = width; _height = height;
        _background = new Color(background[0], background[1], background[2], background[3]);
        _ = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width,
            PreferredBackBufferHeight = height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk SkSL vs Skia (headless)";
    }

    protected override void Initialize()
    {
        try { base.Initialize(); }
        catch (Exception ex) { Skipped = true; SkipReason = $"{ex.GetType().Name}: {ex.Message}"; }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done || Skipped) { Exit(); return; }
        _done = true;
        try { RenderAll(); }
        catch (Exception ex) { Failures.Add($"threw: {ex}"); }
        Exit();
    }

    private void RenderAll()
    {
        GraphicsDevice gd = GraphicsDevice;
        var quad = new[]
        {
            new NdcVertex(-1f, -1f), new NdcVertex(-1f, 1f), new NdcVertex(1f, -1f),
            new NdcVertex( 1f, -1f), new NdcVertex(-1f, 1f), new NdcVertex(1f,  1f),
        };

        foreach (GlJob job in _jobs)
        {
            Case c = job.Case;
            using var effect = new Effect(gd, _mgfx[c.Name]);
            effect.Parameters["iResolution"].SetValue(job.SwapResolution
                ? new Vector3(_height, _width, 1f)
                : new Vector3(_width, _height, 1f));
            foreach ((string name, float[] v) in c.Uniforms)
            {
                EffectParameter? p = effect.Parameters[name];
                if (p is null) { Failures.Add($"{job.Name}: effect has no parameter '{name}'"); continue; }
                switch (v.Length)
                {
                    case 1: p.SetValue(v[0]); break;
                    case 2: p.SetValue(new Vector2(v[0], v[1])); break;
                    case 3: p.SetValue(new Vector3(v[0], v[1], v[2])); break;
                    default: p.SetValue(new Vector4(v[0], v[1], v[2], v[3])); break;
                }
            }

            var textures = new List<Texture2D>();
            foreach ((string name, Texels tex) in c.Children)
            {
                var t2d = new Texture2D(gd, tex.Width, tex.Height, false, SurfaceFormat.Color);
                t2d.SetData(job.StraightTextures ? tex.Straight : tex.Premul);
                textures.Add(t2d);
                EffectParameter? tp = effect.Parameters[name + "Texture"];
                EffectParameter? sp = effect.Parameters[name + "TexelSize"];
                if (tp is null || sp is null) { Failures.Add($"{job.Name}: effect lacks '{name}Texture'/'{name}TexelSize'"); continue; }
                tp.SetValue(t2d);
                sp.SetValue(new Vector2(1f / tex.Width, 1f / tex.Height));
            }

            using var rt = new RenderTarget2D(gd, _width, _height, false, SurfaceFormat.Color, DepthFormat.None);
            gd.SetRenderTarget(rt);
            gd.Clear(_background);
            gd.BlendState = job.NonPremulBlend ? BlendState.NonPremultiplied : BlendState.AlphaBlend;
            gd.DepthStencilState = DepthStencilState.None;
            gd.RasterizerState = RasterizerState.CullNone;
            for (int s = 0; s < 4; s++)
                gd.SamplerStates[s] = SamplerState.LinearClamp;
            foreach (EffectPass pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
            }
            gd.SetRenderTarget(null);

            var px = new byte[_width * _height * 4];
            rt.GetData(px);
            Images[job.Name] = px;
            foreach (Texture2D t2d in textures) t2d.Dispose();
        }
    }
}

readonly struct NdcVertex : IVertexType
{
    private readonly Vector4 _position;
    public NdcVertex(float x, float y) => _position = new Vector4(x, y, 0f, 1f);
    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Position, 0));
    VertexDeclaration IVertexType.VertexDeclaration => Declaration;
}
