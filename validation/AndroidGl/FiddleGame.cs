using System;
using Android.Util;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;

namespace ShadowDusk.Validation.AndroidGl;

/// <summary>
/// Phase 50 on-device shader fiddle. It takes the Pixelated effect as a SHADER STRING (what a
/// user would type), compiles it to a MonoGame <c>.mgfx</c> IN MEMORY, AT RUNTIME, on the device
/// via ShadowDusk (seamless <c>new EffectCompiler()</c>), and then actually RENDERS with it: a
/// procedurally-drawn cat is shown twice - the original on the left, and the same cat run through
/// the on-device-compiled Pixelated pixel shader on the right.
///
/// Outcome also goes to logcat (tag SHADOWDUSK; <c>adb logcat -s SHADOWDUSK</c>).
/// </summary>
public sealed class FiddleGame : Game
{
    private const string Tag = "SHADOWDUSK";

    // The "user-typed" shader: the Pixelated effect (the MonoGame/XnaFiddle corpus shader).
    // `round` is written as floor(x+0.5) so it is valid on every GL ES dialect. Compiled and
    // run ENTIRELY on the device.
    private const string PixelatedHlsl = @"
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float pixels = 128.0f;      // sampling resolution
    float pixelation = 4.0f;    // block size -> ~32 blocks across, gentler pixelation

    float mx = input.TextureCoordinates.x * pixels;
    float my = input.TextureCoordinates.y * pixels;

    float x = floor(mx / pixelation + 0.5f) * pixelation;
    float y = floor(my / pixelation + 0.5f) * pixelation;
    float2 coord = float2(x / pixels, y / pixels);

    return tex2D(SpriteTextureSampler, coord) * input.Color;
}

technique SpriteDrawing
{
    pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); }
}
";

    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _cat = null!;
    private Effect? _pixelEffect;
    private Color _clear = new Color(26, 26, 40);

    public FiddleGame()
    {
        _graphics = new GraphicsDeviceManager(this);
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _cat = CatTexture.Create(GraphicsDevice, 256, 256);

        try
        {
            Log.Info(Tag, "On-device compile: Pixelated shader via ShadowDusk EffectCompiler(OpenGL) ...");

            // The product call, SEAMLESS: plain new EffectCompiler(). On Android it auto-selects
            // the managed SpirvReflector; HLSL text -> .mgfx bytes, fully in memory, on the device.
            var result = new EffectCompiler()
                .CompileAsync(PixelatedHlsl, new CompilerOptions { Target = PlatformTarget.OpenGL })
                .GetAwaiter().GetResult();

            if (result.IsFailure)
            {
                _clear = Color.Orange;
                Log.Error(Tag, "COMPILE REJECTED: " + string.Join("; ",
                    Array.ConvertAll(result.Error, e => e.Code + " " + e.Message)));
                return;
            }

            byte[] mgfx = result.Value.Data;
            _pixelEffect = new Effect(GraphicsDevice, mgfx);
            Log.Info(Tag, $"ON-DEVICE COMPILE OK: {mgfx.Length} byte .mgfx -> Effect technique '" +
                          (_pixelEffect.CurrentTechnique?.Name ?? "?") + "'; rendering pixelated cat.");
        }
        catch (Exception ex)
        {
            _clear = Color.Red;
            Log.Error(Tag, "NATIVE MISSING / load error: " + ex.GetType().Name + ": " + ex.Message, ex);
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(_clear);

        int w = GraphicsDevice.Viewport.Width;
        int h = GraphicsDevice.Viewport.Height;
        float scale = Math.Min(w * 0.44f / _cat.Width, h * 0.5f / _cat.Height);
        var origin = new Vector2(_cat.Width / 2f, _cat.Height / 2f);
        var leftPos = new Vector2(w * 0.27f, h * 0.42f);
        var rightPos = new Vector2(w * 0.73f, h * 0.42f);

        // Original cat (left). PointClamp keeps the pixelation crisp; this first (no-effect) pass
        // also primes SpriteBatch's vertex shader, which the pixel-only effect relies on.
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        _spriteBatch.Draw(_cat, leftPos, null, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);
        _spriteBatch.End();

        // Same cat, PIXELATED by the shader compiled on the device a moment ago (right).
        if (_pixelEffect != null)
        {
            _spriteBatch.Begin(effect: _pixelEffect, samplerState: SamplerState.PointClamp);
            _spriteBatch.Draw(_cat, rightPos, null, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);
            _spriteBatch.End();
        }

        base.Draw(gameTime);
    }
}
