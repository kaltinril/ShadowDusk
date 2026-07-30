using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit coverage for the Phase 46 G6 (user structs) and G7 (arrays + added intrinsics + parser
/// hardening) gap-closures. Asserts the emitted HLSL for hand-written minimal snippets: that a struct
/// is emitted with a factory, that a struct-member matrix multiply still hits the matrix-order trap
/// (<c>mul(</c>), that an array constructor becomes a brace list, that the added intrinsics map, and
/// that the genuinely-unsupported shapes reject with a located diagnostic.
/// </summary>
public sealed class StructArrayTests
{
    private static string ConvertOk(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeTrue(string.Format(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        r.Fx.ShouldNotBeNull();
        return r.Fx!;
    }

    private static ConvertResult ConvertReject(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeFalse();
        r.Fx.ShouldBeNull();
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Line > 0 && d.Column > 0, "a reject must carry a located error");
        return r;
    }

    // ── G6: structs ───────────────────────────────────────────────────────────

    [Fact]
    public void Struct_IsEmittedWithFactory_AndConstructorCallsIt()
    {
        const string glsl = """
        struct Ray { vec3 origin; vec3 dir; };
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            Ray r = Ray(vec3(uv, 0.0), vec3(0.0, 0.0, 1.0));
            fragColor = vec4(r.origin.xy, r.dir.z, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("struct Ray", Case.Sensitive);
        fx.ShouldContain("Ray make_Ray(float3 origin, float3 dir)", Case.Sensitive);
        // The GLSL Name(...) constructor must route to the generated factory, never Ray(...).
        fx.ShouldContain("make_Ray(float3(uv, 0.0), float3(0.0, 0.0, 1.0))", Case.Sensitive);
        // Member access is emitted verbatim (no swizzle mangling on the field name).
        fx.ShouldContain("r.origin.xy", Case.Sensitive);
        fx.ShouldContain("r.dir.z", Case.Sensitive);
    }

    [Fact]
    public void StructMatrixMember_Multiply_StillUsesMul()
    {
        // The trap MUST keep working through a struct member: s.rot (a mat2) * v -> mul(v, s.rot).
        const string glsl = """
        struct Xform { mat2 rot; vec2 off; };
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            float c = cos(iTime), s = sin(iTime);
            Xform x = Xform(mat2(c, -s, s, c), uv);
            vec2 q = x.rot * x.off;
            fragColor = vec4(q, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // mul(v, M) order: the vector member must be the first mul() argument, the matrix member second.
        fx.ShouldContain("mul(x.off, x.rot)", Case.Sensitive);
        fx.ShouldNotContain("x.rot * x.off", Case.Sensitive);
    }

    [Fact]
    public void StructParamAndReturn_AreSupported()
    {
        const string glsl = """
        struct Hit { float t; vec3 n; };
        Hit trace(vec2 uv) { return Hit(uv.x, vec3(uv, 1.0)); }
        vec3 shade(Hit h) { return h.n * h.t; }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(shade(trace(uv)), 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("Hit trace(float2 uv)", Case.Sensitive);
        fx.ShouldContain("float3 shade(Hit h)", Case.Sensitive);
    }

    [Fact]
    public void StructConstructor_WrongArity_Rejects()
    {
        const string glsl = """
        struct P { vec2 a; vec2 b; };
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            P p = P(fragCoord);
            fragColor = vec4(p.a, p.b);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("expects 2 argument", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedInlineStructMember_Rejects()
    {
        const string glsl = """
        struct M { vec3 a; struct { float x; } inner; };
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("struct", StringComparison.OrdinalIgnoreCase));
    }

    // ── G7: arrays ────────────────────────────────────────────────────────────

    [Fact]
    public void ConstArrayConstructor_BecomesBraceList()
    {
        const string glsl = """
        const float k[3] = float[](0.2, 0.5, 0.3);
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float v = k[0] + k[1] + k[2];
            fragColor = vec4(v, v, v, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("static const float k[3] = { 0.2, 0.5, 0.3 };", Case.Sensitive);
    }

    [Fact]
    public void SizedArrayConstructor_BecomesBraceList()
    {
        const string glsl = """
        const vec2 pts[2] = vec2[2](vec2(0.0), vec2(1.0));
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(pts[0], pts[1]);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("static const float2 pts[2] = { ((float2)(0.0)), ((float2)(1.0)) };", Case.Sensitive);
    }

    [Fact]
    public void LocalFixedArray_DeclaredAndIndexed()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            float a[2];
            a[0] = uv.x;
            a[1] = uv.y;
            fragColor = vec4(a[0], a[1], 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float a[2];", Case.Sensitive);
    }

    [Fact]
    public void UnsizedArray_Rejects()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a[];
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("Unsized", StringComparison.Ordinal));
    }

    [Fact]
    public void ArrayConstructorSizeMismatch_Rejects()
    {
        const string glsl = """
        const float k[2] = float[3](0.1, 0.2, 0.3);
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(k[0]);
        }
        """;

        // The array constructor declares 3 but the parser catches the size/element mismatch.
        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("size", StringComparison.OrdinalIgnoreCase));
    }

    // ── G7: added intrinsics ──────────────────────────────────────────────────

    [Fact]
    public void Fwidth_MapsToSameName()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            float w = fwidth(uv.x);
            fragColor = vec4(w, w, w, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("fwidth(uv.x)", Case.Sensitive);
    }

    [Fact]
    public void MatrixCompMult_IsComponentwiseNotMul()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            mat2 a = mat2(1.0, 2.0, 3.0, 4.0);
            mat2 b = mat2(2.0, 2.0, 2.0, 2.0);
            mat2 c = matrixCompMult(a, b);
            fragColor = vec4(c[0], c[1]);
        }
        """;

        string fx = ConvertOk(glsl);
        // matrixCompMult is componentwise: emit (a * b), NOT a mul()-reordered product.
        fx.ShouldContain("float2x2 c = (a * b);", Case.Sensitive);
    }

    [Fact]
    public void RoundEven_Rejects()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float q = roundEven(fragCoord.x);
            fragColor = vec4(q, 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("roundEven", StringComparison.Ordinal));
    }

    [Fact]
    public void TextureBias_Rejects_BecauseTex2DbiasFailsOnGlDx()
    {
        // texture(s, uv, bias) maps to tex2Dbias, which does NOT compile on the OpenGL/DirectX
        // targets, so the converter rejects it loudly rather than emit GL/DX-incompatible output.
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = texture(iChannel0, uv, 1.5);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("mip-bias", StringComparison.Ordinal));
    }

    // ── G7: parser hardening ──────────────────────────────────────────────────

    [Fact]
    public void CommaOperator_InForIncrement_Parses()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float acc = 0.0;
            for (int i = 0, j = 3; i < 3; i++, j--)
            {
                acc += float(j);
            }
            fragColor = vec4(acc, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("i++, j--", Case.Sensitive);
    }
}
