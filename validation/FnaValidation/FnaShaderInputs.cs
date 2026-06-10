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
    /// <summary>One corpus entry: display name, repo-relative .fx path, gate membership, scene.</summary>
    public sealed record CorpusShader(
        string Name, string RelativePath, bool Gate, FnaScene Scene = FnaScene.Sprite);

    private static CorpusShader Fixture(string name, bool gate) =>
        new(name, Path.Combine("tests", "fixtures", "shaders", name + ".fx"), gate);

    private static CorpusShader VsFixture(string name, bool gate) =>
        new(name, Path.Combine("tests", "fixtures", "shaders", name + ".fx"), gate, FnaScene.VsQuad);

    private static CorpusShader Golden(string name) =>
        new(name, Path.Combine("tests", "fixtures", "golden", "FNA", name + ".fx"), Gate: false);

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
        // ---- extended PS-only corpus (reported, not gating) ----
        Fixture("BasicShader", gate: false),
        Fixture("BlendShader", gate: false),
        Fixture("ClipShader", gate: false),
        Fixture("ClipShaderNew", gate: false),
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
    /// Set every parameter any corpus shader might expose. Null-conditional so a name
    /// absent on a given effect is silently skipped — both arms get the identical
    /// call, so the comparison stays fair. The first block is a VERBATIM port of
    /// <c>DxShaderInputs.SetParams</c> (same names, same values); the second block
    /// covers the extended corpus (second textures get the same cat, mirroring how
    /// the DX harness feeds Dissolve's <c>_dissolveTex</c>).
    /// </summary>
    public static void SetParams(Effect e, Texture2D cat)
    {
        // ---- gate set: verbatim DxShaderInputs.SetParams values ----
        e.Parameters["SpriteTexture"]?.SetValue(cat);

        e.Parameters["TintColor"]?.SetValue(new Vector4(1f, 0.5f, 0.5f, 1f));

        e.Parameters["_sepiaTone"]?.SetValue(new Vector3(1.2f, 1.0f, 0.8f));

        e.Parameters["BloomThreshold"]?.SetValue(new Vector4(0.25f, 0.25f, 0.25f, 0.25f));
        e.Parameters["BloomIntensity"]?.SetValue(1.5f);
        e.Parameters["BloomSaturation"]?.SetValue(0.8f);

        e.Parameters["_attenuation"]?.SetValue(800.0f);
        e.Parameters["_linesFactor"]?.SetValue(0.04f);

        e.Parameters["angle"]?.SetValue(0.5f);
        e.Parameters["scale"]?.SetValue(0.5f);
        e.Parameters["ScreenSize"]?.SetValue(new Vector2(cat.Width, cat.Height));

        e.Parameters["_dissolveTex"]?.SetValue(cat);
        e.Parameters["_progress"]?.SetValue(0.5f);
        e.Parameters["_dissolveThreshold"]?.SetValue(0.04f);
        e.Parameters["_dissolveThresholdColor"]?.SetValue(new Vector4(1f, 0.5f, 0f, 1f));

        // ---- extended corpus (reported set) ----
        e.Parameters["ElapsedTime"]?.SetValue(0.5f);              // BlendShader
        e.Parameters["Character01"]?.SetValue(cat);               // BlendShader
        e.Parameters["Character02"]?.SetValue(cat);               // BlendShader
        e.Parameters["ClipTexture"]?.SetValue(cat);               // ClipShader / ClipShaderSpriteTarget
        e.Parameters["DrawTexture"]?.SetValue(cat);               // ClipShader / ClipShaderSpriteTarget
        e.Parameters["Mask"]?.SetValue(cat);                      // ClipShaderNew
        e.Parameters["_secondTexture"]?.SetValue(cat);            // MultiTexture / MultiTextureOverlay
        e.Parameters["RenderTargetTexture"]?.SetValue(cat);       // SimpleLightShader
        e.Parameters["MaskTexture"]?.SetValue(cat);               // SimpleLightShader
        e.Parameters["_alphaTest"]?.SetValue(new Vector3(0.5f, -1f, 1f)); // SpriteAlphaTest
        e.Parameters["amount"]?.SetValue(0.3f);                   // Teleport
        e.Parameters["t"]?.SetValue(cat);                         // golden textured.fx

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
        e.Parameters["WorldViewProjection"]?.SetValue(Matrix.Identity);
        e.Parameters["Tint"]?.SetValue(new Vector4(1f, 0.5f, 0.5f, 1f));
        // PolygonLight: worldPos = texCoord, so a light at (0.5,0.5) with radius 0.75
        // produces a spatially-varying radial gradient over the whole quad.
        e.Parameters["viewProjectionMatrix"]?.SetValue(Matrix.Identity);
        e.Parameters["lightSource"]?.SetValue(new Vector2(0.5f, 0.5f));
        e.Parameters["lightColor"]?.SetValue(new Vector3(1f, 0.75f, 0.5f));
        e.Parameters["lightRadius"]?.SetValue(0.75f);
        // VertexAndPixel: exact-dyadic scale + translation (all entries representable
        // and arithmetic exact in fp32, so both arms compute bit-identical vertex
        // positions and rasterize identical edges) — the visible off-center half-size
        // quad proves the three matrix uploads actually flow, and the translation row
        // would expose a row/column-major mismatch that identity/scale never could.
        e.Parameters["World"]?.SetValue(Matrix.CreateScale(0.5f));
        e.Parameters["View"]?.SetValue(Matrix.CreateTranslation(0.25f, 0.25f, 0f));
        e.Parameters["Projection"]?.SetValue(Matrix.Identity);
        e.Parameters["Color"]?.SetValue(new Vector4(0.2f, 0.7f, 0.9f, 1f));
        // FnaMultiPassStates: the cat through TexSampler (Tint set above).
        e.Parameters["SceneTexture"]?.SetValue(cat);
        e.Parameters["probeA"]?.SetValue(0.25f);                  // FnaPreshaderProbe
        e.Parameters["probeB"]?.SetValue(0.5f);                   // FnaPreshaderProbe
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
