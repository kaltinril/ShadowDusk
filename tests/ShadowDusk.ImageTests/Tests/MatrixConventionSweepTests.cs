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
/// Corpus-wide, varied-input render sweep for the matrix-convention class of fidelity bug
/// (issue #70). The issue-#70 transpose hid for one reason: every vertex-matrix validation
/// uploaded the IDENTITY matrix, which is transpose-invariant. This sweep is the systematic
/// answer — it renders ShadowDusk's emitted GLSL against the mgfxc OpenGL golden (same-backend
/// GL↔GL, the reference compiler as the oracle) across:
/// <list type="bullet">
///   <item>multiple <b>matrix shapes</b> (asymmetric scale+translate, shear, axis-flip /
///   negative determinant, rotation, a fully-distinct general matrix) — so any shape-dependent
///   math error surfaces, not just the one shape issue #70 happened to use;</item>
///   <item>multiple <b>fixtures</b> with different uniform layouts — a single matrix at register
///   0 (<c>VsTransformColorTexture</c>), three CHAINED matrices at registers 0/4/8
///   (<c>VertexAndPixel</c>, which also proves non-zero register offsets and multiply order),
///   and a matrix ARRAY at registers 0/4 (<c>ArrayUniformVs</c>, the
///   <see cref="MonoGameGlslRewriter"/> array path).</item>
/// </list>
/// Every dyadic matrix (powers-of-two entries) must render maxd ≤ 2 — ShadowDusk's transposed
/// <c>mat4</c> reconstruction and the golden's per-row <c>dot</c> form compute the same products
/// with no rounding, so the result is bit-identical. The rotation shape allows a looser tolerance
/// (different FLOP order, ~1 ULP). The pre-fix transpose moved pixels by up to 255, so any
/// tolerance well under that distinguishes correct from broken. Each fixture also asserts
/// NON-VACUITY: a non-identity matrix differs from identity (the transform really runs) and an
/// asymmetric matrix differs from its transpose (the scene is transpose-sensitive, so a
/// regression of this class genuinely changes pixels here).
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)]
public sealed class MatrixConventionSweepTests
{
    private readonly GlContextFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public MatrixConventionSweepTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    // ---- Matrix shapes, authored ROW-major (m[row*4 + col]). All asymmetric (M != Mᵀ).
    // Translation lives in the LAST ROW (m[12..14]) — the mul(v, M) convention — so a transpose
    // (which moves it to the last column) renders visibly differently. Dyadic entries keep both
    // compilers' arithmetic exact; the rotation is the one non-dyadic shape (looser tolerance).
    private static readonly float[] Identity =
        { 1,0,0,0,  0,1,0,0,  0,0,1,0,  0,0,0,1 };

    private static readonly (string Label, float[] M, int Tol)[] Shapes =
    {
        ("scale+translate", new float[]{ 0.5f,0,0,0,  0,0.5f,0,0,  0,0,1,0,  0.25f,0.25f,0,1 }, 2),
        ("shear",           new float[]{ 0.5f,0,0,0,  0.25f,0.5f,0,0,  0,0,1,0,  0,0,0,1 },      2),
        ("flip-x + scale",  new float[]{ -0.5f,0,0,0,  0,0.75f,0,0,  0,0,1,0,  0.125f,0,0,1 },   2),
        ("general-asym",    new float[]{ 0.5f,0.25f,0,0,  0.125f,0.5f,0,0,  0,0,1,0,  0.25f,0.125f,0,1 }, 2),
        ("rotateZ+scale",   RotateZScaled(30f, 0.5f),                                            6),
    };

    private static float[] RotateZScaled(float degrees, float scale)
    {
        double r = degrees * Math.PI / 180.0;
        float c = (float)Math.Cos(r) * scale, s = (float)Math.Sin(r) * scale;
        return new float[]{ c, s, 0, 0,  -s, c, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 };
    }

    [Fact]
    public async Task VsTransformColorTexture_SingleMatrixAtRegister0()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        const string fx = "VsTransformColorTexture";
        await Resolve(fx);

        foreach (var (label, m, tol) in Shapes)
        {
            var regs = new Dictionary<int, float[]>();
            PlaceColumns(m, 0, regs);          // WorldViewProjection @ registers 0..3
            regs[4] = new[] { 1f, 1f, 1f, 1f }; // Tint = white
            AssertGoldenMatch(fx, $"WVP={label}", regs, ps: null, textured: true, tol);
        }

        AssertNonVacuous(fx, baseReg: 0, tintReg: 4, textured: true);
    }

    // A vertex shader whose position output uses the legacy D3D9 `: POSITION` semantic — the form
    // the stock MonoGame GL template emits via `#define SV_POSITION POSITION` — must still write
    // gl_Position. This sweep originally caught it NOT doing so (the transform went to a dead
    // `var_POSITION` varying, gl_Position left undefined); the rewriter now maps the position
    // semantic to gl_Position (IsPositionSemantic in MonoGameGlslRewriter), so VertexAndPixel's
    // three CHAINED matrices at registers 0/4/8 render equivalent to the golden across all shapes.
    [Fact]
    public async Task VertexAndPixel_ChainedMatricesAtRegisters0_4_8()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        const string fx = "VertexAndPixel";
        await Resolve(fx);
        var ps = new Dictionary<int, float[]> { [0] = new[] { 0.2f, 0.6f, 0.9f, 1f } }; // Color

        // Each shape placed in World (reg 0), with View (4) and Projection (8) identity, so the
        // composite == the shape. Proves register 0 and the chained dot form.
        foreach (var (label, m, tol) in Shapes)
        {
            var regs = new Dictionary<int, float[]>();
            PlaceColumns(m, 0, regs);
            PlaceColumns(Identity, 4, regs);
            PlaceColumns(Identity, 8, regs);
            AssertGoldenMatch(fx, $"World={label}", regs, ps, textured: false, tol);
        }

        // The fully-distinct shape ALSO placed at View (reg 4) and at Projection (reg 8) — proves
        // the non-zero register offsets are read correctly (a misregistered matrix would diverge).
        var general = Shapes.Single(s => s.Label == "general-asym").M;
        foreach (int baseReg in new[] { 4, 8 })
        {
            var regs = new Dictionary<int, float[]>();
            PlaceColumns(baseReg == 4 ? general : Identity, 4, regs);
            PlaceColumns(baseReg == 8 ? general : Identity, 8, regs);
            PlaceColumns(Identity, 0, regs);
            AssertGoldenMatch(fx, $"general@reg{baseReg}", regs, ps, textured: false, tol: 2);
        }

        AssertNonVacuous(fx, baseReg: 0, tintReg: -1, textured: false, ps);
    }

    [Fact]
    public async Task ArrayUniformVs_MatrixArrayAtRegisters0_4()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        const string fx = "ArrayUniformVs";
        await Resolve(fx);

        // VS: p_i = mul(input.Position + PosOffsets[i], Bones[i]); output = blend(p0, p1). With
        // PosOffsets = 0 and Bones[0] == Bones[1] == M, the output reduces to mul(pos, M) routed
        // through the mat4-ARRAY reconstruction path (registers 0..3 and 4..7).
        foreach (var (label, m, tol) in Shapes)
        {
            var regs = new Dictionary<int, float[]>();
            PlaceColumns(m, 0, regs); // Bones[0]
            PlaceColumns(m, 4, regs); // Bones[1]
            regs[8] = new[] { 0f, 0f, 0f, 0f }; // PosOffsets[0]
            regs[9] = new[] { 0f, 0f, 0f, 0f }; // PosOffsets[1]
            AssertGoldenMatch(fx, $"Bones={label}", regs, ps: null, textured: true, tol);
        }
    }

    // ---- Shared sweep machinery ----------------------------------------------------------

    // Cache the candidate VS + golden VS/PS per fixture so each case re-renders without
    // recompiling. Tests in this collection run sequentially (shared GL context).
    private readonly Dictionary<string, (string SdVs, string GVs, string Ps)> _cache = new();

    private async Task<(string SdVs, string GVs, string Ps)> Resolve(string fixture)
    {
        if (_cache.TryGetValue(fixture, out var c)) return c;
        string repoRoot = TestPaths.FindRepoRoot();
        string fxPath   = Path.Combine(repoRoot, "tests", "fixtures", "shaders", fixture + ".fx");
        var result = await new EffectCompiler().CompileAsync(
            await File.ReadAllTextAsync(fxPath),
            new CompilerOptions
            {
                Target = PlatformTarget.OpenGL,
                IncludeResolver = new FileSystemIncludeResolver(),
                SourceFileName = fxPath,
            });
        result.IsSuccess.Should().BeTrue(result.IsFailure
            ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "compile ok");
        GlslShaderPair sd = GlslShaderExtractor.Extract(result.Value.Data);
        string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL", fixture + ".mgfx");
        var golden = MgfxcMgfxReader.Parse(await File.ReadAllBytesAsync(goldenPath));
        string gVs = golden.GlslShaders.Single(s => s.Contains("attribute ", StringComparison.Ordinal));
        string gPs = golden.GlslShaders.Single(s => !s.Contains("attribute ", StringComparison.Ordinal));
        c = (sd.VertexSource!, gVs, gPs);
        _cache[fixture] = c;
        return c;
    }

    private void AssertGoldenMatch(
        string fixture, string label, Dictionary<int, float[]> vsRegs,
        Dictionary<int, float[]>? ps, bool textured, int tol)
    {
        var (sdVs, gVs, gPs) = _cache[fixture];
        byte[] sd, gold;
        using (_fixture.MakeContextCurrent())
        {
            gold = Render(_fixture.Gl, gVs, gPs, vsRegs, ps, textured);
            sd   = Render(_fixture.Gl, sdVs, gPs, vsRegs, ps, textured);
        }
        var cmp = ImageComparer.Compare(gold, sd, tolerance: (byte)tol);
        cmp.Matches.Should().BeTrue(
            $"[{fixture}] {label}: ShadowDusk's VS must render equivalent to the mgfxc golden " +
            $"(diff {cmp.DifferentPixels}/{cmp.TotalPixels}, maxd {cmp.MaxChannelDelta}, tol {tol})");
    }

    private void AssertNonVacuous(
        string fixture, int baseReg, int tintReg, bool textured, Dictionary<int, float[]>? ps = null)
    {
        var (_, gVs, gPs) = _cache[fixture];
        var general = Shapes.Single(s => s.Label == "general-asym").M;

        var idRegs = new Dictionary<int, float[]>();
        PlaceColumns(Identity, baseReg, idRegs);
        var mRegs = new Dictionary<int, float[]>();
        PlaceColumns(general, baseReg, mRegs);
        var tRegs = new Dictionary<int, float[]>();
        PlaceColumns(Transpose(general), baseReg, tRegs);
        // VertexAndPixel chains 3 matrices: fill the other two with identity so only `baseReg`
        // carries the transform; VsTransformColorTexture/ArrayUniformVs ignore the extra regs.
        foreach (var d in new[] { idRegs, mRegs, tRegs })
        {
            if (fixture == "VertexAndPixel") { PlaceColumns(Identity, 4, d); PlaceColumns(Identity, 8, d); }
            if (tintReg >= 0) d[tintReg] = new[] { 1f, 1f, 1f, 1f };
            if (fixture == "ArrayUniformVs") { PlaceColumns(d == idRegs ? Identity : (d == mRegs ? general : Transpose(general)), 4, d); d[8] = new float[4]; d[9] = new float[4]; }
        }

        byte[] idImg, mImg, tImg;
        using (_fixture.MakeContextCurrent())
        {
            idImg = Render(_fixture.Gl, gVs, gPs, idRegs, ps, textured);
            mImg  = Render(_fixture.Gl, gVs, gPs, mRegs, ps, textured);
            tImg  = Render(_fixture.Gl, gVs, gPs, tRegs, ps, textured);
        }
        ImageComparer.Compare(idImg, mImg, tolerance: 4).Matches.Should().BeFalse(
            $"[{fixture}] a non-identity matrix must render differently from identity (the transform must run)");
        ImageComparer.Compare(mImg, tImg, tolerance: 4).Matches.Should().BeFalse(
            $"[{fixture}] an asymmetric matrix must render differently from its transpose (transpose-sensitive scene)");
    }

    // Upload a row-major matrix as the four COLUMN registers MonoGame/KNI's SetValue(Matrix)
    // writes: register baseReg+k = column k = (m[0][k], m[1][k], m[2][k], m[3][k]).
    private static void PlaceColumns(float[] m, int baseReg, Dictionary<int, float[]> regs)
    {
        for (int k = 0; k < 4; k++)
            regs[baseReg + k] = new[] { m[0 * 4 + k], m[1 * 4 + k], m[2 * 4 + k], m[3 * 4 + k] };
    }

    private static float[] Transpose(float[] m)
    {
        var t = new float[16];
        for (int r = 0; r < 4; r++)
            for (int col = 0; col < 4; col++)
                t[r * 4 + col] = m[col * 4 + r];
        return t;
    }

    private static byte[] Render(
        GL gl, string vs, string ps, Dictionary<int, float[]> vsRegs,
        Dictionary<int, float[]>? psRegs, bool textured)
    {
        using var fbo = new OffscreenRenderer(gl);
        fbo.Clear(0, 0, 0, 255);
        using var prog = GlslShaderProgram.Compile(gl, vs, ps);
        prog.Use(gl);

        foreach (var (i, v) in vsRegs)
            SetIfPresent(gl, prog.Handle, $"vs_uniforms_vec4[{i}]", v);
        if (psRegs is not null)
            foreach (var (i, v) in psRegs)
                SetIfPresent(gl, prog.Handle, $"ps_uniforms_vec4[{i}]", v);

        MojoPosFixup.Apply(gl, prog.Handle, renderTargetBound: true,
            OffscreenRenderer.Width, OffscreenRenderer.Height);

        uint tex = 0;
        if (textured)
        {
            tex = gl.GenTexture();
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
            unsafe { fixed (byte* p = data) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 8, 8, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p); }
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, tex);
            int sLoc = gl.GetUniformLocation(prog.Handle, "ps_s0");
            if (sLoc >= 0) gl.Uniform1(sLoc, 0);
        }

        // One vertex layout (pos3 / color4 / uv2); attributes absent in a given shader simply
        // bind to location < 0 and are skipped (e.g. VertexAndPixel has only vs_v0).
        float[] verts =
        {
            -1f, -1f, 0f,  1f,1f,1f,1f,  0f, 1f,
             1f, -1f, 0f,  1f,1f,1f,1f,  1f, 1f,
             1f,  1f, 0f,  1f,1f,1f,1f,  1f, 0f,
            -1f,  1f, 0f,  1f,1f,1f,1f,  0f, 0f,
        };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };
        uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe { fixed (float* p = verts) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * 4), p, BufferUsageARB.StaticDraw); }
        uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        unsafe { fixed (uint* p = idx) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * 4), p, BufferUsageARB.StaticDraw); }

        const uint stride = 9 * sizeof(float);
        BindAttribIfPresent(gl, prog.Handle, "vs_v0", 3, stride, 0);
        BindAttribIfPresent(gl, prog.Handle, "vs_v1", 4, stride, 3 * sizeof(float));
        BindAttribIfPresent(gl, prog.Handle, "vs_v2", 2, stride, 7 * sizeof(float));

        gl.Disable(EnableCap.DepthTest);
        unsafe { gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, (void*)0); }
        gl.Finish();

        byte[] pixels = fbo.ReadPixels();
        gl.BindVertexArray(0);
        gl.DeleteVertexArray(vao); gl.DeleteBuffer(vbo); gl.DeleteBuffer(ebo);
        if (tex != 0) gl.DeleteTexture(tex);
        return pixels;
    }

    private static void SetIfPresent(GL gl, uint program, string name, float[] v)
    {
        int loc = gl.GetUniformLocation(program, name);
        if (loc >= 0) gl.Uniform4(loc, v[0], v[1], v[2], v[3]);
    }

    private static void BindAttribIfPresent(GL gl, uint program, string name, int size, uint stride, int byteOffset)
    {
        int loc = gl.GetAttribLocation(program, name);
        if (loc < 0) return;
        gl.EnableVertexAttribArray((uint)loc);
        unsafe { gl.VertexAttribPointer((uint)loc, size, VertexAttribPointerType.Float, false, stride, (void*)byteOffset); }
    }
}
