using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.ShaderFiddle.Web;

/// <summary>
/// KNI (nkast) Blazor-WebGL game that draws the standard cat image with a
/// custom <see cref="Effect"/> applied, mirroring the desktop Phase-17
/// validation path (<c>validation/Shared/EffectImageRenderer.cs</c>): the same
/// <see cref="BlendState.Opaque"/> + <see cref="SamplerState.LinearClamp"/>
/// state and the SpriteBatch VS prime so a PS-only effect inherits SpriteBatch's
/// vertex shader. The <c>.mgfx</c> bytes come from
/// <see cref="ApplyEffect"/> — either precompiled mode-1 corpus bytes or, once
/// Phase 100 wires the WASM compiler, in-browser mode-2 output.
/// </summary>
public sealed class ShaderFiddleGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _catBytes;

    // Applied before LoadContent runs (renderer not ready yet); flushed there.
    private byte[]? _pendingMgfx;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private Effect? _effect;

    public ShaderFiddleGame(byte[] catBytes, byte[]? initialMgfx)
    {
        _catBytes = catBytes;
        _pendingMgfx = initialMgfx;

        _gdm = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = new MemoryStream(_catBytes);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);

        if (_pendingMgfx is not null)
        {
            ApplyEffect(_pendingMgfx);
            _pendingMgfx = null;
        }

        base.LoadContent();
    }

    /// <summary>
    /// Swap in a new effect from <c>.mgfx</c> bytes. Returns <c>null</c> on
    /// success, or a human-readable message if <see cref="Effect"/> rejects the
    /// bytes (e.g. KNI's forked <c>MGFXReader10</c> diverges from MonoGame's).
    /// Safe to call from the Blazor UI thread — WASM is single-threaded and the
    /// game loop runs on the same thread.
    /// </summary>
    public string? ApplyEffect(byte[] mgfxBytes)
    {
        if (_cat is null)
        {
            // Renderer not booted yet — defer until LoadContent.
            _pendingMgfx = mgfxBytes;
            return null;
        }

        Effect candidate;
        try
        {
            candidate = new Effect(GraphicsDevice, mgfxBytes);
        }
        catch (Exception ex)
        {
            return $"new Effect(gd, bytes) failed in KNI WebGL: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            WebShaderInputs.SetParams(candidate, _cat);
        }
        catch
        {
            /* a missing/typed param must not abort the swap */
        }

        _effect?.Dispose();
        _effect = candidate;
        return null;
    }

    /// <summary>
    /// Remove any applied effect so the plain cat renders with no shader,
    /// returning the canvas to its original (unprocessed) image. Also clears any
    /// effect queued before <see cref="LoadContent"/> ran. Safe to call from the
    /// Blazor UI thread.
    /// </summary>
    public void ClearEffect()
    {
        _pendingMgfx = null;
        _effect?.Dispose();
        _effect = null;
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);

        if (_cat is null)
        {
            base.Draw(gameTime);
            return;
        }

        Rectangle dest = FitCentered(_cat.Width, _cat.Height,
            GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

        // Prime SpriteBatch's sprite vertex shader. The corpus shaders are
        // pixel-only; SpriteBatch's VS must be active before the PS-only effect
        // is applied — matches the desktop validation harness.
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        _sb.Draw(_cat, dest, Color.White);
        _sb.End();

        if (_effect is not null)
        {
            // Pin texture slot 1 (Dissolve's second texture, _dissolveTex) to a
            // defined sampler state. SpriteBatch.Begin only sets slot 0; slot 1
            // otherwise keeps the runtime default, which KNI WebGL and desktop
            // DesktopGL resolve differently for the NPOT cat — that shifts the
            // _dissolveTex sample and flips Dissolve's threshold-band tint in WebGL
            // (Phase 24: Δ198/1.68% px -> Δ128/0.145% px after this pin; passes).
            // See tests/ShadowDusk.BrowserTests/DISSOLVE-INVESTIGATION.md.
            GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp;

            // Immediate applies each pass before the draw; Opaque so the result
            // is exactly the shader output (the cat is opaque; no blend bleed).
            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, null, null, _effect);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();
        }

        base.Draw(gameTime);
    }

    /// <summary>Largest centered rect of (w,h) aspect fitting inside the viewport.</summary>
    private static Rectangle FitCentered(int w, int h, int vw, int vh)
    {
        if (w <= 0 || h <= 0 || vw <= 0 || vh <= 0)
            return new Rectangle(0, 0, vw, vh);

        float scale = Math.Min((float)vw / w, (float)vh / h);
        int dw = (int)(w * scale);
        int dh = (int)(h * scale);
        return new Rectangle((vw - dw) / 2, (vh - dh) / 2, dw, dh);
    }
}
