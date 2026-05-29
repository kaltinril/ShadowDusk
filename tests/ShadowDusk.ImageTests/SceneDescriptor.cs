// Per-fixture scene metadata for Phase 16 image regression tests.
//
// This file is a pure-data declarative catalog: for each fixture shader, it
// describes what clear color, uniforms, textures, tolerance, and output-stem
// suffix the renderer should use when producing the candidate image.
//
// No GL calls, no I/O, no rendering happens here. The OffscreenRenderer (a
// sibling file owned by another agent) consumes this catalog and performs the
// actual draws.
//
// Uniform names are taken verbatim from the corresponding .fx source files
// (see tests/fixtures/shaders/*.fx); they are NOT invented. SPIRV-Cross
// preserves these names when emitting GLSL, optionally wrapped in a UBO whose
// member names match the original HLSL.
//
// Geometry is fixed across every scene: two triangles forming a unit quad
// covering NDC [-1, 1] x [-1, 1] with the interleaved attribute layout
// described in PHASE-16 section 4 (float3 POSITION, float4 COLOR0,
// float2 TEXCOORD0). Per-vertex COLOR0 is set to magenta (1, 0, 1, 1) in the
// renderer so that Minimal.fx (which renders vertex color through) produces
// the magenta described in plan section 4.2.

using System.Collections.Generic;

namespace ShadowDusk.ImageTests;

/// <summary>
/// A single shader uniform value. Discriminated union over the small set of
/// types our nine fixture shaders actually use.
/// </summary>
public abstract record UniformValue
{
    private UniformValue() { }

    /// <summary>Single scalar float uniform.</summary>
    public sealed record FloatValue(float V) : UniformValue;

    /// <summary>Four-component float vector uniform (covers vec2/vec3/vec4 cases too).</summary>
    public sealed record Vec4Value(float X, float Y, float Z, float W) : UniformValue;

    /// <summary>4x4 float matrix uniform. <paramref name="Values"/> is 16 floats, column-major.</summary>
    public sealed record Mat4Value(float[] Values) : UniformValue;
}

/// <summary>
/// A simple 2D texture description, used by <c>textured.fx</c>.
/// <paramref name="RgbaPixels"/> is row-major, top-left origin, 4 bytes per
/// pixel (R, G, B, A); total length must equal <c>Width * Height * 4</c>.
/// </summary>
public sealed record TextureDescriptor(int Width, int Height, byte[] RgbaPixels);

/// <summary>
/// Describes a single render for one (technique, pass) of a fixture. The
/// renderer compiles that technique+pass to a GL program, clears the FBO with
/// <see cref="ClearColor"/>, binds <see cref="Uniforms"/> and
/// <see cref="Textures"/>, draws the standard unit quad, and reads the result
/// back for comparison with tolerance <see cref="Tolerance"/>.
///
/// <para>
/// <see cref="MojoConstantRegisters"/> is only consulted when rendering
/// MojoShader-dialect GLSL (the mgfxc cross-validation goldens). That dialect
/// exposes free uniforms as an unnamed <c>uniform vec4 ps_uniforms_vec4[N]</c>
/// constant-register array rather than named uniforms, so the renderer can't
/// bind by name. This map gives each uniform's constant-register index so the
/// same values drive both the ShadowDusk (named/UBO) and mgfxc (array) programs.
/// Leave it <c>null</c> for fixtures with no free uniforms or rendered only
/// from ShadowDusk output.
/// </para>
/// </summary>
public sealed record SceneRender(
    int TechniqueIndex,
    int PassIndex,
    (byte R, byte G, byte B, byte A) ClearColor,
    IReadOnlyDictionary<string, UniformValue> Uniforms,
    IReadOnlyDictionary<string, TextureDescriptor> Textures,
    byte Tolerance,
    string OutputStemSuffix,
    IReadOnlyDictionary<string, int>? MojoConstantRegisters = null);

/// <summary>
/// All renders that should be produced for a single fixture shader. A fixture
/// with one technique and one pass has a single <see cref="SceneRender"/>;
/// multipass / multitechnique fixtures have one entry per pass / technique.
/// </summary>
public sealed record SceneDescriptor(string FixtureName, IReadOnlyList<SceneRender> Renders);

/// <summary>
/// Convenience accessors for common matrix uniform values.
/// </summary>
public static class Mat4
{
    /// <summary>Column-major 4x4 identity matrix as a uniform value.</summary>
    public static UniformValue.Mat4Value Identity { get; } = new UniformValue.Mat4Value(new float[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f,
    });
}

/// <summary>
/// Static catalog of every fixture scene that the image-regression tests
/// drive. Keys are fixture stem names (matching <c>tests/fixtures/shaders/&lt;stem&gt;.fx</c>).
/// </summary>
public static class SceneCatalog
{
    /// <summary>
    /// Fixture name to scene descriptor. All entries are pre-built once in
    /// the static constructor; the dictionary is exposed read-only.
    /// </summary>
    public static IReadOnlyDictionary<string, SceneDescriptor> All { get; }

    static SceneCatalog()
    {
        var empty = new Dictionary<string, UniformValue>();
        var noTextures = new Dictionary<string, TextureDescriptor>();
        var catalog = new Dictionary<string, SceneDescriptor>();

        // -------------------------------------------------------------------
        // Minimal.fx
        //
        // Pure passthrough vertex shader (no transform) + pixel shader that
        // returns the interpolated COLOR0. No uniforms at all. The standard
        // quad's per-vertex COLOR0 is magenta (1, 0, 1, 1) so the entire
        // framebuffer ends up magenta. Tolerance 0 — solid color, exact match.
        // -------------------------------------------------------------------
        catalog["Minimal"] = new SceneDescriptor(
            FixtureName: "Minimal",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: empty,
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: ""),
            });

        // -------------------------------------------------------------------
        // textured.fx
        //
        // Passthrough vertex shader; pixel shader samples a Texture2D named
        // `Texture` via `TextureSampler`. We bind a 4x4 solid red texture.
        // Tolerance 1 — allow ±1/255 per channel for any bilinear/format
        // rounding even though the source texels are all identical.
        // -------------------------------------------------------------------
        catalog["textured"] = new SceneDescriptor(
            FixtureName: "textured",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: empty,
                    Textures: new Dictionary<string, TextureDescriptor>
                    {
                        ["Texture"] = MakeSolidTexture(4, 4, 255, 0, 0, 255),
                    },
                    Tolerance: 1,
                    OutputStemSuffix: ""),
            });

        // -------------------------------------------------------------------
        // cbuffer.fx
        //
        // cbuffer Transforms { float4x4 WorldViewProj; float4 DiffuseColor; };
        // PS returns DiffuseColor directly. Identity transform + green
        // DiffuseColor yields a solid green framebuffer. Tolerance 0.
        // -------------------------------------------------------------------
        catalog["cbuffer"] = new SceneDescriptor(
            FixtureName: "cbuffer",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: new Dictionary<string, UniformValue>
                    {
                        ["WorldViewProj"] = Mat4.Identity,
                        ["DiffuseColor"] = new UniformValue.Vec4Value(0f, 1f, 0f, 1f),
                    },
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: ""),
            });

        // -------------------------------------------------------------------
        // multipass.fx
        //
        // Single technique, two passes; no uniforms.
        //   Pass 0 ("Opaque")      -> PS_Solid returns (1, 0, 0, 1)  -- red
        //   Pass 1 ("Transparent") -> PS_Alpha returns (1, 0, 0, 0.5) -- red @ 0.5 alpha
        //
        // Note: the provisional plan table claimed pass 1 was green; the
        // actual fixture source uses semi-transparent red. The renderer reads
        // the framebuffer back as RGBA8 without blending (default state for
        // this fixture, since no AlphaBlendEnable is set), so the alpha
        // channel is preserved verbatim. Tolerance 0 — solid color outputs.
        // -------------------------------------------------------------------
        catalog["multipass"] = new SceneDescriptor(
            FixtureName: "multipass",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: empty,
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: "_pass0"),
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 1,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: empty,
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: "_pass1"),
            });

        // -------------------------------------------------------------------
        // multitechnique.fx
        //
        // Three techniques (TechA / TechB / TechC), each one pass. All share
        // a single uniform float4x4 WorldViewProj. Each technique returns a
        // distinct solid color (red / green / blue). Tolerance 0.
        // -------------------------------------------------------------------
        var multitechniqueUniforms = new Dictionary<string, UniformValue>
        {
            ["WorldViewProj"] = Mat4.Identity,
        };
        catalog["multitechnique"] = new SceneDescriptor(
            FixtureName: "multitechnique",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: multitechniqueUniforms,
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: "_techA"),
                new SceneRender(
                    TechniqueIndex: 1,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: multitechniqueUniforms,
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: "_techB"),
                new SceneRender(
                    TechniqueIndex: 2,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: multitechniqueUniforms,
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: "_techC"),
            });

        // -------------------------------------------------------------------
        // render-states.fx
        //
        // PS returns (1, 1, 1, 0.5). The pass declares AlphaBlendEnable=True,
        // CullMode=None, DepthBufferEnable=False — the renderer is expected
        // to honor these by enabling GL_BLEND with
        // src=SRC_ALPHA, dst=ONE_MINUS_SRC_ALPHA.
        //
        // White-with-0.5-alpha source over a (0, 0, 0, 1) opaque-black clear
        // gives final color (0.5, 0.5, 0.5, 1.0), which is (128, 128, 128,
        // 255) in 8-bit. Tolerance 2 — blend-factor rounding can produce 127
        // or 128 depending on rounding mode.
        // -------------------------------------------------------------------
        catalog["render-states"] = new SceneDescriptor(
            FixtureName: "render-states",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: new Dictionary<string, UniformValue>
                    {
                        ["WorldViewProj"] = Mat4.Identity,
                    },
                    Textures: noTextures,
                    Tolerance: 2,
                    OutputStemSuffix: ""),
            });

        // -------------------------------------------------------------------
        // annotations.fx
        //
        // Single free uniform `float4 TintColor` decorated with HLSL
        // annotations (UIName, UIOrder) — those are stripped by the parser
        // so the GLSL just sees a plain TintColor uniform. Passthrough VS,
        // PS returns TintColor. Magenta (1, 0, 1, 1) -> solid magenta.
        // Tolerance 0.
        // -------------------------------------------------------------------
        catalog["annotations"] = new SceneDescriptor(
            FixtureName: "annotations",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: new Dictionary<string, UniformValue>
                    {
                        ["TintColor"] = new UniformValue.Vec4Value(1f, 0f, 1f, 1f),
                    },
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: ""),
            });

        // -------------------------------------------------------------------
        // platform-macros.fx
        //
        // Has a `#if GLSL ... #elif SM4 ... #else ...` ladder. The OpenGL
        // compile path defines GLSL, so the pixel shader returns
        // (0, 1, 0, 1) — solid green. WorldViewProj uniform required for VS.
        // Tolerance 0.
        // -------------------------------------------------------------------
        catalog["platform-macros"] = new SceneDescriptor(
            FixtureName: "platform-macros",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: new Dictionary<string, UniformValue>
                    {
                        ["WorldViewProj"] = Mat4.Identity,
                    },
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: ""),
            });

        // -------------------------------------------------------------------
        // basiceffect-mini.fx
        //
        // Has four techniques: Tech0 (vertex colors), Tech1 (white), Tech2
        // (flat gray), Tech3 (debug magenta). To keep this fixture simple
        // and avoid having to wire per-vertex lighting / normals, we render
        // only Tech0 (PS_Vertex returns input.Color verbatim). The standard
        // quad supplies per-vertex COLOR0 = magenta (1, 0, 1, 1), so the
        // output is solid magenta — same color as Minimal.fx but with an
        // actual transform applied.
        //
        // Tolerance 0 — the input vertex color is uniform across all four
        // verts so interpolation is constant.
        // -------------------------------------------------------------------
        catalog["basiceffect-mini"] = new SceneDescriptor(
            FixtureName: "basiceffect-mini",
            Renders: new[]
            {
                new SceneRender(
                    TechniqueIndex: 0,
                    PassIndex: 0,
                    ClearColor: ((byte)0, (byte)0, (byte)0, (byte)255),
                    Uniforms: new Dictionary<string, UniformValue>
                    {
                        ["WorldViewProj"] = Mat4.Identity,
                    },
                    Textures: noTextures,
                    Tolerance: 0,
                    OutputStemSuffix: ""),
            });

        All = catalog;
    }

    private static TextureDescriptor MakeSolidTexture(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[(i * 4) + 0] = r;
            pixels[(i * 4) + 1] = g;
            pixels[(i * 4) + 2] = b;
            pixels[(i * 4) + 3] = a;
        }

        return new TextureDescriptor(width, height, pixels);
    }
}
