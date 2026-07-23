#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Dx12.VsDriven;

/// <summary>
/// Renders <c>apos-shapes-sm6.fx</c> on a real MonoGame WindowsDX12 device — the DirectX12
/// analogue of <c>ShadowDusk.Validation.Dx.VsDriven.AposShapesRenderer</c> (DX11) and
/// <c>ShadowDusk.Validation.VsDrivenVulkan.AposShapesRenderer</c> (Phase 54, mirroring Phase 51
/// A3's DX slice and issue #145's Vulkan gate).
///
/// <para><b>Same fixture, SAME branch as Vulkan.</b> DirectX12's macro set is <c>{MGFX, HLSL,
/// SM6}</c> (matching Vulkan's <c>SM6</c>, no <c>VULKAN</c>-only macro), so the fixture takes
/// its <c>#elif SM6</c> branch: <c>vs_6_0</c>/<c>ps_6_0</c>, <c>Texture2D</c>/<c>SamplerState</c>
/// pairs — the SAME branch and parameter names (<c>TextureTex</c>/<c>FontTex</c>/
/// <c>BlueNoiseTex</c>) the Vulkan renderer binds, NOT DX11's legacy
/// <c>TextureSampler</c>/<c>FontSampler</c>/<c>BlueNoiseSampler</c> names.</para>
///
/// <para>Vertex layout, non-identity transform, and dithering-off discipline are identical to
/// the DX11/Vulkan renderers — see their remarks for the full rationale.</para>
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
        Window.Title = "ShadowDusk Apos.Shapes DX12 validation (headless)";
    }

    protected override void LoadContent()
    {
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
        public readonly Vector4 FillA;       // TEXCOORD1
        public readonly Vector4 FillB;       // TEXCOORD2
        public readonly Vector4 BorderA;     // TEXCOORD3
        public readonly Vector4 BorderB;     // TEXCOORD4
        public readonly Vector4 FillCoord;   // TEXCOORD5
        public readonly Vector4 BorderCoord; // TEXCOORD6
        public readonly Vector4 Meta1;       // TEXCOORD7  x: lineSize, y: aaPixels, z: sdfSize, w: extra
        public readonly Vector4 Meta2;       // TEXCOORD8
        public readonly Vector4 Meta3;       // TEXCOORD9
        public readonly Vector4 ClipDist;    // POSITION1
        public readonly Vector2 ClipRoundAA; // NORMAL0

        public AposVertex(Vector4 position, Vector4 texCoord, Vector4 fillA, Vector4 fillB,
                          Vector4 borderA, Vector4 borderB, Vector4 fillCoord, Vector4 borderCoord,
                          Vector4 meta1, Vector4 meta2, Vector4 meta3, Vector4 clipDist, Vector2 clipRoundAa)
        {
            Position = position; TexCoord = texCoord; FillA = fillA; FillB = fillB;
            BorderA = borderA; BorderB = borderB; FillCoord = fillCoord; BorderCoord = borderCoord;
            Meta1 = meta1; Meta2 = meta2; Meta3 = meta3; ClipDist = clipDist; ClipRoundAA = clipRoundAa;
        }

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(  0, VertexElementFormat.Vector4, VertexElementUsage.Position,           0),
            new VertexElement( 16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  0),
            new VertexElement( 32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  1),
            new VertexElement( 48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  2),
            new VertexElement( 64, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  3),
            new VertexElement( 80, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  4),
            new VertexElement( 96, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  5),
            new VertexElement(112, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  6),
            new VertexElement(128, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  7),
            new VertexElement(144, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  8),
            new VertexElement(160, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate,  9),
            new VertexElement(176, VertexElementFormat.Vector4, VertexElementUsage.Position,           1),
            new VertexElement(192, VertexElementFormat.Vector2, VertexElementUsage.Normal,             0));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private static AposVertex[] BuildCircleQuad()
    {
        const float Meta      = 0f;
        const float Rounded   = 0f;
        const float LineSize  = 0.10f;
        const float AaPixels  = 1.0f;
        const float SdfSize   = 0.70f;

        var fill   = new Vector4(1.00f, 0.25f, 0.50f, 1f);
        var border = new Vector4(0.00f, 0.40f, 1.00f, 1f);

        var clipDist    = new Vector4(1000f, 1000f, 1000f, 1000f);
        var clipRoundAa = new Vector2(0f, 1f);

        var coord = new Vector4(0f, 0f, 1f, 1f);
        var zero  = Vector4.Zero;

        AposVertex Corner(float x, float y) => new(
            position:    new Vector4(x, y, 0f, 1f),
            texCoord:    new Vector4(x, y, Rounded, Meta),
            fillA:       fill, fillB: fill,
            borderA:     border, borderB: border,
            fillCoord:   coord, borderCoord: coord,
            meta1:       new Vector4(LineSize, AaPixels, SdfSize, SdfSize),
            meta2:       zero, meta3: zero,
            clipDist:    clipDist, clipRoundAa: clipRoundAa);

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
            GraphicsDevice.SamplerStates[2]  = SamplerState.PointWrap;

            // NON-IDENTITY, ASYMMETRIC transform (issue #70 discipline), exact-dyadic values
            // so both compilers compute bit-identical positions and a correct result is maxd 0.
            effect.Parameters["view_projection"]?.SetValue(new Matrix(
                0.5f,  0f,    0f, 0f,
                0f,    0.5f,  0f, 0f,
                0f,    0f,    1f, 0f,
                0.25f, 0.25f, 0f, 1f));

            effect.Parameters["half_viewport"]?.SetValue(new Vector2(Size / 2f, Size / 2f));
            effect.Parameters["dither_scale"]?.SetValue(0f);
            effect.Parameters["dither_mode"]?.SetValue(0f);

            // SM6 branch parameter names (same as Vulkan's renderer) - DX12 takes the same
            // #elif SM6 branch, not DX11's legacy sampler-object names.
            effect.Parameters["TextureTex"]?.SetValue(_white);
            effect.Parameters["FontTex"]?.SetValue(_white);
            effect.Parameters["BlueNoiseTex"]?.SetValue(_white);

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
