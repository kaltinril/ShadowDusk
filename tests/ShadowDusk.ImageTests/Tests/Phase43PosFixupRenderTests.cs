#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ImageTests.GlContext;
using ShadowDusk.ImageTests.Rendering;
using Silk.NET.OpenGL;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Phase 43 F3 — the dynamic <c>posFixup</c> contract, validated two ways.
///
/// <para><b>String-decisive (vs the mgfxc golden):</b> the OpenGL golden
/// <c>VsTransformColorTexture.mgfx</c> VS carries the exact MojoShader form —
/// <c>uniform vec4 posFixup;</c> +
/// <c>gl_Position.y = gl_Position.y * posFixup.y;</c> +
/// <c>gl_Position.xy += posFixup.zw * gl_Position.ww;</c>. ShadowDusk's emitted VS
/// must contain the same lines (the golden's lines are READ from the golden, not
/// hardcoded here).</para>
///
/// <para><b>Render-decisive (backbuffer proxy):</b> MonoGame 3.8.2 sets
/// <c>posFixup.y = +1</c> for the backbuffer and <c>-1</c> only when a render
/// target is bound (<c>GraphicsDevice.OpenGL.cs</c>, <c>ActivateShaderProgram</c>).
/// The pre-Phase-43 static <c>FlipVertexY</c> matched only the render-target case —
/// the backbuffer case rendered vertically inverted. A true headless default-
/// framebuffer readback is not reliable in this harness (pixel-ownership of a
/// hidden window is undefined), so the honest proxy is: render the SAME effect with
/// the render-target fixup (y = -1) and the backbuffer fixup (y = +1) and assert
/// the orientation flips, AND assert the render-target case is pixel-equivalent to
/// the mgfxc golden's VS+PS rendered identically (same-backend GL↔GL). The
/// real-runtime backbuffer evidence is <c>validation/VsDriven</c>'s backbuffer mode
/// (real MonoGame <c>GetBackBufferData</c>).</para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)]
public sealed class Phase43PosFixupRenderTests
{
    private readonly GlContextFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public Phase43PosFixupRenderTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    private static async Task<byte[]> CompileFixtureAsync(CancellationToken ct)
    {
        string repoRoot = TestPaths.FindRepoRoot();
        string fxPath   = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "VsTransformColorTexture.fx");
        string src      = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        }, ct);

        result.IsSuccess.Should().BeTrue(result.IsFailure
            ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "compile ok");
        return result.Value.Data;
    }

    private static (string Vs, string Ps) ReadGoldenPair()
    {
        string repoRoot   = TestPaths.FindRepoRoot();
        string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL", "VsTransformColorTexture.mgfx");
        var reader        = MgfxcMgfxReader.Parse(File.ReadAllBytes(goldenPath));

        string vs = reader.GlslShaders.Single(s => s.Contains("attribute ", StringComparison.Ordinal));
        string ps = reader.GlslShaders.Single(s => !s.Contains("attribute ", StringComparison.Ordinal));
        return (vs, ps);
    }

    [Fact]
    public async Task EmittedVs_ContainsGoldenPosFixupForm_NoStaticFlip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        byte[] sdMgfx           = await CompileFixtureAsync(cts.Token);
        GlslShaderPair sd       = GlslShaderExtractor.Extract(sdMgfx);
        (string goldenVs, _)    = ReadGoldenPair();

        sd.VertexSource.Should().NotBeNull("the VS-driven fixture must ship a compiled VS");
        string sdVs = sd.VertexSource!;

        // The golden's posFixup lines, READ from the golden itself (trimmed —
        // mgfxc indents with tabs, ShadowDusk follows SPIRV-Cross's spacing).
        string[] goldenFixupLines = goldenVs
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("posFixup", StringComparison.Ordinal))
            .ToArray();

        goldenFixupLines.Should().BeEquivalentTo(new[]
        {
            "uniform vec4 posFixup;",
            "gl_Position.y = gl_Position.y * posFixup.y;",
            "gl_Position.xy += posFixup.zw * gl_Position.ww;",
        }, "the golden is the oracle for the exact posFixup form");

        string[] sdLines = sdVs.Split('\n').Select(l => l.Trim()).ToArray();
        foreach (string goldenLine in goldenFixupLines)
        {
            sdLines.Should().Contain(goldenLine,
                $"ShadowDusk's VS must carry the golden's posFixup form line '{goldenLine}'");
        }

        sdVs.Should().NotContain("-gl_Position.y",
            "the static FlipVertexY negation must be gone — the flip is posFixup's job");
    }

    [Fact]
    public async Task RenderTargetFixup_MatchesGolden_AndBackbufferFixup_FlipsOrientation()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        byte[] sdMgfx        = await CompileFixtureAsync(cts.Token);
        GlslShaderPair sd    = GlslShaderExtractor.Extract(sdMgfx);
        (string gVs, string gPs) = ReadGoldenPair();

        byte[] sdRt, sdBackbuffer, goldenRt;
        using (_fixture.MakeContextCurrent())
        {
            sdRt         = RenderQuad(_fixture.Gl, sd.VertexSource!, sd.FragmentSource, renderTargetBound: true);
            sdBackbuffer = RenderQuad(_fixture.Gl, sd.VertexSource!, sd.FragmentSource, renderTargetBound: false);
            goldenRt     = RenderQuad(_fixture.Gl, gVs, gPs, renderTargetBound: true);
        }

        // (a) Render-target case == the mgfxc golden, same-backend (this is also the
        // pre-Phase-43 static-flip image: y = -1 reproduces the baked negation).
        var cmp = ImageComparer.Compare(goldenRt, sdRt, tolerance: 4);
        cmp.Matches.Should().BeTrue(
            "with the render-target fixup (y = -1) ShadowDusk must render pixel-equivalent " +
            $"to the mgfxc golden; diff {cmp.DifferentPixels}/{cmp.TotalPixels}, maxd {cmp.MaxChannelDelta}");

        // (b) Backbuffer case must be the VERTICAL MIRROR of the render-target case —
        // the orientation genuinely responds to posFixup.y (the exact behavior the
        // static flip could not provide).
        byte[] sdRtMirrored = FlipRows(sdRt, OffscreenRenderer.Width, OffscreenRenderer.Height);
        var mirrorCmp = ImageComparer.Compare(sdRtMirrored, sdBackbuffer, tolerance: 4);
        mirrorCmp.Matches.Should().BeTrue(
            "the backbuffer fixup (y = +1) must render the vertical mirror of the render-target " +
            $"fixup (y = -1); diff {mirrorCmp.DifferentPixels}/{mirrorCmp.TotalPixels}, maxd {mirrorCmp.MaxChannelDelta}");

        // (c) And the two cases must actually DIFFER (the test texture is vertically
        // asymmetric, so identical images would mean posFixup is dead).
        ImageComparer.Compare(sdRt, sdBackbuffer, tolerance: 4).Matches.Should().BeFalse(
            "y = +1 and y = -1 must produce different orientations for an asymmetric scene");
    }

    private static byte[] FlipRows(byte[] rgba, int width, int height)
    {
        var flipped = new byte[rgba.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            Array.Copy(rgba, y * stride, flipped, (height - 1 - y) * stride, stride);
        return flipped;
    }

    /// <summary>
    /// Renders the VS+PS pair over the standard quad with WorldViewProjection =
    /// identity, Tint = white, a vertically-asymmetric texture (top half red,
    /// bottom half blue), and the posFixup value real MonoGame would set for the
    /// given target case (no half-pixel offset — MonoGame's default).
    /// </summary>
    private static byte[] RenderQuad(GL gl, string vs, string ps, bool renderTargetBound)
    {
        using var fbo = new OffscreenRenderer(gl);
        fbo.Clear(0, 0, 0, 255);

        using var prog = GlslShaderProgram.Compile(gl, vs, ps);
        prog.Use(gl);

        // The uniform contract both compilers share: identity matrix columns in
        // vs_uniforms_vec4[0..3] (this test isolates posFixup, so identity is fine; the
        // non-identity matrix transform is render-pinned separately by
        // Issue70MatrixTransposeRenderTests), white tint in [4].
        SetVec4(gl, prog.Handle, "vs_uniforms_vec4[0]", 1, 0, 0, 0);
        SetVec4(gl, prog.Handle, "vs_uniforms_vec4[1]", 0, 1, 0, 0);
        SetVec4(gl, prog.Handle, "vs_uniforms_vec4[2]", 0, 0, 1, 0);
        SetVec4(gl, prog.Handle, "vs_uniforms_vec4[3]", 0, 0, 0, 1);
        SetVec4(gl, prog.Handle, "vs_uniforms_vec4[4]", 1, 1, 1, 1);

        MojoPosFixup.Apply(gl, prog.Handle, renderTargetBound,
            OffscreenRenderer.Width, OffscreenRenderer.Height);

        // Vertically-asymmetric 8x8 texture: rows 0..3 red, rows 4..7 blue.
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        var data = new byte[8 * 8 * 4];
        for (int yy = 0; yy < 8; yy++)
        for (int xx = 0; xx < 8; xx++)
        {
            int o = (yy * 8 + xx) * 4;
            data[o]     = (byte)(yy < 4 ? 255 : 0);
            data[o + 2] = (byte)(yy < 4 ? 0 : 255);
            data[o + 3] = 255;
        }
        unsafe
        {
            fixed (byte* p = data)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                    8, 8, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        int sLoc = gl.GetUniformLocation(prog.Handle, "ps_s0");
        if (sLoc >= 0) gl.Uniform1(sLoc, 0);

        // Quad: POSITION (vs_v0), COLOR0 white (vs_v1), TEXCOORD0 (vs_v2),
        // uv (0,0) at the TOP-left vertex.
        float[] verts =
        {
            // pos.xyz        color.rgba      uv.xy
            -1f, -1f, 0f,     1f,1f,1f,1f,    0f, 1f, // bottom-left
             1f, -1f, 0f,     1f,1f,1f,1f,    1f, 1f, // bottom-right
             1f,  1f, 0f,     1f,1f,1f,1f,    1f, 0f, // top-right
            -1f,  1f, 0f,     1f,1f,1f,1f,    0f, 0f, // top-left
        };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };

        uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe { fixed (float* p = verts) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * 4), p, BufferUsageARB.StaticDraw); }
        uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        unsafe { fixed (uint* p = idx) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * 4), p, BufferUsageARB.StaticDraw); }

        const uint stride = 9 * sizeof(float);
        BindAttrib(gl, prog.Handle, "vs_v0", 3, stride, 0);
        BindAttrib(gl, prog.Handle, "vs_v1", 4, stride, 3 * sizeof(float));
        BindAttrib(gl, prog.Handle, "vs_v2", 2, stride, 7 * sizeof(float));

        gl.Disable(EnableCap.DepthTest);
        unsafe { gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, (void*)0); }
        gl.Finish();

        byte[] pixels = fbo.ReadPixels();
        gl.BindVertexArray(0);
        gl.DeleteVertexArray(vao); gl.DeleteBuffer(vbo); gl.DeleteBuffer(ebo); gl.DeleteTexture(tex);
        return pixels;
    }

    private static void SetVec4(GL gl, uint program, string name, float x, float y, float z, float w)
    {
        int loc = gl.GetUniformLocation(program, name);
        loc.Should().BeGreaterThanOrEqualTo(0, $"uniform '{name}' must be active in the program");
        gl.Uniform4(loc, x, y, z, w);
    }

    private static void BindAttrib(GL gl, uint program, string name, int size, uint stride, int byteOffset)
    {
        int loc = gl.GetAttribLocation(program, name);
        loc.Should().BeGreaterThanOrEqualTo(0, $"attribute '{name}' must be active in the program");
        gl.EnableVertexAttribArray((uint)loc);
        unsafe
        {
            gl.VertexAttribPointer((uint)loc, size, VertexAttribPointerType.Float,
                normalized: false, stride, (void*)byteOffset);
        }
    }
}
