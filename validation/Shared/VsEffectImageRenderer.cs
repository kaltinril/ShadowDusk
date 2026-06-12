#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation;

/// <summary>
/// Rung-4 renderer for a VS-DRIVEN effect (Phase 28). SpriteBatch supplies its own
/// vertex shader, so it cannot exercise a <c>.mgfx</c> that ships its own VS. This
/// renderer instead draws a textured quad through a CUSTOM vertex buffer
/// (POSITION / COLOR0 / TEXCOORD0) so the candidate effect's vertex shader actually
/// runs: it sets the effect's <c>WorldViewProjection</c> + <c>Tint</c> uniforms BY
/// NAME, binds the cat texture, and draws two triangles with
/// <see cref="GraphicsDevice.DrawUserPrimitives{T}"/>.
///
/// The renderer is byte-identical for the baseline (mgfxc golden) and candidate
/// (ShadowDusk) runs — only the <c>.mgfx</c> bytes differ — so any pixel difference
/// is attributable solely to the compiler that produced them.
/// </summary>
public sealed class VsEffectImageRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<ShaderJob> _jobs;
    private readonly bool _renderToBackbuffer;

    private Texture2D _cat = null!;
    private bool _done;

    public List<ShaderOutcome> Outcomes { get; } = new();

    /// <summary>
    /// Raw pixels of each successful render (same order as <see cref="Outcomes"/>),
    /// so callers can compare baseline vs candidate in-process without a PNG decoder.
    /// </summary>
    public List<(string Name, Color[] Pixels, int Width, int Height)> Captures { get; } = new();

    /// <param name="renderToBackbuffer">
    /// Phase 43 F3: when <c>true</c>, draws to the BACKBUFFER (no
    /// <c>RenderTarget2D</c>) and reads it back via
    /// <see cref="GraphicsDevice.GetBackBufferData{T}(T[])"/>. This is the case the
    /// static-Y-flip bug hid in — MonoGame sets <c>posFixup.y = +1</c> here (vs
    /// <c>-1</c> with a render target bound), so only an effect carrying the dynamic
    /// posFixup contract renders upright.
    /// </param>
    public VsEffectImageRenderer(
        string catPath, string outDir, IReadOnlyList<ShaderJob> jobs,
        bool renderToBackbuffer = false)
    {
        _catPath = catPath;
        _outDir = outDir;
        _jobs = jobs;
        _renderToBackbuffer = renderToBackbuffer;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk VS-driven validation (headless)";
    }

    protected override void LoadContent()
    {
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }

        GraphicsDevice.Clear(Color.Black);
        foreach (var job in _jobs)
            Outcomes.Add(RenderOne(job, _cat.Width, _cat.Height));

        _done = true;
        Exit();
    }

    // Vertex with POSITION / COLOR0 / TEXCOORD0 — the SpriteBatch-compatible set the
    // fixture's VS consumes (matches the .mgfx attribute table vs_v0/vs_v1/vs_v2).
    private readonly struct VsVertex : IVertexType
    {
        public readonly Vector3 Position;
        public readonly Color Color;
        public readonly Vector2 TexCoord;

        public VsVertex(Vector3 position, Color color, Vector2 texCoord)
        {
            Position = position; Color = color; TexCoord = texCoord;
        }

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Color, VertexElementUsage.Color, 0),
            new VertexElement(16, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private ShaderOutcome RenderOne(ShaderJob job, int w, int h)
    {
        if (job.Bytes is null)
            return new ShaderOutcome(job.Name, false, false,
                $"compile failed: {job.CompileError}", null);

        Effect effect;
        try { effect = new Effect(GraphicsDevice, job.Bytes); }
        catch (Exception ex)
        {
            return new ShaderOutcome(job.Name, false, false,
                $"new Effect() threw: {ex.Message}", null);
        }

        // Backbuffer mode draws straight to the swap chain (posFixup.y = +1 in real
        // MonoGame); render-target mode is the original Phase 28 path (y = -1).
        int outW = _renderToBackbuffer ? GraphicsDevice.PresentationParameters.BackBufferWidth  : w;
        int outH = _renderToBackbuffer ? GraphicsDevice.PresentationParameters.BackBufferHeight : h;

        using var rt = _renderToBackbuffer
            ? null
            : new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

            // A full-screen quad in clip space. The VS multiplies POSITION by
            // WorldViewProjection; an identity transform maps these clip-space corners
            // straight to the viewport so the whole cat fills the target. A slight
            // off-identity scale would also exercise the matrix, but identity keeps the
            // baseline/candidate comparison about the SHADER, not floating-point edges.
            // Vertices are wound so the two triangles cover the quad with CullNone.
            // TexCoord (0,0) top-left .. (1,1) bottom-right.
            var verts = new[]
            {
                new VsVertex(new Vector3(-1f,  1f, 0f), Color.White, new Vector2(0f, 0f)), // TL
                new VsVertex(new Vector3( 1f,  1f, 0f), Color.White, new Vector2(1f, 0f)), // TR
                new VsVertex(new Vector3(-1f, -1f, 0f), Color.White, new Vector2(0f, 1f)), // BL
                new VsVertex(new Vector3( 1f, -1f, 0f), Color.White, new Vector2(1f, 1f)), // BR
            };
            var indices = new short[] { 0, 1, 2, 2, 1, 3 };

            // Identity transform + opaque-white tint -> output == the sampled texture,
            // so baseline and candidate must produce the same image as a plain blit.
            effect.Parameters["WorldViewProjection"]?.SetValue(Matrix.Identity);
            effect.Parameters["Tint"]?.SetValue(new Vector4(1f, 1f, 1f, 1f));
            effect.Parameters["SpriteTexture"]?.SetValue(_cat);

            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                GraphicsDevice.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    verts, 0, verts.Length,
                    indices, 0, 2,
                    VsVertex.Declaration);
            }

            var pixels = new Color[outW * outH];
            if (_renderToBackbuffer)
            {
                // Real-runtime backbuffer readback (GraphicsDevice.GetBackBufferData,
                // implemented on DesktopGL via glReadPixels + row flip).
                GraphicsDevice.GetBackBufferData(pixels);
            }
            else
            {
                GraphicsDevice.SetRenderTarget(null);
                rt!.GetData(pixels);
            }
            Captures.Add((job.Name, pixels, outW, outH));

            string png = Path.Combine(_outDir, job.Name + ".png");
            using (var snapshot = new Texture2D(GraphicsDevice, outW, outH))
            {
                snapshot.SetData(pixels);
                using var outFs = File.Create(png);
                snapshot.SaveAsPng(outFs, outW, outH);
            }
            if (_renderToBackbuffer)
                GraphicsDevice.SetRenderTarget(null);

            return new ShaderOutcome(job.Name, true, true, null, png);
        }
        catch (Exception ex)
        {
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            string frames = string.Join(" <- ",
                (ex.StackTrace ?? "").Split('\n').Take(4).Select(f => f.Trim().Replace("at ", "")));
            return new ShaderOutcome(job.Name, true, false,
                $"render threw: {ex.GetType().Name}: {ex.Message} @ {frames}", null);
        }
        finally
        {
            effect.Dispose();
        }
    }
}
