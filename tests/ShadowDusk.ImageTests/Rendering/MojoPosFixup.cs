#nullable enable

using Silk.NET.OpenGL;

namespace ShadowDusk.ImageTests.Rendering;

/// <summary>
/// Sets the MojoShader <c>posFixup</c> uniform exactly the way real MonoGame 3.8.2
/// does (<c>GraphicsDevice.OpenGL.cs</c>, <c>ActivateShaderProgram</c>) so this
/// proxy renderer honors the dynamic position-fixup contract ShadowDusk's vertex
/// shaders carry since Phase 43 F3:
///
/// <code>
/// posFixup.x = 1                       (mad-friendly constant)
/// posFixup.y = +1 backbuffer / -1 when a render target is bound
/// posFixup.zw = ( (63/64)/vpW, -(63/64)/vpH ) when UseHalfPixelOffset, else (0,0)
///               (.w additionally negated when a render target is bound)
/// </code>
///
/// A program without the uniform (PS-only effects paired with a passthrough VS,
/// pre-Phase-43 goldens' SpriteEffect-less shapes) is skipped — the same
/// <c>posFixupLoc == -1</c> early-out MonoGame performs.
/// </summary>
public static class MojoPosFixup
{
    /// <summary>
    /// Applies the posFixup uniform to <paramref name="program"/> (which must be the
    /// currently bound program). No-op when the program does not declare it.
    /// </summary>
    /// <param name="gl">The GL API.</param>
    /// <param name="program">The linked, bound program handle.</param>
    /// <param name="renderTargetBound">
    /// <c>true</c> when rendering into an FBO (MonoGame's render-target case — flips
    /// vertically, matching the pre-Phase-43 static flip); <c>false</c> for the
    /// backbuffer case (no flip — the case the static flip got wrong).
    /// </param>
    /// <param name="viewportWidth">Viewport width in pixels (for the half-pixel offset).</param>
    /// <param name="viewportHeight">Viewport height in pixels (for the half-pixel offset).</param>
    /// <param name="useHalfPixelOffset">
    /// Mirrors MonoGame's <c>GraphicsDevice.UseHalfPixelOffset</c> (default false).
    /// </param>
    public static void Apply(
        GL gl,
        uint program,
        bool renderTargetBound,
        int viewportWidth,
        int viewportHeight,
        bool useHalfPixelOffset = false)
    {
        int loc = gl.GetUniformLocation(program, "posFixup");
        if (loc < 0)
            return; // MonoGame's posFixupLoc == -1 skip.

        float x = 1.0f;
        float y = 1.0f;
        float z = 0.0f;
        float w = 0.0f;
        if (useHalfPixelOffset)
        {
            z = (63.0f / 64.0f) / viewportWidth;
            w = -(63.0f / 64.0f) / viewportHeight;
        }

        if (renderTargetBound)
        {
            y *= -1.0f;
            w *= -1.0f;
        }

        gl.Uniform4(loc, x, y, z, w);
    }
}
