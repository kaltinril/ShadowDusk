#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// The GROUND-TRUTH reference renderer for the Phase 46 fidelity gate. It establishes a single
/// hidden GLFW + OpenGL 3.3 Compatibility context (the same pattern as
/// <c>tests/ShadowDusk.ImageTests/GlContext/GlContextFixture.cs</c>) and renders each ORIGINAL
/// ShaderToy GLSL <c>mainImage</c> body DIRECTLY - no conversion, no ShadowDusk pipeline - into an
/// offscreen FBO at a fixed resolution + fixed uniforms. This is the "what the original GLSL renders"
/// truth we diff our converted <c>.mgfx</c> render against.
///
/// <para>
/// Orientation: the fragment shader reads <c>gl_FragCoord.xy</c> directly (GL bottom-left origin),
/// exactly as ShaderToy.com does. <c>glReadPixels</c> returns rows bottom-first, so
/// <see cref="ReadPixelsTopFirst"/> flips them to TOP-row-first - matching MonoGame's
/// <c>RenderTarget2D.GetData</c> layout so the two buffers align pixel-for-pixel for diffing.
/// </para>
/// </summary>
public sealed class GlReferenceRenderer : IDisposable
{
    private readonly int _width;
    private readonly int _height;

    private IWindow? _window;
    private GL? _gl;
    private uint _fbo;
    private uint _colorTex;
    private uint _vao;
    private uint _vbo;

    private bool _disposed;

    /// <summary>True when no GL context could be created (no GPU/driver); the gate must report this.</summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>Why the GL context is unavailable (when <see cref="IsUnavailable"/>).</summary>
    public string? UnavailableReason { get; private set; }

    public GlReferenceRenderer(int width, int height)
    {
        _width = width;
        _height = height;

        try
        {
            PreloadGlfwNative();
            Window.PrioritizeGlfw();

            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(1, 1),
                Title = "ShadowDusk ShaderToy fidelity reference (offscreen)",
                IsVisible = false,
                ShouldSwapAutomatically = false,
                IsEventDriven = true,
                API = new GraphicsAPI(
                    ContextAPI.OpenGL,
                    ContextProfile.Compatability,
                    ContextFlags.Default,
                    new APIVersion(3, 3)),
                VSync = false,
            };

            _window = Window.Create(options);
            _window.Initialize();
            _gl = GL.GetApi(_window);

            CreateFbo();
            CreateFullscreenQuad();
        }
        catch (Exception ex)
        {
            IsUnavailable = true;
            UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
            DisposeQuietly();
        }
    }

    private static void PreloadGlfwNative()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";
        string path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "runtimes", rid, "native", "libglfw.so.3");
        if (System.IO.File.Exists(path))
            NativeLibrary.TryLoad(path, out _);
    }

    private void CreateFbo()
    {
        GL gl = _gl!;
        _fbo = gl.GenFramebuffer();
        _colorTex = gl.GenTexture();

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        ReadOnlySpan<byte> empty = ReadOnlySpan<byte>.Empty;
        gl.TexImage2D(
            TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)_width, (uint)_height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, empty);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);

        GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Reference FBO incomplete (0x{(uint)status:X4}).");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private unsafe void CreateFullscreenQuad()
    {
        GL gl = _gl!;
        // Two triangles covering NDC [-1,1]^2. The fragment shader uses gl_FragCoord, so the only
        // vertex attribute needed is position.
        float[] verts =
        {
            -1f, -1f,
             3f, -1f,   // oversized triangle trick would work too; use a quad-as-2-tris for clarity
            -1f,  3f,
        };

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = verts)
        {
            gl.BufferData(
                BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);
        }
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Render one ShaderToy GLSL body to the offscreen FBO at the fixed uniforms and read the result
    /// back TOP-row-first (MonoGame layout). Returns null + a reason when the GLSL fails to compile or
    /// link as a plain <c>#version 330</c> ShaderToy fragment shader (it then cannot be a fair
    /// reference and must be SKIPPED, not faked).
    /// </summary>
    public (byte[]? Rgba, string? SkipReason) RenderReference(
        string mainImageGlsl, RefUniforms u)
    {
        if (IsUnavailable || _gl is null)
            return (null, "GL reference context unavailable: " + UnavailableReason);

        GL gl = _gl;

        string fragSource = BuildFragmentSource(mainImageGlsl);

        (uint program, string? err) = TryBuildProgram(gl, VertexSource, fragSource);
        if (program == 0)
            return (null, "GLSL did not compile/link as a plain reference shader: " + err);

        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            gl.Viewport(0, 0, (uint)_width, (uint)_height);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear((uint)ClearBufferMask.ColorBufferBit);

            gl.UseProgram(program);
            SetUniforms(gl, program, u);

            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.BindVertexArray(0);

            gl.Finish();
            byte[] rgba = ReadPixelsTopFirst(gl);

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return (rgba, null);
        }
        finally
        {
            gl.DeleteProgram(program);
        }
    }

    private static void SetUniforms(GL gl, uint program, RefUniforms u)
    {
        SetVec3(gl, program, "iResolution", u.ResolutionX, u.ResolutionY, 1f);
        SetFloat(gl, program, "iTime", u.Time);
        // iGlobalTime is the deprecated ShaderToy alias of iTime; drive it identically so a body that
        // reads the legacy spelling renders the same scene the converter (which folds it to iTime) does.
        SetFloat(gl, program, "iGlobalTime", u.Time);
        SetFloat(gl, program, "iTimeDelta", u.TimeDelta);
        SetInt(gl, program, "iFrame", u.Frame);
        SetVec4(gl, program, "iMouse", u.MouseX, u.MouseY, u.MouseZ, u.MouseW);
    }

    private static void SetFloat(GL gl, uint program, string name, float v)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0) gl.Uniform1(loc, v);
    }

    private static void SetInt(GL gl, uint program, string name, int v)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0) gl.Uniform1(loc, v);
    }

    private static void SetVec3(GL gl, uint program, string name, float x, float y, float z)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0) gl.Uniform3(loc, x, y, z);
    }

    private static void SetVec4(GL gl, uint program, string name, float x, float y, float z, float w)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0) gl.Uniform4(loc, x, y, z, w);
    }

    private unsafe byte[] ReadPixelsTopFirst(GL gl)
    {
        int stride = _width * 4;
        var pixels = new byte[stride * _height];
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = pixels)
        {
            gl.ReadPixels(
                0, 0, (uint)_width, (uint)_height,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        // GL framebuffer origin is bottom-left (row 0 = lowest gl_FragCoord.y). MonoGame's
        // RenderTarget2D.GetData is top-row-first. Flip rows so both buffers are top-first and align.
        var rowTmp = new byte[stride];
        for (int y = 0; y < _height / 2; y++)
        {
            int top = y * stride;
            int bottom = (_height - 1 - y) * stride;
            Array.Copy(pixels, top, rowTmp, 0, stride);
            Array.Copy(pixels, bottom, pixels, top, stride);
            Array.Copy(rowTmp, 0, pixels, bottom, stride);
        }

        return pixels;
    }

    // ---- GLSL assembly ------------------------------------------------------------------------

    private const string VertexSource =
        "#version 330 core\n" +
        "layout(location = 0) in vec2 aPos;\n" +
        "void main() { gl_Position = vec4(aPos, 0.0, 1.0); }\n";

    /// <summary>
    /// Wrap an original ShaderToy <c>mainImage</c> body in a minimal <c>#version 330</c> fragment
    /// shader that declares the standard ShaderToy uniforms + an <c>out vec4 fragColor</c> and a
    /// <c>main()</c> that calls <c>mainImage(c, gl_FragCoord.xy)</c>. This is exactly the ShaderToy.com
    /// runtime contract, so a body that renders on ShaderToy renders here.
    /// </summary>
    private static string BuildFragmentSource(string mainImageGlsl)
    {
        // ShaderToy bodies sometimes alias iGlobalTime/iResolution.x etc.; we only declare the
        // canonical builtins. Bodies that need custom uniforms are filtered out by the caller (they
        // would fail to link here, which we report as a SKIP rather than a fake pass).
        return
            "#version 330 core\n" +
            "precision highp float;\n" +
            "uniform vec3  iResolution;\n" +
            "uniform float iTime;\n" +
            "uniform float iTimeDelta;\n" +
            "uniform int   iFrame;\n" +
            "uniform vec4  iMouse;\n" +
            "uniform float iGlobalTime;\n" +   // legacy alias some bodies use
            "out vec4 sd_fragColor;\n" +
            "\n" +
            mainImageGlsl + "\n" +
            "\n" +
            "void main() {\n" +
            "    vec4 c = vec4(0.0);\n" +
            "    mainImage(c, gl_FragCoord.xy);\n" +
            "    sd_fragColor = c;\n" +
            "}\n";
    }

    private static (uint Program, string? Error) TryBuildProgram(GL gl, string vs, string fs)
    {
        uint v = gl.CreateShader(ShaderType.VertexShader);
        gl.ShaderSource(v, vs);
        gl.CompileShader(v);
        gl.GetShader(v, ShaderParameterName.CompileStatus, out int vok);
        if (vok == 0)
        {
            string log = gl.GetShaderInfoLog(v);
            gl.DeleteShader(v);
            return (0, "vertex: " + log.Trim());
        }

        uint f = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(f, fs);
        gl.CompileShader(f);
        gl.GetShader(f, ShaderParameterName.CompileStatus, out int fok);
        if (fok == 0)
        {
            string log = gl.GetShaderInfoLog(f);
            gl.DeleteShader(v);
            gl.DeleteShader(f);
            return (0, "fragment: " + log.Trim());
        }

        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, v);
        gl.AttachShader(prog, f);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int lok);
        gl.DetachShader(prog, v);
        gl.DetachShader(prog, f);
        gl.DeleteShader(v);
        gl.DeleteShader(f);
        if (lok == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            return (0, "link: " + log.Trim());
        }

        return (prog, null);
    }

    private void DisposeQuietly()
    {
        try { _gl?.Dispose(); } catch { /* teardown */ }
        _gl = null;
        try { _window?.Dispose(); } catch { /* teardown */ }
        _window = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_gl is not null)
        {
            try
            {
                if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
                if (_colorTex != 0) _gl.DeleteTexture(_colorTex);
                if (_vbo != 0) _gl.DeleteBuffer(_vbo);
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
            }
            catch { /* teardown */ }
        }

        DisposeQuietly();
    }
}

/// <summary>The fixed ShaderToy uniform set both the reference and the test render use.</summary>
public readonly record struct RefUniforms(
    float ResolutionX, float ResolutionY,
    float Time, float TimeDelta, int Frame,
    float MouseX, float MouseY, float MouseZ, float MouseW);
