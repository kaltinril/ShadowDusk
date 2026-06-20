#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ShadowDusk.ShaderToy.Sample;

/// <summary>
/// The interactive capstone window. On startup, and whenever the user cycles shaders, it runs the
/// full RUNTIME path (ShaderToy GLSL -> <c>.fx</c> -> in-memory <c>.mgfx</c> via ShadowDusk -> live
/// <see cref="Effect"/>) and renders the result animated and interactive over a fullscreen quad.
///
/// <para>When started with a file path on the command line, that file is added to the catalog as the
/// first, HOT-RELOADABLE entry: editing it on disk auto re-converts + recompiles + reloads it live.</para>
///
/// <para>Controls: SPACE / RIGHT = next shader, LEFT = previous, mouse drives <c>iMouse</c>,
/// ESC = quit. The window title shows the current shader name and the uniforms it references; a
/// convert/compile error is drawn on screen (the sample never crashes on a bad shader).</para>
/// </summary>
public sealed class SampleGame : Game
{
    private const int Width = 960;
    private const int Height = 540;

    private readonly GraphicsDeviceManager _gdm;
    private readonly IReadOnlyList<ShaderSource> _sources;

    private int _index;
    private CompiledShaderToy? _current;
    private float _time;
    private int _frame;
    private Vector4 _mouse;

    private KeyboardState _prevKeyboard;

    private SpriteBatch _spriteBatch = null!;
    private PixelFont _font = null!;

    // Hot-reload state for the currently-loaded source (external files only).
    private DateTime _watchedWriteTimeUtc;
    private double _pollAccumulator;
    private double _reloadFlashSeconds;
    private string _reloadFlashText = string.Empty;

    /// <param name="external">A user-supplied shader file (hot-reloaded), or <c>null</c> for the bundled catalog only.</param>
    public SampleGame(ShaderSource? external = null)
    {
        _sources = ShaderCatalog.Build(external, out _index);
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Width,
            PreferredBackBufferHeight = Height,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = false;
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _font = new PixelFont(GraphicsDevice);
        LoadCurrentShader();
    }

    private ShaderSource Active => _sources[_index];

    private void LoadCurrentShader()
    {
        _current?.Effect?.Dispose();

        ShaderSource source = Active;
        _current = SampleCompiler.Build(GraphicsDevice, source);

        // Reset the animation clock so each freshly-compiled shader starts at t = 0.
        _time = 0f;
        _frame = 0;

        // Remember the file's write time so a hot-reload poll only re-builds when it actually changes.
        _watchedWriteTimeUtc = SafeWriteTimeUtc(source.Path);

        UpdateTitle();

        // Surface a convert/compile failure once, on load, rather than crashing or spamming Draw.
        if (!_current.Ok)
            Console.Error.WriteLine($"[sample] {source.DisplayName}:\n{_current.Error}");
    }

    private void UpdateTitle()
    {
        ShaderSource source = Active;
        string uniforms = _current is { UsedUniforms.Count: > 0 }
            ? string.Join(", ", _current.UsedUniforms)
            : "(none)";
        string status = _current is { Ok: true } ? "compiled in-memory via ShadowDusk" : "ERROR (see screen)";
        string tag = source.IsExternal ? "EXTERNAL (hot-reload)" : "bundled";
        Window.Title =
            $"ShadowDusk ShaderToy sample  |  [{_index + 1}/{_sources.Count}] " +
            $"{source.DisplayName} ({tag})  |  uniforms: {uniforms}  |  {status}  |  " +
            "SPACE/arrows cycle, ESC quit";
    }

    protected override void Update(GameTime gameTime)
    {
        KeyboardState keyboard = Keyboard.GetState();

        if (keyboard.IsKeyDown(Keys.Escape))
        {
            Exit();
            return;
        }

        if (Pressed(keyboard, Keys.Space) || Pressed(keyboard, Keys.Right))
            Cycle(+1);
        else if (Pressed(keyboard, Keys.Left))
            Cycle(-1);
        else if (Pressed(keyboard, Keys.R))
            Reload("reloaded (R)"); // manual reload, handy for any entry

        _prevKeyboard = keyboard;

        double dt = gameTime.ElapsedGameTime.TotalSeconds;

        // Hot-reload: poll the external file's last-write-time a few times a second. A poll (vs a
        // FileSystemWatcher) is simplest, has no threading, and is plenty responsive for an editor save.
        if (Active.IsExternal)
        {
            _pollAccumulator += dt;
            if (_pollAccumulator >= 0.25)
            {
                _pollAccumulator = 0;
                DateTime current = SafeWriteTimeUtc(Active.Path);
                if (current != _watchedWriteTimeUtc && current != default)
                    Reload("reloaded");
            }
        }

        if (_reloadFlashSeconds > 0)
            _reloadFlashSeconds -= dt;

        // Drive the ShaderToy clock and mouse (ShaderToy's bottom-left-origin convention: y up,
        // and the click-position zw is only non-zero while a button is held).
        _time += (float)dt;
        _frame++;

        MouseState mouse = Mouse.GetState();
        float mx = mouse.X;
        float my = Height - 1 - mouse.Y; // flip to ShaderToy's bottom-left origin
        bool down = mouse.LeftButton == ButtonState.Pressed;
        _mouse = new Vector4(mx, my, down ? mx : 0f, down ? my : 0f);

        base.Update(gameTime);
    }

    private bool Pressed(KeyboardState now, Keys key) =>
        now.IsKeyDown(key) && _prevKeyboard.IsKeyUp(key);

    private void Cycle(int delta)
    {
        int count = _sources.Count;
        _index = ((_index + delta) % count + count) % count;
        LoadCurrentShader();
    }

    /// <summary>Re-runs the full runtime path for the active source and shows a brief on-screen flash.</summary>
    private void Reload(string flash)
    {
        LoadCurrentShader();
        _reloadFlashSeconds = 1.5;
        _reloadFlashText = _current is { Ok: true } ? flash : "reload ERROR";
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        CompiledShaderToy? current = _current;
        if (current is { Ok: true, Effect: { } effect })
        {
            effect.SetResolution(Width, Height);
            effect.SetTime(_time);
            effect.SetTimeDelta((float)gameTime.ElapsedGameTime.TotalSeconds);
            effect.SetFrame(_frame);
            effect.SetMouse(_mouse);
            effect.Draw();
        }

        DrawOverlay(current);

        base.Draw(gameTime);
    }

    /// <summary>
    /// Draws the on-screen overlay: a convert/compile error (so a bad shader is visible, not just a
    /// black screen) and a transient "reloaded" flash. Uses the built-in <see cref="PixelFont"/>, so
    /// no content build / SpriteFont asset is needed.
    /// </summary>
    private void DrawOverlay(CompiledShaderToy? current)
    {
        bool hasError = current is { Ok: false };
        bool hasFlash = _reloadFlashSeconds > 0;
        if (!hasError && !hasFlash)
            return;

        _spriteBatch.Begin();

        if (hasError)
        {
            // Dim the (black) background slightly and draw the diagnostic wrapped to the window width.
            _spriteBatch.Draw(WhitePixel, new Rectangle(0, 0, Width, Height), new Color(0, 0, 0, 200));
            string header = $"CONVERT/COMPILE ERROR: {Active.DisplayName}";
            _font.DrawString(_spriteBatch, header, new Vector2(16, 16), Color.OrangeRed, scale: 2);

            string body = current!.Error;
            int line = 0;
            foreach (string wrapped in WrapForWidth(body, Width - 32, scale: 2))
            {
                _font.DrawString(
                    _spriteBatch, wrapped,
                    new Vector2(16, 16 + PixelFont.LineHeight(2) * (2 + line)),
                    Color.White, scale: 2);
                line++;
                if (line > 26) // do not overflow the window with a huge diagnostic
                    break;
            }
        }

        if (hasFlash)
        {
            Color c = _reloadFlashText.Contains("ERROR", StringComparison.Ordinal) ? Color.OrangeRed : Color.LightGreen;
            _font.DrawString(_spriteBatch, _reloadFlashText, new Vector2(16, Height - PixelFont.LineHeight(3) - 12), c, scale: 3);
        }

        _spriteBatch.End();
    }

    private Texture2D WhitePixel => _whitePixel ??= CreateWhitePixel();
    private Texture2D? _whitePixel;

    private Texture2D CreateWhitePixel()
    {
        var tex = new Texture2D(GraphicsDevice, 1, 1);
        tex.SetData(new[] { Color.White });
        return tex;
    }

    /// <summary>Greedy word + hard-newline wrapping to a target pixel width for the bitmap font.</summary>
    private static IEnumerable<string> WrapForWidth(string text, int pixelWidth, int scale)
    {
        int glyphAdvance = (5 + 1) * scale;
        int maxChars = Math.Max(8, pixelWidth / glyphAdvance);

        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = rawLine.Replace("\t", "    ", StringComparison.Ordinal);
            if (line.Length <= maxChars)
            {
                yield return line;
                continue;
            }

            // Hard-wrap overly long lines (file paths and diagnostics have few spaces).
            for (int i = 0; i < line.Length; i += maxChars)
                yield return line.Substring(i, Math.Min(maxChars, line.Length - i));
        }
    }

    private static DateTime SafeWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Effect?.Dispose();
            _spriteBatch?.Dispose();
            _font?.Dispose();
            _whitePixel?.Dispose();
        }

        base.Dispose(disposing);
    }
}
