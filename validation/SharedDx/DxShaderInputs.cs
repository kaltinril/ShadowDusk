#nullable enable

using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Dx;

/// <summary>
/// DirectX (Phase 18) shared inputs — a verbatim copy of Phase 17's
/// <c>ShaderInputs</c> so the DX comparison uses the SAME shader list, the SAME
/// cat image, and the SAME parameter values set BY NAME on both baseline and
/// candidate. Parameter names/types come from the .fx sources in
/// tests/fixtures/shaders/. Kept separate from the GL copy only so the two
/// backends' projects stay fully independent.
/// </summary>
public static class DxShaderInputs
{
    /// <summary>The 10 SM3 PS-only shaders Phase 17 validated on GL, in fixed order.</summary>
    public static readonly string[] ShaderNames =
    {
        "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
        "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
    };

    /// <summary>
    /// Set every parameter any of the 10 shaders might expose. Null-conditional
    /// so a name absent on a given effect is silently skipped — both sides get
    /// the identical call, so the comparison stays fair.
    /// </summary>
    public static void SetParams(Effect e, Texture2D cat)
    {
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
