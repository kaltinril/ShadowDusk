#nullable enable

using Silk.NET.OpenGL;

namespace ShadowDusk.ImageTests.GlContext;

/// <summary>
/// Wraps a 128x128 RGBA8 offscreen Framebuffer Object (FBO) used as the render
/// target for every visual-regression test. Owned by a single thread/context.
/// </summary>
public sealed class OffscreenRenderer : IDisposable
{
    public const int Width  = 128;
    public const int Height = 128;

    private readonly GL   _gl;
    private readonly uint _fbo;
    private readonly uint _colorTexture;
    private readonly uint _depthRenderbuffer;

    private bool _disposed;

    public OffscreenRenderer(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _gl = gl;

        _fbo               = _gl.GenFramebuffer();
        _colorTexture      = _gl.GenTexture();
        _depthRenderbuffer = _gl.GenRenderbuffer();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        // Color attachment: GL_RGBA8 texture.
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        // Allocate storage only — no source pixels — by passing an empty Span.
        ReadOnlySpan<byte> emptyPixels = ReadOnlySpan<byte>.Empty;
        _gl.TexImage2D(
            target:         TextureTarget.Texture2D,
            level:          0,
            internalformat: InternalFormat.Rgba8,
            width:          Width,
            height:         Height,
            border:         0,
            format:         PixelFormat.Rgba,
            type:           PixelType.UnsignedByte,
            pixels:         emptyPixels);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _colorTexture,
            0);

        // Depth attachment: GL_DEPTH_COMPONENT16 renderbuffer.
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent16, Width, Height);
        _gl.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer,
            _depthRenderbuffer);

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            // Clean up before throwing so the caller doesn't leak GL objects.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.DeleteFramebuffer(_fbo);
            _gl.DeleteTexture(_colorTexture);
            _gl.DeleteRenderbuffer(_depthRenderbuffer);
            throw new InvalidOperationException(
                $"Offscreen FBO is not complete (glCheckFramebufferStatus = 0x{(uint)status:X4}).");
        }

        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Binds the FBO as the active draw target and sets the viewport to 128x128.
    /// Subsequent draw calls render into the offscreen color/depth attachments.
    /// </summary>
    public void Bind()
    {
        ThrowIfDisposed();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, Width, Height);
    }

    /// <summary>
    /// Binds the FBO, sets the viewport, and clears both color and depth attachments.
    /// Color bytes are normalized to the GL [0..1] range.
    /// </summary>
    public void Clear(byte r, byte g, byte b, byte a)
    {
        ThrowIfDisposed();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, Width, Height);
        _gl.ClearColor(r / 255f, g / 255f, b / 255f, a / 255f);
        _gl.ClearDepth(1.0);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    }

    /// <summary>
    /// Reads the color attachment back into a tightly-packed RGBA byte array.
    /// Length is always <see cref="Width"/> * <see cref="Height"/> * 4.
    /// </summary>
    public byte[] ReadPixels()
    {
        ThrowIfDisposed();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        const int Stride = Width * 4;
        var pixels = new byte[Stride * Height];

        // Match the default pack alignment to 1 so 4-channel rows aren't padded.
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        _gl.ReadPixels<byte>(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        // OpenGL's framebuffer origin is bottom-left; PNG and most image
        // libraries (including ImageSharp) use a top-left origin. Flip rows
        // here so callers can hand the buffer straight to an image encoder.
        Span<byte> rowA = stackalloc byte[Stride];
        for (int y = 0; y < Height / 2; y++)
        {
            int top    = y * Stride;
            int bottom = (Height - 1 - y) * Stride;
            pixels.AsSpan(top, Stride).CopyTo(rowA);
            pixels.AsSpan(bottom, Stride).CopyTo(pixels.AsSpan(top, Stride));
            rowA.CopyTo(pixels.AsSpan(bottom, Stride));
        }

        return pixels;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_colorTexture);
        _gl.DeleteRenderbuffer(_depthRenderbuffer);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
