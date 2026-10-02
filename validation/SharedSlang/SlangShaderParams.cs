#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Slang;

/// <summary>
/// Issue #230: the parameter values every real-slangc render arm sets BY NAME, identically on
/// the reference and the candidate effect. Compiles against MonoGame (the DirectX_12 and Vulkan
/// drivers) and FNA (<c>validation/FnaValidation -- slang</c>) alike: both expose the same
/// <c>Microsoft.Xna.Framework.Graphics.Effect</c> API.
///
/// <para>Every uniform in the 21-shader corpus gets a NON-DEFAULT value (zero would render a
/// <c>Tint</c> black and collapse a VS-driven shader to a point, so a lost binding could not be
/// seen), and the vertex-stage transform is the non-identity ASYMMETRIC matrix the issue-#70
/// discipline requires: an identity is transpose-invariant and cannot catch a packing bug. A
/// name absent from an effect is skipped (both arms get the same call); a type mismatch is NOT
/// swallowed - it is returned so the arm fails visibly instead of rendering with zeros.</para>
/// </summary>
public static class SlangShaderParams
{
    /// <summary>Scale 0.5 then translate (0.25, 0.25) in clip space, the same matrix
    /// <c>validation/Shared/VsEffectImageRenderer</c> uses. Transposed, the translation lands in
    /// the w row and the quad leaves the frustum, so the vertex control diverges loudly.</summary>
    public static readonly Matrix VertexTransform = new(
        0.5f,  0f,    0f, 0f,
        0f,    0.5f,  0f, 0f,
        0f,    0f,    1f, 0f,
        0.25f, 0.25f, 0f, 1f);

    /// <summary>Sets every corpus parameter the effect exposes. Returns the names actually set
    /// and any SetValue failure text (null when clean).</summary>
    public static (IReadOnlyList<string> Set, string? Error) Apply(Effect effect, Texture2D texture)
    {
        var set = new List<string>();
        var errors = new List<string>();

        void Set(string name, Action<EffectParameter> apply)
        {
            EffectParameter? p = effect.Parameters[name];
            if (p is null)
                return;
            try
            {
                apply(p);
                set.Add(name);
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Set("SpriteTexture", p => p.SetValue(texture));
        Set("WorldViewProjection", p => p.SetValue(VertexTransform));
        Set("Tint", p => p.SetValue(new Vector4(1f, 0.6f, 0.3f, 1f)));
        Set("Desaturation", p => p.SetValue(0.65f));
        Set("ScrollOffset", p => p.SetValue(new Vector2(0.15f, 0.3f)));
        Set("Wave", p => p.SetValue(new Vector2(3f, 0.15f)));
        Set("Levels", p => p.SetValue(4f));
        Set("Cutoff", p => p.SetValue(0.45f));
        Set("Strength", p => p.SetValue(0.7f));
        Set("TintColor", p => p.SetValue(new Vector3(1f, 0.55f, 0.25f)));
        Set("TintAmount", p => p.SetValue(0.8f));
        Set("TexelSize", p => p.SetValue(new Vector2(2f / texture.Width, 2f / texture.Height)));
        Set("BlendColor", p => p.SetValue(new Vector3(0.9f, 0.45f, 0.2f)));

        return (set, errors.Count == 0 ? null : "SetValue failed: " + string.Join(" | ", errors));
    }
}
