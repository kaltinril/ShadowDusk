using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.AndroidGl;

/// <summary>
/// Builds a recognizable cartoon-cat <see cref="Texture2D"/> procedurally (no content pipeline,
/// no bundled PNG) so the Phase 50 harness has a real image to run the on-device-compiled
/// Pixelated shader over. All coordinates are normalized 0..1 of the texture.
/// </summary>
internal static class CatTexture
{
    public static Texture2D Create(GraphicsDevice gd, int w, int h)
    {
        var px = new Color[w * h];

        var bg        = new Color(204, 226, 245);
        var orange    = new Color(245, 150, 70);
        var darkOrange= new Color(214, 120, 50);
        var pink      = new Color(255, 192, 198);
        var nosePink  = new Color(232, 120, 140);
        var white     = new Color(255, 255, 255);
        var green     = new Color(70, 175, 95);
        var black     = new Color(35, 35, 40);

        Fill(px, bg);

        // ears (drawn first, behind the head)
        FillTri(px, w, h, 0.20f, 0.42f, 0.38f, 0.08f, 0.50f, 0.44f, orange);
        FillTri(px, w, h, 0.80f, 0.42f, 0.62f, 0.08f, 0.50f, 0.44f, orange);
        FillTri(px, w, h, 0.27f, 0.40f, 0.38f, 0.18f, 0.47f, 0.42f, pink);
        FillTri(px, w, h, 0.73f, 0.40f, 0.62f, 0.18f, 0.53f, 0.42f, pink);

        // head
        FillCircle(px, w, h, 0.50f, 0.58f, 0.36f, orange);

        // tabby forehead stripes
        FillRect(px, w, h, 0.485f, 0.27f, 0.515f, 0.40f, darkOrange);
        FillRect(px, w, h, 0.40f, 0.32f, 0.43f, 0.43f, darkOrange);
        FillRect(px, w, h, 0.57f, 0.32f, 0.60f, 0.43f, darkOrange);

        // eyes
        Eye(px, w, h, 0.385f, 0.56f, white, green, black);
        Eye(px, w, h, 0.615f, 0.56f, white, green, black);

        // nose
        FillTri(px, w, h, 0.46f, 0.66f, 0.54f, 0.66f, 0.50f, 0.72f, nosePink);

        // mouth
        Line(px, w, h, 0.50f, 0.72f, 0.50f, 0.77f, black, 3);
        Line(px, w, h, 0.50f, 0.77f, 0.43f, 0.81f, black, 3);
        Line(px, w, h, 0.50f, 0.77f, 0.57f, 0.81f, black, 3);

        // whiskers
        Line(px, w, h, 0.40f, 0.67f, 0.13f, 0.62f, black, 2);
        Line(px, w, h, 0.40f, 0.71f, 0.11f, 0.71f, black, 2);
        Line(px, w, h, 0.40f, 0.75f, 0.13f, 0.80f, black, 2);
        Line(px, w, h, 0.60f, 0.67f, 0.87f, 0.62f, black, 2);
        Line(px, w, h, 0.60f, 0.71f, 0.89f, 0.71f, black, 2);
        Line(px, w, h, 0.60f, 0.75f, 0.87f, 0.80f, black, 2);

        var tex = new Texture2D(gd, w, h);
        tex.SetData(px);
        return tex;
    }

    private static void Eye(Color[] px, int w, int h, float cx, float cy, Color white, Color iris, Color pupil)
    {
        FillEllipse(px, w, h, cx, cy, 0.085f, 0.105f, white);
        FillCircle(px, w, h, cx, cy + 0.01f, 0.055f, iris);
        FillCircle(px, w, h, cx, cy + 0.015f, 0.028f, pupil);
        FillCircle(px, w, h, cx - 0.022f, cy - 0.025f, 0.014f, white); // highlight
    }

    private static void Fill(Color[] px, Color c) { for (int i = 0; i < px.Length; i++) px[i] = c; }

    private static void Set(Color[] px, int w, int h, int x, int y, Color c)
    { if ((uint)x < (uint)w && (uint)y < (uint)h) px[y * w + x] = c; }

    private static void FillCircle(Color[] px, int w, int h, float ncx, float ncy, float nr, Color c)
    {
        int cx = (int)(ncx * w), cy = (int)(ncy * h), r = (int)(nr * w);
        for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            { int dx = x - cx, dy = y - cy; if (dx * dx + dy * dy <= r * r) Set(px, w, h, x, y, c); }
    }

    private static void FillEllipse(Color[] px, int w, int h, float ncx, float ncy, float nrx, float nry, Color c)
    {
        int cx = (int)(ncx * w), cy = (int)(ncy * h), rx = (int)(nrx * w), ry = (int)(nry * h);
        for (int y = cy - ry; y <= cy + ry; y++)
            for (int x = cx - rx; x <= cx + rx; x++)
            { float dx = (x - cx) / (float)rx, dy = (y - cy) / (float)ry; if (dx * dx + dy * dy <= 1f) Set(px, w, h, x, y, c); }
    }

    private static void FillRect(Color[] px, int w, int h, float nx0, float ny0, float nx1, float ny1, Color c)
    {
        int x0 = (int)(nx0 * w), y0 = (int)(ny0 * h), x1 = (int)(nx1 * w), y1 = (int)(ny1 * h);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(px, w, h, x, y, c);
    }

    private static void FillTri(Color[] px, int w, int h,
        float ax, float ay, float bx, float by, float cx2, float cy2, Color col)
    {
        var a = new Vector2(ax * w, ay * h);
        var b = new Vector2(bx * w, by * h);
        var cc = new Vector2(cx2 * w, cy2 * h);
        int minx = (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, cc.X)));
        int maxx = (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, cc.X)));
        int miny = (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, cc.Y)));
        int maxy = (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, cc.Y)));
        float area = Edge(a, b, cc);
        for (int y = miny; y <= maxy; y++)
            for (int x = minx; x <= maxx; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = Edge(b, cc, p), w1 = Edge(cc, a, p), w2 = Edge(a, b, p);
                bool inside = area >= 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                if (inside) Set(px, w, h, x, y, col);
            }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static void Line(Color[] px, int w, int h, float nx0, float ny0, float nx1, float ny1, Color c, int thick)
    {
        int x0 = (int)(nx0 * w), y0 = (int)(ny0 * h), x1 = (int)(nx1 * w), y1 = (int)(ny1 * h);
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        int t = thick / 2;
        while (true)
        {
            for (int oy = -t; oy <= t; oy++) for (int ox = -t; ox <= t; ox++) Set(px, w, h, x0 + ox, y0 + oy, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
