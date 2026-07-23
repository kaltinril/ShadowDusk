#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.VsDriven;

/// <summary>
/// Renders <c>apos-shapes.fx</c> (the Phase 49 pin, upstream commit <c>3fb73b8d</c>) on a real
/// MonoGame.Framework.DesktopGL device — the GL slice of Phase 51 A3 (Apos.Shapes render-proof).
///
/// <para><b>Why this fixture, not <c>apos-shapes-sm6.fx</c> (the DX/Vulkan fixture).</b> DX
/// (<c>validation/VsDrivenDx -- apos</c>) and Vulkan (<c>validation/VsDrivenVulkan -- apos</c>)
/// both render <c>apos-shapes-sm6.fx</c> at maxd 0. GL was tried against the SAME fixture first,
/// and it compiles fine (OpenGL's macro set is SM4/SM6-free, so it takes the fixture's
/// <c>#elif OPENGL</c> branch), but the render diverged completely (maxd 255, solid black) —
/// and NOT because of anything ShadowDusk emits. Reverse-engineering the real
/// <c>mgfxc /Profile:OpenGL</c> golden's embedded GLSL (MojoShader's ps_3_0 translation) found
/// the final pixel-shader output is a ternary,
/// <c>ps_oC0 = ((-ps_r0.w >= 0.0) ? ps_r1 : ps_r4)</c>, where <c>ps_r4</c> is hard-zeroed
/// (<c>ps_c10.xxxx</c>, a literal 0.0) for every shape outside the two texture/font branches —
/// which is every shape this render-proof exercises, since <c>ps_r0.w</c> is set from the SAME
/// hardcoded <c>0.0</c> constant in that case. That makes the branch condition
/// <c>-0.0 &gt;= 0.0</c> — a value that IEEE-754 defines as true (-0.0 == 0.0), but MojoShader's
/// GLSL translation of this specific fxc-optimized ps_3_0 comparison evaluates false on this
/// GPU/driver, permanently selecting the hard-zeroed branch and rendering black regardless of
/// fill/border/meta input. An independent double-precision recomputation of the shader's OkLab
/// math (bypassing the shader entirely) confirmed ShadowDusk's <c>apos-shapes-sm6.fx</c> GL
/// candidate is the mathematically correct one — this is a genuine bug in mgfxc's own GL/
/// MojoShader compile of that specific fxc-optimized shader revision, not a ShadowDusk defect
/// (the same class of "the reference compiler's own output is wrong" situation the Vulkan
/// SlotOffset bug already is — see docs/validation-matrix.md).
///
/// <c>apos-shapes.fx</c> is upstream's OLDER, hand-written (not fxc-SM3-optimizer-mangled)
/// revision: no <c>Pack11</c>/<c>DecodeDigit</c> negative-zero branch trick, a plain
/// sequential <c>if/else if</c> shape dispatch, and Cantor-pair (<c>Unpair</c>) color packing
/// instead. It renders correctly on the real mgfxc GL golden, confirming the sm6 divergence is
/// specific to that revision's fxc-optimized codegen, not a structural GL gap.</para>
///
/// <para><b>Why a bespoke renderer.</b> Like the sm6 fixture, this is not a SpriteBatch effect:
/// its vertex input is <c>POSITION0, TEXCOORD0-8</c> (10 elements, no <c>POSITION1</c>/
/// <c>NORMAL0</c> — this revision predates the sm6 fixture's clip-distance split), and Fill/
/// Border each pack TWO colors (fill1/fill2, border1/border2) via a Cantor pairing function
/// (<c>Unpair</c>), not the sm6 fixture's base-2048 quantization (<c>Pack11</c>). The vertex
/// data below Cantor-pairs each color's (R,G) and (B,A) byte pairs to match.</para>
/// </summary>
public sealed class AposShapesRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _outDir;
    private readonly IReadOnlyList<(string Name, byte[]? Bytes, string? Error)> _jobs;

    private Texture2D _white = null!;
    private bool _done;

    public List<(string Name, bool Loaded, bool Rendered, string? Error)> Outcomes { get; } = new();
    public List<(string Name, Color[] Pixels, int Width, int Height)> Captures { get; } = new();

    private const int Size = 128;

    public AposShapesRenderer(string outDir, IReadOnlyList<(string Name, byte[]? Bytes, string? Error)> jobs)
    {
        _outDir = outDir;
        _jobs = jobs;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Size,
            PreferredBackBufferHeight = Size,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Apos.Shapes GL validation (headless)";
    }

    protected override void LoadContent()
    {
        // A 1x1 opaque white texture is enough: this draw never takes the shader's
        // texture/font shape branches, but every sampler slot must still be bound.
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }

        GraphicsDevice.Clear(Color.Black);
        foreach (var job in _jobs)
            RenderOne(job);

        _done = true;
        Exit();
    }

    /// <summary>The upstream <c>VertexInput</c> struct, field for field, in declaration order.</summary>
    private readonly struct AposVertex : IVertexType
    {
        public readonly Vector4 Position;    // POSITION0
        public readonly Vector4 TexCoord;    // TEXCOORD0  xy: local pos, z: rounding, w: packed meta
        public readonly Vector4 Fill;        // TEXCOORD1  x/y: Cantor-paired fill1 (R,G)/(B,A); z/w: fill2
        public readonly Vector4 Border;      // TEXCOORD2  same packing, border1/border2
        public readonly Vector4 FillCoord;   // TEXCOORD3
        public readonly Vector4 BorderCoord; // TEXCOORD4
        public readonly Vector4 Meta1;       // TEXCOORD5  x: lineSize, y: aaSize, z: sdfSize
        public readonly Vector4 Meta2;       // TEXCOORD6
        public readonly Vector4 Meta3;       // TEXCOORD7
        public readonly Vector4 ClipRect;    // TEXCOORD8  xy read by BoxSDF; kept well inside (1,1) so nothing clips

        public AposVertex(Vector4 position, Vector4 texCoord, Vector4 fill, Vector4 border,
                          Vector4 fillCoord, Vector4 borderCoord,
                          Vector4 meta1, Vector4 meta2, Vector4 meta3, Vector4 clipRect)
        {
            Position = position; TexCoord = texCoord; Fill = fill; Border = border;
            FillCoord = fillCoord; BorderCoord = borderCoord;
            Meta1 = meta1; Meta2 = meta2; Meta3 = meta3; ClipRect = clipRect;
        }

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(  0, VertexElementFormat.Vector4, VertexElementUsage.Position,          0),
            new VertexElement( 16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement( 32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 1),
            new VertexElement( 48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement( 64, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement( 80, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement( 96, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 5),
            new VertexElement(112, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 6),
            new VertexElement(128, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 7),
            new VertexElement(144, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 8));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    /// <summary>
    /// Szudzik's elegant pairing function — the exact inverse of the shader's <c>Unpair</c>:
    /// <c>Unpair(n)</c> returns <c>(f2,f1)</c> when <c>f2&lt;f1</c> (i.e. n = b*b+a for a&lt;b)
    /// and <c>(f1,f2-f1)</c> otherwise (n = a*a+a+b for a&gt;=b). Both a and b must be
    /// non-negative integers (here, 0-255 color byte values).
    /// </summary>
    private static float Pair(int a, int b) => a < b ? (float)(b * b + a) : (float)(a * a + a + b);

    /// <summary>Cantor-pairs one RGBA color into the shader's packed (R,G)/(B,A) TEXCOORD form.</summary>
    private static Vector4 PackColor(Vector4 rgba)
    {
        int r = (int)MathF.Round(rgba.X * 255f);
        int g = (int)MathF.Round(rgba.Y * 255f);
        int b = (int)MathF.Round(rgba.Z * 255f);
        int a = (int)MathF.Round(rgba.W * 255f);
        return new Vector4(Pair(r, g), Pair(b, a), 0f, 0f);
    }

    private static AposVertex[] BuildCircleQuad()
    {
        // meta = 0 selects the plain (undashed, ungradiented) CIRCLE branch: Unpair(0) = (0, 0),
        // so shape 0, gradientStyles/fillStyles/borderStyles all 0.
        const float Meta     = 0f;
        const float Rounded  = 0f;
        const float LineSize = 0.10f;   // border band width, world units
        const float AaSize   = 1.0f;    // AA footprint size
        const float SdfSize  = 0.70f;   // circle radius, world units

        var fill   = new Vector4(1.00f, 0.25f, 0.50f, 1f); // distinctive so a blank frame is obvious
        var border = new Vector4(0.00f, 0.40f, 1.00f, 1f);

        // fill1 == fill2 and border1 == border2 (solid, no gradient): pack the SAME color into
        // both halves of the TEXCOORD.
        var fillPacked = PackColor(fill);
        fillPacked.Z = fillPacked.X;
        fillPacked.W = fillPacked.Y;
        var borderPacked = PackColor(border);
        borderPacked.Z = borderPacked.X;
        borderPacked.W = borderPacked.Y;

        // ClipRect.xy well inside (1,1): BoxSDF((0,0),(1,1)) = -1 <= 0, so clipD never discards.
        var clipRect = Vector4.Zero;

        var coord = new Vector4(0f, 0f, 1f, 1f); // gradient endpoints (unused at style 0)
        var zero  = Vector4.Zero;

        AposVertex Corner(float x, float y) => new(
            position:    new Vector4(x, y, 0f, 1f),
            texCoord:    new Vector4(x, y, Rounded, Meta),
            fill:        fillPacked, border: borderPacked,
            fillCoord:   coord, borderCoord: coord,
            meta1:       new Vector4(LineSize, AaSize, SdfSize, 0f),
            meta2:       zero, meta3: zero,
            clipRect:    clipRect);

        return new[] { Corner(-1f, 1f), Corner(1f, 1f), Corner(-1f, -1f), Corner(1f, -1f) };
    }

    private void RenderOne((string Name, byte[]? Bytes, string? Error) job)
    {
        if (job.Bytes is null)
        {
            Outcomes.Add((job.Name, false, false, $"compile failed: {job.Error}"));
            return;
        }

        Effect effect;
        try { effect = new Effect(GraphicsDevice, job.Bytes); }
        catch (Exception ex)
        {
            Outcomes.Add((job.Name, false, false, $"new Effect() threw: {ex.Message}"));
            return;
        }

        using var rt = new RenderTarget2D(GraphicsDevice, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);
            GraphicsDevice.BlendState        = BlendState.NonPremultiplied;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState   = RasterizerState.CullNone;
            GraphicsDevice.SamplerStates[0]  = SamplerState.LinearClamp;
            GraphicsDevice.SamplerStates[1]  = SamplerState.LinearClamp;

            // NON-IDENTITY, ASYMMETRIC transform — the issue-#70 input discipline, matching the
            // DX/Vulkan gates exactly: exact-dyadic values (0.5 scale, 0.25 translate) so both
            // compilers compute bit-identical positions and a correct result is maxd 0.
            effect.Parameters["view_projection"]?.SetValue(new Matrix(
                0.5f,  0f,    0f, 0f,
                0f,    0.5f,  0f, 0f,
                0f,    0f,    1f, 0f,
                0.25f, 0.25f, 0f, 1f));

            // Every sampler slot must be bound even though this draw never takes the
            // texture/font shape branches.
            effect.Parameters["TextureSampler"]?.SetValue(_white);
            effect.Parameters["FontSampler"]?.SetValue(_white);

            var verts   = BuildCircleQuad();
            var indices = new short[] { 0, 1, 2, 2, 1, 3 };

            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                GraphicsDevice.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList, verts, 0, verts.Length,
                    indices, 0, 2, AposVertex.Declaration);
            }

            var pixels = new Color[Size * Size];
            GraphicsDevice.SetRenderTarget(null);
            rt.GetData(pixels);
            Captures.Add((job.Name, pixels, Size, Size));

            string png = Path.Combine(_outDir, job.Name + ".png");
            using (var fs = File.Create(png))
                rt.SaveAsPng(fs, Size, Size);

            Outcomes.Add((job.Name, true, true, null));
        }
        catch (Exception ex)
        {
            GraphicsDevice.SetRenderTarget(null);
            Outcomes.Add((job.Name, true, false, $"render threw: {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            effect.Dispose();
        }
    }
}
