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
);
