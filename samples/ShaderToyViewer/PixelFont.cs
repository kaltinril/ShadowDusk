#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.ShaderToyViewer;

/// <summary>
/// A tiny built-in 5x7 bitmap font so the sample can draw status / error text on screen WITHOUT a
/// content build (no <c>SpriteFont</c> asset, no MGCB step). Glyphs are hand-encoded as 7 rows of 5
/// bits and rendered as filled rectangles from a 1x1 white texture via <see cref="SpriteBatch"/>.
/// Only the characters the sample needs (uppercase + digits + common punctuation) are defined;
/// unknown characters render as a blank space, which is fine for diagnostics.
/// </summary>
public sealed class PixelFont : IDisposable
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;

    private readonly Texture2D _pixel;
    private bool _disposed;

    public PixelFont(GraphicsDevice device)
    {
        if (device is null)
            throw new ArgumentNullException(nameof(device));
        _pixel = new Texture2D(device, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    /// <summary>The on-screen height of one line of text at the given pixel <paramref name="scale"/>.</summary>
    public static int LineHeight(int scale) => (GlyphHeight + 1) * scale;

    /// <summary>
    /// Draws multi-line text (split on <c>\n</c>) at <paramref name="origin"/>. Uppercases the text so
    /// it can use the compact glyph set, and clips nothing (the caller positions it).
    /// </summary>
    public void DrawString(SpriteBatch batch, string text, Vector2 origin, Color color, int scale = 2)
    {
        if (batch is null)
            throw new ArgumentNullException(nameof(batch));
        if (string.IsNullOrEmpty(text))
            return;

        float startX = origin.X;
        float x = startX;
        float y = origin.Y;
        int advance = (GlyphWidth + 1) * scale;

        foreach (char raw in text)
        {
            if (raw == '\n')
            {
                x = startX;
                y += LineHeight(scale);
                continue;
            }

            char c = char.ToUpperInvariant(raw);
            if (Glyphs.TryGetValue(c, out byte[]? rows))
                DrawGlyph(batch, rows, x, y, color, scale);

            x += advance;
        }
    }

    private void DrawGlyph(SpriteBatch batch, byte[] rows, float x, float y, Color color, int scale)
    {
        for (int r = 0; r < GlyphHeight; r++)
        {
            byte bits = rows[r];
            for (int col = 0; col < GlyphWidth; col++)
            {
                // Bit (GlyphWidth-1-col) of the row is the leftmost pixel.
                if ((bits & (1 << (GlyphWidth - 1 - col))) == 0)
                    continue;
                var rect = new Rectangle(
                    (int)x + col * scale, (int)y + r * scale, scale, scale);
                batch.Draw(_pixel, rect, color);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pixel.Dispose();
    }

    // 5x7 glyphs: each entry is 7 rows, each row a 5-bit mask (MSB = leftmost pixel).
    private static readonly IReadOnlyDictionary<char, byte[]> Glyphs = BuildGlyphs();

    private static Dictionary<char, byte[]> BuildGlyphs()
    {
        // Encoded as binary literals row by row for legibility.
        return new Dictionary<char, byte[]>
        {
            [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
            ['A'] = Rows(0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001),
            ['B'] = Rows(0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110),
            ['C'] = Rows(0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110),
            ['D'] = Rows(0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110),
            ['E'] = Rows(0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111),
            ['F'] = Rows(0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000),
            ['G'] = Rows(0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111),
            ['H'] = Rows(0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001),
            ['I'] = Rows(0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110),
            ['J'] = Rows(0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100),
            ['K'] = Rows(0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001),
            ['L'] = Rows(0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111),
            ['M'] = Rows(0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001),
            ['N'] = Rows(0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001),
            ['O'] = Rows(0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110),
            ['P'] = Rows(0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000),
            ['Q'] = Rows(0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101),
            ['R'] = Rows(0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001),
            ['S'] = Rows(0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110),
            ['T'] = Rows(0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100),
            ['U'] = Rows(0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110),
            ['V'] = Rows(0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100),
            ['W'] = Rows(0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001),
            ['X'] = Rows(0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001),
            ['Y'] = Rows(0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100),
            ['Z'] = Rows(0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111),
            ['0'] = Rows(0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110),
            ['1'] = Rows(0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110),
            ['2'] = Rows(0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111),
            ['3'] = Rows(0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110),
            ['4'] = Rows(0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010),
            ['5'] = Rows(0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110),
            ['6'] = Rows(0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110),
            ['7'] = Rows(0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000),
            ['8'] = Rows(0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110),
            ['9'] = Rows(0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100),
            ['.'] = Rows(0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00110, 0b00110),
            [','] = Rows(0b00000, 0b00000, 0b00000, 0b00000, 0b00110, 0b00110, 0b01000),
            [':'] = Rows(0b00000, 0b00110, 0b00110, 0b00000, 0b00110, 0b00110, 0b00000),
            ['-'] = Rows(0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000),
            ['_'] = Rows(0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111),
            ['/'] = Rows(0b00001, 0b00010, 0b00100, 0b00100, 0b01000, 0b10000, 0b10000),
            ['\\'] = Rows(0b10000, 0b01000, 0b00100, 0b00100, 0b00010, 0b00001, 0b00001),
            ['('] = Rows(0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010),
            [')'] = Rows(0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000),
            ['!'] = Rows(0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100),
            ['?'] = Rows(0b01110, 0b10001, 0b00001, 0b00110, 0b00100, 0b00000, 0b00100),
            ['#'] = Rows(0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010),
            ['%'] = Rows(0b11001, 0b11010, 0b00100, 0b00100, 0b01011, 0b10011, 0b00000),
            ['+'] = Rows(0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000),
            ['='] = Rows(0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000),
            ['*'] = Rows(0b00000, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0b00000),
            ['>'] = Rows(0b01000, 0b00100, 0b00010, 0b00001, 0b00010, 0b00100, 0b01000),
            ['<'] = Rows(0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010),
            ['['] = Rows(0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110),
            [']'] = Rows(0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110),
            ['"'] = Rows(0b01010, 0b01010, 0b01010, 0b00000, 0b00000, 0b00000, 0b00000),
            ['\''] = Rows(0b00100, 0b00100, 0b00100, 0b00000, 0b00000, 0b00000, 0b00000),
        };
    }

    private static byte[] Rows(params int[] rows)
    {
        var bytes = new byte[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            bytes[i] = (byte)rows[i];
        return bytes;
    }
}
