using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ShaderViewer;

public class Game1 : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;
    private SpriteFont _font = null!;
    private Texture2D _catTexture = null!;
    private Texture2D _dividerLine = null!;

    // Scissor rasterizer state — created once, reused every frame
    private static readonly RasterizerState RsScissor = new() { ScissorTestEnable = true, CullMode = CullMode.None };

    private readonly List<ShaderEntry> _entries = new();
    private int _index;
    private KeyboardState _prev;

    private record ShaderEntry(string Name, Effect? Effect, string? LoadError);

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this);
        _graphics.PreferredBackBufferWidth  = 1280;
        _graphics.PreferredBackBufferHeight = 720;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "ShadowDusk Shader Viewer";
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _font        = Content.Load<SpriteFont>("Font");

        var catPath = Path.Combine(AppContext.BaseDirectory, "Content", "cat.jpg");
        using var stream = File.OpenRead(catPath);
        _catTexture = Texture2D.FromStream(GraphicsDevice, stream);

        _dividerLine = new Texture2D(GraphicsDevice, 1, 1);
        _dividerLine.SetData(new[] { Color.White });

        _entries.Add(new ShaderEntry("(no effect - passthrough)", null, null));

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders", "DirectX_11");
        if (!Directory.Exists(shaderDir))
        {
            _entries.Add(new ShaderEntry($"[ERROR] Shader dir not found: {shaderDir}", null, null));
            return;
        }

        foreach (var path in Directory.GetFiles(shaderDir, "*.mgfx"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var effect = new Effect(GraphicsDevice, File.ReadAllBytes(path));
                _entries.Add(new ShaderEntry(name, effect, null));
            }
            catch (Exception ex)
            {
                _entries.Add(new ShaderEntry(name, null, $"Load failed: {Sanitize(ex.Message)}"));
            }
        }
    }

    protected override void Update(GameTime gameTime)
    {
        var keys = Keyboard.GetState();

        if (keys.IsKeyDown(Keys.Escape))
            Exit();

        if (IsPressed(keys, _prev, Keys.Right) || IsPressed(keys, _prev, Keys.D))
            _index = (_index + 1) % _entries.Count;

        if (IsPressed(keys, _prev, Keys.Left) || IsPressed(keys, _prev, Keys.A))
            _index = (_index - 1 + _entries.Count) % _entries.Count;

        _prev = keys;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        var entry    = _entries[_index];
        var vp       = GraphicsDevice.Viewport;
        var half     = vp.Width / 2;
        var fullDest = new Rectangle(0, 0, vp.Width, vp.Height);

        GraphicsDevice.Clear(Color.Black);

        // ── Left half: always the original, no effect ─────────────────────────
        // Drawing this first also primes SpriteBatch's internal sprite vertex
        // shader, which pixel-only effects (no VS defined) will inherit.
        GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, half, vp.Height);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.LinearClamp, null, RsScissor);
        _spriteBatch.Draw(_catTexture, fullDest, Color.White);
        _spriteBatch.End();

        // ── Right half: effect applied, scissored to right side ───────────────
        string? drawError = null;
        bool    shaderOn  = false;

        GraphicsDevice.ScissorRectangle = new Rectangle(half, 0, half, vp.Height);

        if (entry.LoadError != null)
        {
            // Shader failed to load — dim the right side to signal the error
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.LinearClamp, null, RsScissor);
            _spriteBatch.Draw(_catTexture, fullDest, new Color(60, 60, 60));
            _spriteBatch.End();
        }
        else if (entry.Effect == null)
        {
            // Passthrough — right side is identical to left
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.LinearClamp, null, RsScissor);
            _spriteBatch.Draw(_catTexture, fullDest, Color.White);
            _spriteBatch.End();
            shaderOn = true;
        }
        else
        {
            TrySetCommonParameters(entry.Effect, gameTime);
            try
            {
                // SpriteSortMode.Immediate applies each pass before the draw call.
                // For pixel-only shaders (no VS in the pass) the sprite VS set by
                // the left-half draw above stays active — this is intentional.
                _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
                    SamplerState.LinearClamp, null, RsScissor, entry.Effect);
                _spriteBatch.Draw(_catTexture, fullDest, Color.White);
                _spriteBatch.End();
                shaderOn = true;
            }
            catch (Exception ex)
            {
                try { _spriteBatch.End(); } catch { }
                drawError = Sanitize(ex.Message);

                // Show dimmed original so the screen isn't just black
                _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                    SamplerState.LinearClamp, null, RsScissor);
                _spriteBatch.Draw(_catTexture, fullDest, new Color(60, 60, 60));
                _spriteBatch.End();
            }
        }

        // ── HUD (no scissor, drawn over everything) ────────────────────────────
        GraphicsDevice.ScissorRectangle = vp.Bounds;
        _spriteBatch.Begin();

        // Divider
        _spriteBatch.Draw(_dividerLine, new Rectangle(half - 1, 0, 3, vp.Height), Color.White * 0.6f);

        // Side labels
        DrawShadowed("Original", new Vector2(half / 2 - 30, vp.Height - 48), Color.LightGray);
        var rightLabel = (shaderOn && entry.Effect != null) ? entry.Name : "(no effect)";
        DrawShadowed(rightLabel, new Vector2(half + half / 2 - 40, vp.Height - 48), Color.LightGray);

        // Header
        DrawShadowed($"[{_index + 1}/{_entries.Count}] {entry.Name}", new Vector2(10, 10), Color.White);

        if (entry.LoadError != null)
            DrawShadowed(entry.LoadError, new Vector2(10, 32), Color.OrangeRed);
        else if (drawError != null)
            DrawShadowed($"Draw error: {drawError}", new Vector2(10, 32), Color.OrangeRed);

        DrawShadowed("Left/Right to cycle   ESC to quit", new Vector2(10, vp.Height - 28), Color.Yellow);

        _spriteBatch.End();

        base.Draw(gameTime);
    }

    private void TrySetCommonParameters(Effect effect, GameTime gameTime)
    {
        var t = (float)gameTime.TotalGameTime.TotalSeconds;
        var w = _graphics.PreferredBackBufferWidth;
        var h = _graphics.PreferredBackBufferHeight;

        var screenOrtho = Matrix.CreateOrthographicOffCenter(0, w, h, 0, 0, -1);

        effect.Parameters["Texture"]?.SetValue(_catTexture);
        effect.Parameters["Texture2"]?.SetValue(_catTexture);
        effect.Parameters["DiffuseMap"]?.SetValue(_catTexture);
        effect.Parameters["Lightmap"]?.SetValue(_catTexture);
        effect.Parameters["SpriteTexture"]?.SetValue(_catTexture);
        effect.Parameters["Character01"]?.SetValue(_catTexture);
        effect.Parameters["Character02"]?.SetValue(_catTexture);
        effect.Parameters["_secondTexture"]?.SetValue(_catTexture);
        effect.Parameters["Mask"]?.SetValue(_catTexture);
        effect.Parameters["ClipTexture"]?.SetValue(_catTexture);
        effect.Parameters["DrawTexture"]?.SetValue(_catTexture);
        effect.Parameters["RenderTargetTexture"]?.SetValue(_catTexture);
        effect.Parameters["MaskTexture"]?.SetValue(_catTexture);
        effect.Parameters["_normalMap"]?.SetValue(_catTexture);
        effect.Parameters["_dissolveTex"]?.SetValue(_catTexture);
        effect.Parameters["_colorMap"]?.SetValue(_catTexture);
        effect.Parameters["TextureSize"]?.SetValue(new Vector2(_catTexture.Width, _catTexture.Height));
        effect.Parameters["ScreenSize"]?.SetValue(new Vector2(w, h));
        effect.Parameters["Time"]?.SetValue(t);
        effect.Parameters["ElapsedTime"]?.SetValue(t);
        effect.Parameters["Progress"]?.SetValue(0.5f + 0.5f * MathF.Sin(t));
        effect.Parameters["amount"]?.SetValue(0.3f + 0.3f * MathF.Sin(t));
        effect.Parameters["_progress"]?.SetValue(0.3f + 0.3f * MathF.Sin(t));
        effect.Parameters["_dissolveThreshold"]?.SetValue(0.04f);
        effect.Parameters["_dissolveThresholdColor"]?.SetValue(new Vector4(1f, 0.5f, 0f, 1f));
        effect.Parameters["Strength"]?.SetValue(1f);
        effect.Parameters["Intensity"]?.SetValue(1f);
        effect.Parameters["LightPosition"]?.SetValue(new Vector2(0.5f, 0.5f));
        effect.Parameters["LightColor"]?.SetValue(new Vector3(1f, 1f, 0.9f));
        effect.Parameters["AmbientColor"]?.SetValue(new Vector3(0.2f, 0.2f, 0.25f));
        effect.Parameters["Radius"]?.SetValue(0.4f);
        effect.Parameters["lightSource"]?.SetValue(new Vector2(0.5f, 0.5f));
        effect.Parameters["lightColor"]?.SetValue(new Vector3(1f, 1f, 0.9f));
        effect.Parameters["lightRadius"]?.SetValue(0.5f);
        effect.Parameters["BloomThreshold"]?.SetValue(0.25f);
        effect.Parameters["BloomIntensity"]?.SetValue(1f);
        effect.Parameters["BaseIntensity"]?.SetValue(1f);
        effect.Parameters["BloomSaturation"]?.SetValue(0.8f);
        effect.Parameters["BaseSaturation"]?.SetValue(1f);
        effect.Parameters["GlowIntensity"]?.SetValue(1f);
        effect.Parameters["GlowSize"]?.SetValue(0.005f);
        effect.Parameters["_sepiaTone"]?.SetValue(new Vector3(1.2f, 1.0f, 0.8f));
        effect.Parameters["_alphaTest"]?.SetValue(new Vector3(0.1f, -1f, 1f));
        effect.Parameters["Color"]?.SetValue(Vector4.One);

        // VS-driven shaders: matrix params (non-standard names)
        effect.Parameters["_matrixTransform"]?.SetValue(screenOrtho);      // ForwardLighting
        effect.Parameters["MatrixTransform"]?.SetValue(screenOrtho);       // SpriteEffect
        effect.Parameters["viewProjectionMatrix"]?.SetValue(screenOrtho);  // PolygonLight

        // 3D effects (BasicEffect, AlphaTestEffect, etc.)
        effect.Parameters["WorldViewProj"]?.SetValue(screenOrtho);
        effect.Parameters["World"]?.SetValue(Matrix.Identity);
        effect.Parameters["View"]?.SetValue(Matrix.Identity);
        effect.Parameters["Projection"]?.SetValue(screenOrtho);
        effect.Parameters["DiffuseColor"]?.SetValue(Vector4.One);
        effect.Parameters["EmissiveColor"]?.SetValue(Vector3.Zero);
        effect.Parameters["Alpha"]?.SetValue(1f);
        effect.Parameters["AlphaTest"]?.SetValue(new Vector4(0, 0, 0, 0));
        effect.Parameters["DirLight0Direction"]?.SetValue(new Vector3(0f, 0f, -1f));
        effect.Parameters["DirLight0DiffuseColor"]?.SetValue(Vector3.One);
        effect.Parameters["DirLight0SpecularColor"]?.SetValue(Vector3.Zero);
        effect.Parameters["EyePosition"]?.SetValue(new Vector3(0f, 0f, 1f));
    }

    private void DrawShadowed(string text, Vector2 pos, Color color)
    {
        _spriteBatch.DrawString(_font, text, pos + Vector2.One, Color.Black * 0.8f);
        _spriteBatch.DrawString(_font, text, pos, color);
    }

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c >= 32 && c <= 126 ? c : '?');
        return sb.ToString();
    }

    private static bool IsPressed(KeyboardState cur, KeyboardState prev, Keys key)
        => cur.IsKeyDown(key) && !prev.IsKeyDown(key);
}
