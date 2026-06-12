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
/// Phase 34 — explicit-LOD / gradient RENDER validation (rung-4-grade, real GL driver).
///
/// <para>The sibling <see cref="Phase34TextureBreadthTests"/> proves ShadowDusk's
/// LOD/grad GLSL <i>compiles + links</i> in the real driver (rung 3). These tests
/// go one rung further for the OpenGL backend: they <b>render</b> ShadowDusk's
/// emitted <c>texture2DLod</c> / <c>texture2DGrad</c> fragment shader (the
/// MojoShader-faithful legacy spelling + guarded extension header, Phase 43 F7)
/// against a real mipmapped texture (mip 0 = White, mip 2 = Blue, mip 3 = Green)
/// in the GL 3.3 Compatibility context, read the pixels back, and assert the
/// requested mip level / gradient is actually <b>honored</b> — i.e. an explicit
/// <c>texture2DLod(…, 2.0)</c> returns mip 2 (Blue), and a large
/// <c>texture2DGrad</c> derivative selects a high mip (not mip 0).</para>
///
/// <para>This closes the rung-3→rung-4 gap for LOD/grad <i>on the OpenGL emission
/// itself</i>: it proves ShadowDusk emits a builtin whose explicit level/gradient
/// the real driver obeys, not merely one that links. (See the plan's
/// "What stays a limitation" for why the full real-<b>MonoGame</b> SpriteBatch
/// path does not surface explicit-LOD for a PS-only effect — a runtime-path
/// interaction, not a defect in this emitted GLSL, which renders correctly here.)</para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)] // shared GL fixture; see GlContextCollection
public sealed class Phase34LodGradRenderTests
{
    private readonly GlContextFixture _fixture;
    private readonly ITestOutputHelper _output;

    public Phase34LodGradRenderTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // Mip palette: level 0 White, 1 Red, 2 Blue, 3 Green — chosen so a wrong mip
    // is unmistakable. 8x8 base → levels 0..3.
    private static readonly (byte r, byte g, byte b)[] MipColors =
    {
        (255, 255, 255), (255, 0, 0), (0, 0, 255), (0, 255, 0),
    };

    private static async Task<string> ExtractFragmentAsync(string fxRel, CancellationToken ct)
    {
        string fxPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", fxRel);
        string src = await File.ReadAllTextAsync(fxPath, ct);
        var result = await new EffectCompiler().CompileAsync(src, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fxPath,
        }, ct);
        result.IsSuccess.Should().BeTrue(result.IsFailure
            ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "compile ok");
        return GlslShaderExtractor.Extract(result.Value.Data).FragmentSource;
    }

    [Fact]
    public async Task SampleLevel_EmittedTextureLod_HonorsExplicitMip2_InRealDriver()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // ShadowDusk's actual emitted PS for SampleLevel(..., 2.0). Phase 43 F7:
        // texture2DLod(ps_s0, uv, 2.0) + the guarded extension header — the
        // MojoShader-faithful form Mesa's strict front-end accepts (the generic
        // textureLod does not exist in versionless legacy GLSL).
        string ps = await ExtractFragmentAsync("examples/ExSampleLevelHidef.fx", cts.Token);
        ps.Should().Contain("texture2DLod(", "the SampleLevel fixture must emit the legacy texture2DLod");
        ps.Should().Contain("GL_ARB_shader_texture_lod", "the guarded extension header must be prepended");

        (byte r, byte g, byte b) center;
        using (_fixture.MakeContextCurrent())
            center = RenderMipProbe(_fixture.Gl, ps);

        _output.WriteLine($"SampleLevel textureLod(…,2.0) center = ({center.r},{center.g},{center.b}); mip2 = (0,0,255)");
        // The explicit LOD 2.0 must select mip 2 (Blue), NOT mip 0 (White).
        center.Should().Be(MipColors[2],
            "ShadowDusk's emitted textureLod(…, 2.0) must sample mip level 2 in the real GL driver");
    }

    [Fact]
    public void TextureGrad_LargeGradient_SelectsHighMip_InRealDriver()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }

        // Use ShadowDusk's emitted spelling (Phase 43 F7: the legacy texture2DGrad +
        // the guarded extension header, as the grad fixture now produces) with a
        // LARGE derivative so the LOD math lands on a high mip. The grad fixture
        // itself uses a deliberately tiny gradient (→ mip 0), which can't
        // distinguish "honored" from "ignored"; a large gradient can.
        const string ps =
            "#if __VERSION__ >= 300\n" +
            "#define texture2DGrad textureGrad\n" +
            "#elif defined(GL_ARB_shader_texture_lod)\n" +
            "#extension GL_ARB_shader_texture_lod : enable\n" +
            "#define texture2DGrad texture2DGradARB\n" +
            "#elif defined(GL_EXT_gpu_shader4)\n" +
            "#extension GL_EXT_gpu_shader4 : enable\n" +
            "#else\n" +
            "#define texture2DGrad(a,b,c,d) texture2D(a,b)\n" +
            "#endif\n" +
            "#define ps_oC0 gl_FragColor\n" +
            "uniform sampler2D ps_s0;\n" +
            "varying vec4 vTexCoord0;\n" +
            // dFdx/dFdy of a 0.5-scaled coord across an 8-texel base ≈ several texels,
            // pushing the computed LOD to the top of the chain.
            "void main() { ps_oC0 = texture2DGrad(ps_s0, vTexCoord0.xy, vec2(0.5, 0.0), vec2(0.0, 0.5)); }\n";

        (byte r, byte g, byte b) center;
        using (_fixture.MakeContextCurrent())
            center = RenderMipProbe(_fixture.Gl, ps);

        _output.WriteLine($"textureGrad(large) center = ({center.r},{center.g},{center.b}); mip0 = (255,255,255)");
        // A large gradient must NOT resolve to mip 0 (White) — it must select a higher
        // (smaller) mip. (We don't pin the exact level — driver LOD rounding varies —
        // only that the gradient is HONORED, i.e. it left mip 0.)
        center.Should().NotBe(MipColors[0],
            "a large textureGrad derivative must select a higher mip, not mip 0");
    }

    /// <summary>
    /// Renders <paramref name="fragmentSource"/> over a fullscreen quad whose UV is
    /// constant (0.5) — so screen-space derivatives are ~0 and AUTOMATIC LOD would
    /// pick mip 0. Any non-mip-0 result therefore comes from the shader's EXPLICIT
    /// LOD/gradient, isolating "honored" from "auto". Returns the center texel.
    /// </summary>
    private static (byte r, byte g, byte b) RenderMipProbe(GL gl, string fragmentSource)
    {
        using var fbo = new OffscreenRenderer(gl);
        fbo.Clear(0, 0, 0, 255);

        // Mipmapped 8x8 texture with the distinct per-level palette.
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        int level = 0;
        for (int s = 8; s >= 1; s /= 2)
        {
            var (r, g, b) = MipColors[System.Math.Min(level, MipColors.Length - 1)];
            var data = new byte[s * s * 4];
            for (int i = 0; i < s * s; i++) { data[i * 4] = r; data[i * 4 + 1] = g; data[i * 4 + 2] = b; data[i * 4 + 3] = 255; }
            unsafe
            {
                fixed (byte* p = data)
                    gl.TexImage2D(TextureTarget.Texture2D, level, InternalFormat.Rgba8,
                        (uint)s, (uint)s, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
            level++;
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, level - 1);

        // Pair the PS with the MojoShader-dialect passthrough VS (emits vTexCoord0).
        string vs = PassthroughVertexShader.MojoShaderDialect;
        using var prog = GlslShaderProgram.Compile(gl, vs, fragmentSource);
        prog.Use(gl);

        // Fullscreen quad; constant UV 0.5 → zero derivatives → auto-LOD = mip 0.
        float[] verts =
        {
            -1f,-1f,0f, 1f,1f,1f,1f, 0.5f,0.5f,
             1f,-1f,0f, 1f,1f,1f,1f, 0.5f,0.5f,
             1f, 1f,0f, 1f,1f,1f,1f, 0.5f,0.5f,
            -1f, 1f,0f, 1f,1f,1f,1f, 0.5f,0.5f,
        };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };

        uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe { fixed (float* p = verts) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * 4), p, BufferUsageARB.StaticDraw); }
        uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        unsafe { fixed (uint* p = idx) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * 4), p, BufferUsageARB.StaticDraw); }

        const uint stride = 9 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0); }       // vs_v0 POSITION
        gl.EnableVertexAttribArray(2);
        unsafe { gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float))); } // vs_v2 TEXCOORD0

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        int sLoc = gl.GetUniformLocation(prog.Handle, "ps_s0");
        if (sLoc >= 0) gl.Uniform1(sLoc, 0);

        gl.Disable(EnableCap.DepthTest);
        unsafe { gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, (void*)0); }
        gl.Finish();

        byte[] all = fbo.ReadPixels();
        gl.DeleteVertexArray(vao); gl.DeleteBuffer(vbo); gl.DeleteBuffer(ebo); gl.DeleteTexture(tex);

        int c = (OffscreenRenderer.Height / 2 * OffscreenRenderer.Width + OffscreenRenderer.Width / 2) * 4;
        return (all[c], all[c + 1], all[c + 2]);
    }
}
