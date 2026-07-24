#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Apos.Shapes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.AposGallery;

/// <summary>
/// Phase 55: renders Apos.Shapes' full public shape-drawing surface through the REAL
/// <c>ShapeBatch</c> (not a hand-rolled vertex harness), once per given effect "arm". One arm
/// with <c>UseEmbeddedGolden = true</c> drives <c>ShapeBatch</c>'s own precompiled effect (the
/// package's golden, loaded via its private <c>LoadEmbeddedEffect</c>); the rest drive a
/// ShadowDusk-compiled <see cref="Effect"/>. Every arm draws the IDENTICAL gallery scene
/// through the SAME <c>ShapeBatch</c> C# vertex-building code and the SAME view/projection
/// matrix, so any pixel divergence between arms can only come from the shader itself, never
/// from vertex construction.
///
/// <para>This single file compiles unchanged against every backend's MonoGame flavor
/// (DesktopGL, WindowsDX, and MonoGame.Framework.Native for DX12/Vulkan) — it is linked via
/// <c>&lt;Compile Include&gt;</c> from all four <c>validation/VsDriven*</c> projects, not
/// duplicated. GL does not use the golden arm (see <c>NOTICE.md</c> — the package's own GL
/// compile of this shader revision is a confirmed MojoShader bug, not a ShadowDusk defect), but
/// the plumbing here is backend-agnostic either way.</para>
/// </summary>
public sealed class AposGalleryRenderer : Game
{
    /// <summary>One rendering pass: either the package's embedded golden effect, or a
    /// ShadowDusk-compiled candidate (<paramref name="CompileError"/> non-null ⇒ compile failed,
    /// no render is attempted for this arm).</summary>
    public sealed record Arm(string Name, bool UseEmbeddedGolden, byte[]? EffectBytes, string? CompileError);

    public const int CellSize = 100;
    public const int Cols = 6;
    public const int Rows = 5;
    public const int Width = CellSize * Cols;
    public const int Height = CellSize * Rows;

    private readonly GraphicsDeviceManager _gdm;
    private readonly string _outDir;
    private readonly IReadOnlyList<Arm> _arms;
    private bool _done;

    public List<(string Name, bool Loaded, bool Rendered, string? Error)> Outcomes { get; } = new();
    public List<(string Name, Color[] Pixels, int Width, int Height)> Captures { get; } = new();

    /// <summary>Cell rectangle for each named gallery entry, in the SAME order <see cref="DrawGallery"/>
    /// emits them — built from the same source list so the two can never drift out of sync.</summary>
    public static IReadOnlyList<(string Name, Rectangle Cell)> Cells { get; } = BuildCellLayout();

    public AposGalleryRenderer(string outDir, IReadOnlyList<Arm> arms)
    {
        _outDir = outDir;
        _arms = arms;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = Width,
            PreferredBackBufferHeight = Height,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Apos.Shapes gallery validation (headless)";
    }

    protected override void LoadContent() => Directory.CreateDirectory(_outDir);

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }

        foreach (var arm in _arms)
            RenderArm(arm);

        _done = true;
        Exit();
    }

    private void RenderArm(Arm arm)
    {
        if (!arm.UseEmbeddedGolden && arm.EffectBytes is null)
        {
            Outcomes.Add((arm.Name, false, false, $"compile failed: {arm.CompileError}"));
            return;
        }

        Effect? candidateEffect = null;
        ShapeBatch batch;
        try
        {
            if (!arm.UseEmbeddedGolden)
                candidateEffect = new Effect(GraphicsDevice, arm.EffectBytes!);
            batch = new ShapeBatch(GraphicsDevice, candidateEffect);
        }
        catch (Exception ex)
        {
            Outcomes.Add((arm.Name, false, false, $"new ShapeBatch()/Effect() threw: {ex.Message}"));
            candidateEffect?.Dispose();
            return;
        }

        using var rt = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Black);

            // Non-identity, asymmetric view — the issue-#70 discipline: an identity transform
            // can't detect a coordinate-handling bug. Both arms run the IDENTICAL ShapeBatch
            // C# vertex-building code with this SAME matrix, so any divergence between arms can
            // only come from the shader, never from vertex construction.
            var view = Matrix.CreateScale(1.15f) * Matrix.CreateTranslation(6f, 4f, 0f);
            batch.Begin(view: view);
            DrawGallery(batch);
            batch.End();

            var pixels = new Color[Width * Height];
            GraphicsDevice.SetRenderTarget(null);
            rt.GetData(pixels);
            Captures.Add((arm.Name, pixels, Width, Height));

            string png = Path.Combine(_outDir, arm.Name + ".png");
            using (var fs = File.Create(png))
                rt.SaveAsPng(fs, Width, Height);

            Outcomes.Add((arm.Name, true, true, null));
        }
        catch (Exception ex)
        {
            GraphicsDevice.SetRenderTarget(null);
            Outcomes.Add((arm.Name, true, false, $"render threw: {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            batch.Dispose();
            candidateEffect?.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------
    // The gallery scene: one call per ShapeBatch public Draw*/Fill*/Border* shape method (30
    // entries — every shape kind × {Draw, Fill, Border}), laid out on a fixed grid. At least one
    // entry per shape kind exercises a non-default style knob: FillCircle/FillPath use a
    // Gradient, BorderCircle/BorderLine use a DashStyle, DrawRectangle uses CornerRadii,
    // BorderRectangle/FillEquilateralTriangle use rotation, BorderTriangle uses a non-default
    // aaSize, FillHexagon uses the "rounded" corner knob.
    // ---------------------------------------------------------------------------------------
    private static void DrawGallery(ShapeBatch sb)
    {
        foreach (var entry in GalleryEntries())
            entry.Draw(sb, CellCenter(entry.Index));
    }

    private static IReadOnlyList<(string Name, Rectangle Cell)> BuildCellLayout()
    {
        var list = new List<(string, Rectangle)>();
        foreach (var entry in GalleryEntries())
        {
            int col = entry.Index % Cols, row = entry.Index / Cols;
            list.Add((entry.Name, new Rectangle(col * CellSize, row * CellSize, CellSize, CellSize)));
        }
        return list;
    }

    private static Vector2 CellCenter(int index)
    {
        int col = index % Cols, row = index / Cols;
        return new Vector2(col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f);
    }

    private static Vector2[] OpenPathPts(Vector2 c) => new[]
    {
        c + new Vector2(-32f, 22f),
        c + new Vector2(-10f, -22f),
        c + new Vector2(16f, 12f),
        c + new Vector2(32f, -16f),
    };

    private static Vector2[] ClosedPathPts(Vector2 c) => new[]
    {
        c + new Vector2(0f, -26f),
        c + new Vector2(25f, -8f),
        c + new Vector2(15f, 22f),
        c + new Vector2(-15f, 22f),
        c + new Vector2(-25f, -8f),
    };

    private static IReadOnlyList<(int Index, string Name, Action<ShapeBatch, Vector2> Draw)> GalleryEntries()
    {
        (string, Action<ShapeBatch, Vector2>)[] raw =
        {
            ("DrawCircle", (sb, c) => sb.DrawCircle(c, 28f, Color.OrangeRed, Color.Black, thickness: 4f)),
            ("FillCircle", (sb, c) => sb.FillCircle(c, 28f,
                new Gradient(c + new Vector2(-22f, 0f), Color.Yellow, c + new Vector2(22f, 0f), Color.Purple, Gradient.Shape.Linear))),
            ("BorderCircle", (sb, c) => sb.BorderCircle(c, 28f, Color.Cyan, thickness: 5f, dash: new DashStyle(6f, 4f))),

            ("DrawRectangle", (sb, c) => sb.DrawRectangle(c - new Vector2(26f, 18f), new Vector2(52f, 36f),
                Color.LimeGreen, Color.Black, thickness: 4f, cornerRadii: new CornerRadii(10f))),
            ("FillRectangle", (sb, c) => sb.FillRectangle(c - new Vector2(26f, 18f), new Vector2(52f, 36f), Color.Gold)),
            ("BorderRectangle", (sb, c) => sb.BorderRectangle(c - new Vector2(22f, 22f), new Vector2(44f, 44f),
                Color.HotPink, thickness: 4f, rotation: 0.4f)),

            ("DrawLine", (sb, c) => sb.DrawLine(c + new Vector2(-28f, -18f), c + new Vector2(28f, 18f), 6f, Color.White, Color.Black, thickness: 3f)),
            ("FillLine", (sb, c) => sb.FillLine(c + new Vector2(-28f, 18f), c + new Vector2(28f, -18f), 6f, Color.Aqua)),
            ("BorderLine", (sb, c) => sb.BorderLine(c + new Vector2(-28f, 0f), c + new Vector2(28f, 0f), 8f,
                Color.Red, thickness: 4f, dash: new DashStyle(5f, 3f))),

            ("DrawPath", (sb, c) => sb.DrawPath(OpenPathPts(c), 6f, Color.Wheat, Color.Black,
                thickness: 3f, join: PathJoin.Miter, cap: PathCap.Square)),
            ("FillPath", (sb, c) => sb.FillPath(ClosedPathPts(c), 6f,
                new Gradient(c, Color.Blue, c + new Vector2(0f, 26f), Color.White, Gradient.Shape.Radial), closed: true)),
            ("BorderPath", (sb, c) => sb.BorderPath(OpenPathPts(c), 5f, Color.OrangeRed, thickness: 3f)),

            ("DrawHexagon", (sb, c) => sb.DrawHexagon(c, 26f, Color.MediumPurple, Color.Black, thickness: 3f)),
            ("FillHexagon", (sb, c) => sb.FillHexagon(c, 26f, Color.Teal, rounded: 6f)),
            ("BorderHexagon", (sb, c) => sb.BorderHexagon(c, 26f, Color.Coral, thickness: 4f)),

            ("DrawEquilateralTriangle", (sb, c) => sb.DrawEquilateralTriangle(c, 28f, Color.Khaki, Color.Black, thickness: 3f)),
            ("FillEquilateralTriangle", (sb, c) => sb.FillEquilateralTriangle(c, 28f, Color.SeaGreen, rotation: 0.5f)),
            ("BorderEquilateralTriangle", (sb, c) => sb.BorderEquilateralTriangle(c, 28f, Color.Salmon, thickness: 3f)),

            ("DrawTriangle", (sb, c) => sb.DrawTriangle(c + new Vector2(0f, -26f), c + new Vector2(24f, 22f), c + new Vector2(-24f, 22f),
                Color.LightBlue, Color.Black, thickness: 3f)),
            ("FillTriangle", (sb, c) => sb.FillTriangle(c + new Vector2(0f, -26f), c + new Vector2(24f, 22f), c + new Vector2(-24f, 22f), Color.Plum)),
            ("BorderTriangle", (sb, c) => sb.BorderTriangle(c + new Vector2(0f, -26f), c + new Vector2(24f, 22f), c + new Vector2(-24f, 22f),
                Color.Tomato, thickness: 4f, aaSize: 3f)),

            ("DrawEllipse", (sb, c) => sb.DrawEllipse(c, 30f, 17f, Color.LightGreen, Color.Black, thickness: 3f)),
            ("FillEllipse", (sb, c) => sb.FillEllipse(c, 30f, 17f, Color.Orchid)),
            ("BorderEllipse", (sb, c) => sb.BorderEllipse(c, 30f, 17f, Color.SteelBlue, thickness: 4f)),

            ("DrawArc", (sb, c) => sb.DrawArc(c, 0f, 4.2f, 16f, 28f, Color.Yellow, Color.Black, thickness: 3f)),
            ("FillArc", (sb, c) => sb.FillArc(c, 0.3f, 5.0f, 14f, 26f, Color.DeepSkyBlue)),
            ("BorderArc", (sb, c) => sb.BorderArc(c, 0.5f, 4.5f, 15f, 28f, Color.Crimson, thickness: 4f)),

            ("DrawRing", (sb, c) => sb.DrawRing(c, 0f, MathHelper.TwoPi, 15f, 28f, Color.Turquoise, Color.Black, thickness: 3f)),
            ("FillRing", (sb, c) => sb.FillRing(c, 0f, MathHelper.TwoPi, 15f, 28f, Color.Violet)),
            ("BorderRing", (sb, c) => sb.BorderRing(c, 0f, MathHelper.TwoPi, 15f, 28f, Color.Chartreuse, thickness: 4f)),
        };

        var result = new (int, string, Action<ShapeBatch, Vector2>)[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            result[i] = (i, raw[i].Item1, raw[i].Item2);
        return result;
    }
}
