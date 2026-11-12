// =============================================================================
// SkSLVsGl: render-fidelity gate for the SkSL INPUT frontend (ShadowDusk.SkSL).
// -----------------------------------------------------------------------------
//     SkSL --[real SkiaSharp SKRuntimeEffect, CPU raster]--------------> reference image
//     SkSL --[ShadowDusk.SkSL]--> .fx --[EffectCompiler, OpenGL]--> .mgfx
//          --[real MonoGame DesktopGL Effect, NDC fullscreen quad]----> candidate image
//
// Skia has no reference compiler for this direction, so the evidence is rendered-image fidelity
// against real Skia (as validation/SkiaVsKni is for the output direction). Same source texels,
// same size, same uniforms; tolerance 2/255.
//
// Positive controls (each MUST diverge or the gate fails):
//   * control-flipped-y     the gradient's Skia SkSL with its Y flipped (origin convention);
//   * control-child-offset  the child-eval case with a different eval offset on Skia.
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

const int Size = 64;
int tolerance = 2;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
        tolerance = t;

string outDir = Path.Combine(ShaderInputs.FindRepoRoot(), "validation", "output-sksl");
Directory.CreateDirectory(outDir);

float[] sizeUniform = { Size, Size };
byte[] source = SourceTexels();

var cases = new List<Case>
{
    new("Gradient",
        "uniform float2 size;\nhalf4 main(float2 c) { half2 uv = half2(c / size); return half4(uv.x, uv.y, 0.25, 1); }",
        new() { ["size"] = sizeUniform }, null),
    new("Math",
        "uniform float2 size; uniform half4 tint;\n" +
        "half4 main(float2 c) { float2 uv = c / size; half3 k = half3(mix(tint.rgb, half3(uv.x, uv.y, 1 - uv.x), 0.5));\n" +
        "  return half4(fract(k * 3.0), 1); }",
        new() { ["size"] = sizeUniform, ["tint"] = new[] { 0.9f, 0.4f, 0.2f, 1f } }, null),
    new("ChildEval",
        "uniform shader img;\n" +
        "half4 main(float2 c) { half4 s = img.eval(c + float2(40, 30)); return half4(1 - s.r, s.g, s.b, 1); }",
        new() { ["imgSize"] = sizeUniform }, "img"),
};

// Skia-only variants for the positive controls (the GL arm keeps the real shader).
var controls = new Dictionary<string, (string Of, string Sksl)>
{
    ["control-flipped-y"] = ("Gradient",
        "uniform float2 size;\nhalf4 main(float2 c) { half2 uv = half2(float2(c.x, size.y - c.y) / size); return half4(uv.x, uv.y, 0.25, 1); }"),
    ["control-child-offset"] = ("ChildEval",
        "uniform shader img;\n" +
        "half4 main(float2 c) { half4 s = img.eval(c + float2(43, 30)); return half4(1 - s.r, s.g, s.b, 1); }"),
};

// ---- Convert + compile every case through the real product pipeline ----------
var mgfx = new Dictionary<string, byte[]>();
foreach (Case c in cases)
{
    ConvertResult conv = SkSLConverter.Convert(c.Sksl);
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
foreach ((string name, (string of, string sksl)) in controls)
    reference[name] = RenderSkia(sksl, cases.First(k => k.Name == of));

// ---- GL arm (candidate) -------------------------------------------------------
using var game = new GlArm(cases, mgfx, source, Size);
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
    Console.WriteLine($"[sksl] {c.Name,-10} maxd {maxd,3}, {over} px over {tolerance}, {Distinct(refImg)} colors -> {(match ? "MATCH" : "DIFF")}");
    ok &= match;
}

foreach ((string name, (string of, _)) in controls)
{
    (int maxd, int over) = Compare(reference[name], game.Images[of], tolerance);
    bool caught = over > 0;
    Console.WriteLine($"[sksl] {name,-20} maxd {maxd,3}, {over} px over {tolerance} -> {(caught ? "caught (diverges, as it must)" : "NOT CAUGHT")}");
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

    var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
    var children = new SKRuntimeEffectChildren(effect);
    using SKImage? image = c.Child is null ? null : SKImage.FromPixelCopy(info, source);
    using SKShader? childShader = image?.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    if (c.Child is not null)
        children[c.Child] = childShader;

    using SKShader shader = effect.ToShader(uniforms, children);
    using var bitmap = new SKBitmap(info);
    bitmap.Erase(SKColors.Transparent);
    using (var canvas = new SKCanvas(bitmap))
    using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false })
        canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);
    return bitmap.Bytes;
}

static byte[] SourceTexels()
{
    var px = new byte[Size * Size * 4];
    for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int i = (y * Size + x) * 4;
            px[i] = (byte)(x * 4);
            px[i + 1] = (byte)(y * 4);
            px[i + 2] = (byte)((x * 5 + y * 3) & 255);
            px[i + 3] = 255;
        }
    return px;
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
    var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using SKImage image = SKImage.FromPixelCopy(info, rgba);
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream fs = File.Create(path);
    data.SaveTo(fs);
}

internal sealed record Case(string Name, string Sksl, Dictionary<string, float[]> Uniforms, string? Child);

internal sealed class GlArm : Game
{
    private readonly IReadOnlyList<Case> _cases;
    private readonly IReadOnlyDictionary<string, byte[]> _mgfx;
    private readonly byte[] _source;
    private readonly int _size;
    private bool _done;

    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Failures { get; } = new();
    public Dictionary<string, byte[]> Images { get; } = new();

    public GlArm(IReadOnlyList<Case> cases, IReadOnlyDictionary<string, byte[]> mgfx, byte[] source, int size)
    {
        _cases = cases; _mgfx = mgfx; _source = source; _size = size;
        _ = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = size,
            PreferredBackBufferHeight = size,
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
        using var tex = new Texture2D(gd, _size, _size, false, SurfaceFormat.Color);
        tex.SetData(_source);

        var quad = new[]
        {
            new NdcVertex(-1f, -1f), new NdcVertex(-1f, 1f), new NdcVertex(1f, -1f),
            new NdcVertex( 1f, -1f), new NdcVertex(-1f, 1f), new NdcVertex(1f,  1f),
        };

        foreach (Case c in _cases)
        {
            using var effect = new Effect(gd, _mgfx[c.Name]);
            effect.Parameters["iResolution"]?.SetValue(new Vector3(_size, _size, 1f));
            foreach ((string name, float[] v) in c.Uniforms)
            {
                EffectParameter? p = effect.Parameters[name];
                if (p is null) { Failures.Add($"{c.Name}: effect has no parameter '{name}'"); continue; }
                switch (v.Length)
                {
                    case 1: p.SetValue(v[0]); break;
                    case 2: p.SetValue(new Vector2(v[0], v[1])); break;
                    case 3: p.SetValue(new Vector3(v[0], v[1], v[2])); break;
                    default: p.SetValue(new Vector4(v[0], v[1], v[2], v[3])); break;
                }
            }
            if (c.Child is not null)
            {
                EffectParameter? tp = effect.Parameters[c.Child + "Texture"];
                if (tp is null) Failures.Add($"{c.Name}: effect has no parameter '{c.Child}Texture'");
                else tp.SetValue(tex);
            }

            using var rt = new RenderTarget2D(gd, _size, _size, false, SurfaceFormat.Color, DepthFormat.None);
            gd.SetRenderTarget(rt);
            gd.Clear(Color.Transparent);
            gd.BlendState = BlendState.Opaque;
            gd.DepthStencilState = DepthStencilState.None;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.LinearClamp;
            foreach (EffectPass pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2);
            }
            gd.SetRenderTarget(null);

            var px = new byte[_size * _size * 4];
            rt.GetData(px);
            Images[c.Name] = px;
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
