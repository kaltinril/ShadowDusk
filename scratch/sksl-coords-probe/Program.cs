// Coordinate probe: what `main`'s coordinate is for a 40x30 rect at (17,11) in a 96x64 target.
// R = 2*c.x, G = 2*c.y (exact for half-pixel centers up to 127.5).
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.SkSL;
using ShadowDusk.ShaderToy;
using SkiaSharp;

const int W = 96, H = 64, RX = 17, RY = 11, RW = 40, RH = 30;
string sksl = "half4 main(float2 c) { return half4(half(c.x * 2.0 / 255.0), half(c.y * 2.0 / 255.0), 0, 1); }";
string outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);
Console.WriteLine($"SkiaSharp assembly {typeof(SKCanvas).Assembly.GetName().Version}, native {SkiaSharpVersion.Native}");

var images = new Dictionary<string, byte[]>();

// Skia, Gum's way (Runtimes/SkiaGum/RenderingLibrary/Renderer.cs DrawRenderTargetImage):
// effect.ToShader with no local matrix, canvas translated to the rect's corner, DrawRect(0,0,w,h).
images["skia-gum"] = Skia((canvas, paint) => { canvas.Translate(RX, RY); canvas.DrawRect(new SKRect(0, 0, RW, RH), paint); });
// Skia, rect drawn in place with no translate (device-space coordinate, for reference).
images["skia-inplace"] = Skia((canvas, paint) => canvas.DrawRect(new SKRect(RX, RY, RX + RW, RY + RH), paint));

if (args.Contains("--skia-only")) { Report(); return 0; }

ConvertResult conv = SkSLToFxConverter.Convert(sksl);
if (!conv.Success) { Console.Error.WriteLine(string.Join("|", conv.Diagnostics.Select(d => d.Message))); return 2; }
File.WriteAllText(Path.Combine(outDir, "converted.fx"), conv.Fx);
var compiled = await new EffectCompiler().CompileAsync(conv.Fx!, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "probe.fx" });
if (compiled.IsFailure) { Console.Error.WriteLine(string.Join("|", compiled.Error.Select(e => e.Code + " " + e.Message))); return 2; }

foreach (var tgt in new[]{PlatformTarget.DirectX_11}) { var r = await new EffectCompiler().CompileAsync(conv.Fx!, new CompilerOptions { Target = tgt, SourceFileName = "probe.fx" }); Console.WriteLine(tgt + ": " + (r.IsFailure ? string.Join("|", r.Error.Select(e => e.Code + " " + e.Message)) : "ok " + r.Value.Data.Length + " bytes")); }
using (var game = new Arm(compiled.Value.Data, images)) game.Run();
Report();
return 0;

void Report()
{
    (string, int, int)[] corners = { ("TL", RX, RY), ("TR", RX + RW - 1, RY), ("BL", RX, RY + RH - 1), ("BR", RX + RW - 1, RY + RH - 1) };
    foreach ((string name, byte[] px) in images)
    {
        WritePng(Path.Combine(outDir, name + ".png"), px);
        bool Bg(int i) => px[i] == 30 && px[i + 1] == 60 && px[i + 2] == 90;
        var parts = corners.Select(k =>
        {
            int i = (k.Item3 * W + k.Item2) * 4;
            return Bg(i) ? $"{k.Item1}=bg" : $"{k.Item1}=({px[i] / 2.0},{px[i + 1] / 2.0})";
        });
        int minX = W, minY = H, maxX = -1, maxY = -1;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (!Bg((y * W + x) * 4)) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        string box = maxX < 0 ? "nothing drawn" : $"drawn x[{minX}..{maxX}] y[{minY}..{maxY}]";
        Console.WriteLine($"{name,-30} {string.Join("  ", parts)}  | {box}");
    }
}

byte[] Skia(Action<SKCanvas, SKPaint> draw)
{
    using var effect = SKRuntimeEffect.CreateShader(sksl, out string errors) ?? throw new Exception(errors);
    using var shader = effect.ToShader(new SKRuntimeEffectUniforms(effect), new SKRuntimeEffectChildren(effect));
    using var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
    bitmap.Erase(new SKColor(30, 60, 90, 255));
    using (var canvas = new SKCanvas(bitmap))
    using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
        draw(canvas, paint);
    return bitmap.Bytes;
}

void WritePng(string path, byte[] rgba)
{
    using SKImage image = SKImage.FromPixelCopy(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul), rgba);
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream fs = File.Create(path);
    data.SaveTo(fs);
}

sealed class Arm : Game
{
    const int W = 96, H = 64, RX = 17, RY = 11, RW = 40, RH = 30;
    readonly byte[] _mgfx; readonly Dictionary<string, byte[]> _images; bool _done;
    public Arm(byte[] mgfx, Dictionary<string, byte[]> images)
    {
        _mgfx = mgfx; _images = images;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = W, PreferredBackBufferHeight = H, GraphicsProfile = GraphicsProfile.HiDef };
    }
    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        _done = true;
        var gd = GraphicsDevice;
        using var effect = new Effect(gd, _mgfx);
        using var tex = new Texture2D(gd, RW, RH);
        tex.SetData(Enumerable.Repeat(Color.White, RW * RH).ToArray());
        EffectParameter? res = effect.Parameters["iResolution"];

        // 1. Gum's MonoGame way (RenderingLibrary/Graphics/Renderer.cs BlitRenderTarget): SpriteBatch.Begin with
        //    the effect, Draw the texture at the container's rect.
        foreach ((string tag, Vector3 r) in new[] { ("target", new Vector3(W, H, 1)), ("sprite", new Vector3(RW, RH, 1)) })
        {
            res?.SetValue(r);
            _images["mg-spritebatch-res-" + tag] = Render(() =>
            {
                using var sb = new SpriteBatch(gd);
                sb.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
                sb.Draw(tex, new Rectangle(RX, RY, RW, RH), Color.White);
                sb.End();
            });
        }

        // 2. The converted shader's own contract: its VS takes NDC POSITION. A quad covering only the sub-rect.
        if (args0Ndc)
        {
            float L = RX * 2f / W - 1, R = (RX + RW) * 2f / W - 1, T = 1 - RY * 2f / H, B = 1 - (RY + RH) * 2f / H;
            var quad = new[] { new V(L, B), new V(L, T), new V(R, B), new V(R, B), new V(L, T), new V(R, T) };
            foreach ((string tag, Vector3 r) in new[] { ("target", new Vector3(W, H, 1)), ("sprite", new Vector3(RW, RH, 1)) })
            {
                res?.SetValue(r);
                _images["mg-ndcquad-res-" + tag] = Render(() =>
                {
                    gd.BlendState = BlendState.AlphaBlend; gd.DepthStencilState = DepthStencilState.None; gd.RasterizerState = RasterizerState.CullNone;
                    foreach (var pass in effect.CurrentTechnique.Passes) { pass.Apply(); gd.DrawUserPrimitives(PrimitiveType.TriangleList, quad, 0, 2); }
                });
            }
        }
        Exit();
    }
    public static bool args0Ndc = !Environment.GetCommandLineArgs().Contains("--no-ndc");
    byte[] Render(Action draw)
    {
        var gd = GraphicsDevice;
        using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(30, 60, 90, 255));
        draw();
        gd.SetRenderTarget(null);
        var px = new byte[W * H * 4];
        rt.GetData(px);
        return px;
    }
}

readonly struct V : IVertexType
{
    readonly Vector4 _p;
    public V(float x, float y) => _p = new Vector4(x, y, 0, 1);
    public static readonly VertexDeclaration Decl = new(new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Position, 0));
    VertexDeclaration IVertexType.VertexDeclaration => Decl;
}
