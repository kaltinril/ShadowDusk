#nullable enable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.SkiaVsKni;

/// <summary>
/// The reference arm: ShadowDusk's OpenGL <c>.mgfx</c> (rung-4 proven on real KNI desktop by
/// validation/KniDesktopGL) loaded into a real KNI v4.2.9001 <see cref="Effect"/> on SDL2.GL and
/// drawn through <see cref="SpriteBatch"/> the way Gum's KNI renderer draws a container: the
/// container texture on unit 0, the tint as SpriteBatch's vertex color, 1:1 at the texture's size.
/// </summary>
internal sealed class KniArm : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<KniJob> _jobs;
    private readonly IReadOnlyDictionary<string, byte[]> _textures;
    private bool _done;

    public bool Skipped { get; private set; }
    public string? SkipReason { get; private set; }
    public List<string> Failures { get; } = new();
    public Dictionary<string, byte[]> Images { get; } = new(StringComparer.Ordinal);

    public KniArm(IReadOnlyList<KniJob> jobs, IReadOnlyDictionary<string, byte[]> textures)
    {
        _jobs = jobs;
        _textures = textures;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = SkiaVsKni.Images.Size,
            PreferredBackBufferHeight = SkiaVsKni.Images.Size,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Skia vs KNI: KNI arm (headless)";
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
        try { RenderAll(); }
        catch (Exception ex) { Failures.Add($"threw: {ex}"); }
        Exit();
    }

    private void RenderAll()
    {
        GraphicsDevice gd = GraphicsDevice;
        int size = SkiaVsKni.Images.Size;
        using var sb = new SpriteBatch(gd);
        var dest = new Rectangle(0, 0, size, size);

        var uploaded = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        foreach ((string name, byte[] rgba) in _textures)
        {
            var tex = new Texture2D(gd, size, size, false, SurfaceFormat.Color);
            tex.SetData(rgba);
            uploaded[name] = tex;
        }
        Texture2D container = uploaded["SpriteTexture"];

        try
        {
            foreach (KniJob job in _jobs)
            {
                using var effect = new Effect(gd, job.Mgfx);
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
                // Every texture other than the container is bound by parameter (Mask's MaskTexture).
                foreach ((string name, Texture2D tex) in uploaded)
                {
                    if (name != "SpriteTexture")
                        effect.Parameters[name]?.SetValue(tex);
                }

                using var rt = new RenderTarget2D(gd, size, size, false, SurfaceFormat.Color, DepthFormat.None);
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);

                // Prime SpriteBatch's own vertex shader, then Immediate so the pixel-only effect's
                // pass is applied before the draw (validation/Shared/EffectImageRenderer's recipe).
                sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                sb.Draw(container, dest, Color.White);
                sb.End();
                gd.Clear(Color.Transparent);

                var tint = new Color(job.Tint[0], job.Tint[1], job.Tint[2], job.Tint[3]);
                sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
                // Unit 1 (Mask's second texture) gets the same sampler as unit 0.
                gd.SamplerStates[1] = SamplerState.LinearClamp;
                sb.Draw(container, dest, tint);
                sb.End();
                gd.SetRenderTarget(null);

                var px = new byte[size * size * 4];
                rt.GetData(px);
                Images[job.Name] = px;
            }
        }
        finally
        {
            foreach (Texture2D tex in uploaded.Values)
                tex.Dispose();
        }
    }
}
