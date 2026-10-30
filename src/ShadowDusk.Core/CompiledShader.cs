#nullable enable

using ShadowDusk.Core.Reflection;

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
    /// The effect's parameters as the compiled file exposes them to the runtime
    /// (<c>Effect.Parameters</c>), in file order: name, shape, type, annotations (MGFX and KNIFX; FNA lists none), and the default
    /// value the HLSL declares (<see cref="ParameterReflection.DefaultValue"/>, null when
    /// unknown), so a tool can show <c>float Scale = 2.0;</c> without loading the effect into a
    /// runtime.
    /// </summary>
    /// <remarks>
    /// Reflection only. On MGFX and KNIFX the defaults are exactly what the effect bytes in
    /// <see cref="Data"/> carry. On FNA the reported default can be larger than what the
    /// <c>.fxb</c> file carries: the compiler trims the file's constant table to the registers the
    /// shader reads, while the reflection value is read from the source (a <c>float Weights[3]</c>
    /// the shader reads only as <c>Weights[1]</c> reflects as 0.75, 1.75, 2.75; the file holds
    /// 0.75, 1.75, 0). The list and each default are read-only.
    /// </remarks>
    public IReadOnlyList<ParameterReflection> Parameters { get; init; } = [];

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
    /// Render-proven through a real <c>ContentManager</c> on MonoGame (WindowsDX and DesktopGL),
    /// KNI (4.2.9001 and 4.3.9001, MGFX v10 and KNIFX payloads) and FNA 26.06 (Phase 64); the
    /// container carries the XNA-4.0 type-reader name, the only one every runtime resolves.
    /// </remarks>
    /// <returns>The complete <c>.xnb</c> file bytes.</returns>
    public byte[] ToXnb() => XnbWriter.Wrap(Data, Target);
}
