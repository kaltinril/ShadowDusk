#nullable enable

using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Fna;

/// <summary>
/// FNA (Phase 39 rung 3/4) shared inputs. The PS-only GATE set and its parameter
/// values are a faithful port of <c>validation/SharedDx/DxShaderInputs.cs</c> (which is
/// itself the Phase 17 set), so the FNA comparison uses the SAME shaders, the SAME cat
/// image, and the SAME parameter values as the proven GL/DX validations; the VS-driven
/// GATE set (custom-geometry quad scene) is the 17-VS analog. The extended (reported,
/// non-gating) set adds the rest of the SM3-expressible PS-only corpus plus the two
/// fxc golden sources from <c>tests/fixtures/golden/FNA/</c>.
/// </summary>
public static class FnaShaderInputs
{
    /// <summary>One corpus entry: display name, repo-relative .fx path, gate membership,
    /// scene, and (optionally) a technique to select by name instead of CurrentTechnique.</summary>
    public sealed record CorpusShader(
        string Name, string RelativePath, bool Gate,
        FnaScene Scene = FnaScene.Sprite, string? Technique = null);

    private static CorpusShader Fixture(string name, bool gate) =>
        new(name, Path.Combine("tests", "fixtures", "shaders", name + ".fx"), gate);

    private static CorpusShader VsFixture(string name, bool gate) =>
        new(name, Path.Combine("tests", "fixtures", "shaders", name + ".fx"), gate, FnaScene.VsQuad);

    private static CorpusShader Golden(string name, bool gate = false) =>
        new(name, Path.Combine("tests", "fixtures", "golden", "FNA", name + ".fx"), gate);

    /// <summary>
    /// The corpus, in fixed order. The GATE set (all must PASS for exit code 0) is the
    /// Phase 17 PS-only ten plus the four VS-driven effects (the 17-VS analog); the
    /// rest are reported, not gating.
    /// </summary>
    public static readonly CorpusShader[] Corpus =
    {
        // ---- rung-4 GATE set (Phase 17's ten PS-only shaders) ----
        Fixture("Grayscale", gate: true),
        Fixture("Invert", gate: true),
        Fixture("TintShader", gate: true),
        Fixture("Sepia", gate: true),
        Fixture("Saturate", gate: true),
        Fixture("Pixelated", gate: true),
        Fixture("Scanlines", gate: true),
        Fixture("Fading", gate: true),
        Fixture("Dots", gate: true),
        Fixture("Dissolve", gate: true),
        // ---- rung-4 GATE set, VS-driven (the effect ships its OWN vertex shader;
        //      rendered through the custom-geometry quad scene — the Phase-28 analog) ----
        VsFixture("VsTransformColorTexture", gate: true),
        VsFixture("PolygonLight", gate: true),
        VsFixture("VertexAndPixel", gate: true),
        VsFixture("FnaMultiPassStates", gate: true),
        // Same binary, second technique selected BY NAME (PS-only → Sprite scene; the
        // technique-by-name lookup in real FNA is otherwise never exercised). Scene is
        // chosen per the SELECTED technique, so the whole-binary VS guard is skipped
        // for technique-selector rows.
        new("FnaMultiPassStatesT2",
            Path.Combine("tests", "fixtures", "shaders", "FnaMultiPassStates.fx"),
            Gate: true, FnaScene.Sprite, Technique: "SinglePass"),
        // Matrix calibration row (Phase 40): square float4x4 through PS arithmetic,
        // explicit M upload (both arms identical — fxc bakes the source default,
        // ShadowDusk deliberately bakes zeros until F2 is settled, so defaults must
        // not be what the comparison exercises).
        Golden("matrix", gate: true),
        // ---- extended PS-only corpus (reported, not gating) ----
        Fixture("BasicShader", gate: false),
        Fixture("BlendShader", gate: false),
        Fixture("ClipShader", gate: false),
        // Gate row since Phase 40: the live brace-form sampler case (`sampler S { … };`)
        // whose binding the FNA path used to silently lose — with the distinct mask
        // texture, a lost Mask binding now diverges from the oracle instead of hiding.
        Fixture("ClipShaderNew", gate: true),
        Fixture("ClipShaderSpriteTarget", gate: false),
        Fixture("MultiTexture", gate: false),
        Fixture("MultiTextureOverlay", gate: false),
        Fixture("SimpleLightShader", gate: false),
        Fixture("SpriteAlphaTest", gate: false),
        Fixture("Teleport", gate: false),
        // ---- fxc golden sources (reported, not gating) ----
        Golden("minimal"),
        Golden("textured"),
        // ---- diagnostic probe (reported, not gating): isolates fxc's preshader path ----
        new("FnaPreshaderProbe",
            Path.Combine("validation", "FnaValidation", "FnaPreshaderProbe.fx"), Gate: false),
        // ---- Dissolve bisection ladder (reported, not gating): each probe isolates one
        //      construct of Dissolve's failing region; the first diverging probe pinpoints
        //      the vkd3d idiom MojoShader mistranslates ----
        new("FnaProbeCmp",
            Path.Combine("validation", "FnaValidation", "FnaProbeCmp.fx"), Gate: false),
        new("FnaProbeBoolLerp",
            Path.Combine("validation", "FnaValidation", "FnaProbeBoolLerp.fx"), Gate: false),
        new("FnaProbeIfc",
            Path.Combine("validation", "FnaValidation", "FnaProbeIfc.fx"), Gate: false),
        new("FnaProbeClip",
            Path.Combine("validation", "FnaValidation", "FnaProbeClip.fx"), Gate: false),
    };

    /// <summary>
    /// Set every parameter any corpus shader might expose, returning the names actually
    /// set (the per-arm hit list — a name set on one arm but absent on the other is a
    /// fidelity signal the harness reports). A name absent on a given effect is skipped —
    /// both arms get the identical call, so the comparison stays fair. The first block is
    /// a VERBATIM port of <c>DxShaderInputs.SetParams</c> (same names, same values).
    /// Second/mask-style textures get <paramref name="mask"/>, a deterministic texture
    /// VISIBLY DISTINCT from the cat: when every slot held the same cat, a lost or
    /// mis-registered second-texture binding rendered pixel-identical to a correct one
    /// (how the brace-form sampler bug stayed invisible — Phase 40).
    /// </summary>
    public static IReadOnlyList<string> SetParams(Effect e, Texture2D cat, Texture2D mask)
    {
        var hits = new List<string>();
        void Set(string name, Action<EffectParameter> apply)
        {
            EffectParameter? p = e.Parameters[name];
            if (p is null)
                return;
            apply(p);
            hits.Add(name);
        }

        // ---- gate set: verbatim DxShaderInputs.SetParams values ----
        Set("SpriteTexture", p => p.SetValue(cat));

        Set("TintColor", p => p.SetValue(new Vector4(1f, 0.5f, 0.5f, 1f)));

        Set("_sepiaTone", p => p.SetValue(new Vector3(1.2f, 1.0f, 0.8f)));

        Set("BloomThreshold", p => p.SetValue(new Vector4(0.25f, 0.25f, 0.25f, 0.25f)));
        Set("BloomIntensity", p => p.SetValue(1.5f));
        Set("BloomSaturation", p => p.SetValue(0.8f));

        Set("_attenuation", p => p.SetValue(800.0f));
        Set("_linesFactor", p => p.SetValue(0.04f));

        Set("angle", p => p.SetValue(0.5f));
        Set("scale", p => p.SetValue(0.5f));
        Set("ScreenSize", p => p.SetValue(new Vector2(cat.Width, cat.Height)));

        Set("_dissolveTex", p => p.SetValue(mask));
        Set("_progress", p => p.SetValue(0.5f));
        Set("_dissolveThreshold", p => p.SetValue(0.04f));
        Set("_dissolveThresholdColor", p => p.SetValue(new Vector4(1f, 0.5f, 0f, 1f)));

        // ---- extended corpus (reported set) ----
        Set("ElapsedTime", p => p.SetValue(0.5f));                // BlendShader
        Set("Character01", p => p.SetValue(cat));                 // BlendShader
        Set("Character02", p => p.SetValue(mask));                // BlendShader
        Set("ClipTexture", p => p.SetValue(mask));                // ClipShader / ClipShaderSpriteTarget
        Set("DrawTexture", p => p.SetValue(cat));                 // ClipShader / ClipShaderSpriteTarget
        Set("Mask", p => p.SetValue(mask));                       // ClipShaderNew
        Set("_secondTexture", p => p.SetValue(mask));             // MultiTexture / MultiTextureOverlay
        Set("RenderTargetTexture", p => p.SetValue(cat));         // SimpleLightShader
        Set("MaskTexture", p => p.SetValue(mask));                // SimpleLightShader
        Set("_alphaTest", p => p.SetValue(new Vector3(0.5f, -1f, 1f))); // SpriteAlphaTest
        Set("amount", p => p.SetValue(0.3f));                     // Teleport
        Set("t", p => p.SetValue(cat));                           // golden textured.fx

        // ---- VS-driven gate set (custom-geometry quad scene) ----
        // CAUTION: this block claims very common uniform names (World / View /
        // Projection / Color / Tint / WorldViewProjection) in the shared SetParams
        // namespace. SetParams runs against EVERY corpus effect via ?., so any future
        // corpus row declaring one of these names silently receives THESE values, not
        // defaults — check new fixtures against this list before adding them (e.g.
        // PenumbraLight.fx already declares both Color and WorldViewProjection).
        // VsTransformColorTexture: identity WVP maps the clip-space quad corners
        // straight to the viewport; the non-white tint keeps the VS color path
        // (vertexColor * Tint) non-vacuous. Tint is also FnaMultiPassStates' PS uniform.
        Set("WorldViewProjection", p => p.SetValue(Matrix.Identity));
        Set("Tint", p => p.SetValue(new Vector4(1f, 0.5f, 0.5f, 1f)));
        // PolygonLight: worldPos = texCoord, so a light at (0.5,0.5) with radius 0.75
        // produces a spatially-varying radial gradient over the whole quad.
        Set("viewProjectionMatrix", p => p.SetValue(Matrix.Identity));
        Set("lightSource", p => p.SetValue(new Vector2(0.5f, 0.5f)));
        Set("lightColor", p => p.SetValue(new Vector3(1f, 0.75f, 0.5f)));
        Set("lightRadius", p => p.SetValue(0.75f));
        // VertexAndPixel: exact-dyadic scale + translation (all entries representable
        // and arithmetic exact in fp32, so both arms compute bit-identical vertex
        // positions and rasterize identical edges) — the visible off-center half-size
        // quad proves the three matrix uploads actually flow, and the translation row
        // would expose a row/column-major mismatch that identity/scale never could.
        Set("World", p => p.SetValue(Matrix.CreateScale(0.5f)));
        Set("View", p => p.SetValue(Matrix.CreateTranslation(0.25f, 0.25f, 0f)));
        Set("Projection", p => p.SetValue(Matrix.Identity));
        Set("Color", p => p.SetValue(new Vector4(0.2f, 0.7f, 0.9f, 1f)));
        // FnaMultiPassStates: the cat through TexSampler (Tint set above).
        Set("SceneTexture", p => p.SetValue(cat));

        // ---- diagnostic probes ----
        Set("probeA", p => p.SetValue(0.25f));                    // FnaPreshaderProbe
        Set("probeB", p => p.SetValue(0.5f));                     // FnaPreshaderProbe

        // ---- matrix golden (Phase 40 calibration row) ----
        // Exact-dyadic, NON-symmetric (translation row) — a row/column-major mishandling
        // shifts the gradient; identical SetValue in both arms overrides fxc's baked
        // default vs our baked zeros (the documented F2 difference must not be what the
        // render comparison exercises).
        Set("M", p => p.SetValue(Matrix.CreateTranslation(0.5f, 0.25f, 0f)));

        return hits;
    }

    /// <summary>
    /// The deterministic "second texture": spatially varying in every channel INCLUDING
    /// alpha, and visibly distinct from the cat. Procedural (no asset, no randomness) so
    /// both arms and every run see identical bytes. The red-channel gradient gives
    /// Dissolve a spatially-varying kill region; the alpha band exercises mask/clip paths.
    /// </summary>
    public static Texture2D CreateMaskTexture(GraphicsDevice gd, int width = 256, int height = 256)
    {
        var data = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)(width - 1);
                float v = y / (float)(height - 1);
                byte r = (byte)(u * 255f);
                byte g = (byte)(v * 255f);
                byte b = (byte)((x * 7 + y * 13) % 256);
                byte a = (byte)(128 + (x * 3 + y * 5) % 128); // 128..255, varies per pixel
                data[y * width + x] = new Color(r, g, b, a);
            }
        }

        var texture = new Texture2D(gd, width, height, false, SurfaceFormat.Color);
        texture.SetData(data);
        return texture;
    }

    /// <summary>Walk up from the executing assembly to the repo root (has ShadowDusk.slnx).</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root (ShadowDusk.slnx) above " + AppContext.BaseDirectory);
    }

    public static string CatPath(string repoRoot) =>
        Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
}
