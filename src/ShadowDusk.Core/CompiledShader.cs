#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The successful output of <see cref="IShaderCompiler.CompileAsync"/>: the compiled effect
/// bytes together with the platform they were produced for. For MonoGame/KNI targets the
/// bytes are a <c>.mgfx</c> effect; for <see cref="PlatformTarget.Fna"/> they are the D3D9
/// fx_2_0 effects binary (<c>.fxb</c>). Either way they can be written to a file or fed
/// directly to the consumer runtime's <c>Effect</c> constructor.
/// </summary>
/// <param name="Target">The platform backend the effect was compiled for.</param>
/// <param name="Data">
/// The compiled effect bytes, ready to load into the target runtime's <c>Effect</c>.
/// </param>
public sealed record CompiledShader(
    PlatformTarget Target,
    byte[] Data
)
{
    /// <summary>
    /// Non-fatal diagnostics produced while compiling: the underlying compiler's own
    /// warnings (verbatim — never reworded) plus ShadowDusk's GL portability findings
    /// (<c>SD0400</c>–<c>SD0499</c>: constructs that compile here but are known to fail
    /// or misbehave at runtime on some GL stacks — e.g. WebGL1/KNI Reach loop limits,
    /// ANGLE derivative zeroing, a SpriteBatch-incompatible pixel-shader input, a
    /// GLSL-1.30+/ES-3.00-only construct in the versionless GL source) and its
    /// reflection findings (<c>SD0104</c>: a vertex-input semantic that fell through to
    /// the TextureCoordinate default, exactly as mgfxc warns). Empty
    /// when there is nothing to report. The effect bytes in <see cref="Data"/> are
    /// valid regardless — warnings never gate output.
    /// </summary>
    public IReadOnlyList<ShaderError> Warnings { get; init; } = [];

    /// <summary>
    /// Returns <see cref="Data"/> wrapped in an XNB content container, ready to drop into a
    /// content directory where <c>Content.Load&lt;Effect&gt;("Foo")</c> will find it —
    /// <b>no consumer code change at all</b> (issue #199).
    /// </summary>
    /// <remarks>
    /// A container only: the payload is <see cref="Data"/> verbatim, so the effect bytes are
    /// identical to what a plain <c>.mgfx</c> / <c>.fxb</c> write would produce. The XNB
    /// platform identifier is <b>derived</b> from <see cref="Target"/> (see
    /// <see cref="XnbWriter.PlatformIdentifierFor"/>) and is never something the consumer
    /// selects. Write the result to <c>&lt;AssetName&gt;.xnb</c>; the asset name
    /// <c>Content.Load</c> uses is the file name, so no companion file is needed.
    /// </remarks>
    /// <returns>The complete <c>.xnb</c> file bytes.</returns>
    public byte[] ToXnb() => XnbWriter.Wrap(Data, Target);
}
