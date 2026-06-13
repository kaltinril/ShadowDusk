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
/// Issue #70 — a custom vertex shader's <c>mul(position, WorldViewProjection)</c> must
/// render the SAME geometry as the mgfxc golden in the real runtime, for a
/// <b>non-identity, asymmetric</b> matrix.
///
/// <para><b>Why this test exists.</b> The Phase 28 VS-driven validation and
/// <see cref="Phase43PosFixupRenderTests"/> only ever uploaded the IDENTITY matrix
/// (<c>validation/Shared/VsEffectImageRenderer.cs</c> sets
/// <c>WorldViewProjection = Matrix.Identity</c>; this file's sibling pins identity columns
/// into <c>vs_uniforms_vec4[0..3]</c> with the note "identity is symmetric, so fxc row-major
/// vs SPIRV-Cross column-major layouts agree"). Identity is transpose-invariant, so the entire
/// corpus was BLIND to a transposed-matrix bug — which is exactly what issue #70 was: the
/// dialect rewriter reconstructed <c>mat4(reg0..reg3)</c> with the registers as COLUMNS, while
/// MonoGame/KNI upload register k = column k and SPIRV-Cross swaps the multiply operands, so a
/// real (asymmetric) WorldViewProjection rendered transposed/garbled (the reporter's "exploded
/// cube"). The fix reconstructs the matrix transposed (<c>BuildUploadedMat4</c>).</para>
///
/// <para><b>What this proves (rung 4, same-backend GL↔GL).</b> Both the mgfxc golden VS and
/// ShadowDusk's VS read the SAME uploaded registers. With an asymmetric matrix uploaded as its
/// columns (MonoGame's <c>SetValue(Matrix)</c> convention), ShadowDusk must render
/// pixel-equivalent to the golden. The test is proven NON-VACUOUS by also rendering the golden
/// with the matrix's TRANSPOSE columns and asserting the image genuinely differs — i.e. the
/// scene is transpose-sensitive, so the pre-fix rewriter (which produced exactly that
/// transposed image) WOULD have failed this test.</para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)]
public sealed class Issue70MatrixTransposeRenderTests
{
    private readonly GlContextFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public Issue70MatrixTransposeRenderTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    // An asymmetric WorldViewProjection (M != Mᵀ), authored row-major (XNA Matrix order):
    //   [ 0.5  0.2  0    0   ]
    //   [ 0    0.5  0    0   ]
    //   [ 0    0    1    0   ]
    //   [ 0.1  0.3  0    1   ]
    // It shears + offsets the clip-space quad while keeping it on-screen and non-degenerate.
    // MonoGame/KNI's SetValue(Matrix) uploads each COLUMN into one register:
    private static readonly float[][] AsymmetricColumns =
    {
        new[] { 0.5f, 0.0f, 0.0f, 0.1f }, // column 0
        new[] { 0.2f, 0.5f, 0.0f, 0.3f }, // column 1
        new[] { 0.0f, 0.0f, 1.0f, 0.0f }, // column 2
        new[] { 0.0f, 0.0f, 0.0f, 1.0f }, // column 3
    };

    // The TRANSPOSE uploaded as columns == the original matrix's ROWS. Rendering the golden
    // with these is what the PRE-FIX (transposing) rewriter effectively produced from the
    // AsymmetricColumns upload — used here only to prove the scene is transpose-sensitive.
    private static readonly float[][] TransposedColumns =
    {
        new[] { 0.5f, 0.2f, 0.0f, 0.0f }, // row 0 of M
        new[] { 0.0f, 0.5f, 0.0f, 0.0f }, // row 1 of M
        new[] { 0.0f, 0.0f, 1.0f, 0.0f }, // row 2 of M
        new[] { 0.1f, 0.3f, 0.0f, 1.0f }, // row 3 of M
    };

    [Fact]
    public async Task NonIdentityMatrix_ShadowDuskRendersEquivalentToMgfxcGolden_Issue70()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        byte[] sdMgfx         = await CompileFixtureAsync(cts.Token);
        GlslShaderPair sd     = GlslShaderExtractor.Extract(sdMgfx);
        (string gVs, string gPs) = ReadGoldenPair();

        byte[] goldenImg, shadowDuskImg, goldenTransposedImg;
        using (_fixture.MakeContextCurrent())
        {
            goldenImg           = RenderQuad(_fixture.Gl, gVs, gPs, AsymmetricColumns);
            shadowDuskImg       = RenderQuad(_fixture.Gl, sd.VertexSource!, sd.FragmentSource, AsymmetricColumns);
            goldenTransposedImg = RenderQuad(_fixture.Gl, gVs, gPs, TransposedColumns);
        }

        // (a) THE FIX: ShadowDusk's VS, given the asymmetric matrix's columns, renders
        // pixel-equivalent to the mgfxc golden. (Pre-fix, this produced goldenTransposedImg.)
        var cmp = ImageComparer.Compare(goldenImg, shadowDuskImg, tolerance: 4);
        cmp.Matches.Should().BeTrue(
            "for a non-identity WorldViewProjection, ShadowDusk's vertex transform must match the " +
            $"mgfxc golden (same-backend GL↔GL); diff {cmp.DifferentPixels}/{cmp.TotalPixels}, " +
            $"maxd {cmp.MaxChannelDelta}");

        // (b) NON-VACUOUS: the asymmetric matrix and its transpose render DIFFERENT images, so a
        // transposed-matrix regression genuinely changes pixels (this scene can catch it).
        var transposeCmp = ImageComparer.Compare(goldenImg, goldenTransposedImg, tolerance: 4);
        transposeCmp.Matches.Should().BeFalse(
            "the asymmetric matrix must render differently from its transpose, else the test could " +
            "not distinguish the issue #70 bug from the fix");
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

    /// <summary>
    /// Renders the VS+PS pair over the standard quad with WorldViewProjection supplied as
    /// four column registers (<paramref name="matrixColumns"/>) — the MonoGame/KNI upload
    /// layout — Tint = white, the same vertically-asymmetric texture and render-target posFixup
    /// as the Phase 43 harness, so the only thing under test is the matrix transform.
    /// </summary>
    private static byte[] RenderQuad(GL gl, string vs, string ps, float[][] matrixColumns)
    {
        using var fbo = new OffscreenRenderer(gl);
        fbo.Clear(0, 0, 0, 255);

        using var prog = GlslShaderProgram.Compile(gl, vs, ps);
        prog.Use(gl);

        for (int i = 0; i < 4; i++)
            SetVec4(gl, prog.Handle, $"vs_uniforms_vec4[{i}]",
                matrixColumns[i][0], matrixColumns[i][1], matrixColumns[i][2], matrixColumns[i][3]);
        SetVec4(gl, prog.Handle, "vs_uniforms_vec4[4]", 1, 1, 1, 1); // Tint = white

        MojoPosFixup.Apply(gl, prog.Handle, renderTargetBound: true,
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

        // Quad: POSITION (vs_v0), COLOR0 white (vs_v1), TEXCOORD0 (vs_v2).
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
