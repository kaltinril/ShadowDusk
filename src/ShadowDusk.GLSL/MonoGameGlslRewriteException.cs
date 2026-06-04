#nullable enable

namespace ShadowDusk.GLSL;

/// <summary>
/// Thrown by <see cref="MonoGameGlslRewriter.Rewrite"/> when the SPIRV-Cross GLSL
/// contains a construct the MojoShader-dialect rewrite cannot lower to a form that
/// is valid across every MonoGame/KNI GL profile (desktop GL, KNI Reach/WebGL1, and
/// KNI HiDef/WebGL2). Failing loudly here — rather than emitting GLSL that silently
/// breaks under one profile — upholds the Phase 33 generality bar: any shader that
/// compiles for GL must work in HiDef, or the compile must fail at build time.
/// </summary>
public sealed class MonoGameGlslRewriteException : Exception
{
    public MonoGameGlslRewriteException(string message) : base(message) { }
}
