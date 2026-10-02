#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.ImageTests.GlContext;
using ShadowDusk.ImageTests.Rendering;
using Silk.NET.OpenGL;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Issue #215 follow-up: rewriter Rule 16 covers <c>tan</c> as well as <c>sin</c>/<c>cos</c>.
/// The first cut left <c>tan</c> on the raw argument on the recorded claim that fxc has
/// no D3D9 <c>tan</c> to mirror. Measured: <c>fxc /T ps_3_0</c> lowers
/// <c>tan(input.TexCoord.x * 800.0 * scale)</c> to <c>mad / frc / mad / sincos / rcp /
/// mul</c>, the same range reduction followed by sin/cos, so mgfxc's GLSL never takes the
/// tangent of a large value and ShadowDusk's must not either.
///
/// <para>No shader in the byte-identity corpus calls <c>tan</c> (Apos.Shapes does, on a
/// half-angle that is always small), so this is the end-to-end cover: a <c>tan</c>-ONLY
/// shader reaching ~800 rad goes through the real OpenGL pipeline (DXC, SPIRV-Cross, the
/// managed rewrite), and the emitted fragment shader is compiled, linked and RENDERED in
/// the real GL driver, then compared per pixel against a double-precision reference.</para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)]
public sealed class Issue215TanRangeReductionTests
{
    // Argument = TexCoord.x * 800: 0 to 800 rad across the quad, 6.25 rad per pixel of the
    // 128-wide target. The tangent itself is unbounded at its poles, so the colour encodes
    // it through the double-angle identities, which are smooth and bounded for every t:
    //   R = 0.5 + t / (1 + t*t)                 = 0.5 + 0.5 * sin(2x)
    //   G = 0.5 + 0.5 * (1 - t*t) / (1 + t*t)   = 0.5 + 0.5 * cos(2x)
    // atan2 / atan ride along in blue (not asserted on) to pin that the real pipeline's
    // spelling of them (GLSL `atan` for both) is left alone. tanh is deliberately absent:
    // GLSL 1.10 / ES 1.00 have no tanh (the SD0403 portability warning), so it would not
    // link on a strict driver; the pure rewriter tests cover that lookalike.
    private const string TanSource = """
struct VSOut { float4 Position : POSITION; float2 TexCoord : TEXCOORD0; };

float4 PS(VSOut input) : COLOR
{
    float t = tan(input.TexCoord.x * 800.0);
    float d = 1.0 + t * t;
    float lookalikes = atan2(input.TexCoord.y, input.TexCoord.x + 2.0) + atan(input.TexCoord.y);
    return float4(0.5 + t / d, 0.5 + 0.5 * (1.0 - t * t) / d, saturate(lookalikes * 0.5), 1.0);
}

technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
""";

    private readonly GlContextFixture _fixture;
    private readonly ITestOutputHelper _output;

    public Issue215TanRangeReductionTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static async Task<string> CompileFragmentAsync(CancellationToken ct)
    {
        var result = await new EffectCompiler().CompileAsync(TanSource, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Issue215Tan.fx",
        }, ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "compile ok");
        return GlslShaderExtractor.Extract(result.Value.Data).FragmentSource;
    }

    [Fact]
    public async Task TanOnlyShader_ArgumentIsReduced_AndTheHelperIsDeclared()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string ps = await CompileFragmentAsync(cts.Token);
        _output.WriteLine(ps);

        ps.ShouldContain("tan(sd_reduce_angle(", Case.Sensitive);
        // The shader calls neither sin nor cos: the helper must be emitted for tan alone.
        System.Text.RegularExpressions.Regex.IsMatch(ps, @"\b(sin|cos)\s*\(").ShouldBeFalse(
            "the fixture must stay tan-only, or it no longer proves the helper is emitted for tan alone");
        int helperDecl = ps.IndexOf("float sd_reduce_angle(float x)", StringComparison.Ordinal);
        helperDecl.ShouldBeGreaterThan(0, "the Rule 16 helper must be emitted for a tan-only shader");
        ps.IndexOf("void main()", StringComparison.Ordinal).ShouldBeGreaterThan(helperDecl);

        // Every tan call is reduced; atan (GLSL's spelling of atan2 too) is not.
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(ps, @"\btan\s*\(([^()]*)"))
        {
            m.Groups[1].Value.ShouldBe("sd_reduce_angle", $"unreduced tan call: {m.Value}");
        }
        ps.ShouldContain("atan(", Case.Sensitive, "the fixture's atan/atan2 must survive to the GLSL");
        ps.ShouldNotContain("atan(sd_reduce_angle", Case.Sensitive);
    }

    [Fact]
    public async Task TanOnlyShader_CompilesLinksAndRendersTheTangent_InRealDriver()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string ps = await CompileFragmentAsync(cts.Token);

        byte[] pixels;
        using (_fixture.MakeContextCurrent())
        {
            // Throws GlslCompileException (with the driver's info log) on a compile or
            // link failure.
            pixels = RenderUnitQuad(_fixture.Gl, ps);
        }

        // One row is enough: the shader depends on x only. Pixel i's centre is
        // u = (i + 0.5) / Width, so the argument there is exactly 800 * u.
        const int row = OffscreenRenderer.Height / 2;
        int worstR = 0, worstG = 0;
        for (int i = 0; i < OffscreenRenderer.Width; i++)
        {
            double x = 800.0 * (i + 0.5) / OffscreenRenderer.Width;
            int expectedR = (int)Math.Round(255.0 * (0.5 + 0.5 * Math.Sin(2.0 * x)));
            int expectedG = (int)Math.Round(255.0 * (0.5 + 0.5 * Math.Cos(2.0 * x)));
            int at = (row * OffscreenRenderer.Width + i) * 4;
            worstR = Math.Max(worstR, Math.Abs(pixels[at] - expectedR));
            worstG = Math.Max(worstG, Math.Abs(pixels[at + 1] - expectedG));
        }

        _output.WriteLine($"tan up to 800 rad vs double-precision reference: max diff R={worstR} G={worstG} (of 255)");
        // 4/255 is the render gates' own tolerance. A reduction that lost the phase (a
        // wrong constant, a missing second term) is off by tens of levels here: the
        // colour moves 127.5 levels per radian of phase error.
        worstR.ShouldBeLessThanOrEqualTo(4, "red (0.5 + 0.5 sin 2x) must match the reference tangent");
        worstG.ShouldBeLessThanOrEqualTo(4, "green (0.5 + 0.5 cos 2x) must match the reference tangent");
    }

    /// <summary>
    /// Draws <paramref name="fragmentSource"/> over a full-target quad whose TEXCOORD0
    /// runs 0..1 in both axes, and returns the RGBA8 read-back.
    /// </summary>
    private static byte[] RenderUnitQuad(GL gl, string fragmentSource)
    {
        using var fbo = new OffscreenRenderer(gl);
        fbo.Clear(0, 0, 0, 255);

        using var program = GlslShaderProgram.Compile(
            gl, PassthroughVertexShader.PickFor(fragmentSource), fragmentSource);
        program.Handle.ShouldNotBe(0u);
        program.Use(gl);

        // position (3), colour (4), texcoord (2)
        float[] verts =
        {
            -1f, -1f, 0f,  1f, 1f, 1f, 1f,  0f, 0f,
             1f, -1f, 0f,  1f, 1f, 1f, 1f,  1f, 0f,
             1f,  1f, 0f,  1f, 1f, 1f, 1f,  1f, 1f,
            -1f,  1f, 0f,  1f, 1f, 1f, 1f,  0f, 1f,
        };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };

        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe
        {
            fixed (float* p = verts)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }
        uint ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        unsafe
        {
            fixed (uint* p = idx)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        }

        // Bind by NAME so the test holds for either passthrough dialect.
        const uint stride = 9 * sizeof(float);
        int posLoc = AttribLocation(gl, program.Handle, "vs_v0", "in_var_POSITION");
        int uvLoc = AttribLocation(gl, program.Handle, "vs_v2", "in_var_TEXCOORD0");
        posLoc.ShouldBeGreaterThanOrEqualTo(0, "the passthrough vertex shader's position attribute");
        uvLoc.ShouldBeGreaterThanOrEqualTo(0, "the passthrough vertex shader's texcoord attribute");
        gl.EnableVertexAttribArray((uint)posLoc);
        unsafe { gl.VertexAttribPointer((uint)posLoc, 3, VertexAttribPointerType.Float, false, stride, (void*)0); }
        gl.EnableVertexAttribArray((uint)uvLoc);
        unsafe { gl.VertexAttribPointer((uint)uvLoc, 2, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float))); }

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        unsafe { gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, (void*)0); }
        gl.Finish();

        byte[] all = fbo.ReadPixels();
        gl.DeleteVertexArray(vao);
        gl.DeleteBuffer(vbo);
        gl.DeleteBuffer(ebo);
        return all;
    }

    private static int AttribLocation(GL gl, uint program, params string[] names)
    {
        foreach (string name in names)
        {
            int loc = gl.GetAttribLocation(program, name);
            if (loc >= 0)
            {
                return loc;
            }
        }
        return -1;
    }
}
