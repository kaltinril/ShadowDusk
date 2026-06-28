using System;
using Android.Util;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;

namespace ShadowDusk.Validation.AndroidGl;

/// <summary>
/// Phase 50 proof-of-concept: an on-device shader fiddle. It takes a shader as a STRING (what a
/// user would type into a text box), compiles it to a MonoGame <c>.mgfx</c> blob IN MEMORY, AT
/// RUNTIME, on the device via ShadowDusk's <see cref="EffectCompiler"/>, and loads the result
/// into a live <see cref="Effect"/>. The whole point of Phase 50: no host precompile, no .xnb,
/// no content pipeline - text to renderable Effect, live, on Android.
///
/// The outcome is reported three ways so it is unambiguous on a real device:
///   * logcat tag <c>SHADOWDUSK</c> (read with <c>adb logcat -s SHADOWDUSK</c>),
///   * the clear colour - GREEN = compiled + Effect loaded on device, ORANGE = the compiler
///     ran but rejected the shader, RED = a native (DXC / SPIRV-Cross) is missing,
///   * the window title is irrelevant on Android; the colour + logcat are the signal.
/// </summary>
public sealed class FiddleGame : Game
{
    private const string Tag = "SHADOWDUSK";

    // The "user-typed" shader: the canonical MonoGame GL sprite pixel shader. A successful
    // on-device compile of THIS proves the faithful HLSL -> SPIR-V -> GLSL -> .mgfx pipeline
    // ran entirely on the phone.
    private const string UserHlsl = @"
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
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique SpriteDrawing
{
    pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); }
}
";

    private readonly GraphicsDeviceManager _graphics;
    private Color _clear = Color.CornflowerBlue;
    private string _status = "(compiling on device...)";
    private Effect? _effect;

    public FiddleGame()
    {
        _graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent()
    {
        try
        {
            Log.Info(Tag, "On-device compile: ShadowDusk EffectCompiler.CompileAsync(OpenGL) ...");

            // The product call, SEAMLESS: plain new EffectCompiler(), no injection, no flag.
            // On Android the compiler auto-selects the pure-managed SpirvReflector (the native
            // DXIL-oracle reflection is unavailable on .NET-for-Android); on desktop it keeps
            // the DXIL oracle. Either way the .mgfx is byte-identical.
            var result = new EffectCompiler()
                .CompileAsync(UserHlsl, new CompilerOptions { Target = PlatformTarget.OpenGL })
                .GetAwaiter().GetResult();

            if (result.IsFailure)
            {
                _status = "COMPILE REJECTED: " + string.Join("; ",
                    Array.ConvertAll(result.Error, e => e.Code + " " + e.Message));
                _clear = Color.Orange;   // the compiler RAN (natives loaded) but the shader was rejected
                Log.Error(Tag, _status);
                return;
            }

            byte[] mgfx = result.Value.Data;

            // Load the freshly-compiled blob into a live MonoGame Effect on the device.
            _effect = new Effect(GraphicsDevice, mgfx);

            _status = $"ON-DEVICE COMPILE OK: {mgfx.Length} byte .mgfx -> Effect technique '" +
                      (_effect.CurrentTechnique?.Name ?? "?") + "'";
            _clear = Color.Green;        // SUCCESS - in-memory, on-device, runtime compile worked
            Log.Info(Tag, _status);
        }
        catch (Exception ex)
        {
            // Expected until the android-arm64 natives ship: the first native P/Invoke
            // (DXC or SPIRV-Cross) throws DllNotFoundException. This isolates the exact blocker.
            _status = "NATIVE MISSING / load error: " + ex.GetType().Name + ": " + ex.Message;
            _clear = Color.Red;
            Log.Error(Tag, _status, ex);
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(_clear);
        base.Draw(gameTime);
    }
}
