#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.ShaderToyViewer.Runtime;

/// <summary>
/// Minimal runtime helper that drives a ShaderToy-converted <see cref="Effect"/> (the
/// <c>.fx</c> emitted by <c>ShadowDusk.ShaderToy</c>) over a fullscreen screen-space pass.
///
/// <para>
/// The converted effect carries its OWN vertex + pixel shaders: the vertex shader takes a
/// POSITION already in NDC ([-1,1]) and derives the [0,1] UV the pixel-shader harness maps to
/// ShaderToy's bottom-left <c>fragCoord</c>. So this helper does NOT use <see cref="SpriteBatch"/>
/// (which would override the effect's vertex shader with its own sprite VS); it draws two
/// triangles covering the viewport directly with the effect's passes applied. That keeps the
/// helper to the single assumption ShaderToy's model needs — "apply this pixel shader over these
/// vertices" — so it could later drive other geometry by swapping the vertex buffer.
/// </para>
///
/// <para>
/// Uniform setters are best-effort: ShadowDusk only emits the uniforms a shader actually
/// references, so <see cref="SetResolution"/>/<see cref="SetTime"/>/... silently no-op when the
/// matching <see cref="EffectParameter"/> is absent. This mirrors how a host would feed the
/// standard ShaderToy uniform set without knowing which the shader uses.
/// </para>
/// </summary>
public sealed class ShaderToyEffect : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly Effect _effect;
    private readonly bool _ownsEffect;
    private readonly VertexBuffer _quad;

    private bool _disposed;

    /// <summary>
    /// Wraps a loaded ShaderToy effect.
    /// </summary>
    /// <param name="device">The graphics device the effect was created on.</param>
    /// <param name="effect">The loaded converted effect.</param>
    /// <param name="ownsEffect">
    /// When true, <see cref="Dispose"/> also disposes <paramref name="effect"/>.
    /// </param>
    public ShaderToyEffect(GraphicsDevice device, Effect effect, bool ownsEffect = false)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _effect = effect ?? throw new ArgumentNullException(nameof(effect));
        _ownsEffect = ownsEffect;

        // Two triangles covering the full NDC square [-1,1] x [-1,1]. The converted vertex
        // shader passes POSITION straight through (already NDC) and derives UV from it.
        var verts = new[]
        {
            new VertexPositionTexture(new Vector3(-1f, -1f, 0f), new Vector2(0f, 1f)),
            new VertexPositionTexture(new Vector3(-1f,  1f, 0f), new Vector2(0f, 0f)),
            new VertexPositionTexture(new Vector3( 1f, -1f, 0f), new Vector2(1f, 1f)),
            new VertexPositionTexture(new Vector3( 1f,  1f, 0f), new Vector2(1f, 0f)),
        };

        _quad = new VertexBuffer(
            _device, VertexPositionTexture.VertexDeclaration, verts.Length, BufferUsage.WriteOnly);
        _quad.SetData(verts);
    }

    /// <summary>The wrapped effect (e.g. to inspect its parameters/techniques).</summary>
    public Effect Effect => _effect;

    /// <summary>
    /// Push <c>iResolution</c> (xy = pixel size, z = pixel aspect = 1). No-op if absent.
    /// </summary>
    public void SetResolution(float width, float height)
        => TrySet("iResolution", new Vector3(width, height, 1f));

    /// <summary>Push <c>iTime</c> (seconds). No-op if absent.</summary>
    public void SetTime(float seconds) => TrySet("iTime", seconds);

    /// <summary>Push <c>iTimeDelta</c> (seconds per frame). No-op if absent.</summary>
    public void SetTimeDelta(float seconds) => TrySet("iTimeDelta", seconds);

    /// <summary>Push <c>iFrame</c> (integer frame counter). No-op if absent.</summary>
    public void SetFrame(int frame) => TrySet("iFrame", frame);

    /// <summary>
    /// Push <c>iMouse</c> (xy = current pixel, zw = click pixel; ShaderToy convention). No-op
    /// if absent.
    /// </summary>
    public void SetMouse(Vector4 mouse) => TrySet("iMouse", mouse);

    /// <summary>
    /// Push one of the <c>iChannel0..3</c> texture channels. <paramref name="index"/> selects
    /// the channel; the effect exposes the sampler via an <c>iChannelNTexture</c> parameter.
    /// No-op if that channel is absent.
    /// </summary>
    public void SetChannel(int index, Texture2D? texture)
    {
        if (index is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(index), index, "iChannel index must be 0..3.");

        TrySet($"iChannel{index}Texture", texture);
    }

    /// <summary>
    /// Render the fullscreen ShaderToy pass into the currently bound render target (or the back
    /// buffer). Applies every pass of the effect's first technique to two triangles covering the
    /// viewport.
    /// </summary>
    public void Draw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Opaque + no depth: the output is exactly the shader's color, with no blend or
        // depth-test interference (ShaderToy's image pass is an unconditional screen fill).
        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;

        _device.SetVertexBuffer(_quad);

        EffectTechnique technique = _effect.CurrentTechnique ?? _effect.Techniques[0];
        foreach (EffectPass pass in technique.Passes)
        {
            pass.Apply();
            // TriangleStrip over the 4 NDC corner vertices = two triangles = full quad.
            _device.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }

    /// <summary>
    /// Set a CUSTOM effect parameter by name (a value driven by a custom <c>uniform</c> the source
    /// shader declared). No-op if the effect has no such parameter. Mirrors the best-effort contract of
    /// the standard uniform setters.
    /// </summary>
    public void SetCustom(string name, float value) => TrySet(name, value);

    /// <inheritdoc cref="SetCustom(string,float)"/>
    public void SetCustom(string name, Vector3 value) => TrySet(name, value);

    /// <inheritdoc cref="SetCustom(string,float)"/>
    public void SetCustom(string name, Vector4 value) => TrySet(name, value);

    /// <inheritdoc cref="SetCustom(string,float)"/>
    public void SetCustom(string name, Texture2D? value) => TrySet(name, value);

    private void TrySet(string name, float value) => Parameter(name)?.SetValue(value);
    private void TrySet(string name, int value) => Parameter(name)?.SetValue(value);
    private void TrySet(string name, Vector3 value) => Parameter(name)?.SetValue(value);
    private void TrySet(string name, Vector4 value) => Parameter(name)?.SetValue(value);

    private void TrySet(string name, Texture2D? value)
    {
        if (value is not null)
            Parameter(name)?.SetValue(value);
    }

    private EffectParameter? Parameter(string name) => _effect.Parameters[name];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _quad.Dispose();
        if (_ownsEffect)
            _effect.Dispose();
    }
}
