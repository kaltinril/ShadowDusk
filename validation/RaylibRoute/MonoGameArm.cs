#nullable enable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.RaylibRoute;

/// <summary>
/// The reference arm: ShadowDusk's OpenGL <c>.mgfx</c> (rung-4 proven against mgfxc) loaded into
/// a real MonoGame DesktopGL <see cref="Effect"/> and drawn through <see cref="SpriteBatch"/>, the
/// way a MonoGame game applies a post-process. It also produces the shared source image, so the
/// raylib arm samples byte-identical texels instead of decoding the JPEG itself.
/// </summary>
internal sealed class MonoGameArm : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly JobFile _jobs;
    private bool _done;

    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Failures { get; } = new();

    public MonoGameArm(string catPath, string outDir, JobFile jobs)
    {
        _catPath = catPath;
        _outDir = outDir;
        _jobs = jobs;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = jobs.Size,
            PreferredBackBufferHeight = jobs.Size,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk raylib route: MonoGame arm (headless)";
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
        try { Run(); }
        catch (Exception ex) { Failures.Add($"MonoGame arm threw: {ex}"); }
        Exit();
    }

    private void Run()
    {
        GraphicsDevice gd = GraphicsDevice;
        int size = _jobs.Size;
        var sb = new SpriteBatch(gd);
        var dest = new Rectangle(0, 0, size, size);

        // The shared source: the cat scaled to the scene size once, here, so both arms start
        // from identical texels.
        Texture2D source, extra;
        using (var fs = File.OpenRead(_catPath))
        using (var cat = Texture2D.FromStream(gd, fs))
        using (var scaled = new RenderTarget2D(gd, size, size, false, SurfaceFormat.Color, DepthFormat.None))
        {
            gd.SetRenderTarget(scaled);
            gd.Clear(Color.Transparent);
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            sb.Draw(cat, dest, Color.White);
            sb.End();
            gd.SetRenderTarget(null);

            var texels = new Color[size * size];
            scaled.GetData(texels);
            source = new Texture2D(gd, size, size, false, SurfaceFormat.Color);
            source.SetData(texels);
            extra = new Texture2D(gd, size, size, false, SurfaceFormat.Color);
            extra.SetData(texels);
            File.WriteAllBytes(JobIo.SourcePath(_outDir), ToBytes(texels));
        }

        var tint = new Color(_jobs.Tint[0], _jobs.Tint[1], _jobs.Tint[2], _jobs.Tint[3]);

        foreach (RenderJob job in _jobs.Jobs)
        {
            if (job.MgfxPath is null)
                continue;

            using var effect = new Effect(gd, File.ReadAllBytes(job.MgfxPath));
            foreach ((string name, float[] v) in job.Uniforms)
            {
                EffectParameter? p = effect.Parameters[name];
                if (p is null)
                    continue;
                switch (v.Length)
                {
                    case 1: p.SetValue(v[0]); break;
                    case 2: p.SetValue(new Vector2(v[0], v[1])); break;
                    case 3: p.SetValue(new Vector3(v[0], v[1], v[2])); break;
                    case 4: p.SetValue(new Vector4(v[0], v[1], v[2], v[3])); break;
                    default: throw new InvalidDataException($"{job.Name}: uniform {name} has {v.Length} floats");
                }
            }
            foreach (string texture in job.DrawTextures)
                effect.Parameters[texture]?.SetValue(source);
            foreach (ExtraTexture texture in job.ExtraTextures)
                effect.Parameters[texture.HlslTexture]?.SetValue(extra);

            using var rt = new RenderTarget2D(gd, size, size, false, SurfaceFormat.Color, DepthFormat.None);
            gd.SetRenderTarget(rt);
            gd.Clear(Color.Transparent);

            // Same shape as validation/Shared/EffectImageRenderer: prime SpriteBatch's own vertex
            // shader, then Immediate so the pixel-only effect's pass is applied before the draw.
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            sb.Draw(source, dest, Color.White);
            sb.End();
            gd.Clear(Color.Transparent);
            sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
            sb.Draw(source, dest, tint);
            sb.End();
            gd.SetRenderTarget(null);

            var px = new Color[size * size];
            rt.GetData(px);
            File.WriteAllBytes(JobIo.ImagePath(_outDir, "monogame", job.Name), ToBytes(px));
            using (var png = File.Create(Path.ChangeExtension(JobIo.ImagePath(_outDir, "monogame", job.Name), ".png")))
                rt.SaveAsPng(png, size, size);
        }
        source.Dispose();
        extra.Dispose();
        sb.Dispose();
    }

    private static byte[] ToBytes(Color[] px)
    {
        var bytes = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            bytes[i * 4] = px[i].R;
            bytes[i * 4 + 1] = px[i].G;
            bytes[i * 4 + 2] = px[i].B;
            bytes[i * 4 + 3] = px[i].A;
        }
        return bytes;
    }
}
